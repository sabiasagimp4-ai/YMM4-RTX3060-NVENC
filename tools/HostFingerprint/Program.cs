using NVEncVideoWriterPlugin;

// Build-independent fingerprints of YMM4's own assemblies (HostFingerprint.cs), for comparing YMM4 versions.
// Reads the files only; nothing is loaded or run. The output is hashes and type/member names, not code.
//   dotnet run --project tools/HostFingerprint -- report <YMM4 dir> > 4.56.1.0.tsv
//   dotnet run --project tools/HostFingerprint -- diff <old.tsv> <new.tsv>
//   dotnet run --project tools/HostFingerprint -- members <old YMM4 dir> <new YMM4 dir> <type full name>...
//   dotnet run --project tools/HostFingerprint -- contracts <YMM4 dir>              (HostContracts parts and the verdict)
//   dotnet run --project tools/HostFingerprint -- compare <read YMM4 dir> <new YMM4 dir>  (the verdict if the first were read)
//   dotnet run --project tools/HostFingerprint -- emit <out.cs> <version>=<YMM4 dir>... (HostBaselines.cs, newest first)
switch (args)
{
    case ["contracts", var directory]:
        Contracts(directory);
        return 0;
    case ["compare", var before, var after]:
        Compare(before, after);
        return 0;
    case ["emit", var output, .. var builds] when builds.Length > 0:
        Emit(output, builds);
        return 0;
    case ["report", var directory]:
        Report(directory);
        return 0;
    case ["diff", var before, var after]:
        Diff(Read(before), Read(after));
        return 0;
    case ["members", var before, var after, .. var typeNames]:
        foreach (var typeName in typeNames) Members(before, after, typeName);
        return 0;
    default:
        Console.Error.WriteLine("Usage: report <dir> | diff <old.tsv> <new.tsv> | members <old dir> <new dir> <type>... | contracts <dir> | emit <out.cs> <version>=<dir>...");
        return 2;
}

static IEnumerable<string> HostAssemblies(string directory) =>
    Directory.GetFiles(directory, "YukkuriMovieMaker*.dll").Order(StringComparer.OrdinalIgnoreCase);

static void Report(string directory)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    int count = 0;
    foreach (var path in HostAssemblies(directory))
    {
        HostFingerprint fingerprint;
        try { fingerprint = new HostFingerprint(path); }
        catch (BadImageFormatException) { continue; }
        using (fingerprint)
        {
            Console.WriteLine($"#\t{fingerprint.AssemblyName}\t{fingerprint.Mvid:D}");
            foreach (var type in fingerprint.TypeNames.Order(StringComparer.Ordinal))
            {
                Console.WriteLine($"{fingerprint.AssemblyName}\t{type}\t{fingerprint.Hash(type)}");
                count++;
            }
        }
    }
    Console.Error.WriteLine($"{count} types in {clock.Elapsed.TotalSeconds:F1} s");
}

static Dictionary<(string Assembly, string Type), string> Read(string path) =>
    File.ReadLines(path).Where(line => !line.StartsWith('#') && line.Length > 0).Select(line => line.Split('\t'))
        .ToDictionary(parts => (parts[0], parts[1]), parts => parts[2]);

static void Diff(Dictionary<(string Assembly, string Type), string> before, Dictionary<(string Assembly, string Type), string> after)
{
    foreach (var assembly in before.Keys.Concat(after.Keys).Select(key => key.Assembly).Distinct().Order(StringComparer.Ordinal))
    {
        var old = before.Where(pair => pair.Key.Assembly == assembly).ToDictionary(pair => pair.Key.Type, pair => pair.Value);
        var current = after.Where(pair => pair.Key.Assembly == assembly).ToDictionary(pair => pair.Key.Type, pair => pair.Value);
        var changed = old.Keys.Intersect(current.Keys).Where(type => old[type] != current[type]).Order(StringComparer.Ordinal).ToArray();
        var removed = old.Keys.Except(current.Keys).Order(StringComparer.Ordinal).ToArray();
        var added = current.Keys.Except(old.Keys).Order(StringComparer.Ordinal).ToArray();
        int same = old.Keys.Intersect(current.Keys).Count() - changed.Length;
        Console.WriteLine($"## {assembly}: {same} same, {changed.Length} changed, {added.Length} added, {removed.Length} removed");
        foreach (var type in changed) Console.WriteLine($"  ~ {type}");
        foreach (var type in added) Console.WriteLine($"  + {type}");
        foreach (var type in removed) Console.WriteLine($"  - {type}");
    }
}

static void Members(string before, string after, string typeName)
{
    SortedDictionary<string, string> Load(string directory)
    {
        foreach (var path in HostAssemblies(directory))
        {
            using var fingerprint = new HostFingerprint(path);
            if (fingerprint.Contains(typeName)) return fingerprint.MemberHashes(typeName);
        }
        return [];
    }
    var old = Load(before);
    var current = Load(after);
    Console.WriteLine($"## {typeName}");
    foreach (var member in old.Keys.Union(current.Keys).Order(StringComparer.Ordinal))
    {
        bool inOld = old.TryGetValue(member, out var a), inNew = current.TryGetValue(member, out var b);
        if (inOld && inNew && a == b) continue;
        Console.WriteLine($"  {(inOld && inNew ? "~" : inNew ? "+" : "-")} {member}");
    }
}

static void Contracts(string directory)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var parts = HostContracts.Describe(directory);
    Console.WriteLine($"# parts described in {clock.Elapsed.TotalSeconds:F1} s");
    foreach (var (feature, members) in parts)
    {
        Console.WriteLine($"## {feature}: {members.Count} types, digest {HostFingerprint.HashText(string.Join("\n", members.Select(m => m.Key + "=" + m.Value)))}");
        foreach (var (member, hash) in members) Console.WriteLine($"  {member} {hash}");
    }
    var evaluation = HostContracts.Evaluate(parts);
    Console.WriteLine($"# verdict: baseline {evaluation.Baseline ?? "(none)"}; features: {string.Join(", ", evaluation.Features.Order(StringComparer.Ordinal))}");
    foreach (var (feature, problem) in evaluation.Problems) Console.WriteLine($"#   off {feature}: {problem}");
}

static void Emit(string output, string[] builds)
{
    var text = new System.Text.StringBuilder();
    text.Append("""
        // <auto-generated>
        // The HostContracts parts of the YMM4 builds whose code was read, newest first. Regenerate with
        //   dotnet run --project tools/HostFingerprint -- emit NVEncVideoWriterPlugin/HostBaselines.cs <version>=<YMM4 dir>...
        // </auto-generated>
        namespace NVEncVideoWriterPlugin;

        internal static partial class HostContracts
        {
            internal static readonly Baseline[] Baselines =
            [

        """);
    foreach (var build in builds)
    {
        int equals = build.IndexOf('=');
        string version = build[..equals], directory = build[(equals + 1)..];
        text.Append($"        new(\"{version}\", new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)\n        {{\n");
        foreach (var (feature, members) in HostContracts.Describe(directory))
        {
            text.Append($"            [\"{feature}\"] = new Dictionary<string, string>(StringComparer.Ordinal)\n            {{\n");
            foreach (var (member, hash) in members) text.Append($"                [\"{member}\"] = \"{hash}\",\n");
            text.Append("            },\n");
        }
        text.Append("        }),\n");
    }
    text.Append("    ];\n}\n");
    File.WriteAllText(output, text.ToString().ReplaceLineEndings("\n"));
    Console.WriteLine($"wrote {output}");
}

static void Compare(string before, string after)
{
    var baseline = new HostContracts.Baseline(Path.GetFileName(Path.TrimEndingDirectorySeparator(before)),
        HostContracts.Describe(before).ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value));
    var evaluation = HostContracts.Evaluate(HostContracts.Describe(after), [baseline]);
    Console.WriteLine($"# verdict against {baseline.Version}: features: {string.Join(", ", evaluation.Features.Order(StringComparer.Ordinal))}");
    foreach (var (feature, problem) in evaluation.Problems) Console.WriteLine($"#   off {feature}: {problem}");
}
