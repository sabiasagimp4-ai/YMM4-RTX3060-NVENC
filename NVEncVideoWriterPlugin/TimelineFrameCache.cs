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
internal static partial class TimelineFrameCache
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const int RecordHeader = 24;
    private const int PreviewRecordHeader = 32;
    // Images the cache holds on the GPU besides retained frames: uploads in flight, readback textures, shown copies.
    // The whole reservation is retention plus this (384 MiB at the initial 128 MiB retention).
    private const long GpuWorkingBudget = 256L * 1024 * 1024;
    private const long MaxGpuRetentionBudget = 8192L * 1024 * 1024;
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
    private static long gpuRetainedBytes, gpuRetentionBudget = GpuMemoryPolicy.InitialBudget;
    private static bool gpuRetentionEnabled = true;
    private static Lazy<FrameCacheStore> store = new(() => CacheMemoryController.CreateStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4-RTX3060-NVENC", "cache")));
    private static FieldInfo sceneField = null!, devicesField = null!, outputField = null!, collectorField = null!, pickerField = null!;
    private static FieldInfo playerSourceField = null!, playerContextField = null!, playerTargetField = null!;
    private static PropertyInfo needRects = null!, itemRects = null!, backBuffer = null!, playerIsPlaying = null!;
    // Null on players without zoom and pan (YMM4 4.54 and older), which draw the output with the context's transform.
    private static PropertyInfo? previewZoom, previewCenter;
    private static MethodInfo? visibleVideoSize, previewTransform;
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
        set { lock (cacheGate) { gpuRetentionBudget = Math.Clamp(value, 0, MaxGpuRetentionBudget); TrimGpuFrames(0); } }
    }
    // Lock-free, for GpuMemoryController: frames over a lowered budget are released on the render thread at its next
    // Update (TrimGpuFrames there), not on the controller's timer thread.
    internal static long GpuRetentionBudgetNow => Interlocked.Read(ref gpuRetentionBudget);
    internal static long GpuRetainedBytesNow => Interlocked.Read(ref gpuRetainedBytes);
    internal static void SetGpuRetentionBudgetDeferred(long value) =>
        Interlocked.Exchange(ref gpuRetentionBudget, Math.Clamp(value, 0, MaxGpuRetentionBudget));
    private static long GpuImageBudget => Interlocked.Read(ref gpuRetentionBudget) + GpuWorkingBudget;
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
    private static long gpuReadAheads;
    internal static long GpuReadAheads => Interlocked.Read(ref gpuReadAheads);
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
    internal static Action? BeforeCacheLookupForTests { get; set; }

    // Tests only: a store with other budgets or in another folder (the caller disposes it).
    internal static void UseStore(FrameCacheStore replacement)
    {
        lock (cacheGate) { Interlocked.Increment(ref generation); ClearGpuFrames(); store = new Lazy<FrameCacheStore>(replacement); }
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
        bool readinessAdded = false;
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
                previewZoom = playerType.GetProperty("PreviewDisplayZoom");
                previewCenter = playerType.GetProperty("PreviewViewCenter");
                playerIsPlaying = playerType.GetProperty("IsPlaying", Instance)!;
                visibleVideoSize = playerType.GetMethod("GetVisibleVideoSize", Instance);
                previewTransform = playerType.GetMethod("CreatePreviewViewTransform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                backBuffer = playerTargetField.FieldType.GetProperty("BackBuffer")!;
                draw = playerType.GetMethods(Instance).Single(m => m.Name == "Draw" && m.GetParameters().Length == 0);
                edit = playerType.GetMethod("Edit", Instance, Type.EmptyTypes);
                // The players of 4.54 and older have none of the zoom and pan members (all four or none).
                bool zoomable = previewZoom is not null || previewCenter is not null || visibleVideoSize is not null || previewTransform is not null;
                if (playerSourceField.FieldType != type || playerContextField.FieldType != typeof(IGraphicsDevicesAndContext)
                    || zoomable && (previewZoom?.PropertyType != typeof(float) || previewCenter?.PropertyType != typeof(Vector2)
                        || visibleVideoSize is null || visibleVideoSize.GetParameters().Length != 1 || visibleVideoSize.GetParameters()[0].ParameterType != typeof(float)
                        || previewTransform is null || !previewTransform.GetParameters().Select(p => p.ParameterType)
                            .SequenceEqual([typeof(Vector2), typeof(Vector2), typeof(float), typeof(float)]))
                    || playerIsPlaying.PropertyType != typeof(bool) || backBuffer.PropertyType != typeof(ID2D1Bitmap1)
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
            readinessAdded = true;
            if (!NativeTachieReadiness.TryInstall(host, harmony, out reason))
                throw new NotSupportedException(reason);
            // Randomness seeded by the renderer's own objects is drawn from the model where the code was reviewed.
            if (features.IdentityRandom) RandomSeedAlignment.TryInstall(host, harmony);
            // Items YMM4 leaves in no particular draw order (same layer, overlapping) are drawn in item-list order.
            DrawOrderAlignment.TryInstall(host, harmony, out _);
            // The bundled tachie's blinking is seeded alike in every run: patched now when the audited plugin is loaded,
            // else when a verified tachie is first described.
            BlinkSeedAlignment.Use(harmony);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!assembly.IsDynamic) { AnimationTachieDependencies.AlignBlink(assembly); PsdTachieDependencies.AlignBlink(assembly); }
            reason = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            RandomSeedAlignment.Uninstall(harmony);
            DrawOrderAlignment.Uninstall(harmony);
            BlinkSeedAlignment.Uninstall(harmony);
            if (readinessAdded) FrameRenderReadiness.Uninstall(harmony);
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
        internal bool DeviceLost;
        internal long RecentUpdateTicks;
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
        string environment, int fps, object owner) : IDisposable
    {
        // The render-thread state and frame rate LiveKey/CacheKey were composed with (StillCurrent compares them).
        internal readonly object Owner = owner;
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
        internal readonly FrameCacheStore.Publication Publication = cacheKey is null ? default : store.Value.BeginPublication();
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
        if (__state.RunsHost) RestoreHostOutputBeforeUpdate(__instance);
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
            if (state.DeviceLost) return Bypass("描画デバイスの再作成を待っているため、通常描画を使用します。");
            string environment = KeyEnvironment(context);
            state.Environment = environment;
            if (!state.Tracker.TryCapture(FrameOf(time, scene), out capture, out var reason, settle: true, background: preview)) return Bypass(reason);
            if ((!AnimationTachieDependencies.SafeSource(__instance, scene, FrameOf(time, scene))
                || !PsdTachieDependencies.SafeSource(__instance, scene, FrameOf(time, scene), capture)))
                return Bypass("立ち絵の画像一覧・付属設定・描画ソースを確認できないため、通常描画を使用します。");
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
                currentGeneration, time, usageKey, viewport, wantRects, traits.RectsReusable, usageName == "Playing", environment, fps, __instance);
            capture = null;
            BeforeCacheLookupForTests?.Invoke();
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
                TrimGpuFrames(0); // a budget lowered by GpuMemoryController takes effect on this thread
                if (gpuRetentionEnabled && viewport is not null && cacheKey is not null) state.GpuAdmission.Observe(cacheKey);
                // The capture's full validation runs last: it is the expensive part, and during playback the key
                // rarely matches the previous frame's.
                if (currentGeneration == Interlocked.Read(ref generation) && state.Generation == currentGeneration
                    && state.LastKey == liveKey && (state.LastViewportKey is null || state.LastViewportKey == cacheKey)
                    && state.LastOutput is { NativePointer: not 0 }
                    && ReferenceEquals(state.LastOutput, previousOutput) && StillCurrent(pending))
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
                ReadAhead(state, scene, time, usageKey, resident, !paused, devices);
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
            if (viewport is { } view) ReadAhead(state, scene, time, usageKey, view, !paused, devices);
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
        catch (Exception error)
        {
            ObserveDeviceLoss(pending?.State ?? (sources.TryGetValue(__instance, out var failedState) ? failedState : null), error);
            pending?.Dispose(); return Bypass("キャッシュを使用しませんでした: " + error.GetType().Name);
        }
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
            if (!FrameRenderReadiness.IsUpdateReady(__instance, __state.Capture.Shown))
            {
                status = FrameRenderReadiness.CoverageProblem ?? "動画のデコード完了を確認できないフレームは保存しません。";
                return;
            }
            if (!StillCurrent(__state)) return;
            var output = (ID2D1CommandList)outputField.GetValue(__instance)!;
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
            if (__state.CacheKey is not null)
                deferred = __state.Viewport is { } view ? StorePreview(__instance, __state, output, view) : StoreExport(__state, output);
        }
        catch (Exception error) { ObserveDeviceLoss(__state.State, error); status = "フレームの保存に失敗しました: " + error.GetType().Name; }
        finally { if (!deferred) __state.Dispose(); }
    }

    // Queues disk reads for the frames the player shows next (playback: the next half second; paused: two frames
    // either way, for stepping and scrubbing). Keys come from the current description without leasing files: a read
    // only moves a stored record into RAM, and showing it still needs the exact key of a validated capture.
    private static void ReadAhead(SourceState state, Scene scene, TimeSpan time, string usage, PreviewViewport viewport, bool playing,
        IGraphicsDevicesAndContext devices)
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
            string stamp = $"{environment}|{usage}|{scene.FPS}|{viewport.Normalized}";
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
            if (playing && viewport.IsPlaying) WarmNextGpuFrame(state, devices, keys, viewport, scene.FPS);
        }
        catch (Exception error) { ObserveDeviceLoss(state, error); } // optional
    }

    private static Exception? Finalizer(object __instance, Exception? __exception, UpdateMeasurement? __state)
    {
        if (__exception is null && sources.TryGetValue(__instance, out var lostState) && lostState.DeviceLost)
            lostState.Released(); // The bypassed host update released its old displayed borrower.
        if (__state is not null)
        {
            __state.EndHost();
            __state.Pending?.Dispose();
            __state.TotalTicks = PreviewPerformance.Timestamp - __state.Started;
            if (__state.Pending is { } pending)
            {
                pending.State.RecentUpdateTicks = __state.TotalTicks;
                ObserveDeviceLoss(pending.State, __exception);
            }
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
        if (!valid || (!AnimationTachieDependencies.SafeSource(value.Owner, value.Scene, FrameOf(value.Time, value.Scene))
            || !PsdTachieDependencies.SafeSource(value.Owner, value.Scene, FrameOf(value.Time, value.Scene), value.Capture))) return false;
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
        // Lower case, as the store keeps keys: its lookups then use the key as it is instead of a lowered copy.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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
        GpuMemoryController.ObserveAdapter(adapter);
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

    // For the cache bars: changes whenever what TryGetPreviewResidency reports for the timeline may change (the store's
    // contents, the frames' keys, the view or the player's source), except that keys described or verified in the
    // background are adopted only when someone asks (a bar asks again after a while anyway). Null while unknown.
    internal readonly record struct ResidencyStamp(long Store, long Keys, long Revision, int Source, PreviewViewport View, string Environment);

    internal static ResidencyStamp? PreviewResidencyStamp(Timeline timeline)
    {
        if (!PreviewEnabled || StoreIfCreated is not { } created) return null;
        object? source;
        PreviewViewport viewport;
        lock (cacheGate)
        {
            if (!latestViewports.TryGetValue(timeline, out var latest) || latest.Source is null
                || !latest.Source.TryGetTarget(out source)) return null;
            viewport = latest.Viewport;
        }
        if (!sources.TryGetValue(source, out var state) || state.Environment is not { } environment) return null;
        return new(created.Version, state.Tracker.KeyStamp, state.Tracker.Revision, RuntimeHelpers.GetHashCode(state), viewport.Normalized, environment);
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
        var keys = new string?[frames.Count];
        lock (state.StatusKeys)
        {
            string stamp = $"{environment}|{usage}|{scene.FPS}|{viewport.Normalized}";
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

    // A producer that renders before it stores (the idle pre-renderer) takes this before rendering: a purge or a store
    // swap after it rejects the frame, however late the producer reaches the commit.
    internal readonly record struct PrimeTicket(long Generation, FrameCacheStore.Publication Publication);
    internal static PrimeTicket BeginPrime() => new(Interlocked.Read(ref generation), store.Value.BeginPublication());

    // capture: a capture of this frame from a tracker of the source's own scene that the caller holds (the idle
    // pre-renderer's clone capture); without it the source's tracker captures the frame here.
    // ticket: taken before the frame was rendered (BeginPrime); without it, the purge state when this is called.
    internal static bool TryPrimePreviewIfCurrent(object timelineSource, TimeSpan time, object usage,
        PreviewViewport viewport, string? expectedModelKey, CancellationToken cancellation, KeyCapture? capture = null,
        PrimeTicket? ticket = null) =>
        TryPrimeCore(timelineSource, time, usage, viewport, expectedModelKey, cancellation, capture, ticket);

    // The idle pre-renderer's own renderer: Updates of it skip the live preview's cache (lookups, keys, stores).
    internal static void ExcludeFromPreviewCache(object sourceOrOwner) =>
        privateSources.AddOrUpdate(GetTimelineSource(sourceOrOwner), new ReadbackPool());

    private static bool TryPrimeCore(object timelineSource, TimeSpan time, object usage,
        PreviewViewport? viewport, string? expectedModelKey, CancellationToken cancellation = default, KeyCapture? provided = null,
        PrimeTicket? ticket = null)
    {
        if (!Enabled || cancellation.IsCancellationRequested) return false;
        long captureGeneration = ticket?.Generation ?? Interlocked.Read(ref generation);
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
                // The images the key names must be the ones the render showed.
                if (!FrameRenderReadiness.WasLastUpdateReady(timelineSource, time, capture!.Shown)) return false;
                var traits = modelTraits.GetValue(capture!.Model, static model => new ModelTraits(model));
                string usageKey = exporting ? usageName : PreviewUsage.KeyFor(usageName, traits.ShowOnlyPreview);
                var key = MakeKey(capture.Key, time, scene.FPS, usageKey, context, viewport);
                var destination = store.Value;
                var publication = ticket?.Publication ?? destination.BeginPublication();
                var output = (ID2D1CommandList)outputField.GetValue(timelineSource)!;
                privateSources.TryGetValue(timelineSource, out var pool); // the idle renderer's textures, reused
                var record = viewport is { } view ? CapturePreview(context, output, view, pool) : CaptureScene(context, output, scene);
                if (record is null || !capture.Validate()) return false; // resolves the files again: outside cacheGate
                lock (cacheGate)
                {
                    if (cancellation.IsCancellationRequested || captureGeneration != generation
                        || !capture.Validate(files: false) || !EnabledFor(exporting)) return false;
                    if (!destination.PutOwned(key, record, publication)) return false;
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
        HostApi.InvalidateIfSourceSettingsChanged(devices.CacheProvider);
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
        if (sources.TryGetValue(__instance, out var exported)) FinishExportStores(exported);
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
}
