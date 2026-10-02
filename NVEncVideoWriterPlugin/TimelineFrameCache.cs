using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using ItemRect = (YukkuriMovieMaker.Project.Items.IVideoItem item, Vortice.RawRectF rect, System.Numerics.Vector2[] quad,
    YukkuriMovieMaker.Player.Video.DrawDescription desc, System.Collections.Generic.IEnumerable<YukkuriMovieMaker.Player.Video.VideoController> itemControllers);

namespace NVEncVideoWriterPlugin;

// All failures are optional-cache failures: retain the host renderer and its UI metadata.
internal static class TimelineFrameCache
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const int RecordHeader = 24;
    private const int PreviewRecordHeader = 32;
    private const long GpuBudget = 384L * 1024 * 1024;
    private static readonly TimeSpan RectsRefreshDelay = TimeSpan.FromMilliseconds(100);
    // A paused request for a frame stored on disk only waits this long for the disk worker (off the UI thread: the
    // player renders on its own task) before rendering the frame itself.
    private static readonly TimeSpan PausedDiskWait = TimeSpan.FromMilliseconds(50);
    private static readonly ConditionalWeakTable<string, ModelTraits> modelTraits = new();
    private static readonly ConditionalWeakTable<object, SourceState> sources = new();
    private static readonly ConditionalWeakTable<object, UpdateMeasurement> updateMeasurements = new();
    private static readonly ConditionalWeakTable<object, PlayerAssociation> sourcePlayers = new();
    private static readonly ConditionalWeakTable<object, ReadbackPool> privateSources = new();
    private static readonly ConditionalWeakTable<Timeline, LatestViewport> latestViewports = new();
    private static readonly ConditionalWeakTable<ID2D1DeviceContext, string> renderEnvironments = new();
    private static readonly object cacheGate = new();
    // Only immutable pixel-upload command lists are retained. Host effect graphs remain source-owned.
    private static readonly LinkedList<GpuFrame> gpuLru = new();
    private static long gpuRetainedBytes, gpuRetentionBudget = 128L << 20;
    private static bool gpuRetentionEnabled = true;
    private static Lazy<FrameCacheStore> store = new(() => CacheMemoryController.CreateStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4-RTX3060-NVENC", "cache")));
    private static FieldInfo sceneField = null!, devicesField = null!, outputField = null!, collectorField = null!, pickerField = null!;
    private static FieldInfo playerSourceField = null!, playerContextField = null!, playerTargetField = null!;
    private static PropertyInfo needRects = null!, itemRects = null!, previewZoom = null!, previewCenter = null!, backBuffer = null!, playerIsPlaying = null!;
    private static MethodInfo visibleVideoSize = null!, previewTransform = null!;
    private static FieldInfo? timelineChangedField, pointerOverPreviewField;
    private static Type pickerType = null!;
    private static long hits, misses, gpuBytes, generation;
    private static long gpuHits, liveReuses, ramHits, diskHits, bypasses, previewStored, previewStoreTicks, readAheads;
    private static long drawnOnce, shownCopiesDropped, readbackBusySkips, readbackPoolBytes, readbackTexturesCreated;
    // Preview readbacks a source keeps on the GPU at once (ReadbacksInFlight): a frame is stored unless all are busy.
    private const int ReadbacksInFlight = 3;
    private static string status = "自動キャッシュは停止中です";
    private static bool previewEnabled, exportEnabled, previewSupported, rectsSupported, refreshSupported;
    private static bool drawOnce = true;

    // The preview (TimelineVideoPlayer: playing, paused, idle pre-rendering, cache bars) and export (any writer) are
    // switched separately. Enabled: either; setting it switches both.
    internal static bool Enabled
    {
        get => PreviewEnabled || ExportEnabled;
        set => SetEnabled(value, value);
    }
    internal static bool PreviewEnabled => Volatile.Read(ref previewEnabled);
    internal static bool ExportEnabled => Volatile.Read(ref exportEnabled);

    internal static void SetEnabled(bool preview, bool export)
    {
        lock (cacheGate)
        {
            if (previewEnabled == preview && exportEnabled == export) return;
            Volatile.Write(ref previewEnabled, preview);
            Volatile.Write(ref exportEnabled, export);
            using (var setting = CacheTrace.Measure("cache-settings", "state", preview ? "preview-on" : "preview-off"))
                if (setting is not null) setting.Detail = export ? "export-on" : "export-off";
            Interlocked.Increment(ref generation); // in-flight captures of a switched-off use are dropped
            status = preview && export ? "キャッシュ待機中（プレビュー・動画出力）"
                : preview ? "キャッシュ待機中（プレビューのみ）"
                : export ? "キャッシュ待機中（動画出力のみ）" : "自動キャッシュは停止中です";
            ClearGpuFrames();
        }
    }

    private static bool EnabledFor(bool exporting) => exporting ? ExportEnabled : PreviewEnabled;
    internal static string Status => Volatile.Read(ref status);
    internal static long Hits => Interlocked.Read(ref hits);
    internal static long Misses => Interlocked.Read(ref misses);
    internal static long GpuBytes => Interlocked.Read(ref gpuBytes);
    internal static long GpuHits => Interlocked.Read(ref gpuHits);
    internal static long GpuRetainedBytes { get { lock (cacheGate) return gpuRetainedBytes; } }
    internal static long GpuRetentionBudget
    {
        get { lock (cacheGate) return gpuRetentionBudget; }
        set { lock (cacheGate) { gpuRetentionBudget = Math.Clamp(value, 0, GpuBudget / 2); TrimGpuFrames(0); } }
    }
    internal static bool GpuRetentionEnabled
    {
        get { lock (cacheGate) return gpuRetentionEnabled; }
        set { lock (cacheGate) { gpuRetentionEnabled = value; if (!value) ClearGpuFrames(); } }
    }
    internal static long CacheGeneration => Interlocked.Read(ref generation);
    // Hits by path: the frame already in the source (same request again), the RAM store, a frame read back from disk.
    internal static long LiveReuses => Interlocked.Read(ref liveReuses);
    internal static long RamHits => Interlocked.Read(ref ramHits);
    internal static long DiskHits => Interlocked.Read(ref diskHits);
    internal static long Bypasses => Interlocked.Read(ref bypasses);
    // Preview frames stored after the host rendered them (playback, pauses and seeks; idle pre-rendering not counted).
    internal static long PreviewStored => Interlocked.Read(ref previewStored);
    // Render-thread time per stored preview frame: drawing into the preview view, GPU readback, copy (not disk I/O).
    internal static double PreviewStoreMilliseconds
    {
        get { long count = PreviewStored; return count == 0 ? 0 : Interlocked.Read(ref previewStoreTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / count; }
    }
    // Disk reads queued ahead of the playhead.
    internal static long ReadAheads => Interlocked.Read(ref readAheads);
    // Rendered preview frames shown from the pixels drawn for storing them (the player's Draw blits them instead of
    // evaluating the composition a second time), and those shown copies replaced by the host's output again because
    // the player drew another view.
    internal static long DrawnOnce => Interlocked.Read(ref drawnOnce);
    internal static long ShownCopiesDropped => Interlocked.Read(ref shownCopiesDropped);
    // Rendered preview frames not stored because every readback slot was still on the GPU.
    internal static long ReadbackBusySkips => Interlocked.Read(ref readbackBusySkips);
    // Reserved bytes of idle textures kept for reuse by the sources' readbacks (ReadbackPool), apart from GpuBytes.
    internal static long ReadbackPoolBytes => Interlocked.Read(ref readbackPoolBytes);
    // Drawing targets and staging textures created for preview readbacks (not taken from a pool).
    internal static long ReadbackTexturesCreated => Interlocked.Read(ref readbackTexturesCreated);
    // Tests only: false shows the host's output of stored frames, which the player's Draw evaluates again (A/B).
    internal static bool DrawOnce
    {
        get => Volatile.Read(ref drawOnce);
        set => Volatile.Write(ref drawOnce, value);
    }
    // Full Update time by path, including deferred work, storing and finalizer cleanup.
    internal static readonly FrameTimeSamples RenderTimes = new(), RamTimes = new(), DiskTimes = new(), LiveTimes = new(), GpuTimes = new();
    internal static FrameCacheStore? StoreIfCreated => store.IsValueCreated ? store.Value : null;

    // Tests only: a store with other budgets or in another folder (the caller disposes it).
    internal static void UseStore(FrameCacheStore replacement)
    {
        lock (cacheGate) { ClearGpuFrames(); store = new Lazy<FrameCacheStore>(replacement); }
    }

    // Tests only: the preview view of a source, standing in for a TimelineVideoPlayer with a real swap chain.
    internal static Func<object, PreviewViewport?>? TestViewport { get; set; }

    internal readonly record struct PreviewViewport(int Width, int Height, Matrix3x2 Transform, Vector2 TargetOffset,
        float DpiX, float DpiY, Vortice.DCommon.PixelFormat BackBufferFormat,
        AntialiasMode AntialiasMode, TextAntialiasMode TextAntialiasMode, PrimitiveBlend PrimitiveBlend, UnitMode UnitMode,
        Guid SceneId, Guid TimelineId, long LastDrawTimestamp, bool IsPlaying);

    private sealed class PlayerAssociation { internal WeakReference<object>? Player; }
    private sealed class LatestViewport(Guid sceneId, Guid timelineId)
    {
        internal Guid SceneId = sceneId;
        internal Guid TimelineId = timelineId;
        internal Scenes? Scenes;
        internal PreviewViewport Viewport;
        internal WeakReference<object>? Player;
        internal WeakReference<object>? Source;
    }
    internal static void Clear()
    {
        // The generation bump alone invalidates every in-flight capture (StillCurrent). The disk purge can
        // take seconds, and the render thread takes cacheGate on every Update, so never wait for it inside.
        lock (cacheGate)
        {
            Interlocked.Increment(ref generation);
            ClearGpuFrames();
            status = "キャッシュを消去しています…";
        }
        try { if (store.IsValueCreated) store.Value.Clear(); }
        catch { status = "キャッシュを完全には消去できませんでした"; throw; }
        status = "キャッシュを消去しました";
        DynamicComputeCache.Shared.Clear();
    }

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        var patched = new List<MethodBase>();
        try
        {
            var features = HostFeatures.For(host);
            var type = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
            pickerType = host.GetType("YukkuriMovieMaker.Player.Video.CompositeItemPicker", true)!;
            sceneField = type.GetField("scene", Instance)!;
            devicesField = type.GetField("devices", Instance)!;
            outputField = type.GetField("commandList", Instance)!;
            collectorField = type.GetField("disposer", Instance)!;
            pickerField = type.GetField("itemPicker", Instance)!;
            needRects = type.GetProperty("NeedTimelineItemRects")!;
            itemRects = type.GetProperty("TimelineItemRects")!;
            if (sceneField.FieldType != typeof(Scene) || outputField.FieldType != typeof(ID2D1CommandList)
                || collectorField.FieldType != typeof(DisposeCollector) || needRects.PropertyType != typeof(bool))
                throw new NotSupportedException("TimelineSource contract changed");

            var update = type.GetMethods(Instance).Single(m => m.Name == "Update" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(TimeSpan));
            var dispose = type.GetMethod("Dispose", Instance, [typeof(bool)])!;
            EnsureNoExternalHarmonyOwners(update, harmony.Id);
            EnsureNoExternalHarmonyOwners(dispose, harmony.Id);
            // The preview (player) part is used only where the player's code is known (HostFeatures).
            MethodInfo? draw = null, edit = null;
            previewSupported = rectsSupported = refreshSupported = false;
            if (features.Preview)
            {
                var playerType = host.GetType("YukkuriMovieMaker.Player.TimelineVideoPlayer", true)!;
                playerSourceField = playerType.GetField("timelineVideo", Instance)!;
                playerContextField = playerType.GetField("devicesAndContext", Instance)!;
                playerTargetField = playerType.GetField("renderTarget", Instance)!;
                previewZoom = playerType.GetProperty("PreviewDisplayZoom")!;
                previewCenter = playerType.GetProperty("PreviewViewCenter")!;
                playerIsPlaying = playerType.GetProperty("IsPlaying", Instance)!;
                visibleVideoSize = playerType.GetMethod("GetVisibleVideoSize", Instance)!;
                previewTransform = playerType.GetMethod("CreatePreviewViewTransform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                backBuffer = playerTargetField.FieldType.GetProperty("BackBuffer")!;
                draw = playerType.GetMethods(Instance).Single(m => m.Name == "Draw" && m.GetParameters().Length == 0);
                edit = playerType.GetMethod("Edit", Instance, Type.EmptyTypes);
                if (playerSourceField.FieldType != type || playerContextField.FieldType != typeof(IGraphicsDevicesAndContext)
                    || previewZoom.PropertyType != typeof(float) || previewCenter.PropertyType != typeof(Vector2)
                    || playerIsPlaying.PropertyType != typeof(bool) || backBuffer.PropertyType != typeof(ID2D1Bitmap1)
                    || visibleVideoSize.GetParameters().Length != 1 || visibleVideoSize.GetParameters()[0].ParameterType != typeof(float)
                    || !previewTransform.GetParameters().Select(p => p.ParameterType)
                        .SequenceEqual([typeof(Vector2), typeof(Vector2), typeof(float), typeof(float)])
                    || draw.ReturnType != typeof(void))
                    throw new NotSupportedException("TimelineVideoPlayer preview contract changed");

                EnsureNoExternalHarmonyOwners(draw, harmony.Id);
                previewSupported = true;
                // Optional: without them, frames that need item rects are rendered normally.
                rectsSupported = features.SelectionRects && itemRects.PropertyType == typeof(List<ItemRect>);
                timelineChangedField = playerType.GetField("isTimelineChanged", Instance);
                pointerOverPreviewField = playerType.GetField("isMouseOverPreviewArea", Instance);
                refreshSupported = rectsSupported && edit?.ReturnType == typeof(void)
                    && timelineChangedField?.FieldType == typeof(bool) && pointerOverPreviewField?.FieldType == typeof(bool)
                    && (Harmony.GetPatchInfo(edit)?.Owners.All(owner => owner == harmony.Id) ?? true);
            }
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Postfix)),
                finalizer: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Finalizer)));
            patched.Add(update);
            harmony.Patch(dispose, postfix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Disposed)));
            patched.Add(dispose);
            if (draw is not null)
            {
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(ObservePlayer)),
                    finalizer: new HarmonyMethod(typeof(TimelineFrameCache), nameof(DrawFinalizer)));
                patched.Add(draw);
            }
            if (refreshSupported)
            {
                harmony.Patch(edit!, prefix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(BeforeEdit)));
                patched.Add(edit!);
            }
            if (!FrameRenderReadiness.TryInstall(host, harmony, out reason))
                throw new NotSupportedException(reason);
            reason = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            foreach (var target in patched)
                try { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); } catch { }
            reason = "キャッシュ用フックを接続できません: " + error.GetBaseException().Message;
            return false;
        }
    }

    private static void EnsureNoExternalHarmonyOwners(MethodBase target, string ownId)
    {
        var owners = Harmony.GetPatchInfo(target)?.Owners.Where(owner => owner != ownId).ToArray() ?? [];
        if (owners.Length != 0)
            throw new NotSupportedException($"Unverified Harmony owners on {target.DeclaringType?.FullName}.{target.Name}: {string.Join(", ", owners)}");
    }

    private sealed class SourceState(Scene scene)
    {
        internal readonly KeyDependencyTracker Tracker = new(scene);
        internal readonly PreviewRects<ItemRect> Rects = new();
        internal string? LastKey;
        internal string? LastViewportKey;
        internal ID2D1CommandList? LastOutput;
        // The key and project revision of the item rects currently in TimelineItemRects (null: none or unknown).
        internal string? RectsKey;
        internal long RectsRevision;
        // KeyEnvironment of the last preview/export update, for status queries off the render thread.
        internal volatile string? Environment;
        internal readonly Dictionary<(string FrameKey, int Frame), string> StatusKeys = [];
        internal string StatusStamp = string.Empty;
        internal long Bytes;
        internal GpuFrame? ActiveGpuFrame;
        internal readonly Dictionary<string, GpuFrame> GpuFrames = [];
        internal readonly FrameAdmissionHistory GpuAdmission = new();
        internal readonly FrameCacheEconomics Economics = new();
        internal long Generation;
        // Rendered preview frames whose GPU readback is still running, oldest first (at most ReadbacksInFlight);
        // finished on this source's render thread.
        internal readonly LinkedList<DeferredStore> Deferred = new();
        // Textures of this source's readbacks, reused from frame to frame.
        internal readonly ReadbackPool Pool = new();
        internal bool IsDisposed;
        internal int ReadAheadFrame = int.MinValue;
        // Read-ahead keys by (frame key, frame) for one environment/usage/view (render thread only): a playing window
        // gains one frame per update, so the others are not composed and hashed again.
        internal readonly Dictionary<(string FrameKey, int Frame), string> ReadAheadKeys = [];
        internal string ReadAheadStamp = string.Empty;
        // While the output is the copy ShowRendered made of this update's render: the host's own output, which the
        // host's disposer still owns, the view the copy is exact for and the drawing target it shows (back to Pool
        // once the copy is gone).
        internal ID2D1CommandList? HostOutput;
        internal PreviewViewport ShownViewport;
        internal PooledTarget? ShownTarget;
        // When the host's update or disposal has released the outputs (callers replacing the output themselves
        // dispose HostOutput first).
        internal void Released()
        {
            var released = Interlocked.Exchange(ref Bytes, 0);
            if (released != 0) Interlocked.Add(ref gpuBytes, -released);
            lock (cacheGate)
            {
                ActiveGpuFrame?.Release(); ActiveGpuFrame = null; HostOutput = null;
                ReleaseShownTarget();
            }
            LastKey = null;
            LastViewportKey = null;
            LastOutput = null;
            RectsKey = null;
        }
        // Under cacheGate, once no output draws the shown copy any more.
        internal void ReleaseShownTarget()
        {
            if (ShownTarget is not { } target) return;
            ShownTarget = null;
            if (!Pool.Return(target, ShownViewport)) target.Dispose();
        }
        ~SourceState()
        {
            Released();
            Pool.Clear();
            lock (cacheGate) foreach (var frame in GpuFrames.Values.ToArray()) RemoveGpuFrame(frame);
            try { Tracker.Dispose(); } catch { }
        }
    }

    private sealed class ModelTraits(string model)
    {
        internal readonly bool ShowOnlyPreview = PreviewUsage.ModelUsesShowOnlyPreview(model);
        internal readonly bool RectsReusable = PreviewUsage.RectsReusable(model);
    }

    private enum RectsUpdate { Clear, Keep, Restore, Defer }

    private sealed class Pending(SourceState state, Scene scene, IGraphicsDevicesAndContext devices,
        ID2D1CommandList? previousOutput, KeyCapture capture, string liveKey, string? cacheKey,
        long generation, TimeSpan time, string usageKey, PreviewViewport? viewport, bool wantRects, bool rectsReusable, bool playing,
        string environment, int fps) : IDisposable
    {
        // The render-thread state and frame rate LiveKey/CacheKey were composed with (StillCurrent compares them).
        internal readonly string Environment = environment;
        internal readonly int Fps = fps;
        internal readonly bool Playing = playing;
        internal readonly long TraceOperation = CacheTrace.OperationId;
        private int disposed;
        internal readonly SourceState State = state;
        internal readonly Scene Scene = scene;
        internal readonly IGraphicsDevicesAndContext Devices = devices;
        internal readonly ID2D1CommandList? PreviousOutput = previousOutput;
        internal readonly KeyCapture Capture = capture;
        internal readonly string LiveKey = liveKey;
        internal readonly string? CacheKey = cacheKey;
        internal readonly long Generation = generation;
        internal readonly TimeSpan Time = time;
        internal readonly string UsageKey = usageKey;
        internal readonly PreviewViewport? Viewport = viewport;
        internal readonly bool WantRects = wantRects;
        internal readonly bool RectsReusable = rectsReusable;
        internal bool CacheHit;
        internal FrameTimeSamples? Path;
        private int handedOver;
        // A pending preview store keeps the capture past this update; Harmony's finalizer still calls Dispose.
        internal void HandOver() => Volatile.Write(ref handedOver, 1);
        public void Dispose() { if (Volatile.Read(ref handedOver) == 0) Release(); }
        internal void Release() { if (Interlocked.Exchange(ref disposed, 1) == 0) Capture.Dispose(); }
    }

    private sealed class DeferredStore(Pending pending, PreviewReadback readback) : IDisposable
    {
        internal readonly Pending Pending = pending;
        internal readonly PreviewReadback Readback = readback;
        public void Dispose() { Readback.Dispose(); Pending.Release(); }
    }

    private sealed class UpdateMeasurement(bool preview, TimeSpan time, string usage)
    {
        internal readonly long Started = PreviewPerformance.Timestamp;
        internal readonly CacheTrace.Span? Trace = CacheTrace.Measure("timeline-update", frameTimeTicks: time.Ticks, usage: usage);
        internal readonly int ThreadId = Environment.CurrentManagedThreadId;
        internal readonly bool Preview = preview;
        internal Pending? Pending;
        internal bool RunsHost, HostEnded, Completed, Consumed;
        internal long HostStarted, TotalTicks;
        internal void EndHost()
        {
            if (Preview && RunsHost && !HostEnded)
            {
                PreviewPerformance.End(PreviewStage.HostRender, HostStarted);
                HostEnded = true;
            }
        }
    }

    private static bool Prefix(object __instance, TimeSpan time, object usage, out UpdateMeasurement __state)
    {
        string name = usage.ToString() ?? string.Empty;
        // Idle pre-rendering is not the preview's frame time.
        __state = new UpdateMeasurement(name is "Playing" or "Paused" && !privateSources.TryGetValue(__instance, out _), time, name);
        updateMeasurements.AddOrUpdate(__instance, __state);
        __state.RunsHost = CachePrefix(__instance, time, usage, out var pending);
        __state.Pending = pending;
        __state.HostStarted = PreviewPerformance.Timestamp;
        return __state.RunsHost;
    }

    private static bool CachePrefix(object __instance, TimeSpan time, object usage, out Pending? __state)
    {
        __state = null;
        // The idle pre-renderer's own source: primed explicitly (TryPrimePreviewIfCurrent), never served or stored here.
        if (privateSources.TryGetValue(__instance, out _)) return true;
        if (sources.TryGetValue(__instance, out var existing)) CompleteDeferred(existing);
        if (!Enabled) return true;
        long keyStarted = PreviewPerformance.Timestamp;
        bool keyMeasured = false;
        KeyCapture? capture = null;
        Pending? pending = null;
        try
        {
            string usageName = usage.ToString() ?? string.Empty;
            bool exporting = usageName == "Exporting", paused = usageName == "Paused", preview = paused || usageName == "Playing";
            if (!exporting && !preview) return Bypass("キャッシュの対象はプレビュー（再生・一時停止）と動画出力の描画だけです。");
            if (!EnabledFor(exporting)) return true; // switched off for this use in the settings
            if (preview && !previewSupported) return Bypass("このYMM4ではプレビューのキャッシュを使いません（動画出力のみ）。");
            bool wantRects = (bool)needRects.GetValue(__instance)!;
            if (wantRects && (exporting || !rectsSupported)) return Bypass("アイテムの表示枠が必要な描画のため、通常描画を使用します。");
            var scene = (Scene)sceneField.GetValue(__instance)!;
            if (scene.ParentScenes.Length != 0) return Bypass("入れ子のシーンは通常描画を使用します。");
            var picker = pickerField.GetValue(__instance)!;
            if (picker.GetType() != pickerType || pickerType.GetFields(Instance).Any(f => f.GetValue(picker) != null))
                return Bypass("アイテム選択の操作中は通常描画を使用します。");
            var devices = (IGraphicsDevicesAndContext)devicesField.GetValue(__instance)!;
            var context = devices.DeviceContext;
            if (!ValidContext(context)) return Bypass("描画コンテキストの状態が対象外のため、通常描画を使用します。");
            var state = sources.GetValue(__instance, _ => new SourceState(scene));
            string environment = KeyEnvironment(context);
            state.Environment = environment;
            if (!state.Tracker.TryCapture(FrameOf(time, scene), out capture, out var reason, settle: true, background: preview)) return Bypass(reason);
            var traits = modelTraits.GetValue(capture!.Model, static model => new ModelTraits(model));
            string usageKey = exporting ? usageName : PreviewUsage.KeyFor(usageName, traits.ShowOnlyPreview);
            PreviewViewport? viewport = preview && TryGetPreviewViewportForSource(__instance, out var currentViewport)
                && currentViewport.SceneId == scene.ID && currentViewport.TimelineId == scene.Timeline.ID ? currentViewport : null;
            int fps = scene.FPS;
            string liveKey = ComposeKey(capture.Key, time, fps, usageKey, environment, null);
            string? cacheKey = exporting ? liveKey : viewport is { } value ? ComposeKey(capture.Key, time, fps, usageKey, environment, value) : null;
            long currentGeneration = Interlocked.Read(ref generation);
            long revision = capture.Revision;
            var previousOutput = (ID2D1CommandList?)outputField.GetValue(__instance);
            pending = new Pending(state, scene, devices, previousOutput, capture, liveKey, cacheKey,
                currentGeneration, time, usageKey, viewport, wantRects, traits.RectsReusable, usageName == "Playing", environment, fps);
            capture = null;
            if (preview) PreviewPerformance.End(PreviewStage.KeyGeneration, keyStarted);
            keyMeasured = true;
            using var lookup = preview ? PreviewPerformance.Measure(PreviewStage.CacheLookup) : default;
            // With rects requested, a reused frame restores the rects of an earlier render of the same key and
            // project revision, keeps the live ones, or (paused, pointer away from the preview) is shown without
            // them until BeforeEdit asks the player to re-render it. Otherwise the frame is rendered normally.
            ItemRect[]? recalled = null;
            RectsUpdate? stored = RectsUpdate.Clear;
            if (wantRects)
            {
                stored = null;
                if (!state.Rects.IsMissing(time))
                {
                    if (traits.RectsReusable && state.Rects.TryRecall(liveKey, currentGeneration, revision, out var rects))
                    {
                        recalled = rects;
                        stored = RectsUpdate.Restore;
                    }
                    else if (paused && refreshSupported && !PointerOverPreview(__instance)) stored = RectsUpdate.Defer;
                }
            }
            lock (cacheGate)
            {
                if (gpuRetentionEnabled && viewport is not null && cacheKey is not null) state.GpuAdmission.Observe(cacheKey);
                if (currentGeneration == Interlocked.Read(ref generation) && state.Generation == currentGeneration
                    && state.LastKey == liveKey && (state.LastViewportKey is null || state.LastViewportKey == cacheKey)
                    && state.LastOutput is { NativePointer: not 0 }
                    && ReferenceEquals(state.LastOutput, previousOutput))
                {
                    var update = wantRects && traits.RectsReusable && !state.Rects.IsMissing(time)
                        && state.RectsKey == liveKey && state.RectsRevision == revision ? RectsUpdate.Keep : stored;
                    if (update is { } live)
                    {
                        Hit(__instance, pending, live, recalled);
                        Interlocked.Increment(ref liveReuses);
                        pending.Path = LiveTimes;
                        __state = pending;
                        return false;
                    }
                }
            }
            if (stored is not null && cacheKey is not null && viewport is { } resident && TryRestoreGpu(__instance, pending))
            {
                // A run of GPU-resident frames still reads the frames after it ahead from disk.
                ReadAhead(state, scene, time, usageKey, resident, !paused);
                Hit(__instance, pending, stored.Value, recalled);
                Interlocked.Increment(ref gpuHits);
                pending.Path = GpuTimes;
                __state = pending;
                return false;
            }
            bool fromDisk = false;
            bool replaced = false;
            if (stored is not null && cacheKey is not null)
            {
                ReadOnlyMemory<byte> record;
                bool found;
                using (PreviewPerformance.Measure(PreviewStage.CacheRead))
                    found = store.Value.TryGet(cacheKey, paused && viewport is not null ? PausedDiskWait : TimeSpan.Zero, out record, out fromDisk);
                if (found)
                    using (PreviewPerformance.Measure(PreviewStage.CacheRestore)) replaced = TryReplaceFrame(__instance, pending, record);
            }
            if (viewport is { } view) ReadAhead(state, scene, time, usageKey, view, !paused);
            if (replaced)
            {
                Hit(__instance, pending, stored!.Value, recalled);
                Interlocked.Increment(ref fromDisk ? ref diskHits : ref ramHits);
                pending.Path = fromDisk ? DiskTimes : RamTimes;
                __state = pending;
                return false;
            }
            Interlocked.Increment(ref misses);
            pending.Path = RenderTimes;
            __state = pending;
            return true;
        }
        catch (Exception error) { pending?.Dispose(); return Bypass("キャッシュを使用しませんでした: " + error.GetType().Name); }
        finally
        {
            capture?.Dispose();
            if (!keyMeasured && usage.ToString() is "Playing" or "Paused")
                PreviewPerformance.End(PreviewStage.KeyGeneration, keyStarted);
        }
    }

    private static void Postfix(object __instance, UpdateMeasurement __state)
    {
        __state.EndHost();
        CachePostfix(__instance, __state.Pending);
    }

    private static void CachePostfix(object __instance, Pending? __state)
    {
        if (__state is null) return;
        bool deferred = false;
        try
        {
            if (__state.CacheHit) return;
            __state.State.Released(); // The host update disposed the previous source-owned output.
            if (__state.WantRects) __state.State.Rects.Rendered(); // the host computed this frame's rects
            if (!FrameRenderReadiness.IsUpdateReady(__instance))
            {
                status = FrameRenderReadiness.CoverageProblem ?? "動画のデコード完了を確認できないフレームは保存しません。";
                return;
            }
            if (!StillCurrent(__state)) return;
            var output = (ID2D1CommandList)outputField.GetValue(__instance)!;
            if (__state.CacheKey is not null && __state.Viewport is null)
            {
                var record = CaptureScene(__state.Devices.DeviceContext, output, __state.Scene);
                if (record != null && StillCurrent(__state, files: true)) // file I/O, outside cacheGate
                    lock (cacheGate)
                        if (StillCurrent(__state) && store.Value.PutOwned(__state.CacheKey, record)) status = "描画したフレームを保存しました。";
            }
            // Rects of a frame whose decoding was not confirmed are never remembered (returned above).
            var rects = __state.WantRects && __state.RectsReusable ? SnapshotRects(__instance) : null;
            lock (cacheGate) if (StillCurrent(__state) && sources.TryGetValue(__instance, out var current) && ReferenceEquals(current, __state.State))
            {
                current.LastKey = __state.LiveKey;
                current.LastViewportKey = null;
                current.LastOutput = output;
                current.Bytes = 0;
                current.Generation = __state.Generation;
                current.RectsKey = rects is null ? null : __state.LiveKey;
                current.RectsRevision = __state.Capture.Revision;
                if (rects is not null) current.Rects.Remember(__state.LiveKey, rects, __state.Generation, __state.Capture.Revision);
            }
            // Last: storing may finish (and dispose) the capture at once.
            if (__state.CacheKey is not null && __state.Viewport is { } view) deferred = StorePreview(__instance, __state, output, view);
        }
        catch (Exception error) { status = "フレームの保存に失敗しました: " + error.GetType().Name; }
        finally { if (!deferred) __state.Dispose(); }
    }

    // A preview frame the host rendered (playing, paused, seeking) is stored for its view, like an idle pre-rendered
    // one. Completion is polled on the render thread without waiting for the GPU. Up to ReadbacksInFlight copies per
    // source remain pending, so that a GPU a frame or two behind does not skip frames; a capture is skipped only
    // while all are busy. True when it took over `pending`.
    // The frame is drawn once: the pixels drawn for the store are also what the player shows (ShowRendered).
    private static bool StorePreview(object source, Pending pending, ID2D1CommandList output, PreviewViewport viewport)
    {
        var state = pending.State;
        bool full;
        lock (cacheGate) full = state.Deferred.Count >= ReadbacksInFlight;
        if (full)
        {
            CompleteDeferred(state);
            lock (cacheGate) full = state.Deferred.Count >= ReadbacksInFlight;
        }
        if (full)
        {
            using var trace = CacheTrace.Measure("cache-admission", "policy");
            if (trace is not null) trace.Outcome = "readback-busy";
            Interlocked.Increment(ref readbackBusySkips);
            return false;
        }
        if (!pending.State.Economics.ShouldAdmit(pending.CacheKey!, (long)viewport.Width * viewport.Height * 4 + PreviewRecordHeader, gpuRetentionEnabled))
        {
            using var trace = CacheTrace.Measure("cache-admission", "policy");
            if (trace is not null) trace.Outcome = "render-cheaper";
            return false;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var readback = BeginPreviewReadback(pending.Devices.DeviceContext, output, viewport, DrawOnce, state.Pool, out var shown, out var shownTarget);
        if (readback is null) return false;
        try
        {
            lock (cacheGate)
            {
                if (!StillCurrent(pending) || state.IsDisposed || state.Deferred.Count >= ReadbacksInFlight) { readback.Dispose(); return false; }
                pending.HandOver();
                state.Deferred.AddLast(new DeferredStore(pending, readback));
                if (shown is not null && ShowRendered(source, pending, shown, (long)viewport.Width * viewport.Height * 4, output, viewport, shownTarget))
                {
                    shown = null;
                    shownTarget = null;
                }
            }
        }
        finally
        {
            if (shown is not null) DisposeShownCopy(shown, viewport, state.Pool, shownTarget);
        }
        Interlocked.Add(ref previewStoreTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (!pending.Playing && !refreshSupported) CompleteDeferred(state, limit: ReadbacksInFlight);
        return true;
    }

    // Under cacheGate, on the render thread, after the host's update of `pending` (CachePostfix). `shown` draws the
    // pixels BeginPreviewReadback drew from the host's output for `viewport`; the player's Draw then blits them
    // instead of evaluating the composition a second time. Pixels and accounting as for a hit (CommitReplacement),
    // but the host's output is kept, still owned by the host's disposer, for a Draw with another view.
    // `target` (null without a pool) is the drawing target `shown` draws; the source returns it to its pool when the
    // copy is gone.
    private static bool ShowRendered(object source, Pending pending, ID2D1CommandList shown, long bytes,
        ID2D1CommandList hostOutput, PreviewViewport viewport, PooledTarget? target)
    {
        // StorePreview has just checked StillCurrent under the same lock.
        if (!sources.TryGetValue(source, out var state) || !ReferenceEquals(state, pending.State)
            || !ReferenceEquals(outputField.GetValue(source), hostOutput) || state.HostOutput is { NativePointer: not 0 }
            || state.Bytes != 0 || state.ActiveGpuFrame is not null || state.ShownTarget is not null) return false;
        var collector = (DisposeCollector)collectorField.GetValue(source)!;
        collector.Collect(shown);
        outputField.SetValue(source, shown);
        state.HostOutput = hostOutput;
        state.ShownViewport = viewport;
        state.ShownTarget = target;
        state.LastOutput = shown;
        state.LastViewportKey = pending.CacheKey;
        state.Bytes = bytes;
        Interlocked.Increment(ref drawnOnce);
        return true;
    }

    // Before the player draws (its Draw prefix, on the render thread): a copy shown by ShowRendered is exact only for
    // the view it was drawn for. After a zoom, pan or resize the host's output is shown again, as without the copy.
    private static void ShowHostOutputIfViewChanged(object source, SourceState state, PreviewViewport viewport)
    {
        if (state.HostOutput is null) return; // every Draw: nothing shown from a copy
        lock (cacheGate)
        {
            if (state.HostOutput is not { NativePointer: not 0 } host
                || viewport with { LastDrawTimestamp = 0, IsPlaying = false } == state.ShownViewport with { LastDrawTimestamp = 0, IsPlaying = false })
                return;
            if (outputField.GetValue(source) is not ID2D1CommandList shown || !ReferenceEquals(shown, state.LastOutput)) return;
            using var trace = CacheTrace.Measure("shown-copy", "state");
            if (trace is not null) trace.Outcome = "view-changed";
            var collector = (DisposeCollector)collectorField.GetValue(source)!;
            outputField.SetValue(source, host);
            collector.Remove(shown);
            shown.Dispose();
            var released = Interlocked.Exchange(ref state.Bytes, 0);
            if (released != 0) Interlocked.Add(ref gpuBytes, -released);
            state.ReleaseShownTarget();
            state.HostOutput = null;
            state.LastOutput = host;
            state.LastViewportKey = null;
            Interlocked.Increment(ref shownCopiesDropped);
        }
    }

    // A copy from BeginPreviewReadback(show: true) that is not shown, with its part of the reservation and, with a
    // pool, its drawing target.
    internal static void DisposeShownCopy(ID2D1CommandList shown, PreviewViewport viewport, ReadbackPool? pool = null, PooledTarget? target = null)
    {
        shown.Dispose();
        Interlocked.Add(ref gpuBytes, -(long)viewport.Width * viewport.Height * 4);
        if (target is not null && pool?.Return(target, viewport) != true) target.Dispose();
    }

    // Tests only: the player's Draw prefix with `viewport` as its view.
    internal static void ObserveDrawView(object source, PreviewViewport viewport)
    {
        if (sources.TryGetValue(source, out var state)) ShowHostOutputIfViewChanged(source, state, viewport);
    }

    // On the source's render thread. The frame is stored only if its capture is still current: an edit, purge or
    // file change in between drops it. Oldest first; the GPU runs the copies in order, so the first one still busy
    // ends the poll. At most `limit` frames are finished per call (each allocates and copies a frame on the render
    // thread); waitForGpu finishes all.
    private static void CompleteDeferred(SourceState state, bool waitForGpu = false, int limit = 1)
    {
        for (int finished = 0; waitForGpu || finished < limit; finished++)
            if (!CompleteOldestDeferred(state, waitForGpu)) return;
    }

    // False when nothing was pending or the oldest readback is still running.
    private static bool CompleteOldestDeferred(SourceState state, bool waitForGpu)
    {
        DeferredStore deferred;
        lock (cacheGate)
        {
            if (state.Deferred.First is not { } oldest) return false;
            deferred = oldest.Value;
            state.Deferred.RemoveFirst(); // exclusively owned here: purge must not dispose a mapped texture
        }
        bool retained = false, busy = false;
        using var trace = CacheTrace.Measure("deferred-store", frameTimeTicks: deferred.Pending.Time.Ticks,
            usage: deferred.Pending.UsageKey, operation: deferred.Pending.TraceOperation);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (cacheGate) if (!StillCurrent(deferred.Pending)) return true;
            byte[]? record;
            if (waitForGpu) record = FinishPreviewReadback(deferred.Readback);
            else if (!TryFinishPreviewReadback(deferred.Readback, out record))
            {
                busy = true;
                lock (cacheGate) if (StillCurrent(deferred.Pending) && !state.IsDisposed)
                {
                    state.Deferred.AddFirst(deferred);
                    retained = true;
                }
                if (trace is not null) trace.Outcome = retained ? "gpu-busy" : "cancelled";
                return false;
            }
            if (!StillCurrent(deferred.Pending, files: true)) return true; // file I/O, outside cacheGate
            lock (cacheGate) if (StillCurrent(deferred.Pending))
            {
                if (!deferred.Pending.State.Economics.ShouldAdmit(deferred.Pending.CacheKey!, record!.LongLength, gpuRetentionEnabled)) return true;
                if (!store.Value.PutOwned(deferred.Pending.CacheKey!, record)) return true;
                Interlocked.Increment(ref previewStored);
                status = "描画したプレビューのフレームを保存しました。";
            }
            return true;
        }
        catch (Exception error)
        {
            status = "プレビューのフレームの保存に失敗しました: " + error.GetType().Name;
            return !busy;
        }
        finally
        {
            if (!retained) deferred.Dispose();
            Interlocked.Add(ref previewStoreTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    // Tests only (render thread): finishes the pending preview readback of a source.
    internal static void CompletePendingStore(object source)
    {
        if (sources.TryGetValue(source, out var state)) CompleteDeferred(state, waitForGpu: true);
    }

    // Queues disk reads for the frames the player shows next (playback: the next half second; paused: two frames
    // either way, for stepping and scrubbing). Keys come from the current description without leasing files: a read
    // only moves a stored record into RAM, and showing it still needs the exact key of a validated capture.
    private static void ReadAhead(SourceState state, Scene scene, TimeSpan time, string usage, PreviewViewport viewport, bool playing)
    {
        try
        {
            int frame = FrameOf(time, scene);
            if (state.ReadAheadFrame == frame || state.Environment is not { } environment) return;
            state.ReadAheadFrame = frame;
            var frames = new List<int>();
            // Extend the lead when observed disk latency rises; Prefetch still caps the byte window at half RAM.
            double leadSeconds = Math.Clamp(0.5 + store.Value.DiskReadMilliseconds * 0.004, 0.5, 2);
            int ahead = playing ? Math.Clamp((int)Math.Ceiling(scene.FPS * leadSeconds), 4, 120) : 2;
            for (int i = 1; i <= ahead; i++) frames.Add(frame + i);
            if (!playing) frames.AddRange([frame - 1, frame - 2]);
            frames.RemoveAll(value => value < 0);
            var frameKeys = new string?[frames.Count];
            if (frames.Count == 0 || !state.Tracker.TryPeekFrameKeys(frames, frameKeys, out _)) return;
            var keys = new string?[frames.Count];
            var view = viewport with { LastDrawTimestamp = 0, IsPlaying = false };
            string stamp = $"{environment}|{usage}|{scene.FPS}|{view}";
            if (state.ReadAheadStamp != stamp || state.ReadAheadKeys.Count > 4096)
            {
                state.ReadAheadKeys.Clear();
                state.ReadAheadStamp = stamp;
            }
            for (int i = 0; i < frames.Count; i++)
            {
                if (frameKeys[i] is not { } frameKey) continue;
                if (!state.ReadAheadKeys.TryGetValue((frameKey, frames[i]), out var key))
                    state.ReadAheadKeys[(frameKey, frames[i])] = key =
                        ComposeKey(frameKey, scene.Timeline.VideoInfo.GetTimeFrom(frames[i]), scene.FPS, usage, environment, viewport);
                keys[i] = key;
            }
            int queued = store.Value.Prefetch(keys);
            if (queued != 0) Interlocked.Add(ref readAheads, queued);
        }
        catch { } // optional
    }

    private static Exception? Finalizer(Exception? __exception, UpdateMeasurement? __state)
    {
        if (__state is not null)
        {
            __state.EndHost();
            __state.Pending?.Dispose();
            __state.TotalTicks = PreviewPerformance.Timestamp - __state.Started;
            __state.Pending?.Path?.Add(__state.TotalTicks);
            if (__state.Preview) PreviewPerformance.Add(PreviewStage.TotalUpdate, __state.TotalTicks);
            __state.Completed = __exception is null;
            if (__state.Trace is { } trace)
            {
                trace.Outcome = __exception is not null ? "exception" : __state.Pending is null ? "bypass"
                    : __state.RunsHost ? "render" : ReferenceEquals(__state.Pending.Path, LiveTimes) ? "live"
                    : ReferenceEquals(__state.Pending.Path, GpuTimes) ? "gpu"
                    : ReferenceEquals(__state.Pending.Path, DiskTimes) ? "disk" : "ram";
                trace.Detail = __exception?.GetType().Name;
                trace.Dispose();
            }
        }
        return __exception;
    }

    // files: resolve the captured files' paths again (FileDependencyLease.VerifyPaths). Only the checks right before
    // a result outlives the Update that captured it (a store commit, an export store) pass true; within the capture's
    // own Update its lease has just opened every file, and the open handles deny writes, deletes and renames.
    private static bool StillCurrent(Pending value, bool files = false)
    {
        using var validation = CacheTrace.Measure("cache-state-validation");
        if (!EnabledFor(value.UsageKey == "Exporting") || value.Generation != Interlocked.Read(ref generation)) return false;
        bool valid;
        using (CacheTrace.Measure(files ? "capture-dependency-validation" : "capture-state-validation")) valid = value.Capture.Validate(files);
        if (!valid) return false;
        // The keys are functions of the capture, time, usage and viewport (fixed in Pending), the frame rate and the
        // context's render state: comparing those two is the same check as composing both keys again.
        using var keys = CacheTrace.Measure("render-environment-key-validation");
        return value.Scene.FPS == value.Fps && KeyEnvironment(value.Devices.DeviceContext) == value.Environment;
    }

    // usage: "Exporting", or the preview key from PreviewUsage.KeyFor ("Preview" unless ShowOnlyPreviewEffect is used).
    private static string MakeKey(string model, TimeSpan time, int fps, string usage, ID2D1DeviceContext context, PreviewViewport? viewport) =>
        ComposeKey(model, time, fps, usage, KeyEnvironment(context), viewport);

    // The render-thread state a key depends on; read only on the thread that owns the context.
    private static string KeyEnvironment(ID2D1DeviceContext context) =>
        $"{RenderEnvironment(context)}|{context.AntialiasMode}|{context.TextAntialiasMode}|{context.PrimitiveBlend}";

    private static string ComposeKey(string model, TimeSpan time, int fps, string usage, string environment, PreviewViewport? viewport)
    {
        string value = $"pixels-v7|{environment}|{model}|{FrameTimeKey.For(time, fps)}|{usage}";
        if (viewport is { } view)
            value += $"|{view.SceneId:N}|{view.TimelineId:N}|{view.Width}|{view.Height}|{Bits(view.Transform.M11)}|{Bits(view.Transform.M12)}|{Bits(view.Transform.M21)}|{Bits(view.Transform.M22)}|{Bits(view.Transform.M31)}|{Bits(view.Transform.M32)}|{Bits(view.TargetOffset.X)}|{Bits(view.TargetOffset.Y)}|{Bits(view.DpiX)}|{Bits(view.DpiY)}|{view.BackBufferFormat.Format}|{view.BackBufferFormat.AlphaMode}|{view.AntialiasMode}|{view.TextAntialiasMode}|{view.PrimitiveBlend}|{view.UnitMode}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    // Stored pixels outlive the process: rasterization may differ between GPUs, drivers and plugin builds.
    // Failure to identify the adapter throws, which callers treat as a cache bypass.
    private static string RenderEnvironment(ID2D1DeviceContext context) => renderEnvironments.GetValue(context, static value =>
    {
        using var probe = value.CreateBitmap(new SizeI(1, 1), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var surface = probe.Surface ?? throw new NotSupportedException("Render target has no DXGI surface");
        using var device = surface.GetDevice<IDXGIDevice>();
        using var adapter = device.GetAdapter();
        var description = adapter.Description;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        // IDXGIDevice is the documented way to read the user-mode driver version from DXGI.
        string driver = adapter.CheckInterfaceSupport<IDXGIDevice>(out long version) ? version.ToString("X16", invariant) : "unknown";
        return string.Join(':', description.VendorId.ToString("X4", invariant), description.DeviceId.ToString("X4", invariant),
            description.SubsystemId.ToString("X8", invariant), description.Revision.ToString("X2", invariant), description.Description,
            driver, typeof(TimelineFrameCache).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
    });

    // The frame TimelineSource.Update renders for this time.
    internal static int FrameOf(TimeSpan time, Scene scene) => FrameTime.TimeToFrame(time, scene.Timeline.VideoInfo.FPS);

    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    internal static bool TryGetLatestPreviewViewport(Timeline timeline, Scenes scenes, out PreviewViewport viewport)
    {
        viewport = default;
        lock (cacheGate)
        {
            if (!latestViewports.TryGetValue(timeline, out var latest) || !ReferenceEquals(latest.Scenes, scenes)
                || latest.Player is null || !latest.Player.TryGetTarget(out var player)
                || latest.Source is null || !latest.Source.TryGetTarget(out var source)
                || !ReferenceEquals(playerSourceField.GetValue(player), source)
                || latest.Viewport.SceneId != latest.SceneId || latest.Viewport.TimelineId != latest.TimelineId) return false;
            if (outputField.GetValue(source) is not ID2D1CommandList { NativePointer: not 0 }) return false;
            viewport = latest.Viewport;
            return IsValidViewport(viewport, int.MaxValue);
        }
    }

    // For the cache status bars: per frame 2 = in RAM, 1 = on disk, 0 = not stored, for the preview of the
    // timeline shown in the player at its current view. False while the player, its view or the state is unknown.
    internal static bool TryGetPreviewResidency(Timeline timeline, IReadOnlyList<int> frames, Span<byte> residency)
    {
        residency.Clear();
        object? source;
        PreviewViewport viewport;
        lock (cacheGate)
        {
            if (!latestViewports.TryGetValue(timeline, out var latest) || latest.Source is null
                || !latest.Source.TryGetTarget(out source)) return false;
            viewport = latest.Viewport;
        }
        return TryGetPreviewResidency(source, viewport, frames, residency);
    }

    internal static bool TryGetPreviewResidency(object source, PreviewViewport viewport, IReadOnlyList<int> frames, Span<byte> residency)
    {
        residency.Clear();
        if (!PreviewEnabled || !sources.TryGetValue(source, out var state) || state.Environment is not { } environment) return false;
        var scene = (Scene)sceneField.GetValue(source)!;
        var frameKeys = new string?[frames.Count];
        if (!state.Tracker.TryPeekFrameKeys(frames, frameKeys, out string model)) return false;
        // Idle pre-rendering stores playback frames; they serve pauses too unless ShowOnlyPreviewEffect is used.
        string usage = PreviewUsage.KeyFor("Playing", modelTraits.GetValue(model, static value => new ModelTraits(value)).ShowOnlyPreview);
        var view = viewport with { LastDrawTimestamp = 0, IsPlaying = false };
        var keys = new string?[frames.Count];
        lock (state.StatusKeys)
        {
            string stamp = $"{environment}|{usage}|{scene.FPS}|{view}";
            if (state.StatusStamp != stamp)
            {
                state.StatusKeys.Clear();
                state.StatusStamp = stamp;
            }
            for (int i = 0; i < frames.Count; i++)
            {
                if (frameKeys[i] is not { } frameKey) continue;
                if (!state.StatusKeys.TryGetValue((frameKey, frames[i]), out var key))
                {
                    key = ComposeKey(frameKey, scene.Timeline.VideoInfo.GetTimeFrom(frames[i]), scene.FPS, usage, environment, viewport);
                    if (state.StatusKeys.Count >= 65536) state.StatusKeys.Clear();
                    state.StatusKeys[(frameKey, frames[i])] = key;
                }
                keys[i] = key;
            }
        }
        store.Value.GetResidency(keys, residency);
        return true;
    }

    // For the idle pre-renderer, on the thread that owns the source, before rendering: whether the preview frame it
    // would prime is already stored (in RAM or on disk), so that it is not rendered again.
    internal static bool IsPreviewStored(object timelineSource, TimeSpan time, KeyCapture capture, PreviewViewport viewport)
    {
        if (!PreviewEnabled) return false;
        try
        {
            timelineSource = GetTimelineSource(timelineSource);
            var scene = (Scene)sceneField.GetValue(timelineSource)!;
            var context = ((IGraphicsDevicesAndContext)devicesField.GetValue(timelineSource)!).DeviceContext;
            if (!ValidContext(context)) return false;
            var traits = modelTraits.GetValue(capture.Model, static model => new ModelTraits(model));
            var key = MakeKey(capture.Key, time, scene.FPS, PreviewUsage.KeyFor("Playing", traits.ShowOnlyPreview), context, viewport);
            var residency = new byte[1];
            store.Value.GetResidency([key], residency);
            return residency[0] != 0;
        }
        catch { return false; }
    }

    internal static bool TryPrime(object timelineSource, TimeSpan time, object usage) =>
        TryPrimeCore(timelineSource, time, usage, null, null);

    internal static bool TryPrimePreview(object timelineSource, TimeSpan time, object usage,
        PreviewViewport viewport, string? expectedModelKey = null) =>
        TryPrimeCore(timelineSource, time, usage, viewport, expectedModelKey);

    // capture: a capture of this frame from a tracker of the source's own scene that the caller holds (the idle
    // pre-renderer's clone capture); without it the source's tracker captures the frame here.
    internal static bool TryPrimePreviewIfCurrent(object timelineSource, TimeSpan time, object usage,
        PreviewViewport viewport, string? expectedModelKey, CancellationToken cancellation, KeyCapture? capture = null) =>
        TryPrimeCore(timelineSource, time, usage, viewport, expectedModelKey, cancellation, capture);

    // The idle pre-renderer's own renderer: Updates of it skip the live preview's cache (lookups, keys, stores).
    internal static void ExcludeFromPreviewCache(object sourceOrOwner) =>
        privateSources.AddOrUpdate(GetTimelineSource(sourceOrOwner), new ReadbackPool());

    private static bool TryPrimeCore(object timelineSource, TimeSpan time, object usage,
        PreviewViewport? viewport, string? expectedModelKey, CancellationToken cancellation = default, KeyCapture? provided = null)
    {
        if (!Enabled || cancellation.IsCancellationRequested) return false;
        long captureGeneration = Interlocked.Read(ref generation);
        try
        {
            timelineSource = GetTimelineSource(timelineSource);
            if (!FrameRenderReadiness.WasLastUpdateReady(timelineSource, time)) return false;
            var scene = (Scene)sceneField.GetValue(timelineSource)!;
            if (scene.ParentScenes.Length != 0 || (bool)needRects.GetValue(timelineSource)!) return false;
            string usageName = usage.ToString() ?? string.Empty;
            bool exporting = usageName == "Exporting", playing = usageName == "Playing";
            if (!EnabledFor(exporting)) return false;
            if (viewport is null ? !exporting : !playing || !IsValidViewport(viewport.Value, int.MaxValue)
                || viewport.Value.SceneId != scene.ID || viewport.Value.TimelineId != scene.Timeline.ID) return false;
            // A paused player need not redraw. Identity, live source association and captures determine validity;
            // wall-clock age must not stop caching a long composition.
            if (viewport is { } preview && (preview.IsPlaying || preview.LastDrawTimestamp <= 0)) return false;
            var picker = pickerField.GetValue(timelineSource)!;
            if (picker.GetType() != pickerType || pickerType.GetFields(Instance).Any(f => f.GetValue(picker) != null)) return false;
            var devices = (IGraphicsDevicesAndContext)devicesField.GetValue(timelineSource)!;
            var context = devices.DeviceContext;
            if (!ValidContext(context)) return false;
            KeyCapture? owned = null;
            if (provided is null && !sources.GetValue(timelineSource, _ => new SourceState(scene)).Tracker.TryCapture(FrameOf(time, scene), out owned, out _))
                return false;
            var capture = provided ?? owned;
            using (owned)
            {
                if (expectedModelKey is not null && capture!.Key != expectedModelKey) return false;
                var traits = modelTraits.GetValue(capture!.Model, static model => new ModelTraits(model));
                string usageKey = exporting ? usageName : PreviewUsage.KeyFor(usageName, traits.ShowOnlyPreview);
                var key = MakeKey(capture.Key, time, scene.FPS, usageKey, context, viewport);
                var output = (ID2D1CommandList)outputField.GetValue(timelineSource)!;
                privateSources.TryGetValue(timelineSource, out var pool); // the idle renderer's textures, reused
                var record = viewport is { } view ? CapturePreview(context, output, view, pool) : CaptureScene(context, output, scene);
                if (record is null || !capture.Validate()) return false; // resolves the files again: outside cacheGate
                lock (cacheGate)
                {
                    if (cancellation.IsCancellationRequested || captureGeneration != generation
                        || !capture.Validate(files: false) || !EnabledFor(exporting)) return false;
                    if (!store.Value.PutOwned(key, record)) return false;
                    status = viewport is null ? "出力フレームを先読みしました。" : "プレビューのフレームを先読みしました。";
                    return true;
                }
            }
        }
        catch { return false; }
    }

    private static object GetTimelineSource(object sourceOrOwner)
    {
        if (sceneField.DeclaringType!.IsInstanceOfType(sourceOrOwner)) return sourceOrOwner;
        var sourceField = sourceOrOwner.GetType().GetField("source", Instance);
        if (sourceField?.FieldType == sceneField.DeclaringType && sourceField.GetValue(sourceOrOwner) is { } source)
            return source;
        throw new NotSupportedException("TimelineSourceAndDevices no longer exposes its source field.");
    }

    private static byte[]? CaptureScene(ID2D1DeviceContext context, ID2D1Image output, Scene scene)
    {
        var bounds = context.GetImageLocalBounds(output);
        var half = new Vector2(scene.Width / 2f, scene.Height / 2f);
        var left = MathF.Floor(bounds.Left + half.X) - half.X;
        var top = MathF.Floor(bounds.Top + half.Y) - half.Y;
        var right = MathF.Ceiling(bounds.Right + half.X) - half.X;
        var bottom = MathF.Ceiling(bounds.Bottom + half.Y) - half.Y;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom)
            || right <= left || bottom <= top || right - left > context.MaximumBitmapSize || bottom - top > context.MaximumBitmapSize)
            return null;
        return Capture(context, output, checked((int)(right - left)), checked((int)(bottom - top)), new Vector2(left, top));
    }

    private static bool TryGetPreviewViewportForSource(object source, out PreviewViewport viewport)
    {
        viewport = default;
        if (TestViewport?.Invoke(source) is { } test)
        {
            viewport = test with { LastDrawTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
            return true;
        }
        if (!sourcePlayers.TryGetValue(source, out var association) || association.Player is null
            || !association.Player.TryGetTarget(out var player) || !ReferenceEquals(playerSourceField.GetValue(player), source)) return false;
        var scene = (Scene)sceneField.GetValue(source)!;
        var targetResources = playerTargetField.GetValue(player);
        if (targetResources is null || backBuffer.GetValue(targetResources) is not ID2D1Bitmap1 target) return false;
        var devices = (IGraphicsDevicesAndContext)playerContextField.GetValue(player)!;
        var context = devices.DeviceContext;
        var visible = (Vector2)visibleVideoSize.Invoke(player, [previewZoom.GetValue(player)])!;
        var transform = (Matrix3x2)previewTransform.Invoke(null,
            [visible, previewCenter.GetValue(player)!, (float)scene.Width, (float)scene.Height])! * context.Transform;
        var pixelSize = target.PixelSize;
        viewport = new PreviewViewport(pixelSize.Width, pixelSize.Height, transform,
            new Vector2(scene.Width / 2f, scene.Height / 2f), target.Dpi.Width, target.Dpi.Height, target.PixelFormat,
            context.AntialiasMode, context.TextAntialiasMode, context.PrimitiveBlend, context.UnitMode,
            scene.ID, scene.Timeline.ID, System.Diagnostics.Stopwatch.GetTimestamp(), (bool)playerIsPlaying.GetValue(player)!);
        return IsValidViewport(viewport, context.MaximumBitmapSize);
    }

    private readonly record struct DrawMeasurement(long Started, UpdateMeasurement? Update, CacheTrace.Span? Trace);

    private static void ObservePlayer(object __instance, out DrawMeasurement __state)
    {
        __state = new(PreviewPerformance.Timestamp, null, CacheTrace.Measure("player-draw"));
        try
        {
            var source = playerSourceField.GetValue(__instance);
            if (source is not null && updateMeasurements.TryGetValue(source, out var measurement)
                && measurement.Preview && measurement.Completed && !measurement.Consumed
                && measurement.ThreadId == Environment.CurrentManagedThreadId)
            {
                measurement.Consumed = true;
                __state = __state with { Update = measurement };
                if (measurement.Trace is { } previous) __state.Trace?.Relate(previous);
            }
            if (source is null || !TryGetPreviewViewportForPlayer(__instance, source, out var viewport)) return;
            if (sources.TryGetValue(source, out var shownState)) ShowHostOutputIfViewChanged(source, shownState, viewport);
            using (var viewTrace = CacheTrace.Measure("viewport", "state"))
                if (viewTrace is not null) viewTrace.Detail = $"{viewport.Width}x{viewport.Height};playing={viewport.IsPlaying};"
                    + $"dpi={Bits(viewport.DpiX)},{Bits(viewport.DpiY)};"
                    + $"transform={Bits(viewport.Transform.M11)},{Bits(viewport.Transform.M12)},{Bits(viewport.Transform.M21)},"
                    + $"{Bits(viewport.Transform.M22)},{Bits(viewport.Transform.M31)},{Bits(viewport.Transform.M32)}";
            var scene = (Scene)sceneField.GetValue(source)!;
            var association = sourcePlayers.GetValue(source, _ => new PlayerAssociation());
            var latest = latestViewports.GetValue(scene.Timeline, _ => new LatestViewport(scene.ID, scene.Timeline.ID));
            lock (cacheGate)
            {
                association.Player = new WeakReference<object>(__instance);
                latest.SceneId = scene.ID;
                latest.TimelineId = scene.Timeline.ID;
                latest.Scenes = scene.Scenes;
                latest.Viewport = viewport;
                latest.Player = new WeakReference<object>(__instance);
                latest.Source = new WeakReference<object>(source);
            }
        }
        catch { }
    }

    private static Exception? DrawFinalizer(Exception? __exception, DrawMeasurement __state)
    {
        long drawTicks = PreviewPerformance.Timestamp - __state.Started;
        if (__state.Trace is { } trace)
        {
            if (__exception is not null) { trace.Outcome = "exception"; trace.Detail = __exception.GetType().Name; }
            trace.Dispose();
        }
        PreviewPerformance.Add(PreviewStage.PreviewDraw, drawTicks);
        // CPU time in Update + Draw, excluding the gap, Present, audio and playback/scheduler waits.
        if (__exception is null && __state.Update is { } update)
        {
            long ticks = update.TotalTicks + drawTicks;
            PreviewPerformance.Add(PreviewStage.TotalPreview, ticks);
            if (update.Pending is { CacheKey: { } key, Viewport: { } view } pending)
            {
                if (update.RunsHost) pending.State.Economics.ObserveRender(key, ticks);
                else if (ReferenceEquals(pending.Path, RamTimes))
                    pending.State.Economics.ObserveRestore((long)view.Width * view.Height * 4 + PreviewRecordHeader, ticks);
                else if (ReferenceEquals(pending.Path, GpuTimes))
                    pending.State.Economics.ObserveRestore((long)view.Width * view.Height * 4 + PreviewRecordHeader, ticks, gpu: true);
                using var costTrace = CacheTrace.Measure("frame-cost", "cpu-wall");
                if (costTrace is not null) costTrace.Detail = $"path={(update.RunsHost ? "render" : "restore")};ticks={ticks};frequency={System.Diagnostics.Stopwatch.Frequency}";
            }
        }
        return __exception;
    }

    private static bool TryGetPreviewViewportForPlayer(object player, object source, out PreviewViewport viewport)
    {
        viewport = default;
        var scene = (Scene)sceneField.GetValue(source)!;
        var targetResources = playerTargetField.GetValue(player);
        if (targetResources is null || backBuffer.GetValue(targetResources) is not ID2D1Bitmap1 target) return false;
        var devices = (IGraphicsDevicesAndContext)playerContextField.GetValue(player)!;
        var context = devices.DeviceContext;
        var visible = (Vector2)visibleVideoSize.Invoke(player, [previewZoom.GetValue(player)])!;
        var transform = (Matrix3x2)previewTransform.Invoke(null,
            [visible, previewCenter.GetValue(player)!, (float)scene.Width, (float)scene.Height])! * context.Transform;
        var pixelSize = target.PixelSize;
        viewport = new PreviewViewport(pixelSize.Width, pixelSize.Height, transform,
            new Vector2(scene.Width / 2f, scene.Height / 2f), target.Dpi.Width, target.Dpi.Height, target.PixelFormat,
            context.AntialiasMode, context.TextAntialiasMode, context.PrimitiveBlend, context.UnitMode,
            scene.ID, scene.Timeline.ID, System.Diagnostics.Stopwatch.GetTimestamp(), (bool)playerIsPlaying.GetValue(player)!);
        return IsValidViewport(viewport, context.MaximumBitmapSize);
    }

    private static bool IsValidViewport(PreviewViewport viewport, int maximumSize)
    {
        var m = viewport.Transform;
        return viewport.Width > 0 && viewport.Height > 0 && viewport.Width <= maximumSize && viewport.Height <= maximumSize
            && float.IsFinite(viewport.DpiX) && float.IsFinite(viewport.DpiY) && viewport.DpiX > 0 && viewport.DpiY > 0
            && float.IsFinite(viewport.TargetOffset.X) && float.IsFinite(viewport.TargetOffset.Y)
            && float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && MathF.Abs(m.GetDeterminant()) > 1e-8f;
    }
    private static bool ValidContext(ID2D1DeviceContext context) => context.Transform == Matrix3x2.Identity
        && context.Dpi.Width == 96 && context.Dpi.Height == 96 && context.UnitMode == UnitMode.Dips;
    private static bool Bypass(string reason)
    {
        using var trace = CacheTrace.Measure("cache-bypass", "state");
        if (trace is not null) { trace.Outcome = "bypass"; trace.Detail = reason; }
        status = reason; Interlocked.Increment(ref bypasses); return true;
    }
    private static void Hit(object source, Pending pending, RectsUpdate update, ItemRect[]? recalled)
    {
        pending.CacheHit = true;
        var devices = pending.Devices;
        devices.CacheProvider.InvalidateIfSourceSettingsChanged();
        devices.CacheProvider.Clear();
        var rects = (IList)itemRects.GetValue(source)!;
        lock (rects)
        {
            if (update != RectsUpdate.Keep) rects.Clear();
            if (update == RectsUpdate.Restore)
            {
                var typed = (List<ItemRect>)rects;
                foreach (var rect in recalled!) typed.Add((rect.item, rect.rect, (Vector2[])rect.quad.Clone(), rect.desc, rect.itemControllers));
            }
        }
        lock (cacheGate)
        {
            pending.State.RectsKey = update is RectsUpdate.Keep or RectsUpdate.Restore ? pending.LiveKey : null;
            pending.State.RectsRevision = pending.Capture.Revision;
        }
        if (pending.WantRects)
        {
            if (update == RectsUpdate.Defer) pending.State.Rects.MarkMissing(pending.Time, System.Diagnostics.Stopwatch.GetTimestamp());
            else pending.State.Rects.Rendered();
        }
        Interlocked.Increment(ref hits);
        status = update == RectsUpdate.Defer ? "キャッシュから表示しました（表示枠は静止後に再計算します）"
            : pending.WantRects ? "キャッシュから表示しました（表示枠も復元しました）"
            : pending.UsageKey == "Exporting" ? "動画出力キャッシュを再利用しました" : "キャッシュから表示しました";
    }

    // The controllers are materialized: the host enumerates them from Draw and the UI thread later.
    private static ItemRect[] SnapshotRects(object source)
    {
        var rects = (List<ItemRect>)itemRects.GetValue(source)!;
        lock (rects)
            return rects.Select(rect => (rect.item, rect.rect, (Vector2[])rect.quad.Clone(), rect.desc,
                (IEnumerable<YukkuriMovieMaker.Player.Video.VideoController>)rect.itemControllers.ToArray())).ToArray();
    }

    // Unknown pointer state counts as over the preview: the frame is then rendered with its rects.
    private static bool PointerOverPreview(object source) =>
        !sourcePlayers.TryGetValue(source, out var association) || association.Player is null
        || !association.Player.TryGetTarget(out var player) || !ReferenceEquals(playerSourceField.GetValue(player), source)
        || pointerOverPreviewField?.GetValue(player) is not false;

    // Runs on the player's render loop before it decides whether to update a paused frame: a frame shown
    // without rects is re-rendered once the playhead rests on it, or at once when the pointer is over the preview.
    private static void BeforeEdit(object __instance)
    {
        try
        {
            if (playerSourceField.GetValue(__instance) is not { } source || !sources.TryGetValue(source, out var state)) return;
            CompleteDeferred(state); // the player loop runs this on the render thread, a frame after the readback began
            var delay = pointerOverPreviewField!.GetValue(__instance) is true ? TimeSpan.Zero : RectsRefreshDelay;
            if (state.Rects.ShouldRequestRefresh(System.Diagnostics.Stopwatch.GetTimestamp(), delay))
                timelineChangedField!.SetValue(__instance, true);
        }
        catch { }
    }
    private static void Disposed(object __instance, bool disposing)
    {
        if (!disposing) return;
        lock (cacheGate)
        {
            if (privateSources.TryGetValue(__instance, out var privatePool)) privatePool.Clear();
            if (!sources.TryGetValue(__instance, out var state)) return;
            state.IsDisposed = true;
            foreach (var deferred in state.Deferred) deferred.Dispose();
            state.Deferred.Clear();
            state.Released();
            state.Pool.Clear();
            foreach (var frame in state.GpuFrames.Values.ToArray()) RemoveGpuFrame(frame);
            state.Tracker.Dispose();
            sources.Remove(__instance);
        }
    }

    // Includes all image bounds, not just the scene rectangle. Origin matches the caller's pixel phase.
    internal static byte[]? Capture(ID2D1DeviceContext context, ID2D1Image output, int width, int height, Vector2 origin)
    {
        long bytes = checked((long)width * height * 4);
        if (width <= 0 || height <= 0 || bytes > FrameCacheStore.MaxFrameBytes - RecordHeader || !Reserve(bytes * 2)) return null;
        ID2D1Image? oldTarget = null;
        var oldTransform = Matrix3x2.Identity;
        var saved = false;
        var drawing = false;
        try
        {
            oldTarget = context.Target;
            oldTransform = context.Transform;
            saved = true;
            using var target = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            using var readable = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                target.PixelFormat, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            context.Target = target;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw(); drawing = true;
            context.Clear(new Color4(0, 0, 0, 0));
            context.DrawImage(output, -origin);
            context.EndDraw().CheckError(); drawing = false;
            context.Target = null;
            readable.CopyFromBitmap(target).CheckError();
            var record = GC.AllocateUninitializedArray<byte>(checked((int)bytes + RecordHeader)); // fully written below
            "YMPX"u8.CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), height);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), origin.X);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), origin.Y);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 1);
            var mapped = readable.Map(MapOptions.Read);
            try
            {
                for (var row = 0; row < height; row++)
                    Marshal.Copy(mapped.Bits + row * mapped.Pitch, record, RecordHeader + row * width * 4, width * 4);
            }
            finally { readable.Unmap(); }
            return record;
        }
        finally
        {
            try
            {
                if (drawing) { try { context.EndDraw(); } catch { } }
                if (saved) { context.Target = oldTarget; context.Transform = oldTransform; }
            }
            finally { oldTarget?.Dispose(); Interlocked.Add(ref gpuBytes, -bytes * 2); }
        }
    }

    internal static byte[]? CapturePreview(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport, ReadbackPool? pool = null)
    {
        using var readback = BeginPreviewReadback(context, output, viewport, false, pool, out _, out _);
        return readback is null ? null : FinishPreviewReadback(readback);
    }

    // A CPU-readable copy of a preview frame that the GPU may still be producing. Holds its GPU reservation; its staging
    // texture goes back to `pool`, if any, when it is disposed.
    internal sealed class PreviewReadback(ID3D11Texture2D readable, ID3D11DeviceContext immediate, PreviewViewport viewport, long bytes,
        ReadbackPool? pool = null) : IDisposable
    {
        private int disposed;
        internal readonly ID3D11Texture2D Readable = readable;
        internal readonly ID3D11DeviceContext Immediate = immediate;
        internal readonly PreviewViewport Viewport = viewport;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { if (pool?.Return(Readable, Viewport) != true) Readable.Dispose(); }
            finally { Immediate.Dispose(); Interlocked.Add(ref gpuBytes, -bytes); }
        }
    }

    // Draws the frame as the player would and queues its copy to a CPU-readable bitmap, without waiting for the GPU.
    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport)
        => BeginPreviewReadback(context, output, viewport, false, null, out _, out _);

    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport,
        bool show, out ID2D1CommandList? shown) => BeginPreviewReadback(context, output, viewport, show, null, out shown, out _);

    // show: also record `shown`, a command list that draws the drawn pixels for the view (as UploadPreview does), so
    // that the frame need not be drawn again. It holds the drawing target and `bytes` of the GPU reservation, both
    // the caller's from then on. Null when show is false or recording failed (the readback is still returned).
    // pool: the drawing target and staging texture come from it and go back to it (the target only once nothing
    // draws it: with `shown`, `shownTarget` is the caller's to return).
    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport,
        bool show, ReadbackPool? pool, out ID2D1CommandList? shown, out PooledTarget? shownTarget)
    {
        shown = null;
        shownTarget = null;
        using var measurement = PreviewPerformance.Measure(PreviewStage.BeginGpuCopy);
        long bytes = checked((long)viewport.Width * viewport.Height * 4);
        if (!IsValidViewport(viewport, context.MaximumBitmapSize) || bytes > FrameCacheStore.MaxFrameBytes - PreviewRecordHeader
            || !Reserve(bytes * 2)) return null;
        ID2D1Image? oldTarget = null;
        ID2D1Bitmap1? target = null;
        PooledTarget? pooled = null;
        ID3D11Texture2D? readable = null;
        ID3D11DeviceContext? immediate = null;
        var oldTransform = Matrix3x2.Identity;
        var oldAntialias = context.AntialiasMode;
        var oldTextAntialias = context.TextAntialiasMode;
        var oldPrimitiveBlend = context.PrimitiveBlend;
        var oldUnitMode = context.UnitMode;
        var saved = false;
        var drawing = false;
        var returned = false;
        try
        {
            oldTarget = context.Target;
            oldTransform = context.Transform;
            saved = true;
            pooled = pool?.TakeTarget(viewport);
            target = pooled?.Bitmap;
            if (target is null)
            {
                target = context.CreateBitmap(new SizeI(viewport.Width, viewport.Height), new BitmapProperties1(
                    viewport.BackBufferFormat, viewport.DpiX, viewport.DpiY, BitmapOptions.Target));
                Interlocked.Increment(ref readbackTexturesCreated);
                if (pool is not null) pooled = new PooledTarget(target);
            }
            context.Target = target;
            context.Transform = viewport.Transform;
            context.AntialiasMode = viewport.AntialiasMode;
            context.TextAntialiasMode = viewport.TextAntialiasMode;
            context.PrimitiveBlend = viewport.PrimitiveBlend;
            context.UnitMode = viewport.UnitMode;
            context.BeginDraw(); drawing = true;
            context.Clear(new Color4(0, 0, 0, 1));
            context.DrawImage(output, viewport.TargetOffset);
            context.EndDraw().CheckError(); drawing = false;
            context.Target = null;
            // Use the actual D2D target's device, never a guessed host/global device. D2D CPU-read Map has
            // no DO_NOT_WAIT flag; an explicit staging texture is needed to keep GPU waits off playback.
            using var surface = target.Surface;
            using var texture = surface.QueryInterface<ID3D11Texture2D>();
            using var device = texture.Device;
            var description = texture.Description;
            if (description.Width != viewport.Width || description.Height != viewport.Height
                || description.MipLevels != 1 || description.ArraySize != 1 || description.SampleDescription.Count != 1
                || description.Format != viewport.BackBufferFormat.Format) return null;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            readable = pool?.TakeStaging(viewport);
            if (readable is null)
            {
                readable = device.CreateTexture2D(description);
                Interlocked.Increment(ref readbackTexturesCreated);
            }
            immediate = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            immediate.CopyResource(readable, texture);
            immediate.Flush(); // submit the copy, including paused frames; does not wait for completion
            if (show)
            {
                // The target is not drawn to again while the copy is shown: like UploadPreview's bitmap, an unchanging
                // image (pooled, it is drawn to only after the copy is gone).
                try { shown = pooled is not null ? pooled.Show(context, viewport) : RecordPreviewImage(context, target, viewport); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { shown = null; }
            }
            var result = new PreviewReadback(readable, immediate, viewport, bytes, pool);
            readable = null;
            immediate = null;
            returned = true;
            return result;
        }
        finally
        {
            try
            {
                if (drawing) { try { context.EndDraw(); } catch { } }
                if (saved)
                {
                    context.Target = oldTarget;
                    context.Transform = oldTransform;
                    context.AntialiasMode = oldAntialias;
                    context.TextAntialiasMode = oldTextAntialias;
                    context.PrimitiveBlend = oldPrimitiveBlend;
                    context.UnitMode = oldUnitMode;
                }
            }
            finally
            {
                oldTarget?.Dispose();
                if (!returned) { shown?.Dispose(); shown = null; }
                // A returned readback keeps the staging half until it is disposed, a shown copy the target half.
                Interlocked.Add(ref gpuBytes, returned ? (shown is null ? -bytes : 0) : -bytes * 2);
                if (shown is not null && pooled is not null) shownTarget = pooled;
                // Copies queued from the target run before anything drawn to it next: it can be reused at once.
                else if (pooled is not null) { if (!pool!.Return(pooled, viewport)) pooled.Dispose(); }
                else target?.Dispose();
                if (readable is not null && pool?.Return(readable, viewport) != true) readable.Dispose();
                immediate?.Dispose();
            }
        }
    }

    // Blocking completion is reserved for explicit capture and test helpers, never live cache updates.
    internal static byte[] FinishPreviewReadback(PreviewReadback readback)
    {
        ReadPreviewReadback(readback, MapFlags.None, out var record);
        return record!;
    }

    internal static bool TryFinishPreviewReadback(PreviewReadback readback, out byte[]? record)
        => ReadPreviewReadback(readback, MapFlags.DoNotWait, out record);

    private static bool ReadPreviewReadback(PreviewReadback readback, MapFlags flags, out byte[]? record)
    {
        record = null;
        using var trace = CacheTrace.Measure("readback-poll", "gpu-copy");
        long mapStarted = PreviewPerformance.Timestamp;
        var result = readback.Immediate.Map(readback.Readable, 0, MapMode.Read, flags, out var mapped);
        PreviewPerformance.End(PreviewStage.MapWait, mapStarted);
        if (result.Code == unchecked((int)0x887A000A)) // DXGI_ERROR_WAS_STILL_DRAWING
        {
            if (trace is not null) trace.Outcome = "gpu-busy";
            return false;
        }
        result.CheckError(); // device loss/other failures invalidate the optional capture
        try
        {
            var viewport = readback.Viewport;
            if (mapped.DataPointer == IntPtr.Zero || mapped.RowPitch < checked(viewport.Width * 4))
                throw new InvalidDataException("Invalid preview staging layout");
            long allocationStarted = PreviewPerformance.Timestamp;
            // The header and every pixel row are written below; zeroing 8 MB first would only cost time.
            record = GC.AllocateUninitializedArray<byte>(checked(viewport.Width * viewport.Height * 4 + PreviewRecordHeader));
            PreviewPerformance.End(PreviewStage.CpuAllocation, allocationStarted);
            "YMPX"u8.CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), viewport.Width);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), viewport.Height);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), 0);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 2);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(24), viewport.DpiX);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(28), viewport.DpiY);
            using var measurement = PreviewPerformance.Measure(PreviewStage.CpuMemcpy);
            for (var row = 0; row < viewport.Height; row++)
                Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, record,
                    PreviewRecordHeader + row * viewport.Width * 4, viewport.Width * 4);
            if (trace is not null) trace.Outcome = "ready";
            return true;
        }
        finally { readback.Immediate.Unmap(readback.Readable, 0); }
    }

    // Cache owner and active-output borrower share one accounted allocation. Each native wrapper
    // owns a distinct COM reference; eviction never disposes the source's wrapper.
    private sealed class GpuFrame(SourceState owner, string key, ID2D1DeviceContext context,
        ID2D1CommandList command, long bytes, long generation)
    {
        internal readonly WeakReference<SourceState> Owner = new(owner);
        internal readonly string Key = key;
        internal readonly ID2D1DeviceContext Context = context;
        internal readonly ID2D1CommandList Command = command;
        internal readonly long Bytes = bytes, Generation = generation;
        internal LinkedListNode<GpuFrame>? Node;
        internal int Owners = 2; // retention + currently displayed output
        internal void Release()
        {
            if (--Owners != 0) return;
            try { Command.Dispose(); }
            finally { Interlocked.Add(ref gpuBytes, -Bytes); }
        }
    }

    // All retention operations run under cacheGate. Global LRU is bounded by bytes and entry count;
    // weak owners do not keep a disposed/unreachable TimelineSource alive.
    private static void RemoveGpuFrame(GpuFrame frame)
    {
        if (frame.Node is null) return;
        gpuLru.Remove(frame.Node); frame.Node = null;
        if (frame.Owner.TryGetTarget(out var owner)) owner.GpuFrames.Remove(frame.Key);
        gpuRetainedBytes -= frame.Bytes;
        frame.Release();
    }
    private static void ClearGpuFrames()
    {
        while (gpuLru.First is { } node) RemoveGpuFrame(node.Value);
    }
    private static void TrimGpuFrames(long incoming)
    {
        while (gpuLru.First is { } node && (gpuRetainedBytes + incoming > gpuRetentionBudget
            || (incoming > 0 && gpuLru.Count >= 64))) RemoveGpuFrame(node.Value);
    }
    private static void RetainUploaded(Pending pending, ID2D1CommandList command, long bytes)
    {
        if (!gpuRetentionEnabled || bytes > gpuRetentionBudget || pending.CacheKey is null) return;
        // Check every LRU victim required to make room. A one-use scan cannot displace equally
        // frequent residents; repeated requests can. Count aging lets a new working set take over.
        long remainingBytes = gpuRetainedBytes;
        int remainingCount = gpuLru.Count;
        int frequency = pending.State.GpuAdmission.Frequency(pending.CacheKey);
        for (var node = gpuLru.First; node is not null && (remainingBytes + bytes > gpuRetentionBudget || remainingCount >= 64); node = node.Next)
        {
            int residentFrequency = node.Value.Owner.TryGetTarget(out var owner) ? owner.GpuAdmission.Frequency(node.Value.Key) : 0;
            if (frequency <= residentFrequency) return;
            remainingBytes -= node.Value.Bytes; remainingCount--;
        }
        ID2D1CommandList? retained = null;
        try
        {
            retained = command.QueryInterface<ID2D1CommandList>();
            if (pending.State.GpuFrames.TryGetValue(pending.CacheKey, out var previous)) RemoveGpuFrame(previous);
            TrimGpuFrames(bytes);
            var frame = new GpuFrame(pending.State, pending.CacheKey, pending.Devices.DeviceContext, retained, bytes, pending.Generation);
            frame.Node = gpuLru.AddLast(frame);
            pending.State.GpuFrames.Add(frame.Key, frame);
            gpuRetainedBytes += bytes;
            pending.State.Bytes = 0; // transfer the existing reservation; do not double-count aliases
            pending.State.ActiveGpuFrame = frame;
            retained = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            using var failure = CacheTrace.Measure("gpu-retention", "state");
            if (failure is not null) { failure.Outcome = "failed"; failure.Detail = error.GetType().Name; }
        }
        finally { retained?.Dispose(); }
    }
    private static bool TryRestoreGpu(object source, Pending pending)
    {
        using var restore = CacheTrace.Measure("gpu-resident-restore");
        if (restore is not null) restore.Outcome = "miss";
        lock (cacheGate)
        {
            if (!gpuRetentionEnabled || pending.CacheKey is null || !pending.State.GpuFrames.TryGetValue(pending.CacheKey, out var frame)) return false;
            if (frame.Generation != pending.Generation || !ReferenceEquals(frame.Context, pending.Devices.DeviceContext)
                || frame.Context.NativePointer == 0 || !StillCurrent(pending)) { RemoveGpuFrame(frame); return false; }
            ID2D1CommandList? borrowed = null;
            bool transferred = false;
            try
            {
                using (CacheTrace.Measure("gpu-command-borrow")) borrowed = frame.Command.QueryInterface<ID2D1CommandList>();
                frame.Owners++;
                if (!CommitReplacement(source, pending, borrowed, 0, frame)) return false;
                transferred = true;
                if (restore is not null) restore.Outcome = "hit";
                gpuLru.Remove(frame.Node!); gpuLru.AddLast(frame.Node!);
                return true;
            }
            finally
            {
                if (!transferred && borrowed is not null) { borrowed.Dispose(); frame.Release(); }
            }
        }
    }
    private static bool CommitReplacement(object source, Pending pending, ID2D1CommandList replacement, long bytes, GpuFrame? borrowed)
    {
        if (!StillCurrent(pending) || !sources.TryGetValue(source, out var currentState)
            || !ReferenceEquals(currentState, pending.State)
            || !ReferenceEquals(outputField.GetValue(source), pending.PreviousOutput)) return false;
        using var swap = CacheTrace.Measure("cache-output-commit");
        var collector = (DisposeCollector)collectorField.GetValue(source)!;
        var previous = (ID2D1CommandList?)outputField.GetValue(source);
        collector.Collect(replacement);
        outputField.SetValue(source, replacement);
        if (previous != null) { collector.Remove(previous); previous.Dispose(); }
        // No host update runs: the host's output kept behind a shown copy (ShowRendered) is released here.
        if (pending.State.HostOutput is { NativePointer: not 0 } host && !ReferenceEquals(host, previous)) { collector.Remove(host); host.Dispose(); }
        pending.State.Released();
        pending.State.LastOutput = replacement;
        pending.State.LastKey = pending.LiveKey;
        pending.State.LastViewportKey = pending.Viewport is null ? null : pending.CacheKey;
        pending.State.Bytes = bytes;
        pending.State.ActiveGpuFrame = borrowed;
        pending.State.Generation = pending.Generation;
        return true;
    }

    private static bool TryReplaceFrame(object source, Pending pending, ReadOnlyMemory<byte> record)
    {
        if (!StillCurrent(pending) || !ParseRecord(record.Span, out int width, out int height, out var origin, out int version, out float dpiX, out float dpiY))
            return false;
        bool exporting = pending.UsageKey == "Exporting";
        if (exporting && version != 1) return false;
        if (!exporting)
        {
            if (version != 2 || pending.Viewport is not PreviewViewport viewport || viewport.Width != width || viewport.Height != height
                || BitConverter.SingleToInt32Bits(viewport.DpiX) != BitConverter.SingleToInt32Bits(dpiX)
                || BitConverter.SingleToInt32Bits(viewport.DpiY) != BitConverter.SingleToInt32Bits(dpiY)) return false;
        }
        long bytes = (long)width * height * 4;
        if (!Reserve(bytes)) return false;
        ID2D1CommandList? replacement = null;
        bool transferred = false;
        try
        {
            var context = pending.Devices.DeviceContext;
            replacement = version == 1 ? Upload(context, record.Span) : UploadPreview(context, record.Span, pending.Viewport!.Value);
            var gateWait = CacheTrace.Measure("cache-output-lock-wait");
            try
            {
                lock (cacheGate)
                {
                    gateWait?.Dispose();
                    if (!CommitReplacement(source, pending, replacement, bytes, null)) return false;
                    transferred = true;
                    if (version == 2) RetainUploaded(pending, replacement, bytes);
                    return true;
                }
            }
            finally { gateWait?.Dispose(); }
        }
        finally
        {
            if (!transferred) { replacement?.Dispose(); Interlocked.Add(ref gpuBytes, -bytes); }
        }
    }

    internal static ID2D1CommandList Upload(ID2D1DeviceContext context, ReadOnlySpan<byte> record)
    {
        if (!ParseRecord(record, out int width, out int height, out var origin, out int version, out _, out _) || version != 1)
            throw new InvalidDataException("Invalid scene pixel record");
        ID2D1Bitmap1 allocated;
        using (CacheTrace.Measure("restore-bitmap-allocation"))
            allocated = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
        using var bitmap = allocated;
        using (CacheTrace.Measure("restore-copy-from-memory")) bitmap.CopyFromMemory(record[RecordHeader..], width * 4).CheckError();
        using var recording = CacheTrace.Measure("restore-command-recording");
        var command = context.CreateCommandList();
        using var oldTarget = context.Target;
        var drawing = false;
        try
        {
            context.Target = command;
            context.BeginDraw(); drawing = true;
            context.DrawImage(bitmap, origin, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            context.EndDraw().CheckError(); drawing = false;
            context.Target = null;
            command.Close().CheckError();
            return command;
        }
        catch { command.Dispose(); throw; }
        finally
        {
            if (drawing) { try { context.EndDraw(); } catch { } }
            context.Target = oldTarget;
        }
    }

    internal static ID2D1CommandList UploadPreview(ID2D1DeviceContext context, ReadOnlySpan<byte> record, PreviewViewport viewport)
    {
        if (!ParseRecord(record, out int width, out int height, out _, out int version, out float dpiX, out float dpiY) || version != 2
            || width != viewport.Width || height != viewport.Height
            || BitConverter.SingleToInt32Bits(dpiX) != BitConverter.SingleToInt32Bits(viewport.DpiX)
            || BitConverter.SingleToInt32Bits(dpiY) != BitConverter.SingleToInt32Bits(viewport.DpiY)
            || !IsValidViewport(viewport, context.MaximumBitmapSize)) throw new InvalidDataException("Invalid preview pixel record");
        ID2D1Bitmap1 allocated;
        using (CacheTrace.Measure("restore-bitmap-allocation"))
            allocated = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(viewport.BackBufferFormat, dpiX, dpiY));
        using var bitmap = allocated;
        using (CacheTrace.Measure("restore-copy-from-memory")) bitmap.CopyFromMemory(record[PreviewRecordHeader..], width * 4).CheckError();
        using var recording = CacheTrace.Measure("restore-command-recording");
        return RecordPreviewImage(context, bitmap, viewport);
    }

    // A command list that draws `image`, the view's pixels (viewport size), 1:1 onto the player's target when the
    // player draws it with the view's transform and offset.
    private static ID2D1CommandList RecordPreviewImage(ID2D1DeviceContext context, ID2D1Image image, PreviewViewport viewport)
    {
        var command = context.CreateCommandList();
        using var oldTarget = context.Target;
        var oldTransform = context.Transform;
        var oldAntialias = context.AntialiasMode;
        var oldTextAntialias = context.TextAntialiasMode;
        var oldPrimitiveBlend = context.PrimitiveBlend;
        var oldUnitMode = context.UnitMode;
        var drawing = false;
        try
        {
            var offsetTransform = Matrix3x2.CreateTranslation(viewport.TargetOffset) * viewport.Transform;
            if (!Matrix3x2.Invert(offsetTransform, out var inverse)) throw new InvalidDataException("Preview transform is not invertible");
            context.Target = command;
            context.Transform = inverse;
            context.AntialiasMode = viewport.AntialiasMode;
            context.TextAntialiasMode = viewport.TextAntialiasMode;
            context.PrimitiveBlend = viewport.PrimitiveBlend;
            context.UnitMode = viewport.UnitMode;
            context.BeginDraw(); drawing = true;
            context.DrawImage(image, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            context.EndDraw().CheckError(); drawing = false;
            context.Target = null;
            command.Close().CheckError();
            return command;
        }
        catch { command.Dispose(); throw; }
        finally
        {
            if (drawing) { try { context.EndDraw(); } catch { } }
            context.Target = oldTarget;
            context.Transform = oldTransform;
            context.AntialiasMode = oldAntialias;
            context.TextAntialiasMode = oldTextAntialias;
            context.PrimitiveBlend = oldPrimitiveBlend;
            context.UnitMode = oldUnitMode;
        }
    }

    internal static bool ParseRecord(ReadOnlySpan<byte> record, out int width, out int height, out Vector2 origin)
        => ParseRecord(record, out width, out height, out origin, out _, out _, out _);

    internal static bool ParseRecord(ReadOnlySpan<byte> record, out int width, out int height, out Vector2 origin,
        out int version, out float dpiX, out float dpiY)
    {
        width = height = version = 0; origin = default; dpiX = dpiY = 96;
        if (record.Length < RecordHeader || !record[..4].SequenceEqual("YMPX"u8)) return false;
        width = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        origin = new(BinaryPrimitives.ReadSingleLittleEndian(record[12..]), BinaryPrimitives.ReadSingleLittleEndian(record[16..]));
        version = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
        int header = version switch { 1 => RecordHeader, 2 => PreviewRecordHeader, _ => 0 };
        if (header == 0 || record.Length < header) return false;
        if (version == 2)
        {
            dpiX = BinaryPrimitives.ReadSingleLittleEndian(record[24..]);
            dpiY = BinaryPrimitives.ReadSingleLittleEndian(record[28..]);
        }
        return width > 0 && height > 0 && float.IsFinite(origin.X) && float.IsFinite(origin.Y)
            && float.IsFinite(dpiX) && float.IsFinite(dpiY) && dpiX > 0 && dpiY > 0
            && (long)width * height * 4 == record.Length - header && record.Length <= FrameCacheStore.MaxFrameBytes;
    }
    // A source's view-sized drawing targets and staging textures, reused by its readbacks instead of creating two
    // textures per stored frame (8 MB each at 1080p: driver allocation, and page faults on the first Map). Pooled
    // textures stay reserved (readbackPoolBytes). A new view size or format, and the source's disposal, release them.
    internal sealed class ReadbackPool
    {
        private const int MaxTargets = 2, MaxStagings = ReadbacksInFlight;
        private readonly object gate = new();
        private readonly Stack<PooledTarget> targets = new();
        private readonly Stack<ID3D11Texture2D> stagings = new();
        private (int Width, int Height, Format Format, Vortice.DCommon.AlphaMode Alpha, float DpiX, float DpiY) shape;
        private bool cleared;

        private static (int, int, Format, Vortice.DCommon.AlphaMode, float, float) ShapeOf(PreviewViewport viewport) =>
            (viewport.Width, viewport.Height, viewport.BackBufferFormat.Format, viewport.BackBufferFormat.AlphaMode, viewport.DpiX, viewport.DpiY);
        private long Bytes => (long)shape.Width * shape.Height * 4;

        internal PooledTarget? TakeTarget(PreviewViewport viewport)
        {
            lock (gate) return Fits(viewport) && targets.TryPop(out var target) ? Taken(target) : null;
        }
        internal ID3D11Texture2D? TakeStaging(PreviewViewport viewport)
        {
            lock (gate) return Fits(viewport) && stagings.TryPop(out var staging) ? Taken(staging) : null;
        }
        // False when the texture is not kept (the caller disposes it).
        internal bool Return(PooledTarget target, PreviewViewport viewport)
        {
            lock (gate) return Keep(viewport, targets, target, MaxTargets);
        }
        internal bool Return(ID3D11Texture2D staging, PreviewViewport viewport)
        {
            lock (gate) return Keep(viewport, stagings, staging, MaxStagings);
        }
        internal void Clear()
        {
            lock (gate)
            {
                cleared = true;
                Drop();
            }
        }

        private T Taken<T>(T texture)
        {
            Interlocked.Add(ref readbackPoolBytes, -Bytes);
            return texture;
        }
        private bool Keep<T>(PreviewViewport viewport, Stack<T> pooled, T texture, int maximum) where T : IDisposable
        {
            if (cleared) return false;
            if (!Fits(viewport))
            {
                Drop();
                shape = ShapeOf(viewport);
            }
            if (pooled.Count >= maximum) return false;
            pooled.Push(texture);
            Interlocked.Add(ref readbackPoolBytes, Bytes);
            return true;
        }
        private bool Fits(PreviewViewport viewport) => shape == ShapeOf(viewport);
        private void Drop()
        {
            int count = targets.Count + stagings.Count;
            while (targets.TryPop(out var target)) target.Dispose();
            while (stagings.TryPop(out var staging)) staging.Dispose();
            if (count != 0) Interlocked.Add(ref readbackPoolBytes, -count * Bytes);
        }
    }

    // A pooled drawing target and the command list that shows it 1:1 for a view (RecordPreviewImage), recorded once
    // and shown again whenever the target is reused for that view: the copy draws whatever the target holds now.
    internal sealed class PooledTarget(ID2D1Bitmap1 bitmap) : IDisposable
    {
        internal ID2D1Bitmap1 Bitmap { get; } = bitmap;
        private ID2D1CommandList? display;
        private PreviewViewport displayView;

        // A new reference to the target's copy for `viewport` (the caller owns it), recorded only for a new view.
        internal ID2D1CommandList Show(ID2D1DeviceContext context, PreviewViewport viewport)
        {
            var view = viewport with { LastDrawTimestamp = 0, IsPlaying = false };
            if (display is not { NativePointer: not 0 } recorded || displayView != view)
            {
                display?.Dispose();
                display = null;
                recorded = RecordPreviewImage(context, Bitmap, viewport);
                display = recorded;
                displayView = view;
            }
            return recorded.QueryInterface<ID2D1CommandList>();
        }

        public void Dispose()
        {
            try { display?.Dispose(); }
            finally { Bitmap.Dispose(); }
        }
    }

    private static bool Reserve(long bytes)
    {
        while (true)
        {
            long current = Interlocked.Read(ref gpuBytes);
            if (bytes <= 0 || bytes > GpuBudget - current - Interlocked.Read(ref readbackPoolBytes)) return false;
            if (Interlocked.CompareExchange(ref gpuBytes, current + bytes, current) == current) return true;
        }
    }
}

