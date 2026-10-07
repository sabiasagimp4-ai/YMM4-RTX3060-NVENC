namespace NVEncVideoWriterPlugin;

// A bounded, allocation-free traversal. Every frame appears once, including frames before the CTI.
internal static class IdleFramePlan
{
    internal static (int Start, int End) Range(int length, int start, int endExclusive)
    {
        length = Math.Max(0, length);
        int first = Math.Clamp(start, 0, length);
        int end = endExclusive <= 0 ? length : Math.Clamp(endExclusive, 0, length);
        return (first, Math.Max(first, end));
    }
    internal static bool TryGetFrame(int start, int endExclusive, int anchor, IdleCacheOrder order, long ordinal, out int frame)
    {
        frame = 0;
        if (start < 0 || endExclusive <= start) return false;
        if (!TryGetFrame(endExclusive - start, Math.Clamp(anchor, start, endExclusive - 1) - start, order, ordinal, out int relative)) return false;
        frame = start + relative;
        return true;
    }
    internal static bool TryGetFrame(int length, int anchor, IdleCacheOrder order, long ordinal, out int frame)
    {
        frame = 0;
        if (length <= 0 || ordinal < 0 || ordinal >= length) return false;
        anchor = Math.Clamp(anchor, 0, length - 1);
        long value;
        if (order == IdleCacheOrder.FromStart) value = ordinal;
        else if (order == IdleCacheOrder.AroundCurrentTime)
        {
            int paired = Math.Min(anchor, length - 1 - anchor);
            long pairedCount = 2L * paired + 1;
            if (ordinal < pairedCount)
                value = ordinal == 0 ? anchor : anchor + (ordinal % 2 == 1 ? -(ordinal + 1) / 2 : ordinal / 2);
            else
                value = anchor + (anchor > length - 1 - anchor ? -(ordinal - paired) : ordinal - paired);
        }
        else value = (anchor + ordinal) % length;
        frame = (int)value;
        return true;
    }
}

// When the pre-renderer runs: a change of the timeline's current frame is playback or a seek, and a playing player
// draws the frames just ahead of the playhead itself.
internal static class IdleSchedule
{
    internal enum FrameChange { None, Playback, Seek }

    // While playing the frame advances by itself: a step forward of up to two seconds (at least 60 frames, for fast
    // playback between two observations) is playback; a jump back (a loop, a click on the ruler) or further forward
    // is a seek. Paused, every change is a seek.
    internal static FrameChange Classify(int previous, int frame, bool playing, double fps)
    {
        if (frame == previous) return FrameChange.None;
        long step = (long)frame - previous;
        double limit = Math.Max(60, double.IsFinite(fps) ? 2 * fps : 0);
        return playing && step > 0 && step <= limit ? FrameChange.Playback : FrameChange.Seek;
    }

    // The frames ahead of the playhead the player reaches while a worker renders one frame, plus one: a worker leaves
    // them to the player (it would finish them too late).
    internal static int Lead(double fps, double frameMilliseconds)
    {
        if (!double.IsFinite(fps) || fps <= 0) return 1;
        double played = fps * (double.IsFinite(frameMilliseconds) ? Math.Max(0, frameMilliseconds) : 0) / 1000;
        return (int)Math.Clamp(Math.Ceiling(played) + 1, 1, 600);
    }

    // A frame the playing player draws before a worker could: at the playhead or within the lead after it. Frames
    // before the playhead (reached after the plan wraps to the start) are kept for the next playback.
    internal static bool LeftToPlayer(int frame, int playhead, int lead) => frame >= playhead && frame - (long)playhead <= lead;

    // A worker's frame time, smoothed (the first sample is taken as it is).
    internal static double Smooth(double previous, double sample) =>
        !double.IsFinite(sample) || sample < 0 ? previous : previous <= 0 || !double.IsFinite(previous) ? sample : previous * 0.8 + sample * 0.2;
}
