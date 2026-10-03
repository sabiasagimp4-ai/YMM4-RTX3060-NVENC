using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using YukkuriMovieMaker.Project;

// Temporary investigation (not a check): which host code reads which settings. Never fails.
internal static class HostProbe
{
    internal static void Run(string hostDir, Scene scene)
    {
        Console.WriteLine("=== HOSTPROBE BEGIN ===");
        try { SettingsReaders(hostDir); }
        catch (Exception error) { Console.WriteLine("probe failed: " + error); }
        try
        {
            var layerSettings = typeof(Timeline).GetProperty("LayerSettings")!.PropertyType;
            foreach (var property in layerSettings.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  LayerSettings.{property.Name} : {property.PropertyType.Name}");
            var layer = layerSettings.GetProperties().Select(p => p.PropertyType).FirstOrDefault(t => t.IsGenericType)?.GetGenericArguments().FirstOrDefault();
            if (layer is not null)
                foreach (var property in layer.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    Console.WriteLine($"  {layer.Name}.{property.Name} : {property.PropertyType.Name}");
        }
        catch (Exception error) { Console.WriteLine("layer probe failed: " + error); }
        Console.WriteLine("=== HOSTPROBE END ===");
        Console.Out.Flush();
    }

    // Every method of the host assemblies that calls a property getter of a type in YukkuriMovieMaker.Settings
    // (naive token scan of call/callvirt operands), grouped by setting.
    private static void SettingsReaders(string hostDir)
    {
        var readers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
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
                if (name.StartsWith("get_", StringComparison.Ordinal) && parent.StartsWith("YukkuriMovieMaker.Settings.", StringComparison.Ordinal))
                    targets[MetadataTokens.GetToken(handle)] = parent["YukkuriMovieMaker.Settings.".Length..] + "." + name[4..];
            }
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                string name = md.GetString(method.Name), parent = TypeName(md, method.GetDeclaringType());
                if (name.StartsWith("get_", StringComparison.Ordinal) && parent.StartsWith("YukkuriMovieMaker.Settings.", StringComparison.Ordinal))
                    targets[MetadataTokens.GetToken(handle)] = parent["YukkuriMovieMaker.Settings.".Length..] + "." + name[4..];
            }
            if (targets.Count == 0) continue;
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                string caller = TypeName(md, method.GetDeclaringType());
                if (caller.StartsWith("YukkuriMovieMaker.Settings.", StringComparison.Ordinal)) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent();
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] is not (0x28 or 0x6F)) continue;
                    if (!targets.TryGetValue(BitConverter.ToInt32(il.AsSpan(i + 1, 4)), out var target)) continue;
                    if (!readers.TryGetValue(target, out var set)) readers[target] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add($"{Path.GetFileNameWithoutExtension(file)}:{caller}.{md.GetString(method.Name)}");
                }
            }
        }
        foreach (var (setting, callers) in readers)
        {
            Console.WriteLine($"SETTING {setting} ({callers.Count})");
            foreach (var caller in callers.Take(40)) Console.WriteLine("    " + caller);
        }
    }

    private static string ParentName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
        HandleKind.TypeDefinition => TypeName(md, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => "spec",
        _ => parent.Kind.ToString(),
    };

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        string name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? md.GetString(type.Namespace) + "." + name : TypeName(md, declaring) + "/" + name;
    }
}
