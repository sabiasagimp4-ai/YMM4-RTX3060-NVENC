using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Does a YMM4 build define every type and member the plugin references in the assemblies YMM4 ships? The runtime
// resolves these only when the code that uses them first runs, so a plugin that loads can still fail later (an export,
// a feature) on a build that lacks one. Reads the files only: names and signatures, as the runtime binds them.
internal static class ApiCheck
{
    internal static List<string> Missing(string pluginPath, string hostDirectory)
    {
        using var plugin = new Module(pluginPath);
        var hosts = new Dictionary<string, Module?>(StringComparer.OrdinalIgnoreCase);
        Module? Host(string assembly)
        {
            if (!hosts.TryGetValue(assembly, out var module))
            {
                string path = Path.Combine(hostDirectory, assembly + ".dll");
                hosts[assembly] = module = File.Exists(path) ? new Module(path) : null;
            }
            return module;
        }
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var handle in plugin.Reader.TypeReferences)
            {
                if (plugin.Scope(handle) is not { } assembly || !IsHostAssembly(assembly, hostDirectory)) continue;
                if (Resolve(Host, assembly, plugin.Name(handle)) is null) missing.Add($"{assembly}: {plugin.Name(handle)}");
            }
            foreach (var handle in plugin.Reader.MemberReferences)
            {
                var member = plugin.Reader.GetMemberReference(handle);
                var (assembly, typeName) = member.Parent.Kind switch
                {
                    HandleKind.TypeReference => (plugin.Scope((TypeReferenceHandle)member.Parent), plugin.Name((TypeReferenceHandle)member.Parent)),
                    HandleKind.TypeSpecification => plugin.GenericDefinition((TypeSpecificationHandle)member.Parent),
                    _ => (null, null),
                };
                if (assembly is null || typeName is null || !IsHostAssembly(assembly, hostDirectory)) continue;
                string name = plugin.Reader.GetString(member.Name);
                string signature = plugin.MemberSignature(member.Signature);
                if (Resolve(Host, assembly, typeName) is not { } found) continue; // reported with the type
                if (!HasMember(Host, found.Module, found.Type, name, signature)) missing.Add($"{assembly}: {typeName}::{name} {signature}");
            }
        }
        finally
        {
            foreach (var module in hosts.Values) module?.Dispose();
        }
        return missing.ToList();
    }

    // The plugin's own references to host-provided assemblies (the others come with the runtime or the plugin).
    private static bool IsHostAssembly(string assembly, string hostDirectory) => File.Exists(Path.Combine(hostDirectory, assembly + ".dll"));

    // The type's definition in that assembly, following type forwarders.
    private static (Module Module, TypeDefinitionHandle Type)? Resolve(Func<string, Module?> host, string assembly, string typeName, int depth = 0)
    {
        if (depth > 4 || host(assembly) is not { } module) return null;
        if (module.Types.TryGetValue(typeName, out var type)) return (module, type);
        // A nested type is forwarded with its outermost type.
        int plus = typeName.IndexOf('+');
        return module.Forwarded.TryGetValue(plus < 0 ? typeName : typeName[..plus], out var target) ? Resolve(host, target, typeName, depth + 1) : null;
    }

    // A member with this name and signature on the type or one of its base types.
    private static bool HasMember(Func<string, Module?> host, Module module, TypeDefinitionHandle type, string name, string signature)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            var definition = module.Reader.GetTypeDefinition(type);
            foreach (var handle in definition.GetMethods())
            {
                var method = module.Reader.GetMethodDefinition(handle);
                if (module.Reader.StringComparer.Equals(method.Name, name) && module.MemberSignature(method.Signature) == signature) return true;
            }
            foreach (var handle in definition.GetFields())
            {
                var field = module.Reader.GetFieldDefinition(handle);
                if (module.Reader.StringComparer.Equals(field.Name, name) && module.MemberSignature(field.Signature) == signature) return true;
            }
            var (baseAssembly, baseName) = definition.BaseType.Kind switch
            {
                HandleKind.TypeDefinition => (module.Assembly, module.Name((TypeDefinitionHandle)definition.BaseType)),
                HandleKind.TypeReference => (module.Scope((TypeReferenceHandle)definition.BaseType), module.Name((TypeReferenceHandle)definition.BaseType)),
                HandleKind.TypeSpecification => module.GenericDefinition((TypeSpecificationHandle)definition.BaseType),
                _ => (null, null),
            };
            if (baseAssembly is null || baseName is null || Resolve(host, baseAssembly, baseName) is not { } next) return false;
            (module, type) = next;
        }
        return false;
    }

    private sealed class Module : IDisposable, ISignatureTypeProvider<string, object?>
    {
        private readonly PEReader pe;
        internal MetadataReader Reader { get; }
        internal string Assembly { get; }
        internal Dictionary<string, TypeDefinitionHandle> Types { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Forwarded { get; } = new(StringComparer.Ordinal);

        internal Module(string path)
        {
            pe = new PEReader(File.OpenRead(path));
            Reader = pe.GetMetadataReader();
            Assembly = Reader.GetString(Reader.GetAssemblyDefinition().Name);
            foreach (var handle in Reader.TypeDefinitions) Types.TryAdd(Name(handle), handle);
            foreach (var handle in Reader.ExportedTypes)
            {
                var exported = Reader.GetExportedType(handle);
                if (exported.Implementation.Kind != HandleKind.AssemblyReference) continue;
                string name = Join(Reader.GetString(exported.Namespace), Reader.GetString(exported.Name));
                Forwarded.TryAdd(name, Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name));
            }
        }

        public void Dispose() => pe.Dispose();

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        internal string Name(TypeDefinitionHandle handle)
        {
            var definition = Reader.GetTypeDefinition(handle);
            string name = Reader.GetString(definition.Name);
            return definition.GetDeclaringType() is { IsNil: false } outer ? Name(outer) + "+" + name : Join(Reader.GetString(definition.Namespace), name);
        }

        internal string Name(TypeReferenceHandle handle)
        {
            var reference = Reader.GetTypeReference(handle);
            string name = Reader.GetString(reference.Name);
            return reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? Name((TypeReferenceHandle)reference.ResolutionScope) + "+" + name
                : Join(Reader.GetString(reference.Namespace), name);
        }

        // The assembly a type reference resolves in (through its declaring types).
        internal string? Scope(TypeReferenceHandle handle)
        {
            var scope = Reader.GetTypeReference(handle).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) scope = Reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference ? Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                : scope.Kind == HandleKind.ModuleDefinition ? Assembly : null;
        }

        // A generic instantiation's type definition (its assembly and name).
        internal (string? Assembly, string? Name) GenericDefinition(TypeSpecificationHandle handle)
        {
            var blob = Reader.GetBlobReader(Reader.GetTypeSpecification(handle).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return (null, null);
            blob.ReadSignatureTypeCode(); // class or valuetype
            var definition = blob.ReadTypeHandle();
            return definition.Kind switch
            {
                HandleKind.TypeReference => (Scope((TypeReferenceHandle)definition), Name((TypeReferenceHandle)definition)),
                HandleKind.TypeDefinition => (Assembly, Name((TypeDefinitionHandle)definition)),
                _ => (null, null),
            };
        }

        internal string MemberSignature(BlobHandle handle)
        {
            var header = Reader.GetBlobReader(handle).ReadSignatureHeader();
            var decoder = new SignatureDecoder<string, object?>(this, Reader, null);
            var blob = Reader.GetBlobReader(handle);
            if (header.Kind == SignatureKind.Field) return "field " + decoder.DecodeFieldSignature(ref blob);
            var method = decoder.DecodeMethodSignature(ref blob);
            return $"{(method.Header.IsInstance ? "instance " : "static ")}{method.ReturnType} <{method.GenericParameterCount}>({string.Join(",", method.ParameterTypes)})";
        }

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => $"fnptr {signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => isRequired ? $"{unmodifiedType} modreq({modifier})" : unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Name(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
            return new SignatureDecoder<string, object?>(this, reader, genericContext).DecodeType(ref blob);
        }
    }
}
