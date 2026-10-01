namespace NVEncVideoWriterPlugin;

// Pixel layout of the cache status bars, host-independent so that it is unit-tested without WPF.
// A frame f covers x in [f * pixelsPerFrame - offset, (f + 1) * pixelsPerFrame - offset).
internal static class CacheBarLayout
{
    internal const int SamplesPerColumn = 4;
    internal const byte NoFrames = 255;

    // The frames sampled for each pixel column; column c uses frames[columnStarts[c]..columnStarts[c + 1]].
    // Columns covering many frames are sampled evenly, so the cost stays bounded at any zoom.
    internal static int[] SampleFrames(double offset, double pixelsPerFrame, int width, int frameCount, out int[] columnStarts)
    {
        columnStarts = new int[Math.Max(0, width) + 1];
        var frames = new List<int>();
        if (width <= 0 || frameCount <= 0 || !(pixelsPerFrame > 0) || !double.IsFinite(offset) || !double.IsFinite(pixelsPerFrame))
            return [];
        for (int column = 0; column < width; column++)
        {
            columnStarts[column] = frames.Count;
            double first = (column + offset) / pixelsPerFrame;
            double end = (column + 1 + offset) / pixelsPerFrame;
            long from = Math.Max(0, (long)Math.Floor(first));
            long to = Math.Min(frameCount - 1L, (long)Math.Ceiling(end) - 1);
            if (to < from) continue;
            long count = to - from + 1;
            if (count <= SamplesPerColumn)
                for (long frame = from; frame <= to; frame++) frames.Add((int)frame);
            else
                for (int sample = 0; sample < SamplesPerColumn; sample++)
                    frames.Add((int)(from + (count - 1) * sample / (SamplesPerColumn - 1)));
        }
        columnStarts[width] = frames.Count;
        return frames.ToArray();
    }

    // A column shows the lowest state among its samples: green only where every sampled frame is in RAM,
    // blue where every one is stored and some only on disk.
    internal static byte[] ColumnStates(ReadOnlySpan<byte> residency, int[] columnStarts)
    {
        var states = new byte[Math.Max(0, columnStarts.Length - 1)];
        for (int column = 0; column < states.Length; column++)
        {
            int start = columnStarts[column], end = columnStarts[column + 1];
            if (start == end) { states[column] = NoFrames; continue; }
            byte lowest = 2;
            for (int i = start; i < end; i++) lowest = Math.Min(lowest, residency[i]);
            states[column] = lowest;
        }
        return states;
    }

    // Runs of equal, drawn states (1 = disk, 2 = RAM).
    internal static List<(int X, int Width, byte State)> Runs(byte[] states)
    {
        var runs = new List<(int, int, byte)>();
        for (int x = 0; x < states.Length;)
        {
            byte state = states[x];
            int end = x + 1;
            while (end < states.Length && states[end] == state) end++;
            if (state is 1 or 2) runs.Add((x, end - x, state));
            x = end;
        }
        return runs;
    }
}
