using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NVEncVideoWriterPlugin;

// Build-independent fingerprints of host code, read from the PE file (nothing is loaded or run). A type is
// described by its base type, interfaces, fields, serialization attributes and every method's IL, with
// metadata tokens replaced by the names they refer to, branch targets by instruction indices and
// compiler-generated ordinals by '#'; its compiler-generated nested types (lambdas, closures, state machines)
// are part of it. A type whose code did not change has the same fingerprint in every YMM4 build, so a new
// version can be compared with the versions whose code was read.
internal sealed partial class HostFingerprint : IDisposable
{
    internal const string Version = "il-v1";
    private static readonly Dictionary<ushort, OpCode> opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => (ushort)code.Value);
    // Attributes that change what is serialized (the cache key is the serialized project). UI attributes
    // (display names, editors) are left out so that text changes do not count as code changes.
    private static readonly string[] serializationNamespaces = ["Newtonsoft.Json", "System.Text.Json", "System.Runtime.Serialization"];

    private readonly PEReader pe;
    private readonly MetadataReader reader;
    private readonly Dictionary<string, TypeDefinitionHandle> types = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityHandle, string> names = [];
    private readonly NameProvider provider;

    internal HostFingerprint(string path) : this(File.OpenRead(path)) { }

    internal HostFingerprint(Stream stream)
    {
        pe = new PEReader(stream);
        try
        {
            reader = pe.GetMetadataReader();
            provider = new NameProvider(this);
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                if (IsCompilerGenerated(reader.GetString(definition.Name))) continue;
                types.TryAdd(TypeName(handle), handle);
            }
        }
        catch
        {
            pe.Dispose();
            throw;
        }
    }

    internal Guid Mvid => reader.GetGuid(reader.GetModuleDefinition().Mvid);
    internal string AssemblyName => reader.GetString(reader.GetAssemblyDefinition().Name);
    internal IEnumerable<string> TypeNames => types.Keys;
    internal bool Contains(string typeName) => types.ContainsKey(typeName);

    // Null when the assembly does not define the type.
    internal string? Hash(string typeName) => Describe(typeName) is { } text ? HashText(text) : null;

    internal string? Describe(string typeName)
    {
        if (!types.TryGetValue(typeName, out var handle)) return null;
        var text = new StringBuilder();
        DescribeType(handle, text);
        return text.ToString();
    }

    // Per member, for reports of what changed between two builds.
    internal SortedDictionary<string, string> MemberHashes(string typeName)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(TypeDefinitionHandle type, string prefix)
        {
            foreach (var (header, body) in Members(type))
            {
                string key = prefix + header;
                for (int i = 2; result.ContainsKey(key); i++) key = $"{prefix}{header} #{i}";
                result[key] = HashText(body);
            }
            foreach (var nested in reader.GetTypeDefinition(type).GetNestedTypes())
                if (IsCompilerGenerated(reader.GetString(reader.GetTypeDefinition(nested).Name)))
                    Add(nested, prefix + Normalize(reader.GetString(reader.GetTypeDefinition(nested).Name)) + "/");
        }
        if (types.TryGetValue(typeName, out var handle)) Add(handle, string.Empty);
        return result;
    }

    internal static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    public void Dispose() => pe.Dispose();

    private void DescribeType(TypeDefinitionHandle handle, StringBuilder text)
    {
        var definition = reader.GetTypeDefinition(handle);
        text.Append(Version).Append(" type ").Append(TypeName(handle))
            .Append(' ').Append(definition.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Sealed))
            .Append(" : ").Append(definition.BaseType.IsNil ? "-" : Name(definition.BaseType)).Append('\n');
        foreach (var name in definition.GetInterfaceImplementations().Select(i => Name(reader.GetInterfaceImplementation(i).Interface)).Order(StringComparer.Ordinal))
            text.Append("implements ").Append(name).Append('\n');
        AppendAttributes(definition.GetCustomAttributes(), text);
        foreach (var (header, body) in Members(handle).OrderBy(m => m.Header, StringComparer.Ordinal).ThenBy(m => m.Body, StringComparer.Ordinal))
            text.Append(header).Append('\n').Append(body);
        // Lambdas, closures, iterators and async state machines belong to the type that declares them.
        var nested = definition.GetNestedTypes().Where(n => IsCompilerGenerated(reader.GetString(reader.GetTypeDefinition(n).Name)))
            .Select(n => { var inner = new StringBuilder(); DescribeType(n, inner); return inner.ToString(); })
            .Order(StringComparer.Ordinal);
        foreach (var inner in nested) text.Append("nested {\n").Append(inner).Append("}\n");
    }

    private IEnumerable<(string Header, string Body)> Members(TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var body = new StringBuilder();
            if ((field.Attributes & FieldAttributes.HasDefault) != 0 && !field.GetDefaultValue().IsNil)
                body.Append("default ").Append(Convert.ToHexString(reader.GetBlobBytes(reader.GetConstant(field.GetDefaultValue()).Value))).Append('\n');
            AppendAttributes(field.GetCustomAttributes(), body);
            yield return ($"field {((field.Attributes & FieldAttributes.Static) != 0 ? "static " : string.Empty)}{Normalize(reader.GetString(field.Name))} : {field.DecodeSignature(provider, null)}", body.ToString());
        }
        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var signature = property.DecodeSignature(provider, null);
            var body = new StringBuilder();
            AppendAttributes(property.GetCustomAttributes(), body);
            yield return ($"property {Normalize(reader.GetString(property.Name))}({string.Join(",", signature.ParameterTypes)}) : {signature.ReturnType}", body.ToString());
        }
        foreach (var methodHandle in definition.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var flags = method.Attributes & (MethodAttributes.Static | MethodAttributes.Virtual | MethodAttributes.Abstract);
            yield return ($"method {flags} {Normalize(reader.GetString(method.Name))}{Signature(method.DecodeSignature(provider, null))}", Body(method));
        }
    }

    private string Body(MethodDefinition method)
    {
        var text = new StringBuilder();
        AppendAttributes(method.GetCustomAttributes(), text);
        if (method.RelativeVirtualAddress == 0) return text.Append("no body\n").ToString();
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil)
            text.Append("locals ").Append(string.Join(", ", reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(provider, null)))
                .Append(body.LocalVariablesInitialized ? " init" : string.Empty).Append('\n');

        // Pass 1: instruction offsets, so that branch targets and handler ranges become instruction indices.
        var instructions = new List<(int Offset, OpCode Code, long Operand, int[]? Targets)>();
        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            int offset = il.Offset;
            ushort value = il.ReadByte();
            if (value == 0xFE) value = (ushort)(0xFE00 | il.ReadByte());
            if (!opCodes.TryGetValue(value, out var code)) return text.Append("invalid IL at ").Append(offset).Append('\n').ToString();
            long operand = 0;
            int[]? targets = null;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: operand = il.ReadSByte(); break;
                case OperandType.ShortInlineI: operand = code.Value == OpCodes.Ldc_I4_S.Value ? il.ReadSByte() : il.ReadByte(); break;
                case OperandType.ShortInlineVar: operand = il.ReadByte(); break;
                case OperandType.InlineVar: operand = il.ReadUInt16(); break;
                case OperandType.InlineI8:
                case OperandType.InlineR: operand = il.ReadInt64(); break;
                case OperandType.InlineSwitch:
                    targets = new int[il.ReadInt32()];
                    for (int i = 0; i < targets.Length; i++) targets[i] = il.ReadInt32();
                    break;
                default: operand = il.ReadInt32(); break;
            }
            instructions.Add((offset, code, operand, targets));
        }
        var indexOf = new Dictionary<int, int>();
        for (int i = 0; i < instructions.Count; i++) indexOf[instructions[i].Offset] = i;
        int end = instructions.Count == 0 ? 0 : body.GetILBytes()!.Length;
        string Target(int offset) => indexOf.TryGetValue(offset, out int index) ? "#" + index : offset == end ? "#end" : "@" + offset;

        // Pass 2: the normalized listing.
        for (int i = 0; i < instructions.Count; i++)
        {
            var (offset, code, operand, targets) = instructions[i];
            int next = i + 1 < instructions.Count ? instructions[i + 1].Offset : end;
            text.Append(code.Name);
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget: text.Append(' ').Append(Target(next + (int)operand)); break;
                case OperandType.InlineSwitch: text.Append(' ').Append(string.Join(",", targets!.Select(t => Target(next + t)))); break;
                case OperandType.InlineString: text.Append(" \"").Append(reader.GetUserString(MetadataTokens.UserStringHandle((int)operand & 0xFFFFFF)).ReplaceLineEndings("\\n")).Append('"'); break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineSig: text.Append(' ').Append(Member(MetadataTokens.EntityHandle((int)operand))); break;
                default: text.Append(' ').Append(operand); break;
            }
            text.Append('\n');
        }
        foreach (var region in body.ExceptionRegions)
        {
            text.Append("region ").Append(region.Kind).Append(' ').Append(Target(region.TryOffset)).Append('-').Append(Target(region.TryOffset + region.TryLength))
                .Append(" handler ").Append(Target(region.HandlerOffset)).Append('-').Append(Target(region.HandlerOffset + region.HandlerLength));
            if (region.Kind == ExceptionRegionKind.Filter) text.Append(" filter ").Append(Target(region.FilterOffset));
            if (!region.CatchType.IsNil) text.Append(" catch ").Append(Name(region.CatchType));
            text.Append('\n');
        }
        return text.ToString();
    }

    private void AppendAttributes(CustomAttributeHandleCollection attributes, StringBuilder text)
    {
        foreach (var line in attributes.Select(reader.GetCustomAttribute).Select(attribute => (Type: AttributeType(attribute), attribute.Value))
            .Where(a => serializationNamespaces.Any(ns => a.Type.StartsWith(ns + ".", StringComparison.Ordinal))
                || a.Type == "System.ComponentModel.DefaultValueAttribute")
            .Select(a => $"attribute {a.Type} {Convert.ToHexString(reader.GetBlobBytes(a.Value))}").Order(StringComparer.Ordinal))
            text.Append(line).Append('\n');
    }

    private string AttributeType(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MethodDefinition => TypeName(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
        HandleKind.MemberReference => Name(reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
        _ => "?",
    };

    private string Member(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                return Name(handle);
            case HandleKind.FieldDefinition:
            {
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return $"{TypeName(field.GetDeclaringType())}::{Normalize(reader.GetString(field.Name))} : {field.DecodeSignature(provider, null)}";
            }
            case HandleKind.MethodDefinition:
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return $"{TypeName(method.GetDeclaringType())}::{Normalize(reader.GetString(method.Name))}{Signature(method.DecodeSignature(provider, null))}";
            }
            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                string parent = member.Parent.Kind switch
                {
                    HandleKind.MethodDefinition => Member(member.Parent),
                    HandleKind.ModuleReference => "module " + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)member.Parent).Name),
                    _ => Name(member.Parent),
                };
                string name = Normalize(reader.GetString(member.Name));
                return member.GetKind() == MemberReferenceKind.Field
                    ? $"{parent}::{name} : {member.DecodeFieldSignature(provider, null)}"
                    : $"{parent}::{name}{Signature(member.DecodeMethodSignature(provider, null))}";
            }
            case HandleKind.MethodSpecification:
            {
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return $"{Member(specification.Method)}<{string.Join(",", specification.DecodeSignature(provider, null))}>";
            }
            case HandleKind.StandaloneSignature:
                return "sig" + Signature(reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(provider, null));
            default:
                return handle.Kind.ToString();
        }
    }

    private static string Signature(MethodSignature<string> signature) =>
        $"{(signature.GenericParameterCount > 0 ? $"<{signature.GenericParameterCount}>" : string.Empty)}({string.Join(",", signature.ParameterTypes)}) : {signature.ReturnType}{(signature.Header.IsInstance ? string.Empty : " static")}";

    private string Name(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => TypeName((TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, null),
        _ => handle.Kind.ToString(),
    };

    private string TypeName(TypeDefinitionHandle handle)
    {
        if (names.TryGetValue(handle, out var cached)) return cached;
        var definition = reader.GetTypeDefinition(handle);
        string name = Normalize(reader.GetString(definition.Name));
        var declaring = definition.GetDeclaringType();
        string result = !declaring.IsNil ? TypeName(declaring) + "+" + name
            : definition.Namespace.IsNil ? name : reader.GetString(definition.Namespace) + "." + name;
        names[handle] = result;
        return result;
    }

    private string TypeName(TypeReferenceHandle handle)
    {
        if (names.TryGetValue(handle, out var cached)) return cached;
        var reference = reader.GetTypeReference(handle);
        string name = Normalize(reader.GetString(reference.Name));
        string result = reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? TypeName((TypeReferenceHandle)reference.ResolutionScope) + "+" + name
            : reference.Namespace.IsNil ? name : reader.GetString(reference.Namespace) + "." + name;
        names[handle] = result;
        return result;
    }

    private static bool IsCompilerGenerated(string name) => name.StartsWith('<');

    // "<Update>b__12_0" -> "<Update>b__#_#", "<<M>g__Local|3_1>d" -> "<<M>g__Local|#_#>d": compiler-generated
    // ordinals shift whenever another member of the type is added. The bodies they name are compared instead.
    internal static string Normalize(string name) => name.StartsWith('<') ? Ordinals().Replace(name, "#") : name;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Ordinals();

    private sealed class NameProvider(HostFingerprint owner) : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr" + Signature(signature);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";
        public string GetPinnedType(string elementType) => elementType + " pinned";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => owner.TypeName(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => owner.TypeName(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
