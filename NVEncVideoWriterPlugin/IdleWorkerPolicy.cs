namespace NVEncVideoWriterPlugin;

// Auto reserves an estimate per additional device; unknown/WARP adapters keep one worker.
internal static class IdleWorkerPolicy
{
    internal const long PerWorkerReserve = 512 * GpuMemoryPolicy.MiB;
    internal static int Count(int requested, int logicalCores, GpuMemorySnapshot? sample, bool busy = false, long measuredWorkerBytes = 0)
    {
        if (busy) return 1;
        if (requested > 0) return Math.Clamp(requested, 1, 4);
        if (sample is not { Software: false, Budget: > 0, CurrentUsage: >= 0 } memory) return 1;
        if (measuredWorkerBytes <= 0) return 1;
        long reserve = Math.Max(PerWorkerReserve, measuredWorkerBytes);
        long available = memory.CurrentUsage >= memory.Budget ? 0 : memory.Budget - memory.CurrentUsage;
        long headroom = available - Math.Min(available, GpuMemoryPolicy.Reserve(memory.Budget));
        return (int)Math.Clamp(Math.Min(Math.Clamp(logicalCores / 2, 1, 4), headroom / reserve), 1, 4);
    }
}

// Completed ordinals may arrive in any order. Only their contiguous prefix moves the resume cursor.
internal sealed class IdleCompletionCursor(long first, long last)
{
    private readonly bool[] completed = new bool[checked((int)(last - first + 1))];
    private int contiguous;
    internal long Next => first + contiguous;
    internal long Complete(long ordinal)
    {
        long index = ordinal - first;
        if (index < 0 || index >= completed.Length) throw new ArgumentOutOfRangeException(nameof(ordinal));
        completed[index] = true;
        while (contiguous < completed.Length && completed[contiguous]) contiguous++;
        return Next;
    }
}
