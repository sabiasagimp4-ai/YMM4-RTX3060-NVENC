using System.Reflection;
using System.Collections.Immutable;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Project.Effects;

internal static class EditDescriptionChecks
{
    internal static void Run(Assembly host)
    {
        // Each independent case owns its STA, scene and cache, and has a hard time bound.
        for (int batch = 0; batch < 4; batch++)
        {
            int seed = 105 + batch;
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try { RunCase(host, seed); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Incremental description oracle" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), "Incremental edit oracle exceeded 30 seconds");
            if (failure is not null) throw new InvalidOperationException("Incremental edit oracle seed " + seed, failure);
        }
        Console.WriteLine("SPEEDUP5_CHECK edits=1000; exact_json=true; exact_paths=true; additions/deletions/moves/keyframes/effects/characters/settings/undo/redo=true; product_enabled=" + false);
    }

    private sealed class ForeignBlur : GaussianBlurEffect { public int Unannounced { get; set; } }

    private static void RunCase(Assembly host, int seed)
    {
        var harmony = new Harmony("ymm.tests.edit-oracle." + seed);
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix:
            new HarmonyMethod(typeof(EditDescriptionChecks), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        try
        {
            var random = new Random(seed);
            var timeline = new Timeline();
            var scenes = new Scenes(false); scenes.AddScene(timeline);
            var nested = new Timeline(); scenes.AddScene(nested);
            var scene = new Scene(timeline, scenes, []);
            var character = VoiceDescriptionMeasurements.Character("edit oracle " + seed);
            var voice = new VoiceItem(character) { Frame = 0, Length = 60, Font = "Arial", VoiceCache = new byte[4096] };
            var text = new TextItem { Frame = 0, Length = 60, Font = "Arial", Text = "oracle" };
            var shape = new ShapeItem { Frame = 0, Length = 60 };
            timeline.Items = [shape, text, voice];
            ItemDescriptionFragments? cache = null;
            using var fragments = cache = new ItemDescriptionFragments(sender => cache!.Invalidate(sender));
            void Compare(int edit)
            {
                string incremental, incrementalReason; string[] incrementalPaths; bool incrementalOk;
                using (fragments.Enter(scene))
                    incrementalOk = FrameCacheKey.TryDescribe(scene, out incremental, out incrementalPaths, out incrementalReason);
                bool fullOk = FrameCacheKey.TryDescribe(scene, out string full, out string[] fullPaths, out string fullReason);
                Check(incremental == full && incrementalOk == fullOk && incrementalPaths.SequenceEqual(fullPaths),
                    $"Edit {edit} mismatch: incremental={incrementalReason}; full={fullReason}");
            }
            Compare(-1); Compare(-2);
            Console.WriteLine("SPEEDUP5_GUARD reused=" + fragments.Reused + "; bypass=" + fragments.WitnessBypass);
            Check(fragments.Reused > 0, "Audited simple items did not reuse any fragments: " + fragments.WitnessBypass);
            var keyframes = new KeyFrames(); shape.X.SetKeyFrames(keyframes);
            scenes.UndoRedoManager.Subscribe(timeline);
            var external = new ForeignBlur();
            for (int edit = 0; edit < 250; edit++)
            {
                switch (random.Next(12))
                {
                    case 0:
                        timeline.Items = timeline.Items.Add(new ShapeItem { Frame = random.Next(60), Length = random.Next(1, 60), Layer = random.Next(20) });
                        break;
                    case 1:
                        if (timeline.Items.Count > 3) timeline.Items = timeline.Items.RemoveAt(random.Next(3, timeline.Items.Count));
                        break;
                    case 2:
                        shape.Frame = random.Next(60); shape.Layer = random.Next(10); shape.X.Values[0].Value = random.NextDouble() * 100;
                        break;
                    case 3:
                        if (keyframes.Count < 4) keyframes.Insert(random.Next(1, 60));
                        else keyframes.RemoveAt(random.Next(keyframes.Count));
                        shape.X.AnimationType = AnimationType.直線移動;
                        break;
                    case 4:
                        shape.VideoEffects = shape.VideoEffects.Add(new GaussianBlurEffect());
                        if (shape.VideoEffects.Count > 4) shape.VideoEffects = shape.VideoEffects.RemoveAt(0);
                        break;
                    case 5:
                        if (shape.VideoEffects.Count > 1) shape.VideoEffects = shape.VideoEffects.Reverse().ToImmutableList();
                        break;
                    case 6:
                        character.Name = "changed " + random.Next(10000); character.X.SetFirstValue(random.NextDouble());
                        break;
                    case 7:
                        timeline.VideoInfo.Width = 320 + random.Next(8); nested.VideoInfo.FPS = 24 + random.Next(8);
                        break;
                    case 8:
                        shape.Length++;
                        scenes.UndoRedoManager.Record();
                        scenes.UndoRedoManager.UndoAsync().GetAwaiter().GetResult();
                        Compare(edit);
                        scenes.UndoRedoManager.RedoAsync().GetAwaiter().GetResult();
                        break;
                    case 9:
                        text.Text = "0.00049\\Unicode 読み値 " + random.Next(1000);
                        voice.VoiceCache![random.Next(voice.VoiceCache.Length)] ^= 1; // Mutable array, no notification.
                        break;
                    case 10:
                        text.VideoEffects = [external]; external.Unannounced++;
                        break;
                    case 11:
                        shape.X.SetFirstValue(edit % 2 == 0 ? 0.0004 : 0.00049);
                        shape.X.Bezier.Points[0].Point = new System.Numerics.Vector2((float)random.NextDouble(), (float)random.NextDouble());
                        break;
                }
                Compare(edit);
            }
            // The tracker receives a mutable AnimationValue's child notification even when its parent is unchanged.
            using var tracker = new KeyDependencyTracker(scene);
            tracker.TryCapture(out var capture, out _); capture?.Dispose();
            long revision = tracker.Revision;
            shape.X.Values[0].Value += .01;
            Check(tracker.Revision != revision, "Child value edit did not invalidate the tracked model");
            timeline.Items = [];
            Compare(251);
            Check(fragments.Retained == 0, "Removed items retained fragment entries");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
