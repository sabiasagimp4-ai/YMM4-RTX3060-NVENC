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

internal static class AnimationTachieChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly host)
    {
        if (!HostFeatures.For(host).AnimationTachie)
        {
            Console.WriteLine("Animation tachie checks skipped: the animation tachie is not cached on this build");
            return;
        }
        // The nested layouts (group, composite, scene) take the "pixels" case's frames through a group control, a composite
        // group's source and another scene's source.
        foreach (string name in new[] { "pixels", "hidden-vowels", "timeout", "retained-ini", "changed-list", "overwrite", "rollback", "metadata-budget", "output-lifetime", "idle-inactive", "preserved-directory-time", "differential", "same-layer-faces", "hidden-layer" }.Concat(TachieLayouts.Nested))
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { using var fixture = new Case(host, name); RunCase(fixture, name, host); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Animation tachie " + name };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), "Animation case exceeded 30 seconds: " + name);
            if (failure is not null) throw new InvalidOperationException("Animation case " + name, failure);
            Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "Animation case leaked GPU resources");
            Console.WriteLine("SPEEDUP2B_CHECK " + name + ": passed");
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
            using var idleTracker = new KeyDependencyTracker(test.Fixture.Scene);
            Check(SpinWait.SpinUntil(() => { if (!idleTracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)), "Inactive scene did not become ready");
            Check(idleTracker.TryCapture(0, out var capture, out var reason), reason);
            using (capture)
            using (var batch = new IdleFramePreRenderer.BatchRenderer(idleTracker, capture!.Model))
            {
                Check(SpinWait.SpinUntil(() => { if (!batch.CloneTracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)), "Inactive clone did not become ready");
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                for (int frame = 0; frame < idleReference.Length; frame++)
                {
                    var idleResult = IdleFramePreRenderer.PrimeBatchFrame(idleTracker, test.Fixture.Scene, batch, frame, test.View, () => true, CancellationToken.None, out reason);
                    Check(idleResult == IdleFramePreRenderer.IdleFrameResult.Rendered, "Inactive tachie stopped idle frame: " + idleResult + "; " + reason);
                    long hits = TimelineFrameCache.Hits; test.Update(frame);
                    Check(TimelineFrameCache.Hits == hits + 1 && test.Pixels().SequenceEqual(idleReference[frame]), "Inactive idle hit/pixels differed");
                }
                Check(IdleFramePreRenderer.PrimeBatchFrame(idleTracker, test.Fixture.Scene, batch, 30, test.View, () => true, CancellationToken.None, out _) == IdleFramePreRenderer.IdleFrameResult.Normal,
                    "Visible tachie occupied the host envelope calculation slots");
            }
            return;
        }
        if (name == "rollback") { test.CheckInstallRollback(host); return; }
        if (name == "retained-ini")
        {
            // An attached INI is a dependency, and frames drawn with what it says are cached; a source that kept
            // the settings of an INI deleted since is not.
            string ini = Path.ChangeExtension(test.Fixture.Images[0], ".ini");
            File.WriteAllText(ini, "blend=4\nopacity=25\nplaceon=face\n");
            TimelineFrameCache.Enabled = false; test.Update(10);
            var plain = test.Pixels();
            Check(AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out var withIni)
                && withIni.Contains(Path.GetFullPath(ini), StringComparer.OrdinalIgnoreCase), "An existing INI was not a dependency");
            test.Warm(10);
            Check(test.Pixels().SequenceEqual(plain), "A frame with an attached INI was cached with another picture");
            TimelineFrameCache.Enabled = false;
            File.Delete(ini);
            TimelineFrameCache.Clear();
            Check(AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out _), "The PNG-only description did not recover");
            Check(!AnimationTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, 30), "Deleted INI left an unsafe source admitted");
            TimelineFrameCache.Enabled = true;
            long hits = TimelineFrameCache.Hits;
            for (int i = 0; i < 4; i++) { test.Update(30); TimelineFrameCache.CompletePendingStore(test.Source); }
            Check(TimelineFrameCache.Hits == hits && test.Store.RamBytes == 0, "A retained INI source was cached");
            return;
        }
        TimelineFrameCache.Enabled = false; test.Update(0);
        if (name == "timeout") { CheckTimeout(test, host); return; }
        if (name == "output-lifetime")
        {
            int[] samples = Enumerable.Range(0, 120).ToArray();
            var normal = samples.Select(frame => { test.Update(frame); return test.Pixels(); }).ToArray();
            test.Warm(0);
            var collector = (DisposeCollector)test.Source.GetType().GetField("disposer", Instance)!.GetValue(test.Source)!;
            var entries = (IList)collector.GetType().GetField("disposables", Instance)!.GetValue(collector)!;
            for (int i = 0; i < samples.Length; i++)
            {
                // Include a normal-render bypass while a shown copy exists, and then resume caching.
                TimelineFrameCache.Enabled = i % 11 != 0;
                test.Update(samples[i]); TimelineFrameCache.CompletePendingStore(test.Source);
                Check(test.Pixels().SequenceEqual(normal[i]), "Output lifetime changed pixels at " + i);
                Check(entries.Cast<object>().OfType<Vortice.Direct2D1.ID2D1CommandList>().Count() <= 2,
                    "Original host command lists accumulated across preview updates");
            }
            return;
        }
        if (name == "metadata-budget")
        {
            test.Warm(0);
            var characters = typeof(AnimationTachieDependencies).GetField("listingCharacters", BindingFlags.Static | BindingFlags.NonPublic)!;
            long original = (long)characters.GetValue(null)!;
            try
            {
                characters.SetValue(null, 8L << 20); // Simulate exhausted metadata, without allocating sixteen MiB of paths.
                Check(AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out _),
                    "The metadata cap discarded an existing first listing");
                string extra = Path.Combine(test.Fixture.Root, "new-body.png"); File.Copy(test.Fixture.Images[0], extra);
                AnimationTachieFixture.Set(test.Fixture.Tachies[0].TachieItemParameter, "Body", extra);
                Check(!AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out _),
                    "An unrecorded listing was admitted after the metadata cap");
            }
            finally { characters.SetValue(null, original); }
            Check(AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out _),
                "Available metadata did not admit the new listing");
            return;
        }
        if (name is "changed-list" or "overwrite" or "preserved-directory-time")
        {
            test.Warm(30);
            long hits = TimelineFrameCache.Hits;
            if (name is "changed-list" or "preserved-directory-time")
            {
                string added = Path.Combine(test.Fixture.Root, "eye.2.png");
                DateTime directoryTime = Directory.GetLastWriteTimeUtc(test.Fixture.Root);
                File.Copy(test.Fixture.Images[0], added);
                if (name == "preserved-directory-time")
                {
                    Directory.SetLastWriteTimeUtc(test.Fixture.Root, directoryTime);
                    Check(Directory.GetLastWriteTimeUtc(test.Fixture.Root) == directoryTime, "Directory time counterexample was not prepared");
                }
                Check(!AnimationTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, 30), "New numbered eye was not rejected synchronously");
                File.Delete(added);
                Check(!AnimationTachieDependencies.SafeSource(test.Source, test.Fixture.Scene, 30), "Changed list recovered before restart");
                string vowel = Path.Combine(test.Fixture.Root, "mouth.a.png"); File.Copy(test.Fixture.Images[0], vowel);
                Check(!AnimationTachieDependencies.Listing(Path.Combine(test.Fixture.Root, "mouth.png"), out _, out _), "New vowel was not rejected");
            }
            else
            {
                File.WriteAllBytes(test.Fixture.Images[0], FramePixelChecks.Png(80, 100, (_, _) => (10, 200, 90, 255)));
            }
            test.Update(30);
            Check(TimelineFrameCache.Hits == hits, "Changed part was served from cache");
            return;
        }
        if (name == "hidden-vowels")
            Check(AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out var files)
                && files.Contains(Path.Combine(test.Fixture.Root, "mouth.A.png"), StringComparer.OrdinalIgnoreCase),
                "Upper-case vowel accepted by the host was omitted from dependencies");
        // A tachie on a hidden layer is not drawn; the frames of the others are still cached.
        if (name == "hidden-layer") test.Fixture.Timeline.LayerSettings.IsVisibles[test.Fixture.Tachies[1].Layer] = false;
        int[] frames = [0, 29, 30, 31, 35, 40, 45, 50, 59, 60, 74, 89, 90, 99, 100, 104, 119, 120, 149, 150];
        var reference = frames.ToDictionary(frame => frame, frame => { test.Update(frame); return test.Pixels(); });
        if (test.Faces.Length != 0)
        {
            // Both faces are shown at 60..99: differential composite takes the lower face's etc part, the plain tachie
            // only the upper face's parts; of one layer, the first in the item list is the upper one.
            Check(!reference[45].SequenceEqual(reference[0]), "The upper face did not change the picture");
            if (name == "differential")
            {
                AnimationTachieFixture.Set(test.Fixture.Tachies[0].TachieItemParameter, "IsDifferentialComposite", false);
                TimelineFrameCache.Enabled = false; test.Update(74);
                Check(!test.Pixels().SequenceEqual(reference[74]), "Differential composite did not change the picture");
                AnimationTachieFixture.Set(test.Fixture.Tachies[0].TachieItemParameter, "IsDifferentialComposite", true);
            }
        }
        if (TachieLayouts.Nested.Contains(name)) Check(reference[0].Any(value => value != 0), "The " + name + " layout drew nothing");
        if (name == "hidden-vowels")
        {
            Check(!reference[29].SequenceEqual(reference[30]), "No-speech visibility did not change");
            Check(!reference[35].SequenceEqual(reference[40]), "Vowel mouth did not change pixels");
        }
        foreach (int frame in frames)
        {
            test.Warm(frame);
            Check(test.Pixels().SequenceEqual(reference[frame]), "Animation cached pixels differ at " + frame);
        }
        if (name == "same-layer-faces")
        {
            // Swapping the faces of one layer in the item list swaps which one is shown: the frames showing both change
            // their key and picture, the others neither.
            var (upper, lower) = (test.Faces[0], test.Faces[1]);
            Check(FrameCacheKey.TryDescribe(test.Scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var before, out var reason), reason);
            var items = test.Fixture.Timeline.Items;
            test.Fixture.Timeline.Items = items.Remove(upper).Remove(lower).Add(lower).Add(upper);
            Check(FrameCacheKey.TryDescribe(test.Scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var after, out reason), reason);
            Check(before!.For(74).Content != after!.For(74).Content && before.For(45).Content == after.For(45).Content,
                "Swapping faces of one layer did not change exactly the frames showing both");
            TimelineFrameCache.Enabled = false; test.Update(74); var swapped = test.Pixels();
            Check(!swapped.SequenceEqual(reference[74]), "Swapping faces of one layer did not change the picture");
            test.Warm(74);
            Check(test.Pixels().SequenceEqual(swapped), "A swapped face frame was not cached with its own picture");
        }
        using var tracker = new KeyDependencyTracker(test.Scene);
        int rendered = 0;
        var result = IdleFramePreRenderer.PrimeLiveFrame(tracker, test.Scene, test.Source, _ => rendered++,
            30, test.View, () => true, CancellationToken.None);
        Check(result == IdleFramePreRenderer.IdleFrameResult.Normal && rendered == 0, "Idle tachie started a competing calculation");
        Check(AnimationTachieDependencies.SessionResource.Contains("animation-blink-session://", StringComparison.Ordinal), "Blink session salt missing");
        if (name == "pixels") CheckStableBlink(test, tracker, reference);
    }

    // The blinking is seeded alike in every run (BlinkSeedAlignment): frames are not keyed for this run's objects, so
    // a clone of the scene renders a frame without a voice (no lip-sync calculation) while paused, under the live key
    // and with the live pixels.
    private static void CheckStableBlink(Case test, KeyDependencyTracker tracker, IReadOnlyDictionary<int, byte[]> reference)
    {
        Check(AnimationTachieDependencies.StableBlink(test.Fixture.Characters[0]), "The audited animation tachie's blink seed was not aligned");
        Check(SpinWait.SpinUntil(() => { if (!tracker.TryCapture(0, out var c, out _)) return false; c!.Dispose(); return true; }, TimeSpan.FromSeconds(5)),
            "The tachie scene did not become ready");
        Check(!tracker.IsSessionKeyed(0) && !tracker.IsSessionKeyed(30), "Animation tachie frames were still keyed for this run");
        Check(tracker.TryCapture(0, out var capture, out var reason), reason);
        using (capture)
        using (var batch = new IdleFramePreRenderer.BatchRenderer(tracker, capture!.Model))
        {
            TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
            IdleFramePreRenderer.IdleFrameResult idle = default;
            Check(SpinWait.SpinUntil(() => (idle = IdleFramePreRenderer.PrimeBatchFrame(tracker, test.Scene, batch, 0, test.View, () => true,
                CancellationToken.None, out reason)) == IdleFramePreRenderer.IdleFrameResult.Rendered, TimeSpan.FromSeconds(5)),
                "A frame without a voice was not rendered while paused: " + idle + "; " + reason);
            Check(IdleFramePreRenderer.PrimeBatchFrame(tracker, test.Scene, batch, 30, test.View, () => true, CancellationToken.None, out _)
                == IdleFramePreRenderer.IdleFrameResult.Normal, "A frame with a voice was rendered while paused");
            long hits = TimelineFrameCache.Hits; test.Update(0);
            Check(TimelineFrameCache.Hits == hits + 1 && test.Pixels().SequenceEqual(reference[0]), "The paused clone's frame was not the live picture");
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
                prefix: new(typeof(AnimationTachieChecks), nameof(ShortWait)));
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
    private static void NoOp() { }
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
        internal readonly AnimationTachieFixture Fixture;
        internal readonly Scene Scene;
        // "differential" and "same-layer-faces": two faces of the first character, both shown at 60..99.
        internal readonly TachieFaceItem[] Faces = [];
        internal readonly GraphicsDevices Devices = new();
        internal readonly IGraphicsDevicesAndContext Context;
        internal readonly ITimelineSource Source;
        internal readonly FrameCacheStore Store;
        internal readonly TimelineFrameCache.PreviewViewport View;
        private readonly Harmony harmony = new("ymm.tests.animation-checks");
        private readonly bool oldPreview = TimelineFrameCache.PreviewEnabled, oldExport = TimelineFrameCache.ExportEnabled, oldGpu = TimelineFrameCache.GpuRetentionEnabled;
        private readonly Func<object, TimelineFrameCache.PreviewViewport?>? oldViewport = TimelineFrameCache.TestViewport;
        private readonly FrameCacheStore? oldStore = TimelineFrameCache.StoreIfCreated;
        internal Case(Assembly host, string name)
        {
            bool vowels = name == "hidden-vowels";
            string layout = TachieLayouts.Nested.Contains(name) ? name : "root";
            harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new(typeof(AnimationTachieChecks), nameof(SkipLoader)));
            ProbeLoader.Stub(ProbeLoader.Assemblies(host).Append(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(host.Location)!, AnimationTachieDependencies.AssemblyName + ".dll"))));
            Fixture = new(vowels);
            var voices = Fixture.Timeline.Items.OfType<VoiceItem>().Take(2).ToArray();
            voices[0].Frame = 30; voices[0].Length = 30; voices[1].Frame = 90; voices[1].Length = 30;
            if (vowels)
            {
                var shapes = new[] { MouthShape.A, MouthShape.I, MouthShape.U, MouthShape.E, MouthShape.O };
                for (int i = 0; i < shapes.Length; i++)
                    File.WriteAllBytes(Path.Combine(Fixture.Root, "mouth." + (i == 0 ? "A" : shapes[i].ToString().ToLowerInvariant()) + ".png"),
                        FramePixelChecks.Png(80, 100, (x, y) => ((byte)(40 + i * 40), (byte)20, (byte)220, (byte)(y >= 60 && y < 64 + i * 3 ? 255 : 0))));
                foreach (var voice in voices)
                {
                    voice.LipSyncFrames = shapes.Select((shape, i) => new LipSyncFrame(TimeSpan.FromSeconds(i / 3.0), shape)).ToArray();
                    var property = voice.TachieFaceParameter.GetType().GetProperty("MouthAnimation")!;
                    property.SetValue(voice.TachieFaceParameter, Enum.Parse(property.PropertyType, "VowelLipSyncPriority"));
                    var eye = voice.TachieFaceParameter.GetType().GetProperty("EyeAnimation")!;
                    eye.SetValue(voice.TachieFaceParameter, Enum.Parse(eye.PropertyType, "AlwaysClose"));
                }
            }
            Fixture.Timeline.Items = Fixture.Timeline.Items.Where(item => item is TachieItem).ToImmutableList().AddRange(voices);
            if (name is "differential" or "same-layer-faces")
            {
                // The upper face (40..99) sets the body, the lower one (60..119) only an etc part. Of one layer, the
                // first in the item list is shown.
                string green = Path.Combine(Fixture.Root, "body-green.png"), marker = Path.Combine(Fixture.Root, "etc-marker.png");
                File.WriteAllBytes(green, FramePixelChecks.Png(80, 100, (x, _) => (20, (byte)(120 + x), 40, 255)));
                File.WriteAllBytes(marker, FramePixelChecks.Png(80, 100, (x, y) => (250, 250, 250, (byte)(x < 20 && y < 20 ? 255 : 0))));
                var upper = new TachieFaceItem(Fixture.Characters[0]) { Frame = 40, Length = 60, Layer = 21 };
                var lower = new TachieFaceItem(Fixture.Characters[0]) { Frame = 60, Length = 60, Layer = name == "differential" ? 20 : 21 };
                AnimationTachieFixture.Set(upper.TachieFaceParameter, "Body", Fixture.Images[1]);
                if (name == "differential")
                {
                    AnimationTachieFixture.Set(lower.TachieFaceParameter, "Etc1", marker);
                    AnimationTachieFixture.Set(Fixture.Tachies[0].TachieItemParameter, "IsDifferentialComposite", true);
                }
                else AnimationTachieFixture.Set(lower.TachieFaceParameter, "Body", green);
                Faces = [upper, lower];
                Fixture.Timeline.Items = Fixture.Timeline.Items.Add(upper).Add(lower);
            }
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
        internal void CheckInstallRollback(Assembly host)
        {
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
            var foreign = new Harmony("ymm.tests.animation-foreign-read");
            var method = host.GetType("YukkuriMovieMaker.Player.Video.Items.TachieSource", true)!
                .GetMethod("ReadVolumeAfterRequiredWait", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                foreign.Patch(method, prefix: new(typeof(AnimationTachieChecks), nameof(NoOp)));
                Check(!TimelineFrameCache.TryInstall(host, harmony, out var reason) && reason.Contains("external Harmony owner", StringComparison.Ordinal),
                    "Conflicting lip-sync patch was admitted");
                Check(!FrameRenderReadiness.Installed && !NativeTachieReadiness.Installed,
                    "A failed native install left readiness state installed");
                Check(Harmony.GetPatchInfo(method)?.Owners.Contains(foreign.Id) == true,
                    "Rollback removed someone else's patch");
            }
            finally { foreign.UnpatchAll(foreign.Id); }
            Check(TimelineFrameCache.TryInstall(host, harmony, out var recovered), "Install did not recover after rollback: " + recovered);
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
            }, TimeSpan.FromSeconds(5)), "Animation frame did not become reusable: " + frame + "; " + TimelineFrameCache.Status);
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
