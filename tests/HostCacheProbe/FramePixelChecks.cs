using System.Reflection;
using System.Numerics;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Player.Video;

internal static class FramePixelChecks
{
    internal static void Run(Assembly host, string? videoPath, HostFeatures features)
    {
        var bootstrap = new Harmony("ymm.tests.pixel-builtin-loader");
        var loader = typeof(PluginAssemblyLoader);
        bootstrap.Patch(loader.TypeInitializer!, prefix: new HarmonyMethod(typeof(FramePixelChecks), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using (var original = dc.CreateCommandList())
        {
            dc.Target = original;
            dc.BeginDraw();
            using var brush = dc.CreateSolidColorBrush(new Color4(0.8f, 0.3f, 0.7f, 0.37f));
            dc.FillRectangle(new Vortice.RawRectF(-81.25f, -40.75f, 82.125f, 43.5f), brush);
            dc.EndDraw().CheckError(); dc.Target = null; original.Close().CheckError();
            foreach (int width in new[] { 320, 321 })
            {
                const int height = 181;
                var half = new Vector2(width / 2f, height / 2f);
                var bounds = dc.GetImageLocalBounds(original);
                var origin = new Vector2(MathF.Floor(bounds.Left + half.X) - half.X, MathF.Floor(bounds.Top + half.Y) - half.Y);
                int fullWidth = (int)(MathF.Ceiling(bounds.Right + half.X) - half.X - origin.X);
                int fullHeight = (int)(MathF.Ceiling(bounds.Bottom + half.Y) - half.Y - origin.Y);
                var saved = TimelineFrameCache.Capture(dc, original, fullWidth, fullHeight, origin)!;
                using var uploaded = TimelineFrameCache.Upload(dc, saved);
                var baseline = TimelineFrameCache.Capture(dc, original, width, height, -half)!;
                var cached = TimelineFrameCache.Capture(dc, uploaded, width, height, -half)!;
                Check(baseline.SequenceEqual(cached), $"BGRA alpha/negative bounds/odd-size parity failed at width={width}");
            }
        }
        CheckLatePreviewTransformParity(dc);
        Console.WriteLine("GPU pixel parity: alpha edges, negative bounds, even/odd scene size OK");

        var harmony = new Harmony("ymm.tests.frame-cache");
        try
        {
            // The real plugin loader loads the built-in readers; this probe bypasses it, so load them here
            // (metadata only, no host code runs) so that readiness coverage matches the real host.
            foreach (var reader in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(host.Location)!, "YukkuriMovieMaker.Plugin.FileSource.*.dll"))
                if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == System.IO.Path.GetFileNameWithoutExtension(reader)))
                    System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(reader);
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            Console.WriteLine("Render readiness coverage (verify against host code):");
            foreach (var line in FrameRenderReadiness.Coverage) Console.WriteLine("  " + line);
            const string mediaFoundation = "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation";
            if (features.DecoderVerified(mediaFoundation))
                Check(FrameRenderReadiness.Coverage.Any(line => line.Contains(": MF2 (", StringComparison.Ordinal)),
                    "No MF2 video source was recognized; video frames would never be cached");
            var timeline = new Timeline();
            timeline.VideoInfo.Width = 321; timeline.VideoInfo.Height = 181;
            timeline.VideoInfo.SetBackground(System.Windows.Media.Color.FromArgb(137, 123, 76, 231));
            var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
            var shape = new ShapeItem { Frame = 0, Length = 100 };
            shape.X.SetFirst(-12.25); shape.Y.SetFirst(8.75); shape.Opacity.SetFirst(43);
            timeline.Items = timeline.Items.Add(shape);
            var scene = new Scene(timeline, scenes, []);
            var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
            using (source)
            {
                TimelineFrameCache.Enabled = false;
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                var baseline = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
                var baselineClock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 3; i++) source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                baselineClock.Stop();
                TimelineFrameCache.Enabled = true;
                TimelineFrameCache.Clear();
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                long oldHits = TimelineFrameCache.Hits;
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits > oldHits, "Actual source did not hit: " + TimelineFrameCache.Status);
                oldHits = TimelineFrameCache.Hits;
                const int ReuseSamples = 200;
                var reuseTimes = new double[ReuseSamples];
                var reuseClock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < ReuseSamples; i++)
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                    reuseTimes[i] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                reuseClock.Stop();
                Check(TimelineFrameCache.Hits - oldHits == ReuseSamples, "Repeated source cache hit count changed during timing sample");
                Array.Sort(reuseTimes);
                Console.WriteLine($"Measured TimelineSource.Update: baseline {baselineClock.Elapsed.TotalMilliseconds / 3:F2} ms/update; live reuse {reuseClock.Elapsed.TotalMilliseconds / ReuseSamples:F3} ms/update, p50 {reuseTimes[ReuseSamples / 2]:F3} ms, p95 {reuseTimes[ReuseSamples * 95 / 100]:F3} ms (3/{ReuseSamples} samples; no performance threshold)");
                var cached = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
                Check(baseline.SequenceEqual(cached), "Actual background/ShapeItem source pixel parity failed");
                CheckEditDuringLiveLookup(source, timeline, dc);
                HostCompat.EditModel(timeline, System.Windows.Media.Colors.Red);
                oldHits = TimelineFrameCache.Hits;
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits == oldHits, "Background edit reused stale output");
                Thread.Sleep(300); // the render path waits for edits to settle before re-describing the model
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits == oldHits, "Edited frame was reused before it was re-rendered");
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits == oldHits + 1, "Re-rendered frame after an edit was not reused");
                // Clear invalidates the live frame and the store; reuse resumes after one render.
                TimelineFrameCache.Clear();
                oldHits = TimelineFrameCache.Hits;
                long oldMisses = TimelineFrameCache.Misses;
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits == oldHits && TimelineFrameCache.Misses == oldMisses + 1, "Clear did not invalidate the live frame");
                source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                Check(TimelineFrameCache.Hits == oldHits + 1, "Reuse did not resume after Clear");
                CheckSeparateSwitches(source);
            }
            Check(TimelineFrameCache.GpuBytes == 0, "Source disposal leaked global GPU reservation");
            Console.WriteLine("Actual host automatic source cache: hit/parity/invalidation/GPU cleanup OK");
            CheckExportStore(host, context);
            Check(TimelineFrameCache.GpuBytes == 0, "Export store checks leaked global GPU reservation");
            DrawOrderMeasurements.Run(host, context);
            if (features.DecoderVerified("YukkuriMovieMaker.Plugin.FileSource.WIC"))
            {
                if (ImageSequence.TimeMappingAvailable) CheckImageSequence(host, context, harmony);
                else Console.WriteLine("Image sequence check skipped: this build has no time mapping the key can use (sequences are not cached)");
                FileNotificationSafetyChecks.Run(host, context);
                if (features.SimpleTachie) SimpleTachiePixelChecks.Run(host);
                Check(TimelineFrameCache.GpuBytes == 0, "File notification checks leaked global GPU reservation");
            }
            else Console.WriteLine("Image sequence check skipped: the WIC reader is not trusted on this build");
            if (features is { Preview: true, SelectionRects: true })
            {
                PreviewRectChecks.Run(host, context);
                Check(TimelineFrameCache.GpuBytes == 0, "Preview rect checks leaked global GPU reservation");
            }
            else Console.WriteLine("Preview rect checks skipped: rect reuse is off on this build");
            if (features.Preview)
            {
                PreviewDeliveryChecks.Run(host, context, features.SelectionRects);
                Check(TimelineFrameCache.GpuBytes == 0, "Preview delivery checks leaked global GPU reservation");
                IdleRandomChecks.Run(host, context);
                Check(TimelineFrameCache.GpuBytes == 0, "Idle random checks leaked global GPU reservation");
                VoiceCachePixelChecks.Run(host, context);
                Check(TimelineFrameCache.GpuBytes == 0, "Voice cache checks leaked global GPU reservation");
                OperationSequenceChecks.Run(host, context);
                Check(TimelineFrameCache.GpuBytes == 0, "Operation sequences leaked global GPU reservation");
            }
            if (features.DecoderVerified(mediaFoundation)) CheckBoundaryTimes(host, context, videoPath);
            if (features.DecoderVerified(mediaFoundation)) CheckVideoDecodeFailureIsNotStored(host, context, videoPath);
            else Console.WriteLine("Video decode-failure check skipped: the MediaFoundation reader is not trusted on this build");
        }
        finally
        {
            TimelineFrameCache.Enabled = false;
            TimelineFrameCache.Clear();
            FrameRenderReadiness.Uninstall(harmony);
            harmony.UnpatchAll(harmony.Id);
        }
    }
    private static bool SkipLoader() => false;

    private static void CheckEditDuringLiveLookup(ITimelineSource source, Timeline timeline, ID2D1DeviceContext dc)
    {
        long hits = TimelineFrameCache.Hits;
        TimelineFrameCache.BeforeCacheLookupForTests = () =>
        {
            TimelineFrameCache.BeforeCacheLookupForTests = null;
            HostCompat.EditModel(timeline, System.Windows.Media.Colors.Green);
        };
        try
        {
            source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
            Check(TimelineFrameCache.Hits == hits, "Edit between capture and live lookup reused stale output");
            var edited = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
            TimelineFrameCache.Enabled = false;
            source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
            var fresh = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
            Check(edited.SequenceEqual(fresh), "Concurrent-edit fallback differs from fresh host rendering");
            Console.WriteLine("Host live reuse: edit after capture rejects stale output and matches fresh pixels.");
        }
        finally { TimelineFrameCache.BeforeCacheLookupForTests = null; TimelineFrameCache.Enabled = true; }
    }

    // The settings switch the preview cache and the export cache separately (NVENC output is a third switch).
    private static void CheckSeparateSwitches(ITimelineSource source)
    {
        // From an empty cache (the export frame is already stored by the checks before): render, then reuse.
        long Reused(TimelineSourceUsage usage)
        {
            TimelineFrameCache.Clear();
            long hits = TimelineFrameCache.Hits;
            source.Update(TimeSpan.Zero, usage);
            source.Update(TimeSpan.Zero, usage);
            return TimelineFrameCache.Hits - hits;
        }
        try
        {
            TimelineFrameCache.SetEnabled(preview: true, export: false);
            Check(Reused(TimelineSourceUsage.Exporting) == 0, "Export frames were cached with the export cache switched off");
            Check(Reused(TimelineSourceUsage.Paused) == 1, "The preview cache did not work on its own");
            TimelineFrameCache.SetEnabled(preview: false, export: true);
            Check(Reused(TimelineSourceUsage.Paused) == 0, "Preview frames were cached with the preview cache switched off");
            Check(Reused(TimelineSourceUsage.Exporting) == 1, "The export cache did not work on its own");
        }
        finally { TimelineFrameCache.SetEnabled(preview: true, export: true); }

        // Settings files from before the switches were separated carry their one switch over to both caches.
        var legacy = new FrameCacheToolSettings { Enabled = true };
        legacy.Initialize();
        Check(legacy.PreviewCache && legacy.ExportCache && legacy.NvencOutput && legacy.SettingsVersion == 1, "Old settings (cache on) were not carried over");
        var fresh = new FrameCacheToolSettings();
        fresh.Initialize();
        Check(!fresh.PreviewCache && !fresh.ExportCache && fresh.NvencOutput, "New settings: caches off, NVENC output on");
        Check(fresh.AutomaticRamBudget && fresh.RamLimitMiB == 2048 && fresh.CacheFramesWhenIdle && fresh.IdleDelaySeconds == 8,
            "New memory/idle defaults");
        fresh.RamLimitMiB = -1;
        Check(fresh.RamLimitMiB == 64, "RAM minimum setting");
        fresh.RamLimitMiB = int.MaxValue;
        Check(fresh.RamLimitMiB == 16384, "RAM maximum setting");
        fresh.IdleDelaySeconds = double.NaN;
        fresh.IdleOrder = (IdleCacheOrder)int.MaxValue;
        Check(fresh.IdleDelaySeconds == 8 && fresh.IdleOrder == IdleCacheOrder.FromCurrentTime, "Invalid idle settings fall back");
        var current = new FrameCacheToolSettings { SettingsVersion = 1, Enabled = true, PreviewCache = false, ExportCache = true, NvencOutput = false };
        current.Initialize();
        Check(!current.PreviewCache && current.ExportCache && !current.NvencOutput, "Current settings were changed on load");
        Console.WriteLine("Settings: preview and export caches switch separately; old settings carried over");
    }

    // An export stores its frames without waiting for the GPU (copies finished by later frames, the last ones when the
    // source is disposed): a second export of the same range restores every frame from the store, pixel for pixel.
    private static void CheckExportStore(Assembly host, IGraphicsDevicesAndContext context)
    {
        const int Frames = 10, Width = 321, Height = 181;
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        timeline.VideoInfo.SetBackground(System.Windows.Media.Color.FromArgb(200, 10, 120, 60));
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        for (int i = 0; i < Frames; i++)
        {
            var shape = new ShapeItem { Frame = i, Length = 1 };
            shape.X.SetFirst(-140 + i * 31.5); shape.Y.SetFirst(-60 + i * 9.25); shape.Opacity.SetFirst(40 + i * 6);
            timeline.Items = timeline.Items.Add(shape);
        }
        var scene = new Scene(timeline, scenes, []);
        var dc = context.DeviceContext;
        var half = new Vector2(Width / 2f, Height / 2f);
        byte[][] Export(bool cache)
        {
            TimelineFrameCache.Enabled = cache;
            var pixels = new byte[Frames][];
            var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
            using (source)
                for (int frame = 0; frame < Frames; frame++)
                {
                    source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
                    pixels[frame] = TimelineFrameCache.Capture(dc, source.Output, Width, Height, -half)!;
                }
            return pixels;
        }
        try
        {
            var baseline = Export(cache: false);
            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            long misses = TimelineFrameCache.Misses;
            var first = Export(cache: true);
            Check(TimelineFrameCache.Misses - misses == Frames, $"The first export rendered {TimelineFrameCache.Misses - misses} of {Frames} frames: {TimelineFrameCache.Status}");
            long ramHits = TimelineFrameCache.RamHits;
            var second = Export(cache: true);
            Check(TimelineFrameCache.RamHits - ramHits == Frames,
                $"The second export restored {TimelineFrameCache.RamHits - ramHits} of {Frames} frames from the store (the last ones are finished when the export's source is disposed)");
            for (int frame = 0; frame < Frames; frame++)
                Check(first[frame].SequenceEqual(baseline[frame]) && second[frame].SequenceEqual(baseline[frame]), $"Export frame {frame} differs from the host's render");
            Check(baseline.Distinct(new BytesComparer()).Count() == Frames, "The export fixture's frames are not all different");
        }
        finally { TimelineFrameCache.Enabled = true; }
        Console.WriteLine($"Export store: {Frames} frames stored without waiting for the GPU (the last on disposal), restored by a second export pixel for pixel OK");
    }

    // A numbered PNG played by YMM4's own sequence reader: once its images are fingerprinted, every frame is stored and
    // restored pixel for pixel; a key that names another image than the one the reader showed is never stored.
    private static void CheckImageSequence(Assembly host, IGraphicsDevicesAndContext context, Harmony harmony)
    {
        Check(FrameRenderReadiness.Coverage.Any(line => line.StartsWith(FrameRenderReadiness.WicSequenceTypeName + ": image sequence", StringComparison.Ordinal)),
            "The image sequence reader was not verified: " + string.Join(" | ", FrameRenderReadiness.Coverage));
        const int Frames = 6, Width = 160, Height = 90, Attempts = 100;
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ymm-cache-sequence-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var patch = new Harmony("ymm.tests.sequence-misprediction");
        try
        {
            for (int i = 0; i < 2 * Frames; i++)
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, $"shot{i}.png"), Png(48, 32, (x, y) =>
                    ((byte)(i * 21), (byte)(255 - i * 19), (byte)((x * 5 + y * 3 + i * 40) % 256), (byte)(x < 8 + i * 3 ? 255 : 160))));
            var timeline = new Timeline();
            timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
            var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
            timeline.Items = timeline.Items.Add(new VideoItem { FilePath = System.IO.Path.Combine(directory, "shot0.png"), Frame = 0, Length = Frames });
            var scene = new Scene(timeline, scenes, []);
            var dc = context.DeviceContext;
            var half = new Vector2(Width / 2f, Height / 2f);
            ITimelineSource NewSource() => (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
            byte[] Render(ITimelineSource source, int frame)
            {
                source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
                return TimelineFrameCache.Capture(dc, source.Output, Width, Height, -half)!;
            }
            // A frame is ready to store once rendering it again at once reuses it (the images are fingerprinted in the
            // background first; the live reuse is only kept for frames the render check accepted).
            bool Accepted(ITimelineSource source, int frame)
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    Render(source, frame);
                    long hits = TimelineFrameCache.Hits;
                    Render(source, frame);
                    if (TimelineFrameCache.Hits > hits) return true;
                    Thread.Sleep(50);
                }
                return false;
            }
            Console.WriteLine("Video readers in the order YMM4 tries them: " + string.Join(", ", PluginLoader.VideoFileSourcePlugins.Select(reader => reader.GetType().Name)));
            TimelineFrameCache.Enabled = false;
            var baseline = new byte[Frames][];
            using (var source = NewSource())
                for (int frame = 0; frame < Frames; frame++) baseline[frame] = Render(source, frame);
            Check(baseline.Distinct(new BytesComparer()).Count() == Frames, "The sequence's frames are not all different (the reader did not play it)");

            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            using (var source = NewSource()) // its pending stores finish when it is disposed
                for (int frame = 0; frame < Frames; frame++)
                    Check(Accepted(source, frame), $"Sequence frame {frame} was never accepted for the store: " + TimelineFrameCache.Status);
            using (var source = NewSource())
                for (int frame = 0; frame < Frames; frame++)
                {
                    bool restored = false;
                    for (int attempt = 0; attempt < Attempts && !restored; attempt++)
                    {
                        long ramHits = TimelineFrameCache.RamHits;
                        var pixels = Render(source, frame);
                        restored = TimelineFrameCache.RamHits > ramHits;
                        if (restored) Check(pixels.SequenceEqual(baseline[frame]), $"Sequence frame {frame} restored from the store differs from the host's render");
                        else
                        {
                            Check(pixels.SequenceEqual(baseline[frame]), $"Sequence frame {frame} rendered with the cache differs from the host's render");
                            Thread.Sleep(50);
                        }
                    }
                    Check(restored, $"Sequence frame {frame} was never restored from the store: " + TimelineFrameCache.Status);
                }

            // A key that names another image than the one the reader shows (the next one): each frame is keyed and
            // rendered, matches the host, and is never kept; the status says why. With the right keys they are again.
            patch.Patch(typeof(ImageSequence).GetMethod(nameof(ImageSequence.FrameFiles), BindingFlags.Static | BindingFlags.NonPublic)!,
                postfix: new HarmonyMethod(typeof(FramePixelChecks), nameof(Mispredict)));
            TimelineFrameCache.Clear();
            using (var source = NewSource())
                for (int frame = 0; frame < Frames; frame++)
                {
                    bool rejected = false;
                    for (int attempt = 0; attempt < Attempts && !rejected; attempt++)
                    {
                        long hits = TimelineFrameCache.Hits;
                        Check(Render(source, frame).SequenceEqual(baseline[frame]) && Render(source, frame).SequenceEqual(baseline[frame]),
                            $"Sequence frame {frame} under a mispredicted key differs from the host's render");
                        Check(TimelineFrameCache.Hits == hits, $"Sequence frame {frame} under a key naming another image was reused");
                        rejected = TimelineFrameCache.Status.Contains("デコード完了", StringComparison.Ordinal);
                        if (!rejected) Thread.Sleep(50);
                    }
                    Check(rejected, $"Sequence frame {frame} under a key naming another image was not rejected by the render check: " + TimelineFrameCache.Status);
                }
            patch.UnpatchAll(patch.Id);
            using (var source = NewSource())
                Check(Accepted(source, 2), "Sequence frames were not accepted again with the right keys: " + TimelineFrameCache.Status);
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            TimelineFrameCache.Enabled = true;
            try { System.IO.Directory.Delete(directory, true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"Image sequence (WIC reader, {Frames} frames): accepted after fingerprinting, restored pixel for pixel; a key naming another image is never kept");
    }

    private static void Mispredict(ref string[]? __result)
    {
        if (__result is { Length: > 1 } shown) __result = [.. shown.Skip(1), shown[0]];
    }

    // A minimal RGBA PNG (one IDAT, no filtering).
    internal static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var raw = new System.IO.MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                raw.Write([r, g, b, a]);
            }
        }
        var compressed = new System.IO.MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());
        var png = new System.IO.MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            png.Write(BigEndian((uint)data.Length));
            png.Write(typed);
            png.Write(BigEndian(Crc32(typed)));
        }
        Chunk("IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 6, 0, 0, 0]);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] value) => value.Length;
    }

    // Real reader, injected decoder failure: the host renders transparency and returns normally, and the
    // cache must neither store nor reuse that frame. Recovery must re-enable reuse.
    private static void CheckVideoDecodeFailureIsNotStored(Assembly host, IGraphicsDevicesAndContext context, string? videoPath)
    {
        if (videoPath is null || !System.IO.File.Exists(videoPath))
        {
            Console.WriteLine("Video decode-failure check skipped (pass --video <mp4>)");
            return;
        }
        var reader = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation");
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tryDecode = reader.GetType("YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.Source2.MFFrameDecoder", true)!.GetMethod("TryDecodeAt", Instance)!;
        var legacy = reader.GetType("YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.MFVideoFileSource", true)!;
        // RefreshCurrentFrameWithReload has exception filters Harmony 2.4.2 cannot rebuild; Update itself is hookable.
        var legacyUpdate = legacy.GetMethod("Update", Instance, [typeof(TimeSpan)])!;
        clearCurrentFrame = legacy.GetMethod("ClearCurrentFrame", Instance)!;

        var timeline = new Timeline();
        timeline.VideoInfo.Width = 320; timeline.VideoInfo.Height = 180; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(new VideoItem { FilePath = videoPath, Frame = 0, Length = 30 });
        var scene = new Scene(timeline, scenes, []);
        var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;
        using (source)
        {
            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            bool Reused(int frame, int attempts)
            {
                var time = timeline.VideoInfo.GetTimeFrom(frame);
                for (int i = 0; i < attempts; i++)
                {
                    source.Update(time, TimelineSourceUsage.Exporting);
                    long hits = TimelineFrameCache.Hits;
                    source.Update(time, TimelineSourceUsage.Exporting);
                    if (TimelineFrameCache.Hits > hits) return true;
                    Thread.Sleep(50); // external files are fingerprinted in the background first
                }
                return false;
            }
            Check(Reused(5, 100), "Decoded video frame was never reused: " + TimelineFrameCache.Status);

            var failure = new Harmony("ymm.tests.decode-failure");
            failure.Patch(tryDecode, prefix: new HarmonyMethod(typeof(FramePixelChecks), nameof(FailDecode)));
            failure.Patch(legacyUpdate, prefix: new HarmonyMethod(typeof(FramePixelChecks), nameof(FailLegacyUpdate)));
            try
            {
                TimelineFrameCache.Clear();
                long misses = TimelineFrameCache.Misses;
                Check(!Reused(12, 3), "A frame whose video decode failed was stored or reused");
                Check(TimelineFrameCache.Misses > misses, "Decode-failure frames never reached the cache: " + TimelineFrameCache.Status);
                Check(TimelineFrameCache.Status.Contains("デコード完了", StringComparison.Ordinal), "Unexpected status: " + TimelineFrameCache.Status);
            }
            finally { failure.UnpatchAll(failure.Id); }
            Check(Reused(14, 20), "Reuse did not resume after decoding recovered: " + TimelineFrameCache.Status);
        }
        Console.WriteLine("Real reader decode failure (MF2 TryDecodeAt / legacy timeout): not stored, reuse resumes after recovery OK");
    }

    // Exact time keys: a time one tick before a frame boundary is a request of its own. With the cache ON and the
    // boundary frame already stored, the earlier time must show what the host renders for it with the cache OFF
    // (a key by frame number used to hand it the boundary frame).
    private static void CheckBoundaryTimes(Assembly host, IGraphicsDevicesAndContext context, string? videoPath)
    {
        if (videoPath is null || !System.IO.File.Exists(videoPath))
        {
            Console.WriteLine("Boundary time check skipped (pass --video <mp4>)");
            return;
        }
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 320; timeline.VideoInfo.Height = 180; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(new VideoItem { FilePath = videoPath, Frame = 0, Length = 60 });
        var scene = new Scene(timeline, scenes, []);
        var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        using (source)
        {
            var dc = context.DeviceContext;
            byte[] Render(TimeSpan time)
            {
                source.Update(time, TimelineSourceUsage.Exporting);
                return TimelineFrameCache.Capture(dc, source.Output, 320, 180, new(-160, -90))!;
            }
            var boundary = timeline.VideoInfo.GetTimeFrom(15);
            var earlier = boundary - TimeSpan.FromTicks(1);
            TimelineFrameCache.Enabled = false;
            var atBoundary = Render(boundary);
            var beforeBoundary = Render(earlier);
            TimelineFrameCache.Enabled = true;
            TimelineFrameCache.Clear();
            bool stored = false;
            for (int i = 0; i < 100 && !stored; i++) // the video file is fingerprinted in the background first
            {
                source.Update(boundary, TimelineSourceUsage.Exporting);
                long hits = TimelineFrameCache.Hits;
                source.Update(boundary, TimelineSourceUsage.Exporting);
                stored = TimelineFrameCache.Hits > hits;
                if (!stored) Thread.Sleep(50);
            }
            Check(stored, "The boundary frame was never reused: " + TimelineFrameCache.Status);
            long renders = TimelineFrameCache.Misses;
            Check(Render(earlier).SequenceEqual(beforeBoundary) && TimelineFrameCache.Misses == renders + 1,
                "One tick before a frame boundary, the cache did not render what the host renders without it");
            Check(Render(boundary).SequenceEqual(atBoundary), "The boundary frame differs from the host render");
            Console.WriteLine($"Boundary times (real video, one tick apart): host samples {(atBoundary.SequenceEqual(beforeBoundary) ? "are equal" : "differ")}; cache ON matches cache OFF at both");
        }
    }

    private static MethodInfo clearCurrentFrame = null!;

    private static bool FailDecode(ref bool __result)
    {
        __result = false; // the source has already disposed its previous frame, so it draws transparency
        return false;
    }

    private static bool FailLegacyUpdate(object __instance, TimeSpan time)
    {
        clearCurrentFrame.Invoke(__instance, [time]); // what the legacy reader's Update does after a timeout
        return false;
    }

    private static void CheckLatePreviewTransformParity(ID2D1DeviceContext dc)
    {
        const int width = 321, height = 181;
        using var original = dc.CreateCommandList();
        dc.Target = original;
        dc.BeginDraw();
        using (var brush = dc.CreateSolidColorBrush(new Color4(0.8f, 0.3f, 0.7f, 0.37f)))
            dc.FillRectangle(new Vortice.RawRectF(-81.25f, -40.75f, 82.125f, 43.5f), brush);
        dc.EndDraw().CheckError(); dc.Target = null; original.Close().CheckError();
        var half = new Vector2(width / 2f, height / 2f);
        var visible = new Vector2(271f, 137f);
        var viewCenter = new Vector2(13f, -9f);
        var scale = new Vector2(width / visible.X, height / visible.Y);
        var transform = Matrix3x2.CreateScale(scale, half) * Matrix3x2.CreateTranslation(-viewCenter * scale);
        var viewport = new TimelineFrameCache.PreviewViewport(width, height, transform, half,
            dc.Dpi.Width, dc.Dpi.Height,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            Guid.NewGuid(), Guid.NewGuid(), System.Diagnostics.Stopwatch.GetTimestamp(), false);
        var saved = TimelineFrameCache.CapturePreview(dc, original, viewport)!;
        var accounted = TimelineFrameCache.GpuBytes;
        using (var readback = TimelineFrameCache.BeginPreviewReadback(dc, original, viewport))
        {
            Check(readback is not null, "Explicit preview staging readback unavailable");
            Check(TimelineFrameCache.GpuBytes == accounted + (long)width * height * 4, "Pending staging allocation not accounted");
            byte[]? polled = null;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!TimelineFrameCache.TryFinishPreviewReadback(readback!, out polled))
            {
                Check(polled is null, "Busy GPU allocated/returned an incomplete record");
                Check(deadline.Elapsed < TimeSpan.FromSeconds(10), "Nonblocking staging copy never completed");
                Thread.Sleep(1); // test harness only; live playback polls on subsequent player updates
            }
            Check(saved.SequenceEqual(polled!), "Nonblocking staging pixels differ from explicit capture");
            Check(TimelineFrameCache.TryFinishPreviewReadback(readback!, out var again) && saved.SequenceEqual(again!),
                "Staging resource was not unmapped after completion");
            readback!.Dispose(); // repeated disposal must be harmless
        }
        Check(TimelineFrameCache.GpuBytes == accounted, "Staging reservation leaked after disposal");
        Console.WriteLine("Nonblocking preview staging: eventual exact pixels, remap and reservation release OK");
        using var savedImage = TimelineFrameCache.UploadPreview(dc, saved, viewport);
        var direct = CapturePreview(dc, original, width, height, transform);
        var cached = CapturePreview(dc, savedImage, width, height, transform);
        Check(direct.SequenceEqual(cached), "Viewport cache changed pixels under TimelineVideoPlayer's late zoom/pan transform");
        // Drawn once: the copy a stored frame shows (the pixels drawn for the store) draws what the host's output draws.
        long beforeShown = TimelineFrameCache.GpuBytes;
        var shownReadback = TimelineFrameCache.BeginPreviewReadback(dc, original, viewport, true, out var shown);
        try
        {
            Check(shownReadback is not null && shown is not null, "Drawn-once preview copy unavailable");
            Check(TimelineFrameCache.GpuBytes == beforeShown + 2L * width * height * 4, "Drawn-once copy and staging were not both accounted");
            Check(direct.SequenceEqual(CapturePreview(dc, shown!, width, height, transform)),
                "Drawn-once copy changed pixels under TimelineVideoPlayer's late zoom/pan transform");
        }
        finally
        {
            shownReadback?.Dispose();
            if (shown is not null) TimelineFrameCache.DisposeShownCopy(shown, viewport);
        }
        Check(TimelineFrameCache.GpuBytes == beforeShown, "Drawn-once copy reservation leaked");
        var makeKey = typeof(TimelineFrameCache).GetMethod("MakeKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var key = (string)makeKey.Invoke(null, ["model", TimeSpan.Zero, 30, "Preview", dc, viewport])!;
        var changed = (string)makeKey.Invoke(null, ["model", TimeSpan.Zero, 30, "Preview", dc,
            viewport with { Transform = Matrix3x2.CreateTranslation(1, 0) * transform }])!;
        Check(key != changed, "Preview cache key ignored the view transform");
        Console.WriteLine("Late preview zoom/pan parity (cached and drawn-once copies) and transform-key invalidation OK");
    }

    private static byte[] CapturePreview(ID2D1DeviceContext dc, ID2D1Image source, int width, int height, Matrix3x2 transform)
    {
        using var target = dc.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var oldTarget = dc.Target;
        var oldTransform = dc.Transform;
        try
        {
            dc.Target = target;
            dc.BeginDraw();
            dc.Clear(new Color4(0, 0, 0, 1));
            dc.Transform = transform;
            dc.DrawImage(source, new Vector2(width / 2f, height / 2f));
            dc.EndDraw().CheckError();
            dc.Target = null;
            return TimelineFrameCache.Capture(dc, target, width, height, Vector2.Zero)!;
        }
        finally
        {
            dc.Target = oldTarget;
            dc.Transform = oldTransform;
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
