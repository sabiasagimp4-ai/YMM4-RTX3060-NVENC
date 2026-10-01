using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Item rects (selection, hover and drag UI) on cached preview frames, against the real host TimelineSource.
// Expects TimelineFrameCache to be installed. The player is a stand-in carrying only the fields the cache reads.
internal static class PreviewRectChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        Check(Flag("rectsSupported") && Flag("refreshSupported"),
            "Rect reuse or the paused refresh hook is unavailable on this host (TimelineItemRects / Edit / isTimelineChanged changed?)");
        CheckKeepRestoreAndEdit(host, context);
        CheckShowOnlyPreviewKeepsUsagesApart(host, context);
        CheckDeferredRectsAreRefreshed(host, context);
        CheckPreviewResidency(host, context);
        Console.WriteLine("Preview item rects: kept/restored on cached paused and playing frames, dropped on edit, ShowOnlyPreviewEffect separates usages, deferred paused rects re-rendered once, cache bar residency OK");
    }

    private static void CheckKeepRestoreAndEdit(Assembly host, IGraphicsDevicesAndContext context)
    {
        var (timeline, shape, source) = Create(host, context, null);
        using (source)
        {
            TimelineFrameCache.Clear();
            var rects = Rects(source);
            long hits = TimelineFrameCache.Hits;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits && rects.Count == 1 && ReferenceEquals(Item(rects[0]), shape),
                "First paused frame with rects was not rendered by the host");
            var hostRect = rects[0]!;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 1 && rects.Count == 1 && rects[0]!.Equals(hostRect),
                "A repeated paused update (hover/selection change) did not keep the frame and its rects: " + TimelineFrameCache.Status);
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Playing, needRects: false);
            Check(TimelineFrameCache.Hits == hits + 2 && rects.Count == 0,
                "Playback without rects did not reuse the paused frame (shared preview key) or kept rects the host would clear");
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 3 && rects.Count == 1 && SameRect(rects[0]!, hostRect),
                "Remembered rects were not restored on a cached paused frame: " + TimelineFrameCache.Status);
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Playing, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 4 && rects.Count == 1 && SameRect(rects[0]!, hostRect),
                "Playback with the pointer over the preview lost the rects of a cached frame");

            shape.X.SetFirstValue(40);
            Thread.Sleep(300); // the render path waits for edits to settle
            hits = TimelineFrameCache.Hits;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits && rects.Count == 1 && !SameRect(rects[0]!, hostRect),
                "An edited frame reused stale pixels or rects");
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 1 && rects.Count == 1, "The re-rendered frame was not reused with its rects");
            GC.KeepAlive(timeline);
        }
    }

    private static void CheckShowOnlyPreviewKeepsUsagesApart(Assembly host, IGraphicsDevicesAndContext context)
    {
        var effect = (IVideoEffect)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Effects.ShowOnlyPreviewEffect", true)!, true)!;
        effect.GetType().GetProperty("IsPlaying")!.SetValue(effect, false);
        effect.GetType().GetProperty("IsPaused")!.SetValue(effect, true);
        var (timeline, _, source) = Create(host, context, effect);
        using (source)
        {
            TimelineFrameCache.Clear();
            var dc = context.DeviceContext;
            long hits = TimelineFrameCache.Hits;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: false);
            var pausedPixels = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Playing, needRects: false);
            var playingPixels = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
            Check(TimelineFrameCache.Hits == hits, "A paused frame was reused for playback although ShowOnlyPreviewEffect differs");
            Check(!pausedPixels.SequenceEqual(playingPixels), "ShowOnlyPreviewEffect did not change the playback frame (test premise)");
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Playing, needRects: false);
            Check(TimelineFrameCache.Hits == hits + 1, "Playback frames were not reused within the same usage");
            GC.KeepAlive(timeline);
        }
    }

    private static void CheckDeferredRectsAreRefreshed(Assembly host, IGraphicsDevicesAndContext context)
    {
        var (timeline, shape, source) = Create(host, context, null);
        using (source)
        {
            TimelineFrameCache.Clear();
            var playerType = host.GetType("YukkuriMovieMaker.Player.TimelineVideoPlayer", true)!;
            var player = RuntimeHelpers.GetUninitializedObject(playerType);
            playerType.GetField("timelineVideo", Instance)!.SetValue(player, source);
            var pointerOver = playerType.GetField("isMouseOverPreviewArea", Instance)!;
            var changed = playerType.GetField("isTimelineChanged", Instance)!;
            pointerOver.SetValue(player, false);
            var table = typeof(TimelineFrameCache).GetField("sourcePlayers", Static)!.GetValue(null)!;
            var association = table.GetType().GetMethod("GetOrCreateValue")!.Invoke(table, [source])!;
            association.GetType().GetField("Player", Instance)!.SetValue(association, new WeakReference<object>(player));
            var beforeEdit = typeof(TimelineFrameCache).GetMethod("BeforeEdit", Static)!;
            bool RefreshRequested()
            {
                changed.SetValue(player, false);
                beforeEdit.Invoke(null, [player]);
                return (bool)changed.GetValue(player)!;
            }
            var rects = Rects(source);

            // Scrubbing with the pointer away from the preview: the cached frame is shown at once, its rects follow.
            long hits = TimelineFrameCache.Hits;
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: false);
            var shown = System.Diagnostics.Stopwatch.StartNew();
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 1 && rects.Count == 0, "A cached paused frame without known rects was not shown at once");
            bool early = RefreshRequested();
            if (shown.ElapsedMilliseconds < 80) Check(!early, "Refresh was requested while the playhead may still be moving");
            Thread.Sleep(150);
            Check(early || RefreshRequested(), "No refresh was requested after the playhead rested on a frame without rects");
            Check(!RefreshRequested(), "Refresh must be requested once per frame");
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 1 && rects.Count == 1 && ReferenceEquals(Item(rects[0]), shape),
                "The refresh did not re-render the frame with host rects");
            Update(source, TimeSpan.Zero, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits + 2 && rects.Count == 1, "The refreshed frame was not reused with its rects");

            // Pointer over the preview: rects are needed for clicks right away, so unknown rects mean a normal render.
            var later = timeline.VideoInfo.GetTimeFrom(3);
            Update(source, later, TimelineSourceUsage.Paused, needRects: false);
            pointerOver.SetValue(player, true);
            hits = TimelineFrameCache.Hits;
            Update(source, later, TimelineSourceUsage.Paused, needRects: true);
            Check(TimelineFrameCache.Hits == hits && rects.Count == 1, "A frame without known rects was reused under the pointer");

            // Playback with the pointer over the preview never shows a frame without its rects.
            var playback = timeline.VideoInfo.GetTimeFrom(6);
            Update(source, playback, TimelineSourceUsage.Playing, needRects: false);
            Update(source, playback, TimelineSourceUsage.Playing, needRects: true);
            Check(TimelineFrameCache.Hits == hits && rects.Count == 1, "Playback reused a frame without rects under the pointer");
            Check(!RefreshRequested(), "Nothing is missing, yet a refresh was requested");
        }
    }

    // What the cache bars read: a primed preview frame is reported in RAM, its neighbours are not stored.
    private static void CheckPreviewResidency(Assembly host, IGraphicsDevicesAndContext context)
    {
        var (timeline, _, source) = Create(host, context, null);
        using (source)
        {
            TimelineFrameCache.Clear();
            var time = timeline.VideoInfo.GetTimeFrom(3);
            source.Update(time, TimelineSourceUsage.Playing);
            var viewport = new TimelineFrameCache.PreviewViewport(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                context.DeviceContext.AntialiasMode, context.DeviceContext.TextAntialiasMode, context.DeviceContext.PrimitiveBlend,
                context.DeviceContext.UnitMode, ((Scene)source.GetType().GetField("scene", Instance)!.GetValue(source)!).ID, timeline.ID,
                System.Diagnostics.Stopwatch.GetTimestamp(), false);
            Check(TimelineFrameCache.TryPrimePreview(source, time, TimelineSourceUsage.Playing, viewport), "Could not prime a preview frame: " + TimelineFrameCache.Status);
            var residency = new byte[3];
            Check(TimelineFrameCache.TryGetPreviewResidency(source, viewport, [3, 4, 2], residency), "Residency was not available");
            Check(residency.SequenceEqual(new byte[] { 2, 0, 0 }), $"Unexpected residency {string.Join(",", residency)}");
            Check(TimelineFrameCache.TryGetPreviewResidency(source, viewport with { Width = 320 }, [3], residency.AsSpan(0, 1)) && residency[0] == 0,
                "Another view must not report the primed frame");
            TimelineFrameCache.Clear();
            Check(TimelineFrameCache.TryGetPreviewResidency(source, viewport, [3], residency.AsSpan(0, 1)) && residency[0] == 0,
                "Clear must empty the bars");
        }
    }

    private static (Timeline Timeline, ShapeItem Shape, ITimelineSource Source) Create(Assembly host, IGraphicsDevicesAndContext context, IVideoEffect? effect)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 321; timeline.VideoInfo.Height = 181; timeline.VideoInfo.FPS = 30;
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        var shape = new ShapeItem { Frame = 0, Length = 100 };
        shape.X.SetFirstValue(-12.25); shape.Y.SetFirstValue(8.75);
        if (effect is not null) shape.VideoEffects = shape.VideoEffects.Add(effect);
        timeline.Items = timeline.Items.Add(shape);
        var scene = new Scene(timeline, scenes, []);
        var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;
        return (timeline, shape, source);
    }

    private static void Update(ITimelineSource source, TimeSpan time, TimelineSourceUsage usage, bool needRects)
    {
        source.GetType().GetProperty("NeedTimelineItemRects")!.SetValue(source, needRects);
        source.Update(time, usage);
    }

    private static IList Rects(ITimelineSource source) => (IList)source.GetType().GetProperty("TimelineItemRects")!.GetValue(source)!;

    private static object? Item(object? rect) => ((ITuple)rect!)[0];

    private static bool SameRect(object left, object right)
    {
        var a = (ITuple)left;
        var b = (ITuple)right;
        return ReferenceEquals(a[0], b[0]) && Equals(a[1], b[1]) && ((Vector2[])a[2]!).SequenceEqual((Vector2[])b[2]!)
            && Equals(a[3], b[3]) && ((IEnumerable<VideoController>)a[4]!).SequenceEqual((IEnumerable<VideoController>)b[4]!);
    }

    private static bool Flag(string name) => (bool)typeof(TimelineFrameCache).GetField(name, Static)!.GetValue(null)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
