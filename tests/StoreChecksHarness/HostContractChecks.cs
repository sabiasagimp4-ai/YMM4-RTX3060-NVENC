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

        var changedSimple = Parts();
        changedSimple[HostContracts.SimpleTachie]["X|simple-tachie"] = "changed";
        verdict = HostContracts.Evaluate(changedSimple, [new("1.0", Frozen(baseline))]);
        Check(!verdict.Has(HostContracts.SimpleTachie) && verdict.Has(HostContracts.Core) && verdict.Has(HostContracts.Preview),
            "Changed simple tachie code must disable its feature while preserving ordinary caching");
        var changedWrapper = Parts();
        changedWrapper[HostContracts.WrappedSources]["X|wrapped-sources"] = "changed";
        verdict = HostContracts.Evaluate(changedWrapper, [new("1.0", Frozen(baseline))]);
        Check(!verdict.Has(HostContracts.SimpleTachie) && verdict.Has(HostContracts.Core), "Simple tachie must require verified source wrappers");

        var changedLipSync = Parts();
        changedLipSync[HostContracts.LipSync]["X|lip-sync-readiness"] = "changed";
        verdict = HostContracts.Evaluate(changedLipSync, [new("1.0", Frozen(baseline))]);
        Check(!verdict.Has(HostContracts.LipSync) && !verdict.Has(HostContracts.AnimationTachie) && !verdict.Has(HostContracts.PsdTachie)
            && verdict.Has(HostContracts.SimpleTachie) && verdict.Has(HostContracts.Core),
            "Changed envelope code must disable advanced tachie while preserving simple and ordinary caching");
        var changedAnimation = Parts();
        changedAnimation[HostContracts.AnimationTachie]["X|animation-tachie"] = "changed";
        verdict = HostContracts.Evaluate(changedAnimation, [new("1.0", Frozen(baseline))]);
        Check(!verdict.Has(HostContracts.AnimationTachie) && verdict.Has(HostContracts.LipSync),
            "Changed animation assembly must disable animation admission");

        var changedPsd = Parts();
        changedPsd[HostContracts.PsdTachie]["X|psd-tachie"] = "changed";
        verdict = HostContracts.Evaluate(changedPsd, [new("1.0", Frozen(baseline))]);
        Check(!verdict.Has(HostContracts.PsdTachie) && verdict.Has(HostContracts.LipSync) && verdict.Has(HostContracts.AnimationTachie),
            "PSD changes must disable only PSD, retaining verified lip sync and PNG animation");

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

        // Reviewed builds: only the features recorded for them, with exactly the recorded code.
        Check(HostContracts.Digest(new Dictionary<string, string> { ["A|T"] = "1", ["B|U"] = "2" })
            == HostContracts.Digest(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["B|U"] = "2", ["A|T"] = "1" })
            && HostContracts.Digest(new Dictionary<string, string> { ["A|T"] = "1" }) != HostContracts.Digest(new Dictionary<string, string> { ["A|T"] = "2" }),
            "A digest must depend on the parts only, not on their order");
        HostContracts.ReviewedBuild Reviewed(string versions, params string[] features) =>
            new(versions, features.ToDictionary(feature => feature, feature => HostContracts.Digest(baseline[feature])));
        var otherCore = Parts();
        otherCore[HostContracts.Core]["X|Core"] = "other";
        verdict = HostContracts.Evaluate(Parts(), [new("2.0", Frozen(otherCore))],
            [Reviewed("0.9", HostContracts.Core, HostContracts.Preview, decoder)]);
        Check(verdict.Baseline == "0.9" && verdict.Features.SetEquals([HostContracts.Core, HostContracts.Preview, decoder])
            && verdict.Problems[HostContracts.RulerBars].Contains("確かめていない", StringComparison.Ordinal),
            "A reviewed build must enable only the features recorded for it");
        verdict = HostContracts.Evaluate(changedPreview, [new("2.0", Frozen(otherCore))], [Reviewed("0.9", HostContracts.Core, HostContracts.Preview)]);
        Check(verdict.Baseline == "0.9" && verdict.Has(HostContracts.Core) && !verdict.Has(HostContracts.Preview)
            && verdict.Problems[HostContracts.Preview].Contains("確かめたコードと異なります", StringComparison.Ordinal),
            "Code that differs from what was reviewed must turn the feature off");
        verdict = HostContracts.Evaluate(newWitness, [new("2.0", Frozen(otherCore))], [Reviewed("0.9", HostContracts.Core)]);
        Check(verdict.Baseline is null && verdict.Features.Count == 0, "A reviewed build whose core differs must not enable anything");
        verdict = HostContracts.Evaluate(Parts(), [new("2.0", Frozen(otherCore))],
            [Reviewed("0.9", HostContracts.Core), Reviewed("0.8", HostContracts.Core, HostContracts.RulerBars), Reviewed("0.7", HostContracts.Core)]);
        Check(verdict.Baseline == "0.8" && verdict.Has(HostContracts.RulerBars), "The matching build that enables the most features must be used");
        verdict = HostContracts.Evaluate(Parts(), [new("1.0", Frozen(baseline))], [Reviewed("0.9", [.. all])]);
        Check(verdict.Baseline == "1.0" && verdict.Features.SetEquals(all), "A read build must win over an equal reviewed one");
        var readWithoutRuler = Frozen(baseline).ToDictionary(p => p.Key, p => p.Value);
        readWithoutRuler.Remove(HostContracts.RulerBars);
        verdict = HostContracts.Evaluate(changedPreview, [new("1.0", readWithoutRuler)], [Reviewed("0.9", HostContracts.Core, HostContracts.RulerBars)]);
        Check(verdict.Baseline == "1.0 / 0.9" && verdict.Has(HostContracts.RulerBars) && verdict.Has(HostContracts.WrappedSources)
            && !verdict.Has(HostContracts.Preview) && !verdict.Problems.ContainsKey(HostContracts.RulerBars) && verdict.Problems.ContainsKey(HostContracts.Preview),
            "Builds with the same core must add up their features");

        // The records themselves: known features, each with the features it requires, and the core always.
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var build in HostContracts.Reviewed)
        {
            Check(labels.Add(build.Versions) && build.Digests.ContainsKey(HostContracts.Core), $"{build.Versions}: a record needs a unique label and the core");
            foreach (var feature in build.Digests.Keys)
            {
                var rule = HostContracts.Rules.SingleOrDefault(rule => rule.Feature == feature);
                Check(rule is not null && rule.Requires.All(build.Digests.ContainsKey), $"{build.Versions}: {feature} is unknown or lacks a feature it requires");
            }
        }
        Console.WriteLine($"Host contracts: fingerprints (determinism, bodies, branches, lambdas, references), verdicts ({HostContracts.Rules.Length} rules, {HostContracts.Reviewed.Length} reviewed records) OK");
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
