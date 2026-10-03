using NVEncVideoWriterPlugin;

// GpuMemoryPolicy: the GPU frame retention budget from the render adapter's video memory.
internal static class GpuBudgetChecks
{
    private const long M = GpuMemoryPolicy.MiB;

    internal static void Run()
    {
        // RTX 3060 12 GB: grows in steps after three healthy samples, up to the limit (a quarter of 12 GB is more).
        var policy = new GpuMemoryPolicy();
        var rtx3060 = new GpuMemorySnapshot(11264 * M, 1500 * M, 12288 * M, false);
        long current = GpuMemoryPolicy.InitialBudget;
        Check(policy.Next(current, 0, 2048 * M, rtx3060) == current, "first healthy sample holds");
        Check(policy.Next(current, 0, 2048 * M, rtx3060) == current, "second healthy sample holds");
        current = policy.Next(current, 0, 2048 * M, rtx3060);
        Check(current == 256 * M, "third sample grows by one step");
        current = Settle(policy, current, 2048 * M, rtx3060);
        Check(current == 2048 * M, $"a 12 GB card reaches the 2048 MiB limit, not {current / M} MiB");
        Check(Settle(new GpuMemoryPolicy(), 128 * M, 512 * M, rtx3060) == 512 * M, "a lower limit wins");
        Check(Settle(new GpuMemoryPolicy(), 128 * M, 8192 * M, rtx3060) == 3072 * M, "at most a quarter of dedicated memory");

        // Its own retained frames are not pressure: they are part of the usage the target subtracts.
        Check(new GpuMemoryPolicy().Next(2048 * M, 2048 * M, 2048 * M, rtx3060 with { CurrentUsage = 3500 * M }) == 2048 * M,
            "retained frames do not shrink their own budget");

        // 4 GB (laptop RTX 3050): a quarter is 1 GiB; 8 GB (RTX 4060): 2 GiB.
        Check(Settle(new GpuMemoryPolicy(), 128 * M, 8192 * M, new(3584 * M, 1000 * M, 4096 * M, false)) == 1024 * M, "4 GB card: 1 GiB");
        Check(Settle(new GpuMemoryPolicy(), 128 * M, 8192 * M, new(7400 * M, 1200 * M, 8192 * M, false)) == 2048 * M, "8 GB card: 2 GiB");
        // Integrated GPU: little dedicated memory, stays at the initial budget whatever its shared budget.
        Check(Settle(new GpuMemoryPolicy(), 128 * M, 2048 * M, new(8192 * M, 500 * M, 128 * M, false)) == 128 * M, "integrated GPU holds 128 MiB");

        // Pressure: another application takes VRAM and the OS lowers this process's budget.
        policy = new GpuMemoryPolicy();
        current = policy.Next(1024 * M, 1024 * M, 2048 * M, rtx3060 with { Budget = 3000 * M, CurrentUsage = 2900 * M });
        Check(current <= 512 * M, $"pressure at least halves at once, not {current / M} MiB");
        current = policy.Next(1024 * M, 512 * M, 2048 * M, rtx3060 with { Budget = 2000 * M, CurrentUsage = 1500 * M });
        Check(current == 0, "no room left drops retention to zero");
        Check(policy.Next(0, 0, 2048 * M, rtx3060) == 0, "recovery waits for consecutive healthy samples");

        // Unmeasured adapters keep their budget and never grow.
        policy = new GpuMemoryPolicy();
        for (int i = 0; i < 10; i++)
            Check(policy.Next(128 * M, 0, 2048 * M, new(64 * 1024 * M, 100 * M, 0, true)) == 128 * M, "software adapter (WARP) holds");
        Check(policy.Next(128 * M, 0, 2048 * M, null) == 128 * M, "failed sample holds");
        Check(policy.Next(128 * M, 0, 2048 * M, rtx3060 with { Budget = 0 }) == 128 * M, "invalid sample holds");
        policy.Next(128 * M, 0, 2048 * M, rtx3060);
        policy.Next(128 * M, 0, 2048 * M, rtx3060);
        Check(policy.Next(128 * M, 0, 2048 * M, null) == 128 * M && policy.Next(128 * M, 0, 2048 * M, rtx3060) == 128 * M,
            "a failed sample resets the growth count");
        Check(policy.Next(1024 * M, 0, 256 * M, null) == 256 * M, "a lowered limit applies even without a sample");

        Check(GpuMemoryPolicy.EntryLimit(128 * M) == 64 && GpuMemoryPolicy.EntryLimit(2048 * M) == 512
            && GpuMemoryPolicy.EntryLimit(64 * 1024 * M) == 1024, "entry limit scales with the budget, 64 to 1024");
        Console.WriteLine("VRAM budget: growth to the card's limit (4/8/12 GB, integrated), own frames, pressure, unmeasured adapters and entry limit passed.");
    }

    private static long Settle(GpuMemoryPolicy policy, long current, long maximum, GpuMemorySnapshot sample)
    {
        for (int i = 0; i < 200; i++)
        {
            long next = policy.Next(current, current, maximum, sample with { CurrentUsage = sample.CurrentUsage + current });
            Check(next <= maximum, "never exceeds the limit");
            current = next;
        }
        return current;
    }

    private static void Check(bool result, string message) { if (!result) throw new Exception("VRAM budget: " + message); }
}
