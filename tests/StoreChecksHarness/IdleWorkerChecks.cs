using NVEncVideoWriterPlugin;

internal static class IdleWorkerChecks
{
    internal static void Run()
    {
        Check(IdleWorkerPolicy.Count(0, 16, null) == 1, "Unknown adapter must keep one worker");
        var memory = new GpuMemorySnapshot(8 * (1024 * GpuMemoryPolicy.MiB), (1024 * GpuMemoryPolicy.MiB), 12 * (1024 * GpuMemoryPolicy.MiB), false);
        Check(IdleWorkerPolicy.Count(0, 8, memory) == 1, "Unmeasured worker memory must keep one worker");
        Check(IdleWorkerPolicy.Count(0, 8, memory, measuredWorkerBytes: 4 * 1024 * GpuMemoryPolicy.MiB) == 1,
            "Measured device memory must constrain auto workers");
        Check(IdleWorkerPolicy.Count(0, 2, memory) == 1, "Auto reserves half the CPU cores");
        Check(IdleWorkerPolicy.Count(0, 8, memory, measuredWorkerBytes: 512 * GpuMemoryPolicy.MiB) == 4, "Auto can use four workers with headroom");
        Check(IdleWorkerPolicy.Count(0, 8, memory with { CurrentUsage = 7 * (1024 * GpuMemoryPolicy.MiB) }) == 1,
            "Auto keeps one worker under GPU pressure");
        Check(IdleWorkerPolicy.Count(0, 8, memory with { Budget = 1, CurrentUsage = long.MaxValue },
            measuredWorkerBytes: 1) == 1, "Overflowing/over-budget usage must keep one worker");
        Check(IdleWorkerPolicy.Count(0, 8, memory with { Software = true }) == 1, "WARP auto must keep one worker");
        Check(IdleWorkerPolicy.Count(4, 8, memory, busy: true) == 1, "Activity must override manual workers");
        Check(IdleWorkerPolicy.Count(2, 2, null) == 2 && IdleWorkerPolicy.Count(99, 2, null) == 4,
            "Manual workers are bounded and available for software rendering checks");
        for (int seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            var cursor = new IdleCompletionCursor(17, 76);
            var completed = new HashSet<long>();
            foreach (int offset in Enumerable.Range(0, 60).OrderBy(_ => random.Next()))
            {
                long ordinal = 17 + offset;
                completed.Add(ordinal);
                long expected = 17;
                while (completed.Contains(expected)) expected++;
                Check(cursor.Complete(ordinal) == expected && cursor.Complete(ordinal) == expected,
                    "Out-of-order or duplicate completion advanced across an unfinished frame");
            }
            Check(cursor.Next == 77, "Completed range did not reach its end");
        }
        Console.WriteLine("Idle worker auto/manual pressure policy and out-of-order resume cursor OK");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
