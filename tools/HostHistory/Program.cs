using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

// Temporary, for reading how YMM4's code changed between versions on CI (.github/workflows/host-history.yml).
// Nothing it writes is committed.
//   decompile <YMM4 dir> <out dir> <types file>
// <types file>: lines "Assembly<TAB>Namespace.Type" (top-level types). Writes <out dir>/<Assembly>/<Type>.cs.
if (args is not ["decompile", var directory, var output, var list])
{
    Console.Error.WriteLine("usage: decompile <YMM4 dir> <out dir> <types file>");
    return 2;
}
var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
foreach (var group in File.ReadLines(list).Select(line => line.Split('\t')).Where(parts => parts.Length == 2).GroupBy(parts => parts[0]))
{
    string path = Path.Combine(directory, group.Key + ".dll");
    if (!File.Exists(path)) continue;
    var module = new PEFile(path);
    var resolver = new UniversalAssemblyResolver(path, false, module.DetectTargetFrameworkId());
    resolver.AddSearchDirectory(directory);
    var decompiler = new CSharpDecompiler(path, resolver, settings);
    Directory.CreateDirectory(Path.Combine(output, group.Key));
    foreach (var parts in group)
    {
        string text;
        try { text = decompiler.DecompileTypeAsString(new FullTypeName(parts[1])); }
        catch (Exception error) { text = $"// decompile failed: {error.GetType().Name}: {error.Message}\n"; }
        File.WriteAllText(Path.Combine(output, group.Key, parts[1] + ".cs"), text);
    }
}
return 0;
