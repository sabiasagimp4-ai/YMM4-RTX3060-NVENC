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
    // Rect reuse depends on which preview controllers the host builds and from what state; read in YMM4 4.56.1.0.
    private static readonly Guid RectReuseVerifiedHost = Guid.Parse("23e5b5b5-adcf-43b7-b976-b6b63f8dadea");
    private static readonly TimeSpan RectsRefreshDelay = TimeSpan.FromMilliseconds(100);
    private static readonly ConditionalWeakTable<string, ModelTraits> modelTraits = new();
    private static readonly ConditionalWeakTable<object, SourceState> sources = new();
    private static readonly ConditionalWeakTable<object, PlayerAssociation> sourcePlayers = new();
    private static readonly ConditionalWeakTable<Timeline, LatestViewport> latestViewports = new();
    private static readonly ConditionalWeakTable<ID2D1DeviceContext, string> renderEnvironments = new();
    private static readonly object cacheGate = new();
    private static readonly Lazy<FrameCacheStore> store = new(() => new FrameCacheStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4-RTX3060-NVENC", "cache")));
    private static FieldInfo sceneField = null!, devicesField = null!, outputField = null!, collectorField = null!, pickerField = null!;
    private static FieldInfo playerSourceField = null!, playerContextField = null!, playerTargetField = null!;
    private static PropertyInfo needRects = null!, itemRects = null!, previewZoom = null!, previewCenter = null!, backBuffer = null!, playerIsPlaying = null!;
    private static MethodInfo visibleVideoSize = null!, previewTransform = null!;
    private static FieldInfo? timelineChangedField, pointerOverPreviewField;
    private static Type pickerType = null!;
    private static long hits, misses, gpuBytes, generation;
    private static string status = "自動キャッシュは停止中です";
    private static bool enabled, rectsSupported, refreshSupported;

    internal static bool Enabled
    {
        get => Volatile.Read(ref enabled);
        set { lock (cacheGate) { Volatile.Write(ref enabled, value); Interlocked.Increment(ref generation); status = value ? "キャッシュ待機中" : "自動キャッシュは停止中です"; } }
    }
    internal static string Status => Volatile.Read(ref status);
    internal static long Hits => Interlocked.Read(ref hits);
    internal static long Misses => Interlocked.Read(ref misses);
    internal static long GpuBytes => Interlocked.Read(ref gpuBytes);

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
            status = "キャッシュを消去しています…";
        }
        try { if (store.IsValueCreated) store.Value.Clear(); }
        catch { status = "キャッシュを完全には消去できませんでした"; throw; }
        status = "キャッシュを消去しました";
    }

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        var patched = new List<MethodBase>();
        try
        {
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
            var draw = playerType.GetMethods(Instance).Single(m => m.Name == "Draw" && m.GetParameters().Length == 0);
            var update = type.GetMethods(Instance).Single(m => m.Name == "Update" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(TimeSpan));
            var dispose = type.GetMethod("Dispose", Instance, [typeof(bool)])!;
            var edit = playerType.GetMethod("Edit", Instance, Type.EmptyTypes);
            if (playerSourceField.FieldType != type || playerContextField.FieldType != typeof(IGraphicsDevicesAndContext)
                || previewZoom.PropertyType != typeof(float) || previewCenter.PropertyType != typeof(Vector2)
                || playerIsPlaying.PropertyType != typeof(bool) || backBuffer.PropertyType != typeof(ID2D1Bitmap1)
                || visibleVideoSize.GetParameters().Length != 1 || visibleVideoSize.GetParameters()[0].ParameterType != typeof(float)
                || !previewTransform.GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual([typeof(Vector2), typeof(Vector2), typeof(float), typeof(float)])
                || draw.ReturnType != typeof(void))
                throw new NotSupportedException("TimelineVideoPlayer preview contract changed");

            EnsureNoExternalHarmonyOwners(update, harmony.Id);
            EnsureNoExternalHarmonyOwners(dispose, harmony.Id);
            EnsureNoExternalHarmonyOwners(draw, harmony.Id);
            // Optional: without them, frames that need item rects are rendered normally.
            rectsSupported = host.ManifestModule.ModuleVersionId == RectReuseVerifiedHost && itemRects.PropertyType == typeof(List<ItemRect>);
            timelineChangedField = playerType.GetField("isTimelineChanged", Instance);
            pointerOverPreviewField = playerType.GetField("isMouseOverPreviewArea", Instance);
            refreshSupported = rectsSupported && edit?.ReturnType == typeof(void)
                && timelineChangedField?.FieldType == typeof(bool) && pointerOverPreviewField?.FieldType == typeof(bool)
                && (Harmony.GetPatchInfo(edit)?.Owners.All(owner => owner == harmony.Id) ?? true);
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Postfix)),
                finalizer: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Finalizer)));
            patched.Add(update);
            harmony.Patch(dispose, postfix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(Disposed)));
            patched.Add(dispose);
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(TimelineFrameCache), nameof(ObservePlayer)));
            patched.Add(draw);
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
        internal long Bytes;
        internal long Generation;
        internal void Released()
        {
            var released = Interlocked.Exchange(ref Bytes, 0);
            if (released != 0) Interlocked.Add(ref gpuBytes, -released);
            LastKey = null;
            LastViewportKey = null;
            LastOutput = null;
            RectsKey = null;
        }
        ~SourceState() { Released(); try { Tracker.Dispose(); } catch { } }
    }

    private sealed class ModelTraits(string model)
    {
        internal readonly bool ShowOnlyPreview = PreviewUsage.ModelUsesShowOnlyPreview(model);
        internal readonly bool RectsReusable = PreviewUsage.RectsReusable(model);
    }

    private enum RectsUpdate { Clear, Keep, Restore, Defer }

    private sealed class Pending(SourceState state, Scene scene, IGraphicsDevicesAndContext devices,
        ID2D1CommandList? previousOutput, KeyCapture capture, string liveKey, string? cacheKey,
        long generation, TimeSpan time, string usageKey, PreviewViewport? viewport, bool wantRects, bool rectsReusable) : IDisposable
    {
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
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Capture.Dispose(); }
    }

    private static bool Prefix(object __instance, TimeSpan time, object usage, out Pending? __state)
    {
        __state = null;
        if (!Enabled) return true;
        KeyCapture? capture = null;
        Pending? pending = null;
        try
        {
            string usageName = usage.ToString() ?? string.Empty;
            bool exporting = usageName == "Exporting", paused = usageName == "Paused", preview = paused || usageName == "Playing";
            if (!exporting && !preview) return Bypass("キャッシュの対象はプレビュー（再生・一時停止）と動画出力の描画だけです。");
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
            if (!state.Tracker.TryCapture(FrameOf(time, scene), out capture, out var reason, settle: true)) return Bypass(reason);
            var traits = modelTraits.GetValue(capture!.Model, static model => new ModelTraits(model));
            string usageKey = exporting ? usageName : PreviewUsage.KeyFor(usageName, traits.ShowOnlyPreview);
            PreviewViewport? viewport = preview && TryGetPreviewViewportForSource(__instance, out var currentViewport)
                && currentViewport.SceneId == scene.ID && currentViewport.TimelineId == scene.Timeline.ID ? currentViewport : null;
            string liveKey = MakeKey(capture.Key, time, scene.FPS, usageKey, context, null);
            string? cacheKey = exporting ? liveKey : viewport is { } value ? MakeKey(capture.Key, time, scene.FPS, usageKey, context, value) : null;
            long currentGeneration = Interlocked.Read(ref generation);
            long revision = capture.Revision;
            var previousOutput = (ID2D1CommandList?)outputField.GetValue(__instance);
            pending = new Pending(state, scene, devices, previousOutput, capture, liveKey, cacheKey,
                currentGeneration, time, usageKey, viewport, wantRects, traits.RectsReusable);
            capture = null;
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
                        __state = pending;
                        return false;
                    }
                }
            }
            if (stored is { } fromStore && cacheKey is not null && store.Value.TryGet(cacheKey, out var record)
                && TryReplaceFrame(__instance, pending, record))
            {
                Hit(__instance, pending, fromStore, recalled);
                __state = pending;
                return false;
            }
            Interlocked.Increment(ref misses);
            __state = pending;
            return true;
        }
        catch (Exception error) { pending?.Dispose(); return Bypass("キャッシュを使用しませんでした: " + error.GetType().Name); }
        finally { capture?.Dispose(); }
    }

    private static void Postfix(object __instance, Pending? __state)
    {
        if (__state is null) return;
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
                lock (cacheGate) if (record != null && StillCurrent(__state))
                {
                    store.Value.PutOwned(__state.CacheKey, record);
                    status = "描画したフレームを保存しました。";
                }
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
        }
        catch (Exception error) { status = "フレームの保存に失敗しました: " + error.GetType().Name; }
        finally { __state.Dispose(); }
    }

    private static Exception? Finalizer(Exception? __exception, Pending? __state) { __state?.Dispose(); return __exception; }

    private static bool StillCurrent(Pending value) => Enabled && value.Generation == Interlocked.Read(ref generation)
        && value.Capture.Validate() && MakeKey(value.Capture.Key, value.Time, value.Scene.FPS, value.UsageKey, value.Devices.DeviceContext, null) == value.LiveKey
        && (value.Viewport is null || value.CacheKey == MakeKey(value.Capture.Key, value.Time, value.Scene.FPS, value.UsageKey, value.Devices.DeviceContext, value.Viewport.Value));

    // usage: "Exporting", or the preview key from PreviewUsage.KeyFor ("Preview" unless ShowOnlyPreviewEffect is used).
    private static string MakeKey(string model, TimeSpan time, int fps, string usage, ID2D1DeviceContext context, PreviewViewport? viewport)
    {
        string value = $"pixels-v5|{RenderEnvironment(context)}|{model}|{FrameTimeKey.For(time, fps)}|{usage}|{context.AntialiasMode}|{context.TextAntialiasMode}|{context.PrimitiveBlend}";
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

    internal static bool TryPrime(object timelineSource, TimeSpan time, object usage) =>
        TryPrimeCore(timelineSource, time, usage, null, null);

    internal static bool TryPrimePreview(object timelineSource, TimeSpan time, object usage,
        PreviewViewport viewport, string? expectedModelKey = null) =>
        TryPrimeCore(timelineSource, time, usage, viewport, expectedModelKey);

    private static bool TryPrimeCore(object timelineSource, TimeSpan time, object usage,
        PreviewViewport? viewport, string? expectedModelKey)
    {
        if (!Enabled) return false;
        try
        {
            timelineSource = GetTimelineSource(timelineSource);
            if (!FrameRenderReadiness.WasLastUpdateReady(timelineSource, time)) return false;
            var scene = (Scene)sceneField.GetValue(timelineSource)!;
            if (scene.ParentScenes.Length != 0 || (bool)needRects.GetValue(timelineSource)!) return false;
            string usageName = usage.ToString() ?? string.Empty;
            bool exporting = usageName == "Exporting", playing = usageName == "Playing";
            if (viewport is null ? !exporting : !playing || !IsValidViewport(viewport.Value, int.MaxValue)
                || viewport.Value.SceneId != scene.ID || viewport.Value.TimelineId != scene.Timeline.ID) return false;
            if (viewport is { } preview && (preview.IsPlaying || preview.LastDrawTimestamp <= 0
                || System.Diagnostics.Stopwatch.GetElapsedTime(preview.LastDrawTimestamp) > TimeSpan.FromSeconds(30))) return false;
            var picker = pickerField.GetValue(timelineSource)!;
            if (picker.GetType() != pickerType || pickerType.GetFields(Instance).Any(f => f.GetValue(picker) != null)) return false;
            var devices = (IGraphicsDevicesAndContext)devicesField.GetValue(timelineSource)!;
            var context = devices.DeviceContext;
            if (!ValidContext(context)) return false;
            var state = sources.GetValue(timelineSource, _ => new SourceState(scene));
            if (!state.Tracker.TryCapture(FrameOf(time, scene), out var capture, out _)) return false;
            using (capture)
            {
                if (expectedModelKey is not null && capture!.Key != expectedModelKey) return false;
                var traits = modelTraits.GetValue(capture!.Model, static model => new ModelTraits(model));
                string usageKey = exporting ? usageName : PreviewUsage.KeyFor(usageName, traits.ShowOnlyPreview);
                var key = MakeKey(capture.Key, time, scene.FPS, usageKey, context, viewport);
                var output = (ID2D1CommandList)outputField.GetValue(timelineSource)!;
                var record = viewport is { } view ? CapturePreview(context, output, view) : CaptureScene(context, output, scene);
                lock (cacheGate)
                {
                    if (record is null || !capture.Validate() || !Enabled) return false;
                    store.Value.PutOwned(key, record);
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

    private static void ObservePlayer(object __instance)
    {
        try
        {
            var source = playerSourceField.GetValue(__instance);
            if (source is null || !TryGetPreviewViewportForPlayer(__instance, source, out var viewport)) return;
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
    private static bool Bypass(string reason) { status = reason; return true; }
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
            : pending.WantRects ? "キャッシュから表示しました（表示枠も復元しました）" : "動画出力キャッシュを再利用しました";
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
            if (!sources.TryGetValue(__instance, out var state)) return;
            state.Released();
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
            var record = new byte[checked((int)bytes + RecordHeader)];
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

    internal static byte[]? CapturePreview(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport)
    {
        long bytes = checked((long)viewport.Width * viewport.Height * 4);
        if (!IsValidViewport(viewport, context.MaximumBitmapSize) || bytes > FrameCacheStore.MaxFrameBytes - PreviewRecordHeader
            || !Reserve(bytes * 2)) return null;
        ID2D1Image? oldTarget = null;
        var oldTransform = Matrix3x2.Identity;
        var oldAntialias = context.AntialiasMode;
        var oldTextAntialias = context.TextAntialiasMode;
        var oldPrimitiveBlend = context.PrimitiveBlend;
        var oldUnitMode = context.UnitMode;
        var saved = false;
        var drawing = false;
        try
        {
            oldTarget = context.Target;
            oldTransform = context.Transform;
            saved = true;
            using var target = context.CreateBitmap(new SizeI(viewport.Width, viewport.Height), new BitmapProperties1(
                viewport.BackBufferFormat, viewport.DpiX, viewport.DpiY, BitmapOptions.Target));
            using var readable = context.CreateBitmap(new SizeI(viewport.Width, viewport.Height), new BitmapProperties1(
                viewport.BackBufferFormat, viewport.DpiX, viewport.DpiY, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
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
            readable.CopyFromBitmap(target).CheckError();
            var record = new byte[checked((int)bytes + PreviewRecordHeader)];
            "YMPX"u8.CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), viewport.Width);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), viewport.Height);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), 0);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 2);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(24), viewport.DpiX);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(28), viewport.DpiY);
            var mapped = readable.Map(MapOptions.Read);
            try
            {
                for (var row = 0; row < viewport.Height; row++)
                    Marshal.Copy(mapped.Bits + row * mapped.Pitch, record, PreviewRecordHeader + row * viewport.Width * 4, viewport.Width * 4);
            }
            finally { readable.Unmap(); }
            return record;
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
            finally { oldTarget?.Dispose(); Interlocked.Add(ref gpuBytes, -bytes * 2); }
        }
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
            lock (cacheGate)
            {
                if (!StillCurrent(pending) || !sources.TryGetValue(source, out var currentState)
                    || !ReferenceEquals(currentState, pending.State)
                    || !ReferenceEquals(outputField.GetValue(source), pending.PreviousOutput)) return false;
                var collector = (DisposeCollector)collectorField.GetValue(source)!;
                var previous = (ID2D1CommandList?)outputField.GetValue(source);
                collector.Collect(replacement);
                outputField.SetValue(source, replacement);
                if (previous != null) { collector.Remove(previous); previous.Dispose(); }
                pending.State.Released();
                pending.State.LastOutput = replacement;
                pending.State.LastKey = pending.LiveKey;
                pending.State.LastViewportKey = version == 2 ? pending.CacheKey : null;
                pending.State.Bytes = bytes;
                pending.State.Generation = pending.Generation;
                transferred = true;
                return true;
            }
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
        using var bitmap = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
        bitmap.CopyFromMemory(record[RecordHeader..], width * 4).CheckError();
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
        using var bitmap = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
            viewport.BackBufferFormat, dpiX, dpiY));
        bitmap.CopyFromMemory(record[PreviewRecordHeader..], width * 4).CheckError();
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
            context.DrawImage(bitmap, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
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
    private static bool Reserve(long bytes)
    {
        while (true)
        {
            long current = Interlocked.Read(ref gpuBytes);
            if (bytes <= 0 || bytes > GpuBudget - current) return false;
            if (Interlocked.CompareExchange(ref gpuBytes, current + bytes, current) == current) return true;
        }
    }
}
