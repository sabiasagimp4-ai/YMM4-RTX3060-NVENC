using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class EditDescriptionMeasurements
{
    internal static void Run(Assembly host, bool incremental = false)
    {
        foreach (bool text in new[] { false, true })
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try { RunCase(host, text, incremental); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Edit description measurement" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), "Edit description measurement exceeded 30 seconds");
            if (failure is not null) throw new InvalidOperationException("Edit description measurement", failure);
        }
    }
    private static void RunCase(Assembly host, bool text, bool incremental)
    {
        const int Count = 1000;
        var harmony = new Harmony("ymm.tests.edit-description");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix:
            new HarmonyMethod(typeof(EditDescriptionMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        try
        {
            var timeline = new Timeline(); timeline.VideoInfo.Width = 321; timeline.VideoInfo.Height = 181; timeline.VideoInfo.FPS = 30;
            timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, Count).Select(index =>
                text ? (IItem)new TextItem { Frame = 0, Length = 60, Layer = index, Text = "description " + index, Font = "Arial" }
                    : new ShapeItem { Frame = 0, Length = 60, Layer = index }));
            timeline.RefreshTimelineLengthAndMaxLayer();
            var scenes = new Scenes(false); scenes.AddScene(timeline); var scene = new Scene(timeline, scenes, []);
            using var tracker = incremental ? new KeyDependencyTracker(scene, true) : new KeyDependencyTracker(scene);
            string reason = string.Empty;
            Check(SpinWait.SpinUntil(() =>
            {
                if (!tracker.TryCapture(0, out var warm, out reason)) return false;
                using (warm) return warm!.Validate();
            }, TimeSpan.FromSeconds(10)), "Description did not become ready: " + reason);
            var target = (YukkuriMovieMaker.Project.Items.VisualItem)timeline.Items[Count / 2];
            for (int repeat = 1; repeat <= 2; repeat++)
            {
                long revision = tracker.Revision;
                var clock = Stopwatch.StartNew();
                target.X.SetFirstValue(12.345 + repeat);
                Check(tracker.Revision != revision, "The edited animation did not invalidate the description");
                Check(tracker.TryCapture(0, out var capture, out reason), reason);
                double editMilliseconds = clock.Elapsed.TotalMilliseconds;
                using (capture)
                {
                    var fullClock = Stopwatch.StartNew();
                    Check(FrameCacheKey.TryDescribe(scene, out string full, out _, out reason), reason);
                    double fullMilliseconds = fullClock.Elapsed.TotalMilliseconds;
                    Check(capture!.Model == full && capture.Validate(), "Edited description did not exactly equal the fresh full model");
                    Console.WriteLine("SPEEDUP5 " + JsonSerializer.Serialize(new
                    { kind = text ? "texts" : "shapes", items = Count, edited_items = 1, repeat,
                        edit_to_capture_ms = editMilliseconds, full_description_ms = fullMilliseconds,
                        normalized_time = editMilliseconds / fullMilliseconds, model_characters = full.Length,
                        exact_match = true, debounce_included = false, graphics_render_included = false,
                        incremental, reused_fragments = tracker.FragmentReused, serialized_fragments = tracker.FragmentSerialized }));
                }
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    private static bool SkipLoader() => false;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
