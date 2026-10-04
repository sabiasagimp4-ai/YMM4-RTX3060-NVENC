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
using YukkuriMovieMaker.Project.Items;

internal static class AnimationTachieChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly host)
    {
        foreach (string name in new[] { "pixels", "hidden-vowels", "timeout", "retained-ini", "changed-list", "overwrite" })
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { using var fixture = new Case(host, name == "hidden-vowels"); RunCase(fixture, name, host); }
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
        if (name == "retained-ini")
        {
            string ini = Path.ChangeExtension(test.Fixture.Images[0], ".ini");
            File.WriteAllText(ini, "blend=4\nopacity=25\nplaceon=face\n");
            TimelineFrameCache.Enabled = false; test.Update(30);
            Check(!AnimationTachieDependencies.TryFiles(test.Fixture.Tachies[0], test.Fixture.Timeline, out _), "Existing INI was admitted");
            File.Delete(ini);
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
        if (name is "changed-list" or "overwrite")
        {
            test.Warm(30);
            long hits = TimelineFrameCache.Hits;
            if (name == "changed-list")
            {
                string added = Path.Combine(test.Fixture.Root, "eye.2.png");
                File.Copy(test.Fixture.Images[0], added);
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
        int[] frames = [0, 29, 30, 31, 35, 40, 45, 50, 59, 60, 74, 89, 90, 104, 119, 120, 149, 150];
        var reference = frames.ToDictionary(frame => frame, frame => { test.Update(frame); return test.Pixels(); });
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
        using var tracker = new KeyDependencyTracker(test.Fixture.Scene);
        int rendered = 0;
        var result = IdleFramePreRenderer.PrimeLiveFrame(tracker, test.Fixture.Scene, test.Source, _ => rendered++,
            30, test.View, () => true, CancellationToken.None);
        Check(result == IdleFramePreRenderer.IdleFrameResult.Normal && rendered == 0, "Idle tachie started a competing calculation");
        Check(AnimationTachieDependencies.SessionResource.Contains("animation-blink-session://", StringComparison.Ordinal), "Blink session salt missing");
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
                sessionField.SetValue(core, session); taskField.SetValue(core, Task.CompletedTask);
                object[] args = [TimelineSourceUsage.Playing, 0, 30, session, Task.CompletedTask, TimeSpan.Zero, null!, null!];
                Check(!NativeTachieReadiness.ValueReady(core, 4, args, 0), "Partially published " + terminal + " was accepted as completed zero");
            }
        }
        finally { sessionField.SetValue(core, originalSession); taskField.SetValue(core, originalTask); }
    }
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
        internal readonly GraphicsDevices Devices = new();
        internal readonly IGraphicsDevicesAndContext Context;
        internal readonly ITimelineSource Source;
        internal readonly FrameCacheStore Store;
        internal readonly TimelineFrameCache.PreviewViewport View;
        private readonly Harmony harmony = new("ymm.tests.animation-checks");
        private readonly bool oldPreview = TimelineFrameCache.PreviewEnabled, oldExport = TimelineFrameCache.ExportEnabled, oldGpu = TimelineFrameCache.GpuRetentionEnabled;
        private readonly Func<object, TimelineFrameCache.PreviewViewport?>? oldViewport = TimelineFrameCache.TestViewport;
        private readonly FrameCacheStore? oldStore = TimelineFrameCache.StoreIfCreated;
        internal Case(Assembly host, bool vowels)
        {
            harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new(typeof(AnimationTachieChecks), nameof(SkipLoader)));
            ProbeLoader.Stub(ProbeLoader.Assemblies(host).Append(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(host.Location)!, AnimationTachieDependencies.AssemblyName + ".dll"))));
            Fixture = new(vowels);
            var voices = Fixture.Timeline.Items.OfType<VoiceItem>().Take(2).ToArray();
            voices[0].Frame = 30; voices[0].Length = 30; voices[1].Frame = 90; voices[1].Length = 30;
            if (vowels)
            {
                var shapes = new[] { MouthShape.A, MouthShape.I, MouthShape.U, MouthShape.E, MouthShape.O };
                for (int i = 0; i < shapes.Length; i++)
                    File.WriteAllBytes(Path.Combine(Fixture.Root, "mouth." + shapes[i].ToString().ToLowerInvariant() + ".png"),
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
            Fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
            Context = Devices.CreateContext();
            Source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!, Instance, null, [Context, Fixture.Scene, null], null)!;
            var dc = Context.DeviceContext;
            View = new(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode, Fixture.Scene.ID, Fixture.Timeline.ID, Stopwatch.GetTimestamp(), false);
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
