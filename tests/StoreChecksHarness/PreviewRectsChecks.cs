using System.Diagnostics;
using NVEncVideoWriterPlugin;

internal static class PreviewRectsChecks
{
    internal static void Run()
    {
        Check(PreviewUsage.KeyFor("Playing", false) == "Preview" && PreviewUsage.KeyFor("Paused", false) == "Preview",
            "Playing and paused previews must share a key without ShowOnlyPreviewEffect");
        Check(PreviewUsage.KeyFor("Playing", true) == "Playing" && PreviewUsage.KeyFor("Paused", true) == "Paused",
            "ShowOnlyPreviewEffect must keep playing and paused keys apart");
        Check(PreviewUsage.KeyFor("Exporting", false) == "Exporting", "Export key must never merge with the preview");

        Check(PreviewUsage.ModelUsesShowOnlyPreview("{\"$type\":\"YukkuriMovieMaker.Project.Effects.ShowOnlyPreviewEffect, YukkuriMovieMaker\"}")
            && !PreviewUsage.ModelUsesShowOnlyPreview("{\"$type\":\"YukkuriMovieMaker.Project.Effects.BlurEffect, YukkuriMovieMaker\"}"),
            "ShowOnlyPreviewEffect must be detected in the serialized model");
        Check(!PreviewUsage.RectsReusable("{\"$type\":\"YukkuriMovieMaker.Project.Effects.MeshDeformationEffect, YukkuriMovieMaker\"}")
            && PreviewUsage.RectsReusable("{\"$type\":\"YukkuriMovieMaker.Project.Effects.CenterPointEffect, YukkuriMovieMaker\"}"),
            "Selection-dependent mesh controllers must disable rect reuse");

        var rects = new PreviewRects<int>();
        rects.Remember("a", [1, 2], generation: 1, revision: 7);
        Check(rects.TryRecall("a", 1, 7, out var recalled) && recalled.SequenceEqual([1, 2]), "Remembered rects must be recalled");
        Check(!rects.TryRecall("b", 1, 7, out _), "Unknown key recalled rects");
        Check(!rects.TryRecall("a", 1, 8, out _) && rects.Count == 0 && !rects.TryRecall("a", 1, 7, out _),
            "An edit (new revision) must drop every remembered rect, also for the old revision");
        rects.Remember("a", [1], 1, 8);
        Check(!rects.TryRecall("a", 2, 8, out _) && rects.Count == 0, "A new cache generation must drop every remembered rect");
        for (int i = 0; i < 600; i++) rects.Remember("k" + i, [i], 1, 1);
        Check(rects.Count == 512 && !rects.TryRecall("k0", 1, 1, out _) && rects.TryRecall("k599", 1, 1, out _), "Rect cache must stay bounded (LRU)");
        Check(rects.TryRecall("k88", 1, 1, out _), "Precondition");
        for (int i = 600; i < 1000; i++) rects.Remember("k" + i, [i], 1, 1);
        Check(rects.TryRecall("k88", 1, 1, out _), "Recently recalled rects must survive eviction");

        var frame = TimeSpan.FromTicks(333_333);
        long start = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(100);
        Check(!rects.IsMissing(frame) && !rects.ShouldRequestRefresh(start, delay), "Nothing missing initially");
        rects.MarkMissing(frame, start);
        Check(rects.IsMissing(frame) && !rects.IsMissing(frame + frame), "Missing rects must be bound to their time");
        Check(!rects.ShouldRequestRefresh(start, delay), "Refresh must wait while the playhead may still be moving");
        long later = start + Stopwatch.Frequency; // one second later
        Check(rects.ShouldRequestRefresh(later, delay) && !rects.ShouldRequestRefresh(later, delay), "Refresh must be requested exactly once");
        rects.MarkMissing(frame + frame, later);
        Check(!rects.IsMissing(frame) && rects.IsMissing(frame + frame), "A newer missing frame replaces the older one");
        rects.Rendered();
        Check(!rects.IsMissing(frame + frame) && !rects.ShouldRequestRefresh(later + Stopwatch.Frequency, delay), "A real render clears the missing state");
        Console.WriteLine("Preview rects: shared preview key, bounded recall, drop on edit/clear, one delayed refresh per missing frame.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Preview rects: " + message);
    }
}
