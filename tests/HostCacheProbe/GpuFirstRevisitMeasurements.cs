using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
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

internal static class GpuFirstRevisitMeasurements
{
    internal static void Run(Assembly host, bool regression = false)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try { RunCase(host, regression); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "GPU first revisit measurement" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Check(finished.Wait(TimeSpan.FromSeconds(30)), "GPU first revisit measurement exceeded 30 seconds");
        if (failure is not null) throw new InvalidOperationException("GPU first revisit measurement", failure);
    }

    private static void RunCase(Assembly host, bool regression)
    {
        const int Frames = 8, Width = 1920, Height = 1080;
        var harmony = new Harmony("ymm.tests.gpu-first-revisit");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(GpuFirstRevisitMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var target = dc.CreateBitmap(new SizeI(Width, Height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        var timeline = new Timeline(); timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1, Layer = 0 };
            item.X.SetFirstValue(-550 + 137.25 * frame); item.Opacity.SetFirstValue(47);
            return item;
        })).Add(new TextItem { Frame = 0, Length = Frames, Layer = 1, Text = "GPU first revisit", Font = "Arial" });
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(timeline); var scene = new Scene(timeline, scenes, []);
        using var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        string root = Path.Combine(Path.GetTempPath(), "ymm-first-revisit-" + Guid.NewGuid().ToString("N"));
        using var store = new FrameCacheStore(root, 128L << 20, 0);
        var view = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f),
            96, 96, target.PixelFormat, dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            scene.ID, timeline.ID, Stopwatch.GetTimestamp(), true);
        long oldBudget = TimelineFrameCache.GpuRetentionBudget;
        bool oldRetention = TimelineFrameCache.GpuRetentionEnabled;
        void Update(int frame) => source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
        void Draw()
        {
            using var old = dc.Target; var transform = dc.Transform;
            try
            {
                dc.Target = target; dc.Transform = view.Transform; dc.BeginDraw();
                dc.Clear(new Color4(0, 0, 0, 1)); dc.DrawImage(source.Output, view.TargetOffset); dc.EndDraw().CheckError();
            }
            finally { dc.Target = old; dc.Transform = transform; }
        }
        try
        {
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            TimelineFrameCache.UseStore(store); TimelineFrameCache.GpuRetentionEnabled = true;
            TimelineFrameCache.GpuRetentionBudget = Frames * (long)Width * Height * 4;
            TimelineFrameCache.TestViewport = value => ReferenceEquals(value, source) ? view : null;
            var pixels = new byte[Frames][];
            TimelineFrameCache.Enabled = false;
            for (int frame = 0; frame < Frames; frame++)
            { Update(frame); pixels[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, view)!; }
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(0); TimelineFrameCache.CompletePendingStore(source); long hits = TimelineFrameCache.Hits; Update(0);
                return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(30)), "GPU revisit benchmark never keyed: " + TimelineFrameCache.Status);
            void Measure(string mode, bool enabled, int repeat, bool finish)
            {
                TimelineFrameCache.Enabled = enabled;
                long hits = TimelineFrameCache.Hits, gpu = TimelineFrameCache.GpuHits;
                var watch = Stopwatch.StartNew();
                for (int frame = 0; frame < Frames; frame++)
                { Update(frame); Draw(); if (finish) TimelineFrameCache.CompletePendingStore(source); }
                double elapsed = watch.Elapsed.TotalMilliseconds;
                if (regression && mode == "first-revisit")
                    Check(TimelineFrameCache.Hits - hits == Frames && TimelineFrameCache.GpuHits - gpu == Frames,
                        "The first revisit did not use the GPU for every cold-retained frame");
                Console.WriteLine((regression ? "GPU_CHECK6 " : "SPEEDUP6 ") + JsonSerializer.Serialize(new
                {
                    mode, repeat, frames = Frames, width = Width, height = Height, ms_per_frame = elapsed / Frames,
                    cache_hits = TimelineFrameCache.Hits - hits, gpu_hits = TimelineFrameCache.GpuHits - gpu,
                    retained_bytes = TimelineFrameCache.GpuRetainedBytes, budget_bytes = TimelineFrameCache.GpuRetentionBudget,
                    synchronous_store_completion = finish,
                }));
            }
            for (int repeat = 1; repeat <= 2; repeat++)
            {
                Measure("off", false, repeat, false);
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                Measure("cold-store", true, repeat, true);
                Measure("first-revisit", true, repeat, false);
                if (regression) Check(TimelineFrameCache.GpuRetainedBytes == Frames * (long)Width * Height * 4,
                    "Cold copies were not retained for every frame");
                for (int frame = 0; frame < Frames; frame++)
                {
                    Update(frame); Check(TimelineFrameCache.CapturePreview(dc, source.Output, view)!.SequenceEqual(pixels[frame]), "GPU first revisit pixel mismatch at " + frame);
                }
            }
            if (regression)
            {
                TimelineFrameCache.Clear(); TimelineFrameCache.GpuRetentionEnabled = false;
                for (int frame = 0; frame < Frames; frame++) { Update(frame); Draw(); TimelineFrameCache.CompletePendingStore(source); }
                TimelineFrameCache.GpuRetentionEnabled = true;
                long ahead = TimelineFrameCache.GpuReadAheads;
                Update(0); Draw();
                Check(TimelineFrameCache.GpuReadAheads > ahead, "Playback did not warm the next RAM frame on the GPU");
                long gpu = TimelineFrameCache.GpuHits; Update(1);
                Check(TimelineFrameCache.GpuHits > gpu && TimelineFrameCache.CapturePreview(dc, source.Output, view)!.SequenceEqual(pixels[1]),
                    "GPU read-ahead did not serve matching pixels");
                long bytes = TimelineFrameCache.GpuBytes;
                var lost = new System.Runtime.InteropServices.COMException("Test-only DXGI device removal notification", unchecked((int)0x887A0005));
                TimelineFrameCache.NotifyDeviceLossForTests(source, lost);
                Check(TimelineFrameCache.GpuRetainedBytes == 0 && TimelineFrameCache.GpuBytes <= bytes,
                    "Device loss did not discard retained ownership");
                long hits = TimelineFrameCache.Hits;
                for (int frame = 0; frame < Frames; frame++)
                {
                    Update(frame); Check(TimelineFrameCache.CapturePreview(dc, source.Output, view)!.SequenceEqual(pixels[frame]),
                        "Device-loss fallback differs from normal host pixels");
                }
                Check(TimelineFrameCache.Hits == hits && TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0,
                    "Device-loss fallback hit the cache or leaked resources");
                using (var fresh = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!)
                {
                    TimelineFrameCache.TestViewport = value => ReferenceEquals(value, fresh) ? view : null;
                    Check(SpinWait.SpinUntil(() =>
                    {
                        fresh.Update(TimeSpan.Zero, TimelineSourceUsage.Playing); TimelineFrameCache.CompletePendingStore(fresh);
                        long previousHits = TimelineFrameCache.Hits;
                        fresh.Update(TimeSpan.Zero, TimelineSourceUsage.Playing);
                        return TimelineFrameCache.Hits > previousHits;
                    }, TimeSpan.FromSeconds(10)), "Caching did not recover on a fresh source after device loss");
                    Check(TimelineFrameCache.CapturePreview(dc, fresh.Output, view)!.SequenceEqual(pixels[0]),
                        "Fresh source recovery pixels differ");
                }
                Console.WriteLine("GPU extension: cold retention, RAM read-ahead pixels and simulated device-loss fallback passed.");
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = false;
            source.Dispose(); context.CacheProvider.Clear();
            TimelineFrameCache.GpuRetentionBudget = oldBudget; TimelineFrameCache.GpuRetentionEnabled = oldRetention;
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "GPU revisit benchmark leaked resources");
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
    private static bool SkipLoader() => false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
