using NVEncVideoWriterPlugin;

// HostFingerprint on this assembly's own sample types, and the HostContracts verdict on made-up part lists.
internal static class HostContractChecks
{
    internal static void Run()
    {
        Check(HostFingerprint.Normalize("<Update>b__12_0") == "<Update>b__#_#"
            && HostFingerprint.Normalize("<<M>g__Local|3_1>d") == "<<M>g__Local|#_#>d"
            && HostFingerprint.Normalize("Vector2") == "Vector2", "Compiler-generated ordinals must be normalized, other names kept");

        string path = typeof(HostContractChecks).Assembly.Location;
        string a = typeof(FingerprintSamples.A).FullName!, b = typeof(FingerprintSamples.B).FullName!, c = typeof(FingerprintSamples.C).FullName!;
        using (var first = new HostFingerprint(path))
        using (var second = new HostFingerprint(path))
        {
            Check(first.Hash(a) is { } hash && hash == second.Hash(a), "A type's fingerprint must be deterministic");
            Check(first.Hash("No.Such.Type") is null, "A missing type must have no fingerprint");
            var membersA = first.MemberHashes(a);
            var membersB = first.MemberHashes(b);
            var membersC = first.MemberHashes(c);
            string add = membersA.Keys.Single(k => k.Contains(" Add(", StringComparison.Ordinal));
            Check(membersA[add] == membersB[add], "Identical method bodies must have identical fingerprints in any type");
            Check(membersA[add] != membersC[add], "A changed constant must change the method's fingerprint");
            Check(first.Hash(a) != first.Hash(b), "The type name is part of the type's fingerprint");
            string branch = membersA.Keys.Single(k => k.Contains(" Clamp(", StringComparison.Ordinal));
            Check(membersA[branch] == membersB[branch] && membersA[branch] != membersC[branch], "Identical bodies with branches must compare equal, changed ones not");
            Check(membersA.Keys.Any(k => k.StartsWith("<>c/", StringComparison.Ordinal)), "Lambdas must be described with their declaring type");
            var references = first.References(a)!;
            Check(references.Contains("System.Math::Max") && references.Contains(a + "::Add")
                && references.Contains("System.Func`2"), "References must name called members, own methods and used types");
            Check(!first.References(c)!.Contains("System.Math::Max"), "References must only name what the code uses");
        }

        // Verdicts on made-up part lists: every rule's feature with one part.
        var baseline = Parts();
        var all = HostContracts.Rules.Select(r => r.Feature).ToHashSet();
        var verdict = HostContracts.Evaluate(Parts(), [new("1.0", Frozen(baseline))]);
        Check(verdict.Baseline == "1.0" && verdict.Features.SetEquals(all) && verdict.Problems.Count == 0, "Unchanged parts must keep every feature");

        string decoder = HostContracts.DecoderPrefix + "YukkuriMovieMaker.Plugin.FileSource.FFmpeg";
        var changedDecoder = Parts();
        changedDecoder[decoder]["X|Decoder"] = "changed";
        verdict = HostContracts.Evaluate(changedDecoder, [new("1.0", Frozen(baseline))]);
        Check(verdict.Baseline == "1.0" && !verdict.Has(decoder) && verdict.Features.Count == all.Count - 1
            && verdict.Problems[decoder].Contains("変更 Decoder", StringComparison.Ordinal), "A changed decoder must only turn its own feature off");

        var changedPreview = Parts();
        changedPreview[HostContracts.Preview]["X|Player"] = "changed";
        verdict = HostContracts.Evaluate(changedPreview, [new("1.0", Frozen(baseline))]);
        Check(verdict.Has(HostContracts.Core) && !verdict.Has(HostContracts.Preview) && !verdict.Has(HostContracts.SelectionRects)
            && verdict.Has(HostContracts.RulerBars) && verdict.Problems[HostContracts.SelectionRects].Contains("preview", StringComparison.Ordinal),
            "A feature must be off when a feature it requires is off");

        var newWitness = Parts();
        newWitness[HostContracts.Core]["X|NewSceneReader"] = "new";
        verdict = HostContracts.Evaluate(newWitness, [new("1.0", Frozen(baseline))]);
        Check(verdict.Baseline is null && verdict.Features.Count == 0 && verdict.Problems[HostContracts.Core].Contains("追加 NewSceneReader", StringComparison.Ordinal),
            "A new witness must turn the core (and with it everything) off");

        var missing = Parts();
        missing[HostContracts.Core]["X|Core"] = HostContracts.Missing;
        verdict = HostContracts.Evaluate(missing, [new("1.0", Frozen(baseline))]);
        Check(verdict.Baseline is null && verdict.Problems[HostContracts.Core].Contains("削除 Core", StringComparison.Ordinal), "A missing part must turn its feature off");

        var older = Parts();
        older[HostContracts.Core]["X|Core"] = "older";
        verdict = HostContracts.Evaluate(Parts(), [new("2.0", Frozen(older)), new("1.0", Frozen(baseline))]);
        Check(verdict.Baseline == "1.0" && verdict.Features.SetEquals(all), "The first read build whose core matches must be used");
        verdict = HostContracts.Evaluate(Parts(), [new("2.0", Frozen(older))]);
        Check(verdict.Baseline is null && verdict.Features.Count == 0, "No read build with the same core must mean no cache");

        var partial = Frozen(baseline).ToDictionary(p => p.Key, p => p.Value);
        partial.Remove(HostContracts.RulerBars);
        verdict = HostContracts.Evaluate(Parts(), [new("1.0", partial)]);
        Check(!verdict.Has(HostContracts.RulerBars) && verdict.Has(HostContracts.Core), "A feature the read build has no record of must be off");

        Check(HostContracts.Difference(new Dictionary<string, string> { ["A|T"] = "1" }, new Dictionary<string, string> { ["A|T"] = "1" }) is null,
            "Equal parts must have no difference");
        Console.WriteLine($"Host contracts: fingerprints (determinism, bodies, branches, lambdas, references), verdicts ({HostContracts.Rules.Length} rules) OK");
    }

    private static Dictionary<string, SortedDictionary<string, string>> Parts() =>
        HostContracts.Rules.ToDictionary(rule => rule.Feature, rule => new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [rule.Feature == HostContracts.Core ? "X|Core" : rule.Feature == HostContracts.Preview ? "X|Player"
                : rule.Feature.StartsWith(HostContracts.DecoderPrefix, StringComparison.Ordinal) ? "X|Decoder" : "X|" + rule.Feature] = "1",
        });

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Frozen(Dictionary<string, SortedDictionary<string, string>> parts) =>
        parts.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Host contracts: " + message);
    }
}

internal static class FingerprintSamples
{
    internal sealed class A
    {
        internal int Add(int x) => x + 100;
        internal int Clamp(int x) => x < 0 ? 0 : Math.Max(x, 10);
        internal Func<int, int> Twice() => y => y * 2;
    }

    internal sealed class B
    {
        internal int Add(int x) => x + 100;
        internal int Clamp(int x) => x < 0 ? 0 : Math.Max(x, 10);
    }

    internal sealed class C
    {
        internal int Add(int x) => x + 101; // the same opcode (ldc.i4.s), another operand
        internal int Clamp(int x) => x < 0 ? 1 : x;
    }
}
