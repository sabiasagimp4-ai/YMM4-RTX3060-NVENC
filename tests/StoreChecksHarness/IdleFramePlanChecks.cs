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
    }

    private static int[] Frames(int length, int anchor, IdleCacheOrder order) => Enumerable.Range(0, length)
        .Select(ordinal => { Check(IdleFramePlan.TryGetFrame(length, anchor, order, ordinal, out int frame), "valid ordinal"); return frame; }).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new Exception("Idle plan: " + message); }
}
