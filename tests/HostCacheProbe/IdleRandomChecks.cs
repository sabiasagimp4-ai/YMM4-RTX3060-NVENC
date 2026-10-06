using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

// Random moves end to end on the real host: the idle pre-renderer renders frames of a random move (the animation type
// and the effect, both seeded by object identities) through its own per-frame code (PrimeBatchFrame), and the paused
// player then shows every one of them from the cache with the pixels it renders itself. Also shows why the clone cannot
// render them: a copy of the project draws other random values. Before that, each kind of identity-seeded randomness
// alone: another renderer of the same scene draws the same values (with RandomSeedAlignment where YMM4 seeds with the
// renderer), a copy of the project draws others, and the frames are keyed by those objects.
internal static class IdleRandomChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Frames = 30, Width = 321, Height = 181;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        Console.WriteLine("Random seeds: " + string.Join("; ", RandomSeedAlignment.Coverage));
        // YMM4's shader effects (the mosaics) read their shaders from pack://application: resources, which WPF's
        // Application type registers when it is initialized (no Application object is created here).
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
        foreach (var (name, item, rendererSeeded) in Kinds(host)) CheckKind(host, context, name, item, rendererSeeded);
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
            if (!(HostFeatures.For(host).IdentityRandom && RandomSeedAlignment.EffectsByModel(host)))
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

            // The idle pre-renderer's batch, frame by frame through its own code, then the paused player (YMM4 4.49 and
            // later: the pre-renderer needs Scenes(bool) to clone the project).
            if (!HostCompat.ScenesTakeUndoFlag)
            {
                Console.WriteLine("Idle random move skipped: this YMM4 has no idle pre-render");
                return;
            }
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

    // Each kind of randomness YMM4 seeds with an object's identity, on its own item; rendererSeeded: YMM4 seeds it with
    // the renderer's own object (RandomSeedAlignment seeds it with the model instead).
    private static IEnumerable<(string Name, IItem Item, bool RendererSeeded)> Kinds(Assembly host)
    {
        bool oldEffects = host.GetType(RandomSeedAlignment.EffectBaseName) is { } generic
            && generic.GetMethod("GetRandomValue", Instance | BindingFlags.DeclaredOnly) is { } method
            && HarmonyLib.PatchProcessor.GetOriginalInstructions(method).Zip(HarmonyLib.PatchProcessor.GetOriginalInstructions(method).Skip(1))
                .Any(pair => pair.First.opcode == System.Reflection.Emit.OpCodes.Ldarg_0 && pair.Second.operand is MethodInfo { Name: "GetHashCode" });
        // The random rotation with no span: a new value every frame (MersenneTwister seeded by the hash).
        var rotate = (YukkuriMovieMaker.Plugin.Effects.IVideoEffect)Activator.CreateInstance(
            host.GetType("YukkuriMovieMaker.Project.Effects.RandomRotateEffect", true)!, nonPublic: true)!;
        ((Animation)rotate.GetType().GetProperty("Span")!.GetValue(rotate)!).SetFirst(0);
        var rotating = new ShapeItem { Frame = 0, Length = Frames, Layer = 0 };
        rotating.VideoEffects = rotating.VideoEffects.Add(rotate);
        yield return ("random rotation", rotating, oldEffects);
        yield return ("random text order", new TextItem { Frame = 0, Length = Frames, Layer = 0, Text = "ABCDEFGHIJKL", Font = "Arial",
            DisplayInterval = 50, DisplayDirection = TypewriterAnimationDirection.Random }, true);
        if (!ShadersReadable(out string problem))
        {
            Console.WriteLine("Mosaic checks skipped: YMM4's shader resources cannot be read in this process: " + problem);
            yield break;
        }
        foreach (string type in new[] { "Delaunay", "Voronoi" })
        {
            var mosaic = new YukkuriMovieMaker.Project.Effects.MosaicEffect { MosaicType = Enum.Parse<YukkuriMovieMaker.Project.Effects.MosaicType>(type) };
            mosaic.MosaicParameter = (YukkuriMovieMaker.Project.Effects.MosaicParameters.MosaicParameterBase)Activator.CreateInstance(
                host.GetType($"YukkuriMovieMaker.Project.Effects.MosaicParameters.{type}MosaicParameter", true)!, nonPublic: true)!;
            var shape = new ShapeItem { Frame = 0, Length = Frames, Layer = 0 };
            shape.VideoEffects = shape.VideoEffects.Add(mosaic);
            yield return ($"{type} mosaic", shape, false);
        }
    }

    private static bool ShadersReadable(out string problem)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/YukkuriMovieMaker;component/Resources/Shader/DelaunayMosaic.cso");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            problem = stream is null ? "no resource stream" : string.Empty;
            return stream is not null;
        }
        catch (Exception error) when (error is UriFormatException or IOException or InvalidOperationException or NotSupportedException)
        {
            problem = error.GetBaseException().Message;
            return false;
        }
    }

    private static void CheckKind(Assembly host, IGraphicsDevicesAndContext context, string name, IItem item, bool rendererSeeded)
    {
        var dc = context.DeviceContext;
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        timeline.Items = timeline.Items.Add(item);
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, scene.ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        var copyTimeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
        var copyScenes = HostCompat.NewScenes(); copyScenes.AddScene(copyTimeline);
        bool enabled = TimelineFrameCache.Enabled;
        TimelineFrameCache.Enabled = false;
        var renderers = new List<ITimelineSource>();
        try
        {
            ITimelineSource New(Scene s) { var source = Create(host, context, s); renderers.Add(source); return source; }
            // A first render of the effect warms the device up (its first frame can differ by a pixel from later ones).
            Render(New(new Scene(copyTimeline, copyScenes, [])), copyTimeline, dc, viewport);
            var baseline = Render(New(scene), timeline, dc, viewport);
            int moving = Enumerable.Range(1, Frames - 1).Count(frame => !baseline[frame].SequenceEqual(baseline[frame - 1]));
            var second = Render(New(scene), timeline, dc, viewport);
            int sameSecond = Same(second, baseline);
            int sameCopy = Same(Render(New(new Scene(copyTimeline, copyScenes, [])), copyTimeline, dc, viewport), baseline);
            // A third renderer after the others ran (their code recompiled by .NET by then): still the same values.
            var third = Render(New(scene), timeline, dc, viewport);
            int sameThird = Same(third, baseline);
            string Differing(byte[][] frames) => string.Join(",", Enumerable.Range(0, Frames).Where(frame => !frames[frame].SequenceEqual(baseline[frame])));
            bool identity = HostFeatures.For(host).IdentityRandom && RandomSeedAlignment.EffectsByModel(host);
            bool keyed = identity && (!rendererSeeded || item is not TextItem || RandomSeedAlignment.TextOrderByItem);
            using var tracker = new KeyDependencyTracker(scene);
            bool sessionKeyed = SpinWait.SpinUntil(() =>
            {
                if (tracker.TryCapture(0, out var capture, out _)) capture!.Dispose();
                return Enumerable.Range(0, Frames).All(tracker.IsSessionKeyed);
            }, TimeSpan.FromSeconds(keyed ? 20 : 2));
            string summary = $"{name}: {moving} of {Frames - 1} frames change, another renderer draws {Frames - sameSecond} other frames "
                + $"(a third {Frames - sameThird}), a copy of the project {Frames - sameCopy}"
                + (sameSecond == Frames && sameThird == Frames ? "" : $" [other frames: second {Differing(second)}; third {Differing(third)}; second and third differ in {Frames - Same(second, third)}]");
            Check(sameCopy < Frames, summary + "; the copy should draw other values (identity-seeded)");
            if (keyed)
            {
                Check(sameSecond == Frames && sameThird == Frames, summary + "; renderers of the same scene must draw the same values");
                Check(sessionKeyed, summary + "; the frames were not keyed by their objects");
                Console.WriteLine(summary + "; keyed by its objects for this session");
            }
            else
            {
                Check(!sessionKeyed && Enumerable.Range(0, Frames).All(tracker.RendersNormally), summary + "; a frame with randomness that is not aligned was keyed");
                Console.WriteLine(summary + "; rendered normally on this build");
            }
        }
        finally
        {
            foreach (var renderer in renderers) renderer.Dispose();
            TimelineFrameCache.Enabled = enabled;
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
