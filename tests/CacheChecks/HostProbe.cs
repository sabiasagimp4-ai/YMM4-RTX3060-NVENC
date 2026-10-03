using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// Temporary investigation (not a check): how the host reads image sequences and fonts, and what the other sources the
// cache does not key yet depend on. Never fails.
internal static class HostProbe
{
    internal static void Run(string hostDir)
    {
        Console.WriteLine("=== HOSTPROBE BEGIN ===");
        Try("types", () => Types(hostDir));
        Try("fonts-runtime", FontsRuntime);
        Try("callers", () => Callers(hostDir));
        Try("decompile", () => Decompile(hostDir));
        Console.WriteLine("=== HOSTPROBE END ===");
        Console.Out.Flush();
    }

    private static void Try(string name, Action action)
    {
        Console.WriteLine($"--- {name} ---");
        try { action(); }
        catch (Exception error) { Console.WriteLine($"probe {name} failed: {error}"); }
    }

    private static IEnumerable<string> HostFiles(string hostDir) =>
        Directory.GetFiles(hostDir, "YukkuriMovieMaker*.dll").Order(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Interesting = new(
        "Sequen|Font|Tachie|LipSync|Blink|Lut|GradientMap|Shuffle|NumberText|Midi|SoundFont|DirectShow|AfterImage|MotionBlur|Container|ArrangeGroup|TilingGroup|Particl|PuppetDeform|VectorField|FillSame|DirectionalColorKey|OpenFx|Vst3|Psd|Svg",
        RegexOptions.CultureInvariant);

    private static void Types(string hostDir)
    {
        foreach (var file in HostFiles(hostDir))
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                string name = TypeName(md, handle);
                if (!Interesting.IsMatch(name) || name.Contains("<", StringComparison.Ordinal)) continue;
                var type = md.GetTypeDefinition(handle);
                Console.WriteLine($"  {Path.GetFileName(file)}: {name} fields={type.GetFields().Count} methods={type.GetMethods().Count}");
            }
        }
    }

    private static void FontsRuntime()
    {
        var settingsType = typeof(YukkuriMovieMaker.Settings.FontSettings);
        foreach (var property in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"  FontSettings.{property.Name} : {property.PropertyType}");
        var custom = settingsType.GetProperty("CustomFonts")!.PropertyType;
        var element = custom.IsGenericType ? custom.GetGenericArguments()[0] : custom.GetElementType();
        Console.WriteLine($"  CustomFonts element: {element?.AssemblyQualifiedName}");
        if (element is not null)
            foreach (var property in element.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"    {element.Name}.{property.Name} : {property.PropertyType}");
        var settings = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.FontSettings>.Default;
        Console.WriteLine($"  SystemFonts={settings.SystemFonts.Count()} CustomFonts={settings.CustomFonts.Count()}");
        foreach (var font in settings.SystemFonts.Take(3)) Console.WriteLine($"    system: {Describe(font)}");
    }

    private static string Describe(object value) => string.Join(", ", value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0).Select(p => { try { return $"{p.Name}={p.GetValue(value)}"; } catch { return p.Name + "=?"; } }));

    private static readonly string[] CalledMembers =
    [
        "AddFontResourceEx", "AddFontResource", "AddFontMemResourceEx", "AddFontResourceExW", "AddFontResourceW", "CreateCustomFontCollection",
        "RegisterFontCollectionLoader", "RegisterFontFileLoader", "CreateFontSetBuilder", "AddFontFile", "CreateFontCollectionFromFontSet",
        "CreateFontFileReference", "GetSystemFontCollection", "get_CustomFonts", "get_SystemFonts", "CreateTextFormat", "GetFontCollection",
    ];

    private static void Callers(string hostDir)
    {
        var declaring = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in HostFiles(hostDir))
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var targets = new Dictionary<int, string>();
            foreach (var handle in md.MemberReferences)
            {
                var member = md.GetMemberReference(handle);
                string name = md.GetString(member.Name);
                if (CalledMembers.Contains(name)) targets[MetadataTokens.GetToken(handle)] = ParentName(md, member.Parent) + "." + name;
            }
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                string name = md.GetString(method.Name);
                if (CalledMembers.Contains(name)) targets[MetadataTokens.GetToken(handle)] = TypeName(md, method.GetDeclaringType()) + "." + name;
                // P/Invoke declarations of the font APIs
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 && name.Contains("Font", StringComparison.Ordinal))
                    Console.WriteLine($"  pinvoke {Path.GetFileName(file)}: {TypeName(md, method.GetDeclaringType())}.{name}");
            }
            if (targets.Count == 0) continue;
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent();
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    bool call = il[i] is 0x28 or 0x6F or 0x73 || (il[i] == 0xFE && i + 5 < il.Length && il[i + 1] is 0x06 or 0x07);
                    if (!call) continue;
                    int at = il[i] == 0xFE ? i + 2 : i + 1;
                    if (at + 4 > il.Length) continue;
                    int token = BitConverter.ToInt32(il.AsSpan(at, 4));
                    if (targets.TryGetValue(token, out var target))
                    {
                        string owner = TypeName(md, method.GetDeclaringType());
                        Console.WriteLine($"  {Path.GetFileName(file)}: {owner}.{md.GetString(method.Name)} -> {target}");
                        if (!target.EndsWith("GetSystemFontCollection", StringComparison.Ordinal) && !target.EndsWith("CreateTextFormat", StringComparison.Ordinal))
                            declaring.Add(Path.GetFileName(file) + "|" + owner.Split('/')[0]);
                    }
                }
            }
        }
        fontOwners = declaring.ToArray();
        Console.WriteLine("  font owners: " + string.Join("; ", fontOwners));
    }

    private static string[] fontOwners = [];

    private static string ParentName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
        HandleKind.TypeDefinition => TypeName(md, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => "spec",
        HandleKind.MethodDefinition => "method",
        HandleKind.ModuleReference => "module:" + md.GetString(md.GetModuleReference((ModuleReferenceHandle)parent).Name),
        _ => parent.Kind.ToString(),
    };

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        string name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? md.GetString(type.Namespace) + "." + name : TypeName(md, declaring) + "/" + name;
    }

    private static void Decompile(string hostDir)
    {
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompilers = new Dictionary<string, CSharpDecompiler>(StringComparer.OrdinalIgnoreCase);
        CSharpDecompiler? For(string file)
        {
            if (decompilers.TryGetValue(file, out var known)) return known;
            try { return decompilers[file] = new CSharpDecompiler(Path.Combine(hostDir, file), settings); }
            catch (Exception error) { Console.WriteLine($"{file}: {error.GetType().Name} {error.Message}"); return null; }
        }
        // Simple names searched in every host assembly, with a line cap each.
        (string Name, int Lines)[] simple =
        [
            ("WICSequentialImageVideoSource", 700), ("WICSequentialImageVideoSourcePlugin", 300), ("FontSettings", 300), ("Font", 300),
            ("TachieSource", 700),
        ];
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in HostFiles(hostDir).Select(Path.GetFileName))
        {
            var decompiler = For(file!);
            if (decompiler is null) continue;
            foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions)
            {
                if (type.DeclaringTypeDefinition is not null) continue;
                var match = simple.FirstOrDefault(s => s.Name == type.Name);
                bool owner = fontOwners.Contains(file + "|" + type.FullName);
                if (match.Name is null && !owner) continue;
                if (match.Name == "Font" && !type.Namespace.Contains("Setting", StringComparison.Ordinal) && !type.Namespace.Contains("Font", StringComparison.Ordinal)) continue;
                if (!done.Add(file + "|" + type.FullName)) continue;
                int cap = match.Name is null ? 500 : match.Lines;
                string code;
                try { code = decompiler.DecompileTypeAsString(type.FullTypeName); }
                catch (Exception error) { Console.WriteLine($"{type.FullName}: {error.GetType().Name}"); continue; }
                var lines = code.Split('\n');
                Console.WriteLine($">>> {file}: {type.FullName} ({lines.Length} lines)");
                foreach (var line in lines.Take(cap)) Console.WriteLine(line.TrimEnd('\r'));
                Console.WriteLine($"<<< {type.FullName}");
            }
        }
    }
}
