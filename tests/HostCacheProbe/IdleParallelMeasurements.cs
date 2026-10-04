using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class IdleParallelMeasurements
{
    internal static void Run(Assembly host)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { RunCase(host); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "Idle batch measurement" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(finished.Wait(TimeSpan.FromSeconds(30)), "Idle measurement exceeded 30 seconds");
        if (failure is not null) throw new InvalidOperationException("Idle measurement", failure);
    }

    private static void RunCase(Assembly host)
    {
        const int Frames = 60, Width = 321, Height = 181;
        var harmony = new Harmony("ymm.tests.idle-parallel-measurement");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(IdleParallelMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        var timeline = new Timeline(); timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, 200).Select(index =>
        {
            var item = new ShapeItem { Frame = 0, Length = Frames, Layer = index };
            item.X.SetFirstValue(index % 20 * 14.25 - 145); item.Y.SetFirstValue(index / 20 * 14.5 - 65);
            item.Opacity.SetFirstValue(15 + index % 50);
            return item;
        })).AddRange(Enumerable.Range(0, 20).Select(index => new TextItem
        { Frame = index, Length = Frames - index, Layer = 200 + index, Text = "idle worker " + index, Font = "Arial" }));
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(timeline); var scene = new Scene(timeline, scenes, []);
        using var player = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        string root = Path.Combine(Path.GetTempPath(), "ymm-idle-parallel-" + Guid.NewGuid().ToString("N"));
        using var store = new FrameCacheStore(root, 64L << 20, 0);
        using var tracker = new KeyDependencyTracker(scene);
        var view = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity, new Vector2(Width / 2f, Height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, scene.ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        void Update(int frame) => player.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
        try
        {
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            TimelineFrameCache.UseStore(store); TimelineFrameCache.GpuRetentionEnabled = false;
            TimelineFrameCache.TestViewport = source => ReferenceEquals(source, player) ? view : null;
            TimelineFrameCache.Enabled = false;
            var pixels = new byte[Frames][];
            for (int frame = 0; frame < Frames; frame++) { Update(frame); pixels[frame] = TimelineFrameCache.CapturePreview(dc, player.Output, view)!; }
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(0); TimelineFrameCache.CompletePendingStore(player); long hits = TimelineFrameCache.Hits; Update(0);
                if (!tracker.TryCapture(0, out var capture, out _)) return false;
                capture!.Dispose(); return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(10)), "Idle measurement did not become ready: " + TimelineFrameCache.Status);
            Check(tracker.TryCapture(0, out var initial, out reason), reason);
            long privateBefore = PrivateBytes();
            var gpuBefore = GpuMemoryController.Probe()?.Sample;
            IdleFramePreRenderer.BatchRenderer batch;
            using (initial) batch = new(tracker, initial!.Model);
            using (batch)
            {
                batch.Source.Update(TimeSpan.Zero, TimelineSourceUsage.Playing);
                Check(IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, 0, view, () => true,
                    CancellationToken.None, out reason) is IdleFramePreRenderer.IdleFrameResult.Rendered or IdleFramePreRenderer.IdleFrameResult.Stored, reason);
                var gpuAfter = GpuMemoryController.Probe()?.Sample;
                Console.WriteLine("IDLE_MEMORY " + JsonSerializer.Serialize(new
                { workers = 1, process_private_delta_bytes = PrivateBytes() - privateBefore,
                    gpu_usage_before = gpuBefore?.CurrentUsage, gpu_usage_after = gpuAfter?.CurrentUsage,
                    gpu_is_software = gpuAfter?.Software, note = "process delta; not dedicated VRAM per device" }));
                for (int repeat = 1; repeat <= 2; repeat++)
                {
                    TimelineFrameCache.Enabled = false;
                    var off = Stopwatch.StartNew();
                    for (int frame = 0; frame < Frames; frame++) { Update(frame); TimelineFrameCache.CapturePreview(dc, player.Output, view); }
                    double offMilliseconds = off.Elapsed.TotalMilliseconds;
                    TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                    var clock = Stopwatch.StartNew();
                    int rendered = 0;
                    for (int frame = 0; frame < Frames; frame++)
                    {
                        var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, frame, view,
                            () => true, CancellationToken.None, out reason);
                        Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered, $"Idle frame {frame}: {result}: {reason}");
                        rendered++;
                    }
                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    Console.WriteLine("SPEEDUP8 " + JsonSerializer.Serialize(new
                    { repeat, workers = 1, frames = Frames, rendered, fps = Frames * 1000 / elapsed, total_ms = elapsed,
                        off_capture_ms = offMilliseconds, normalized_time = elapsed / offMilliseconds, width = Width, height = Height,
                        shapes = 200, texts = 20, renderer_preparation_timed = false }));
                    long hits = TimelineFrameCache.Hits;
                    for (int frame = 0; frame < Frames; frame++)
                    { Update(frame); Check(TimelineFrameCache.CapturePreview(dc, player.Output, view)!.SequenceEqual(pixels[frame]), "Idle measurement pixel mismatch at " + frame); }
                    Check(TimelineFrameCache.Hits - hits == Frames, "Idle measurement did not reuse every frame");
                }
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = false;
            player.Dispose(); context.CacheProvider.Clear();
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "Idle measurement leaked GPU resources");
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
    private static bool SkipLoader() => false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
