using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Numerics;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class IdleFramePreRendererChecks
{
    private static int primeCalls;

    internal static void Run()
    {
        CheckClonedSceneIsIndependent();
        CheckCancelledJobCannotCommit();
        CheckUnverifiableFramePassed();
        Console.WriteLine("Idle pre-render: independent scene clone, cancelled commit guard and unverifiable frames passed over OK");
    }

    // A frame showing a file that cannot be verified (behind a directory junction, as in a OneDrive folder) is passed
    // over as one that renders normally, so a batch goes on past it instead of stopping there on every idle tick.
    private static void CheckUnverifiableFramePassed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ymm-idle-unverifiable-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(folder, "real"), link = Path.Combine(folder, "link");
        Directory.CreateDirectory(real);
        var retry = KeyDependencyTracker.UnverifiableRetry;
        try
        {
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                mklink!.WaitForExit();
            Check(Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Could not create the test junction");
            File.WriteAllBytes(Path.Combine(real, "linked.png"), [5, 6, 7, 8]);
            var timeline = new Timeline();
            timeline.Items = timeline.Items.Add(new ImageItem { FilePath = Path.Combine(link, "linked.png"), Frame = 0, Length = 10, Layer = 1 });
            timeline.RefreshTimelineLengthAndMaxLayer();
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            var live = new Scene(timeline, scenes, []);
            Check(FrameCacheKey.TryDescribe(live, out var model, out _, out var reason), reason);
            var clone = IdleFramePreRenderer.CloneSceneFromModel(model);
            KeyDependencyTracker.UnverifiableRetry = TimeSpan.FromMilliseconds(200);
            using var liveTracker = new KeyDependencyTracker(live);
            using var cloneTracker = new KeyDependencyTracker(clone, liveTracker.VerifiedFingerprints);
            Check(SpinWait.SpinUntil(() =>
            {
                if (liveTracker.TryCapture(5, out var capture, out _)) capture!.Dispose();
                return liveTracker.RendersNormally(5);
            }, TimeSpan.FromSeconds(15)), "The live tracker never marked the unverifiable frame to render normally");
            var viewport = new TimelineFrameCache.PreviewViewport(64, 36, Matrix3x2.Identity, Vector2.Zero, 96f, 96f,
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                Vortice.Direct2D1.AntialiasMode.PerPrimitive, Vortice.Direct2D1.TextAntialiasMode.Default,
                Vortice.Direct2D1.PrimitiveBlend.SourceOver, Vortice.Direct2D1.UnitMode.Dips,
                live.ID, live.Timeline.ID, Stopwatch.GetTimestamp(), false);
            var result = IdleFramePreRenderer.PrimeFrame(liveTracker, live, cloneTracker, clone, new object(),
                _ => throw new InvalidOperationException("An unverifiable frame was rendered"), 5, viewport, () => true, CancellationToken.None, out reason);
            Check(result == IdleFramePreRenderer.IdleFrameResult.Normal, $"An unverifiable frame stopped the batch ({result}: {reason})");
        }
        finally
        {
            KeyDependencyTracker.UnverifiableRetry = retry;
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CheckClonedSceneIsIndependent()
    {
        var loader = typeof(PluginAssemblyLoader);
        var bootstrap = new Harmony("ymm.tests.idle-pre-renderer.loader");
        bootstrap.Patch(loader.TypeInitializer!, prefix: new HarmonyMethod(typeof(IdleFramePreRendererChecks), nameof(SkipPluginLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(typeof(Scene).Assembly));
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 321;
        timeline.VideoInfo.Height = 181;
        var shape = new ShapeItem { Frame = 4, Length = 90 };
        shape.X.SetFirstValue(-12.25);
        timeline.Items = timeline.Items.Add(shape);
        // As YMM4 does on load and after edits; setting Items alone leaves Length at 1.
        timeline.RefreshTimelineLengthAndMaxLayer();
        Check(timeline.Length == 94, "Test timeline length: " + timeline.Length);
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var live = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(live, out var model, out var files, out var reason), reason);
        Check(files.Length == 0, "Test scene unexpectedly acquired external file dependencies");

        var renderer = typeof(FrameCacheToolPlugin).Assembly.GetType("NVEncVideoWriterPlugin.IdleFramePreRenderer", true)!;
        var cloneMethod = renderer.GetMethod("CloneSceneFromModel", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clone = (Scene)cloneMethod.Invoke(null, [model])!;
        Check(!ReferenceEquals(clone, live), "Clone reused the live Scene instance");
        Check(clone.ID == live.ID && clone.Timeline.ID == timeline.ID, "Clone changed scene or timeline identity");
        Check(!ReferenceEquals(clone.Timeline, timeline), "Clone reused the live Timeline instance");
        Check(clone.Timeline.Items.Count == 1 && !ReferenceEquals(clone.Timeline.Items[0], shape), "Clone reused live timeline items");
        var clonedShape = (ShapeItem)clone.Timeline.Items[0];
        Check(clonedShape.Frame == shape.Frame && clonedShape.Length == shape.Length, "Clone changed item timing");
        Check(clonedShape.X.GetValue(0, 100, 30) == shape.X.GetValue(0, 100, 30), "Clone changed item parameters");
        Check(clone.Timeline.VideoInfo.Width == timeline.VideoInfo.Width && clone.Timeline.VideoInfo.Height == timeline.VideoInfo.Height,
            "Clone changed video dimensions");
        Check(clone.Timeline.Length == timeline.Length, $"Clone changed the timeline length ({clone.Timeline.Length}, live {timeline.Length})");
        Check(FrameCacheKey.TryDescribe(clone, out var clonedModel, out _, out reason), reason);
        Check(clonedModel == model, "Clone changed the serialized drawing state used for cache identity");
        bootstrap.UnpatchAll(bootstrap.Id);
    }

    private static void CheckCancelledJobCannotCommit()
    {
        var assembly = typeof(FrameCacheToolPlugin).Assembly;
        var renderer = assembly.GetType("NVEncVideoWriterPlugin.IdleFramePreRenderer", true)!;
        var method = renderer.GetMethod("TryPrimeIfCurrent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var liveScene = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(liveScene, out var model, out _, out var reason), reason);
        var cloneMethod = renderer.GetMethod("CloneSceneFromModel", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cloneScene = (Scene)cloneMethod.Invoke(null, [model])!;

        using var liveTracker = CreatePluginTracker(assembly, liveScene, out var liveCapture);
        using var cloneTracker = CreatePluginTracker(assembly, cloneScene, out var cloneCapture);
        using (liveCapture)
        using (cloneCapture)
        {
            Check(CaptureKey(liveCapture) == CaptureKey(cloneCapture), "Live and cloned test captures do not match");
            var cache = assembly.GetType("NVEncVideoWriterPlugin.TimelineFrameCache", true)!;
            var viewportType = cache.GetNestedType("PreviewViewport", BindingFlags.NonPublic)!;
            var viewport = viewportType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
                .Invoke([321, 181, Matrix3x2.Identity, Vector2.Zero, 96f, 96f,
                    new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    Vortice.Direct2D1.AntialiasMode.PerPrimitive, Vortice.Direct2D1.TextAntialiasMode.Default,
                    Vortice.Direct2D1.PrimitiveBlend.SourceOver, Vortice.Direct2D1.UnitMode.Dips,
                    liveScene.ID, liveScene.Timeline.ID, Stopwatch.GetTimestamp(), false]);
            var prime = cache.GetMethod("TryPrimePreviewIfCurrent", BindingFlags.Static | BindingFlags.NonPublic)!;
            var harmony = new Harmony("ymm.tests.idle-pre-renderer.cancel");
            primeCalls = 0;
            harmony.Patch(prime, prefix: new HarmonyMethod(typeof(IdleFramePreRendererChecks), nameof(CountPrimePreview)));
            try
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var result = method.Invoke(null,
                    [cancellation.Token, liveScene, cloneScene, null, TimeSpan.Zero, viewport, liveCapture, cloneCapture]);
                Check(result is false && Volatile.Read(ref primeCalls) == 0,
                    "Cancelled idle job reached the preview-cache commit");
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }
    }

    private static IDisposable CreatePluginTracker(Assembly assembly, Scene scene, out IDisposable capture)
    {
        var trackerType = assembly.GetType("NVEncVideoWriterPlugin.KeyDependencyTracker", true)!;
        var tracker = Activator.CreateInstance(trackerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [scene], null)!;
        var captureType = assembly.GetType("NVEncVideoWriterPlugin.KeyCapture", true)!;
        var args = new object?[] { 0, null, null, false, false };
        bool captured = (bool)trackerType.GetMethod("TryCapture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(int), captureType.MakeByRefType(), typeof(string).MakeByRefType(), typeof(bool), typeof(bool)])!
            .Invoke(tracker, args)!;
        Check(captured, "Could not capture the test scene: " + args[2]);
        capture = (IDisposable)args[1]!;
        return (IDisposable)tracker;
    }

    // The plugin's KeyCapture is internal to its assembly; read its key by reflection.
    private static string CaptureKey(IDisposable capture) =>
        (string)capture.GetType().GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(capture)!;

    private static bool CountPrimePreview(ref bool __result)
    {
        Interlocked.Increment(ref primeCalls);
        __result = false;
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool SkipPluginLoader() => false;
}
