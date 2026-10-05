using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Lowers the version of one assembly reference in a built assembly, in place:
//   dotnet HostReference.dll <assembly> <referenced assembly name> <version>
// YMM4 does not load a plugin that references a newer YukkuriMovieMaker.dll than its own (it reports that the files it
// needs could not be loaded), and that assembly's version is YMM4's version. The plugin is built against the YMM4 whose
// code was read and its reference is lowered to the oldest YMM4 it supports (HostReferenceVersion.targets); HostApi
// guards what that YMM4 lacks, and CI checks the result against it (HostLoadChecks, HostFingerprint api).
if (args.Length != 3 || !Version.TryParse(args[2], out var target))
{
    Console.Error.WriteLine("usage: HostReference <assembly> <reference name> <version>");
    return 2;
}
string path = args[0], name = args[1];
byte[] bytes = File.ReadAllBytes(path);
int position = -1;
Version? current = null;
using (var pe = new PEReader(new MemoryStream(bytes)))
{
    var reader = pe.GetMetadataReader();
    foreach (var handle in reader.AssemblyReferences)
    {
        var reference = reader.GetAssemblyReference(handle);
        if (reader.GetString(reference.Name) != name) continue;
        current = reference.Version;
        position = pe.PEHeaders.MetadataStartOffset + reader.GetTableMetadataOffset(TableIndex.AssemblyRef)
            + (MetadataTokens.GetRowNumber(handle) - 1) * reader.GetTableRowSize(TableIndex.AssemblyRef);
    }
}
if (current is null)
{
    Console.WriteLine($"{Path.GetFileName(path)} does not reference {name}.");
    return 0;
}
if (current == target) return 0;
if (current < target)
{
    Console.Error.WriteLine($"{Path.GetFileName(path)} references {name} {current}, older than {target}.");
    return 1;
}
// An AssemblyRef row starts with the version: major, minor, build and revision, two bytes each, little endian.
ushort[] parts = [(ushort)target.Major, (ushort)target.Minor, (ushort)Math.Max(0, target.Build), (ushort)Math.Max(0, target.Revision)];
for (int i = 0; i < 4; i++) BitConverter.TryWriteBytes(bytes.AsSpan(position + 2 * i, 2), parts[i]);
using (var check = new PEReader(new MemoryStream(bytes)))
{
    var reader = check.GetMetadataReader();
    if (!reader.AssemblyReferences.Select(reader.GetAssemblyReference).Any(r => reader.GetString(r.Name) == name && r.Version == target))
    {
        Console.Error.WriteLine("The rewritten reference does not read back.");
        return 1;
    }
}
File.WriteAllBytes(path, bytes);
Console.WriteLine($"{Path.GetFileName(path)}: reference to {name} lowered from {current} to {target}.");
return 0;
