using NVEncVideoWriterPlugin;

// Build-independent fingerprints of YMM4's own assemblies (HostFingerprint.cs), for comparing YMM4 versions.
// Reads the files only; nothing is loaded or run. The output is hashes and type/member names, not code.
//   dotnet run --project tools/HostFingerprint -- report <YMM4 dir> > 4.56.1.0.tsv
//   dotnet run --project tools/HostFingerprint -- diff <old.tsv> <new.tsv>
//   dotnet run --project tools/HostFingerprint -- members <old YMM4 dir> <new YMM4 dir> <type full name>...
switch (args)
{
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
        Console.Error.WriteLine("Usage: report <dir> | diff <old.tsv> <new.tsv> | members <old dir> <new dir> <type>...");
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
