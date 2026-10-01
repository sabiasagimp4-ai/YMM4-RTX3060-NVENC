namespace NVEncVideoWriterPlugin;

// Pure policy; the controller samples off the render thread. All sizes are bytes.
internal readonly record struct CacheMemorySnapshot(long TotalPhysical, long AvailablePhysical,
    long ProcessPrivate, long Managed, long GcLimit);

internal sealed class CacheMemoryPolicy
{
    internal const long MiB = 1024 * 1024;
    internal const long InitialBudget = 256 * MiB;
    private int healthySamples;

    internal long Next(long current, long resident, long queued, long maximum, CacheMemorySnapshot? sample)
    {
        current = Math.Min(current, maximum);
        if (sample is not { } s || s.TotalPhysical <= 0 || s.AvailablePhysical < 0
            || s.AvailablePhysical > s.TotalPhysical || s.ProcessPrivate < 0 || s.Managed < 0)
        {
            healthySamples = 0;
            return current; // A failed sample never authorizes growth.
        }
        long reserve = Math.Max(1024 * MiB, s.TotalPhysical / 8);
        // The queue and one maximum-sized read/capture can retain arrays after RAM eviction.
        long transient = Math.Max(0, queued) + FrameCacheStore.MaxFrameBytes;
        long physicalRoom = Math.Max(0, s.AvailablePhysical - reserve - transient);
        long processRoom = Math.Max(0, s.TotalPhysical / 2 - Math.Max(0, s.ProcessPrivate - resident) - transient);
        long managedRoom = s.GcLimit > 0
            ? Math.Max(0, s.GcLimit - Math.Max(0, s.Managed - resident) - transient) : long.MaxValue;
        long target = Math.Min(maximum, Math.Min(s.TotalPhysical / 4,
            Math.Min(resident + physicalRoom, Math.Min(processRoom, managedRoom))));
        bool pressure = s.AvailablePhysical < reserve + transient || processRoom < resident || managedRoom < resident;
        if (pressure || target < current)
        {
            healthySamples = 0;
            return Math.Max(0, Math.Min(target, pressure ? current / 2 : current));
        }
        if (target <= current + 64 * MiB) { healthySamples = 0; return current; }
        // Three consecutive healthy samples before each growth step, so pressure recovery cannot oscillate.
        if (++healthySamples < 3) return current;
        healthySamples = 0;
        return Math.Min(target, current + 128 * MiB);
    }

    internal void Reset() => healthySamples = 0;
}
