using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using NVEncVideoWriterPlugin;

internal static class HostIntegrationChecks
{
    // The parts of this host, described on Windows, equal the 4.56.1.0 baseline that the tool recorded on Linux.
    internal static void CheckContracts(Assembly host, string hostDirectory)
    {
        var clock = Stopwatch.StartNew();
        var parts = HostContracts.Describe(hostDirectory);
        var described = clock.Elapsed;
        var baseline = HostContracts.Baselines.Single(b => b.Version == "4.56.1.0");
        foreach (var (feature, expected) in baseline.Features)
            Check(parts.TryGetValue(feature, out var actual) && HostContracts.Difference(expected, actual) is null,
                $"{feature} differs from the recorded baseline: {(actual is null ? "missing" : HostContracts.Difference(expected, actual))}");
        var evaluation = HostContracts.Evaluate(parts);
        Check(evaluation.Baseline == "4.56.1.0" && evaluation.Problems.Count == 0,
            "The read build did not match its own baseline: " + string.Join("; ", evaluation.Problems.Select(p => p.Key + ": " + p.Value)));

        clock.Restart();
        Check(HostIntegration.TryMatchReadBuild(host, out var features, out var detail), detail);
        var first = clock.Elapsed;
        Check(features is { Basis: "4.56.1.0", Preview: true, SelectionRects: true, WrappedSources: true, RulerBars: true }
            && features.VerifiedDecoders is { Count: 3 }, "Contract features of the read build: " + features);
        clock.Restart();
        Check(HostIntegration.TryMatchReadBuild(host, out var again, out detail) && again.Basis == features.Basis, detail);
        Console.WriteLine($"Host contracts on Windows equal the recorded 4.56.1.0 baseline ({parts.Sum(p => p.Value.Count)} parts, "
            + $"described in {described.TotalSeconds:F1} s; startup check {first.TotalSeconds:F1} s, then {clock.Elapsed.TotalMilliseconds:F0} ms from the verdict file)");
    }

    // --integration (a process of its own): HostIntegration.Install as if this host were a build that was not read.
    internal static void Run(Assembly host)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var export = host.GetType("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", true)!.GetMethod("CreateFileAsync", all)!;
        var update = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!.GetMethods(all)
            .Single(m => m.Name == "Update" && m.GetParameters().Length == 2);
        bool Hooked(MethodBase method, string owner) => Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true;

        var harmony = new Harmony("ymm.tests.integration");
        harmony.Patch(AccessTools.Method(typeof(HostContracts), nameof(HostContracts.EvaluateCached)),
            prefix: new HarmonyMethod(typeof(HostIntegrationChecks), nameof(Mismatch)));
        try
        {
            Check(HostIntegration.Install(host, verified: false, version: string.Empty), HostIntegration.Status);
            Check(!HostIntegration.CacheAvailable && HostIntegration.Status.Contains("未確認の版", StringComparison.Ordinal)
                && HostIntegration.Status.Contains("TimelineSource", StringComparison.Ordinal), HostIntegration.Status);
            Check(Hooked(export, HostIntegration.PatchId), "An unread build did not get the export hook");
            Check(!Hooked(update, HostIntegration.PatchId + ".cache"), "An unread build with other code got the cache");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("Host integration: a build whose code differs from the read builds keeps the protected export, without the cache");

        Check(HostIntegration.Install(host, verified: false, version: string.Empty), HostIntegration.Status);
        Check(HostIntegration.CacheAvailable && HostIntegration.Status.Contains("4.56.1.0 と同じ", StringComparison.Ordinal), HostIntegration.Status);
        Check(HostFeatures.For(host) is { Basis: "4.56.1.0", Preview: true, SelectionRects: true, WrappedSources: true, RulerBars: true },
            "Decided features: " + HostFeatures.For(host));
        Check(Hooked(export, HostIntegration.PatchId) && Hooked(update, HostIntegration.PatchId + ".cache"), "The matched build did not get both hooks");
        Console.WriteLine("Host integration: a build whose code matches 4.56.1.0 gets the cache: " + HostIntegration.Status);
    }

    private static bool Mismatch(ref HostContracts.Evaluation __result)
    {
        __result = new(null, new HashSet<string>(), new SortedDictionary<string, string>
        {
            [HostContracts.Core] = "変更 YukkuriMovieMaker.Player.Video.TimelineSource",
        });
        return false;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
