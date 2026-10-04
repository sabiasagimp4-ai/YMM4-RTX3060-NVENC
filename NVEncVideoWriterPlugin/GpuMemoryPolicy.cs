namespace NVEncVideoWriterPlugin;

// One sample of the adapter YMM4 renders with. Budget and CurrentUsage are this process's local (dedicated) video
// memory from IDXGIAdapter3.QueryVideoMemoryInfo: the OS's budget for it and what it uses now. All sizes are bytes.
internal readonly record struct GpuMemorySnapshot(long Budget, long CurrentUsage, long DedicatedVideoMemory, bool Software);

// Pure policy for the bytes of restored frames kept on the GPU (TimelineFrameCache.GpuRetentionBudget); the
// controller samples off the render thread. Like CacheMemoryPolicy: shrink at once, grow in steps after healthy
// samples, never grow on a failed sample.
internal sealed class GpuMemoryPolicy
{
    internal const long MiB = 1024 * 1024;
    // Before the first sample, and the fixed budget when the adapter cannot be measured (WARP, failed sample).
    internal const long InitialBudget = 128 * MiB;
    internal const long Step = 128 * MiB;
    private int healthySamples;

    // The physical-memory ceiling; Next also applies the OS budget and keeps room for other allocations.
    // Integrated GPUs report little dedicated memory and stay at 128 MiB.
    internal static long Ceiling(long maximum, long dedicatedVideoMemory) =>
        Math.Min(maximum, Math.Clamp(dedicatedVideoMemory, InitialBudget, 8192 * MiB));

    // Headroom left for YMM4 itself (effects, decoders, export textures), other processes and usage spikes.
    internal static long Reserve(long budget) => Math.Max(1024 * MiB, budget / 5);

    internal long Next(long current, long retained, long maximum, GpuMemorySnapshot? sample)
    {
        current = Math.Clamp(current, 0, maximum);
        if (sample is not { } s || s.Software || s.Budget <= 0 || s.CurrentUsage < 0 || s.DedicatedVideoMemory < 0)
        {
            healthySamples = 0;
            return current; // An unmeasured adapter keeps its budget and never grows.
        }
        long ceiling = Ceiling(maximum, s.DedicatedVideoMemory);
        long reserve = Reserve(s.Budget);
        // Retained frames are part of the usage; what the rest of the process uses is what retention must not crowd.
        long others = Math.Max(0, s.CurrentUsage - Math.Max(0, retained));
        long target = Math.Clamp(s.Budget - reserve - others, 0, ceiling);
        bool pressure = s.CurrentUsage > s.Budget - reserve / 2;
        if (pressure || target < current)
        {
            healthySamples = 0;
            return Math.Min(target, pressure ? current / 2 : current);
        }
        if (target <= current + MiB * 64) { healthySamples = 0; return current; }
        // Three consecutive healthy samples before each step, so recovery from pressure cannot oscillate.
        if (++healthySamples < 3) return current;
        healthySamples = 0;
        return Math.Min(target, current + Step);
    }

    internal void Reset() => healthySamples = 0;

    // Retained frames per budget: enough for small frames, bounded so eviction scans stay short.
    internal static int EntryLimit(long budget) => (int)Math.Clamp(budget / (4 * MiB), 64, 1024);
}
