using System.Diagnostics;

namespace NVEncVideoWriterPlugin;

internal enum PreviewStage
{
    KeyGeneration, CacheLookup, HostRender, BeginGpuCopy, CpuAllocation,
    MapWait, CpuMemcpy, RamCommit, DiskEnqueue, TotalUpdate, PreviewDraw, TotalPreview, CacheRead, CacheRestore,
}

internal readonly record struct PreviewPerformanceRow(PreviewStage Stage, long SampleCount,
    double MeanMilliseconds, double P50Milliseconds, double P95Milliseconds, long TotalTicks);

internal sealed class FrameTimeSamples
{
    private readonly long[] samples = new long[1024];
    private int count, next;
    private long totalCount, totalTicks;

    internal void Add(long ticks)
    {
        lock (samples)
        {
            ticks = Math.Max(0, ticks);
            samples[next] = ticks;
            next = (next + 1) % samples.Length;
            count = Math.Min(count + 1, samples.Length);
            totalCount++;
            totalTicks += ticks;
        }
    }

    internal void Reset()
    {
        lock (samples) { count = next = 0; totalCount = totalTicks = 0; }
    }

    internal PreviewPerformanceRow Snapshot(PreviewStage stage)
    {
        long[] sorted;
        long n, ticks;
        lock (samples) { sorted = samples[..count]; n = totalCount; ticks = totalTicks; }
        Array.Sort(sorted);
        double Ms(long value) => value * 1000.0 / Stopwatch.Frequency;
        double Percentile(int percent) => sorted.Length == 0 ? 0 : Ms(sorted[(sorted.Length - 1) * percent / 100]);
        return new(stage, n, n == 0 ? 0 : Ms(ticks) / n, Percentile(50), Percentile(95), ticks);
    }

    internal (int Count, double P50, double P95) Summary()
    {
        var row = Snapshot(default);
        return ((int)Math.Min(1024, row.SampleCount), row.P50Milliseconds, row.P95Milliseconds);
    }

    public override string ToString()
    {
        var (n, p50, p95) = Summary();
        return n == 0 ? "-" : $"{p50:F1}/{p95:F1} ms (n={n})";
    }
}

internal static class PreviewPerformance
{
    private static readonly FrameTimeSamples[] samples = Enum.GetValues<PreviewStage>().Select(_ => new FrameTimeSamples()).ToArray();
    internal static bool RecordingEnabled { get; set; } = true;
    internal static long Timestamp => Stopwatch.GetTimestamp();
    internal static Measurement Measure(PreviewStage stage) => new(stage);
    internal readonly struct Measurement(PreviewStage stage) : IDisposable
    {
        private readonly long started = Timestamp;
        private readonly CacheTrace.Span? trace = CacheTrace.Measure(stage.ToString());
        public void Dispose()
        {
            long end = Timestamp;
            trace?.Dispose();
            if (started != 0) Add(stage, end - started);
        }
    }
    internal static void Add(PreviewStage stage, long ticks)
    {
        if (RecordingEnabled) samples[(int)stage].Add(ticks);
    }
    internal static void End(PreviewStage stage, long started)
    {
        long end = Timestamp;
        CacheTrace.Timing(stage.ToString(), started, end);
        Add(stage, end - started);
    }
    internal static void Reset() { foreach (var sample in samples) sample.Reset(); }
    internal static PreviewPerformanceRow[] Snapshot() => Enum.GetValues<PreviewStage>().Select(stage => samples[(int)stage].Snapshot(stage)).ToArray();
}

