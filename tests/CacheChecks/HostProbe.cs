using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;

// Temporary investigation (not a check): prints how the host behaves where the plugin depends on it. Never fails.
internal static class HostProbe
{
    internal static void Run(string hostDir, Scene scene)
    {
        Console.WriteLine("=== HOSTPROBE BEGIN ===");
        Try("settings", () => Settings(scene));
        Try("timeline-properties", () => Properties(typeof(Timeline)));
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

    private static void Settings(Scene scene)
    {
        var type = typeof(YukkuriMovieMaker.Settings.YMMSettings);
        var settings = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.YMMSettings>.Default;
        Console.WriteLine($"YMMSettings base chain: {string.Join(" <- ", Chain(type))}");
        Console.WriteLine($"YMMSettings INotifyPropertyChanged={settings is INotifyPropertyChanged} INotifyPropertyChanging={settings is INotifyPropertyChanging}");
        Properties(type);
        var zoom = type.GetProperty("TimelineZoom");
        if (zoom is null) { Console.WriteLine("YMMSettings.TimelineZoom: missing"); return; }
        using var tracker = new KeyDependencyTracker(scene);
        if (!tracker.TryCapture(out var capture, out string reason)) { Console.WriteLine("capture failed: " + reason); return; }
        using (capture)
        {
            var changed = new List<string>();
            PropertyChangedEventHandler handler = (_, e) => changed.Add(e.PropertyName ?? "<null>");
            if (settings is INotifyPropertyChanged notify) notify.PropertyChanged += handler;
            object? before = zoom.GetValue(settings);
            try
            {
                object next = before switch { double d => d * 1.5 + 1, float f => f * 1.5f + 1, int i => i + 7, _ => before! };
                zoom.SetValue(settings, next);
                Console.WriteLine($"TimelineZoom {before} -> {zoom.GetValue(settings)}: PropertyChanged=[{string.Join(",", changed)}] "
                    + $"capture still valid={capture!.Validate(files: false)} revision {capture.Revision} -> {tracker.Revision}");
            }
            finally
            {
                zoom.SetValue(settings, before);
                if (settings is INotifyPropertyChanged notify2) notify2.PropertyChanged -= handler;
            }
        }
    }

    private static IEnumerable<string> Chain(Type? type)
    {
        for (; type is not null; type = type.BaseType) yield return type.FullName ?? type.Name;
    }

    private static void Properties(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var attributes = property.GetCustomAttributes(true).Select(a => a.GetType().Name.Replace("Attribute", "")).ToArray();
            Console.WriteLine($"  {type.Name}.{property.Name} : {property.PropertyType.Name} set={(property.SetMethod?.IsPublic == true ? "public" : property.SetMethod is null ? "-" : "private")}"
                + (attributes.Length == 0 ? "" : $" [{string.Join(",", attributes)}]"));
        }
    }

    // Methods whose IL references the members of interest (naive token scan of call/callvirt/newobj/ldftn operands).
    private static void Callers(string hostDir)
    {
        (string Member, string Parent)[] interesting =
        [
            ("Update", "IVideoFileSource"), ("Create", "VideoFileSourceFactory"), ("CreateVideoFileSource", ""),
            ("SetMultithreadProtected", ""), ("D3D11CreateDevice", ""), ("D2D1CreateFactory", ""), ("CreateDevice", "ID2D1Factory1"),
            ("CreateDeviceContext", ""), ("get_EncodeFrom", ""), ("get_EncodeTo", ""), ("WriteVideo", ""), ("WriteAudio", ""),
            (".ctor", "TimelineSourceAndDevices"), (".ctor", "GraphicsDevicesAndContext"),
        ];
        foreach (var file in Directory.GetFiles(hostDir, "YukkuriMovieMaker*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            var targets = new Dictionary<int, string>();
            foreach (var handle in md.MemberReferences)
            {
                var member = md.GetMemberReference(handle);
                string name = md.GetString(member.Name), parent = ParentName(md, member.Parent);
                if (interesting.Any(i => i.Member == name && (i.Parent.Length == 0 || parent.Contains(i.Parent, StringComparison.Ordinal))))
                    targets[MetadataTokens.GetToken(handle)] = parent + "." + name;
            }
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                string name = md.GetString(method.Name), parent = TypeName(md, method.GetDeclaringType());
                if (interesting.Any(i => i.Member == name && i.Parent.Length != 0 && parent.Contains(i.Parent, StringComparison.Ordinal)))
                    targets[MetadataTokens.GetToken(handle)] = parent + "." + name;
            }
            if (targets.Count == 0) continue;
            var found = new SortedSet<string>(StringComparer.Ordinal);
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
                        found.Add($"{Path.GetFileName(file)}: {TypeName(md, method.GetDeclaringType())}.{md.GetString(method.Name)} -> {target}");
                }
            }
            foreach (var line in found) Console.WriteLine("  " + line);
        }
    }

    private static string ParentName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
        HandleKind.TypeDefinition => TypeName(md, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => "spec",
        HandleKind.MethodDefinition => "method",
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
        (string File, string Type, string[]? Members)[] targets =
        [
            ("YukkuriMovieMaker.dll", "YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", null),
            ("YukkuriMovieMaker.Plugin.dll", "YukkuriMovieMaker.Player.Video.TimelineSourceAndDevices", null),
            ("YukkuriMovieMaker.Plugin.dll", "YukkuriMovieMaker.Settings.YMMSettings", ["TimelineZoom", "get_TimelineZoom", "set_TimelineZoom"]),
        ];
        foreach (var (file, typeName, members) in targets)
        {
            string path = Path.Combine(hostDir, file);
            CSharpDecompiler decompiler;
            try { decompiler = new CSharpDecompiler(path, settings); }
            catch (Exception error) { Console.WriteLine($"{file}: {error.GetType().Name} {error.Message}"); continue; }
            var definition = decompiler.TypeSystem.FindType(new FullTypeName(typeName)).GetDefinition();
            if (definition is null)
            {
                // The type may live in the other assembly.
                foreach (var other in Directory.GetFiles(hostDir, "YukkuriMovieMaker*.dll"))
                {
                    try
                    {
                        var candidate = new CSharpDecompiler(other, settings);
                        if (candidate.TypeSystem.MainModule.GetTypeDefinition(new TopLevelTypeName(typeName.Substring(0, typeName.LastIndexOf('.')), typeName[(typeName.LastIndexOf('.') + 1)..])) is { } found)
                        { decompiler = candidate; definition = found; Console.WriteLine($"{typeName} found in {Path.GetFileName(other)}"); break; }
                    }
                    catch { }
                }
            }
            if (definition is null) { Console.WriteLine($"{typeName}: not found"); continue; }
            string code = members is null ? decompiler.DecompileTypeAsString(new FullTypeName(typeName))
                : decompiler.DecompileAsString(definition.Members.Where(m => members.Contains(m.Name)).Select(m => m.MetadataToken).ToList());
            Console.WriteLine($">>> {typeName} ({code.Split('\n').Length} lines)");
            foreach (var line in code.Split('\n').Take(900)) Console.WriteLine(line.TrimEnd('\r'));
            Console.WriteLine($"<<< {typeName}");
        }
    }
}
