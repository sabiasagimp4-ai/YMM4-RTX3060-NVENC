using Vortice.DXGI;

namespace NVEncVideoWriterPlugin;

// Sizes the GPU frame retention from the video memory of the adapter YMM4 renders with (any GPU: NVIDIA, AMD,
// Intel). Inactive until PluginSettings configures it, so tests that set the budget directly are not overridden.
// Lock order: TimelineFrameCache's cacheGate may be held when ObserveAdapter takes this gate, so code under this gate
// reads and writes the cache's budget only through its lock-free members.
internal static class GpuMemoryController
{
    private static readonly object gate = new();
    private static readonly GpuMemoryPolicy policy = new();
    private static Timer? timer;
    private static bool configured, automatic = true;
    private static long maximum = 2048 * GpuMemoryPolicy.MiB;
    private static Adapter? adapter;
    private static string status = "VRAMの自動配分は、プレビューの描画が始まると動きます。";
    private sealed record SampleState(GpuMemorySnapshot Value);
    private static SampleState? latestSample;
    private static long adapterGeneration;
    internal static long AdapterGeneration => Interlocked.Read(ref adapterGeneration);
    internal static GpuMemorySnapshot? LatestSample => Volatile.Read(ref latestSample)?.Value;

    private sealed record Adapter(Vortice.Luid Luid, string Name, long DedicatedVideoMemory, bool Software);

    internal static string Status => Volatile.Read(ref status);
    internal static long Maximum { get { lock (gate) return maximum; } }

    internal static void Configure(bool allocateAutomatically, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        lock (gate)
        {
            if (configured && automatic == allocateAutomatically && maximum == maximumBytes) return;
            configured = true;
            Volatile.Write(ref latestSample, null);
            automatic = allocateAutomatically;
            maximum = maximumBytes;
            policy.Reset();
            if (!automatic)
            {
                TimelineFrameCache.SetGpuRetentionBudgetDeferred(maximum);
                Volatile.Write(ref status, $"VRAM（GPU保持）の上限は手動設定です（{maximum / GpuMemoryPolicy.MiB:N0} MiB）。");
            }
            else
            {
                TimelineFrameCache.SetGpuRetentionBudgetDeferred(Math.Min(TimelineFrameCache.GpuRetentionBudgetNow, maximum));
                StartIfReady();
            }
        }
    }

    // From the render path, once per device context: the adapter the preview draws with. Never throws: it runs while
    // the cache key is composed, where an exception would bypass the cache.
    internal static void ObserveAdapter(IDXGIAdapter observed)
    {
        try
        {
            var description = observed.Description;
            bool software = description.VendorId == 0x1414 && description.DeviceId == 0x8C; // Microsoft Basic Render Driver
            using (var adapter1 = observed.QueryInterfaceOrNull<IDXGIAdapter1>())
                if (adapter1 is not null) software |= (adapter1.Description1.Flags & AdapterFlags.Software) != 0;
            var seen = new Adapter(description.Luid, description.Description, (long)description.DedicatedVideoMemory, software);
            lock (gate)
            {
                if (adapter is { } known && known.Luid.LowPart == seen.Luid.LowPart && known.Luid.HighPart == seen.Luid.HighPart) return;
                adapter = seen;
                Interlocked.Increment(ref adapterGeneration);
                IdleFramePreRenderer.ResetWorkerMemory();
                Volatile.Write(ref latestSample, null);
                policy.Reset();
                StartIfReady();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Volatile.Write(ref status, "描画に使うGPUを特定できないため、VRAMの自動配分を保留しています: " + error.GetBaseException().Message);
        }
    }

    private static void StartIfReady()
    {
        if (configured && automatic && adapter is not null)
            timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    // A timer callback never queues another sample behind a slow previous sample. It only changes the budget:
    // frames over it are released on the render thread (TimelineFrameCache trims at the next Update).
    private static void Sample()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (!configured || !automatic || adapter is not { } current) return;
            var snapshot = ReadSnapshot(current);
            Volatile.Write(ref latestSample, snapshot is { } seen ? new SampleState(seen) : null);
            long budget = TimelineFrameCache.GpuRetentionBudgetNow;
            long next = policy.Next(budget, TimelineFrameCache.GpuRetainedBytesNow, maximum, snapshot);
            if (next != budget) TimelineFrameCache.SetGpuRetentionBudgetDeferred(next);
            Volatile.Write(ref status, snapshot switch
            {
                null => $"VRAMの使用量を取得できないため、GPU保持を {next / GpuMemoryPolicy.MiB:N0} MiB のまま保っています（{current.Name}）。",
                { Software: true } => $"ソフトウェア描画（{current.Name}）のため、GPU保持は {next / GpuMemoryPolicy.MiB:N0} MiB に固定しています。",
                { } s => $"VRAMを自動配分しています（{current.Name}：予算 {s.Budget / GpuMemoryPolicy.MiB:N0} MiB、YMM4の使用 {s.CurrentUsage / GpuMemoryPolicy.MiB:N0} MiB、GPU保持の上限 {next / GpuMemoryPolicy.MiB:N0} MiB）。",
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            policy.Reset();
            Volatile.Write(ref latestSample, null);
            Volatile.Write(ref status, "VRAMの自動配分を保留しています: " + error.GetBaseException().Message);
        }
        finally { Monitor.Exit(gate); }
    }

    private static IDXGIFactory4? factory;

    // Null when the adapter is gone or does not report video memory (IDXGIAdapter3 needs Windows 10).
    private static GpuMemorySnapshot? ReadSnapshot(Adapter current)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            factory ??= DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using var found = factory.EnumAdapterByLuid<IDXGIAdapter3>(current.Luid);
            var info = found.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            if (info.Budget > long.MaxValue || info.CurrentUsage > long.MaxValue) return null;
            return new((long)info.Budget, (long)info.CurrentUsage, current.DedicatedVideoMemory, current.Software);
        }
        catch (SharpGen.Runtime.SharpGenException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    // For the host probe: the adapter seen by the render path and one sample of it, without changing any budget.
    internal static (string Name, GpuMemorySnapshot? Sample)? Probe()
    {
        lock (gate) return adapter is { } current ? (current.Name, ReadSnapshot(current)) : null;
    }
}
