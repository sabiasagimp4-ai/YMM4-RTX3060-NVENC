using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NVEncVideoWriterPlugin;

internal static class CacheMemoryController
{
    private static readonly object gate = new();
    private static readonly CacheMemoryPolicy policy = new();
    private static FrameCacheStore? target;
    private static Timer? timer;
    private static bool automatic = true;
    private static long maximum = 2048 * CacheMemoryPolicy.MiB;
    private static string status = "RAMの自動配分を待っています。";

    internal static string Status => Volatile.Read(ref status);
    internal static long Maximum { get { lock (gate) return maximum; } }

    internal static FrameCacheStore CreateStore(string path)
    {
        lock (gate)
        {
            var store = new FrameCacheStore(path, automatic ? Math.Min(maximum, CacheMemoryPolicy.InitialBudget) : maximum);
            target = store;
            timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            return store;
        }
    }

    internal static void Configure(bool allocateAutomatically, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        lock (gate)
        {
            if (automatic == allocateAutomatically && maximum == maximumBytes) return;
            automatic = allocateAutomatically;
            maximum = maximumBytes;
            policy.Reset();
            if (target is { } store)
                store.SetRamBudget(automatic ? Math.Min(store.RamBudget, maximum) : maximum);
        }
    }

    // A timer callback never queues another sample behind a slow previous sample.
    private static void Sample()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (target is not { } store) return;
            if (!automatic) { Volatile.Write(ref status, "RAM上限は手動設定です。"); return; }
            var snapshot = ReadSnapshot();
            store.SetRamBudget(policy.Next(store.RamBudget, store.RamBytes, store.QueuedWriteBytes, maximum, snapshot));
            Volatile.Write(ref status, snapshot is null ? "メモリ使用量を取得できないため、RAM上限の拡張を保留しています。"
                : $"RAMを自動配分しています（空き物理メモリ {snapshot.Value.AvailablePhysical / CacheMemoryPolicy.MiB:N0} MiB）。");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            policy.Reset();
            Volatile.Write(ref status, "RAMの自動配分を保留しています: " + error.GetBaseException().Message);
        }
        finally { Monitor.Exit(gate); }
    }

    private static CacheMemorySnapshot? ReadSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhysical > long.MaxValue || memory.AvailablePhysical > long.MaxValue) return null;
        var gc = GC.GetGCMemoryInfo();
        return new((long)memory.TotalPhysical, (long)memory.AvailablePhysical, PrivateBytes(),
            Math.Max(GC.GetTotalMemory(false), gc.HeapSizeBytes), gc.TotalAvailableMemoryBytes);
    }

    // This process's commit charge. Process.PrivateMemorySize64 reads a snapshot of every process and thread on the
    // system each time; GetProcessMemoryInfo reads only this process.
    private static long PrivateBytes()
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)) return (long)counters.PrivateUsage;
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    // PROCESS_MEMORY_COUNTERS_EX
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
