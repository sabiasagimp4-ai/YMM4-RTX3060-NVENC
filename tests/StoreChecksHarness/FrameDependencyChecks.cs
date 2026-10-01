using NVEncVideoWriterPlugin;
using Entry = NVEncVideoWriterPlugin.FrameDependencyIndex.Entry;

internal static class FrameDependencyChecks
{
    internal static void Run()
    {
        // Layout (frames): a [0,30) b [20,50) c [60,90) transition t [60,70) d (scene item) [100,110)
        var a = new Entry(0, 30, false, false, "A", [@"C:\media\a.wav"]);
        var b = new Entry(20, 30, false, false, "B", [@"C:\media\b.png", @"C:\MEDIA\A.WAV"]);
        var c = new Entry(60, 30, false, false, "C", [@"C:\media\c.mp4"]);
        var t = new Entry(60, 10, true, false, "T", []);
        var d = new Entry(100, 10, false, true, "D", []);
        FrameDependencyIndex Index(params Entry[] entries) =>
            new("G", [@"C:\chara\voice.txt"], "N", [@"C:\nested\n.png"], entries);
        var index = Index(a, b, c, t, d);

        var at10 = index.For(10);
        Check(!at10.Wide && at10.Content.Contains("|A", StringComparison.Ordinal) && !at10.Content.Contains("|B", StringComparison.Ordinal),
            "Frame 10 must depend on a only");
        Check(at10.Files.SequenceEqual([@"C:\chara\voice.txt", @"C:\media\a.wav"]), "Frame files must be the global and item files only");
        Check(index.For(19) == at10 && index.For(0) == at10, "Frames of one segment must share their dependencies");
        var at25 = index.For(25);
        Check(at25.Content.Contains("|A|B", StringComparison.Ordinal) && at25.Files.Length == 3,
            "Overlapping items must both be included, and file names compared case-insensitively");
        Check(index.For(29) == at25 && index.For(30) != at25 && !index.For(30).Content.Contains("|A", StringComparison.Ordinal),
            "An item must not be included at its end frame");
        Check(index.For(55).Content.EndsWith("|items:0", StringComparison.Ordinal) && index.For(55).Files.Length == 1,
            "A frame without items must depend on the global part only");
        Check(index.For(-5).Content == index.For(55).Content && index.For(int.MaxValue).Content == index.For(55).Content,
            "Frames outside every item must share the empty dependencies");

        // The transition at 60 also renders what is shown at frame 59 (nothing) and 65 renders c and t.
        var at65 = index.For(65);
        Check(at65.Content.Contains("|C|T", StringComparison.Ordinal) && at65.Files.Contains(@"C:\media\c.mp4"), "Transition frames must include their items");
        var chained = Index(a with { Frame = 0, Length = 60 }, c, t);
        Check(chained.For(65).Content.Contains("|A|C|T", StringComparison.Ordinal) && chained.For(65).Files.Contains(@"C:\media\a.wav"),
            "A transition must include the items at its first frame - 1");
        var second = new Entry(40, 5, true, false, "T2", []);
        var recursive = Index(a with { Length = 45 }, second, t with { Frame = 45, Length = 10 }, c with { Frame = 45 });
        Check(recursive.For(50).Content.Contains("|A", StringComparison.Ordinal) && recursive.For(50).Content.Contains("|T2", StringComparison.Ordinal),
            "Transitions must be followed recursively");

        var at105 = index.For(105);
        Check(at105.Wide && at105 == index.Whole && at105.Content.Contains("|nested:N", StringComparison.Ordinal)
            && at105.Files.Contains(@"C:\nested\n.png") && at105.Files.Contains(@"C:\media\c.mp4"),
            "A scene item must make the frame depend on the whole project");
        Check(!at10.Content.Contains("nested:N", StringComparison.Ordinal), "Ordinary frames must not depend on other timelines");
        var wideBefore = Index(d with { Frame = 0, Length = 60 }, t, c);
        Check(wideBefore.For(65).Wide, "A wide item reached through a transition must widen the frame");

        // Editing c changes only the frames that contain c.
        var edited = Index(a, b, c with { Hash = "C2" }, t, d);
        Check(edited.For(10).Content == at10.Content && edited.For(25).Content == at25.Content, "Edits must not change unrelated frames");
        Check(edited.For(65).Content != at65.Content && edited.For(105).Content != at105.Content, "Edits must change the frames that contain the item");
        var moved = Index(a, b, c with { Frame = 61, Hash = "C3" }, t, d);
        Check(moved.For(60).Content != at65.Content && !moved.For(60).Content.Contains("|C", StringComparison.Ordinal), "A moved item must leave its old frames");
        var reordered = Index(d, t, c, b, a);
        Check(reordered.For(25).Content == at25.Content && reordered.For(65).Content == at65.Content, "Item order must not change frame dependencies");
        Check(Index(a, b, c, t, d).For(10).Content != new FrameDependencyIndex("G2", [], "N", [], [a]).For(10).Content,
            "A global change must change every frame");

        // An item that cannot be fingerprinted only disables the frames that contain it.
        var font = new Entry(200, 10, false, false, "F", [], Uncacheable: true);
        var withFont = Index(a, font, t with { Frame = 210, Length = 5 });
        Check(withFont.For(10).Cacheable && !withFont.For(205).Cacheable && !withFont.Whole.Cacheable, "Uncacheable items must only disable their frames");
        Check(!withFont.For(212).Cacheable, "A transition after an uncacheable item must not be cacheable");
        var nestedFont = new FrameDependencyIndex("G", [], "N", [], [a, d], nestedUncacheable: true);
        Check(nestedFont.For(10).Cacheable && !nestedFont.For(105).Cacheable, "An uncacheable nested timeline must only disable scene frames");

        // An item keyed by object identities (random seeds) makes only its frames session frames, and a transition
        // after it; one in another timeline, only scene frames.
        var shaking = new Entry(300, 10, false, false, "R", [], Session: true);
        var withShaking = Index(a, shaking, t with { Frame = 310, Length = 5 });
        Check(!withShaking.For(10).Session && withShaking.For(305).Session && withShaking.For(312).Session && withShaking.For(305).Cacheable,
            "Session items must only mark their frames (and transitions after them), and stay cacheable");
        var nestedShaking = new FrameDependencyIndex("G", [], "N", [], [a, d], nestedSession: true);
        Check(!nestedShaking.For(10).Session && nestedShaking.For(105).Session, "A session nested timeline must only mark scene frames");

        long frames = 0;
        var big = Index(Enumerable.Range(0, 5000).Select(i => new Entry(i * 10, 15, false, false, "I" + i, [])).ToArray());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 50_000; f++) frames += big.For(f).Files.Length;
        clock.Stop();
        Console.WriteLine($"Frame dependencies: overlap, end frame, transitions (recursive), wide scene frames, partial invalidation; 50k lookups over 5k items {clock.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Frame dependencies: " + message);
    }
}
