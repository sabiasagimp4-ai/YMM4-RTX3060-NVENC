using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using NVEncVideoWriterPlugin;

internal static class ProcessingTraceChecks
{
    internal static void Run()
    {
        var target = typeof(DiagnosticProcessor).GetMethod(nameof(DiagnosticProcessor.Update))!;
        if (!ProcessingTraceHooks.Targets(typeof(DiagnosticProcessor)).Contains(target)) throw new Exception("Dynamic processor discovery failed");
        string path = Path.Combine(Path.GetTempPath(), "ymm-processor-trace-" + Guid.NewGuid().ToString("N") + ".jsonl");
        CacheTrace.Start(path, "observer-check");
        ProcessingTraceHooks.Start();
        try
        {
            ProcessingTraceHooks.Discover();
            var processor = new DiagnosticProcessor();
            if (processor.Update(false) != 7) throw new Exception("Observer changed the result");
            bool originalError = false;
            try { processor.Update(true); } catch (InvalidOperationException) { originalError = true; }
            if (!originalError) throw new Exception("Observer suppressed the original exception");
        }
        finally { ProcessingTraceHooks.Stop(); CacheTrace.StopAsync().GetAwaiter().GetResult(); }
        string log = File.ReadAllText(path);
        if (!log.Contains("DiagnosticProcessor") || !log.Contains("\"Outcome\":\"exception\"")) throw new Exception("Processor calls were not traced");
        File.Delete(path);
        Console.WriteLine("Dynamic processor trace: interface discovery, real detour, return value, exception and unpatch passed");
    }
}

internal sealed class DiagnosticProcessor : YukkuriMovieMaker.TraceTests.IDiagnosticProcessor
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Update(bool fail) => fail ? throw new InvalidOperationException("expected observer test error") : 7;
}

namespace YukkuriMovieMaker.TraceTests
{
    internal interface IDiagnosticProcessor { int Update(bool fail); }
}
