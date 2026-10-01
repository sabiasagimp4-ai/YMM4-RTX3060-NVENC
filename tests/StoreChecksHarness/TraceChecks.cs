using System.Diagnostics;
using System.Text.Json;
using NVEncVideoWriterPlugin;

internal static class TraceChecks
{
    internal static void Run(string root)
    {
        Check(!CacheTrace.Enabled && CacheTrace.Measure("off") is null, "disabled path");
        string path = Path.Combine(root, "trace.jsonl");
        CacheTrace.Start(path, "portable", 16384);
        long operation;
        using (var outer = CacheTrace.Measure("update", frameTimeTicks: 123, usage: "Paused"))
        {
            operation = outer!.OperationId;
            using var nested = CacheTrace.Measure("lookup");
            nested!.Outcome = "disk";
            CacheTrace.Timing("test-timer", Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp());
        }
        Task.Run(() => { using var io = CacheTrace.Measure("disk-read", "io-wall", operation: operation); }).GetAwaiter().GetResult();
        Parallel.For(0, 1000, i => { using var scope = CacheTrace.Measure("parallel", frameTimeTicks: i); });
        var first = CacheTrace.Measure("out-of-order-parent");
        var second = CacheTrace.Measure("out-of-order-child");
        first!.Dispose(); second!.Dispose();
        using (var following = CacheTrace.Measure("after-out-of-order")) { }
        CacheTrace.StopAsync().GetAwaiter().GetResult();
        var rows = File.ReadLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
        var spans = rows.Where(r => r.RootElement.GetProperty("Kind").GetString() == "span").Select(r => r.RootElement).ToArray();
        Check(rows[0].RootElement.GetProperty("StopwatchFrequency").GetInt64() == Stopwatch.Frequency, "frequency recorded");
        var update = spans.Single(s => s.GetProperty("Stage").GetString() == "update");
        var lookup = spans.Single(s => s.GetProperty("Stage").GetString() == "lookup");
        Check(lookup.GetProperty("ParentId").GetInt64() == update.GetProperty("Id").GetInt64()
            && lookup.GetProperty("OperationId").GetInt64() == operation && lookup.GetProperty("FrameTimeTicks").GetInt64() == 123,
            "nested attribution");
        Check(spans.Single(s => s.GetProperty("Stage").GetString() == "disk-read").GetProperty("OperationId").GetInt64() == operation, "worker correlation");
        Check(spans.Single(s => s.GetProperty("Stage").GetString() == "after-out-of-order").GetProperty("ParentId").GetInt64() == 0,
            "out-of-order Harmony finalizers cannot leak the prior frame context");
        Check(spans.All(s => s.GetProperty("EndTicks").GetInt64() >= s.GetProperty("StartTicks").GetInt64()), "monotonic durations");
        Check(spans.Select(s => s.GetProperty("Id").GetInt64()).Distinct().Count() == spans.Length, "unique ids");
        var summary = rows[^1].RootElement;
        Check(summary.GetProperty("Dropped").GetInt64() == 0 && summary.GetProperty("OpenSpans").GetInt64() == 0
            && summary.GetProperty("Written").GetInt64() == spans.Length, "complete footer");
        foreach (var row in rows) row.Dispose();
        CacheTrace.Start(Path.Combine(root, "overload.jsonl"), "overload", 1);
        Parallel.For(0, 10000, _ => { using var span = CacheTrace.Measure("overflow"); });
        CacheTrace.StopAsync().GetAwaiter().GetResult();
        using var overflow = JsonDocument.Parse(File.ReadLines(Path.Combine(root, "overload.jsonl")).Last());
        Check(overflow.RootElement.GetProperty("Dropped").GetInt64() > 0, "bounded overflow counted");
        CacheTrace.Start(path, "existing-file");
        bool failed = false;
        try { CacheTrace.StopAsync().GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
        Check(failed && File.ReadLines(path).First().Contains("portable"), "I/O errors surface without overwriting");
        Console.WriteLine("Trace: disabled path, nested attribution, worker correlation, concurrent ids, exact ticks, bounded drops and I/O failure passed.");
    }
    private static void Check(bool value, string why) { if (!value) throw new Exception("Trace: " + why); }
}
