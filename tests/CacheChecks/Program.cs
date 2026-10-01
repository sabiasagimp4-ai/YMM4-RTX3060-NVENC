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
        CheckBundledReaders();
        CheckBundledTachie();
        CheckFramePreparesOwnFiles();
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

        // A font every Windows has: the key fingerprints the font files, and an uninstalled one bypasses.
        var text = new TextItem { Text = "Cache key", Font = "Arial" };
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
            // The idle pre-renderer's clone: seeded with the verified files, it has the same key at once.
            var verified = tracker.VerifiedFingerprints;
            using (var seeded = new KeyDependencyTracker(scene, verified))
                Check(seeded.TryGetKey(out string seededKey, out string seededReason) && seededKey == fileKey,
                    "A tracker seeded with verified files did not capture the same key: " + seededReason);
            DateTime modified = File.GetLastWriteTimeUtc(imageFile);
            File.WriteAllBytes(imageFile, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(imageFile, modified);
            using (var stale = new KeyDependencyTracker(scene, verified))
                Check(!stale.TryGetKey(out _, out _), "A tracker seeded before a same-metadata replacement accepted the replaced file");
            // Another tracker (export, the other preview) verifies the new content first, so the shared index knows it.
            using (var other = new KeyDependencyTracker(scene))
                Check(WaitForKey(other) != fileKey, "A new tracker kept the key of the replaced content");
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
        MeasureCaptureCost();
        MeasureDescribeCost();
        CheckBackgroundDescribe();

        timeline.Items = timeline.Items.Add(new TachieItem());
        Check(!FrameCacheKey.TryCreate(scene, out _, out string reason) && reason.Contains("非同期"), "Transient lip-sync was cached in the whole-project key");
        tracker.Dispose();
        Check(!tracker.ValidateRevision(tracker.CaptureRevision()) && !tracker.TryGetKey(out _, out _), "Disposed tracker remained usable");
        Console.WriteLine("Cache drawing keys and tracker: empty/text/shape, seek/selection, edit/restore/undo/redo, nested scene, parent context, same-metadata file replacement, missing input, transient lip-sync bypass, revision capture/validation and disposal OK");
        return 0;
    }

    // What an edit costs before the next frame can be keyed: the whole project is serialized and split per item again
    // (v2 handoff: measure before replacing it with per-item updates). By kind of item, to see where the time goes.
    private static void MeasureDescribeCost()
    {
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        foreach (var (label, count, make) in new (string, int, Func<int, IItem>)[]
        {
            ("shapes", 100, i => Shape(i, blur: false)),
            ("shapes+blur", 100, i => Shape(i, blur: true)),
            ("texts (Arial)", 100, Text),
            ("shapes", 1000, i => Shape(i, blur: false)),
            ("texts (Arial)", 1000, Text),
            ("shapes+blur / texts", 1000, i => i % 2 == 0 ? Shape(i, blur: true) : Text(i)),
            ("shapes+blur / texts", 3000, i => i % 2 == 0 ? Shape(i, blur: true) : Text(i)),
            ("shapes+blur / texts", 5000, i => i % 2 == 0 ? Shape(i, blur: true) : Text(i)),
        })
        {
            var timeline = new Timeline();
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, count).Select(make));
            var scene = new Scene(timeline, scenes, []);
            bool described = FrameCacheKey.TryDescribe(scene, readers, out string model, out _, out _, out string reason);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int runs = 3;
            for (int run = 0; run < runs; run++) FrameCacheKey.TryDescribe(scene, readers, out _, out _, out _, out _);
            clock.Stop();
            Console.WriteLine($"Describe after an edit, {count} {label}: {clock.Elapsed.TotalMilliseconds / runs:F1} ms, "
                + (described ? $"model {model.Length / 1024} KiB" : "bypassed: " + reason) + $" ({runs} samples; no threshold)");
        }

        // Where the time goes, for 1000 shapes (each phase as TryDescribe does it).
        {
            var timeline = new Timeline();
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, 1000).Select(i => Shape(i, blur: false)));
            var items = timeline.Items.ToArray();
            double Phase(Action action)
            {
                action();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                action();
                return clock.Elapsed.TotalMilliseconds;
            }
            string json = string.Empty;
            double files = Phase(() => { foreach (var item in items) item.GetFiles().ToList(); });
            double resources = Phase(() => { foreach (var item in items) item.GetResources().ToList(); });
            double serialize = Phase(() => json = YukkuriMovieMaker.Json.Json.GetJsonText(new { Timelines = new[] { new { timeline.ID, timeline.Items } } }));
            Newtonsoft.Json.Linq.JObject parsed = null!;
            double parse = Phase(() =>
            {
                using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                parsed = Newtonsoft.Json.Linq.JObject.Load(reader);
            });
            double types = Phase(() => parsed.Descendants().OfType<Newtonsoft.Json.Linq.JProperty>().Count(p => p.Name == "$type"));
            double texts = Phase(() => { foreach (var token in parsed.Descendants().OfType<Newtonsoft.Json.Linq.JObject>().Take(1000)) token.ToString(Newtonsoft.Json.Formatting.None); });
            double whole = Phase(() => FrameCacheKey.TryDescribe(new Scene(timeline, scenes, []), readers, out _, out _, out _, out _));
            Console.WriteLine($"Describe phases, 1000 shapes: GetFiles {files:F0} ms, GetResources {resources:F0} ms, YMM4 JSON {serialize:F0} ms ({json.Length / 1024} KiB), "
                + $"parse {parse:F0} ms, $type scan {types:F0} ms, per-object texts {texts:F0} ms; whole description {whole:F0} ms");
        }

        static IItem Shape(int i, bool blur)
        {
            var shape = new ShapeItem { Frame = i * 3, Length = 30, Layer = i % 10 };
            shape.X.SetFirstValue(i);
            if (blur) shape.VideoEffects = shape.VideoEffects.Add(new YukkuriMovieMaker.Project.Effects.GaussianBlurEffect());
            return shape;
        }
        static IItem Text(int i) => new TextItem { Frame = i * 3, Length = 30, Layer = i % 10, Text = "item " + i, Font = "Arial" };
    }

    // The preview's render thread does not describe a large project itself: it renders normally while a background
    // task does, the first time (by the number of items) and after an edit (by how long the last description took).
    private static void CheckBackgroundDescribe()
    {
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var shapes = Enumerable.Range(0, 1000).Select(i =>
        {
            var shape = new ShapeItem { Frame = i * 3, Length = 30, Layer = i % 10 };
            shape.X.SetFirstValue(i);
            return shape;
        }).ToArray();
        timeline.Items = timeline.Items.AddRange(shapes);
        using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
        TimeSpan Returned(out bool keyed, out string reason)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            keyed = tracker.TryCapture(15, out var capture, out reason, settle: true, background: true);
            capture?.Dispose();
            return clock.Elapsed;
        }
        string Ready(out TimeSpan after)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (tracker.TryCapture(15, out var capture, out _, settle: true, background: true))
                    using (capture!) { after = clock.Elapsed; return capture!.Key; }
                Thread.Sleep(5);
            }
            throw new TimeoutException("The background description never finished");
        }
        var first = Returned(out bool keyed, out string reason);
        Check(!keyed && tracker.Describing && reason.Contains("背景"), "A large project was described on the render thread: " + reason);
        Check(first < TimeSpan.FromMilliseconds(250), $"Starting the background description took {first.TotalMilliseconds:F0} ms");
        string before = Ready(out var ready);
        shapes[5].X.SetFirstValue(-1);
        Thread.Sleep(300); // settle
        var edit = Returned(out keyed, out reason);
        Check(!keyed && reason.Contains("背景"), "After an edit, a slow description ran on the render thread: " + reason);
        Check(edit < TimeSpan.FromMilliseconds(250), $"Starting the description after an edit took {edit.TotalMilliseconds:F0} ms");
        Check(Ready(out var again) != before, "The edited frame kept its key");
        Console.WriteLine($"Background description (1000 items): first capture returned in {first.TotalMilliseconds:F1} ms, key after {ready.TotalMilliseconds:F0} ms; "
            + $"after an edit, returned in {edit.TotalMilliseconds:F1} ms, key after {again.TotalMilliseconds:F0} ms");
    }

    // What per-frame keys save on every cached frame: a frame verifies only its own files (FileDependencyLease),
    // not every file of the project. 200 image items with one file each, one item per 10 frames.
    private static void MeasureCaptureCost()
    {
        const int count = 200;
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        string folder = Path.Combine(Path.GetTempPath(), "ymm-capture-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var items = Enumerable.Range(0, count).Select(i =>
            {
                string file = Path.Combine(folder, $"image{i:000}.png");
                File.WriteAllBytes(file, BitConverter.GetBytes(i));
                return (IItem)new ImageItem { FilePath = file, Frame = i * 10, Length = 10, Layer = 1 };
            });
            timeline.Items = timeline.Items.AddRange(items);
            using var tracker = new KeyDependencyTracker(scene);
            WaitForKey(tracker);
            for (int i = 0; i < count; i += 20) WaitForFrameKey(tracker, i * 10 + 5);
            double Average(Func<int, (bool, KeyCapture?)> capture)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++)
                {
                    var (ok, held) = capture(i);
                    Check(ok, "Capture failed while measuring");
                    held!.Dispose();
                }
                return clock.Elapsed.TotalMilliseconds / 20;
            }
            double whole = Average(_ => (tracker.TryCapture(out KeyCapture? held, out string _), held));
            double frame = Average(i => (tracker.TryCapture((i * 10 % count) * 10 + 5, out KeyCapture? held, out string _), held));
            Console.WriteLine($"Key capture with {count} files in the project: whole project {whole:F2} ms/frame, per frame (1 file) {frame:F2} ms/frame (20 samples; no threshold)");
        }
        finally { Directory.Delete(folder, recursive: true); }
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
        // Background images, textures and image brushes load a file as video or image by the extension settings.
        var extensions = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.FileSettings>.Default.FileExtensions;
        Check(extensions.Count != 0, "No file extension settings to check");
        var extension = extensions[0];
        var fileType = extension.FileType;
        extension.FileType = fileType ^ YukkuriMovieMaker.Settings.FileType.動画;
        Check(WaitForFrameKey(tracker, 10) != at10, "A file type setting did not invalidate frames");
        extension.FileType = fileType;
        Check(WaitForFrameKey(tracker, 10) == at10, "Restoring a file type setting did not restore frame keys");
        extensions.Add(new YukkuriMovieMaker.Settings.FileExtension { Extention = ".nvenccheck", FileType = YukkuriMovieMaker.Settings.FileType.画像 });
        Check(WaitForFrameKey(tracker, 10) != at10, "An added file extension did not invalidate frames");
        extensions.RemoveAt(extensions.Count - 1);
        Check(WaitForFrameKey(tracker, 10) == at10, "Removing the added file extension did not restore frame keys");

        string folder = Path.Combine(Path.GetTempPath(), "ymm-frame-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string imageFile = Path.Combine(folder, "frame.png");
        try
        {
            File.WriteAllBytes(imageFile, [5, 6, 7, 8]);
            var image = new ImageItem { FilePath = imageFile, Frame = 100, Length = 10, Layer = 2 };
            timeline.Items = timeline.Items.Add(image);
            string withFile = WaitForFrameKey(tracker, 105);
            // The cache bars of another preview that only showed frame 10: frame 105 was stored by another tracker,
            // so this one verifies its file in the background and then shows it.
            var barsTimeline = new Timeline();
            barsTimeline.Items = barsTimeline.Items.Add(new ShapeItem { Frame = 0, Length = 30 })
                .Add(new ImageItem { FilePath = imageFile, Frame = 100, Length = 10, Layer = 1 });
            var barsScenes = new Scenes(false);
            barsScenes.AddScene(barsTimeline);
            var barsScene = new Scene(barsTimeline, barsScenes, []);
            using (var stored = new KeyDependencyTracker(barsScene))
            using (var display = new KeyDependencyTracker(barsScene))
            {
                string storedKey = WaitForFrameKey(stored, 105);
                Check(display.TryCapture(10, out var shown, out string shownReason), "The bars' tracker could not capture a frame without files: " + shownReason);
                shown!.Dispose();
                var peeked = new string?[1];
                Check(SpinWait.SpinUntil(() => display.TryPeekFrameKeys([105], peeked, out _) && peeked[0] is not null, TimeSpan.FromSeconds(15))
                    && peeked[0] == storedKey, "The cache bars never showed a frame whose file only another tracker had verified");
            }
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

        // A remote file cannot be fingerprinted (like an uninstalled font): only the frames showing it bypass.
        var remote = new ImageItem { FilePath = "https://example.invalid/ymm4-cache-test.png", Frame = 300, Length = 10, Layer = 4 };
        timeline.Items = timeline.Items.Add(remote);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(305, out _, out string remoteReason) && remoteReason.Length != 0, "A frame with an unverifiable file was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "An unverifiable file elsewhere disabled or changed unrelated frames");
        timeline.Items = timeline.Items.Remove(remote);

        // A tachie's lip sync is asynchronous: its frames render normally, the others stay cached.
        var tachie = new TachieItem { Frame = 300, Length = 10, Layer = 4 };
        timeline.Items = timeline.Items.Add(tachie);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(305, out _, out string tachieReason) && tachieReason.Contains("立ち絵"), "A frame showing a tachie was cached: " + tachieReason);
        Check(WaitForFrameKey(tracker, 10) == at10, "A tachie elsewhere disabled or changed unrelated frames");
        timeline.Items = timeline.Items.Remove(tachie);

        var scene = new SceneItem { Frame = 200, Length = 10, Layer = 3 };
        timeline.Items = timeline.Items.Add(scene);
        string sceneFrame = WaitForFrameKey(tracker, 205);
        Check(WaitForFrameKey(tracker, 10) == at10, "A scene item changed frames it does not cover");
        nested.VideoInfo.Height++;
        Check(WaitForFrameKey(tracker, 205) != sceneFrame, "Another timeline's edit did not invalidate a scene item frame");
        Check(WaitForFrameKey(tracker, 10) == at10 && WaitForFrameKey(tracker, 70) == at70, "Another timeline's edit invalidated ordinary frames");
        nested.VideoInfo.Height--;
        Check(WaitForFrameKey(tracker, 205) == sceneFrame, "Restoring another timeline did not restore the scene item frame");
        // A tachie in another timeline reaches the frames of scene items, not the others.
        var nestedTachie = new TachieItem { Frame = 0, Length = 10 };
        nested.Items = nested.Items.Add(nestedTachie);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(205, out _, out _), "A scene item frame drawing a tachie was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "A tachie in another timeline disabled ordinary frames");
        nested.Items = nested.Items.Remove(nestedTachie);
        timeline.Items = timeline.Items.Remove(scene).Remove(early).Remove(late);
        Console.WriteLine("Per-frame keys: unrelated frames survive edits, boundaries, settings, per-frame files, scene items OK");
    }
    private static bool SkipLoader() => false;
    // Readers in the plugin assemblies YMM4 ships (its folder) are built in, so a project with a file or a font
    // stays cacheable; the same assembly names from user\plugin, or other names in YMM4's folder, are not.
    // A character whose tachie comes from a plugin YMM4 ships: its parameter types (foreign to the host assemblies)
    // and its files only reach tachie frames, so frames without the tachie are keyed and the tachie's are not.
    private static void CheckBundledTachie()
    {
        string hostDirectory = Path.GetDirectoryName(typeof(Scene).Assembly.Location)!;
        string file = Path.Combine(hostDirectory, "YukkuriMovieMaker.Plugin.Tachie.SimpleTachie.dll");
        if (!File.Exists(file))
        {
            Console.WriteLine("Bundled tachie check skipped: no SimpleTachie plugin in the YMM4 folder");
            return;
        }
        var plugin = Assembly.LoadFrom(file);
        var parameterType = plugin.GetTypes().First(type => !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null
            && typeof(YukkuriMovieMaker.Plugin.Tachie.ITachieCharacterParameter).IsAssignableFrom(type));
        var character = new YukkuriMovieMaker.Project.Character { Name = "cache-check-tachie" };
        character.TachieCharacterParameter = (YukkuriMovieMaker.Plugin.Tachie.ITachieCharacterParameter)Activator.CreateInstance(parameterType)!;
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(new ShapeItem { Frame = 0, Length = 30 }).Add(new TachieItem(character) { Frame = 50, Length = 10, Layer = 1 });
        var scene = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(scene, out string model, out _, out string reason), "A project with a bundled tachie was not described: " + reason);
        Check(model.Contains(plugin.GetName().Name!, StringComparison.Ordinal), "Premise: the tachie parameter type is part of the description");
        using var tracker = new KeyDependencyTracker(scene);
        Check(tracker.TryCapture(10, out var capture, out reason), "A frame without the tachie was not keyed: " + reason);
        capture!.Dispose();
        Check(!tracker.TryCapture(55, out _, out reason) && reason.Contains("立ち絵"), "The tachie frame was keyed: " + reason);
        Console.WriteLine($"Bundled tachie ({parameterType.Name}): frames without it keyed, its frames rendered normally");
    }

    // A frame only waits for its own files: with 150 large files in the project, a frame whose small file comes last
    // in the project's order is keyed once that file is verified, while the pass over the others still runs.
    private static void CheckFramePreparesOwnFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ymm-cache-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var timeline = new Timeline();
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            var content = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(content);
            var items = new List<IItem>();
            for (int i = 0; i < 150; i++)
            {
                string other = Path.Combine(folder, $"a{i:D3}.png");
                content[0] = (byte)i;
                File.WriteAllBytes(other, content);
                items.Add(new ImageItem { FilePath = other, Frame = i * 10, Length = 10 });
            }
            string own = Path.Combine(folder, "z-own.png");
            File.WriteAllBytes(own, new byte[1024]);
            items.Add(new ImageItem { FilePath = own, Frame = 2000, Length = 10 });
            timeline.Items = timeline.Items.AddRange(items);
            using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool passRunning = false;
            TimeSpan keyed = TimeSpan.Zero;
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (tracker.TryCapture(2005, out var capture, out _))
                {
                    passRunning = tracker.FingerprintPassRunning;
                    keyed = clock.Elapsed;
                    capture!.Dispose();
                    break;
                }
                Thread.Sleep(1);
            }
            Check(keyed != TimeSpan.Zero, "The frame with its own file was never keyed");
            Check(SpinWait.SpinUntil(() => !tracker.FingerprintPassRunning, TimeSpan.FromSeconds(60)), "The fingerprint pass did not finish");
            var all = clock.Elapsed;
            Check(passRunning, $"The frame waited for every file of the project: keyed after {keyed.TotalMilliseconds:F0} ms, pass {all.TotalMilliseconds:F0} ms");
            Check(WaitForFrameKey(tracker, 5).Length != 0, "Other frames were not keyed after the pass");
            Console.WriteLine($"Frame-first file preparation: frame keyed after {keyed.TotalMilliseconds:F0} ms, all 151 files (300 MiB) after {all.TotalMilliseconds:F0} ms");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void CheckBundledReaders()
    {
        string hostDirectory = Path.GetDirectoryName(typeof(Scene).Assembly.Location)!;
        const string community = "YukkuriMovieMaker.Plugin.Community";
        Check(FrameCacheKey.IsBundledPluginAssembly(community, Path.Combine(hostDirectory, community + ".dll"), hostDirectory),
            "A plugin assembly in YMM4's folder was not built in");
        Check(FrameCacheKey.IsBundledPluginAssembly(community, Path.Combine(hostDirectory, community + ".dll"), hostDirectory + Path.DirectorySeparatorChar),
            "A plugin assembly in YMM4's folder (written with a trailing separator) was not built in");
        Check(!FrameCacheKey.IsBundledPluginAssembly(community, Path.Combine(hostDirectory, "user", "plugin", "Some", community + ".dll"), hostDirectory),
            "A plugin assembly under user\\plugin was built in");
        Check(!FrameCacheKey.IsBundledPluginAssembly("OtherReader", Path.Combine(hostDirectory, "OtherReader.dll"), hostDirectory),
            "An assembly of another name in YMM4's folder was built in");
        Check(!FrameCacheKey.IsBundledPluginAssembly(community, string.Empty, hostDirectory), "An assembly without a file was built in");

        // The real ones: every file and audio reader type in YMM4's plugin assemblies (4.56.1.0: Community's MIDI reader).
        var readers = new List<Type>();
        foreach (string file in Directory.GetFiles(hostDirectory, "YukkuriMovieMaker.Plugin.*.dll"))
        {
            Type?[] types;
            try { types = Assembly.LoadFrom(file).GetTypes(); }
            catch (ReflectionTypeLoadException partial) { types = partial.Types; }
            readers.AddRange(types.OfType<Type>().Where(t => !t.IsAbstract && (typeof(YukkuriMovieMaker.Plugin.FileSource.IVideoFileSourcePlugin).IsAssignableFrom(t)
                || typeof(YukkuriMovieMaker.Plugin.FileSource.IImageFileSourcePlugin).IsAssignableFrom(t)
                || typeof(YukkuriMovieMaker.Plugin.FileSource.IAudioFileSourcePlugin).IsAssignableFrom(t))));
        }
        Check(readers.Count != 0, "No reader types were found in YMM4's plugin assemblies");
        foreach (var reader in readers)
            Check(FrameCacheKey.IsBuiltInSourceReader(reader), $"YMM4's own reader {reader.FullName} ({reader.Assembly.GetName().Name}) was not built in");
        Console.WriteLine($"Built-in readers: {string.Join(", ", readers.Select(r => r.Name))}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
