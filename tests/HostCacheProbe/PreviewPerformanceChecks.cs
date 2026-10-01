using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Real host source and GPU, with a stand-in source-only preview Draw. No audio, swap chain or GUI.
// Pixel validation is a separate pass: captures inside the timed loop would synchronize the GPU.
internal static class PreviewPerformanceChecks
{
    private const int Frames = 100, Width = 1920, Height = 1080;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(Assembly host)
    {
        Check(host.GetName().Version?.ToString() == "4.56.1.0", "Performance fixture requires YMM4 4.56.1.0");
        var harmony = new Harmony("ymm.tests.preview-performance");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(PreviewPerformanceChecks), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var target = dc.CreateBitmap(new SizeI(Width, Height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var surface = target.Surface;
        using var dxgi = surface!.GetDevice<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var description = adapter.Description;
        string driver = adapter.CheckInterfaceSupport<IDXGIDevice>(out long version) ? version.ToString("X16") : "unknown";
        string root = Path.Combine(Path.GetTempPath(), "ymm-preview-performance-" + Guid.NewGuid().ToString("N"));
        using var store = new FrameCacheStore(root, Frames * ((long)Width * Height * 4 + 32) + (1L << 20), 0);
        TimelineFrameCache.UseStore(store);
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1 };
            item.X.SetFirstValue(-600 + frame * 12.25); item.Y.SetFirstValue(frame % 7 * 20 - 70);
            item.Opacity.SetFirstValue(43);
            return item;
        }));
        timeline.Items = timeline.Items.Add(new TextItem { Frame = 0, Length = Frames, Layer = 1, Text = "Preview cache measurement", Font = "Arial" });
        var scene = new Scene(timeline, scenes, []);
        ITimelineSource? source = null;
        try
        {
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                Instance, null, [context, scene, null], null)!;
            source.GetType().GetProperty("NeedTimelineItemRects")!.SetValue(source, false);
            var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity,
                new Vector2(Width / 2f, Height / 2f), 96, 96, target.PixelFormat,
                dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
                scene.ID, timeline.ID, Stopwatch.GetTimestamp(), true);
            TimelineFrameCache.TestViewport = value => ReferenceEquals(value, source) ? viewport : null;

            void Update(int frame) => source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
            void Draw()
            {
                using var oldTarget = dc.Target;
                var transform = dc.Transform;
                try
                {
                    dc.Target = target; dc.Transform = viewport.Transform;
                    dc.BeginDraw(); dc.Clear(new Color4(0, 0, 0, 1));
                    dc.DrawImage(source.Output, viewport.TargetOffset);
                    dc.EndDraw().CheckError();
                }
                finally { dc.Target = oldTarget; dc.Transform = transform; }
            }

            TimelineFrameCache.SetEnabled(true, false);
            long warmed = TimelineFrameCache.PreviewStored;
            var warmup = Stopwatch.StartNew();
            while (TimelineFrameCache.PreviewStored == warmed && warmup.Elapsed < TimeSpan.FromSeconds(30))
            {
                Update(Frames - 1); Draw(); TimelineFrameCache.CompletePendingStore(source); Thread.Sleep(10);
            }
            Check(TimelineFrameCache.PreviewStored > warmed, "Warmup never stored a frame: " + TimelineFrameCache.Status);
            // JIT and frame-specific model keys are warmed independently of the measured cold store.
            for (int frame = 0; frame < Frames; frame++) { Update(frame); Draw(); }
            TimelineFrameCache.CompletePendingStore(source);
            TimelineFrameCache.Clear();

            object Measure(string mode, bool enabled)
            {
                TimelineFrameCache.SetEnabled(enabled, false);
                PreviewPerformance.Reset();
                long hits = TimelineFrameCache.RamHits, stored = TimelineFrameCache.PreviewStored;
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var wall = Stopwatch.StartNew();
                for (int frame = 0; frame < Frames; frame++)
                {
                    long started = PreviewPerformance.Timestamp;
                    Update(frame);
                    using (PreviewPerformance.Measure(PreviewStage.PreviewDraw)) Draw();
                    PreviewPerformance.End(PreviewStage.TotalPreview, started);
                }
                wall.Stop();
                var flush = Stopwatch.StartNew();
                TimelineFrameCache.CompletePendingStore(source);
                flush.Stop();
                long ramHits = TimelineFrameCache.RamHits - hits, saved = TimelineFrameCache.PreviewStored - stored;
                var stages = PreviewPerformance.Snapshot();
                Check(stages.Single(s => s.Stage == PreviewStage.TotalUpdate).SampleCount == Frames, mode + " did not measure all updates");
                Check(stages.Single(s => s.Stage == PreviewStage.TotalPreview).SampleCount == Frames, mode + " did not measure all previews");
                if (mode == "cold-store") Check(saved == Frames && ramHits == 0, $"Cold-store counts: saved={saved}, hits={ramHits}");
                if (mode == "ram-hit") Check(ramHits == Frames && saved == 0, $"RAM-hit counts: hits={ramHits}, saved={saved}");
                if (mode == "off") Check(saved == 0 && ramHits == 0, "OFF used the cache");
                long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                Console.WriteLine($"{mode}: {wall.Elapsed.TotalMilliseconds / Frames:F3} ms/frame, final flush {flush.Elapsed.TotalMilliseconds:F3} ms, RAM hits {ramHits}, stored {saved}");
                return new { Mode = mode, WallMilliseconds = wall.Elapsed.TotalMilliseconds, FinalFlushMilliseconds = flush.Elapsed.TotalMilliseconds,
                    AllocatedBytes = bytes, GcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(), RamHits = ramHits, Stored = saved, Stages = stages };
            }

            var measurements = new[] { Measure("off", false), Measure("cold-store", true), Measure("ram-hit", true) };
            // Every frame, with no captures in the preceding measurements.
            for (int frame = 0; frame < Frames; frame++)
            {
                TimelineFrameCache.SetEnabled(false, false); Update(frame);
                var baseline = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                TimelineFrameCache.SetEnabled(true, false); Update(frame);
                var cached = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                Check(baseline.SequenceEqual(cached), $"Frame {frame} pixel parity failed");
            }
            var report = new { HostVersion = host.GetName().Version!.ToString(), HostMvid = host.ManifestModule.ModuleVersionId,
                HostSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(host.Location))),
                Adapter = description.Description, description.VendorId, description.DeviceId, Driver = driver,
                Frames, Width, Height, DiskEnabled = false, PixelParityFrames = Frames,
                TimingScope = "Real TimelineSource.Update + stand-in source-only Draw; no GUI/audio/Present/pacing. Final readback flush separate. Cache OFF still has measurement hooks.",
                Measurements = measurements };
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            string output = Path.GetFullPath("dist/preview-performance.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, options));
            Console.WriteLine($"{description.Description}: {Frames} frames pixel-exact; {output}");
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            source?.Dispose();
            TimelineFrameCache.SetEnabled(false, false);
            FrameRenderReadiness.Uninstall(harmony);
            harmony.UnpatchAll(harmony.Id);
        }
        Check(TimelineFrameCache.GpuBytes == 0, "Performance fixture leaked GPU reservations");
    }

    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
