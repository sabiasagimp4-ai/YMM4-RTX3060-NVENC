using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string hostDir = Path.GetFullPath(args.FirstOrDefault() ?? @"D:\YukkuriMovieMaker_v4_Lite");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(hostDir, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        return Run();
    }

    // Defer binding host model types until the in-place dependency resolver is installed.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run()
    {
        // Test-only built-in discovery; host files remain in place and no application is started.
        var loaderType = typeof(YukkuriMovieMaker.Plugin.PluginAssemblyLoader);
        var bootstrap = new Harmony("ymm.cachechecks.builtin-loader");
        bootstrap.Patch(loaderType.TypeInitializer!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipLoader)));
        AccessTools.StaticFieldRefAccess<IEnumerable<Assembly>>(AccessTools.Field(loaderType, "<Assemblies>k__BackingField"))() =
            new[] { typeof(Scene).Assembly, typeof(YukkuriMovieMaker.Plugin.CacheProvider).Assembly };
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.IsBuiltInSourceReader(typeof(Scene)), "Host source reader assembly was not trusted");
        Check(FrameCacheKey.IsBuiltInSourceReader(typeof(YukkuriMovieMaker.Plugin.CacheProvider)), "Plugin API reader assembly was not trusted");
        Check(!FrameCacheKey.IsBuiltInSourceReader(typeof(Program)), "Custom reader assembly was trusted");
        Type[][] readerTypes = FrameCacheKey.CaptureSourceReaderTypes();
        Check(FrameCacheKey.SourceReadersMatch(readerTypes), "Unchanged source reader stamp mismatched");
        Check(!FrameCacheKey.SourceReadersMatch([readerTypes[0].Append(typeof(Program)).ToArray(), readerTypes[1], readerTypes[2]]), "Source reader list change was not detected");
        using var tracker = new KeyDependencyTracker(scene);
        Check(tracker.TryCapture(out var emptyCapture, out string trackerReason), trackerReason);
        using (emptyCapture!)
        {
            Check(emptyCapture!.Model.Length != 0 && emptyCapture.Validate(), "Capture did not retain the immutable drawing model");
        }
        Check(tracker.TryGetKey(out string trackedEmpty, out trackerReason), trackerReason);
        long stableRevision = tracker.CaptureRevision();
        int invalidations = 0;
        tracker.Invalidated += () => invalidations++;
        tracker.Invalidated += () => throw new InvalidOperationException("test cancellation observer failure");
        Check(tracker.TryGetKey(out string trackedAgain, out _) && trackedAgain == trackedEmpty && tracker.ValidateRevision(stableRevision), "Stable scene key was not reused");
        string empty = Key(scene);
        timeline.CurrentFrame = 10;
        Check(tracker.TryGetKey(out _, out _) && tracker.ValidateRevision(stableRevision), "Seeking invalidated a tracked drawing state");
        Check(Key(scene) == empty, "Seeking changed a drawing key");
        timeline.VideoInfo.Width++;
        Check(!tracker.ValidateRevision(stableRevision), "A capture from before mutation remained valid");
        Check(invalidations > 0, "Model mutation did not signal idle work cancellation");
        Check(tracker.TryGetKey(out string trackedChanged, out _) && trackedChanged != trackedEmpty, "Tracked nested edit did not invalidate");
        Check(Key(scene) != empty, "VideoInfo edit failed to invalidate");
        timeline.VideoInfo.Width--;
        Check(Key(scene) == empty, "Restored model failed to reuse key");

        var text = new TextItem { Text = "Cache key" };
        timeline.Items = timeline.Items.Add(text);
        string textKey = WaitForKey(tracker);
        timeline.SelectedItems = timeline.SelectedItems.Add(text);
        timeline.CurrentFrame++;
        Check(WaitForKey(tracker) == textKey, "Selection or seeking changed a drawing key");
        text.Text = "Changed";
        Check(WaitForKey(tracker) != textKey, "Text edit failed to invalidate");
        text.Text = "Cache key";
        Check(WaitForKey(tracker) == textKey, "Restoring text failed to reuse key");
        timeline.Items = timeline.Items.Remove(text);

        var shape = new ShapeItem();
        timeline.Items = timeline.Items.Add(shape);
        Check(FrameCacheKey.TryCreate(scene, out _, out string shapeReason), "Built-in shape bypassed: " + shapeReason);
        shape.X.SetFirstValue(0.0004);
        string preciseShapeKey = Key(scene);
        shape.X.SetFirstValue(0.00049);
        Check(Key(scene) != preciseShapeKey, "Small parameter changes collided after decimal rounding");
        shape.X.SetFirstValue(0.0004);
        Check(Key(scene) == preciseShapeKey, "Exact parameter restore did not recover the drawing key");
        timeline.Items = timeline.Items.Remove(shape);

        var noFiles = new Timeline();
        var noFileScenes = new Scenes(true);
        noFileScenes.AddScene(noFiles);
        var noFileScene = new Scene(noFiles, noFileScenes, []);
        using var noFileTracker = new KeyDependencyTracker(noFileScene);
        Check(noFileTracker.TryGetKey(out string beforeUndo, out _), "Empty tracked undo scene failed");
        noFileScenes.UndoRedoManager.Subscribe(noFiles);
        noFiles.VideoInfo.Width++;
        noFileScenes.UndoRedoManager.Record();
        Check(noFileTracker.TryGetKey(out string afterEdit, out _) && afterEdit != beforeUndo, "Recorded edit did not change key");
        noFileScenes.UndoRedoManager.UndoAsync().GetAwaiter().GetResult();
        Check(noFileTracker.TryGetKey(out string afterUndo, out _) && afterUndo == beforeUndo, "Undo failed to restore drawing key");
        noFileScenes.UndoRedoManager.RedoAsync().GetAwaiter().GetResult();
        Check(noFileTracker.TryGetKey(out string afterRedo, out _) && afterRedo == afterEdit, "Redo failed to restore drawing key");

        var nested = new Timeline();
        Check(tracker.TryGetKey(out string beforeNestedKey, out string nestedReason), nestedReason);
        long collectionRevision = tracker.CaptureRevision();
        scenes.AddScene(nested);
        Check(!tracker.ValidateRevision(collectionRevision), "Adding a nested scene did not invalidate the capture");
        Check(tracker.TryGetKey(out string afterNestedKey, out nestedReason) && afterNestedKey != beforeNestedKey, nestedReason);
        string nestedKey = Key(scene);
        nested.VideoInfo.Height++;
        Check(Key(scene) != nestedKey, "Nested scene edit failed to invalidate");
        Check(Key(new Scene(timeline, scenes, [Guid.NewGuid()])) != Key(scene), "Parent recursion context missing");

        string folder = Path.Combine(Path.GetTempPath(), "ymm-cache-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string imageFile = Path.Combine(folder, "source.png");
        try
        {
            // The key fingerprints input content; it must not load or render the media.
            File.WriteAllBytes(imageFile, [1, 2, 3, 4]);
            var image = new ImageItem { FilePath = imageFile };
            timeline.Items = timeline.Items.Add(image);
            Type[][] builtInReader = [[typeof(Scene)], [], []];
            Check(FrameCacheKey.TryDescribe(scene, builtInReader, out string readerModel, out _, out string readerReason), readerReason);
            Check(readerModel.Contains(typeof(Scene).Assembly.ManifestModule.ModuleVersionId.ToString("D"), StringComparison.Ordinal), "Built-in reader MVID missing from the key model");
            Check(!FrameCacheKey.TryDescribe(scene, [[typeof(Program)], [], []], out _, out _, out readerReason)
                && readerReason.Contains("カスタム読み込み", StringComparison.Ordinal), "External media with a custom source reader did not bypass");
            Check(!tracker.TryGetKey(out _, out _), "Cold external assets did not bypass while hashing");
            string fileKey = WaitForKey(tracker);
            DateTime modified = File.GetLastWriteTimeUtc(imageFile);
            File.WriteAllBytes(imageFile, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(imageFile, modified);
            Check(!tracker.TryGetKey(out _, out _), "Changed external assets did not bypass while rehashing");
            Check(WaitForKey(tracker) != fileKey, "Same-size same-timestamp content replacement failed to invalidate");
            File.Delete(imageFile);
            Check(!tracker.TryGetKey(out _, out string missingReason) && missingReason.Length != 0, "Missing file did not bypass safely");
            timeline.Items = timeline.Items.Remove(image);
        }
        finally
        {
            if (File.Exists(imageFile)) File.Delete(imageFile);
            Directory.Delete(folder);
        }

        timeline.Items = timeline.Items.Add(new TachieItem());
        Check(!FrameCacheKey.TryCreate(scene, out _, out string reason) && reason.Contains("非同期"), "Transient lip-sync was cached");
        tracker.Dispose();
        Check(!tracker.ValidateRevision(tracker.CaptureRevision()) && !tracker.TryGetKey(out _, out _), "Disposed tracker remained usable");
        Console.WriteLine("Cache drawing keys and tracker: empty/text/shape, seek/selection, edit/restore/undo/redo, nested scene, parent context, same-metadata file replacement, missing input, transient lip-sync bypass, revision capture/validation and disposal OK");
        return 0;
    }

    private static string Key(Scene scene)
    {
        Check(FrameCacheKey.TryCreate(scene, out string key, out string reason), reason);
        Check(key.Length == 64, "Invalid SHA256 key");
        return key;
    }
    private static string WaitForKey(KeyDependencyTracker tracker)
    {
        long deadline = Environment.TickCount64 + 15_000;
        do
        {
            if (tracker.TryGetKey(out string key, out _)) return key;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException("External file fingerprinting did not become ready.");
    }
    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
