using NVEncVideoWriterPlugin;

internal static class IdleFramePlanChecks
{
    internal static void Run()
    {
        Check(Frames(7, 4, IdleCacheOrder.FromCurrentTime).SequenceEqual([4, 5, 6, 0, 1, 2, 3]), "CTI forward, then wrap");
        Check(Frames(7, 3, IdleCacheOrder.AroundCurrentTime).SequenceEqual([3, 2, 4, 1, 5, 0, 6]), "around CTI");
        Check(Frames(7, 1, IdleCacheOrder.AroundCurrentTime).SequenceEqual([1, 0, 2, 3, 4, 5, 6]), "asymmetric forward tail");
        Check(Frames(7, 5, IdleCacheOrder.AroundCurrentTime).SequenceEqual([5, 4, 6, 3, 2, 1, 0]), "asymmetric backward tail");
        for (int length = 1; length <= 120; length++)
            for (int anchor = 0; anchor < length; anchor++)
                foreach (var order in Enum.GetValues<IdleCacheOrder>())
                    Check(Frames(length, anchor, order).Order().SequenceEqual(Enumerable.Range(0, length)), "every frame exactly once");
        Check(!IdleFramePlan.TryGetFrame(0, 0, 0, 0, out _) && !IdleFramePlan.TryGetFrame(5, 0, 0, -1, out _), "empty and negative boundary");
        Check(!IdleFramePlan.TryGetFrame(5, 0, 0, 5, out _), "finished traversal stops");
        Check(IdleFramePlan.TryGetFrame(int.MaxValue, int.MaxValue - 2, IdleCacheOrder.FromCurrentTime, 3, out int frame) && frame == 1, "large timeline does not overflow");
        Check(IdleFramePlan.Range(100, 20, 0) == (20, 100) && IdleFramePlan.Range(100, 80, 20) == (80, 80), "configured end/empty range");
        foreach (var order in Enum.GetValues<IdleCacheOrder>())
            foreach (int anchor in new[] { 0, 22, 99 })
            {
                var frames = Enumerable.Range(0, 7).Select(i => { Check(IdleFramePlan.TryGetFrame(20, 27, anchor, order, i, out int f), "range traversal"); return f; }).ToArray();
                Check(frames.Order().SequenceEqual(Enumerable.Range(20, 7)), "bounded range includes every frame once");
            }
        Console.WriteLine("Idle plan: CTI forward/wrap, around CTI, start, exhaustive boundaries and no duplicates passed.");
        RunSchedule();
    }

    // Playback, seeks and the frames left to a playing player.
    private static void RunSchedule()
    {
        Check(IdleSchedule.Classify(10, 10, false, 30) == IdleSchedule.FrameChange.None
            && IdleSchedule.Classify(10, 10, true, 30) == IdleSchedule.FrameChange.None, "an unchanged frame is no change");
        Check(IdleSchedule.Classify(10, 11, false, 30) == IdleSchedule.FrameChange.Seek
            && IdleSchedule.Classify(10, 9, false, 30) == IdleSchedule.FrameChange.Seek, "paused, every change is a seek");
        Check(IdleSchedule.Classify(10, 11, true, 30) == IdleSchedule.FrameChange.Playback
            && IdleSchedule.Classify(10, 70, true, 30) == IdleSchedule.FrameChange.Playback
            && IdleSchedule.Classify(10, 130, true, 60) == IdleSchedule.FrameChange.Playback, "playing, steps forward up to two seconds are playback");
        Check(IdleSchedule.Classify(10, 71, true, 30) == IdleSchedule.FrameChange.Seek
            && IdleSchedule.Classify(10, 131, true, 60) == IdleSchedule.FrameChange.Seek, "playing, a jump forward is a seek");
        Check(IdleSchedule.Classify(500, 0, true, 30) == IdleSchedule.FrameChange.Seek
            && IdleSchedule.Classify(500, 499, true, 30) == IdleSchedule.FrameChange.Seek, "playing, any step back (a loop) is a seek");
        Check(IdleSchedule.Classify(0, int.MaxValue, true, double.NaN) == IdleSchedule.FrameChange.Seek
            && IdleSchedule.Classify(int.MaxValue, int.MinValue, true, 30) == IdleSchedule.FrameChange.Seek, "extreme frames do not overflow");
        Check(IdleSchedule.Lead(30, 0) == 1 && IdleSchedule.Lead(30, 100) == 4 && IdleSchedule.Lead(60, 50) == 4
            && IdleSchedule.Lead(30, 1000) == 31, "lead: frames played while one frame renders, plus one");
        Check(IdleSchedule.Lead(0, 100) == 1 && IdleSchedule.Lead(double.NaN, 100) == 1 && IdleSchedule.Lead(30, double.NaN) == 1
            && IdleSchedule.Lead(30, -5) == 1 && IdleSchedule.Lead(30, 1e12) == 600, "lead: invalid input and the bound");
        Check(IdleSchedule.LeftToPlayer(100, 100, 3) && IdleSchedule.LeftToPlayer(103, 100, 3)
            && !IdleSchedule.LeftToPlayer(104, 100, 3) && !IdleSchedule.LeftToPlayer(99, 100, 3), "left to the player: the playhead and the lead after it");
        Check(!IdleSchedule.LeftToPlayer(0, int.MaxValue, 600) && IdleSchedule.LeftToPlayer(int.MaxValue, int.MaxValue - 1, 1), "left to the player: no overflow");
        Check(IdleSchedule.Smooth(0, 40) == 40 && IdleSchedule.Smooth(40, 90) == 50 && IdleSchedule.Smooth(40, double.NaN) == 40
            && IdleSchedule.Smooth(40, -1) == 40 && IdleSchedule.Smooth(double.NaN, 30) == 30, "smoothed frame time");
        Console.WriteLine("Idle schedule: playback and seeks told apart, frames left to the playing player, smoothed frame time passed.");
    }

    private static int[] Frames(int length, int anchor, IdleCacheOrder order) => Enumerable.Range(0, length)
        .Select(ordinal => { Check(IdleFramePlan.TryGetFrame(length, anchor, order, ordinal, out int frame), "valid ordinal"); return frame; }).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new Exception("Idle plan: " + message); }
}
