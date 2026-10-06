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
        // Identity-seeded frames are keyed only where their randomness comes from the model (HostContracts identity-random).
        bool identityRandom = HostFeatures.For(typeof(Scene).Assembly).IdentityRandom;
        if (identityRandom) CheckSessionFrameRenderedLive();
        Console.WriteLine("Idle pre-render: independent scene clone, cancelled commit guard, unverifiable frames passed over"
            + (identityRandom ? " and identity-random frames rendered from the live scene OK" : " OK (identity-random frames render normally on this build)"));
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
            var scenes = HostCompat.NewScenes();
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

    // A frame keyed by object identities (a random move) is not rendered from the clone, which draws other random
    // values, but from the live scene under the live key; an edit during the render discards the frame.
    private static void CheckSessionFrameRenderedLive()
    {
        var timeline = new Timeline();
        var still = new ShapeItem { Frame = 0, Length = 30, Layer = 0 };
        var shaking = new ShapeItem { Frame = 60, Length = 30, Layer = 1 };
        shaking.X.AnimationType = YukkuriMovieMaker.Commons.AnimationType.ランダム移動;
        timeline.Items = timeline.Items.Add(still).Add(shaking);
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = HostCompat.NewScenes();
        scenes.AddScene(timeline);
        var live = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(live, out var model, out _, out var reason), reason);
        var clone = IdleFramePreRenderer.CloneSceneFromModel(model);
        using var liveTracker = new KeyDependencyTracker(live);
        using var cloneTracker = new KeyDependencyTracker(clone, liveTracker.VerifiedFingerprints);
        Check(SpinWait.SpinUntil(() =>
        {
            if (liveTracker.TryCapture(70, out var capture, out _)) capture!.Dispose();
            return liveTracker.IsSessionKeyed(70);
        }, TimeSpan.FromSeconds(15)), "A random move's frame was not keyed by its objects");
        Check(!liveTracker.IsSessionKeyed(10), "A frame without randomness was keyed by its objects");
        var viewport = new TimelineFrameCache.PreviewViewport(64, 36, Matrix3x2.Identity, Vector2.Zero, 96f, 96f,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            Vortice.Direct2D1.AntialiasMode.PerPrimitive, Vortice.Direct2D1.TextAntialiasMode.Default,
            Vortice.Direct2D1.PrimitiveBlend.SourceOver, Vortice.Direct2D1.UnitMode.Dips,
            live.ID, live.Timeline.ID, Stopwatch.GetTimestamp(), false);
        // The clone path passes it over.
        var result = IdleFramePreRenderer.PrimeFrame(liveTracker, live, cloneTracker, clone, new object(),
            _ => throw new InvalidOperationException("The clone rendered an identity-random frame"), 70, viewport, () => true, CancellationToken.None, out reason);
        Check(result == IdleFramePreRenderer.IdleFrameResult.Normal, $"The clone path did not pass over an identity-random frame ({result}: {reason})");

        var harmony = new Harmony("ymm.tests.idle-pre-renderer.live");
        harmony.Patch(typeof(TimelineFrameCache).GetMethod("TryPrimePreviewIfCurrent", BindingFlags.Static | BindingFlags.NonPublic)!,
            prefix: new HarmonyMethod(typeof(IdleFramePreRendererChecks), nameof(AcceptPrimePreview)));
        try
        {
            primeCalls = 0;
            var rendered = new List<TimeSpan>();
            result = IdleFramePreRenderer.PrimeLiveFrame(liveTracker, live, new object(), rendered.Add, 70, viewport, () => true, CancellationToken.None);
            Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered && Volatile.Read(ref primeCalls) == 1
                && rendered.SequenceEqual([live.Timeline.VideoInfo.GetTimeFrom(70)]),
                $"An identity-random frame was not rendered from the live scene ({result}, {primeCalls} stores, {rendered.Count} renders)");

            primeCalls = 0;
            result = IdleFramePreRenderer.PrimeLiveFrame(liveTracker, live, new object(),
                _ => shaking.X.SetFirst(shaking.X.GetValue(0, 100, 30) + 1), 70, viewport, () => true, CancellationToken.None);
            Check(result == IdleFramePreRenderer.IdleFrameResult.Unavailable && Volatile.Read(ref primeCalls) == 0,
                $"A frame edited during its live render was stored ({result})");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static bool AcceptPrimePreview(ref bool __result)
    {
        Interlocked.Increment(ref primeCalls);
        __result = true;
        return false;
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
        shape.X.SetFirst(-12.25);
        timeline.Items = timeline.Items.Add(shape);
        // As YMM4 does on load and after edits; setting Items alone leaves Length at 1.
        timeline.RefreshTimelineLengthAndMaxLayer();
        Check(timeline.Length == 94, "Test timeline length: " + timeline.Length);
        var scenes = HostCompat.NewScenes();
        scenes.AddScene(timeline);
        var live = new Scene(timeline, scenes, []);
        var pluginAssembly = typeof(FrameCacheToolPlugin).Assembly;
        string model = DescribePluginScene(pluginAssembly, live, out var files);
        Check(files.Length == 0, "Test scene unexpectedly acquired external file dependencies");

        var renderer = pluginAssembly.GetType("NVEncVideoWriterPlugin.IdleFramePreRenderer", true)!;
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
        string clonedModel = DescribePluginScene(pluginAssembly, clone, out _);
        Check(clonedModel == model, "Clone changed the serialized drawing state used for cache identity");
        bootstrap.UnpatchAll(bootstrap.Id);
    }

    private static void CheckCancelledJobCannotCommit()
    {
        var assembly = typeof(FrameCacheToolPlugin).Assembly;
        var renderer = assembly.GetType("NVEncVideoWriterPlugin.IdleFramePreRenderer", true)!;
        var method = renderer.GetMethod("TryPrimeIfCurrent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var timeline = new Timeline();
        var scenes = HostCompat.NewScenes();
        scenes.AddScene(timeline);
        var liveScene = new Scene(timeline, scenes, []);
        string model = DescribePluginScene(assembly, liveScene, out _);
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
                    [cancellation.Token, liveScene, cloneScene, null, TimeSpan.Zero, viewport, liveCapture, cloneCapture, null]);
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

    // Description payload witnesses are private to the assembly that created the description. These two checks
    // exercise the actual plugin DLL, so both halves must run there rather than mixing it with the linked harness.
    private static string DescribePluginScene(Assembly assembly, Scene scene, out string[] files)
    {
        var type = assembly.GetType("NVEncVideoWriterPlugin.FrameCacheKey", true)!;
        var method = type.GetMethod("TryDescribe", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Scene), typeof(string).MakeByRefType(), typeof(string[]).MakeByRefType(), typeof(string).MakeByRefType()])!;
        object?[] args = [scene, null, null, null];
        Check((bool)method.Invoke(null, args)!, "Could not describe the plugin scene: " + args[3]);
        files = (string[])args[2]!;
        return (string)args[1]!;
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
