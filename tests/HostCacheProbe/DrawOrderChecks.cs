using System.Reflection;
using System.Numerics;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Player.Video;

// Two overlapping items of one layer. YMM4 draws them in the order of its resource dictionary, which the frames a
// renderer drew before can change; with DrawOrderAlignment the plugin draws them in item-list order, so every render
// of a frame is alike, the swapped list is another picture, and the frame key names the order (a cached frame of one
// order is never shown for the other). Without the alignment (it could not be installed) this only reports what YMM4
// does.
internal static class DrawOrderChecks
{
    private const int Width = 160, Height = 90;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        Console.WriteLine($"Item types: TransitionItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(TransitionItem))}; " +
            $"VoiceItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(VoiceItem))}; AudioItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(AudioItem))}");
        bool aligned = DrawOrderAlignment.Installed;
        bool enabled = TimelineFrameCache.Enabled;
        try
        {
            foreach (int blueLayer in new[] { 1, 2 })
            {
                bool tie = blueLayer == 1;
                string layers = tie ? "same layer" : "different layers";
                TimelineFrameCache.Enabled = false; // the host's own pictures
                // One project whose item list is [red, blue], or [blue, red] when swapped.
                var (timeline, scene, red, blue) = Fixture(blueLayer);
                void Order(bool swapped) => timeline.Items = swapped ? [blue, red] : [red, blue];
                var reference = Render(host, context, scene, 30);
                var variants = new List<(string Name, byte[] Pixels)>
                {
                    ("new source again", Render(host, context, scene, 30)),
                    ("after frame 10 (red created first)", Render(host, context, scene, 30, 10)),
                    ("after frames 50, 10", Render(host, context, scene, 30, 50, 10)),
                    ("after 0..29 in order", Render(host, context, scene, 30, Enumerable.Range(0, 30).ToArray())),
                    ("after 59..31 backwards", Render(host, context, scene, 30, Enumerable.Range(31, 29).Reverse().ToArray())),
                };
                for (int i = 0; i < 10; i++) variants.Add(($"new source #{i + 2}", Render(host, context, scene, 30)));
                Order(swapped: true);
                var swapped = Render(host, context, scene, 30);
                var swappedVariants = new List<(string Name, byte[] Pixels)>
                {
                    ("swapped, after frame 10", Render(host, context, scene, 30, 10)),
                    ("swapped, after 59..31 backwards", Render(host, context, scene, 30, Enumerable.Range(31, 29).Reverse().ToArray())),
                };
                Order(swapped: false);
                var differing = variants.Where(variant => !variant.Pixels.AsSpan().SequenceEqual(reference))
                    .Concat(swappedVariants.Where(variant => !variant.Pixels.AsSpan().SequenceEqual(swapped))).Select(variant => variant.Name).ToArray();
                bool swapChanges = !swapped.AsSpan().SequenceEqual(reference);
                Console.WriteLine($"Draw order, two overlapping text items on the {layers} (alignment {(aligned ? "installed" : "not installed")}): " +
                    $"{variants.Count + swappedVariants.Count} renders of frame 30 compared with a new source's; " +
                    (differing.Length == 0 ? "all equal" : "different after: " + string.Join("; ", differing)) +
                    $"; swapping the item list {(swapChanges ? "changes" : "keeps")} the picture");
                if (!aligned) continue;
                Check(differing.Length == 0, $"Draw order ({layers}): renders of one frame differ: {string.Join("; ", differing)}");
                Check(swapChanges == tie, tie ? "Draw order: the item list did not decide the order of a tie"
                    : "Draw order: swapping the item list changed items of different layers");

                // The keys: frame 30 is cacheable in both orders and keyed by the order where the items tie; frame 10
                // (red alone) does not change.
                var frames = Describe(scene);
                Order(swapped: true);
                var swappedFrames = Describe(scene);
                Order(swapped: false);
                Check(frames.For(30).Cacheable && swappedFrames.For(30).Cacheable, $"Draw order ({layers}): frame 30 is not cacheable");
                Check((frames.For(30).Content != swappedFrames.For(30).Content) == tie,
                    tie ? "Draw order: the key does not name the order of a tie" : "Draw order: the key of different layers names the list order");
                Check(frames.For(10).Content == swappedFrames.For(10).Content, "Draw order: the key of a frame without a tie names the list order");

                // Cached: each order shows its own picture, also after the other order's frame was stored, and comes back
                // from the store when the list returns to it. One renderer draws them all (a new renderer starts by
                // fingerprinting the font file, rendering normally meanwhile), each order until a frame is reused.
                TimelineFrameCache.Enabled = true;
                TimelineFrameCache.Clear();
                using var cached = NewSource(host, context, scene);
                var time = scene.Timeline.VideoInfo.GetTimeFrom(30);
                byte[] Reused(string order, Func<long> counter)
                {
                    Check(SpinWait.SpinUntil(() =>
                    {
                        cached.Update(time, TimelineSourceUsage.Exporting); TimelineFrameCache.CompletePendingStore(cached);
                        long before = counter();
                        cached.Update(time, TimelineSourceUsage.Exporting);
                        return counter() > before;
                    }, TimeSpan.FromSeconds(10)), $"Draw order ({layers}): frame 30 of the {order} order was not reused: {TimelineFrameCache.Status}");
                    return TimelineFrameCache.Capture(context.DeviceContext, cached.Output, Width, Height, new Vector2(-Width / 2f, -Height / 2f))!;
                }
                var reused = Reused("first", () => TimelineFrameCache.Hits);
                Order(swapped: true);
                var reusedSwapped = Reused("swapped", () => TimelineFrameCache.Hits);
                Order(swapped: false);
                var restored = Reused("first (from the store)", () => TimelineFrameCache.RamHits);
                Check(reused.AsSpan().SequenceEqual(reference) && reusedSwapped.AsSpan().SequenceEqual(swapped) && restored.AsSpan().SequenceEqual(reference),
                    $"Draw order ({layers}): a cached frame differs from the host's render of its order");
            }
            if (aligned) Console.WriteLine("Draw order: ties drawn in item-list order in every renderer, keyed by the order, cached per order OK");
        }
        finally { TimelineFrameCache.Enabled = enabled; TimelineFrameCache.Clear(); }
    }

    private static ITimelineSource NewSource(Assembly host, IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;

    // Frame `frame` of a new renderer that first drew the frames `before`.
    private static byte[] Render(Assembly host, IGraphicsDevicesAndContext context, Scene scene, int frame, params int[] before)
    {
        using (var source = NewSource(host, context, scene))
        {
            foreach (int at in before) source.Update(scene.Timeline.VideoInfo.GetTimeFrom(at), TimelineSourceUsage.Exporting);
            source.Update(scene.Timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
            return TimelineFrameCache.Capture(context.DeviceContext, source.Output, Width, Height, new Vector2(-Width / 2f, -Height / 2f))!;
        }
    }

    private static FrameDependencyIndex Describe(Scene scene)
    {
        Check(FrameCacheKey.TryDescribe(scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var frames, out string reason) && frames is not null,
            "Draw order: the fixture cannot be described: " + reason);
        return frames!;
    }

    // Red from frame 0 and blue from frame 20, both until 60, overlapping on screen; blue on layer `blueLayer`. The item
    // list is [red, blue].
    private static (Timeline Timeline, Scene Scene, TextItem Red, TextItem Blue) Fixture(int blueLayer)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        TextItem Square(int frame, int layer, double x, System.Windows.Media.Color color)
        {
            var item = new TextItem { Frame = frame, Length = 60 - frame, Layer = layer, Text = "■", Font = "Arial", FontColor = color };
            item.FontSize.SetFirst(60);
            item.X.SetFirst(x);
            return item;
        }
        var red = Square(0, 1, -12, System.Windows.Media.Colors.Red);
        var blue = Square(20, blueLayer, 12, System.Windows.Media.Colors.Blue);
        timeline.Items = [red, blue];
        return (timeline, new Scene(timeline, scenes, []), red, blue);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
