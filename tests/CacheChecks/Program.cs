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
        // 4.56.1.0's PluginLoader also reads these; the skipped static constructor would have created them empty.
        foreach (var name in new[] { "<IncompatiblePluginAssemblies>k__BackingField", "loadFailures" })
            if (AccessTools.Field(loaderType, name) is { } field) // init-only: FieldInfo.SetValue would throw
                AccessTools.StaticFieldRefAccess<object>(field)() ??= Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GetGenericArguments()))!;
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

        CheckFrameKeys(timeline, nested, tracker);

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
        string reason;
        do
        {
            if (tracker.TryGetKey(out string key, out reason)) return key;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException("External file fingerprinting did not become ready: " + reason);
    }

    private static string WaitForFrameKey(KeyDependencyTracker tracker, int frame)
    {
        long deadline = Environment.TickCount64 + 15_000;
        string reason;
        do
        {
            if (tracker.TryCapture(frame, out var capture, out reason))
                using (capture!) return capture!.Key;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException($"Frame {frame} key did not become ready: {reason}");
    }

    // Per-frame keys: an edit changes only the frames of the edited item, and a frame only needs its own files.
    private static void CheckFrameKeys(Timeline timeline, Timeline nested, KeyDependencyTracker tracker)
    {
        var early = new ShapeItem { Frame = 0, Length = 30, Layer = 1 };
        var late = new ShapeItem { Frame = 60, Length = 30, Layer = 1 };
        timeline.Items = timeline.Items.Add(early).Add(late);
        string at10 = WaitForFrameKey(tracker, 10), at45 = WaitForFrameKey(tracker, 45), at70 = WaitForFrameKey(tracker, 70);
        Check(at10 != at45 && at10 != at70 && at45 != at70, "Frames with different items shared a key");
        Check(WaitForFrameKey(tracker, 29) == at10 && WaitForFrameKey(tracker, 30) == at45, "Item boundaries were not respected");
        late.X.SetFirstValue(5);
        Check(WaitForFrameKey(tracker, 10) == at10 && WaitForFrameKey(tracker, 45) == at45, "Editing one item invalidated unrelated frames");
        string edited70 = WaitForFrameKey(tracker, 70);
        Check(edited70 != at70, "Editing an item did not invalidate its frames");
        late.X.SetFirstValue(0);
        Check(WaitForFrameKey(tracker, 70) == at70, "Restoring an item did not restore its frame keys");
        timeline.VideoInfo.Width++;
        Check(WaitForFrameKey(tracker, 45) != at45, "A timeline setting did not invalidate every frame");
        timeline.VideoInfo.Width--;
        Check(WaitForFrameKey(tracker, 45) == at45, "Restoring a timeline setting did not restore frame keys");

        string folder = Path.Combine(Path.GetTempPath(), "ymm-frame-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string imageFile = Path.Combine(folder, "frame.png");
        try
        {
            File.WriteAllBytes(imageFile, [5, 6, 7, 8]);
            var image = new ImageItem { FilePath = imageFile, Frame = 100, Length = 10, Layer = 2 };
            timeline.Items = timeline.Items.Add(image);
            string withFile = WaitForFrameKey(tracker, 105);
            Check(WaitForFrameKey(tracker, 10) == at10, "Adding an item elsewhere changed unrelated frames");
            File.Delete(imageFile);
            Check(!tracker.TryCapture(105, out _, out string missing) && missing.Length != 0, "A frame whose file is missing did not bypass");
            Check(tracker.TryCapture(10, out var unaffected, out string reason), "A frame without the missing file bypassed: " + reason);
            unaffected!.Dispose();
            File.WriteAllBytes(imageFile, [5, 6, 7, 9]);
            Check(WaitForFrameKey(tracker, 105) != withFile, "Replaced file content did not change the frame key");
            timeline.Items = timeline.Items.Remove(image);
        }
        finally
        {
            if (File.Exists(imageFile)) File.Delete(imageFile);
            Directory.Delete(folder);
        }

        var scene = new SceneItem { Frame = 200, Length = 10, Layer = 3 };
        timeline.Items = timeline.Items.Add(scene);
        string sceneFrame = WaitForFrameKey(tracker, 205);
        Check(WaitForFrameKey(tracker, 10) == at10, "A scene item changed frames it does not cover");
        nested.VideoInfo.Height++;
        Check(WaitForFrameKey(tracker, 205) != sceneFrame, "Another timeline's edit did not invalidate a scene item frame");
        Check(WaitForFrameKey(tracker, 10) == at10 && WaitForFrameKey(tracker, 70) == at70, "Another timeline's edit invalidated ordinary frames");
        nested.VideoInfo.Height--;
        Check(WaitForFrameKey(tracker, 205) == sceneFrame, "Restoring another timeline did not restore the scene item frame");
        timeline.Items = timeline.Items.Remove(scene).Remove(early).Remove(late);
        Console.WriteLine("Per-frame keys: unrelated frames survive edits, boundaries, settings, per-frame files, scene items OK");
    }
    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
