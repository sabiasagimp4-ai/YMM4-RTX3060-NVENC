using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
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
    private const int ReadbackSlots = 3; // TimelineFrameCache.ReadbacksInFlight
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(Assembly host)
    {
        Check(host.GetName().Version?.ToString() == "4.56.1.0", "Performance fixture requires YMM4 4.56.1.0");
        TimelineFrameCache.GpuRetentionEnabled = false; // Baseline modes remain byte-store hits.
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
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1 };
            item.X.SetFirst(-600 + frame * 12.25); item.Y.SetFirst(frame % 7 * 20 - 70);
            item.Opacity.SetFirst(43);
            return item;
        }));
        timeline.Items = timeline.Items.Add(new TextItem { Frame = 0, Length = Frames, Layer = 1, Text = "Preview cache measurement", Font = "Arial" });
        var scene = new Scene(timeline, scenes, []);
        ITimelineSource? source = null;
        try
        {
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            source = CreateSource(host, context, scene);
            var viewport = new TimelineFrameCache.PreviewViewport(Width, Height, Matrix3x2.Identity,
                new Vector2(Width / 2f, Height / 2f), 96, 96, target.PixelFormat,
                dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
                scene.ID, timeline.ID, Stopwatch.GetTimestamp(), true);
            TimelineFrameCache.TestViewport = value => ReferenceEquals(value, source) ? viewport : null;

            void Update(int frame) => source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
            void Draw() => DrawView(dc, target, viewport, source);

            TimelineFrameCache.SetEnabled(true, false);
            Check(WarmUntilStored(source, () => { Update(Frames - 1); Draw(); }, TimeSpan.FromSeconds(30)),
                "Warmup never stored a frame: " + TimelineFrameCache.Status);
            // JIT and frame-specific model keys are warmed independently of the measured cold store.
            for (int frame = 0; frame < Frames; frame++) { Update(frame); Draw(); }
            TimelineFrameCache.CompletePendingStore(source);
            TimelineFrameCache.Clear();

            object Measure(string mode, bool enabled, Func<int, int>? sequence = null) =>
                MeasureCore("base", timeline, source, Draw, mode, enabled, sequence);

            var measurements = new List<object> { Measure("off", false) };
            // Stored misses drawn once (the player blits the pixels drawn for the store) and, as before, twice;
            // alternated in this run so that the runner's speed does not decide the comparison.
            measurements.AddRange(MeasureColdPair("base", timeline, source, Draw));
            measurements.AddRange(MeasureColdPair("base", timeline, source, Draw, WaitForGpu(target)));
            // A stored miss shows the pixels drawn for the store: equal to the host's own render, also at a fractional
            // view offset. A Draw with another view (zoom, pan, resize) shows the host's output again.
            var plainView = viewport;
            foreach (var view in new[] { plainView, plainView with { Transform = Matrix3x2.CreateTranslation(1.25f, -.75f) } })
            {
                viewport = view;
                CheckDrawnOnce(dc, source, Update, view, 4, "base");
            }
            viewport = plainView with { Transform = Matrix3x2.CreateScale(.5f) * Matrix3x2.CreateTranslation(13, 7) };
            var zoomedView = viewport;
            TimelineFrameCache.SetEnabled(false, false); Update(5);
            var zoomedRender = TimelineFrameCache.CapturePreview(dc, source.Output, zoomedView)!;
            viewport = plainView;
            TimelineFrameCache.SetEnabled(true, false); TimelineFrameCache.Clear();
            long dropped = TimelineFrameCache.ShownCopiesDropped;
            UpdateShownOnce(source, 5, () => Update(5));
            TimelineFrameCache.ObserveDrawView(source, plainView with { LastDrawTimestamp = 1, IsPlaying = false });
            Check(TimelineFrameCache.ShownCopiesDropped == dropped, "A Draw with the same view dropped the drawn-once copy");
            TimelineFrameCache.ObserveDrawView(source, zoomedView);
            Check(TimelineFrameCache.ShownCopiesDropped == dropped + 1, "A Draw with another view kept the drawn-once copy");
            Check(zoomedRender.SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, zoomedView)!),
                "After a view change the host's output was not shown");
            TimelineFrameCache.CompletePendingStore(source);
            Console.WriteLine("Drawn once: stored misses show their stored pixels (8 frames, fractional offset), a view change shows the host's output again");
            // Nonblocking cold playback may skip captures while a previous GPU copy is busy. Prime all
            // frames explicitly outside the measured loop before asserting a 100% RAM-hit workload.
            for (int frame = 0; frame < Frames; frame++)
            {
                Update(frame); Draw(); TimelineFrameCache.CompletePendingStore(source);
            }
            measurements.Add(Measure("ram-hit", true));
            CacheTrace.Start(Path.GetFullPath("dist/performance-trace.jsonl"), "ram-hit-trace");
            ProcessingTraceHooks.Start();
            ProcessingTraceHooks.Discover(); // Installation cost is outside the measured loop.
            try { measurements.Add(Measure("ram-hit-trace", true)); }
            finally { ProcessingTraceHooks.Stop(); CacheTrace.StopAsync().GetAwaiter().GetResult(); }
            CheckRestoreTrace(Path.GetFullPath("dist/performance-trace.jsonl"));
            // Every frame, with no captures in the preceding measurements.
            for (int frame = 0; frame < Frames; frame++)
            {
                TimelineFrameCache.SetEnabled(false, false); Update(frame);
                var baseline = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                TimelineFrameCache.SetEnabled(true, false); Update(frame);
                var cached = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                Check(baseline.SequenceEqual(cached), $"Frame {frame} pixel parity failed");
            }
            // Counterbalanced hot-set comparison; retain the preceding 100-frame sequential baseline.
            const int HotFrames = 8;
            Func<int, int> hot = frame => frame % HotFrames;
            long oldGpuBudget = TimelineFrameCache.GpuRetentionBudget;
            TimelineFrameCache.GpuRetentionBudget = HotFrames * (long)Width * Height * 4;
            try
            {
                for (int pass = 0; pass < 6; pass++)
                {
                    bool gpu = pass is 1 or 2 or 5;
                    TimelineFrameCache.GpuRetentionEnabled = gpu;
                    for (int frame = 0; frame < HotFrames; frame++) { Update(frame); Draw(); }
                    measurements.Add(Measure((gpu ? "gpu-hit-hot-" : "ram-hit-hot-") + pass, true, hot));
                }
                CacheTrace.Start(Path.GetFullPath("dist/gpu-retention-trace.jsonl"), "gpu-hot-eight-frames");
                try { measurements.Add(Measure("gpu-hit-trace", true, hot)); }
                finally { CacheTrace.StopAsync().GetAwaiter().GetResult(); }
                var gpuRows = File.ReadLines("dist/gpu-retention-trace.jsonl").Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).ToArray();
                Check(gpuRows.Count(row => row.GetProperty("Kind").GetString() == "span"
                    && row.GetProperty("Stage").GetString() == "timeline-update"
                    && row.GetProperty("Outcome").GetString() == "gpu") == Frames, "GPU route was not traced");
                Check(!gpuRows.Any(row => row.GetProperty("Kind").GetString() == "span"
                    && row.GetProperty("Stage").GetString() == "restore-copy-from-memory"), "GPU hits copied pixels again");
                var gpuFooter = gpuRows.Single(row => row.GetProperty("Kind").GetString() == "summary");
                Check(gpuFooter.GetProperty("Dropped").GetInt64() == 0 && gpuFooter.GetProperty("OpenSpans").GetInt64() == 0,
                    "GPU trace was incomplete");
                long frameBytes = (long)Width * Height * 4;
                Check(TimelineFrameCache.GpuRetainedBytes == HotFrames * frameBytes, "GPU budget/alias accounting failed");
                var gpuPixels = new byte[HotFrames][];
                for (int frame = 0; frame < HotFrames; frame++)
                {
                    Update(frame); gpuPixels[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                }
                TimelineFrameCache.SetEnabled(false, false);
                for (int frame = 0; frame < HotFrames; frame++)
                {
                    Update(frame);
                    Check(gpuPixels[frame].SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!),
                        "GPU-resident pixel parity failed: " + frame);
                }
                TimelineFrameCache.SetEnabled(true, false);
                for (int frame = 0; frame < HotFrames; frame++) Update(frame);
                Update(0); // active borrower of frame 0
                var beforeEviction = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                TimelineFrameCache.GpuRetentionBudget = 0;
                Check(TimelineFrameCache.GpuRetainedBytes == 0 && TimelineFrameCache.GpuBytes == frameBytes,
                    "Eviction lost or double-counted the active borrower");
                Check(beforeEviction.SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!),
                    "Eviction disposed a borrowed output");
                Update(1);
                TimelineFrameCache.GpuRetentionBudget = oldGpuBudget;
                for (int frame = 0; frame < HotFrames; frame++) Update(frame);
                // A budget lowered by GpuMemoryController (a timer thread) releases frames on the render thread, at its next Update.
                long retainedBefore = TimelineFrameCache.GpuRetainedBytes;
                Check(retainedBefore > 0, "Frames were not retained again before the deferred budget change");
                TimelineFrameCache.SetGpuRetentionBudgetDeferred(0);
                Check(TimelineFrameCache.GpuRetainedBytes == retainedBefore, "A deferred budget change released frames off the render thread");
                Update(2);
                Check(TimelineFrameCache.GpuRetainedBytes == 0, "The next Update did not apply the lowered budget");
                TimelineFrameCache.GpuRetentionBudget = oldGpuBudget;
                for (int frame = 0; frame < HotFrames; frame++) Update(frame);
                long priorGpuHits = TimelineFrameCache.GpuHits;
                var previousViewport = viewport;
                viewport = viewport with { Transform = Matrix3x2.CreateTranslation(1.25f, -.75f) };
                Update(0);
                Check(TimelineFrameCache.GpuHits == priorGpuHits, "Changed viewport reused stale GPU output");
                var changedView = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                TimelineFrameCache.SetEnabled(false, false); Update(0);
                Check(changedView.SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!), "Changed viewport parity failed");
                viewport = previousViewport;
                TimelineFrameCache.SetEnabled(true, false);
                Update(0); Update(1); Update(0);
                priorGpuHits = TimelineFrameCache.GpuHits;
                timeline.Items.OfType<ShapeItem>().First().X.SetFirst(-350);
                Update(0);
                Check(TimelineFrameCache.GpuHits == priorGpuHits, "Model edit reused stale GPU output");
                var edited = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
                TimelineFrameCache.SetEnabled(false, false); Update(0);
                Check(edited.SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!), "Edited model parity failed");
                TimelineFrameCache.SetEnabled(true, false); Update(0); Update(1);
                TimelineFrameCache.Clear();
                priorGpuHits = TimelineFrameCache.GpuHits;
                Update(0);
                Check(TimelineFrameCache.GpuHits == priorGpuHits && TimelineFrameCache.GpuRetainedBytes == 0,
                    "Purge retained or reused an old generation");
                // Model edit/purge can leave background dependency capture unsettled. Warm the disposal
                // fixture explicitly rather than assuming four immediate updates produced byte-store hits.
                var disposalWarmup = Stopwatch.StartNew();
                while (TimelineFrameCache.GpuRetainedBytes == 0 && disposalWarmup.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Update(0); Draw(); TimelineFrameCache.CompletePendingStore(source);
                    Update(1); Draw(); TimelineFrameCache.CompletePendingStore(source);
                    Thread.Sleep(10);
                }
                Check(TimelineFrameCache.GpuRetainedBytes > 0, "Disposal fixture did not retain GPU frames: " + TimelineFrameCache.Status);
                source.Dispose(); source = null;
                Check(TimelineFrameCache.GpuRetainedBytes == 0 && TimelineFrameCache.GpuBytes == 0,
                    "Source disposal leaked retained or borrowed GPU images");
                Console.WriteLine("GPU retention: 8-frame pixel parity, active-borrow eviction, deferred budget change, viewport/edit/purge invalidation and source disposal OK");
                var vram = GpuMemoryController.Probe();
                Check(vram is not null, "The render path did not report its adapter for the VRAM budget");
                Console.WriteLine(vram!.Value.Sample is { } sample
                    ? $"VRAM sample ({vram.Value.Name}): budget {sample.Budget >> 20} MiB, usage {sample.CurrentUsage >> 20} MiB, dedicated {sample.DedicatedVideoMemory >> 20} MiB, software {sample.Software}"
                    : $"VRAM sample ({vram.Value.Name}): unavailable");
            }
            finally { TimelineFrameCache.GpuRetentionEnabled = false; TimelineFrameCache.GpuRetentionBudget = oldGpuBudget; }
            var effectsMeasurements = MeasureEffectsFixture(host, context, dc, target, viewport, root);
            var filesMeasurements = MeasureFilesFixture(host, harmony, context, dc, target, viewport, root);
            var report = new { HostVersion = host.GetName().Version!.ToString(), HostMvid = host.ManifestModule.ModuleVersionId,
                HostSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(host.Location))),
                Adapter = description.Description, description.VendorId, description.DeviceId, Driver = driver,
                Frames, Width, Height, HotWorkingSetFrames = 8, CounterbalancedHotPasses = 6, DiskEnabled = false, PixelParityFrames = Frames,
                TimingScope = "Real TimelineSource.Update + stand-in source-only Draw; no GUI/audio/Present/pacing. Final readback flush separate. Cache OFF still has measurement hooks.",
                Measurements = measurements, EffectsFixture = effectsMeasurements, FilesFixture = filesMeasurements };
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
        Check(TimelineFrameCache.ReadbackPoolBytes == 0, "Performance fixture leaked pooled readback textures");
    }


    // One measured pass of `Frames` Update+Draw pairs on `source`. Prints a PERF line (parsed from CI logs) with
    // per-frame wall time percentiles and the plugin's stage percentiles (p50/p95/mean, ms).
    // between: runs after each measured frame, outside its time (WaitForGpu).
    private static object MeasureCore(string fixture, Timeline timeline, ITimelineSource source, Action draw, string mode, bool enabled,
        Func<int, int>? sequence = null, bool checkCounts = true, Action? between = null)
    {
        TimelineFrameCache.SetEnabled(enabled, false);
        // Each pass starts from a collected heap: frames stored by earlier passes (8 MB each) are not collected, or
        // paged out on a small runner, during this one. Collections caused by this pass's own frames still count.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        PreviewPerformance.Reset();
        long hits = TimelineFrameCache.RamHits, gpuHits = TimelineFrameCache.GpuHits, stored = TimelineFrameCache.PreviewStored;
        long drawnOnce = TimelineFrameCache.DrawnOnce, busy = TimelineFrameCache.ReadbackBusySkips;
        long created = TimelineFrameCache.ReadbackTexturesCreated;
        var pauses = GC.GetTotalPauseDuration();
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var perFrame = new long[Frames];
        long opens = FileDependencyLease.FileOpens;
        var wall = Stopwatch.StartNew();
        for (int frame = 0; frame < Frames; frame++)
        {
            long started = PreviewPerformance.Timestamp;
            source.Update(timeline.VideoInfo.GetTimeFrom(sequence?.Invoke(frame) ?? frame), TimelineSourceUsage.Playing);
            using (PreviewPerformance.Measure(PreviewStage.PreviewDraw)) draw();
            PreviewPerformance.End(PreviewStage.TotalPreview, started);
            perFrame[frame] = PreviewPerformance.Timestamp - started;
            between?.Invoke();
        }
        wall.Stop();
        opens = FileDependencyLease.FileOpens - opens;
        var flush = Stopwatch.StartNew();
        TimelineFrameCache.CompletePendingStore(source);
        flush.Stop();
        long gpuHitCount = TimelineFrameCache.GpuHits - gpuHits;
        long ramHits = TimelineFrameCache.RamHits - hits, saved = TimelineFrameCache.PreviewStored - stored;
        long once = TimelineFrameCache.DrawnOnce - drawnOnce;
        busy = TimelineFrameCache.ReadbackBusySkips - busy;
        created = TimelineFrameCache.ReadbackTexturesCreated - created;
        // A long frame is told apart from a GC pause and from paging (the working set) by these.
        double gcPause = (GC.GetTotalPauseDuration() - pauses).TotalMilliseconds;
        long workingSet = Environment.WorkingSet >> 20, pooled = TimelineFrameCache.ReadbackPoolBytes >> 20;
        var stages = PreviewPerformance.Snapshot();
        if (checkCounts)
        {
            Check(stages.Single(s => s.Stage == PreviewStage.TotalUpdate).SampleCount == Frames, mode + " did not measure all updates");
            Check(stages.Single(s => s.Stage == PreviewStage.TotalPreview).SampleCount == Frames, mode + " did not measure all previews");
            if (mode.StartsWith("cold-store", StringComparison.Ordinal))
            {
                Check(saved > 0 && saved <= Frames && ramHits == 0 && (TimelineFrameCache.DrawOnce ? once > 0 : once == 0),
                    $"Cold-store counts: saved={saved}, hits={ramHits}, drawn once={once}");
                // The source's pool reuses its readback textures: a pass creates a few, not two per stored frame.
                Check(created <= 2 * ReadbackSlots + 2, $"{mode} created {created} readback textures for {saved} stored frames");
            }
            // With the GPU done before each frame, every readback has completed by the next update.
            if (mode.StartsWith("cold-store", StringComparison.Ordinal) && mode.Contains("-paced", StringComparison.Ordinal))
                Check(saved == Frames && busy == 0, $"{mode}: stored {saved} of {Frames}, {busy} skipped with every readback busy");
            if (mode.StartsWith("ram-hit", StringComparison.Ordinal)) Check(ramHits == Frames && saved == 0, $"RAM-hit counts: hits={ramHits}, saved={saved}");
            if (mode.StartsWith("gpu-hit", StringComparison.Ordinal)) Check(gpuHitCount == Frames && ramHits == 0 && saved == 0,
                $"GPU-hit counts: gpu={gpuHitCount}, ram={ramHits}, saved={saved}");
            if (mode == "off") Check(saved == 0 && ramHits == 0, "OFF used the cache");
        }
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
        Console.WriteLine($"{mode}: {wall.Elapsed.TotalMilliseconds / Frames:F3} ms/frame, final flush {flush.Elapsed.TotalMilliseconds:F3} ms, GPU hits {gpuHitCount}, RAM hits {ramHits}, stored {saved}");
        string Stage(PreviewStage stage)
        {
            var row = stages.Single(s => s.Stage == stage);
            return row.SampleCount == 0 ? "-" : FormattableString.Invariant($"{row.P50Milliseconds:F3}/{row.P95Milliseconds:F3}/{row.MeanMilliseconds:F3}");
        }
        Console.WriteLine(FormattableString.Invariant($"PERF|{fixture}|{mode}|frame={FrameStats(perFrame)}|update={Stage(PreviewStage.TotalUpdate)}|key={Stage(PreviewStage.KeyGeneration)}")
            + FormattableString.Invariant($"|lookup={Stage(PreviewStage.CacheLookup)}|host={Stage(PreviewStage.HostRender)}|copy={Stage(PreviewStage.BeginGpuCopy)}|alloc={Stage(PreviewStage.CpuAllocation)}")
            + FormattableString.Invariant($"|memcpy={Stage(PreviewStage.CpuMemcpy)}|map={Stage(PreviewStage.MapWait)}|draw={Stage(PreviewStage.PreviewDraw)}|bytes={bytes}|gc={gc[0]}/{gc[1]}/{gc[2]}")
            + FormattableString.Invariant($"|gpuhits={gpuHitCount}|ramhits={ramHits}|stored={saved}|opens/frame={opens / (double)Frames:F1}|once={once}|busy={busy}|created={created}|gcpause={gcPause:F1}|ws={workingSet}|pool={pooled}"));
        return new { Fixture = fixture, Mode = mode, WallMilliseconds = wall.Elapsed.TotalMilliseconds, FinalFlushMilliseconds = flush.Elapsed.TotalMilliseconds,
            AllocatedBytes = bytes, GcCollections = gc, GpuHits = gpuHitCount, RamHits = ramHits, Stored = saved, DrawnOnce = once, ReadbackBusy = busy, TexturesCreated = created, GcPauseMilliseconds = gcPause, WorkingSetMiB = workingSet, PoolMiB = pooled,
            FileOpensPerFrame = opens / (double)Frames, FrameMilliseconds = perFrame.Select(Milliseconds).ToArray(), Stages = stages };
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // p50/p95/p99/max/mean of per-frame wall times (ms).
    private static string FrameStats(long[] ticks)
    {
        var sorted = ticks.Select(Milliseconds).Order().ToArray();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return FormattableString.Invariant($"{At(.5):F3}/{At(.95):F3}/{At(.99):F3}/{sorted[^1]:F3}/{sorted.Average():F3}");
    }

    // A real host TimelineSource for `scene`, without item rects (the player asks for them only around a selection).
    private static ITimelineSource CreateSource(Assembly host, object context, Scene scene)
    {
        var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;
        source.GetType().GetProperty("NeedTimelineItemRects")!.SetValue(source, false);
        return source;
    }

    // The stand-in for the player's Draw: a black target and the source's output drawn for the view.
    private static void DrawView(ID2D1DeviceContext dc, ID2D1Bitmap1 target, TimelineFrameCache.PreviewViewport viewport, ITimelineSource source)
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

    // Renders until the first frame is stored (a large project is described and its files hashed first).
    private static bool WarmUntilStored(ITimelineSource source, Action updateAndDraw, TimeSpan limit)
    {
        long warmed = TimelineFrameCache.PreviewStored;
        var warmup = Stopwatch.StartNew();
        while (TimelineFrameCache.PreviewStored == warmed && warmup.Elapsed < limit)
        {
            updateAndDraw(); TimelineFrameCache.CompletePendingStore(source); Thread.Sleep(10);
        }
        return TimelineFrameCache.PreviewStored > warmed;
    }

    // For each of the first `frames` frames: the host's render with the cache off, then a stored miss after a purge,
    // which shows the pixels drawn for the store; both must be equal.
    private static void CheckDrawnOnce(ID2D1DeviceContext dc, ITimelineSource source, Action<int> update,
        TimelineFrameCache.PreviewViewport viewport, int frames, string fixture)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            TimelineFrameCache.SetEnabled(false, false); update(frame);
            var rendered = TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!;
            TimelineFrameCache.SetEnabled(true, false); TimelineFrameCache.Clear();
            UpdateShownOnce(source, frame, () => update(frame));
            Check(rendered.SequenceEqual(TimelineFrameCache.CapturePreview(dc, source.Output, viewport)!),
                $"Drawn-once {fixture} frame {frame} pixel parity failed");
            TimelineFrameCache.CompletePendingStore(source);
        }
    }

    // A miss of `frame` after a purge, shown from the pixels drawn for its store. An update right after the purge can
    // still render normally while the tracker settles; the next one stores.
    private static void UpdateShownOnce(ITimelineSource source, int frame, Action update)
    {
        long once = TimelineFrameCache.DrawnOnce;
        var wait = Stopwatch.StartNew();
        update();
        while (TimelineFrameCache.DrawnOnce == once && wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
            TimelineFrameCache.CompletePendingStore(source);
            update();
        }
        Check(TimelineFrameCache.DrawnOnce == once + 1, $"Stored miss {frame} was not shown from its stored pixels: {TimelineFrameCache.Status}");
    }

    // Waits until the GPU has run everything submitted so far (on WARP: rasterized it), as a GPU faster than the
    // frame rate would be done before the next frame. Used between measured frames.
    private static Action WaitForGpu(ID2D1Bitmap1 target) => () =>
    {
        using var surface = target.Surface;
        using var texture = surface!.QueryInterface<ID3D11Texture2D>();
        using var device = texture.Device;
        using var query = device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
        using var immediate = device.ImmediateContext;
        immediate.End(query);
        immediate.Flush();
        while (immediate.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0) Thread.Yield();
    };

    // Cold playback with every frame stored, drawn once (cold-store) and twice as before (cold-store-twice), three
    // times each in balanced order. Each pass starts from an empty store. With `between` (WaitForGpu) the passes are
    // "-paced": the GPU finishes each frame before the next, so every readback completes in time.
    private static List<object> MeasureColdPair(string fixture, Timeline timeline, ITimelineSource source, Action draw, Action? between = null)
    {
        var measurements = new List<object>();
        string paced = between is null ? "" : "-paced";
        try
        {
            for (int pass = 0; pass < 6; pass++)
            {
                // once, twice, twice, once, once, twice: each variant runs first in a pair and later in a run as often.
                bool once = pass is 0 or 3 or 4;
                TimelineFrameCache.CompletePendingStore(source);
                TimelineFrameCache.Clear();
                TimelineFrameCache.DrawOnce = once;
                string name = (once ? "cold-store" : "cold-store-twice") + paced + (pass / 2 == 0 ? "" : "-" + (pass / 2 + 1));
                measurements.Add(MeasureCore(fixture, timeline, source, draw, name, true, between: between));
            }
        }
        finally { TimelineFrameCache.DrawOnce = true; }
        return measurements;
    }

    // Effects that dominate the frame (large blurred shapes, blurred text): what drawing a stored frame once instead
    // of twice saves where the composition is expensive. Also checks the drawn-once pixels against the host's render.
    private static object MeasureEffectsFixture(Assembly host, object context, ID2D1DeviceContext dc, ID2D1Bitmap1 target,
        TimelineFrameCache.PreviewViewport baseViewport, string root)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).SelectMany(frame => Enumerable.Range(0, 3).Select(layer =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1, Layer = layer };
            item.X.SetFirst(-500 + frame * 10 + layer * 160); item.Y.SetFirst(layer * 120 - 120);
            item.Zoom.SetFirst(600);
            item.Opacity.SetFirst(70);
            item.VideoEffects = item.VideoEffects.Add(new YukkuriMovieMaker.Project.Effects.GaussianBlurEffect());
            return (IItem)item;
        })));
        var text = new TextItem { Frame = 0, Length = Frames, Layer = 3, Text = "Effects measurement", Font = "Arial" };
        text.VideoEffects = text.VideoEffects.Add(new YukkuriMovieMaker.Project.Effects.GaussianBlurEffect());
        timeline.Items = timeline.Items.Add(text);
        var scene = new Scene(timeline, scenes, []);
        var source = CreateSource(host, context, scene);
        var viewport = baseViewport with { SceneId = scene.ID, TimelineId = timeline.ID, LastDrawTimestamp = Stopwatch.GetTimestamp() };
        // RAM for 16 frames: the cold passes only store (older frames are evicted), nothing is read back.
        var store = new FrameCacheStore(Path.Combine(root, "effects-store"), 16 * ((long)Width * Height * 4 + 32), 0);
        TimelineFrameCache.UseStore(store);
        TimelineFrameCache.TestViewport = value => ReferenceEquals(value, source) ? viewport : null;
        var measurements = new List<object>();
        try
        {
            void Update(int frame) => source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
            void Draw() => DrawView(dc, target, viewport, source);
            // Background description (> 200 items) before the first store; then JIT and per-frame keys.
            TimelineFrameCache.SetEnabled(true, false);
            if (!WarmUntilStored(source, () => { Update(Frames - 1); Draw(); }, TimeSpan.FromSeconds(60)))
            {
                Console.WriteLine("PERF|effects|skipped|" + TimelineFrameCache.Status);
                return measurements;
            }
            for (int frame = 0; frame < Frames; frame++) { Update(frame); Draw(); }
            TimelineFrameCache.CompletePendingStore(source);
            CheckDrawnOnce(dc, source, Update, viewport, 4, "effects");
            TimelineFrameCache.Clear();
            measurements.Add(MeasureCore("effects", timeline, source, Draw, "off-paced", false, between: WaitForGpu(target)));
            // Unpaced, WARP queues blurred frames faster than it renders them and stalls for seconds at a time.
            measurements.AddRange(MeasureColdPair("effects", timeline, source, Draw, WaitForGpu(target)));
            TimelineFrameCache.CompletePendingStore(source);
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            source.Dispose();
            store.Dispose();
        }
        return measurements;
    }

    // A project closer to real ones: every frame depends on many material files (voice/BGM/SE files are frame
    // dependencies of the cache key) and the project has more than 200 items, so that the preview describes it in
    // the background as it does for large projects. Also measures the idle pre-renderer's per-frame work on its own
    // source and the cost of the hooks themselves (cache off with hooks vs. no hooks).
    private static object MeasureFilesFixture(Assembly host, Harmony harmony, object context, ID2D1DeviceContext dc, ID2D1Bitmap1 target,
        TimelineFrameCache.PreviewViewport baseViewport, string root)
    {
        const int AudioFiles = 24, ExtraShapes = 120;
        string folder = Path.Combine(root, "materials");
        Directory.CreateDirectory(folder);
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1 };
            item.X.SetFirst(-600 + frame * 12.25); item.Y.SetFirst(frame % 7 * 20 - 70);
            item.Opacity.SetFirst(43);
            return (IItem)item;
        }));
        var extraShapes = Enumerable.Range(0, ExtraShapes).Select(i =>
        {
            var item = new ShapeItem { Frame = i % Frames, Length = 1, Layer = 2 };
            item.Y.SetFirst(200 + i % 5 * 10);
            return item;
        }).ToArray();
        timeline.Items = timeline.Items.AddRange(extraShapes);
        var text = new TextItem { Frame = 0, Length = Frames, Layer = 3, Text = "Many material files", Font = "Arial" };
        timeline.Items = timeline.Items.Add(text);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, AudioFiles).Select(i =>
        {
            string file = Path.Combine(folder, $"voice{i:00}.wav");
            WriteSilence(file, i);
            return (IItem)new AudioItem { FilePath = file, Frame = 0, Length = Frames, Layer = 10 + i };
        }));
        var scene = new Scene(timeline, scenes, []);
        // The original fixture puts 120 one-frame shapes on the same layer across 100 frames.
        // Frames 0..19 therefore have ambiguous host resource ordering and must bypass.
        // Keep that regression explicit; use distinct layers for the fully cacheable benchmark.
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        Check(FrameCacheKey.TryDescribe(scene, readers, out _, out _, out var ambiguous, out var reason), reason);
        for (int frame = 0; frame < Frames; frame++)
            Check(ambiguous!.For(frame).Cacheable == (frame >= ExtraShapes - Frames),
                $"Files fixture ambiguous-order eligibility mismatch at frame {frame}");
        for (int i = 0; i < extraShapes.Length; i++) extraShapes[i].Layer = 2 + i / Frames;
        text.Layer = 4;
        Check(FrameCacheKey.TryDescribe(scene, readers, out _, out _, out var ordered, out reason), reason);
        Check(Enumerable.Range(0, Frames).All(frame => ordered!.For(frame).Cacheable),
            "Files benchmark must have a certified draw order for every frame");
        Console.WriteLine("Files fixture: original 20 ambiguous frames bypass; ordered benchmark certifies all 100 frames.");
        ITimelineSource Create() => CreateSource(host, context, scene);
        var viewport = baseViewport with { SceneId = scene.ID, TimelineId = timeline.ID, LastDrawTimestamp = Stopwatch.GetTimestamp() };
        var store = new FrameCacheStore(Path.Combine(root, "files-store"), Frames * ((long)Width * Height * 4 + 32) + (1L << 20), 0);
        TimelineFrameCache.UseStore(store);
        var measurements = new List<object>();
        ITimelineSource? live = null;
        try
        {
            live = Create();
            var shown = live;
            TimelineFrameCache.TestViewport = value => ReferenceEquals(value, shown) ? viewport : null;
            void Draw(ITimelineSource from) => DrawView(dc, target, viewport, from);
            void Update(ITimelineSource on, int frame) => on.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);

            // Background description (> 200 items) and fingerprinting of the files happen before the first store.
            TimelineFrameCache.SetEnabled(true, false);
            Check(WarmUntilStored(live, () => { Update(live, Frames - 1); Draw(live); }, TimeSpan.FromSeconds(60)),
                "Files fixture warmup never stored a frame: " + TimelineFrameCache.Status);
            for (int frame = 0; frame < Frames; frame++) { Update(live, frame); Draw(live); }
            TimelineFrameCache.CompletePendingStore(live);
            TimelineFrameCache.Clear();

            measurements.Add(MeasureCore("files24", timeline, live, () => Draw(live), "off", false));
            measurements.AddRange(MeasureColdPair("files24", timeline, live, () => Draw(live)));
            measurements.AddRange(MeasureColdPair("files24", timeline, live, () => Draw(live), WaitForGpu(target)));
            for (int frame = 0; frame < Frames; frame++) { Update(live, frame); Draw(live); TimelineFrameCache.CompletePendingStore(live); }
            measurements.Add(MeasureCore("files24", timeline, live, () => Draw(live), "ram-hit", true));
            const int HotFrames = 8;
            long oldBudget = TimelineFrameCache.GpuRetentionBudget;
            TimelineFrameCache.GpuRetentionBudget = HotFrames * (long)Width * Height * 4;
            try
            {
                TimelineFrameCache.GpuRetentionEnabled = true;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int frame = 0; frame < HotFrames; frame++) { Update(live, frame); Draw(live); }
                    measurements.Add(MeasureCore("files24", timeline, live, () => Draw(live), "gpu-hit-hot-" + pass, true, frame => frame % HotFrames));
                }
            }
            finally { TimelineFrameCache.GpuRetentionEnabled = false; TimelineFrameCache.GpuRetentionBudget = oldBudget; }

            // The idle pre-renderer's batches, driven through its own code (IdleFramePreRenderer.PrimeFrame and
            // CreateBatchSource) as RenderBatch does: per batch of 30 a clone of the live model with its own tracker and
            // renderer, per frame the live/clone keys, the stored check, the clone's Update and the synchronous prime.
            // Not included: the UI timer, input checks and the gap between batches.
            TimelineFrameCache.Clear();
            TimelineFrameCache.SetEnabled(true, false);
            measurements.Add(MeasureIdleBatches(scene, timeline, viewport with { IsPlaying = false, LastDrawTimestamp = Stopwatch.GetTimestamp() }));
            live.Dispose(); live = null;

            // Hooks with every cache off vs. no hooks at all (plugin not installed), on fresh sources.
            TimelineFrameCache.SetEnabled(false, false);
            long[] Loop(ITimelineSource on)
            {
                var ticks = new long[Frames];
                for (int frame = 0; frame < Frames; frame++)
                {
                    long started = Stopwatch.GetTimestamp();
                    Update(on, frame); Draw(on);
                    ticks[frame] = Stopwatch.GetTimestamp() - started;
                }
                return ticks;
            }
            long[] Passes(ITimelineSource on)
            {
                Loop(on); // warm
                return Enumerable.Range(0, 3).SelectMany(_ => Loop(on)).ToArray();
            }
            using (var hooked = Create())
            {
                var ticks = Passes(hooked);
                Console.WriteLine($"PERF|files24|hooks-off|frame={FrameStats(ticks)}");
                measurements.Add(new { Fixture = "files24", Mode = "hooks-off", FrameMilliseconds = ticks.Select(Milliseconds).ToArray() });
            }
            FrameRenderReadiness.Uninstall(harmony);
            harmony.UnpatchAll(harmony.Id);
            using (var bare = Create())
            {
                var ticks = Passes(bare);
                Console.WriteLine($"PERF|files24|no-hooks|frame={FrameStats(ticks)}");
                measurements.Add(new { Fixture = "files24", Mode = "no-hooks", FrameMilliseconds = ticks.Select(Milliseconds).ToArray() });
            }
        }
        finally
        {
            live?.Dispose();
            TimelineFrameCache.TestViewport = null;
            store.Dispose();
        }
        return measurements;
    }

    private static object MeasureIdleBatches(Scene scene, Timeline timeline, TimelineFrameCache.PreviewViewport view)
    {
        const int Batch = 30;
        using var liveTracker = new KeyDependencyTracker(scene); // the idle session's tracker of the live scene
        var ready = Stopwatch.StartNew();
        KeyCapture? probe;
        while (!liveTracker.TryCapture(0, out probe, out _) && ready.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(20);
        Check(probe is not null, "Idle fixture: the live tracker never keyed frame 0");
        probe!.Dispose();
        var ticks = new long[Frames];
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long opens = FileDependencyLease.FileOpens;
        var wall = Stopwatch.StartNew();
        // As RenderBatch: one renderer (clone, tracker, TimelineSourceAndDevices) for consecutive batches of one model.
        IdleFramePreRenderer.BatchRenderer? batch = null;
        int renderers = 0;
        try
        {
            for (int start = 0; start < Frames; start += Batch)
            {
                long batchStarted = Stopwatch.GetTimestamp();
                Check(liveTracker.TryCapture(start, out var initial, out var why), "Idle fixture: " + why);
                using (initial)
                    if (!IdleFramePreRenderer.Reusable(batch, liveTracker, initial!.Model))
                    {
                        batch?.Dispose();
                        batch = new IdleFramePreRenderer.BatchRenderer(liveTracker, initial.Model);
                        renderers++;
                    }
                var current = batch!;
                for (int frame = start; frame < Math.Min(Frames, start + Batch); frame++)
                {
                    long started = frame == start ? batchStarted : Stopwatch.GetTimestamp();
                    var result = IdleFramePreRenderer.PrimeFrame(liveTracker, scene, current.CloneTracker, current.CloneScene, current.Source,
                        time => current.Source.Update(time, TimelineSourceUsage.Playing), frame, view, () => true, CancellationToken.None, out _);
                    outcomes[result.ToString()] = outcomes.GetValueOrDefault(result.ToString()) + 1;
                    ticks[frame] = Stopwatch.GetTimestamp() - started;
                }
            }
        }
        finally { batch?.Dispose(); }
        wall.Stop();
        Check(renderers == 1, $"Idle batches of one model created {renderers} renderers");
        opens = FileDependencyLease.FileOpens - opens;
        string summary = string.Join(",", outcomes.Select(pair => $"{pair.Key}={pair.Value}"));
        Console.WriteLine(FormattableString.Invariant($"PERF|files24|idle-batches|frame={FrameStats(ticks)}|fps={Frames * 1000.0 / wall.Elapsed.TotalMilliseconds:F1}|opens/frame={opens / (double)Frames:F1}|renderers={renderers}|{summary}"));
        return new { Fixture = "files24", Mode = "idle-batches", WallMilliseconds = wall.Elapsed.TotalMilliseconds, Outcomes = outcomes,
            FileOpensPerFrame = opens / (double)Frames, FrameMilliseconds = ticks.Select(Milliseconds).ToArray() };
    }

    // A short, valid 16-bit PCM WAV (distinct content per file).
    private static void WriteSilence(string path, int seed)
    {
        const int SampleRate = 48000, Samples = 4800;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + Samples * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(Samples * 2);
        for (int i = 0; i < Samples; i++) writer.Write((short)(i == 0 ? seed : 0));
    }

    private static void CheckRestoreTrace(string path)
    {
        var records = File.ReadLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
        var footer = records.Single(row => row.GetProperty("Kind").GetString() == "summary");
        Check(footer.GetProperty("Dropped").GetInt64() == 0 && footer.GetProperty("OpenSpans").GetInt64() == 0,
            "Performance trace was incomplete");
        var spans = records.Where(row => row.GetProperty("Kind").GetString() == "span")
            .ToDictionary(row => row.GetProperty("Id").GetInt64());
        var restores = spans.Values.Where(row => row.GetProperty("Stage").GetString() == "CacheRestore").ToArray();
        Check(restores.Length == Frames, "Missing restore parent spans");
        foreach (var stage in new[] { "restore-bitmap-allocation", "restore-copy-from-memory", "restore-command-recording",
            "cache-output-lock-wait", "cache-output-commit" })
        {
            var children = spans.Values.Where(row => row.GetProperty("Stage").GetString() == stage).ToArray();
            Check(children.Length == Frames, "Missing detailed restore spans: " + stage);
            foreach (var child in children)
            {
                var parent = spans[child.GetProperty("ParentId").GetInt64()];
                Check(parent.GetProperty("Stage").GetString() == "CacheRestore"
                    && child.GetProperty("OperationId").GetInt64() == parent.GetProperty("OperationId").GetInt64()
                    && child.GetProperty("StartTicks").GetInt64() >= parent.GetProperty("StartTicks").GetInt64()
                    && child.GetProperty("EndTicks").GetInt64() <= parent.GetProperty("EndTicks").GetInt64(),
                    "Detailed restore span attribution failed: " + stage);
            }
        }
        Console.WriteLine("Detailed restore timing: 100 frames, complete and correctly nested");
    }

    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
