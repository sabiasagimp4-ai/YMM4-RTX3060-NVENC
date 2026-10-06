using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Random moves end to end on the real host: the idle pre-renderer renders frames of a random move (the animation type
// and the effect, both seeded by object identities) through its own per-frame code (PrimeBatchFrame), and the paused
// player then shows every one of them from the cache with the pixels it renders itself. Also shows why the clone cannot
// render them: a copy of the project draws other random values.
internal static class IdleRandomChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Frames = 30, Width = 321, Height = 181;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        string root = Path.Combine(Path.GetTempPath(), "ymm-idle-random-" + Guid.NewGuid().ToString("N"));
        var store = new FrameCacheStore(root, 64L << 20, 0);
        TimelineFrameCache.UseStore(store);
        var dc = context.DeviceContext;
        var timeline = RandomTimeline();
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        var player = Create(host, context, scene);
        ITimelineSource? second = null, copy = null;
        var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, scene.ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        TimelineFrameCache.TestViewport = value => ReferenceEquals(value, player) ? viewport : null;
        try
        {
            // The host's own renders, cache off.
            TimelineFrameCache.Enabled = false;
            var baseline = Render(player, timeline, dc, viewport);
            int moving = Enumerable.Range(1, Frames - 1).Count(frame => !baseline[frame].SequenceEqual(baseline[frame - 1]));
            Check(moving >= Frames / 2, $"The random move hardly moves ({moving} of {Frames - 1} frames differ from the previous one)");
            Check(Same(Render(player, timeline, dc, viewport), baseline) == Frames, "The same objects drew other random values on a second pass");
            // The idle pre-renderer's live renderer is another source over the same scene: it must draw the same values.
            second = Create(host, context, scene);
            int sameSecond = Same(Render(second, timeline, dc, viewport), baseline);
            if (!HostFeatures.For(host).IdentityRandom)
            {
                // Before 4.52.0.2 the random effect seeds with its renderer's own object: the frames are never stored.
                Check(sameSecond < Frames, "Another renderer drew the same random values on a build marked as seeding with its renderer");
                TimelineFrameCache.Enabled = true;
                TimelineFrameCache.Clear();
                long storedHits = TimelineFrameCache.Hits;
                Render(player, timeline, dc, viewport);
                var shown = Render(player, timeline, dc, viewport);
                Check(TimelineFrameCache.Hits == storedHits && Same(shown, baseline) == Frames,
                    "Frames with renderer-seeded randomness were shown from the cache");
                Console.WriteLine($"Random move: another renderer differs in {Frames - sameSecond} of {Frames} frames on this build; its frames render normally");
                return;
            }
            Check(sameSecond == Frames, $"Another renderer of the same scene drew other random values ({Frames - sameSecond} of {Frames} frames differ)");
            var copyTimeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
            var copyScenes = HostCompat.NewScenes(); copyScenes.AddScene(copyTimeline);
            copy = Create(host, context, new Scene(copyTimeline, copyScenes, []));
            int sameCopy = Same(Render(copy, copyTimeline, dc, viewport), baseline);
            Check(sameCopy < Frames, "A copy of the project drew the same random values (the clone could have rendered them)");
            Console.WriteLine($"Random move: {moving} of {Frames - 1} frames move; a second renderer of the scene draws the same pixels, "
                + $"a copy of the project differs in {Frames - sameCopy} of {Frames} frames");

            // The idle pre-renderer's batch, frame by frame through its own code, then the paused player.
            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            using var liveTracker = new KeyDependencyTracker(scene);
            Check(SpinWait.SpinUntil(() =>
            {
                if (liveTracker.TryCapture(0, out var capture, out _)) capture!.Dispose();
                return Enumerable.Range(0, Frames).All(liveTracker.IsSessionKeyed);
            }, TimeSpan.FromSeconds(20)), "The random move's frames were not keyed by their objects");
            Check(liveTracker.TryCapture(0, out var initial, out var reason), reason);
            IdleFramePreRenderer.BatchRenderer batch;
            using (initial) batch = new IdleFramePreRenderer.BatchRenderer(liveTracker, initial!.Model);
            var idleView = viewport with { IsPlaying = false, LastDrawTimestamp = Stopwatch.GetTimestamp() };
            string first, again;
            try
            {
                first = Batch(liveTracker, scene, batch, idleView, out var rendered);
                Check(rendered == Frames, $"The idle batch did not render every random frame: {first}");
                again = Batch(liveTracker, scene, batch, idleView, out _);
                Check(again == $"Stored={Frames}", $"The idle batch rendered stored random frames again: {again}");
                Check(batch.LiveSource is not null, "The idle batch did not use a renderer of the live scene");
            }
            finally { batch.Dispose(); }

            long misses = TimelineFrameCache.Misses, ramHits = TimelineFrameCache.RamHits, diskHits = TimelineFrameCache.DiskHits;
            int equal = 0;
            for (int frame = 0; frame < Frames; frame++)
            {
                player.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
                if (TimelineFrameCache.CapturePreview(dc, player.Output, viewport)!.SequenceEqual(baseline[frame])) equal++;
            }
            misses = TimelineFrameCache.Misses - misses;
            long hits = TimelineFrameCache.RamHits - ramHits + TimelineFrameCache.DiskHits - diskHits;
            Check(misses == 0 && hits == Frames, $"The paused player did not show the idle frames from the cache: {misses} renders, {hits} cache hits");
            Check(equal == Frames, $"Idle frames shown from the cache differ from the host's own render ({Frames - equal} of {Frames})");
            Console.WriteLine($"Idle random move: batch {first}, again {again}; the paused player showed all {Frames} from the cache "
                + "with the host's own pixels and rendered none");
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            player.Dispose();
            second?.Dispose();
            copy?.Dispose();
            TimelineFrameCache.Enabled = false;
            store.Dispose();
            TimelineFrameCache.UseStore(new FrameCacheStore(root + "-after", 256L << 20, 0));
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            GC.KeepAlive(timeline);
        }
    }

    // A still shape, one moving at random (the X animation) and one shaken by the random move effect.
    private static Timeline RandomTimeline()
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var still = new ShapeItem { Frame = 0, Length = Frames, Layer = 0 };
        still.Y.SetFirst(60);
        var shaking = new ShapeItem { Frame = 0, Length = Frames, Layer = 1 };
        shaking.X.AnimationType = AnimationType.ランダム移動;
        var shaken = new ShapeItem { Frame = 0, Length = Frames, Layer = 2 };
        shaken.Y.SetFirst(-50);
        var randomMove = (YukkuriMovieMaker.Plugin.Effects.IVideoEffect)Activator.CreateInstance(
            typeof(Scene).Assembly.GetType("YukkuriMovieMaker.Project.Effects.RandomMoveEffect", true)!, nonPublic: true)!;
        shaken.VideoEffects = shaken.VideoEffects.Add(randomMove);
        timeline.Items = timeline.Items.Add(still).Add(shaking).Add(shaken);
        timeline.RefreshTimelineLengthAndMaxLayer();
        // The random move's range is between its first two values: -120 to 120 (set in the saved form, as YMM4 loads it).
        var json = JsonNode.Parse(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
        var x = json["Items"]?[1]?["X"];
        if (x?["Values"] is JsonArray { Count: > 0 } values)
        {
            var low = values[0]!.DeepClone();
            var high = values[0]!.DeepClone();
            low["Value"] = -120.0;
            high["Value"] = 120.0;
            values.Clear();
            values.Add(low);
            values.Add(high);
            timeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(json.ToJsonString())!;
            timeline.RefreshTimelineLengthAndMaxLayer();
        }
        else Console.WriteLine("Random move: the X animation's saved form was not as expected, kept as created: " + x?.ToJsonString());
        return timeline;
    }

    private static string Batch(KeyDependencyTracker liveTracker, Scene scene, IdleFramePreRenderer.BatchRenderer batch,
        TimelineFrameCache.PreviewViewport view, out int rendered)
    {
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int frame = 0; frame < Frames; frame++)
        {
            var result = IdleFramePreRenderer.PrimeBatchFrame(liveTracker, scene, batch, frame, view, () => true, CancellationToken.None, out _);
            outcomes[result.ToString()] = outcomes.GetValueOrDefault(result.ToString()) + 1;
        }
        rendered = outcomes.GetValueOrDefault(nameof(IdleFramePreRenderer.IdleFrameResult.Rendered));
        return string.Join(",", outcomes.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static byte[][] Render(ITimelineSource source, Timeline timeline, Vortice.Direct2D1.ID2D1DeviceContext dc,
        TimelineFrameCache.PreviewViewport viewport)
    {
        var frames = new byte[Frames][];
        for (int frame = 0; frame < Frames; frame++)
        {
            source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
            frames[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
        }
        return frames;
    }

    private static int Same(byte[][] a, byte[][] b) => Enumerable.Range(0, Frames).Count(frame => a[frame].SequenceEqual(b[frame]));

    private static ITimelineSource Create(Assembly host, IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
