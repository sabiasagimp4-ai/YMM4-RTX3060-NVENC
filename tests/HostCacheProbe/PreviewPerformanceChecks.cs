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

            object Measure(string mode, bool enabled, Func<int, int>? sequence = null) =>
                MeasureCore("base", timeline, source, Draw, mode, enabled, sequence);

            var measurements = new List<object> { Measure("off", false), Measure("cold-store", true) };
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
                timeline.Items.OfType<ShapeItem>().First().X.SetFirstValue(-350);
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
                Console.WriteLine("GPU retention: 8-frame pixel parity, active-borrow eviction, viewport/edit/purge invalidation and source disposal OK");
            }
            finally { TimelineFrameCache.GpuRetentionEnabled = false; TimelineFrameCache.GpuRetentionBudget = oldGpuBudget; }
            var filesMeasurements = MeasureFilesFixture(host, harmony, context, dc, target, viewport, root);
            var report = new { HostVersion = host.GetName().Version!.ToString(), HostMvid = host.ManifestModule.ModuleVersionId,
                HostSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(host.Location))),
                Adapter = description.Description, description.VendorId, description.DeviceId, Driver = driver,
                Frames, Width, Height, HotWorkingSetFrames = 8, CounterbalancedHotPasses = 6, DiskEnabled = false, PixelParityFrames = Frames,
                TimingScope = "Real TimelineSource.Update + stand-in source-only Draw; no GUI/audio/Present/pacing. Final readback flush separate. Cache OFF still has measurement hooks.",
                Measurements = measurements, FilesFixture = filesMeasurements };
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


    // One measured pass of `Frames` Update+Draw pairs on `source`. Prints a PERF line (parsed from CI logs) with
    // per-frame wall time percentiles and the plugin's stage percentiles (p50/p95/mean, ms).
    private static object MeasureCore(string fixture, Timeline timeline, ITimelineSource source, Action draw, string mode, bool enabled,
        Func<int, int>? sequence = null, bool checkCounts = true)
    {
        TimelineFrameCache.SetEnabled(enabled, false);
        PreviewPerformance.Reset();
        long hits = TimelineFrameCache.RamHits, gpuHits = TimelineFrameCache.GpuHits, stored = TimelineFrameCache.PreviewStored;
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
        }
        wall.Stop();
        opens = FileDependencyLease.FileOpens - opens;
        var flush = Stopwatch.StartNew();
        TimelineFrameCache.CompletePendingStore(source);
        flush.Stop();
        long gpuHitCount = TimelineFrameCache.GpuHits - gpuHits;
        long ramHits = TimelineFrameCache.RamHits - hits, saved = TimelineFrameCache.PreviewStored - stored;
        var stages = PreviewPerformance.Snapshot();
        if (checkCounts)
        {
            Check(stages.Single(s => s.Stage == PreviewStage.TotalUpdate).SampleCount == Frames, mode + " did not measure all updates");
            Check(stages.Single(s => s.Stage == PreviewStage.TotalPreview).SampleCount == Frames, mode + " did not measure all previews");
            if (mode == "cold-store") Check(saved > 0 && saved <= Frames && ramHits == 0, $"Cold-store counts: saved={saved}, hits={ramHits}");
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
            + FormattableString.Invariant($"|gpuhits={gpuHitCount}|ramhits={ramHits}|stored={saved}|opens/frame={opens / (double)Frames:F1}"));
        return new { Fixture = fixture, Mode = mode, WallMilliseconds = wall.Elapsed.TotalMilliseconds, FinalFlushMilliseconds = flush.Elapsed.TotalMilliseconds,
            AllocatedBytes = bytes, GcCollections = gc, GpuHits = gpuHitCount, RamHits = ramHits, Stored = saved,
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
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1 };
            item.X.SetFirstValue(-600 + frame * 12.25); item.Y.SetFirstValue(frame % 7 * 20 - 70);
            item.Opacity.SetFirstValue(43);
            return (IItem)item;
        }));
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, ExtraShapes).Select(i =>
        {
            var item = new ShapeItem { Frame = i % Frames, Length = 1, Layer = 2 };
            item.Y.SetFirstValue(200 + i % 5 * 10);
            return (IItem)item;
        }));
        timeline.Items = timeline.Items.Add(new TextItem { Frame = 0, Length = Frames, Layer = 3, Text = "Many material files", Font = "Arial" });
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, AudioFiles).Select(i =>
        {
            string file = Path.Combine(folder, $"voice{i:00}.wav");
            WriteSilence(file, i);
            return (IItem)new AudioItem { FilePath = file, Frame = 0, Length = Frames, Layer = 10 + i };
        }));
        var scene = new Scene(timeline, scenes, []);
        var sourceType = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
        ITimelineSource Create()
        {
            var created = (ITimelineSource)Activator.CreateInstance(sourceType, Instance, null, [context, scene, null], null)!;
            created.GetType().GetProperty("NeedTimelineItemRects")!.SetValue(created, false);
            return created;
        }
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
            void Draw(ITimelineSource from)
            {
                using var oldTarget = dc.Target;
                var transform = dc.Transform;
                try
                {
                    dc.Target = target; dc.Transform = viewport.Transform;
                    dc.BeginDraw(); dc.Clear(new Color4(0, 0, 0, 1));
                    dc.DrawImage(from.Output, viewport.TargetOffset);
                    dc.EndDraw().CheckError();
                }
                finally { dc.Target = oldTarget; dc.Transform = transform; }
            }
            void Update(ITimelineSource on, int frame) => on.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);

            // Background description (> 200 items) and fingerprinting of the files happen before the first store.
            TimelineFrameCache.SetEnabled(true, false);
            long warmed = TimelineFrameCache.PreviewStored;
            var warmup = Stopwatch.StartNew();
            while (TimelineFrameCache.PreviewStored == warmed && warmup.Elapsed < TimeSpan.FromSeconds(60))
            {
                Update(live, Frames - 1); Draw(live); TimelineFrameCache.CompletePendingStore(live); Thread.Sleep(10);
            }
            Check(TimelineFrameCache.PreviewStored > warmed, "Files fixture warmup never stored a frame: " + TimelineFrameCache.Status);
            for (int frame = 0; frame < Frames; frame++) { Update(live, frame); Draw(live); }
            TimelineFrameCache.CompletePendingStore(live);
            TimelineFrameCache.Clear();

            measurements.Add(MeasureCore("files24", timeline, live, () => Draw(live), "off", false));
            measurements.Add(MeasureCore("files24", timeline, live, () => Draw(live), "cold-store", true));
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
        for (int start = 0; start < Frames; start += Batch)
        {
            long batchStarted = Stopwatch.GetTimestamp();
            Check(liveTracker.TryCapture(start, out var initial, out var why), "Idle fixture: " + why);
            Scene cloneScene;
            using (initial) cloneScene = IdleFramePreRenderer.CloneSceneFromModel(initial!.Model);
            using var cloneTracker = new KeyDependencyTracker(cloneScene, liveTracker.VerifiedFingerprints);
            using var source = IdleFramePreRenderer.CreateBatchSource(cloneScene);
            for (int frame = start; frame < Math.Min(Frames, start + Batch); frame++)
            {
                long started = frame == start ? batchStarted : Stopwatch.GetTimestamp();
                var result = IdleFramePreRenderer.PrimeFrame(liveTracker, scene, cloneTracker, cloneScene, source,
                    time => source.Update(time, TimelineSourceUsage.Playing), frame, view, () => true, CancellationToken.None, out _);
                outcomes[result.ToString()] = outcomes.GetValueOrDefault(result.ToString()) + 1;
                ticks[frame] = Stopwatch.GetTimestamp() - started;
            }
        }
        wall.Stop();
        opens = FileDependencyLease.FileOpens - opens;
        string summary = string.Join(",", outcomes.Select(pair => $"{pair.Key}={pair.Value}"));
        Console.WriteLine(FormattableString.Invariant($"PERF|files24|idle-batches|frame={FrameStats(ticks)}|fps={Frames * 1000.0 / wall.Elapsed.TotalMilliseconds:F1}|opens/frame={opens / (double)Frames:F1}|{summary}"));
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

