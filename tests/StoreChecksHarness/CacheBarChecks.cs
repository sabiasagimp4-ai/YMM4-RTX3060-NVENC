using NVEncVideoWriterPlugin;

internal static class CacheBarChecks
{
    internal static void Run()
    {
        // Zoomed in: 10 px per frame; each column holds one frame, columns 0-9 frame 0, 10-19 frame 1.
        int[] frames = CacheBarLayout.SampleFrames(0, 10, 50, 100, out int[] starts);
        Check(frames.Length == 50 && frames[0] == 0 && frames[9] == 0 && frames[10] == 1 && frames[49] == 4, "One frame per column when zoomed in");
        Check(starts[0] == 0 && starts[50] == 50, "Column starts index the samples");
        // A column straddling two frames samples both (2.5 px per frame: column 2 covers frames 0 and 1).
        frames = CacheBarLayout.SampleFrames(0, 2.5, 3, 100, out starts);
        Check(frames[starts[2]..starts[3]].SequenceEqual([0, 1]), "A column on a frame boundary samples both frames");
        // Scrolled: offset 5 px at 10 px per frame puts frame 1 at x = 5.
        frames = CacheBarLayout.SampleFrames(5, 10, 10, 100, out starts);
        Check(frames[starts[4]] == 0 && frames[starts[5]] == 1, "The scroll offset shifts the frames");
        // Zoomed out: 100 frames per column are sampled evenly at both ends.
        frames = CacheBarLayout.SampleFrames(0, 0.01, 10, 1000, out starts);
        Check(frames.Length == 40 && frames[starts[1]..starts[2]].SequenceEqual([100, 133, 166, 199]), "Columns with many frames are sampled evenly");
        // Past the end of the timeline there are no frames.
        frames = CacheBarLayout.SampleFrames(0, 1, 10, 5, out starts);
        var states = CacheBarLayout.ColumnStates(new byte[] { 2, 2, 1, 0, 2 }, starts);
        Check(states.SequenceEqual(new byte[] { 2, 2, 1, 0, 2, 255, 255, 255, 255, 255 }), "Column states and empty columns");
        Check(CacheBarLayout.Runs(states).SequenceEqual([(0, 2, (byte)2), (2, 1, (byte)1), (4, 1, (byte)2)]), "Runs merge equal drawn states");
        frames = CacheBarLayout.SampleFrames(0, 0.5, 2, 4, out starts);
        Check(CacheBarLayout.ColumnStates(new byte[] { 2, 1, 2, 0 }, starts).SequenceEqual(new byte[] { 1, 0 }),
            "A column shows its lowest sampled state");
        Check(CacheBarLayout.SampleFrames(0, 0, 10, 10, out _).Length == 0 && CacheBarLayout.SampleFrames(double.NaN, 1, 10, 10, out _).Length == 0
            && CacheBarLayout.SampleFrames(0, 1, 0, 10, out _).Length == 0, "Invalid views sample nothing");
        Console.WriteLine("Cache bars: per-column sampling at any zoom, scroll offset, frame boundaries, lowest state, runs.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Cache bars: " + message);
    }
}
