using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class IdleParallelMeasurements
{
    internal static void Run(Assembly host) => RunBounded(host, 2, false, false);
    internal static void RunChecks(Assembly host)
    {
        var defaults = new FrameCacheToolSettings();
        Check(defaults.IdleWorkers == 0, "Idle workers must default to Auto");
        defaults.IdleWorkers = 99; Check(defaults.IdleWorkers == 4, "Idle worker setting exceeded four");
        defaults.IdleWorkers = -1; Check(defaults.IdleWorkers == 0, "Negative idle worker setting must select Auto");
        var saved = YukkuriMovieMaker.Json.Json.LoadFromText<FrameCacheToolSettings>("{\"IdleWorkers\":2}");
        var old = YukkuriMovieMaker.Json.Json.LoadFromText<FrameCacheToolSettings>("{}");
        Check(saved?.IdleWorkers == 2 && old?.IdleWorkers == 0, "Idle worker setting did not preserve manual / old saved settings");
        RunBounded(host, 2, false, true);
        RunBounded(host, 4, false, true);
        RunBounded(host, 4, true, true);
    }
    private static void RunBounded(Assembly host, int workers, bool random, bool checks)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { RunCase(host, workers, random, checks); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "Idle batch measurement" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(finished.Wait(TimeSpan.FromSeconds(30)), "Idle measurement exceeded 30 seconds");
        if (failure is not null) throw new InvalidOperationException("Idle measurement", failure);
    }

    private static void RunCase(Assembly host, int workers, bool random, bool checks)
    {
        const int Width = 321, Height = 181;
        int Frames = checks ? 24 : 60, Shapes = checks ? 20 : 200, Texts = checks ? 4 : 20;
        var harmony = new Harmony("ymm.tests.idle-parallel-measurement");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(IdleParallelMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        var timeline = new Timeline(); timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Shapes).Select(index =>
        {
            var item = new ShapeItem { Frame = 0, Length = Frames, Layer = index };
            item.X.SetFirstValue(index % 20 * 14.25 - 145); item.Y.SetFirstValue(index / 20 * 14.5 - 65);
            item.Opacity.SetFirstValue(15 + index % 50);
            return item;
        })).AddRange(Enumerable.Range(0, Texts).Select(index => new TextItem
        { Frame = index, Length = Frames - index, Layer = 200 + index, Text = "idle worker " + index, Font = "Arial" }));
        if (random)
        {
            var shaking = new ShapeItem { Frame = Frames / 2, Length = Frames / 2, Layer = 230 };
            shaking.X.AnimationType = AnimationType.ランダム移動;
            timeline.Items = timeline.Items.Add(shaking);
            var json = JsonNode.Parse(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
            var values = (JsonArray)json["Items"]![Shapes + Texts]!["X"]!["Values"]!;
            var low = values[0]!.DeepClone(); var high = values[0]!.DeepClone();
            low["Value"] = -120.0; high["Value"] = 120.0;
            values.Clear(); values.Add(low); values.Add(high);
            timeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(json.ToJsonString())!;
        }
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
            if (random) Check(Enumerable.Range(Frames / 2 + 1, Frames / 2 - 1)
                .Count(frame => !pixels[frame].SequenceEqual(pixels[frame - 1])) >= Frames / 4,
                "Random test scene did not move across enough frames");
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(0); TimelineFrameCache.CompletePendingStore(player); long hits = TimelineFrameCache.Hits; Update(0);
                if (!tracker.TryCapture(0, out var capture, out _)) return false;
                capture!.Dispose(); return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(10)), "Idle measurement did not become ready: " + TimelineFrameCache.Status);
            Check(tracker.TryCapture(0, out var initial, out reason), reason);
            string model;
            using (initial) model = initial!.Model;
            var batches = new IdleFramePreRenderer.BatchRenderer?[workers];
            var owners = new int[workers];
            var devicesSeen = new nint[workers];
            try
            {
                long privateBefore = PrivateBytes();
                var gpuBefore = GpuMemoryController.Probe()?.Sample;
                IdleFramePreRenderer.RunWorkersForTests(workers, worker =>
                {
                    Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Idle worker was not STA");
                    owners[worker] = Environment.CurrentManagedThreadId;
                    var batch = batches[worker] = new(tracker, model);
                    devicesSeen[worker] = batch.Source.Devices.DeviceContext.NativePointer;
                    batch.Source.Update(TimeSpan.Zero, TimelineSourceUsage.Playing);
                    Check(batch.CloneTracker.TryCapture(0, out var warm, out var warmReason), warmReason);
                    warm!.Dispose();
                    TimelineFrameCache.CapturePreview(batch.Source.Devices.DeviceContext, batch.Source.Output, view);
                });
                Check(owners.Distinct().Count() == workers && devicesSeen.Distinct().Count() == workers
                    && devicesSeen.All(pointer => pointer != 0), "Workers did not own distinct threads and drawing devices");
                long privateDelta = PrivateBytes() - privateBefore;
                var gpuAfter = GpuMemoryController.Probe()?.Sample;
                Console.WriteLine("IDLE_MEMORY " + JsonSerializer.Serialize(new
                { workers, process_private_delta_bytes = privateDelta, gpu_usage_before = gpuBefore?.CurrentUsage,
                    gpu_usage_after = gpuAfter?.CurrentUsage, gpu_is_software = gpuAfter?.Software,
                    note = "aggregate process delta; not dedicated VRAM per device" }));
                for (int repeat = 1; repeat <= (checks ? 16 : 2); repeat++)
                {
                    TimelineFrameCache.Enabled = false;
                    var off = Stopwatch.StartNew();
                    for (int frame = 0; frame < Frames; frame++) { Update(frame); TimelineFrameCache.CapturePreview(dc, player.Output, view); }
                    double offMilliseconds = off.Elapsed.TotalMilliseconds;
                    TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                    var visits = new int[Frames];
                    var clock = Stopwatch.StartNew();
                    IdleFramePreRenderer.RunWorkersForTests(workers, worker =>
                    {
                        Check(owners[worker] == Environment.CurrentManagedThreadId, "Worker switched threads between batches");
                        var batch = batches[worker]!;
                        foreach (long ordinal in IdleFramePreRenderer.AssignedOrdinals(0, Frames - 1, worker, workers,
                            position => tracker.IsSessionKeyed((int)position)))
                        {
                            int frame = (int)ordinal;
                            Check(Interlocked.Increment(ref visits[frame]) == 1, "Duplicate assignment: " + frame);
                            Check(!tracker.IsSessionKeyed(frame) || worker == 0, "Session frame assigned to another worker");
                            var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, frame, view,
                                () => true, CancellationToken.None, out string workerReason, allowLive: worker == 0);
                            Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered, $"Idle frame {frame}: {result}: {workerReason}");
                        }
                    });
                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    Check(visits.All(count => count == 1), "A frame was skipped or assigned twice");
                    Check(!random || batches[0]!.LiveSource is not null, "Session worker never used the live scene");
                    Check(batches.Skip(1).All(batch => batch!.LiveSource is null), "A secondary worker created a live renderer");
                    Console.WriteLine((checks ? "IDLE_PARALLEL_CHECK " : "SPEEDUP8 ") + JsonSerializer.Serialize(new
                    { repeat, workers, frames = Frames, rendered = visits.Sum(), fps = Frames * 1000 / elapsed, total_ms = elapsed,
                        off_capture_ms = offMilliseconds, normalized_time = elapsed / offMilliseconds, width = Width, height = Height,
                        shapes = Shapes, texts = Texts, random, renderer_preparation_timed = false }));
                    long hits = TimelineFrameCache.Hits;
                    for (int frame = 0; frame < Frames; frame++)
                    { Update(frame); Check(TimelineFrameCache.CapturePreview(dc, player.Output, view)!.SequenceEqual(pixels[frame]), "Idle measurement pixel mismatch at " + frame); }
                    Check(TimelineFrameCache.Hits - hits == Frames, "Idle measurement did not reuse every frame");
                }
                if (checks)
                {
                    // Stop inside an actual host render, before publication. Dispatch must join every worker.
                    foreach (bool cancel in new[] { true, false })
                    {
                        TimelineFrameCache.Clear();
                        using var cancellation = new CancellationTokenSource();
                        using var barrier = new Barrier(workers, _ =>
                        { if (cancel) cancellation.Cancel(); else TimelineFrameCache.Clear(); });
                        int stopped = 0;
                        IdleFramePreRenderer.RunWorkersForTests(workers, worker =>
                        {
                            var batch = batches[worker]!;
                            var result = IdleFramePreRenderer.PrimeFrame(tracker, scene, batch.CloneTracker, batch.CloneScene,
                                batch.Source, time =>
                                {
                                    batch.Source.Update(time, TimelineSourceUsage.Playing);
                                    Check(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), "Cancellation barrier timed out");
                                }, worker, view, () => true, cancellation.Token, out string workerReason);
                            Check(result == IdleFramePreRenderer.IdleFrameResult.Unavailable,
                                $"Cancelled/purged frame was published: {result}: {workerReason}");
                            Interlocked.Increment(ref stopped);
                        });
                        Check(stopped == workers && store.RamBytes == 0, "Dispatch returned before all cancelled workers joined, or stored stale frames");
                    }
                    if (random)
                    {
                        TimelineFrameCache.Clear();
                        IdleFramePreRenderer.RunWorkersForTests(workers, worker =>
                        {
                            if (worker != 0)
                            {
                                var denied = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batches[worker]!, Frames / 2,
                                    view, () => true, CancellationToken.None, out _, allowLive: false);
                                Check(denied == IdleFramePreRenderer.IdleFrameResult.Unavailable && batches[worker]!.LiveSource is null,
                                    "Secondary worker was allowed to render a newly classified session frame");
                                return;
                            }
                            var source = batches[0]!.LiveSourceFor(scene);
                            var result = IdleFramePreRenderer.PrimeLiveFrame(tracker, scene, source, time =>
                            { source.Update(time, TimelineSourceUsage.Playing); TimelineFrameCache.Clear(); }, Frames / 2, view,
                                () => true, CancellationToken.None);
                            Check(result == IdleFramePreRenderer.IdleFrameResult.Unavailable && store.RamBytes == 0,
                                "Purged session frame was stored after its live render");
                        });
                    }
                    Console.WriteLine($"Idle {workers} workers (random={random}): once-only assignment, pixels, cancel/purge joins OK");
                }
            }
            finally
            {
                IdleFramePreRenderer.RunWorkersForTests(workers, worker =>
                {
                    if (batches[worker] is null) return;
                    Check(owners[worker] == Environment.CurrentManagedThreadId, "Renderer disposed from another thread");
                    batches[worker]!.Dispose();
                });
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
