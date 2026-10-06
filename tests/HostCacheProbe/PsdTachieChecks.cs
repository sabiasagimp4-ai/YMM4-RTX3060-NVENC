using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Voice;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class PsdTachieChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    // Each dependency is checked in a fresh process: the duplicate stays loaded until exit.
    internal static void RunDuplicate(Assembly host, bool parser)
    {
        using var test = new Case(host, false, "root");
        foreach (var item in test.Fixture.Tachies) { item.Frame = 30; item.Length -= 30; }
        test.Fixture.Timeline.Items = test.Fixture.Timeline.Items.Add(new ShapeItem { Frame = 0, Length = 30, Layer = 4 });
        test.Fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
        test.Update(0); byte[] reference = test.Pixels();
        Type plugin = test.Fixture.Characters[0].TachieType;
        Check(PsdTachieDependencies.Verified(plugin) && PsdTachieDependencies.Verified(plugin), "Audited PSD verdict was not cached before duplicate load");
        string name = parser ? "PsdParser" : "YukkuriMovieMaker.Plugin.FileSource.Psd";
        var context = new System.Runtime.Loader.AssemblyLoadContext("duplicate-" + name, isCollectible: false);
        context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(host.Location)!, name + ".dll"));
        Check(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == name) == 2, "Duplicate dependency control did not load two copies");
        Check(!PsdTachieDependencies.Verified(plugin) && !PsdTachieDependencies.Verified(plugin), "Duplicate dependency reused the old valid module verdict");
        Check(PsdTachieDependencies.SharedObjects(test.Fixture.Characters).Length == 0, "Unaudited PSD settings were subscribed");
        using var tracker = new KeyDependencyTracker(test.Fixture.Scene);
        Check(SpinWait.SpinUntil(() => { if (!tracker.TryCapture(0, out var capture, out _)) return false; capture!.Dispose(); return true; }, TimeSpan.FromSeconds(5)),
            "Duplicate PSD disabled an unrelated shape interval");
        Check(!tracker.TryCapture(30, out var active, out _), "Duplicate PSD frame remained cacheable");
        active?.Dispose();
        test.Warm(0);
        Check(test.Pixels().SequenceEqual(reference), "Unrelated cached shape pixels changed after duplicate load");
        Console.WriteLine("PSD_DUPLICATE_MODULE: " + name + "; cached verdict invalidated; PSD rejected; unrelated shape RAM hit and exact pixels passed");
    }

    internal static void Run(Assembly host)
    {
        // The nested layouts (group, composite, scene) take the "pixels" case's frames through a group control, a composite
        // group's source and another scene's source.
        foreach (string name in new[] { "pixels", "hidden-vowels", "notify", "inplace-offset", "inplace-layers", "sidecar", "overwrite", "timeout", "settings-budget", "composite-failure", "preobserved-overwrite", "serializer-defaults", "idle-inactive", "snapshot-encoding", "foreign-enumerable" }.Concat(TachieLayouts.Nested))
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { using var test = new Case(host, name == "hidden-vowels", TachieLayouts.Nested.Contains(name) ? name : "root"); RunCase(test, name, host); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "PSD tachie " + name };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), "PSD case exceeded 30 seconds: " + name);
            if (failure is not null) throw new InvalidOperationException("PSD case " + name, failure);
            Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "PSD case leaked GPU resources");
            Console.WriteLine("SPEEDUP2C_CHECK " + name + ": passed");
        }
    }
    private static void RunCase(Case test, string name, Assembly host)
    {
        if (name == "idle-inactive")
        {
            foreach (var item in test.Fixture.Tachies) { item.Frame = 30; item.Length -= 30; }
            test.Fixture.Timeline.Items = test.Fixture.Timeline.Items.Add(new ShapeItem { Frame = 0, Length = 30, Layer = 4 });
            test.Fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
            TimelineFrameCache.Enabled = false;
            var idleReference = Enumerable.Range(0, 6).Select(frame => { test.Update(frame); return test.Pixels(); }).ToArray();
            using var inactiveTracker = new KeyDependencyTracker(test.Fixture.Scene);
            Check(SpinWait.SpinUntil(() => { if (!inactiveTracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)), "Inactive scene did not become ready");
            Check(inactiveTracker.TryCapture(0, out var capture, out var reason), reason);
            using (capture)
            using (var batch = new IdleFramePreRenderer.BatchRenderer(inactiveTracker, capture!.Model))
            {
                Check(SpinWait.SpinUntil(() => { if (!batch.CloneTracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)), "Inactive clone did not become ready");
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                for (int frame = 0; frame < idleReference.Length; frame++)
                {
                    var idleResult = IdleFramePreRenderer.PrimeBatchFrame(inactiveTracker, test.Fixture.Scene, batch, frame, test.View, () => true, CancellationToken.None, out reason);
                    Check(idleResult == IdleFramePreRenderer.IdleFrameResult.Rendered, "Inactive tachie stopped idle frame: " + idleResult + "; " + reason);
                    long hits = TimelineFrameCache.Hits; test.Update(frame);
                    Check(TimelineFrameCache.Hits == hits + 1 && test.Pixels().SequenceEqual(idleReference[frame]), "Inactive idle hit/pixels differed");
                }
                Check(IdleFramePreRenderer.PrimeBatchFrame(inactiveTracker, test.Fixture.Scene, batch, 30, test.View, () => true, CancellationToken.None, out _) == IdleFramePreRenderer.IdleFrameResult.Normal,
                    "Visible tachie occupied the host envelope calculation slots");
            }
            return;
        }
        if (name == "inplace-layers")
        {
            foreach (object parameter in test.Fixture.Characters.Select(c => (object)c.TachieDefaultFaceParameter)
                .Concat(test.Fixture.Timeline.Items.OfType<VoiceItem>().Select(v => (object)v.TachieFaceParameter)))
            {
                var eyeMode = parameter.GetType().GetProperty("EyeAnimation")!;
                eyeMode.SetValue(parameter, Enum.Parse(eyeMode.PropertyType, "AlwaysClose"));
            }
        }
        if (name == "composite-failure")
        {
            var compositing = new Harmony("ymm.tests.psd-composite-failure");
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(host.Location)!, "YukkuriMovieMaker.Plugin.FileSource.Psd.dll"));
            var method = assembly.GetType("YukkuriMovieMaker.Plugin.FileSource.Psd.PsdFileSourcePlugin", true)!
                .GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "CreateBitmap" && m.GetParameters().Length == 4);
            try
            {
                compositing.Patch(method, prefix: new(typeof(PsdTachieChecks), nameof(FailComposite)));
                TimelineFrameCache.Enabled = true;
                for (int i = 0; i < 4; i++) { test.Update(0); TimelineFrameCache.CompletePendingStore(test.Source); }
                Check(!PsdTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, 0) && test.Store.RamBytes == 0,
                    "Absorbed PSD compositing failure was cached");
            }
            finally { compositing.UnpatchAll(compositing.Id); }
            TimelineFrameCache.Enabled = false;
            foreach (var character in test.Fixture.Characters) PsdTachieFixture.Set(character.TachieCharacterParameter, "FilePath", Path.Combine(test.Fixture.Root, "missing.psd"));
            test.Update(0);
            for (int i = 0; i < 2; i++) PsdTachieFixture.Set(test.Fixture.Characters[i].TachieCharacterParameter, "FilePath", test.Fixture.Images[i]);
            test.Update(0); byte[] recoveredPixels = test.Pixels(); test.Warm(0);
            Check(test.Pixels().SequenceEqual(recoveredPixels), "PSD did not recover after a real source reload");
            return;
        }
        TimelineFrameCache.Enabled = false; test.Update(0);
        if (name == "timeout") { CheckTimeout(test, host); return; }
        if (name == "preobserved-overwrite")
        {
            Check(!HostContent.Changed(test.Fixture.Images[0]), "Own fresh PSD was already rejected");
            byte[] retained = test.Pixels();
            File.WriteAllBytes(test.Fixture.Images[0], PsdTachieFixture.OwnPsd(false));
            TimelineFrameCache.Enabled = false;
            using (var fresh = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Instance, null,
                [test.Context, test.Fixture.Scene, null], null)!)
            {
                fresh.Update(test.Fixture.Timeline.VideoInfo.GetTimeFrom(0), TimelineSourceUsage.Playing);
                Check(!TimelineFrameCache.CapturePreview(test.Context.DeviceContext, fresh.Output, test.View)!.SequenceEqual(retained),
                    "Pre-observation overwrite did not change a fresh host's pixels");
            }
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                test.Update(0); TimelineFrameCache.CompletePendingStore(test.Source);
                return HostContent.Changed(test.Fixture.Images[0]);
            }, TimeSpan.FromSeconds(5)), "Retained PSD bytes did not reject the newer first fingerprint");
            Check(test.Store.RamBytes == 0, "Old retained PSD pixels were stored under newer content");
            Console.WriteLine("SPEEDUP2C_PREOBSERVED fresh_pixels_differ=true; loaded_bytes_mismatch_rejected=true");
            return;
        }

        object settings = PsdTachieDependencies.Settings(test.Fixture.Characters[0]);
        object eyes = settings.GetType().GetProperty("EyeAnimations")!.GetValue(settings)!;
        object eye = ((IEnumerable)eyes).Cast<object>().Single();
        if (name == "foreign-enumerable")
        {
            // A foreign subclass can implement IEnumerable while retaining the native drawing fields.
            // Treating it as an empty list would omit those fields from the key; it must be rejected.
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new("ymm.tests.foreign-psd-eye"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("foreign").DefineType("ForeignEnumerableEye", TypeAttributes.Public, eye.GetType(), [typeof(IEnumerable)]);
            builder.DefineDefaultConstructor(MethodAttributes.Public);
            var method = builder.DefineMethod("GetEnumerator", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                typeof(IEnumerator), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(Array).GetMethod(nameof(Array.Empty))!.MakeGenericMethod(typeof(object)));
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(IEnumerable).GetMethod(nameof(IEnumerable.GetEnumerator))!);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            builder.DefineMethodOverride(method, typeof(IEnumerable).GetMethod(nameof(IEnumerable.GetEnumerator))!);
            object foreign = Activator.CreateInstance(builder.CreateType()!)!;
            foreign.GetType().GetProperty("Offset")!.SetValue(foreign, 7.0);
            object changed = eyes.GetType().GetMethod("SetItem")!.Invoke(eyes, [0, foreign])!;
            settings.GetType().GetProperty("EyeAnimations")!.SetValue(settings, changed);
            bool rejected = false;
            try { PsdTachieDependencies.Snapshot(settings); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Foreign enumerable hid native PSD drawing fields from the snapshot");
            return;
        }
        if (name == "snapshot-encoding")
        {
            string Legacy()
            {
                using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                using (var writer = new Newtonsoft.Json.JsonTextWriter(output) { Formatting = Newtonsoft.Json.Formatting.None })
                    Newtonsoft.Json.JsonSerializer.Create().Serialize(writer, settings);
                return output.ToString();
            }
            for (int edit = 0; edit < 100; edit++)
            {
                eye.GetType().GetProperty("Offset")!.SetValue(eye, edit % 3 == 0 ? -0.0 : edit / 3.0);
                eye.GetType().GetProperty("Interval")!.SetValue(eye, edit / 7.0);
                eye.GetType().GetProperty("Layers")!.SetValue(eye, ImmutableList.Create("i1", "日本語／😀 " + edit));
                string snapshot = PsdTachieDependencies.Snapshot(settings);
                Check(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.Parse(snapshot), Newtonsoft.Json.Linq.JToken.Parse(Legacy())),
                    "Captured PSD encoding omitted or rewrote an actual property");
                Check(ReferenceEquals(snapshot, PsdTachieDependencies.Snapshot(settings)), "Unchanged PSD settings were serialized again");
            }
            eye.GetType().GetProperty("Offset")!.SetValue(eye, 1.0); string a = PsdTachieDependencies.Snapshot(settings);
            eye.GetType().GetProperty("Offset")!.SetValue(eye, 2.0); string b = PsdTachieDependencies.Snapshot(settings);
            eye.GetType().GetProperty("Offset")!.SetValue(eye, 1.0); string restored = PsdTachieDependencies.Snapshot(settings);
            Check(a != b && restored == a, "Non-notifying A/B/A edits left a stale serialized witness");
            return;
        }
        if (name == "serializer-defaults")
        {
            string before = PsdTachieDependencies.Snapshot(settings);
            var original = Newtonsoft.Json.JsonConvert.DefaultSettings;
            try
            {
                Newtonsoft.Json.JsonConvert.DefaultSettings = () => new() { Converters = { new ConstantSettingsConverter() } };
                Check(Newtonsoft.Json.JsonConvert.SerializeObject(settings) == "\"constant\"", "Global serializer control was not applied");
                Check(PsdTachieDependencies.Snapshot(settings) == before, "Global JSON defaults changed a PSD dependency snapshot");
                eye.GetType().GetProperty("Offset")!.SetValue(eye, 2.0);
                Check(PsdTachieDependencies.Snapshot(settings) != before, "Global JSON defaults hid an actual PSD offset change");
            }
            finally { Newtonsoft.Json.JsonConvert.DefaultSettings = original; }
            return;
        }
        if (name is "notify" or "inplace-offset" or "inplace-layers" or "settings-budget")
        {
            const int frame = 32; // The active voice supplies AlwaysClose to the native PSD source.
            test.Warm(frame);
            using var tracker = new KeyDependencyTracker(test.Fixture.Scene);
            KeyCapture? capture = null;
            string reason = string.Empty;
            Check(SpinWait.SpinUntil(() => tracker.TryCapture(frame, out capture, out reason),
                TimeSpan.FromSeconds(5)), "PSD settings capture did not finish keying: " + reason);
            using (capture)
            {
                string oldModel = capture!.Model; long revision = tracker.Revision;
                if (name == "notify")
                {
                    object replacement = Activator.CreateInstance(eye.GetType(), [ImmutableList.Create("i1", "i2", "i3"), 2.0, 4.0])!;
                    var changed = eyes.GetType().GetMethod("SetItem")!.Invoke(eyes, [0, replacement]);
                    settings.GetType().GetProperty("EyeAnimations")!.SetValue(settings, changed);
                    Check(tracker.Revision > revision && !capture.Validate(), "Shared setting notification did not invalidate a capture");
                }
                else
                {
                    if (name == "inplace-offset") eye.GetType().GetProperty("Offset")!.SetValue(eye, 2.0);
                    else eye.GetType().GetProperty("Layers")!.SetValue(eye, name == "settings-budget"
                        ? Enumerable.Repeat("i1", 1025).ToImmutableList() : ImmutableList.Create("i1", "i2", "i7"));
                    Check(tracker.Revision == revision, "The non-notifying control unexpectedly notified");
                    Check(!PsdTachieDependencies.Current(oldModel) && !capture.Validate(), "Non-notifying shared settings left a stale capture valid");
                }
            }
            if (name == "settings-budget")
            {
                Check(!FrameCacheKey.TryDescribe(test.Fixture.Scene, out _, out _, out _), "Oversized settings were admitted");
                return;
            }
            if (name == "notify")
            {
                TimelineFrameCache.Enabled = false; test.Update(frame);
                Check(PsdTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, frame), "Notified native settings did not normalize");
                byte[] notifiedPixels = test.Pixels(); test.Warm(frame);
                Check(test.Pixels().SequenceEqual(notifiedPixels), "Notified settings cache differs from ordinary preview");
                return;
            }
            Check(!PsdTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, frame), "Stale normalized settings were admitted");
            long hits = TimelineFrameCache.Hits;
            test.Update(frame); TimelineFrameCache.CompletePendingStore(test.Source);
            Check(TimelineFrameCache.Hits == hits, "Stale normalized source served a cached frame");
            byte[] retained = test.Pixels();
            TimelineFrameCache.Enabled = false;
            using var fresh = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Instance, null,
                [test.Context, test.Fixture.Scene, null], null)!;
            fresh.Update(test.Fixture.Timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
            byte[] updated = TimelineFrameCache.CapturePreview(test.Context.DeviceContext, fresh.Output, test.View)!;
            Check(!retained.SequenceEqual(updated), "The stale-normalization pixel counterexample was not exercised");
            Console.WriteLine("SPEEDUP2C_STALE " + name + ": live_vs_fresh_pixels_differ=true; cache_rejected=true");
            return;
        }
        if (name is "sidecar" or "overwrite")
        {
            test.Warm(32); long hits = TimelineFrameCache.Hits;
            byte[] before = test.Pixels(); string model;
            Check(FrameCacheKey.TryDescribe(test.Fixture.Scene, out model, out _, out var reason), reason);
            if (name == "sidecar")
            {
                File.WriteAllText(Path.Combine(test.Fixture.Root, "face-0-ymm.json"), "{\"EyeAnimations\":[]}");
                Check(ReferenceEquals(PsdTachieDependencies.Settings(test.Fixture.Characters[0]), settings)
                    && PsdTachieDependencies.Current(model), "Host unexpectedly reread its cached shared sidecar");
                test.Update(32);
                Check(TimelineFrameCache.Hits > hits && test.Pixels().SequenceEqual(before), "Unloaded sidecar changed the preview cache");
                TimelineFrameCache.Enabled = false;
                using var fresh = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Instance, null,
                    [test.Context, test.Fixture.Scene, null], null)!;
                fresh.Update(test.Fixture.Timeline.VideoInfo.GetTimeFrom(32), TimelineSourceUsage.Playing);
                Check(TimelineFrameCache.CapturePreview(test.Context.DeviceContext, fresh.Output, test.View)!.SequenceEqual(before), "New source did not use shared cached settings");
            }
            else
            {
                File.WriteAllBytes(test.Fixture.Images[0], PsdTachieFixture.OwnPsd(false));
                test.Update(32); Check(TimelineFrameCache.Hits == hits, "Overwritten PSD was served from cache");
            }
            return;
        }
        int[] frames = [0, 29, 30, 31, 35, 40, 45, 50, 59, 60, 74, 89, 90, 104, 119, 120, 149, 150];
        // The host draws a closed mouth when the volume is not published in time (2.5 seconds on a thread with a
        // Dispatcher; the first calculation is cold), and the cache never stores such a frame: redraw the reference.
        var voices = test.Fixture.Timeline.Items.OfType<VoiceItem>().ToArray();
        var budget = Stopwatch.StartNew();
        var unpublished = new HashSet<int>();
        var reference = frames.ToDictionary(frame => frame, frame =>
        {
            var time = test.Fixture.Timeline.VideoInfo.GetTimeFrom(frame);
            bool speaking = voices.Any(voice => frame >= voice.Frame && frame < voice.Frame + voice.Length);
            test.Update(frame);
            while (speaking && !FrameRenderReadiness.WasLastUpdateReady(test.Source, time) && budget.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(10); test.Update(frame);
            }
            if (speaking && !FrameRenderReadiness.WasLastUpdateReady(test.Source, time)) unpublished.Add(frame);
            return test.Pixels();
        });
        Check(reference.Values.Any(value => !value.SequenceEqual(reference[30])), "PSD mouth never changed pixels");
        if (TachieLayouts.Nested.Contains(name)) Check(reference[0].Any(value => value != 0), "The " + name + " layout drew nothing");
        if (name == "hidden-vowels") Check(!reference[29].SequenceEqual(reference[30]) && !reference[35].SequenceEqual(reference[40]),
            "PSD no-speech visibility or vowel mouth never changed");
        foreach (int frame in frames)
        {
            // Hidden native sources have no normalized state yet: render those frames normally.
            bool ready = PsdTachieDependencies.SafeSource(test.Source, test.Scene, frame);
            if (name == "hidden-vowels" && !ready) { TimelineFrameCache.Enabled = true; test.Update(frame); }
            else test.Warm(frame);
            Check(test.Pixels().SequenceEqual(reference[frame]), "PSD cached pixels differ at " + frame
                + (unpublished.Contains(frame) ? " (the ordinary reference never got a published volume)" : string.Empty));
        }
        using var idleTracker = new KeyDependencyTracker(test.Scene); int rendered = 0;
        var result = IdleFramePreRenderer.PrimeLiveFrame(idleTracker, test.Scene, test.Source, _ => rendered++,
            30, test.View, () => true, CancellationToken.None);
        Check(result == IdleFramePreRenderer.IdleFrameResult.Normal && rendered == 0, "Idle PSD started a competing envelope");
        if (name == "pixels") CheckStableBlink(test, idleTracker, reference);
    }

    // The blinking is seeded alike in every run (BlinkSeedAlignment): frames are not keyed for this run's objects, so
    // a clone of the scene renders a frame without a voice (no lip-sync calculation) while paused, under the live key
    // and with the live pixels.
    private static void CheckStableBlink(Case test, KeyDependencyTracker tracker, IReadOnlyDictionary<int, byte[]> reference)
    {
        Check(PsdTachieDependencies.StableBlink(test.Fixture.Characters[0]), "The audited PSD tachie's blink seed was not aligned");
        Check(SpinWait.SpinUntil(() => { if (!tracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)),
            "The PSD scene did not become ready");
        Check(!tracker.IsSessionKeyed(0) && !tracker.IsSessionKeyed(30), "PSD tachie frames were still keyed for this run");
        Check(tracker.TryCapture(0, out var capture, out var reason), reason);
        using (capture)
        using (var batch = new IdleFramePreRenderer.BatchRenderer(tracker, capture!.Model))
        {
            TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
            IdleFramePreRenderer.IdleFrameResult idle = default;
            Check(SpinWait.SpinUntil(() => (idle = IdleFramePreRenderer.PrimeBatchFrame(tracker, test.Scene, batch, 0, test.View, () => true,
                CancellationToken.None, out reason)) == IdleFramePreRenderer.IdleFrameResult.Rendered, TimeSpan.FromSeconds(10)),
                "A PSD frame without a voice was not rendered while paused: " + idle + "; " + reason);
            Check(IdleFramePreRenderer.PrimeBatchFrame(tracker, test.Scene, batch, 30, test.View, () => true, CancellationToken.None, out _)
                == IdleFramePreRenderer.IdleFrameResult.Normal, "A PSD frame with a voice was rendered while paused");
            long hits = TimelineFrameCache.Hits; test.Update(0);
            Check(TimelineFrameCache.Hits == hits + 1 && test.Pixels().SequenceEqual(reference[0]), "The paused PSD clone's frame was not the live picture");
        }
    }
    private static void CheckTimeout(Case test, Assembly host)
    {
        var type = host.GetType("YukkuriMovieMaker.Player.Video.Items.TachieSource", true)!;
        var gate = (SemaphoreSlim)type.GetField("envelopeCalculationGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(gate.Wait(TimeSpan.FromSeconds(2)), "First calculation slot remained busy");
        bool second = false;
        var shortWait = new Harmony("ymm.tests.animation-short-wait");
        try
        {
            Check(second = gate.Wait(TimeSpan.FromSeconds(2)), "Second calculation slot remained busy");
            shortWait.Patch(type.GetMethod("ReadVolumeAfterRequiredWait", BindingFlags.Static | BindingFlags.NonPublic)!,
                prefix: new(typeof(PsdTachieChecks), nameof(ShortWait)));
            TimelineFrameCache.Enabled = true;
            using var tracker = new KeyDependencyTracker(test.Fixture.Scene);
            Check(SpinWait.SpinUntil(() =>
            {
                if (!tracker.TryCapture(30, out var capture, out _)) return false;
                capture!.Dispose(); return true;
            }, TimeSpan.FromSeconds(5)), "Timeout frame never finished keying");
            test.Update(30); TimelineFrameCache.CompletePendingStore(test.Source);
            Check(!FrameRenderReadiness.WasLastUpdateReady(test.Source, test.Fixture.Timeline.VideoInfo.GetTimeFrom(30))
                && test.Store.RamBytes == 0, "The real host's timed-out zero was saved");
        }
        finally { shortWait.UnpatchAll(shortWait.Id); if (second) gate.Release(); gate.Release(); }
        test.Warm(30);
        var core = CoreSource(test.Source, test.Fixture.Tachies[0]);
        var sessionField = type.GetField("volumeEnvelopeSession", Instance)!;
        var taskField = type.GetField("envelopeTask", Instance)!;
        var originalSession = sessionField.GetValue(core); var originalTask = taskField.GetValue(core);
        try
        {
            var sessionType = host.GetType("YukkuriMovieMaker.Player.Audio.LipSyncEnvelopeSession", true)!;
            foreach (string terminal in new[] { "Fail", "Cancel" })
            {
                var session = Activator.CreateInstance(sessionType, Instance, null, [30], null)!;
                sessionType.GetMethod("Publish", Instance)!.Invoke(session, [0, 0.0]);
                sessionType.GetMethod(terminal, Instance)!.Invoke(session, null);
                var completedTask = Task.CompletedTask;
                sessionField.SetValue(core, session); taskField.SetValue(core, completedTask);
                Check((int)sessionType.GetProperty("PublishedFrameCount")!.GetValue(session)! == 1 && completedTask.IsCompletedSuccessfully, "Partial-session control was not published successfully");
                object[] args = [TimelineSourceUsage.Playing, 0, 30, session, completedTask, TimeSpan.Zero, null!, null!];
                Check(!NativeTachieReadiness.ValueReady(core, 4, args, 0), "Partially published " + terminal + " was accepted as completed zero");
            }
        }
        finally { sessionField.SetValue(core, originalSession); taskField.SetValue(core, originalTask); }
    }
    private sealed class ConstantSettingsConverter : Newtonsoft.Json.JsonConverter
    {
        public override bool CanConvert(Type type) => type.FullName == PsdTachieDependencies.AssemblyName + ".PsdFileSettings";
        public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer) => writer.WriteValue("constant");
        public override object? ReadJson(Newtonsoft.Json.JsonReader reader, Type type, object? value, Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();
    }
    private static bool FailComposite(ref Vortice.Direct2D1.ID2D1Bitmap? __result) { __result = null; return false; }
    private static void ShortWait(object[] __args) => __args[5] = TimeSpan.Zero;
    private static object CoreSource(object source, TachieItem item)
    {
        var resources = (IDictionary)source.GetType().GetField("timelineResources", Instance)!.GetValue(source)!;
        return resources[item]!.GetType().GetProperty("Source", Instance)!.GetValue(resources[item])!;
    }
    private static bool SkipLoader() => false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Case : IDisposable
    {
        internal readonly PsdTachieFixture Fixture;
        internal readonly Scene Scene;
        internal readonly GraphicsDevices Devices = new();
        internal readonly IGraphicsDevicesAndContext Context;
        internal readonly ITimelineSource Source;
        internal readonly FrameCacheStore Store;
        internal readonly TimelineFrameCache.PreviewViewport View;
        private readonly Harmony harmony = new("ymm.tests.psd-checks");
        private readonly bool oldPreview = TimelineFrameCache.PreviewEnabled, oldExport = TimelineFrameCache.ExportEnabled, oldGpu = TimelineFrameCache.GpuRetentionEnabled;
        private readonly Func<object, TimelineFrameCache.PreviewViewport?>? oldViewport = TimelineFrameCache.TestViewport;
        private readonly FrameCacheStore? oldStore = TimelineFrameCache.StoreIfCreated;
        internal Case(Assembly host, bool vowels, string layout)
        {
            harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new(typeof(PsdTachieChecks), nameof(SkipLoader)));
            ProbeLoader.Stub(ProbeLoader.Assemblies(host).Append(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(host.Location)!, PsdTachieDependencies.AssemblyName + ".dll"))));
            Fixture = new(vowels);
            var voices = Fixture.Timeline.Items.OfType<VoiceItem>().Take(2).ToArray();
            voices[0].Frame = 30; voices[0].Length = 30; voices[1].Frame = 90; voices[1].Length = 30;
            if (vowels)
            {
                foreach (var character in Fixture.Characters)
                {
                    object settings = PsdTachieDependencies.Settings(character);
                    var vowelType = character.TachieType.Assembly.GetType(PsdTachieDependencies.AssemblyName + ".PsdVowelMouthAnimation", true)!;
                    object vowel = Activator.CreateInstance(vowelType)!;
                    foreach (var (name, layer) in new[] { ("Key", "i4"), ("A", "i4"), ("I", "i5"), ("U", "i6"), ("E", "i5"), ("O", "i6"), ("Silence", "i4") })
                        vowelType.GetProperty(name)!.SetValue(vowel, layer);
                    var list = settings.GetType().GetProperty("MouthVowelAnimations")!.GetValue(settings)!;
                    settings.GetType().GetProperty("MouthVowelAnimations")!.SetValue(settings, list.GetType().GetMethod("Add")!.Invoke(list, [vowel]));
                }
                var shapes = new[] { MouthShape.A, MouthShape.I, MouthShape.U, MouthShape.E, MouthShape.O };
                foreach (var voice in voices)
                    voice.LipSyncFrames = shapes.Select((shape, i) => new LipSyncFrame(TimeSpan.FromSeconds(i / 3.0), shape)).ToArray();
            }
            Fixture.Timeline.Items = Fixture.Timeline.Items.Where(item => item is TachieItem).ToImmutableList().AddRange(voices);
            Fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
            Scene = TachieLayouts.Apply(layout, Fixture.Timeline, Fixture.Scene);
            Context = Devices.CreateContext();
            Source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Instance, null, [Context, Scene, null], null)!;
            var dc = Context.DeviceContext;
            View = new(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, Scene.ID, Scene.Timeline.ID, Stopwatch.GetTimestamp(), false);
            Store = new(Path.Combine(Fixture.Root, "store"), 64L << 20, 0);
            TimelineFrameCache.Enabled = false; TimelineFrameCache.GpuRetentionEnabled = false;
            Check(TimelineFrameCache.TryInstall(host, harmony, out var reason), reason);
            TimelineFrameCache.UseStore(Store); TimelineFrameCache.TestViewport = value => ReferenceEquals(value, Source) ? View : null;
        }
        internal void Update(int frame) => Source.Update(Fixture.Timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
        internal byte[] Pixels() => TimelineFrameCache.CapturePreview(Context.DeviceContext, Source.Output, View)!;
        internal void Warm(int frame)
        {
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(frame); TimelineFrameCache.CompletePendingStore(Source);
                long hits = TimelineFrameCache.Hits; Update(frame);
                return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(5)), "PSD frame did not become reusable: " + frame + "; " + TimelineFrameCache.Status);
        }
        public void Dispose()
        {
            TimelineFrameCache.Enabled = false; TimelineFrameCache.TestViewport = oldViewport;
            Source.Dispose(); Context.CacheProvider.Clear(); Context.Dispose(); Devices.Dispose();
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
            if (oldStore is not null) TimelineFrameCache.UseStore(oldStore);
            Store.Dispose(); Fixture.Dispose();
            TimelineFrameCache.GpuRetentionEnabled = oldGpu; TimelineFrameCache.SetEnabled(oldPreview, oldExport);
        }
    }
}
