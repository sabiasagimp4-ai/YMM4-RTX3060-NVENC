using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Real TimelineSource, a fresh uncached-render oracle, and reproducible operation sequences.
// This exercises the host adapter; it does not simulate the GUI/audio transport or an RTX driver.
internal static class OperationSequenceChecks
{
    private const int Frames = 16;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly record struct Configuration(int Width, int Height, int Fps, bool Edited);
    private static readonly Configuration[] Configurations =
        [new(161, 91, 30, false), new(160, 90, 24, true), new(161, 91, 60, false), new(161, 91, 30, false)];

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        var dc = context.DeviceContext;
        string root = Path.Combine(Path.GetTempPath(), "ymm-cache-operations-" + Guid.NewGuid().ToString("N"));
        long frameBytes = 161L * 91 * 4 + 32;
        using var store = new FrameCacheStore(root, frameBytes * 8, 32L << 20);
        var priorStore = TimelineFrameCache.StoreIfCreated;
        long gpuBudget = TimelineFrameCache.GpuRetentionBudget;
        bool gpuEnabled = TimelineFrameCache.GpuRetentionEnabled;
        ITimelineSource[] sources = [];
        try
        {
            TimelineFrameCache.Enabled = false;
            var reference = new Dictionary<(int Project, Configuration Config, int Frame), byte[]>();
            foreach (int project in new[] { 0, 1 })
                foreach (var config in Configurations.Distinct())
                {
                    var (timeline, scene) = Model(project, config);
                    using var oracle = Source(host, context, scene);
                    var viewport = Viewport(dc, scene, config);
                    for (int frame = 0; frame < Frames; frame++)
                    {
                        oracle.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
                        reference[(project, config, frame)] = TimelineFrameCache.CapturePreview(dc, oracle.Output, viewport)!;
                    }
                }

            var models = new[] { Model(0, Configurations[0]), Model(1, Configurations[0]) };
            sources = models.Select(model => Source(host, context, model.Scene)).ToArray();
            var views = models.Select(model => Viewport(dc, model.Scene, Configurations[0])).ToArray();
            TimelineFrameCache.UseStore(store);
            TimelineFrameCache.TestViewport = source => ReferenceEquals(source, sources[0]) ? views[0]
                : ReferenceEquals(source, sources[1]) ? views[1] : null;
            TimelineFrameCache.GpuRetentionEnabled = true;
            TimelineFrameCache.GpuRetentionBudget = frameBytes * 4;
            TimelineFrameCache.Enabled = true;
            long hits = TimelineFrameCache.Hits;
            var random = new Random(31047);
            int checkedFrames = 0;
            for (int phase = 0; phase < Configurations.Length; phase++)
            {
                var config = Configurations[phase];
                for (int project = 0; project < 2; project++)
                {
                    Configure(models[project].Timeline, project, config);
                    views[project] = Viewport(dc, models[project].Scene, config);
                    Verify(project, 7, TimelineSourceUsage.Playing, config); // immediately after the edit
                }
                Thread.Sleep(300); // also exercise reuse after the documented edit-settle interval
                for (int step = 0; step < 64; step++)
                {
                    int project = step % 3 == 0 ? 1 : 0; // switch while other sources have deferred readbacks
                    int frame = step < 16 ? step : step < 32 ? 31 - step : random.Next(Frames);
                    var usage = step % 2 == 0 ? TimelineSourceUsage.Playing : TimelineSourceUsage.Paused;
                    if (step == 35) store.SetRamBudget(0);
                    if (step == 40) store.SetRamBudget(frameBytes * 8);
                    if (step == 43) TimelineFrameCache.GpuRetentionBudget = 0;
                    if (step == 48) TimelineFrameCache.GpuRetentionBudget = frameBytes * 4;
                    if (step == 52) TimelineFrameCache.Clear();
                    Verify(project, frame, usage, config);
                    if (step % 8 == 0) Verify(project, frame, usage, config); // same-frame delivery
                }
                foreach (var source in sources) TimelineFrameCache.CompletePendingStore(source);
            }
            Check(TimelineFrameCache.Hits > hits, "operation replay never exercised reuse");
            Console.WriteLine($"Host operation replay: seed 31047, {checkedFrames} exact pixel comparisons; forward/backward/rapid seeks, "
                + "paused/playing, immediate edits/A-B-A, project switches, size/FPS changes, RAM/GPU pressure and purge passed.");

            void Verify(int project, int frame, TimelineSourceUsage usage, Configuration config)
            {
                var timeline = models[project].Timeline;
                sources[project].Update(timeline.VideoInfo.GetTimeFrom(frame), usage);
                var actual = TimelineFrameCache.CapturePreview(dc, sources[project].Output, views[project]);
                Check(actual is not null && actual.SequenceEqual(reference[(project, config, frame)]),
                    $"Operation replay pixel mismatch: seed 31047, comparison {checkedFrames}, project {project}, frame {frame}, {usage}, {config}; {TimelineFrameCache.Status}");
                checkedFrames++;
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            TimelineFrameCache.Enabled = false;
            foreach (var source in sources) source.Dispose();
            TimelineFrameCache.GpuRetentionEnabled = gpuEnabled;
            TimelineFrameCache.GpuRetentionBudget = gpuBudget;
            if (priorStore is not null) TimelineFrameCache.UseStore(priorStore);
            store.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        Check(TimelineFrameCache.GpuBytes == 0, "operation replay leaked GPU reservations");
    }

    private static (Timeline Timeline, Scene Scene) Model(int project, Configuration config)
    {
        var timeline = new Timeline();
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame => new ShapeItem { Frame = frame, Length = 1 }));
        Configure(timeline, project, config);
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        return (timeline, new Scene(timeline, scenes, []));
    }
    private static void Configure(Timeline timeline, int project, Configuration config)
    {
        timeline.VideoInfo.Width = config.Width; timeline.VideoInfo.Height = config.Height; timeline.VideoInfo.FPS = config.Fps;
        timeline.VideoInfo.BackgroundColor = project == 0 ? System.Windows.Media.Colors.DarkRed : System.Windows.Media.Colors.DarkBlue;
        foreach (var shape in timeline.Items.OfType<ShapeItem>())
        {
            shape.X.SetFirstValue(-65 + shape.Frame * 8 + (config.Edited ? 5 : 0));
            shape.Y.SetFirstValue(project * 12 - 9);
            shape.Opacity.SetFirstValue(37 + shape.Frame * 3);
        }
    }
    private static ITimelineSource Source(Assembly host, IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;
    private static TimelineFrameCache.PreviewViewport Viewport(ID2D1DeviceContext dc, Scene scene, Configuration config) =>
        new(config.Width, config.Height, Matrix3x2.Identity, new Vector2(config.Width / 2f, config.Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            scene.ID, scene.Timeline.ID, Stopwatch.GetTimestamp(), false);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
