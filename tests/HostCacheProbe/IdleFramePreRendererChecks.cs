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
        Console.WriteLine("Idle pre-render: independent scene clone and cancelled commit guard OK");
    }

    private static void CheckClonedSceneIsIndependent()
    {
        var loader = typeof(PluginAssemblyLoader);
        var bootstrap = new Harmony("ymm.tests.idle-pre-renderer.loader");
        bootstrap.Patch(loader.TypeInitializer!, prefix: new HarmonyMethod(typeof(IdleFramePreRendererChecks), nameof(SkipPluginLoader)));
        AccessTools.StaticFieldRefAccess<IEnumerable<Assembly>>(AccessTools.Field(loader, "<Assemblies>k__BackingField"))() =
            [typeof(Scene).Assembly, typeof(CacheProvider).Assembly];
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 321;
        timeline.VideoInfo.Height = 181;
        var shape = new ShapeItem { Frame = 4, Length = 90 };
        shape.X.SetFirstValue(-12.25);
        timeline.Items = timeline.Items.Add(shape);
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
            Check(liveCapture.Key == cloneCapture.Key, "Live and cloned test captures do not match");
            var cache = assembly.GetType("NVEncVideoWriterPlugin.TimelineFrameCache", true)!;
            var viewportType = cache.GetNestedType("PreviewViewport", BindingFlags.NonPublic)!;
            var viewport = viewportType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
                .Invoke([321, 181, Matrix3x2.Identity, Vector2.Zero, 96f, 96f, liveScene.ID, liveScene.Timeline.ID,
                    Stopwatch.GetTimestamp(), false]);
            var prime = cache.GetMethod("TryPrimePreview", BindingFlags.Static | BindingFlags.NonPublic)!;
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
        var args = new object?[] { null, null };
        bool captured = (bool)trackerType.GetMethod("TryCapture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(tracker, args)!;
        Check(captured, "Could not capture the test scene: " + args[1]);
        capture = (IDisposable)args[0]!;
        return (IDisposable)tracker;
    }

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
