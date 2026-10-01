using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Normal preview playback (no idle pre-rendering) stores its frames; a second pass whose frames no longer fit in RAM,
// and a restart, get them back from disk instead of rendering them again. Every frame shown from the cache is
// compared with the host's own render. Real host TimelineSource; the player's view is a stand-in
// (TimelineFrameCache.TestViewport) and playback is paced at the project's frame rate.
internal static class PreviewDeliveryChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Frames = 30, Width = 321, Height = 181;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context, bool rects)
    {
        string root = Path.Combine(Path.GetTempPath(), "ymm-preview-delivery-" + Guid.NewGuid().ToString("N"));
        // RAM for 8 preview frames: most of the 30 have to come from disk on the second pass.
        long frameBytes = (long)Width * Height * 4 + 32;
        long ram = 8 * frameBytes;
        var store = new FrameCacheStore(root, ram, 64L << 20);
        TimelineFrameCache.UseStore(store);
        var dc = context.DeviceContext;
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        // One item per frame: every frame has other pixels and its own per-frame key.
        var shapes = Enumerable.Range(0, Frames).Select(frame =>
        {
            var shape = new ShapeItem { Frame = frame, Length = 1 };
            shape.X.SetFirstValue(-120 + frame * 8.25); shape.Y.SetFirstValue(frame % 7 * 5 - 15);
            return shape;
        }).ToArray();
        timeline.Items = timeline.Items.AddRange(shapes);
        var scene = new Scene(timeline, scenes, []);
        var source = Create(host, context, scene);
        var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            ((Scene)source.GetType().GetField("scene", Instance)!.GetValue(source)!).ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        TimelineFrameCache.TestViewport = value => ReferenceEquals(value, source) ? viewport : null;
        try
        {
            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            var baseline = new byte[Frames][];

            // Pass 1, cold: every frame is rendered by the host and stored after it is shown.
            var before = Counters.Read(store);
            var renderClock = Stopwatch.StartNew();
            for (int frame = 0; frame < Frames; frame++)
            {
                Update(source, timeline, frame, TimelineSourceUsage.Playing);
                baseline[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
            }
            renderClock.Stop();
            TimelineFrameCache.CompletePendingStore(source);
            var cold = Counters.Read(store) - before;
            Check(cold.Renders == Frames && cold.Stored == Frames, $"cold playback did not render and store every frame: {cold}");
            WaitFor(() => store.DiskWrites >= Frames, "stored preview frames reach the disk");
            var residency = new byte[Frames];
            Check(TimelineFrameCache.TryGetPreviewResidency(source, viewport, Enumerable.Range(0, Frames).ToArray(), residency), "residency unavailable");
            int inRam = residency.Count(value => value == 2), onDisk = residency.Count(value => value == 1);
            Check(inRam <= 8 && inRam + onDisk == Frames, $"after pass 1: {inRam} frames in RAM, {onDisk} on disk only");
            Console.WriteLine($"Preview pass 1 (cold playback, idle pre-render off): {cold}; {inRam} in RAM, {onDisk} on disk only; "
                + $"{renderClock.Elapsed.TotalMilliseconds / Frames:F2} ms/frame including the host render and the test's own capture");

            // Pass 2, playback paced at 30 fps: disk-only frames are read ahead of the playhead.
            var second = Play(source, timeline, viewport, dc, baseline, store, 0, Frames);
            Check(second.Renders <= Frames / 3 && second.DiskHits >= Frames / 2,
                $"second playback did not get its frames from disk: {second}");
            Console.WriteLine($"Preview pass 2 (RAM holds 8 of {Frames}): {second}; disk read {store.DiskReadMilliseconds:F2} ms/frame on the worker");

            // A paused seek to a frame stored on disk only waits for the read instead of rendering.
            if (rects) CheckPausedDiskFrameWithRects(source, timeline, viewport, dc, baseline, store, shapes);

            // Edit one item: only its frame changes key; undoing the edit finds the stored frame again.
            CheckEditAndUndo(source, timeline, viewport, dc, baseline, store, shapes[12]);

            // Restart: a new store over the same folder and a new source (new tracker) find the frames on disk.
            TimelineFrameCache.TestViewport = null;
            source.Dispose();
            store.Dispose();
            store = new FrameCacheStore(root, ram, 64L << 20);
            TimelineFrameCache.UseStore(store);
            source = Create(host, context, scene);
            var restarted = source;
            TimelineFrameCache.TestViewport = value => ReferenceEquals(value, restarted) ? viewport : null;
            WaitFor(() => store.DiskBytes > 0, "the restarted store loads its index");
            var paused = Counters.Read(store);
            Update(source, timeline, 10, TimelineSourceUsage.Paused);
            var seek = Counters.Read(store) - paused;
            Check(seek.Renders == 0 && seek.DiskHits == 1, $"a paused seek after restart rendered instead of reading the disk: {seek}");
            Check(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[10]), "restart disk frame pixels differ");
            var third = Play(source, timeline, viewport, dc, baseline, store, 11, Frames);
            Check(third.Renders <= Frames / 3 && third.DiskHits >= (Frames - 11) / 2, $"playback after restart did not use the disk: {third}");
            Console.WriteLine($"Preview after restart: paused seek {seek}; playback 11-{Frames - 1} {third}");
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            source.Dispose();
            TimelineFrameCache.Enabled = false;
            store.Dispose();
            TimelineFrameCache.UseStore(new FrameCacheStore(root + "-after", 256L << 20, 0));
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            GC.KeepAlive(timeline);
        }
        Check(TimelineFrameCache.GpuBytes == 0, "preview delivery checks leaked GPU reservations");
        Console.WriteLine($"Update time p50/p95 by path (WARP, this test's frames): render {TimelineFrameCache.RenderTimes}, RAM {TimelineFrameCache.RamTimes}, "
            + $"disk {TimelineFrameCache.DiskTimes}, same frame {TimelineFrameCache.LiveTimes}; preview store {TimelineFrameCache.PreviewStoreMilliseconds:F2} ms/frame on the render thread");
        Console.WriteLine("Preview storage and disk delivery: stored on normal playback, read back from disk on the second pass and after restart, pixels equal to host renders, edit/undo reuse OK");
    }

    private static Counters Play(ITimelineSource source, Timeline timeline, TimelineFrameCache.PreviewViewport viewport,
        Vortice.Direct2D1.ID2D1DeviceContext dc, byte[][] baseline, FrameCacheStore store, int from, int to)
    {
        var before = Counters.Read(store);
        var clock = Stopwatch.StartNew();
        for (int frame = from; frame < to; frame++)
        {
            Update(source, timeline, frame, TimelineSourceUsage.Playing);
            Check(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[frame]),
                $"frame {frame} differs from the host render ({TimelineFrameCache.Status})");
            var next = TimeSpan.FromSeconds((frame - from + 1) / 30.0);
            if (clock.Elapsed < next) Thread.Sleep(next - clock.Elapsed);
        }
        TimelineFrameCache.CompletePendingStore(source);
        return Counters.Read(store) - before;
    }

    private static void CheckPausedDiskFrameWithRects(ITimelineSource source, Timeline timeline, TimelineFrameCache.PreviewViewport viewport,
        Vortice.Direct2D1.ID2D1DeviceContext dc, byte[][] baseline, FrameCacheStore store, ShapeItem[] shapes)
    {
        var list = (IList)source.GetType().GetProperty("TimelineItemRects")!.GetValue(source)!;
        // The host renders frame 3 with its rects (remembered), then playback pushes it out of RAM.
        Update(source, timeline, 3, TimelineSourceUsage.Paused, needRects: true);
        TimelineFrameCache.CompletePendingStore(source);
        Check(list.Count == 1 && ReferenceEquals(((ITuple)list[0]!)[0], shapes[3]), "host rects for frame 3");
        Play(source, timeline, viewport, dc, baseline, store, 10, Frames);
        var residency = new byte[1];
        Check(TimelineFrameCache.TryGetPreviewResidency(source, viewport, [3], residency) && residency[0] == 1, "frame 3 should be on disk only");
        var before = Counters.Read(store);
        Update(source, timeline, 3, TimelineSourceUsage.Paused, needRects: true);
        var seek = Counters.Read(store) - before;
        Check(seek.Renders == 0 && seek.DiskHits == 1, $"paused seek to a disk-only frame: {seek} ({TimelineFrameCache.Status})");
        Check(list.Count == 1 && ReferenceEquals(((ITuple)list[0]!)[0], shapes[3]), "rects were not restored on the disk-delivered frame");
        Check(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[3]), "disk-delivered paused frame pixels differ");
        Console.WriteLine($"Paused seek to a disk-only frame: {seek}, item rects restored");
    }

    private static void CheckEditAndUndo(ITimelineSource source, Timeline timeline, TimelineFrameCache.PreviewViewport viewport,
        Vortice.Direct2D1.ID2D1DeviceContext dc, byte[][] baseline, FrameCacheStore store, ShapeItem edited)
    {
        double x = -120 + edited.Frame * 8.25; // as created in Run
        edited.X.SetFirstValue(x + 40);
        Thread.Sleep(300); // the render path waits for edits to settle
        var before = Counters.Read(store);
        Update(source, timeline, edited.Frame, TimelineSourceUsage.Paused);
        Check((Counters.Read(store) - before).Renders == 1, "the edited frame was not rendered again");
        Check(!TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[edited.Frame]), "the edit did not change the frame (test premise)");
        before = Counters.Read(store);
        Update(source, timeline, 20, TimelineSourceUsage.Paused);
        var other = Counters.Read(store) - before;
        Check(other.Renders == 0, $"an edit of frame {edited.Frame} re-rendered frame 20: {other}");
        Check(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[20]), "frame 20 pixels after an unrelated edit");
        edited.X.SetFirstValue(x);
        Thread.Sleep(300);
        before = Counters.Read(store);
        Update(source, timeline, edited.Frame, TimelineSourceUsage.Paused);
        var undo = Counters.Read(store) - before;
        Check(undo.Renders == 0, $"undoing the edit did not reuse the stored frame: {undo}");
        Check(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!.SequenceEqual(baseline[edited.Frame]), "undone frame pixels differ");
        TimelineFrameCache.CompletePendingStore(source);
        Console.WriteLine($"Edit of one item: unrelated frame {other}; undo {undo}");
    }

    private static ITimelineSource Create(Assembly host, IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;

    private static void Update(ITimelineSource source, Timeline timeline, int frame, TimelineSourceUsage usage, bool needRects = false)
    {
        source.GetType().GetProperty("NeedTimelineItemRects")!.SetValue(source, needRects);
        source.Update(timeline.VideoInfo.GetTimeFrom(frame), usage);
    }

    private readonly record struct Counters(long Renders, long RamHits, long DiskHits, long LiveReuses, long Stored, long ReadAheads)
    {
        internal static Counters Read(FrameCacheStore store) => new(TimelineFrameCache.Misses, TimelineFrameCache.RamHits,
            TimelineFrameCache.DiskHits, TimelineFrameCache.LiveReuses, TimelineFrameCache.PreviewStored, TimelineFrameCache.ReadAheads);
        public static Counters operator -(Counters a, Counters b) => new(a.Renders - b.Renders, a.RamHits - b.RamHits,
            a.DiskHits - b.DiskHits, a.LiveReuses - b.LiveReuses, a.Stored - b.Stored, a.ReadAheads - b.ReadAheads);
        public override string ToString() =>
            $"host renders {Renders}, RAM hits {RamHits}, disk deliveries {DiskHits}, same-frame reuse {LiveReuses}, stored {Stored}, read-ahead {ReadAheads}";
    }

    private static void WaitFor(Func<bool> condition, string message) => Check(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(20)), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
