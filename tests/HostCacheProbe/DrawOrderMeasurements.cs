using System.Reflection;
using System.Numerics;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Player.Video;

// Measurement, not a check: whether the host draws two overlapping items of one layer in an order that depends on
// more than their content (the item list order, which frames a source drew before, a new source). The cache keys a
// frame by the set of its items' contents, so such a dependence would make two different pictures share a key.
internal static class DrawOrderMeasurements
{
    private const int Width = 160, Height = 90;

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        Console.WriteLine($"Item types: TransitionItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(TransitionItem))}; " +
            $"VoiceItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(VoiceItem))}; AudioItem is IVideoItem: {typeof(IVideoItem).IsAssignableFrom(typeof(AudioItem))}");
        bool enabled = TimelineFrameCache.Enabled;
        TimelineFrameCache.Enabled = false; // the host's own pictures
        try
        {
            foreach (int otherLayer in new[] { 1, 2 })
            {
                var (timeline, scene, red, blue) = Fixture(otherLayer);
                byte[] Render(Action<ITimelineSource>? before, int frame)
                {
                    var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
                    using (source)
                    {
                        before?.Invoke(source);
                        source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
                        return TimelineFrameCache.Capture(context.DeviceContext, source.Output, Width, Height, new Vector2(-Width / 2f, -Height / 2f))!;
                    }
                }
                void At(ITimelineSource source, params int[] frames)
                {
                    foreach (int frame in frames) source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
                }
                var reference = Render(null, 30);
                var variants = new List<(string Name, byte[] Pixels)>
                {
                    ("new source again", Render(null, 30)),
                    ("after frame 10 (red created first)", Render(s => At(s, 10), 30)),
                    ("after frames 50, 10", Render(s => At(s, 50, 10), 30)),
                    ("after 0..29 in order", Render(s => At(s, Enumerable.Range(0, 30).ToArray()), 30)),
                    ("after 59..31 backwards", Render(s => At(s, Enumerable.Range(31, 29).Reverse().ToArray()), 30)),
                };
                for (int i = 0; i < 10; i++) variants.Add(($"new source #{i + 2}", Render(null, 30)));
                timeline.Items = [blue, red]; // the list order only
                variants.Add(("item list order swapped", Render(null, 30)));
                variants.Add(("item list order swapped, after frame 10", Render(s => At(s, 10), 30)));
                timeline.Items = [red, blue];
                var differing = variants.Where(variant => !variant.Pixels.AsSpan().SequenceEqual(reference)).Select(variant => variant.Name).ToArray();
                string layers = otherLayer == 1 ? "same layer" : "different layers";
                Console.WriteLine($"Draw order, two overlapping text items on the {layers}: {variants.Count} renders of frame 30 compared with a new source's; " +
                    (differing.Length == 0 ? "all equal" : "different after: " + string.Join("; ", differing)));
            }
        }
        finally { TimelineFrameCache.Enabled = enabled; }
    }

    // Red from frame 0 and blue from frame 20, both until 60, overlapping on screen; blue on layer `blueLayer`.
    private static (Timeline Timeline, Scene Scene, TextItem Red, TextItem Blue) Fixture(int blueLayer)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        TextItem Square(int frame, int layer, double x, System.Windows.Media.Color color)
        {
            var item = new TextItem { Frame = frame, Length = 60 - frame, Layer = layer, Text = "\u25A0", Font = "Arial", FontColor = color };
            item.FontSize.SetFirst(60);
            item.X.SetFirst(x);
            return item;
        }
        var red = Square(0, 1, -12, System.Windows.Media.Colors.Red);
        var blue = Square(20, blueLayer, 12, System.Windows.Media.Colors.Blue);
        timeline.Items = [red, blue];
        return (timeline, new Scene(timeline, scenes, []), red, blue);
    }
}
