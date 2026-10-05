using NVEncVideoWriterPlugin;

// Build-independent fingerprints of YMM4's own assemblies (HostFingerprint.cs), for comparing YMM4 versions.
// Reads the files only; nothing is loaded or run. The output is hashes and type/member names, not code.
//   dotnet run --project tools/HostFingerprint -- report <YMM4 dir> > 4.56.1.0.tsv
//   dotnet run --project tools/HostFingerprint -- diff <old.tsv> <new.tsv>
//   dotnet run --project tools/HostFingerprint -- members <old YMM4 dir> <new YMM4 dir> <type full name>...
//   dotnet run --project tools/HostFingerprint -- contracts <YMM4 dir>              (HostContracts parts and the verdict)
//   dotnet run --project tools/HostFingerprint -- compare <read YMM4 dir> <new YMM4 dir>  (the verdict if the first were read)
//   dotnet run --project tools/HostFingerprint -- emit <out.cs> <version>=<YMM4 dir>... (HostBaselines.cs, newest first)
//   dotnet run --project tools/HostFingerprint -- scan <YMM4 dir> [<plugin dir>]   (one JSON line for tools/compat)
//   dotnet run --project tools/HostFingerprint -- api <plugin dll> <YMM4 dir>      (references the YMM4 build does not define)
switch (args)
{
    case ["contracts", var directory]:
        Contracts(directory);
        return 0;
    case ["compare", var before, var after]:
        Compare(before, after);
        return 0;
    case ["scan", var directory, .. var plugin] when plugin.Length <= 1:
        Scan(directory, plugin.FirstOrDefault());
        return 0;
    case ["api", var pluginDll, var directory]:
        foreach (var line in ApiCheck.Missing(pluginDll, directory)) Console.WriteLine(line);
        return 0;
    case ["emit", var output, .. var builds]:
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
        Console.Error.WriteLine("Usage: report <dir> | diff <old.tsv> <new.tsv> | members <old dir> <new dir> <type>... | contracts <dir> | emit <out.cs> <version>=<dir>... | scan <dir> [<plugin dir>]");
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

// The given builds are described now; the other recorded builds are kept as compiled in (they stay valid only while
// HostContracts.Rules is unchanged: after changing the rules, emit every read build again).
static void Emit(string output, string[] builds)
{
    var baselines = HostContracts.Baselines.ToDictionary(b => b.Version, b => b.Features);
    foreach (var build in builds)
    {
        int equals = build.IndexOf('=');
        baselines[build[..equals]] = HostContracts.Describe(build[(equals + 1)..])
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value);
    }
    var text = new System.Text.StringBuilder();
    text.Append("""
        // <auto-generated>
        // The HostContracts parts of the YMM4 builds whose code was read, newest first. Regenerate with
        //   dotnet run --project tools/HostFingerprint -- emit NVEncVideoWriterPlugin/HostBaselines.cs <version>=<YMM4 dir>...
        // (builds not given are kept; after changing HostContracts.Rules, give every read build).
        // </auto-generated>
        namespace NVEncVideoWriterPlugin;

        internal static partial class HostContracts
        {
            internal static readonly Baseline[] Baselines =
            [

        """);
    foreach (var (version, features) in baselines.OrderByDescending(pair => Version.Parse(pair.Key)))
    {
        text.Append($"        new(\"{version}\", new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)\n        {{\n");
        foreach (var (feature, members) in features.OrderBy(pair => Array.FindIndex(HostContracts.Rules, rule => rule.Feature == pair.Key)))
        {
            text.Append($"            [\"{feature}\"] = new Dictionary<string, string>(StringComparer.Ordinal)\n            {{\n");
            foreach (var (member, hash) in members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                text.Append($"                [\"{member}\"] = \"{hash}\",\n");
            text.Append("            },\n");
        }
        text.Append("        }),\n");
    }
    text.Append("    ];\n}\n");
    File.WriteAllText(output, text.ToString().ReplaceLineEndings("\n"));
    Console.WriteLine($"wrote {output}: {string.Join(", ", baselines.Keys.OrderByDescending(Version.Parse))}");
}

static void Compare(string before, string after)
{
    var baseline = new HostContracts.Baseline(Path.GetFileName(Path.TrimEndingDirectorySeparator(before)),
        HostContracts.Describe(before).ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value));
    var evaluation = HostContracts.Evaluate(HostContracts.Describe(after), [baseline]);
    Console.WriteLine($"# verdict against {baseline.Version}: features: {string.Join(", ", evaluation.Features.Order(StringComparer.Ordinal))}");
    foreach (var (feature, problem) in evaluation.Problems) Console.WriteLine($"#   off {feature}: {problem}");
}

// What decides whether the plugin can run on this YMM4 build, read from the files only (tools/compat):
// the .NET runtime it starts (runtimeconfig), the host assemblies the plugin references and their versions, whether
// the build is one whose code was read (HostKnownBuilds), and the HostContracts verdict for the cache.
static void Scan(string directory, string? pluginDirectory)
{
    var result = new Dictionary<string, object?>();
    string runtimeConfig = Path.Combine(directory, "YukkuriMovieMaker.runtimeconfig.json");
    if (File.Exists(runtimeConfig))
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        var options = json.RootElement.GetProperty("runtimeOptions");
        // A self-contained YMM4 lists the runtime it carries as includedFrameworks.
        bool selfContained = options.TryGetProperty("includedFrameworks", out var included);
        var frameworks = selfContained ? included.EnumerateArray().ToArray()
            : options.TryGetProperty("frameworks", out var list) ? list.EnumerateArray().ToArray()
            : options.TryGetProperty("framework", out var single) ? [single] : [];
        result["runtime"] = new Dictionary<string, object?>
        {
            ["tfm"] = options.TryGetProperty("tfm", out var tfm) ? tfm.GetString() : null,
            ["selfContained"] = selfContained,
            ["rollForward"] = options.TryGetProperty("rollForward", out var roll) ? roll.GetString() : null,
            ["frameworks"] = frameworks.ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("version").GetString()),
        };
    }
    string? pluginPath = pluginDirectory is null ? null : Directory.GetFiles(pluginDirectory, "YMM4Rtx3060Nvenc.dll").Single();
    if (pluginPath is not null)
    {
        using var stream = File.OpenRead(pluginPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        result["plugin"] = new Dictionary<string, object?> { ["framework"] = TargetFramework(System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe)) };
    }
    // Only the runtime configuration was fetched (a YMM4 whose runtime cannot load the plugin): nothing more to read.
    string hostPath = Path.Combine(directory, "YukkuriMovieMaker.dll");
    if (!File.Exists(hostPath))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        return;
    }
    var host = AssemblyIdentity(hostPath);
    result["assemblyVersion"] = host?.Version;
    result["fileVersion"] = host?.FileVersion;
    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hostPath)));
    result["knownBuild"] = HostKnownBuilds.All.FirstOrDefault(b => b.Host.Mvid == HostFingerprint.ReadMvid(hostPath) && b.Host.Sha256 == sha)?.Version;
    try
    {
        var evaluation = HostContracts.Evaluate(HostContracts.Describe(directory));
        result["contracts"] = new Dictionary<string, object?>
        {
            ["baseline"] = evaluation.Baseline,
            ["features"] = evaluation.Features.Order(StringComparer.Ordinal).ToArray(),
            ["off"] = evaluation.Problems,
        };
    }
    catch (Exception ex) { result["contracts"] = new Dictionary<string, object?> { ["error"] = ex.GetBaseException().Message }; }
    if (pluginPath is not null)
    {
        using var stream = File.OpenRead(pluginPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var references = new List<Dictionary<string, object?>>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            string name = reader.GetString(reference.Name);
            // Framework assemblies come with the runtime checked above, and the plugin carries its own (Harmony).
            if (File.Exists(Path.Combine(pluginDirectory!, name + ".dll"))) continue;
            string candidate = Path.Combine(directory, name + ".dll");
            bool fromHost = File.Exists(candidate);
            if (!fromHost && !name.StartsWith("YukkuriMovieMaker", StringComparison.Ordinal)) continue;
            references.Add(new()
            {
                ["name"] = name,
                ["required"] = reference.Version.ToString(),
                ["host"] = fromHost ? AssemblyIdentity(candidate)?.Version : null,
            });
        }
        var plugin = (Dictionary<string, object?>)result["plugin"]!;
        plugin["references"] = references;
        try { plugin["apiMissing"] = ApiCheck.Missing(pluginPath, directory); }
        catch (Exception ex) { plugin["apiError"] = ex.GetBaseException().Message; }
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
}

static (string Version, string? FileVersion)? AssemblyIdentity(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata) return null;
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        if (!reader.IsAssembly) return null;
        string? fileVersion = Attribute(reader, "AssemblyFileVersionAttribute");
        return (reader.GetAssemblyDefinition().Version.ToString(), fileVersion);
    }
    catch (BadImageFormatException) { return null; }
}

static string? TargetFramework(System.Reflection.Metadata.MetadataReader reader) => Attribute(reader, "TargetFrameworkAttribute");

// The first string argument of an assembly attribute with this type name.
static string? Attribute(System.Reflection.Metadata.MetadataReader reader, string typeName)
{
    foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
        var constructor = reader.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
        var type = reader.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)constructor.Parent);
        if (reader.GetString(type.Name) != typeName) continue;
        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1) continue; // prolog
        return blob.ReadSerializedString();
    }
    return null;
}
