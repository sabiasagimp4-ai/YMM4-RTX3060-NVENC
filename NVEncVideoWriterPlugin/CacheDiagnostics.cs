using System.IO;

namespace NVEncVideoWriterPlugin;

/// <summary>Opt-in trace control for real YMM4 runs. Does not change cache eligibility.</summary>
public static class CacheDiagnostics
{
    public static bool IsRecording => CacheTrace.Enabled;
    public static string? OutputPath => CacheTrace.OutputPath;
    public static long DroppedRecords => CacheTrace.Dropped;
    private static int environmentChecked;
    internal static void TryStartEnvironment()
    {
        if (Interlocked.Exchange(ref environmentChecked, 1) != 0) return;
        if (Environment.GetEnvironmentVariable("YMM4_CACHE_TRACE") is { Length: > 0 } path)
            Start(path, Environment.GetEnvironmentVariable("YMM4_CACHE_SCENARIO") ?? "automation");
    }
    public static void MarkScenario(string scenario) => CacheTrace.Mark(scenario);

    /// <summary>Creates a new JSONL file. Existing files are never overwritten.</summary>
    public static void Start(string path, string scenario = "manual")
    {
        CacheTrace.Start(path, scenario);
        try { ProcessingTraceHooks.Start(); }
        catch { _ = CacheTrace.StopAsync(); throw; }
    }

    public static void StartDefault(string scenario = "manual") => Start(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4-RTX3060-NVENC", "diagnostics",
        $"cache-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl"), scenario);

    /// <summary>Stops capture, removes processor hooks, and asynchronously drains the writer.</summary>
    public static async Task StopAsync()
    {
        ProcessingTraceHooks.Stop();
        await CacheTrace.StopAsync().ConfigureAwait(false);
    }
}
