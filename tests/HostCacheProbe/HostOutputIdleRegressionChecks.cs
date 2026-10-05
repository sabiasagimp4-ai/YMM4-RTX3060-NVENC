using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class HostOutputIdleRegressionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly host, bool outputOnly, bool idleOnly)
    {
        var harmony = new Harmony("ymm.tests.host-output-idle-regressions");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(HostOutputIdleRegressionChecks), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        const int width = 321, height = 181, frames = 120;
        var timeline = new Timeline();
        timeline.VideoInfo.Width = width; timeline.VideoInfo.Height = height; timeline.VideoInfo.FPS = 30;
        timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, frames).Select(frame =>
        {
            var item = new ShapeItem { Frame = frame, Length = 1 };
            item.X.SetFirstValue(-120 + frame * 2); return item;
        }));
        // An identity-seeded item outside the tested range still appears in the whole-scene model.
        var random = new ShapeItem { Frame = frames, Length = 30, Layer = 1 };
        random.X.AnimationType = AnimationType.ランダム移動;
        timeline.Items = timeline.Items.Add(random); timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        using var player = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            Instance, null, [context, scene, null], null)!;
        string root = Path.Combine(Path.GetTempPath(), "ymm-output-idle-regression-" + Guid.NewGuid().ToString("N"));
        using var store = new FrameCacheStore(root, 64L << 20, 0);
        var view = new TimelineFrameCache.PreviewViewport(width, height, Matrix3x2.Identity, new Vector2(width / 2f, height / 2f), 96, 96,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, scene.ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        void Update(int frame) => player.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
        byte[] Pixels() => TimelineFrameCache.CapturePreview(dc, player.Output, view)!;
        try
        {
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            TimelineFrameCache.UseStore(store); TimelineFrameCache.GpuRetentionEnabled = false;
            TimelineFrameCache.TestViewport = value => ReferenceEquals(player, value) ? view : null;
            TimelineFrameCache.Enabled = false;
            var reference = Enumerable.Range(0, frames).Select(frame => { Update(frame); return Pixels(); }).ToArray();
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(0); TimelineFrameCache.CompletePendingStore(player); long hits = TimelineFrameCache.Hits; Update(0);
                return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(10)), "Regression fixture did not become ready: " + TimelineFrameCache.Status);
            if (!idleOnly)
            {
                var collector = (DisposeCollector)player.GetType().GetField("disposer", Instance)!.GetValue(player)!;
                var entries = (IList)collector.GetType().GetField("disposables", Instance)!.GetValue(collector)!;
                TimelineFrameCache.Clear();
                long shown = TimelineFrameCache.DrawnOnce;
                for (int frame = 0; frame < frames; frame++)
                {
                    TimelineFrameCache.Enabled = frame % 11 != 0;
                    Update(frame); TimelineFrameCache.CompletePendingStore(player);
                    Check(Pixels().SequenceEqual(reference[frame]), "Output lifetime changed pixels: " + frame);
                    Check(entries.Cast<object>().OfType<ID2D1CommandList>().Count() <= 2,
                        "OUTPUT_LIFETIME_REGRESSION: original host command lists accumulated");
                }
                Check(TimelineFrameCache.DrawnOnce > shown + frames / 2, "Output lifetime test did not exercise shown replacements");
                Console.WriteLine("HOST_OUTPUT_REGRESSION: 120 frames, normal-render bypasses, exact pixels, bounded command lists passed");
            }
            if (!outputOnly)
            {
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                using var tracker = new KeyDependencyTracker(scene);
                Check(SpinWait.SpinUntil(() => { if (!tracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(10)), "Idle tracker not ready");
                Check(tracker.TryCapture(0, out var capture, out reason), reason);
                using (capture)
                using (var batch = new IdleFramePreRenderer.BatchRenderer(tracker, capture!.Model))
                {
                    Check(SpinWait.SpinUntil(() => { if (!batch.CloneTracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(10)), "Clone tracker not ready");
                    Check(batch.CloneTracker.TryCapture(0, out var cloned, out reason), reason);
                    using (cloned)
                    {
                        Check(capture.Model != cloned!.Model, "Fixture did not create different identity values");
                        Check(capture.Key == cloned.Key, "Inactive random item changed the frame key");
                    }
                    for (int frame = 0; frame < 12; frame++)
                    {
                        var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, frame, view, () => true, CancellationToken.None, out reason);
                        Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered, "IDLE_IDENTITY_REGRESSION: " + result + " " + reason);
                        long hits = TimelineFrameCache.Hits; Update(frame);
                        Check(TimelineFrameCache.Hits == hits + 1 && Pixels().SequenceEqual(reference[frame]), "Idle frame hit or pixel parity failed");
                    }
                    // The active random frame remains unequal and cannot use the clone path.
                    bool activeReady = SpinWait.SpinUntil(() => {
                        if (!tracker.TryCapture(frames, out var a, out _)) return false;
                        using (a) { if (!batch.CloneTracker.TryCapture(frames, out var b, out _)) return false;
                            using (b) Check(a!.Key != b!.Key, "Active random frame lost its identity key"); }
                        return true;
                    }, TimeSpan.FromSeconds(10));
                    Check(activeReady, "Active random captures not ready");
                }
                Console.WriteLine("IDLE_IDENTITY_REGRESSION: inactive random item, 12 primed RAM hits with exact pixels; active frame keys remain unequal");
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = false;
            player.Dispose(); store.Dispose(); TimelineFrameCache.UseStore(new FrameCacheStore(root + "-after", 64L << 20, 0));
            harmony.UnpatchAll(harmony.Id);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
