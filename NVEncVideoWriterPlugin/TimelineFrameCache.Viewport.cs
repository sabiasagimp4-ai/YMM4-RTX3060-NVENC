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

// The player's view of a source: computing it, remembering the latest one for the idle pre-renderer and the cache
// bars, and the hooks around the player's Draw.
internal static partial class TimelineFrameCache
{
    // Tests only: the preview view of a source, standing in for a TimelineVideoPlayer with a real swap chain.
    internal static Func<object, PreviewViewport?>? TestViewport { get; set; }

    internal readonly record struct PreviewViewport(int Width, int Height, Matrix3x2 Transform, Vector2 TargetOffset,
        float DpiX, float DpiY, Vortice.DCommon.PixelFormat BackBufferFormat,
        AntialiasMode AntialiasMode, TextAntialiasMode TextAntialiasMode, PrimitiveBlend PrimitiveBlend, UnitMode UnitMode,
        Guid SceneId, Guid TimelineId, long LastDrawTimestamp, bool IsPlaying)
    {
        // The view alone: when it was drawn last and whether it plays do not change its pixels.
        internal PreviewViewport Normalized => this with { LastDrawTimestamp = 0, IsPlaying = false };
        // BGRA bytes of one frame of the view.
        internal long FrameBytes => checked((long)Width * Height * 4);
    }

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

    private static bool TryGetPreviewViewportForSource(object source, out PreviewViewport viewport)
    {
        viewport = default;
        if (TestViewport?.Invoke(source) is { } test)
        {
            viewport = test with { LastDrawTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
            return true;
        }
        return sourcePlayers.TryGetValue(source, out var association) && association.Player is not null
            && association.Player.TryGetTarget(out var player) && ReferenceEquals(playerSourceField.GetValue(player), source)
            && TryGetPreviewViewportForPlayer(player, source, out viewport);
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
                association.Player = Weak(association.Player, __instance);
                latest.SceneId = scene.ID;
                latest.TimelineId = scene.Timeline.ID;
                latest.Scenes = scene.Scenes;
                latest.Viewport = viewport;
                latest.Player = Weak(latest.Player, __instance);
                latest.Source = Weak(latest.Source, source);
            }
        }
        catch { }
    }

    // Every Draw names the same player and source: a weak reference (a GC handle with a finalizer) only for a new one.
    private static WeakReference<object> Weak(WeakReference<object>? current, object target) =>
        current is not null && current.TryGetTarget(out var known) && ReferenceEquals(known, target) ? current : new(target);

    private static Exception? DrawFinalizer(Exception? __exception, DrawMeasurement __state)
    {
        ObserveDeviceLoss(__state.Update?.Pending?.State, __exception);
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
                pending.State.RecentUpdateTicks = ticks;
                if (update.RunsHost) pending.State.Economics.ObserveRender(key, ticks);
                else if (ReferenceEquals(pending.Path, RamTimes))
                    pending.State.Economics.ObserveRestore(view.FrameBytes + PreviewRecordHeader, ticks);
                else if (ReferenceEquals(pending.Path, GpuTimes))
                    pending.State.Economics.ObserveRestore(view.FrameBytes + PreviewRecordHeader, ticks, gpu: true);
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
        // The player draws the output at the scene's center under the view's zoom and pan (4.55 and later), or under
        // the context's transform alone (4.54 and older: Draw sets no transform).
        var transform = context.Transform;
        if (previewTransform is not null)
        {
            var visible = (Vector2)visibleVideoSize!.Invoke(player, [previewZoom!.GetValue(player)])!;
            transform = (Matrix3x2)previewTransform.Invoke(null,
                [visible, previewCenter!.GetValue(player)!, (float)scene.Width, (float)scene.Height])! * transform;
        }
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
}
