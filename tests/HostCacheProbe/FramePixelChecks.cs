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
            timeline.VideoInfo.BackgroundColor = System.Windows.Media.Color.FromArgb(137, 123, 76, 231);
            var scenes = new Scenes(false); scenes.AddScene(timeline);
            var shape = new ShapeItem { Frame = 0, Length = 100 };
            shape.X.SetFirstValue(-12.25); shape.Y.SetFirstValue(8.75); shape.Opacity.SetFirstValue(43);
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
                var reuseClock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 8; i++) source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                reuseClock.Stop();
                Check(TimelineFrameCache.Hits - oldHits == 8, "Repeated source cache hit count changed during timing sample");
                Console.WriteLine($"Measured TimelineSource.Update: baseline {baselineClock.Elapsed.TotalMilliseconds / 3:F2} ms/update; live reuse {reuseClock.Elapsed.TotalMilliseconds / 8:F2} ms/update (3/8 samples; no performance threshold)");
                var cached = TimelineFrameCache.Capture(dc, source.Output, 321, 181, new(-160.5f, -90.5f))!;
                Check(baseline.SequenceEqual(cached), "Actual background/ShapeItem source pixel parity failed");
                timeline.VideoInfo.BackgroundColor = System.Windows.Media.Colors.Red;
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
            }
            Check(TimelineFrameCache.GpuBytes == 0, "Source disposal leaked global GPU reservation");
            Console.WriteLine("Actual host automatic source cache: hit/parity/invalidation/GPU cleanup OK");
            if (features is { Preview: true, SelectionRects: true })
            {
                PreviewRectChecks.Run(host, context);
                Check(TimelineFrameCache.GpuBytes == 0, "Preview rect checks leaked global GPU reservation");
            }
            else Console.WriteLine("Preview rect checks skipped: rect reuse is off on this build");
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
        var scenes = new Scenes(false); scenes.AddScene(timeline);
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
        using var savedImage = TimelineFrameCache.UploadPreview(dc, saved, viewport);
        var direct = CapturePreview(dc, original, width, height, transform);
        var cached = CapturePreview(dc, savedImage, width, height, transform);
        Check(direct.SequenceEqual(cached), "Viewport cache changed pixels under TimelineVideoPlayer's late zoom/pan transform");
        var makeKey = typeof(TimelineFrameCache).GetMethod("MakeKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var key = (string)makeKey.Invoke(null, ["model", TimeSpan.Zero, 30, "Preview", dc, viewport])!;
        var changed = (string)makeKey.Invoke(null, ["model", TimeSpan.Zero, 30, "Preview", dc,
            viewport with { Transform = Matrix3x2.CreateTranslation(1, 0) * transform }])!;
        Check(key != changed, "Preview cache key ignored the view transform");
        Console.WriteLine("Late preview zoom/pan parity and transform-key invalidation OK");
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
