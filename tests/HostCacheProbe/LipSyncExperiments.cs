using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Windows.Threading;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Plugin.Voice;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;

// Temporary experiments (not a check, to be reverted): what a tachie's lip sync makes a frame depend on, measured on
// the real host (YMM4 4.56.1.0, WARP). Prints results; a failing experiment prints its exception and the rest go on.
internal static class LipSyncExperiments
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const BindingFlags Constructors = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const int Fps = 30, Width = 320, Height = 180;
    private static Assembly host = null!;
    private static Assembly animation = null!;
    private static string work = null!;
    private static Type sessionType = null!, envelopeType = null!, audioSourceType = null!, tachieSourceType = null!;
    private static MethodInfo tryGetValue = null!;
    private static PropertyInfo publishedCount = null!;
    private static readonly Dictionary<string, (string Wav, double Seconds, string Hash)> envelopes = [];

    internal static void Run(Assembly hostAssembly, string hostDir)
    {
        Setup(hostAssembly, hostDir);
        Step("envelope", Envelope);
        Step("render", () => Render(hostDir));
        Step("voice files and idle clones", VoiceFiles);
        Step("voice cache in the model", VoiceCacheModel);
        try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A second process: the per-process string hash YMM4 seeds default blinks with, the envelopes, a temporary voice file.
    internal static int Child(Assembly hostAssembly, string hostDir, string[] args)
    {
        Setup(hostAssembly, hostDir);
        int at = Array.IndexOf(args, "--lipsync-child");
        string directory = args[at + 1];
        var (start, interval) = Blink(directory);
        Console.WriteLine($"CHILD|directory-hash={directory.GetHashCode()}|blink-start={start}|blink-interval={interval}");
        object mode = SettingsBase<YMMSettings>.Default.GetVoiceUpsamplingMode();
        var character = new Character { Name = "child" };
        for (int i = at + 2; i + 1 < args.Length; i += 2)
        {
            var voice = CustomVoice(character, args[i], double.Parse(args[i + 1], CultureInfo.InvariantCulture), 0);
            Console.WriteLine($"CHILD|envelope={Hash(Envelope(voice, 4, mode, out _))}|{Path.GetFileName(args[i])}");
        }
        var cached = CachedVoice(character, WavBytes(24000, 1.0, 5));
        Console.WriteLine($"CHILD|cached-voice-file={cached.FilePath}");
        return 0;
    }

    private static void Setup(Assembly hostAssembly, string hostDir)
    {
        host = hostAssembly;
        work = Path.Combine(Path.GetTempPath(), "ymm-lipsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var bootstrap = new Harmony("ymm.tests.lipsync-loader");
        bootstrap.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(LipSyncExperiments), nameof(SkipLoader)));
        var assemblies = ProbeLoader.Assemblies(host).ToList();
        foreach (string name in new[] { "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie", "YukkuriMovieMaker.Plugin.Tachie.Psd", "YukkuriMovieMaker.Plugin.Tachie.SimpleTachie" })
        {
            string file = Path.Combine(hostDir, name + ".dll");
            if (!File.Exists(file)) { Console.WriteLine("missing " + file); continue; }
            assemblies.Add(AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(file));
        }
        ProbeLoader.Stub(assemblies);
        animation = assemblies.Single(a => a.GetName().Name == "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie");
        sessionType = host.GetType("YukkuriMovieMaker.Player.Audio.LipSyncEnvelopeSession", true)!;
        envelopeType = host.GetType("YukkuriMovieMaker.Player.Audio.LipSyncEnvelope", true)!;
        audioSourceType = host.GetType("YukkuriMovieMaker.Player.Audio.EffectedItemSource", true)!;
        tachieSourceType = host.GetType("YukkuriMovieMaker.Player.Video.Items.TachieSource", true)!;
        tryGetValue = sessionType.GetMethod("TryGetValue", Any)!;
        publishedCount = sessionType.GetProperty("PublishedFrameCount", Any)!;
        var factory = host.GetType("YukkuriMovieMaker.Plugin.TachieSourceFactory", true)!;
        List<object> plugins;
        try { plugins = ((System.Collections.IEnumerable)factory.GetProperty("Plugins", Any)!.GetValue(null)!).Cast<object>().ToList(); }
        catch (Exception error) { Console.WriteLine("TachieSourceFactory.Plugins failed: " + error.GetBaseException().Message); plugins = []; }
        Console.WriteLine("Tachie plugins (PluginLoader): " + string.Join(", ", plugins.Select(p => p.GetType().FullName)));
        if (!plugins.Any(p => p.GetType().Assembly == animation))
        {
            var direct = assemblies.Where(a => a.GetName().Name!.Contains(".Tachie.", StringComparison.Ordinal)).SelectMany(a => a.GetTypes())
                .Where(t => !t.IsAbstract && typeof(ITachiePlugin).IsAssignableFrom(t)).Select(t => (ITachiePlugin)Activator.CreateInstance(t)!).ToList();
            AccessTools.StaticFieldRefAccess<IEnumerable<ITachiePlugin>>(AccessTools.Field(factory, "<Plugins>k__BackingField"))() = direct;
            Console.WriteLine("Tachie plugins registered directly: " + string.Join(", ", direct.Select(p => p.GetType().FullName)));
        }
    }

    private static bool SkipLoader() => false;

    private static void Step(string name, Action body)
    {
        Console.WriteLine($"### {name}");
        var clock = Stopwatch.StartNew();
        try { body(); }
        catch (Exception error) { Console.WriteLine($"!!! {name} failed: {error}"); }
        Console.WriteLine($"### {name}: {clock.ElapsedMilliseconds} ms");
    }

    // ---- The volume envelope (LipSyncEnvelope.Build over the voice as TachieSource reads it) ----

    private static void Envelope()
    {
        object mode = SettingsBase<YMMSettings>.Default.GetVoiceUpsamplingMode();
        Console.WriteLine($"Voice upsampling mode (default settings): {mode}; processors {Environment.ProcessorCount}");
        var character = new Character { Name = "envelope" };
        foreach (var (label, hz, seconds, seed) in new[] { ("5 s 24 kHz", 24000, 5.0, 1), ("60 s 24 kHz", 24000, 60.0, 2), ("600 s 16 kHz", 16000, 600.0, 3), ("5 s 48 kHz", 48000, 5.0, 4) })
        {
            string wav = WriteWav($"voice-{seed}.wav", hz, seconds, seed);
            var voice = CustomVoice(character, wav, seconds, 0);
            var first = Envelope(voice, 4, mode, out double ms1);
            var second = Envelope(voice, 4, mode, out double ms2);
            int unpublished = first.Count(double.IsNaN);
            Console.WriteLine($"Envelope {label}: {voice.Length} frames, build {ms1:F1} / {ms2:F1} ms ({seconds * 1000 / Math.Min(ms1, ms2):F0}x real time), "
                + $"identical twice: {Same(first, second)}, unpublished frames: {unpublished}, max {first.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Max():F4}, sha {Hash(first)}");
            envelopes[label] = (wav, seconds, Hash(first));
            if (seed is 1 or 4)
                foreach (object other in Enum.GetValues(mode.GetType()))
                {
                    var values = Envelope(voice, 4, other, out double ms);
                    int differing = values.Zip(first).Count(p => BitConverter.DoubleToInt64Bits(p.First) != BitConverter.DoubleToInt64Bits(p.Second));
                    double maxDiff = values.Zip(first).Max(p => Math.Abs(p.First - p.Second));
                    Console.WriteLine($"  upsampling {other}: {ms:F1} ms, frames differing from {mode}: {differing}/{values.Length}, max |diff| {maxDiff:G3}");
                }
            if (seed == 1)
                foreach (int smooth in new[] { 1, 2, 8, 20 })
                {
                    var values = Envelope(voice, smooth, mode, out double ms);
                    Console.WriteLine($"  smooth {smooth}: {ms:F1} ms, sha {Hash(values)}, frames differing from smooth 4: {values.Zip(first).Count(p => p.First != p.Second)}");
                }
        }
    }

    private static double[] Envelope(VoiceItem voice, int smooth, object mode, out double milliseconds)
    {
        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        var constructor = audioSourceType.GetConstructors(Constructors).Single(c => c.GetParameters().Length == 8);
        object session = Activator.CreateInstance(sessionType, Constructors, null, new object?[] { voice.Length }, null)!;
        var clock = Stopwatch.StartNew();
        using (var source = (IDisposable)constructor.Invoke(new object?[] { voice, scene, 48000, Fps, mode, true, true, true }))
            envelopeType.GetMethod("Build", Any)!.Invoke(null, new object?[] { source, Fps, smooth, voice.Length, session, CancellationToken.None });
        milliseconds = clock.Elapsed.TotalMilliseconds;
        var values = new double[voice.Length + 1];
        for (int frame = 0; frame < values.Length; frame++) values[frame] = Value(session, frame) ?? double.NaN;
        return values;
    }

    private static double? Value(object session, int frame)
    {
        object?[] args = [frame, 0.0];
        return (bool)tryGetValue.Invoke(session, args)! ? (double)args[1]! : null;
    }

    private static bool Same(double[] a, double[] b) =>
        a.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.Select(BitConverter.DoubleToInt64Bits));

    private static string Hash(double[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())))[..16];

    // ---- Rendering a tachie through TimelineSource ----

    private sealed record Volume(int? Frame, int Length, double Result, bool Authentic, double WaitMs, bool Dispatcher, int Published);
    private sealed record Parts(string? Mouth, string? Eye, TimeSpan BlinkStart, TimeSpan BlinkInterval);
    private sealed record Shot(int Frame, string Hash, Volume? Volume, Parts? Parts, double UpdateMs, double TotalMs);
    private static readonly List<Volume> volumes = [];
    private static readonly List<Parts> partRecords = [];
    private static int recordThread;

    private static void ReadVolumePrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    // Authentic: the value the envelope holds for the frame (or -1 outside the voice), not a fallback after a timeout.
    private static void ReadVolumePostfix(double __result, object?[] __args, long __state)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref recordThread)) return;
        int? frame = (int?)__args[1];
        int length = (int)__args[2]!;
        object? session = __args[3];
        bool inside = frame is int f && f >= 0 && f < length;
        bool authentic;
        int published = -1;
        if (!inside) authentic = __result == -1.0;
        else if (session is null) authentic = false;
        else
        {
            authentic = Value(session, frame!.Value) is double held && BitConverter.DoubleToInt64Bits(held) == BitConverter.DoubleToInt64Bits(__result);
            published = (int)publishedCount.GetValue(session)!;
        }
        lock (volumes) volumes.Add(new(frame, length, __result, authentic, Stopwatch.GetElapsedTime(__state).TotalMilliseconds,
            Dispatcher.FromThread(Thread.CurrentThread) is not null, published));
    }

    private static void AnimationPostfix(object __instance)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref recordThread)) return;
        var type = __instance.GetType();
        string? LayerFile(string field)
        {
            object layer = type.GetField(field, Any)!.GetValue(__instance)!;
            return (string?)layer.GetType().GetProperty("FilePath", Any)!.GetValue(layer);
        }
        lock (partRecords) partRecords.Add(new(LayerFile("mouthLayer"), LayerFile("eyeLayer"),
            (TimeSpan)type.GetField("sozaiMabatakiStart", Any)!.GetValue(__instance)!, (TimeSpan)type.GetField("sozaiMabatakiSpan", Any)!.GetValue(__instance)!));
    }

    private static Shot Shoot(ITimelineSource source, IGraphicsDevicesAndContext context, int frame, TimelineSourceUsage usage)
    {
        Volatile.Write(ref recordThread, Environment.CurrentManagedThreadId);
        int volumesBefore, partsBefore;
        lock (volumes) volumesBefore = volumes.Count;
        lock (partRecords) partsBefore = partRecords.Count;
        var clock = Stopwatch.StartNew();
        source.Update(FrameTime.FrameToTime(frame, Fps), usage);
        double update = clock.Elapsed.TotalMilliseconds;
        byte[] pixels = TimelineFrameCache.Capture(context.DeviceContext, source.Output, Width, Height, new Vector2(-Width / 2f, -Height / 2f))!;
        double total = clock.Elapsed.TotalMilliseconds;
        Volume? volume;
        Parts? parts;
        lock (volumes) volume = volumes.Count > volumesBefore ? volumes[^1] : null;
        lock (partRecords) parts = partRecords.Count > partsBefore ? partRecords[^1] : null;
        return new(frame, Convert.ToHexString(SHA256.HashData(pixels))[..12], volume, parts, update, total);
    }

    private static ITimelineSource NewSource(IGraphicsDevicesAndContext context, Scene scene) =>
        (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Constructors, null, new object?[] { context, scene, null }, null)!;

    private static void OnThread(string name, bool dispatcher, Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (dispatcher) _ = Dispatcher.CurrentDispatcher;
                body();
            }
            catch (Exception error) { failure = error; }
        }) { Name = name, IsBackground = true };
        if (dispatcher) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) Console.WriteLine($"!!! thread {name} failed: {failure}");
    }

    private static void Render(string hostDir)
    {
        string parts = Path.Combine(work, "parts");
        Directory.CreateDirectory(parts);
        WritePng(parts, "body.png", (x, y) => (90, 90, 100, 255));
        WritePng(parts, "eye.png", (x, y) => Eyes(x, y, 10));
        WritePng(parts, "eye.0.png", (x, y) => Eyes(x, y, 1));
        WritePng(parts, "eye.1.png", (x, y) => Eyes(x, y, 5));
        WritePng(parts, "mouth.png", (x, y) => Mouth(x, y, 16, (220, 40, 40)));
        WritePng(parts, "mouth.0.png", (x, y) => Mouth(x, y, 1, (220, 40, 40)));
        WritePng(parts, "mouth.1.png", (x, y) => Mouth(x, y, 5, (220, 40, 40)));
        WritePng(parts, "mouth.2.png", (x, y) => Mouth(x, y, 10, (220, 40, 40)));
        var vowelColors = new Dictionary<string, (byte, byte, byte)> { ["a"] = (250, 120, 0), ["i"] = (0, 200, 120), ["u"] = (0, 120, 250), ["e"] = (200, 0, 200), ["o"] = (250, 250, 0) };
        foreach (var (vowel, color) in vowelColors) WritePng(parts, $"mouth.{vowel}.png", (x, y) => Mouth(x, y, 12, color));
        string body = Path.Combine(parts, "body.png"), eye = Path.Combine(parts, "eye.png"), mouth = Path.Combine(parts, "mouth.png");

        var character = new Character { Name = "lipsync" };
        character.TachieType = animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.AnimationTachiePlugin", true);
        object characterParameter = Activator.CreateInstance(animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.CharacterParameter", true)!)!;
        Set(characterParameter, "Directory", parts);
        character.TachieCharacterParameter = (ITachieCharacterParameter)characterParameter;
        object itemParameter = Activator.CreateInstance(animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.ItemParameter", true)!)!;
        Set(itemParameter, "Body", body);
        Set(itemParameter, "Eye", eye);
        Set(itemParameter, "Mouth", mouth);
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width;
        timeline.VideoInfo.Height = Height;
        timeline.VideoInfo.FPS = Fps;
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        string wav = WriteWav("render.wav", 24000, 6.0, 11);
        var voice = CustomVoice(character, wav, 6.0, 15);
        var tachie = new TachieItem(character) { Frame = 0, Length = 12 * Fps, Layer = 1 };
        tachie.TachieItemParameter = (ITachieItemParameter)itemParameter;
        timeline.Items = timeline.Items.Add(tachie).Add(voice);
        var scene = new Scene(timeline, scenes, []);
        int frames = tachie.Length;
        int voiceEnd = voice.Frame + voice.Length;
        Console.WriteLine($"Scene: tachie 0..{frames}, voice {voice.Frame}..{voiceEnd} ({voice.FilePath}), smooth {character.MouseSmooth}, parts {parts}");

        var probe = new Harmony("ymm.tests.lipsync-probe");
        probe.Patch(tachieSourceType.GetMethod("ReadVolumeAfterRequiredWait", Any)!,
            prefix: new HarmonyMethod(typeof(LipSyncExperiments), nameof(ReadVolumePrefix)), postfix: new HarmonyMethod(typeof(LipSyncExperiments), nameof(ReadVolumePostfix)));
        var animationSource = animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.AnimationTachieSource", true)!;
        probe.Patch(animationSource.GetMethod("Update", Any, [typeof(TachieSourceDescription)])!, postfix: new HarmonyMethod(typeof(LipSyncExperiments), nameof(AnimationPostfix)));
        try
        {
            Shot[] baseline = [];
            int loud = -1;
            OnThread("render", false, () =>
            {
                Console.WriteLine($"Render thread has a dispatcher before rendering: {Dispatcher.FromThread(Thread.CurrentThread) is not null}");
                using var devices = new GraphicsDevices();
                using var context = devices.CreateContext();
                Shot?[] Pass(TimelineSourceUsage usage, IEnumerable<int> order, bool fresh)
                {
                    var shots = new Shot?[frames];
                    ITimelineSource? source = null;
                    try
                    {
                        foreach (int frame in order)
                        {
                            if (fresh || source is null)
                            {
                                source?.Dispose();
                                source = NewSource(context, scene);
                            }
                            shots[frame] = Shoot(source, context, frame, usage);
                        }
                    }
                    finally { source?.Dispose(); }
                    return shots;
                }
                baseline = Pass(TimelineSourceUsage.Exporting, Enumerable.Range(0, frames), false)!;
                Console.WriteLine($"Render thread has a dispatcher after rendering: {Dispatcher.FromThread(Thread.CurrentThread) is not null}");
                Compare("export, in order (baseline)", baseline, baseline);
                Compare("export, in order, again", baseline, Pass(TimelineSourceUsage.Exporting, Enumerable.Range(0, frames), false));
                Compare("playing, in order", baseline, Pass(TimelineSourceUsage.Playing, Enumerable.Range(0, frames), false));
                var shuffled = Enumerable.Range(0, frames).OrderBy(f => (f * 7919) % 104729).ToArray();
                Compare("paused, shuffled (seeks)", baseline, Pass(TimelineSourceUsage.Paused, shuffled, false));
                var sample = Enumerable.Range(0, 24).Select(i => voice.Frame + i * voice.Length / 24).ToArray();
                Compare("paused, a new source per frame (cold envelope)", baseline, Pass(TimelineSourceUsage.Paused, sample, true));
                Compare("playing, backwards", baseline, Pass(TimelineSourceUsage.Playing, Enumerable.Range(0, frames).Reverse(), false));

                // The picture as a function of the inputs: the blink from the directory's string hash, the mouth from the volume.
                var (start, interval) = Blink(parts);
                int eyeCount = PartsCount(eye), mouthCount = PartsCount(mouth);
                int blinkFields = baseline.Count(s => s.Parts is { } p && p.BlinkStart == start && p.BlinkInterval == interval);
                int eyes = baseline.Count(s => s.Parts?.Eye == AnimatedPart(eye, eyeCount, Mabataki(FrameTime.FrameToTime(s.Frame - tachie.Frame, Fps), start, interval)));
                int closed = baseline.Count(s => s.Parts?.Eye != eye);
                int mouths = baseline.Count(s => s.Volume is { } v && s.Parts?.Mouth == AnimatedPart(mouth, mouthCount, v.Result * 10.0 * 100.0 / 100.0));
                var shapes = baseline.Where(s => s.Parts is not null).GroupBy(s => Path.GetFileName(s.Parts!.Mouth)).Select(g => $"{g.Key}:{g.Count()}");
                Console.WriteLine($"Blink: directory hash {parts.GetHashCode()}, start {start}, interval {interval}; the source's fields match on {blinkFields}/{frames} frames; "
                    + $"eye part predicted on {eyes}/{frames} frames ({closed} frames not fully open); parts counts eye {eyeCount}, mouth {mouthCount}");
                Console.WriteLine($"Mouth part predicted from the volume on {mouths}/{frames} frames; parts used: {string.Join(", ", shapes)}");
                var reference = Envelope(voice, character.MouseSmooth, SettingsBase<YMMSettings>.Default.GetVoiceUpsamplingMode(), out double referenceMs);
                int equalToBuild = baseline.Count(s => s.Volume is { Frame: int f } v && f >= 0 && f < voice.Length
                    && BitConverter.DoubleToInt64Bits(v.Result) == BitConverter.DoubleToInt64Bits(reference[f]));
                Console.WriteLine($"Volumes the renders used equal a separate LipSyncEnvelope.Build on {equalToBuild}/{voice.Length} voice frames (build {referenceMs:F1} ms)");
                loud = baseline.Where(s => s.Volume is { Frame: int f } && f >= 0 && f < voice.Length && s.Parts?.Mouth != AnimatedPart(mouth, mouthCount, 0))
                    .OrderByDescending(s => s.Volume!.Result).Select(s => s.Frame).FirstOrDefault(-1);
                timeline.Items = timeline.Items.Remove(tachie);
                try
                {
                    var plain = Pass(TimelineSourceUsage.Exporting, Enumerable.Range(0, frames), false)!;
                    Console.WriteLine($"Cost per frame, export in order: with the tachie Update {baseline.Average(s => s!.UpdateMs):F2} ms / Update+readback {baseline.Average(s => s!.TotalMs):F2} ms; "
                        + $"without it {plain.Average(s => s!.UpdateMs):F2} / {plain.Average(s => s!.TotalMs):F2} ms");
                }
                finally { timeline.Items = timeline.Items.Insert(0, tachie); }
            });
            if (loud < 0) { Console.WriteLine("No open-mouth frame found; skipping the timeout experiments"); return; }
            Console.WriteLine($"Loudest voice frame {loud}: volume {baseline[loud].Volume!.Result:F4}, mouth {Path.GetFileName(baseline[loud].Parts?.Mouth)}");

            // Two envelope computations may run at once in the whole process (TachieSource.envelopeCalculationGate). Holding
            // both slots makes a frame wait for its volume: on a thread with a Dispatcher at most 2.5 s, then the frame is
            // drawn as silent and later frames of that voice stop waiting until the envelope is done.
            var gate = (SemaphoreSlim)tachieSourceType.GetField("envelopeCalculationGate", Any)!.GetValue(null)!;
            OnThread("dispatcher", true, () =>
            {
                using var devices = new GraphicsDevices();
                using var context = devices.CreateContext();
                using var source = NewSource(context, scene);
                Shoot(source, context, 0, TimelineSourceUsage.Paused);
                gate.Wait();
                gate.Wait();
                Shot held;
                Shot[] next;
                try
                {
                    held = Shoot(source, context, loud, TimelineSourceUsage.Paused);
                    next = Enumerable.Range(loud + 1, 5).Select(f => Shoot(source, context, f, TimelineSourceUsage.Paused)).ToArray();
                }
                finally { gate.Release(2); }
                Thread.Sleep(1500);
                var after = Shoot(source, context, loud, TimelineSourceUsage.Paused);
                Console.WriteLine($"Dispatcher thread, envelope blocked: frame {loud} waited {held.Volume?.WaitMs:F0} ms, volume {held.Volume?.Result}, authentic {held.Volume?.Authentic}, "
                    + $"dispatcher {held.Volume?.Dispatcher}, same as export {held.Hash == baseline[loud].Hash}, mouth {Path.GetFileName(held.Parts?.Mouth)}");
                Console.WriteLine($"  next 5 frames: max wait {next.Max(s => s.Volume?.WaitMs ?? 0):F1} ms, authentic {next.Count(s => s.Volume?.Authentic == true)}, "
                    + $"same as export {next.Count(s => s.Hash == baseline[s.Frame].Hash)}");
                Console.WriteLine($"  after the envelope finished: waited {after.Volume?.WaitMs:F1} ms, authentic {after.Volume?.Authentic}, same as export {after.Hash == baseline[loud].Hash}");
            });
            OnThread("blocking", false, () =>
            {
                using var devices = new GraphicsDevices();
                using var context = devices.CreateContext();
                using var source = NewSource(context, scene);
                Shoot(source, context, 0, TimelineSourceUsage.Playing);
                gate.Wait();
                gate.Wait();
                using var release = new Timer(_ => gate.Release(2), null, 3000, Timeout.Infinite);
                var shot = Shoot(source, context, loud, TimelineSourceUsage.Playing);
                Console.WriteLine($"Thread without a Dispatcher (as the preview's), envelope blocked for 3 s: waited {shot.Volume?.WaitMs:F0} ms, authentic {shot.Volume?.Authentic}, "
                    + $"same as export {shot.Hash == baseline[loud].Hash}, Update {shot.UpdateMs:F0} ms");
            });

            // Vowel lip sync: the mouth follows VoiceItem.LipSyncFrames (from Rhubarb or the voice engine, saved in the project).
            object face = Activator.CreateInstance(animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.FaceParameter", true)!)!;
            var modeType = animation.GetType("YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.MouthAnimationMode", true)!;
            Set(face, "MouthAnimation", Enum.Parse(modeType, "VowelLipSyncPriority"));
            MouthShape[] cycle = [MouthShape.A, MouthShape.I, MouthShape.U, MouthShape.E, MouthShape.O, MouthShape.Silent];
            voice.LipSyncFrames = Enumerable.Range(0, 24).Select(i => new LipSyncFrame(TimeSpan.FromSeconds(i * 0.25), cycle[i % cycle.Length])).ToArray();
            voice.TachieFaceParameter = (ITachieFaceParameter)face;
            OnThread("vowels", false, () =>
            {
                using var devices = new GraphicsDevices();
                using var context = devices.CreateContext();
                using var source = NewSource(context, scene);
                int matches = 0, total = 0;
                foreach (int frame in Enumerable.Range(voice.Frame, voice.Length))
                {
                    var shot = Shoot(source, context, frame, TimelineSourceUsage.Exporting);
                    TimeSpan time = FrameTime.FrameToTime(frame - voice.Frame, Fps);
                    var shape = voice.LipSyncFrames.LastOrDefault(f => f.Time <= time)?.Shape ?? MouthShape.Silent;
                    string expected = Path.Combine(parts, "mouth." + shape switch { MouthShape.A => "a", MouthShape.I => "i", MouthShape.U => "u", MouthShape.E => "e", MouthShape.O => "o", _ => "0" } + ".png");
                    total++;
                    if (shot.Parts?.Mouth == expected) matches++;
                }
                Console.WriteLine($"Vowel lip sync: mouth predicted from LipSyncFrames on {matches}/{total} frames");
            });
            Check(FrameCacheKey.TryDescribe(scene, out string model, out _, out string reason), "Describe failed: " + reason);
            Console.WriteLine($"Model with the tachie: {model.Length} chars, holds LipSyncFrames {model.Contains("\"LipSyncFrames\"", StringComparison.Ordinal)}, "
                + $"MouseSmooth {model.Contains("\"MouseSmooth\"", StringComparison.Ordinal)}, VoiceLength {model.Contains("\"VoiceLength\"", StringComparison.Ordinal)}, "
                + $"MouthSensitivity {model.Contains("\"MouthSensitivity\"", StringComparison.Ordinal)}, TachieFaceParameter {model.Contains("\"TachieFaceParameter\"", StringComparison.Ordinal)}");

            // The default blink seeds with string.GetHashCode, which differs per process.
            var args = new List<string> { parts };
            foreach (var (_, (file, seconds, _)) in envelopes.Where(e => e.Key is "5 s 24 kHz" or "60 s 24 kHz")) { args.Add(file); args.Add(seconds.ToString(CultureInfo.InvariantCulture)); }
            string child = RunChild(hostDir, args);
            Console.WriteLine("Child process:" + Environment.NewLine + string.Join(Environment.NewLine, child.Split('\n').Where(l => l.StartsWith("CHILD|", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal)).Select(l => "  " + l.TrimEnd())));
            var (ownStart, ownInterval) = Blink(parts);
            Console.WriteLine($"  this process: directory-hash={parts.GetHashCode()}|blink-start={ownStart}|blink-interval={ownInterval}; envelopes {string.Join(", ", envelopes.Select(e => e.Key + "=" + e.Value.Hash))}");
        }
        finally { probe.UnpatchAll(probe.Id); }
    }

    private static void Compare(string label, Shot[] baseline, Shot?[] shots)
    {
        int compared = 0, same = 0, authentic = 0, authenticSame = 0, fallback = 0, fallbackDiffer = 0;
        double maxWait = 0, update = 0, total = 0;
        foreach (var shot in shots)
        {
            if (shot is null) continue;
            compared++;
            bool equal = shot.Hash == baseline[shot.Frame].Hash;
            if (equal) same++;
            if (shot.Volume?.Authentic ?? true) { authentic++; if (equal) authenticSame++; }
            else { fallback++; if (!equal) fallbackDiffer++; }
            maxWait = Math.Max(maxWait, shot.Volume?.WaitMs ?? 0);
            update += shot.UpdateMs;
            total += shot.TotalMs;
        }
        Console.WriteLine($"{label}: {compared} frames, same picture as export {same}, authentic volume {authentic} (same {authenticSame}), fallback {fallback} (differ {fallbackDiffer}), "
            + $"max volume wait {maxWait:F1} ms, mean Update {update / Math.Max(1, compared):F2} ms, with readback {total / Math.Max(1, compared):F2} ms");
    }

    // AnimationTachieSource (4.56.1.0): an unset blink start/interval is 5 s + StatelessRandom(directory hash, 0 / 1) * 5 s.
    private static (TimeSpan Start, TimeSpan Interval) Blink(string directory)
    {
        int seed = directory.GetHashCode();
        return (TimeSpan.FromSeconds(5L) + TimeSpan.FromSeconds(StatelessRandom.GetDouble(seed, 0L) * 5.0),
            TimeSpan.FromSeconds(5L) + TimeSpan.FromSeconds(StatelessRandom.GetDouble(seed, 1L) * 5.0));
    }

    private static double Mabataki(TimeSpan time, TimeSpan start, TimeSpan interval)
    {
        double num = 0.3;
        double num2 = (time - start).TotalSeconds % (num + interval.TotalSeconds) / num;
        if (!(num2 >= 0.0) || !(num2 <= 0.5))
        {
            if (!(num2 >= 0.5) || !(num2 <= 1.0)) return 1.0;
            return (num2 - 0.5) * 2.0;
        }
        return 1.0 - num2 * 2.0;
    }

    private static int PartsCount(string file)
    {
        int i = 0;
        while (File.Exists(PartPath(file, i))) i++;
        return i + 1;
    }

    private static string PartPath(string file, int number) =>
        Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.{number}{Path.GetExtension(file)}");

    private static string AnimatedPart(string file, int count, double rate)
    {
        int number = (int)Math.Floor(count * rate);
        if (number < 0) number = 0;
        return count - 1 > number ? PartPath(file, number) : file;
    }

    private static string RunChild(string hostDir, IEnumerable<string> extra)
    {
        string process = Environment.ProcessPath!;
        var info = new ProcessStartInfo(process) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add(hostDir);
        info.ArgumentList.Add("--lipsync-child");
        foreach (string argument in extra) info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        var error = child.StandardError.ReadToEndAsync();
        string output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        return output + "\n" + error.Result + $"\nexit {child.ExitCode}";
    }

    // ---- Voice files: temporary files, project voice caches, idle clones ----

    private static VoiceItem CustomVoice(Character character, string wav, double seconds, int frame)
    {
        var voice = new VoiceItem(character) { Frame = frame, Layer = 0 };
        typeof(VoiceItem).GetField("customVoiceFilePath", Any)!.SetValue(voice, wav);
        typeof(VoiceItem).GetProperty(nameof(VoiceItem.VoiceLength))!.GetSetMethod(true)!.Invoke(voice, new object?[] { TimeSpan.FromSeconds(seconds) });
        voice.Length = (int)Math.Ceiling(seconds * Fps);
        return voice;
    }

    // As YMM4 loads a voice item saved with the project's voice cache: the cache is decompressed to a temporary file.
    private static VoiceItem CachedVoice(Character character, byte[] wav)
    {
        var voice = new VoiceItem(character) { Frame = 0, Length = 90, Serif = "cached" };
        voice.VoiceCache = Brotli(wav);
        ((Task)typeof(VoiceItem).GetMethod("CreateVoiceFileFromCacheAsync", Any)!.Invoke(voice, null)!).GetAwaiter().GetResult();
        return voice;
    }

    private static void VoiceFiles()
    {
        var temporary = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("YukkuriMovieMaker", StringComparison.Ordinal))
            .Select(a => a.GetType("YukkuriMovieMaker.Commons.TemporaryFile")).FirstOrDefault(t => t is not null);
        try
        {
            if (temporary is not null)
            {
                object Create() => temporary.GetConstructor(Type.EmptyTypes)?.Invoke(null)
                    ?? temporary.GetConstructors().First().Invoke(temporary.GetConstructors().First().GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
                using var first = (IDisposable)Create();
                using var second = (IDisposable)Create();
                var name = temporary.GetProperty("FullName")!;
                Console.WriteLine($"TemporaryFile ({temporary.Assembly.GetName().Name}): {name.GetValue(first)} / {name.GetValue(second)}");
            }
            else Console.WriteLine("TemporaryFile type not found");
        }
        catch (Exception error) { Console.WriteLine("TemporaryFile: " + error.GetBaseException().Message); }
        var character = new Character { Name = "cached-voice" };
        var voice = CachedVoice(character, WavBytes(24000, 3.0, 21));
        var copy = (VoiceItem)voice.GetClone();
        Console.WriteLine($"Voice from the project's voice cache: FilePath {voice.FilePath}, VoiceLength {voice.VoiceLength}; its clone (GetClone): {copy.FilePath}");
        string wav = WriteWav("custom.wav", 24000, 3.0, 22);
        var custom = new VoiceItem(character) { Frame = 200, Length = 90, Hatsuon = wav, Serif = "custom" };
        typeof(VoiceItem).GetField("customVoiceFilePath", Any)!.SetValue(custom, wav);

        var timeline = new Timeline();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(voice).Add(custom).Add(new ShapeItem { Frame = 400, Length = 30 });
        var scene = new Scene(timeline, scenes, []);
        Check(FrameCacheKey.TryDescribe(scene, out string model, out _, out string reason), "Describe failed: " + reason);
        var clone = IdleFramePreRenderer.CloneSceneFromModel(model);
        var clonedVoices = clone.Timeline.Items.OfType<VoiceItem>().ToArray();
        Console.WriteLine($"Idle clone (from the model): voice FilePaths {string.Join(", ", clonedVoices.Select(v => v.FilePath ?? "(null)"))}, VoiceCache kept {clonedVoices[0].VoiceCache?.Length}");
        using var live = new KeyDependencyTracker(scene);
        using var cloned = new KeyDependencyTracker(clone);
        foreach (int frame in new[] { 10, 210, 410 })
        {
            string a = WaitForFrameKey(live, frame), b = WaitForFrameKey(cloned, frame);
            Console.WriteLine($"  frame {frame}: live and clone keys {(a == b ? "equal" : "differ")}");
        }
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
        return "(no key: " + reason + ")";
    }

    // ---- The project's voice cache in the drawing model ----

    private static void VoiceCacheModel()
    {
        var settings = SettingsBase<YMMSettings>.Default;
        Console.WriteLine($"IsProjectVoiceCacheEnabled (default settings): {settings.GetType().GetProperty("IsProjectVoiceCacheEnabled", Any)?.GetValue(settings) ?? "(no such setting)"}");
        foreach (int hz in new[] { 16000, 24000, 48000 })
        {
            byte[] raw = WavBytes(hz, 3.0, 31);
            byte[] packed = Brotli(raw);
            Console.WriteLine($"Voice cache of 3 s of 16-bit mono at {hz} Hz: WAV {raw.Length} bytes, Brotli {packed.Length} ({100.0 * packed.Length / raw.Length:F0} %), "
                + $"{Convert.ToBase64String(packed).Length / 3} base64 chars per second");
        }
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        byte[] cache = Brotli(WavBytes(24000, 3.0, 32));
        foreach (var (count, withCache) in new[] { (0, true), (50, true), (200, true), (200, false), (600, true), (600, false) })
        {
            var timeline = new Timeline();
            var scenes = new Scenes(false);
            scenes.AddScene(timeline);
            var character = new Character { Name = "voices" };
            timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, count).Select(i =>
            {
                var voice = new VoiceItem(character) { Frame = i * 100, Length = 90, Serif = "せりふ " + i };
                if (withCache) voice.VoiceCache = cache;
                return (IItem)voice;
            }));
            var scene = new Scene(timeline, scenes, []);
            bool described = FrameCacheKey.TryDescribe(scene, readers, out string model, out _, out _, out string reason);
            var clock = Stopwatch.StartNew();
            for (int run = 0; run < 3; run++) FrameCacheKey.TryDescribe(scene, readers, out _, out _, out _, out _);
            clock.Stop();
            Console.WriteLine($"{count} voice items (3 s each{(withCache ? ", with the 24 kHz voice cache" : ", no voice cache")}): describe {clock.Elapsed.TotalMilliseconds / 3:F0} ms, "
                + (described ? $"model {model.Length / 1024} KiB" : "bypassed: " + reason));
        }
    }

    // ---- Files ----

    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Brotli(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) brotli.Write(data);
        return output.ToArray();
    }

    private static string WriteWav(string name, int hz, double seconds, int seed)
    {
        string path = Path.Combine(work, name);
        File.WriteAllBytes(path, WavBytes(hz, seconds, seed));
        return path;
    }

    // Speech-like 16-bit mono: syllables of harmonics with an envelope, short gaps and some pauses.
    private static byte[] WavBytes(int hz, double seconds, int seed)
    {
        var random = new Random(seed);
        int count = (int)(hz * seconds);
        var samples = new short[count];
        int i = 0;
        while (i < count)
        {
            int length = (int)(hz * (0.12 + random.NextDouble() * 0.16));
            double f0 = 110 + random.NextDouble() * 120, amplitude = 0.15 + random.NextDouble() * 0.5;
            for (int k = 0; k < length && i < count; k++, i++)
            {
                double t = (double)k / hz, envelope = Math.Sin(Math.PI * k / length);
                double v = Math.Sin(2 * Math.PI * f0 * t) * 0.6 + Math.Sin(4 * Math.PI * f0 * t) * 0.25 + Math.Sin(6 * Math.PI * f0 * t) * 0.1 + (random.NextDouble() - 0.5) * 0.1;
                samples[i] = (short)Math.Clamp(v * amplitude * envelope * 32767, -32768, 32767);
            }
            i += (int)(hz * (random.NextDouble() < 0.15 ? 0.3 + random.NextDouble() * 0.5 : 0.02 + random.NextDouble() * 0.08));
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + count * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(hz);
        writer.Write(hz * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 2);
        foreach (short sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    private static (byte, byte, byte, byte) Eyes(int x, int y, int height) =>
        (x >= 30 && x < 50 || x >= 70 && x < 90) && Math.Abs(y - 50) * 2 < height ? ((byte)20, (byte)40, (byte)220, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);

    private static (byte, byte, byte, byte) Mouth(int x, int y, int height, (byte R, byte G, byte B) color) =>
        Math.Pow((x - 60) / 22.0, 2) + Math.Pow((y - 115) / Math.Max(0.5, height / 2.0), 2) <= 1 ? (color.R, color.G, color.B, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);

    private static void WritePng(string directory, string name, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        const int width = 120, height = 160;
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                raw.Write([r, g, b, a]);
            }
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            png.Write(BigEndian((uint)data.Length));
            png.Write(typed);
            png.Write(BigEndian(Crc32(typed)));
        }
        Chunk("IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 6, 0, 0, 0]);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        File.WriteAllBytes(Path.Combine(directory, name), png.ToArray());
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
