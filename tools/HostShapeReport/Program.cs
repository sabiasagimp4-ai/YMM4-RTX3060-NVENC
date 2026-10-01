using System.Reflection;
using System.Runtime.InteropServices;
using NVEncVideoWriterPlugin;

// Read-only shape report for the host types the plugin hooks. It loads metadata only
// (MetadataLoadContext): no host code runs and YMM4 is never started. The output lists names and
// signatures, not implementation, so it can be shared to verify the plugin's assumptions.
//   dotnet run --project tools/HostShapeReport -- 'D:\YukkuriMovieMaker_v4_Lite' > host-shapes.txt
//   dotnet run --project tools/HostShapeReport -- <dir> --assemblies a.dll b.dll   (explicit candidates)
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

if (args.Length == 0)
{
    Console.WriteLine("Usage: HostShapeReport <YMM4 directory> [--assemblies <file>...]");
    return 2;
}
string hostDirectory = Path.GetFullPath(args[0]);
int explicitIndex = Array.IndexOf(args, "--assemblies");
string[] candidates = explicitIndex >= 0
    ? args[(explicitIndex + 1)..].Select(Path.GetFullPath).ToArray()
    : new[] { "YukkuriMovieMaker.dll", "YukkuriMovieMaker.Plugin.dll" }.Select(name => Path.Combine(hostDirectory, name))
        .Concat(Directory.GetFiles(hostDirectory, "YukkuriMovieMaker.Plugin.FileSource.*.dll")).Where(File.Exists).ToArray();

// Resolve against the host folder and the shared frameworks (WPF types live in WindowsDesktop).
string runtime = RuntimeEnvironment.GetRuntimeDirectory();
string? shared = Path.GetDirectoryName(Path.GetDirectoryName(runtime.TrimEnd(Path.DirectorySeparatorChar)));
var frameworkDirectories = new List<string> { runtime };
if (shared is not null && Directory.Exists(Path.Combine(shared, "Microsoft.WindowsDesktop.App")))
    frameworkDirectories.AddRange(Directory.GetDirectories(Path.Combine(shared, "Microsoft.WindowsDesktop.App")).OrderDescending().Take(1));
var resolverPaths = frameworkDirectories.SelectMany(directory => Directory.GetFiles(directory, "*.dll"))
    .Concat(Directory.GetFiles(hostDirectory, "*.dll")).Concat(candidates)
    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(group => group.Last());
using var context = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));

var assemblies = candidates.Select(context.LoadFromAssemblyPath).ToArray();
var types = new List<Type>();
foreach (var assembly in assemblies)
{
    Console.WriteLine($"# {assembly.GetName().Name} {assembly.GetName().Version} MVID={assembly.ManifestModule.ModuleVersionId}");
    try { types.AddRange(assembly.GetTypes()); }
    catch (ReflectionTypeLoadException error)
    {
        types.AddRange(error.Types.OfType<Type>());
        Console.WriteLine($"  (only {error.Types.OfType<Type>().Count()} types loadable; the plugin's strict scan would reject this assembly)");
    }
}

var interfaces = types.Where(type => type.IsInterface && type.Name == "IVideoFileSource").ToArray();
Console.WriteLine();
Console.WriteLine($"## IVideoFileSource: {string.Join(", ", interfaces.Select(type => type.FullName))}");
if (interfaces.Length == 1)
{
    var videoSource = interfaces[0];
    foreach (var method in new[] { videoSource }.Concat(videoSource.GetInterfaces()).SelectMany(type => type.GetMethods()))
        Console.WriteLine($"  {Signature(method)}");
    foreach (var type in types.Where(type => ShapeRules.IsImplementation(type, videoSource)).OrderBy(type => type.FullName))
    {
        Console.WriteLine();
        Console.WriteLine($"### {type.FullName} [{type.Assembly.GetName().Name}] => predicted: {ShapeRules.Predict(type, videoSource)}");
        Console.WriteLine($"  bases: {string.Join(" -> ", Bases(type))}");
        DumpFields(type, "  ");
        foreach (var update in HierarchyMethods(type).Where(method => method.Name.EndsWith("Update", StringComparison.Ordinal)))
            Console.WriteLine($"  method {Signature(update)}  [declared in {update.DeclaringType!.Name}]");
        if (ShapeRules.FindField(type, "decodedFrame") is { } frame)
        {
            Console.WriteLine($"  decodedFrame type {frame.FieldType.FullName}:");
            DumpFields(frame.FieldType, "    ");
            DumpProperties(frame.FieldType, "    ");
        }
        if (type.Name == "CachedVideoFileSource")
            Console.WriteLine($"  wrapped source: {(ShapeRules.WrappedSource(type, videoSource) is { } source ? $"resource.{source.Name}: {TypeName(source.PropertyType)}" : "not found")}");
    }
}

foreach (string name in new[]
{
    "YukkuriMovieMaker.Player.Video.TimelineSource",
    "YukkuriMovieMaker.Player.Video.TimelineSourceAndDevices",
    "YukkuriMovieMaker.Player.Video.CompositeItemPicker",
    "YukkuriMovieMaker.Player.TimelineVideoPlayer",
    "YukkuriMovieMaker.VideoFileWriter.VideoFileWriter",
    "YukkuriMovieMaker.Plugin.CacheProvider",
})
{
    Console.WriteLine();
    var type = types.FirstOrDefault(candidate => candidate.FullName == name);
    if (type is null) { Console.WriteLine($"## {name}: not found"); continue; }
    Console.WriteLine($"## {name}");
    DumpFields(type, "  ");
    DumpProperties(type, "  ");
    foreach (var method in type.GetMethods(All).Where(method => !method.IsSpecialName).OrderBy(method => method.Name))
        Console.WriteLine($"  method {Signature(method)}");
    // Async methods keep their locals (loop counters, ranges) in compiler-generated state machines.
    foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Where(nested => nested.Name.Contains("CreateFileAsync")))
    {
        Console.WriteLine($"  state machine {nested.Name}:");
        DumpFields(nested, "    ");
    }
    if (name.EndsWith(".VideoFileWriter", StringComparison.Ordinal) && ShapeRules.FindField(type, "settings") is { } settings)
    {
        Console.WriteLine($"  settings type {settings.FieldType.FullName}:");
        DumpProperties(settings.FieldType, "    ");
    }
}

Console.WriteLine();
Console.WriteLine("## Decompile for verification (ilspycmd -t <type>): the IVideoFileSource implementations above,");
Console.WriteLine("## TimelineSource.Update, TimelineVideoPlayer.Draw, VideoFileWriter.CreateFileAsync, CacheProvider.");
return 0;

static IEnumerable<string> Bases(Type type)
{
    for (var value = type; value is not null; value = value.BaseType) yield return value.FullName ?? value.Name;
}

static IEnumerable<MethodInfo> HierarchyMethods(Type type)
{
    for (var value = type; value is not null && value.FullName != "System.Object"; value = value.BaseType)
        foreach (var method in value.GetMethods(All)) yield return method;
}

static void DumpFields(Type type, string indent)
{
    for (var value = type; value is not null && value.FullName != "System.Object"; value = value.BaseType)
        foreach (var field in value.GetFields(All).Where(field => !field.IsStatic).OrderBy(field => field.Name))
            Console.WriteLine($"{indent}field {value.Name}.{field.Name}: {TypeName(field.FieldType)}");
}

static void DumpProperties(Type type, string indent)
{
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .OrderBy(property => property.Name))
        Console.WriteLine($"{indent}property {property.Name}: {TypeName(property.PropertyType)}{(property.SetMethod is null ? " (get)" : " (get/set)")}");
}

static string Signature(MethodInfo method) =>
    $"{(method.IsStatic ? "static " : string.Empty)}{TypeName(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(parameter => $"{TypeName(parameter.ParameterType)} {parameter.Name}"))})";

static string TypeName(Type type)
{
    try { return type.FullName ?? type.Name; }
    catch (Exception) { return type.Name; }
}
