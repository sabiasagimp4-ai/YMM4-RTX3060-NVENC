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

        // An image sequence item depends at each frame on the one image it shows (two frames per image here): its
        // segments end where the image changes, a transition at its end also shows its image of the frame before,
        // a wide frame only its frame's image, and the whole project every image.
        string[] images = Enumerable.Range(0, 5).Select(i => $@"C:\seq\img{i}.png").ToArray();
        var sequence = new Entry(400, 10, false, false, "S", [@"C:\seq\img0.png"], FrameFiles: Enumerable.Range(0, 10).Select(i => images[i / 2]).ToArray());
        var afterSequence = t with { Frame = 410, Length = 4 };
        var sceneOverSequence = d with { Frame = 404, Length = 2 };
        var withSequence = Index(a, sequence, afterSequence, sceneOverSequence);
        Check(withSequence.For(400).Files.SequenceEqual([@"C:\chara\voice.txt", images[0]])
            && withSequence.For(403).Files.SequenceEqual([@"C:\chara\voice.txt", images[0], images[1]]),
            "A sequence frame must depend on the image it shows (and the item's own files) only");
        withSequence.For(402, out int imageStart, out int imageEnd);
        Check(imageStart == 402 && imageEnd == 404, $"A sequence image's frames must be one segment, not [{imageStart}, {imageEnd})");
        Check(withSequence.For(411).Files.Contains(images[4]) && !withSequence.For(411).Files.Contains(images[3]),
            "A transition must depend on the image its item showed at the frame before");
        Check(withSequence.For(404).Wide && withSequence.For(404).Files.Contains(images[2]) && !withSequence.For(404).Files.Contains(images[3])
            && withSequence.For(405).Files.Contains(images[2]) && withSequence.For(404) != withSequence.Whole,
            "A wide frame must depend on its own image only");
        Check(images.All(withSequence.Whole.Files.Contains), "The whole project must depend on every image shown");
        Check(withSequence.For(10).Files.SequenceEqual(at10.Files), "A sequence must not change other frames");
        bool rejected = false;
        try { _ = Index(sequence with { FrameFiles = images }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Frame files of another length than the item must be rejected");

        // Selected face files are ordinary dependencies. They must split segments, be included through a
        // transition and by a wide frame, and never claim that the image-sequence decoder displayed them.
        var faces = new Entry(600, 30, false, false, "FACES", [], FileRanges:
            [new(600, 10, ["face-a.png"]), new(610, 10, ["face-b.png"]), new(620, 10, [])]);
        var faceIndex = Index(faces, t with { Frame = 630, Length = 5 });
        Check(faceIndex.For(609).Files.Contains("face-a.png") && !faceIndex.For(609).Files.Contains("face-b.png")
            && faceIndex.For(610).Files.Contains("face-b.png") && !faceIndex.For(620).Files.Contains("face-b.png"),
            "A selected face must depend only on the file of its range");
        Check(faceIndex.For(609).Shown is null && faceIndex.For(610).Shown is null, "Ordinary faces must not assert decoder sequence images");
        faceIndex.For(610, out int faceStart, out int faceEnd);
        Check(faceStart == 610 && faceEnd == 620, "Face ranges did not split segments");
        var faceTransition = Index(faces, t with { Frame = 610, Length = 5 });
        Check(faceTransition.For(612).Files.Contains("face-a.png") && faceTransition.For(612).Files.Contains("face-b.png"),
            "A transition must include the selected face on both sides");
        Check(faceIndex.Whole.Files.Contains("face-a.png") && faceIndex.Whole.Files.Contains("face-b.png")
            && Index(faces, d with { Frame = 700 }).For(705).Files.Contains("face-a.png"), "Whole/wide frames omitted face files");
        rejected = false;
        try { _ = Index(faces with { FileRanges = [new(599, 1, ["outside.png"])] }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "A face range outside the item must be rejected");

        // Faces of one character and layer are shown in item-list order: the frames where two are shown are keyed by
        // that order (always cacheable), other frames are not, and a wide frame names every such group.
        var upper = new Entry(800, 20, false, false, "FACE1", [], FaceGroup: "c/3");
        var lower = new Entry(810, 20, false, false, "FACE2", [], FaceGroup: "c/3");
        var otherLayer = new Entry(810, 20, false, false, "FACE3", [], FaceGroup: "c/4");
        var faceOrder = Index(upper, lower, otherLayer);
        var swappedFaces = Index(lower, upper, otherLayer);
        Check(faceOrder.For(815).Cacheable && faceOrder.For(815).Content.Contains("|order:face:FACE1,FACE2", StringComparison.Ordinal)
            && swappedFaces.For(815).Content.Contains("|order:face:FACE2,FACE1", StringComparison.Ordinal),
            "Faces of one layer shown together must be keyed by their list order");
        Check(faceOrder.For(805).Content == swappedFaces.For(805).Content && faceOrder.For(825).Content == swappedFaces.For(825).Content
            && !faceOrder.For(805).Content.Contains("|order:", StringComparison.Ordinal), "Frames showing one face of a group must not name the order");
        var faceScene = Index(upper, lower, d with { Frame = 815, Length = 2 });
        Check(faceScene.For(816).Content.Contains("|order:face:FACE1,FACE2", StringComparison.Ordinal)
            && faceScene.Whole.Content.Contains("|order:face:FACE1,FACE2", StringComparison.Ordinal), "A wide frame must name every face group");

        // An item formatting numbers with the thread's culture marks its frames (and a transition after it).
        var number = new Entry(500, 10, false, false, "U", [], Culture: true);
        var withNumber = Index(a, number, t with { Frame = 510, Length = 5 });
        Check(!withNumber.For(10).Culture && withNumber.For(505).Culture && withNumber.For(512).Culture && withNumber.For(505).Cacheable,
            "Culture items must only mark their frames (and transitions after them)");
        var nestedNumber = new FrameDependencyIndex("G", [], "N", [], [a, d], nestedCulture: true, culture: "ja-JP");
        Check(!nestedNumber.For(10).Culture && nestedNumber.For(105).Culture && nestedNumber.Culture == "ja-JP",
            "A culture nested timeline must only mark scene frames");

        // The segment range: every frame in [start, end) has the same dependencies, and the frames just outside do not.
        foreach (int frame in new[] { -5, 0, 10, 19, 20, 29, 30, 55, 60, 65, 69, 70, 105, 110, 5000 })
        {
            var dependencies = index.For(frame, out int start, out int end);
            Check(start <= frame && frame < end && dependencies == index.For(frame), $"Frame {frame} must lie in its own segment [{start}, {end})");
            Check(index.For(start == int.MinValue ? -1_000_000 : start) == dependencies && index.For(end == int.MaxValue ? 1_000_000 : end - 1) == dependencies,
                $"The segment of frame {frame} [{start}, {end}) must share its dependencies");
            Check(start == int.MinValue || index.For(start - 1) != dependencies, $"Frame {start - 1} must start another segment than {frame}");
            Check(end == int.MaxValue || index.For(end) != dependencies, $"Frame {end} must start another segment than {frame}");
        }
        index.For(25, out int from, out int to);
        Check(from == 20 && to == 30, $"Frames 20-29 must be one segment, not [{from}, {to})");

        long frames = 0;
        var big = Index(Enumerable.Range(0, 5000).Select(i => new Entry(i * 10, 15, false, false, "I" + i, [])).ToArray());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < 50_000; f++) frames += big.For(f).Files.Length;
        clock.Stop();
        Console.WriteLine($"Frame dependencies: overlap, end frame, transitions (recursive), wide scene frames, partial invalidation, image sequences, face order, culture; 50k lookups over 5k items {clock.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Frame dependencies: " + message);
    }
}
