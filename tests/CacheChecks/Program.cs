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
        return Run(args.Contains("--voice-measure"));
    }

    // Defer binding host model types until the in-place dependency resolver is installed.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(bool voiceMeasureOnly)
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
        VoiceDescriptionMeasurements.Run();
        if (voiceMeasureOnly) return 0; // Additional paired benchmark process; the full CI suite still runs separately.
        VoiceDescriptionChecks.Run();
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
        CheckFingerprintCancellation();
        CheckUnverifiableFiles();
        CheckPeekedKeys();
        CheckIdentitySeeds();
        CheckCommunity();
        CheckImageSequence();
        CheckDynamicDependencies();
        CheckAmbiguousDrawingOrder();
        MeasureValidation();
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

        // A font every Windows has: the key fingerprints the font files.
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
            // A reader whose code was not read: the frames showing a file render normally, the others stay cached.
            Check(FrameCacheKey.TryDescribe(scene, [[typeof(Program)], [], []], out _, out _, out var customFrames, out readerReason), readerReason);
            Check(!customFrames!.For(image.Frame).Cacheable, "External media with a custom source reader did not bypass");
            Check(customFrames.For(image.Frame + image.Length + 10).Cacheable, "A custom source reader disabled frames without files");
            Check(FrameCacheKey.TryDescribe(scene, builtInReader, out _, out _, out var builtInFrames, out readerReason)
                && builtInFrames!.For(image.Frame).Cacheable, "External media with a built-in reader bypassed: " + readerReason);
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
            // Overwritten while YMM4 runs: its sources may show the old or the new content, so it renders normally.
            Check(!tracker.TryGetKey(out _, out _), "Changed external assets did not bypass while rehashing");
            Check(WaitForBypass(() => (tracker.TryGetKey(out _, out string why), why)).Contains("上書き", StringComparison.Ordinal),
                "A file overwritten while YMM4 runs was keyed");
            // After a restart its content is what YMM4 shows. Another tracker (export, the other preview) verifies the
            // new content first, so the shared index knows it.
            HostContent.Forget(imageFile);
            using (var other = new KeyDependencyTracker(scene))
                Check(WaitForKey(other) != fileKey, "A new tracker kept the key of the replaced content");
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
        Check(!FrameCacheKey.TryCreate(scene, out _, out string reason) && reason.Contains("立ち絵"), "Transient lip-sync was cached in the whole-project key: " + reason);
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
        // Inline only if the last description was short; either way the render thread must not wait long.
        Check(edit < TimeSpan.FromMilliseconds(250), $"The capture after an edit took {edit.TotalMilliseconds:F0} ms ({reason})");
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

    // Waits until a capture that keeps failing gives a settled reason (the background verification has finished).
    private static string WaitForBypass(Func<(bool Keyed, string Reason)> capture)
    {
        long deadline = Environment.TickCount64 + 15_000;
        string last = string.Empty;
        do
        {
            var (keyed, reason) = capture();
            if (keyed) throw new InvalidOperationException("Expected the frame to render normally");
            if (reason.Contains("上書き", StringComparison.Ordinal)) return reason;
            last = reason;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        return last;
    }

    // Per-frame keys: an edit changes only the frames of the edited item, and a frame only needs its own files.
    // YMM4's own settings: the UI's (timeline zoom, preview volume) leave a capture valid; the scaling mode the
    // renderers read (FrameCacheKey.DrawingSettings) invalidates it and changes the frame keys.
    private static void CheckUiSettings(KeyDependencyTracker tracker, string at10)
    {
        var settings = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.YMMSettings>.Default;
        Check(tracker.TryCapture(10, out var capture, out string reason), "No capture to check settings with: " + reason);
        using (capture)
        {
            double zoom = settings.TimelineZoom, volume = settings.Volume;
            try
            {
                settings.TimelineZoom = zoom * 1.5 + 1;
                settings.Volume = volume > 0.5 ? volume / 2 : volume + 0.25;
                Check(capture!.Validate(files: false), "A timeline zoom or preview volume change invalidated a capture");
            }
            finally { settings.TimelineZoom = zoom; settings.Volume = volume; }
        }
        Check(WaitForFrameKey(tracker, 10) == at10, "A UI setting changed a frame key");
        var zoomMode = typeof(YukkuriMovieMaker.Settings.YMMSettings).GetProperty(nameof(settings.ZoomMode))!;
        object mode = zoomMode.GetValue(settings)!;
        object other = Enum.GetValues(mode.GetType()).Cast<object>().First(value => !value.Equals(mode));
        Check(tracker.TryCapture(10, out capture, out reason), "No capture to check the scaling mode with: " + reason);
        using (capture)
        {
            try
            {
                zoomMode.SetValue(settings, other);
                Check(!capture!.Validate(files: false), "A scaling mode change left a capture valid");
                Check(WaitForFrameKey(tracker, 10) != at10, "A scaling mode change did not change the frame key");
            }
            finally { zoomMode.SetValue(settings, mode); }
        }
        Check(WaitForFrameKey(tracker, 10) == at10, "Restoring the scaling mode did not restore the frame key");
    }

    // A layer's name, color and volume (the timeline's display, the audio) leave the keys of frames without audio as
    // they are; hiding the layer changes them. Frame 10 shows an item on layer 1.
    private static void CheckLayerSettings(Timeline timeline, KeyDependencyTracker tracker, string at10)
    {
        var layers = timeline.LayerSettings;
        var items = layers.GetType().GetProperty("Items")!;
        var original = items.GetValue(layers)!;
        var settingType = items.PropertyType.GetGenericArguments()[0];
        object empty = items.PropertyType.GetField("Empty")!.GetValue(null)!;
        void Use(string label, byte red, bool hidden, double volume)
        {
            var setting = Activator.CreateInstance(settingType)!;
            settingType.GetProperty("Layer")!.SetValue(setting, 1);
            settingType.GetProperty("Label")!.SetValue(setting, label);
            settingType.GetProperty("Color")!.SetValue(setting, System.Windows.Media.Color.FromRgb(red, 0, 0));
            settingType.GetProperty("IsHidden")!.SetValue(setting, hidden);
            settingType.GetProperty("Volume")!.SetValue(setting, volume);
            items.SetValue(layers, empty.GetType().GetMethod("Add")!.Invoke(empty, [setting]));
        }
        try
        {
            Use("A", 10, hidden: false, volume: 1);
            Console.WriteLine("Layer settings as YMM4 saves them: " + YukkuriMovieMaker.Json.Json.GetJsonText(layers));
            string named = WaitForFrameKey(tracker, 10);
            Use("B", 200, hidden: false, volume: 0.25);
            Check(WaitForFrameKey(tracker, 10) == named, "A layer's name, color or volume changed the key of a frame without audio");
            Use("B", 200, hidden: true, volume: 0.25);
            Check(WaitForFrameKey(tracker, 10) != named, "Hiding a layer did not change the key of a frame showing it");
        }
        finally { items.SetValue(layers, original); }
        Check(WaitForFrameKey(tracker, 10) == at10, "Restoring the layer settings did not restore the frame key");
    }

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
        CheckUiSettings(tracker, at10);
        CheckLayerSettings(timeline, tracker, at10);

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
            // Overwritten while YMM4 runs: only the frames using it render normally, and the idle pre-renderer passes
            // them; the original content again does not undo it (a source may have read the new one meanwhile).
            Check(WaitForBypass(() => (tracker.TryCapture(105, out _, out string why), why)).Contains("上書き", StringComparison.Ordinal)
                && tracker.RendersNormally(105), "A frame whose file was overwritten while YMM4 runs was keyed");
            Check(WaitForFrameKey(tracker, 10) == at10, "An overwritten file elsewhere disabled or changed unrelated frames");
            File.WriteAllBytes(imageFile, [5, 6, 7, 8]);
            Check(WaitForBypass(() => (tracker.TryCapture(105, out _, out string why), why)).Contains("上書き", StringComparison.Ordinal),
                "A file restored to its first content was keyed again while YMM4 runs");
            File.WriteAllBytes(imageFile, [5, 6, 7, 9]);
            WaitForBypass(() => (tracker.TryCapture(105, out _, out string why), why)); // the new content is verified
            HostContent.Forget(imageFile); // as after a restart
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
        // The idle pre-renderer passes over such frames instead of stopping there.
        Check(tracker.RendersNormally(305) && !tracker.RendersNormally(10), "Frames that always render normally were not told apart");
        timeline.Items = timeline.Items.Remove(tachie);
        Check(!tracker.RendersNormally(305), "A frame was still reported as rendered normally after an edit, before the project was described again");

        // Code this plugin did not read (a plugin's effect, item type, or a bundled Community effect, all foreign to
        // the host assemblies): only the frames showing that item render normally.
        var foreignEffect = new ShapeItem { Frame = 300, Length = 10, Layer = 4 };
        foreignEffect.VideoEffects = foreignEffect.VideoEffects.Add(new ForeignBlurEffect());
        timeline.Items = timeline.Items.Add(foreignEffect);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(305, out _, out _), "A frame showing a plugin's effect was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "A plugin's effect elsewhere disabled or changed unrelated frames");
        // A plugin the user trusts in the settings is keyed like YMM4's own code; its MVID is part of every key.
        KnownCode.Trusted = [typeof(Program).Assembly.GetName().Name!];
        try
        {
            Check(WaitForFrameKey(tracker, 305) is { Length: > 0 }, "A trusted plugin's effect was not keyed");
            Check(WaitForFrameKey(tracker, 10) != at10, "Trusting a plugin did not change the keys (its MVID must be part of them)");
        }
        finally { KnownCode.Trusted = []; }
        Check(WaitForFrameKey(tracker, 10) == at10 && !tracker.TryCapture(305, out _, out _), "A plugin no longer trusted stayed keyed");
        timeline.Items = timeline.Items.Remove(foreignEffect);
        var foreignItem = new ForeignShapeItem { Frame = 300, Length = 10, Layer = 4 };
        timeline.Items = timeline.Items.Add(foreignItem);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(305, out _, out _), "A frame showing a plugin's item was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "A plugin's item elsewhere disabled or changed unrelated frames");
        timeline.Items = timeline.Items.Remove(foreignItem);
        Check(WaitForFrameKey(tracker, 305) is { Length: > 0 }, "The frame did not become cacheable again after the plugin's item was removed");

        // Fonts are resolved as YMM4 draws them (FrameCacheKey.ResolveFont): an unknown name is drawn in Arial, so it
        // is cached like Arial; a font settings entry maps a name to a face, and changing it changes the frames'
        // keys; a family DirectWrite does not have is drawn by font fallback from the installed fonts, whose identity
        // (FontEnvironment) every text frame holds.
        string? installed = FontEnvironment.Stamp;
        Check(installed is not null && installed.StartsWith("fonts://", StringComparison.Ordinal) && FontEnvironment.Stamp == installed,
            "The installed fonts could not be identified (or not stably): " + installed);
        var arial = FrameCacheKey.ResolveFont("Arial");
        Check(arial.Files.Any(file => Path.GetFileName(file).StartsWith("arial", StringComparison.OrdinalIgnoreCase)),
            "Arial did not resolve to its files: " + string.Join(", ", arial.Files));
        var unknownFont = FrameCacheKey.ResolveFont("ymm-cache-no-such-font");
        Check(unknownFont.Face.EndsWith("\nArial|400|0|5", StringComparison.Ordinal), "An unknown font name was not drawn as Arial: " + unknownFont.Face);
        var fontText = new TextItem { Frame = 300, Length = 10, Layer = 4, Text = "font", Font = "ymm-cache-alias" };
        timeline.Items = timeline.Items.Add(fontText);
        string unknownFrame = WaitForFrameKey(tracker, 305);
        Check(WaitForFrameKey(tracker, 10) == at10, "A text item elsewhere changed unrelated frames");
        var fontSettings = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.FontSettings>.Default;
        var alias = new YukkuriMovieMaker.Settings.Font { FontName = "ymm-cache-alias", CanonicalFontName = "Arial", CanonicalFontWeight = YukkuriMovieMaker.Settings.FontWeight.Bold };
        fontSettings.CustomFonts.Add(alias);
        try
        {
            string boldFrame = WaitForFrameKey(tracker, 305);
            Check(boldFrame != unknownFrame, "Mapping a font name to another face did not change its frames");
            alias.CanonicalFontWeight = YukkuriMovieMaker.Settings.FontWeight.Normal;
            Check(WaitForFrameKey(tracker, 305) != boldFrame, "Editing a font settings entry did not change its frames");
            string normalFrame = WaitForFrameKey(tracker, 305);
            alias.CanonicalFontName = "ymm-cache-no-such-family";
            Check(FrameCacheKey.ResolveFont("ymm-cache-alias").Files.Length == 0, "A family DirectWrite does not have was given files");
            string fallbackFrame = WaitForFrameKey(tracker, 305);
            Check(fallbackFrame != normalFrame && fallbackFrame != boldFrame, "A frame drawn by font fallback shared the key of the named family's frame");
            Check(WaitForFrameKey(tracker, 10) == at10, "A family DirectWrite does not have changed unrelated frames");
        }
        finally { fontSettings.CustomFonts.Remove(alias); }
        Check(WaitForFrameKey(tracker, 305) == unknownFrame, "Removing the font settings entry did not restore the frames");
        timeline.Items = timeline.Items.Remove(fontText);

        // A font named by a control tag is resolved like the item's font, and the user dictionary's asterisk word sets
        // rewrite the drawn text: both are part of the text frames' keys and of no other frame's.
        var tagged = new TextItem { Frame = 300, Length = 10, Layer = 4, Text = "a<@ymm-cache-tag>b<@> ymm", Font = "Arial" };
        timeline.Items = timeline.Items.Add(tagged);
        string taggedFrame = WaitForFrameKey(tracker, 305);
        var tagFont = new YukkuriMovieMaker.Settings.Font { FontName = "ymm-cache-tag", CanonicalFontName = "Arial", CanonicalFontWeight = YukkuriMovieMaker.Settings.FontWeight.Bold };
        fontSettings.CustomFonts.Add(tagFont);
        try { Check(WaitForFrameKey(tracker, 305) != taggedFrame, "Mapping a font named by a control tag did not change its frames"); }
        finally { fontSettings.CustomFonts.Remove(tagFont); }
        Check(WaitForFrameKey(tracker, 305) == taggedFrame, "Removing the control tag's font entry did not restore the frames");
        var dictionaryType = typeof(Scene).Assembly.GetType("YukkuriMovieMaker.KanjiToYomi.UserDictionary", true)!;
        var dictionary = typeof(YukkuriMovieMaker.Plugin.SettingsBase<>).MakeGenericType(dictionaryType)
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;
        var setsProperty = dictionaryType.GetProperty("AsteriskWordSets")!;
        var originalSets = (System.Collections.Immutable.ImmutableList<YukkuriMovieMaker.KanjiToYomi.WordSet>)setsProperty.GetValue(dictionary)!;
        var word = new YukkuriMovieMaker.KanjiToYomi.WordSet("ymm", "YMM");
        setsProperty.SetValue(dictionary, originalSets.Add(word));
        try
        {
            string rewritten = WaitForFrameKey(tracker, 305);
            Check(rewritten != taggedFrame, "Adding an asterisk word set did not change the text frames");
            Check(WaitForFrameKey(tracker, 10) == at10, "An asterisk word set changed frames without text");
            word.To = "YMM4";
            Check(WaitForFrameKey(tracker, 305) != rewritten, "Editing an asterisk word set did not change the text frames");
        }
        finally { setsProperty.SetValue(dictionary, originalSets); }
        Check(WaitForFrameKey(tracker, 305) == taggedFrame, "Removing the asterisk word set did not restore the text frames");
        timeline.Items = timeline.Items.Remove(tagged);

        // The MIDI reader YMM4 ships synthesizes with its own settings and SoundFonts: with a MIDI file in the
        // project, frames that read audio (an audio spectrum shape) render normally, the others stay cached.
        var spectrumType = typeof(Scene).Assembly.GetType("YukkuriMovieMaker.Shape.AudioSpectrumShapePlugin", true)!;
        var spectrumPlugin = (YukkuriMovieMaker.Plugin.Shape.IShapePlugin)Activator.CreateInstance(spectrumType, nonPublic: true)!;
        var spectrum = new ShapeItem { Frame = 300, Length = 10, Layer = 4, ShapeType2 = spectrumType, ShapeParameter = spectrumPlugin.CreateShapeParameter(null) };
        timeline.Items = timeline.Items.Add(spectrum);
        WaitForFrameKey(tracker, 305);
        string midiFolder = Path.Combine(Path.GetTempPath(), "ymm-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(midiFolder);
        string midiFile = Path.Combine(midiFolder, "song.mid");
        try
        {
            File.WriteAllBytes(midiFile, "MThd"u8.ToArray());
            var midi = new AudioItem { FilePath = midiFile, Frame = 400, Length = 10, Layer = 5 };
            timeline.Items = timeline.Items.Add(midi);
            WaitForFrameKey(tracker, 10);
            Check(!tracker.TryCapture(305, out _, out _) && tracker.RendersNormally(305), "An audio spectrum frame was cached with a MIDI file in the project");
            Check(WaitForFrameKey(tracker, 10) == at10, "A MIDI file disabled or changed frames that do not read audio");
            timeline.Items = timeline.Items.Remove(midi);
            Check(WaitForFrameKey(tracker, 305) is { Length: > 0 }, "The audio spectrum frame did not become cacheable again without the MIDI file");
        }
        finally
        {
            timeline.Items = timeline.Items.Remove(spectrum);
            if (File.Exists(midiFile)) File.Delete(midiFile);
            Directory.Delete(midiFolder);
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
        // A tachie in another timeline reaches the frames of scene items, not the others.
        var nestedTachie = new TachieItem { Frame = 0, Length = 10 };
        nested.Items = nested.Items.Add(nestedTachie);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(205, out _, out _), "A scene item frame drawing a tachie was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "A tachie in another timeline disabled ordinary frames");
        nested.Items = nested.Items.Remove(nestedTachie);
        // So does a plugin's effect there.
        var nestedForeign = new ShapeItem { Frame = 0, Length = 10 };
        nestedForeign.VideoEffects = nestedForeign.VideoEffects.Add(new ForeignBlurEffect());
        nested.Items = nested.Items.Add(nestedForeign);
        WaitForFrameKey(tracker, 10);
        Check(!tracker.TryCapture(205, out _, out _), "A scene item frame drawing a plugin's effect was cached");
        Check(WaitForFrameKey(tracker, 10) == at10, "A plugin's effect in another timeline disabled ordinary frames");
        nested.Items = nested.Items.Remove(nestedForeign);
        timeline.Items = timeline.Items.Remove(scene).Remove(early).Remove(late);
        Console.WriteLine("Per-frame keys: unrelated frames survive edits, boundaries, settings, per-frame files, scene items OK");
    }
    // YMM4 seeds random moves and some effects with object identities, so a copy of the project (another session, the
    // idle pre-renderer's clone) draws them otherwise: their frames are keyed by the objects, other frames are not.
    // Text revealed in random order (seeded by YMM4's text source) renders normally.
    private static void CheckIdentitySeeds()
    {
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var still = new ShapeItem { Frame = 0, Length = 30, Layer = 0 };
        var shaking = new ShapeItem { Frame = 60, Length = 30, Layer = 1 };
        shaking.X.AnimationType = YukkuriMovieMaker.Commons.AnimationType.ランダム移動;
        var shaken = new ShapeItem { Frame = 120, Length = 30, Layer = 2 };
        var randomMove = (YukkuriMovieMaker.Plugin.Effects.IVideoEffect)Activator.CreateInstance(
            typeof(Scene).Assembly.GetType("YukkuriMovieMaker.Project.Effects.RandomMoveEffect", true)!, nonPublic: true)!;
        shaken.VideoEffects = shaken.VideoEffects.Add(randomMove);
        var randomText = new TextItem { Frame = 180, Length = 30, Layer = 3, Text = "abc", Font = "Arial", DisplayInterval = 100,
            DisplayDirection = TypewriterAnimationDirection.Random };
        timeline.Items = timeline.Items.Add(still).Add(shaking).Add(shaken).Add(randomText);
        var scene = new Scene(timeline, scenes, []);
        using var tracker = new KeyDependencyTracker(scene);
        string stillKey = WaitForFrameKey(tracker, 10), shakingKey = WaitForFrameKey(tracker, 70), shakenKey = WaitForFrameKey(tracker, 130);
        Check(!tracker.RendersNormally(10) && tracker.RendersNormally(70) && tracker.RendersNormally(130),
            "Frames keyed by object identities were not told apart (the idle pre-renderer must pass them)");
        Check(!tracker.TryCapture(190, out _, out _), "Text revealed in random order was cached");
        Check(WaitForFrameKey(tracker, 70) == shakingKey && WaitForFrameKey(tracker, 130) == shakenKey, "The same objects changed their keys");
        var copyTimeline = YukkuriMovieMaker.Json.Json.LoadFromText<Timeline>(YukkuriMovieMaker.Json.Json.GetJsonText(timeline))!;
        var copyScenes = new Scenes(false);
        copyScenes.AddScene(copyTimeline);
        using var copy = new KeyDependencyTracker(new Scene(copyTimeline, copyScenes, []));
        Check(WaitForFrameKey(copy, 10) == stillKey, "A copy of the project changed a frame without randomness");
        Check(WaitForFrameKey(copy, 70) != shakingKey, "A random move kept its key in a copy of the project");
        Check(WaitForFrameKey(copy, 130) != shakenKey, "A random effect kept its key in a copy of the project");
        shaking.X.AnimationType = YukkuriMovieMaker.Commons.AnimationType.なし;
        WaitForFrameKey(tracker, 70);
        Check(!tracker.RendersNormally(70), "A frame without randomness any more stayed keyed by its objects");
        Console.WriteLine("Identity-seeded randomness: random moves and effects keyed by their objects (a copy differs), random text order not cached");
    }

    // The bundled Community plugin's namespaces read for 4.56.1.0 are keyed without trusting it; the others render
    // normally (MotionBlur draws from the frames drawn before); CameraShake seeds with its own identity.
    private static void CheckCommunity()
    {
        string hostDirectory = Path.GetDirectoryName(typeof(Scene).Assembly.Location)!;
        string file = Path.Combine(hostDirectory, KnownCode.CommunityAssembly + ".dll");
        if (!File.Exists(file)) { Console.WriteLine("Community check skipped: no Community plugin in the YMM4 folder"); return; }
        var community = Assembly.LoadFrom(file);
        if (!KnownCode.Capture().Identity.StartsWith("community:ac765de8", StringComparison.Ordinal))
        {
            Console.WriteLine("Community check skipped: not the audited 4.56.1.0 build (its effects render normally)");
            return;
        }
        YukkuriMovieMaker.Plugin.Effects.IVideoEffect Effect(string name) => (YukkuriMovieMaker.Plugin.Effects.IVideoEffect)Activator.CreateInstance(
            community.GetType("YukkuriMovieMaker.Plugin.Community.Effect.Video." + name, true)!, nonPublic: true)!;
        ShapeItem With(int frame, YukkuriMovieMaker.Plugin.Effects.IVideoEffect effect)
        {
            var shape = new ShapeItem { Frame = frame, Length = 30, Layer = frame / 30 };
            shape.VideoEffects = shape.VideoEffects.Add(effect);
            return shape;
        }
        var lensType = community.GetType("YukkuriMovieMaker.Plugin.Community.Shape.LensFlare.LensFlareShapePlugin", true)!;
        var lens = (YukkuriMovieMaker.Plugin.Shape.IShapePlugin)Activator.CreateInstance(lensType, nonPublic: true)!;
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(With(0, Effect("Bloom.BloomEffect"))).Add(With(60, Effect("MotionBlur.MotionBlurEffect")))
            .Add(With(120, Effect("CameraShake.CameraShakeEffect")))
            .Add(new ShapeItem { Frame = 180, Length = 30, Layer = 6, ShapeType2 = lensType, ShapeParameter = lens.CreateShapeParameter(null) })
            .Add(With(360, Effect("CircularBlur.CircularBlurEffect")));
        using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
        Check(WaitForFrameKey(tracker, 10) is { Length: > 0 } && !tracker.RendersNormally(10), "A Community effect that was read was not keyed");
        Check(!tracker.TryCapture(70, out _, out _), "Community MotionBlur (draws from the frames drawn before) was keyed");
        Check(!tracker.TryCapture(370, out _, out _), "Community CircularBlur (its edges depend on the frame drawn before) was keyed");
        Check(WaitForFrameKey(tracker, 130) is { Length: > 0 } && tracker.RendersNormally(130), "Community CameraShake was not keyed by its identity");
        Check(WaitForFrameKey(tracker, 190) is { Length: > 0 }, "A Community shape that was read was not keyed");
        Console.WriteLine("Community (4.56.1.0): read effects and shapes keyed, MotionBlur and CircularBlur rendered normally, CameraShake keyed by its identity");

        // ShuffleText and ShuffleTextInOut (random characters seeded by the frame) and NumberText (a number formatted
        // with the culture) are keyed with the files of the font their Font property names; NumberText's frames only
        // on a thread with the culture they were described with.
        var numberType = community.GetType("YukkuriMovieMaker.Plugin.Community.Shape.NumberText.NumberText", true)!;
        var number = ((YukkuriMovieMaker.Plugin.Shape.IShapePlugin)Activator.CreateInstance(numberType, nonPublic: true)!).CreateShapeParameter(null);
        var shuffle = Effect("ShuffleText.ShuffleTextEffect");
        var shuffleInOut = Effect("ShuffleTextInOut.ShuffleTextInOutEffect");
        foreach (object part in new object[] { number, shuffle, shuffleInOut }) part.GetType().GetProperty("Font")!.SetValue(part, "Arial");
        timeline.Items = timeline.Items.Add(With(240, shuffle)).Add(With(270, shuffleInOut))
            .Add(new ShapeItem { Frame = 300, Length = 30, Layer = 10, ShapeType2 = numberType, ShapeParameter = number });
        string[] arialFiles = FrameCacheKey.ResolveFont("Arial").Files;
        foreach (int frame in new[] { 250, 280, 310 }) Check(WaitForFrameKey(tracker, frame) is { Length: > 0 }, $"A Community text at frame {frame} was not keyed");
        Check(FrameCacheKey.TryDescribe(new Scene(timeline, scenes, []), FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var frames, out string reason), reason);
        foreach (int frame in new[] { 250, 280, 310 })
            Check(arialFiles.Length != 0 && arialFiles.All(frames!.For(frame).Files.Contains), $"The Community text at frame {frame} does not depend on its font's files");
        Check(!frames!.For(10).Files.Intersect(arialFiles).Any(), "A frame without text depends on font files");
        Check(frames.For(310).Culture && !frames.For(250).Culture && !frames.For(10).Culture, "Only NumberText's frames format with the culture");
        string numberKey = WaitForFrameKey(tracker, 310), shuffleKey = WaitForFrameKey(tracker, 250);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture.Name == "de-DE" ? "en-US" : "de-DE");
        try
        {
            Check(!tracker.TryCapture(310, out _, out _), "A NumberText frame was keyed on a thread with another number format");
            Check(WaitForFrameKey(tracker, 250) == shuffleKey, "Another culture changed a frame without NumberText");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        Check(WaitForFrameKey(tracker, 310) == numberKey, "NumberText was not keyed again on the described culture");
        Console.WriteLine("Community text (4.56.1.0): ShuffleText, ShuffleTextInOut and NumberText keyed with their font files; NumberText only on its culture");
    }

    // A numbered image played as a video (YMM4's sequence reader): a root frame depends on the one image it shows, at
    // 60 images per second through the item's time mapping (frame rate, playback rate, loop), and the files the reader
    // listed must not change while YMM4 runs.
    private static void CheckImageSequence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ymm-cache-sequence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string Image(int index) => Path.Combine(directory, $"shot{index}.png");
            for (int i = 0; i < 12; i++) File.WriteAllBytes(Image(i), [(byte)i]);
            File.WriteAllBytes(Image(20), [20]); // after a gap: not part of the sequence
            File.WriteAllBytes(Path.Combine(directory, "other3.png"), [3]);
            Check(ImageSequence.Files(Image(0)) is { } listed && listed.SequenceEqual(Enumerable.Range(0, 12).Select(Image)),
                "The sequence is not the contiguous numbers from the file's");
            // The reader lists the numbers in order from the lowest and keeps those continuing from the file's own: a
            // file after the first one of its name gives an empty list (it is then not a sequence).
            Check(ImageSequence.Files(Image(5)) is null && ImageSequence.Files(Path.Combine(directory, "other3.png"))?.Length == 1
                && ImageSequence.Files(Path.Combine(directory, "missing7.png")) is null, "A sequence was listed unlike the reader lists it");

            var timeline = new Timeline();
            timeline.VideoInfo.FPS = 30;
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            var video = new VideoItem { FilePath = Image(0), Frame = 0, Length = 12, Layer = 0 };
            timeline.Items = timeline.Items.Add(video);
            var scene = new Scene(timeline, scenes, []);
            FrameDependencyIndex Describe()
            {
                Check(FrameCacheKey.TryDescribe(scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var frames, out string reason), reason);
                return frames!;
            }
            void Expect(string setting, Func<int, int> image)
            {
                var frames = Describe();
                for (int frame = 0; frame < video.Length; frame++)
                {
                    var files = frames.For(frame).Files;
                    string expected = Image(image(frame));
                    Check(frames.For(frame).Cacheable && files.Contains(expected) && files.All(file => file == expected || file == Image(0)),
                        $"{setting}: frame {frame} must depend on {Path.GetFileName(expected)} only, not {string.Join(", ", files.Select(Path.GetFileName))}");
                }
            }
            Expect("30 fps", frame => Math.Min(2 * frame, 11));
            video.IsLooped = true;
            Expect("30 fps, looped", frame => 2 * frame % 12);
            video.IsLooped = false;
            video.PlaybackRate2.SetFirstValue(50);
            Expect("30 fps, 50 %", frame => frame);
            video.PlaybackRate2.SetFirstValue(100);
            timeline.VideoInfo.FPS = 60;
            Expect("60 fps", frame => frame);
            timeline.VideoInfo.FPS = 30;

            using var tracker = new KeyDependencyTracker(scene);
            string second = WaitForFrameKey(tracker, 2), third = WaitForFrameKey(tracker, 3);
            Check(second != third, "Two frames showing different images share a key");
            // An image overwritten while YMM4 runs: only the frame showing it stops being keyed by its old content.
            File.WriteAllBytes(Image(6), [66, 66]);
            File.SetLastWriteTimeUtc(Image(6), DateTime.UtcNow.AddMinutes(1));
            long deadline = Environment.TickCount64 + 15_000;
            bool stale = true;
            while (stale && Environment.TickCount64 < deadline)
            {
                stale = tracker.TryCapture(3, out var capture, out _) && capture!.Key == third;
                capture?.Dispose();
                if (stale) Thread.Sleep(20);
            }
            Check(!stale, "A frame kept the key of an image overwritten since");
            Check(WaitForFrameKey(tracker, 2) == second, "Overwriting one image changed a frame showing another");

            // The reader keeps the list it read: a sequence whose files changed since is not keyed until a restart.
            File.WriteAllBytes(Image(12), [12]);
            Check(!Describe().For(0).Cacheable, "A sequence whose files changed while YMM4 runs was keyed");
            File.Delete(Image(12));
            Check(Describe().For(0).Cacheable, "A sequence back to the list first read was not keyed");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Console.WriteLine("Image sequence: each frame depends on the image it shows (30/60 fps, loop, playback rate), an overwritten image only re-keys its frames, a changed file list is not keyed");
    }

    private static bool SkipLoader() => false;

    // Stand-ins for a plugin's code: types outside the host assemblies.
    private sealed class ForeignBlurEffect : YukkuriMovieMaker.Project.Effects.GaussianBlurEffect { }

    private sealed class DynamicBlurEffect : YukkuriMovieMaker.Project.Effects.GaussianBlurEffect, ICacheDependencyProvider
    {
        [Newtonsoft.Json.JsonIgnore] public bool CanCaptureOnCurrentThread => true;
        [Newtonsoft.Json.JsonIgnore] public string ExternalState { get; set; } = "initial";
        [Newtonsoft.Json.JsonIgnore] public bool FailValidation { get; set; }
        [Newtonsoft.Json.JsonIgnore] public Action? OnValidate { get; set; }
        public CacheDependencySnapshot CaptureDependencies(long ticks) => new("test/dynamic-blur", "1", ExternalState, "cpu", [new("previous-input", ExternalState, ticks - 1, ticks)]);
        public bool IsCurrent(CacheDependencySnapshot snapshot) { OnValidate?.Invoke(); return !FailValidation && snapshot.StateToken == ExternalState; }
    }
    private static void CheckDynamicDependencies()
    {
        var previousTrust = KnownCode.Trusted.ToArray();
        KnownCode.Trusted = previousTrust.Append(typeof(Program).Assembly.GetName().Name!).ToArray();
        try
        {
            var effect = new DynamicBlurEffect();
            var timeline = new Timeline(); var scenes = new Scenes(false); scenes.AddScene(timeline);
            var item = new ShapeItem { Frame = 0, Length = 20 }; item.VideoEffects = [effect]; timeline.Items = timeline.Items.Add(item);
            var scene = new Scene(timeline, scenes, []);
            Check(FrameCacheKey.CaptureDynamicProviders(scene).Contains(effect), "Dynamic provider not found in host animatable tree");
            using var tracker = new KeyDependencyTracker(scene);
            Check(tracker.TryCapture(5, out var first, out string reason), reason);
            using (first!)
            {
                Check(first!.Validate(), "Initial dynamic capture not valid");
                string before = first.Key;
                effect.ExternalState = "changed-without-project-notification";
                Check(!first.Validate(), "Hidden dependency change did not invalidate active capture");
                Check(tracker.TryCapture(5, out var next, out reason), reason);
                using (next!) Check(next!.Key != before && next.Validate(), "Dynamic state missing from frame key");
            }
            effect.FailValidation = true;
            Check(!tracker.TryCapture(5, out _, out _), "Failing dynamic provider did not bypass cache");
            effect.FailValidation = false;
            Check(tracker.TryCapture(5, out var reentrant, out reason), reason);
            using (reentrant)
            {
                effect.OnValidate = () => { effect.OnValidate = null; timeline.VideoInfo.BackgroundColor = System.Windows.Media.Colors.Red; };
                Check(!reentrant!.Validate(files: false), "Provider re-entry edited the model but validated its old capture");
            }
            Check(tracker.TryCapture(5, out var trusted, out reason), reason);
            using (trusted)
            {
                KnownCode.Trusted = previousTrust;
                Check(!trusted!.Validate(files: false), "Trust change left an active capture valid before the next capture");
            }
            KnownCode.Trusted = previousTrust.Append(typeof(Program).Assembly.GetName().Name!).ToArray();
            var front = new DynamicBlurEffect { ExternalState = "red" };
            var back = new DynamicBlurEffect { ExternalState = "blue" };
            item.VideoEffects = [front, back];
            Check(tracker.TryCapture(5, out var ordered, out reason), reason);
            using (ordered)
            {
                (front.ExternalState, back.ExternalState) = (back.ExternalState, front.ExternalState);
                Check(tracker.TryCapture(5, out var swapped, out reason), reason);
                using (swapped) Check(ordered!.Model == swapped!.Model && ordered.Key != swapped.Key,
                    "Hidden state swap between identical host effect slots aliased the frame key");
            }
            Console.WriteLine("Dynamic host dependencies: animatable discovery, hidden state keys, post-capture validation and fail-closed bypass passed.");
        }
        finally { KnownCode.Trusted = previousTrust; }
    }
    // Measurement, not a check: what validating a capture and its parts cost per call on one thread (the render thread
    // runs a capture's validation several times per frame: live, GPU and RAM reuse, the postfix and the deferred store).
    private static void MeasureValidation()
    {
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var shape = new ShapeItem { Frame = 0, Length = 30, Layer = 1 };
        timeline.Items = timeline.Items.Add(shape);
        var previousTrust = KnownCode.Trusted;
        using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
        static double Time(string name, int count, Func<bool> action)
        {
            for (int i = 0; i < 2000; i++) if (!action()) throw new InvalidOperationException(name + " failed");
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) if (!action()) throw new InvalidOperationException(name + " failed");
            double micro = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds / count;
            Console.WriteLine($"  {name}: {micro:F2} us/call ({count} calls)");
            return micro;
        }
        Console.WriteLine("Validation costs on the real host (one thread, no threshold):");
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        Time("FrameCacheKey.DrawingSettings", 20000, () => FrameCacheKey.DrawingSettings().Length > 0);
        Time("FrameCacheKey.SourceReadersMatch", 20000, () => FrameCacheKey.SourceReadersMatch(readers));
        WaitForFrameKey(tracker, 5);
        Check(tracker.TryCapture(5, out var plain, out string reason), reason);
        using (plain)
        {
            Time("KeyCapture.Validate(files: false), no providers", 20000, () => plain!.Validate(files: false));
            Time("KeyDependencyTracker.TryCapture, described, no files", 5000, () => { bool ok = tracker.TryCapture(5, out var c, out _); c?.Dispose(); return ok; });
        }
        try
        {
            KnownCode.Trusted = previousTrust.Append(typeof(Program).Assembly.GetName().Name!).ToArray();
            shape.VideoEffects = [new DynamicBlurEffect()];
            WaitForFrameKey(tracker, 5);
            Check(tracker.TryCapture(5, out var dynamic, out reason), reason);
            using (dynamic) Time("KeyCapture.Validate(files: false), one dynamic provider", 20000, () => dynamic!.Validate(files: false));
        }
        finally { KnownCode.Trusted = previousTrust; }
    }

    private static void CheckAmbiguousDrawingOrder()
    {
        var timeline = new Timeline(); var scenes = new Scenes(false); scenes.AddScene(timeline);
        var first = new ShapeItem { Frame = 0, Length = 20, Layer = 1 };
        var second = new ShapeItem { Frame = 10, Length = 20, Layer = 1 };
        timeline.Items = timeline.Items.Add(first).Add(second);
        var scene = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var frames, out string reason), reason);
        Check(frames!.For(5).Cacheable && !frames.For(10).Cacheable && !frames.For(19).Cacheable && frames.For(20).Cacheable,
            "Same-layer overlap must bypass only its affected frames");
        second.Layer = 2;
        Check(FrameCacheKey.TryDescribe(scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out frames, out reason), reason);
        Check(frames!.For(15).Cacheable, "Distinct layers must remain cacheable");
        Console.WriteLine("Host order certificate: overlapping ties bypass, disjoint frames and distinct layers reuse.");
    }
    private sealed class ForeignShapeItem : ShapeItem { }
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

    // A file that cannot be verified (here: behind a directory junction, as in a OneDrive folder) only disables the
    // frames showing it. They render normally for good, so the idle pre-renderer passes them (RendersNormally), and
    // the file is retried on its own: the project's other files are not opened again and again.
    private static void CheckUnverifiableFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ymm-unverifiable-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(folder, "real"), link = Path.Combine(folder, "link");
        Directory.CreateDirectory(real);
        var retry = KeyDependencyTracker.UnverifiableRetry;
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                mklink!.WaitForExit();
            Check(Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Could not create the test junction");
            File.WriteAllBytes(Path.Combine(real, "plain.png"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(real, "linked.png"), [5, 6, 7, 8]);
            var timeline = new Timeline();
            timeline.Items = timeline.Items
                .Add(new ImageItem { FilePath = Path.Combine(real, "plain.png"), Frame = 0, Length = 10, Layer = 1 })
                .Add(new ImageItem { FilePath = Path.Combine(link, "linked.png"), Frame = 10, Length = 10, Layer = 1 })
                .Add(new ImageItem { FilePath = Path.Combine(real, "plain.png"), Frame = 20, Length = 10, Layer = 1 });
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            KeyDependencyTracker.UnverifiableRetry = TimeSpan.FromMilliseconds(200);
            using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
            WaitForFrameKey(tracker, 5);
            string reason = string.Empty;
            Check(SpinWait.SpinUntil(() => !tracker.TryCapture(15, out _, out reason) && tracker.RendersNormally(15), TimeSpan.FromSeconds(15)),
                "A frame whose file cannot be verified was not marked to render normally: " + reason);
            Check(reason.Contains("検証できません", StringComparison.Ordinal), "Unexpected reason for the unverifiable file: " + reason);
            Check(!tracker.RendersNormally(5) && !tracker.RendersNormally(25), "Frames without the unverifiable file render normally");
            // Longer than the 5 s between passes before: captures of the frame and the cache bars over every frame.
            long opens = FileDependencyLease.FileOpens;
            var keys = new string?[30];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(6))
            {
                tracker.TryCapture(15, out _, out _);
                tracker.TryPeekFrameKeys(Enumerable.Range(0, 30).ToArray(), keys, out _);
                Thread.Sleep(50);
            }
            Check(FileDependencyLease.FileOpens == opens, $"The project's files were opened again {FileDependencyLease.FileOpens - opens} times because of one unverifiable file");
            Check(keys[5] is not null && keys[15] is null && keys[25] is not null, "The cache bars lost the frames without the unverifiable file");
            // The file moves out of the junction (an edit): its frames are cached again.
            ((ImageItem)timeline.Items[1]).FilePath = Path.Combine(real, "linked.png");
            Check(!tracker.RendersNormally(15) || SpinWait.SpinUntil(() => !tracker.RendersNormally(15), TimeSpan.FromSeconds(5)), "An edit did not clear the unverifiable frame");
            WaitForFrameKey(tracker, 15);
            Console.WriteLine("Unverifiable file: only its frames render normally, idle passes them, no repeated project-wide verification OK");
        }
        finally
        {
            KeyDependencyTracker.UnverifiableRetry = retry;
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(folder, recursive: true);
        }
    }

    // The cache bars' keys (TryPeekFrameKeys: a segment's frames decided once, keys hashed outside the tracker's gate)
    // are the keys captures use, for frames in any order and repeated; its key stamp changes with an edit only.
    private static void CheckPeekedKeys()
    {
        var timeline = new Timeline();
        for (int i = 0; i < 40; i++)
            timeline.Items = timeline.Items.Add(new ShapeItem { Frame = i * 7, Length = 11 + i % 5, Layer = i % 3 + 1 });
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
        int[] frames = [.. Enumerable.Range(0, 320), .. Enumerable.Range(0, 320).Reverse(), 5, 300, 5, 17, 17, 400];
        var expected = frames.Distinct().ToDictionary(frame => frame, frame => WaitForFrameKey(tracker, frame));
        var peeked = new string?[frames.Length];
        for (int pass = 0; pass < 2; pass++) // composed, then from the tracker's cache
        {
            long stamp = tracker.KeyStamp;
            Array.Clear(peeked);
            Check(tracker.TryPeekFrameKeys(frames, peeked, out _), "The cache bars' keys were unavailable");
            for (int i = 0; i < frames.Length; i++)
                Check(peeked[i] == expected[frames[i]], $"The cache bars' key of frame {frames[i]} (pass {pass}) differs from its capture's");
            Check(tracker.KeyStamp == stamp, "Asking for keys changed the key stamp");
        }
        Check(expected[5] != expected[300] && expected[17] != expected[5], "Frames of different items shared a key");
        long before = tracker.KeyStamp;
        ((ShapeItem)timeline.Items[3]).X.SetFirstValue(5); // frames 21-34
        Check(tracker.KeyStamp != before, "An edit did not change the key stamp");
        Check(WaitForFrameKey(tracker, 25) != expected[25] && WaitForFrameKey(tracker, 300) == expected[300], "The edit changed the wrong frames");
        Check(tracker.TryPeekFrameKeys([25, 300], peeked, out _) && peeked[0] == WaitForFrameKey(tracker, 25) && peeked[1] == expected[300],
            "The cache bars' keys after an edit differ from the captures'");
        Console.WriteLine("Cache bar keys: equal to the captures' keys in any frame order, cached per segment, stamp follows edits OK");
    }

    // A frame only waits for its own files: with 150 large files in the project, a frame whose small file comes last
    // in the project's order is keyed once that file is verified, while the pass over the others still runs.
    private static void CheckFingerprintCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = Task.Run(() => KeyDependencyTracker.FingerprintSafely(["cancelled-before-open.png"], null, cancellation.Token));
        var result = task.GetAwaiter().GetResult();
        Check(task.IsCompletedSuccessfully && result.Files is null && result.Reason.Contains("中断"),
            "Cancelled fingerprint work escaped as a faulted/abandoned task");
        // Optional-cache failures also return unavailable; no partial result becomes an accepted key.
        var failure = Task.Run(() => KeyDependencyTracker.FingerprintSafely(["ignored"], null, default, leading: 2));
        var unavailable = failure.GetAwaiter().GetResult();
        Check(failure.IsCompletedSuccessfully && unavailable.Files is null && unavailable.Reason.Contains("失敗"),
            "Fingerprint worker failure escaped as an unobserved task exception");
        Console.WriteLine("Fingerprint worker: edit/disposal cancellation and optional failures return unavailable without task faults");
    }

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
