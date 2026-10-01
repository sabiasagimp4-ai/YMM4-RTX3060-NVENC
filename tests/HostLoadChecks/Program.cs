using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

internal static class Program
{
    private const string WriterPluginInterface = "YukkuriMovieMaker.Plugin.FileWriter.IVideoFileWriterPlugin";

    private static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetBaseException().Message);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("usage: HostLoadChecks.exe <hostDir> <YMM4Rtx3060Nvenc.dll>");

        var hostDirectory = Path.GetFullPath(args[0]);
        var pluginPath = Path.GetFullPath(args[1]);
        var pluginDirectory = Path.GetDirectoryName(pluginPath)!;
        var hostPath = Path.Combine(hostDirectory, "YukkuriMovieMaker.dll");
        if (!Directory.Exists(hostDirectory) || !File.Exists(hostPath) || !File.Exists(pluginPath))
            throw new FileNotFoundException("The host directory, YukkuriMovieMaker.dll, or plugin DLL is missing.");

        var searchDirectories = new[] { hostDirectory, pluginDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var directory in searchDirectories)
            {
                var dependencyPath = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(dependencyPath))
                    return context.LoadFromAssemblyPath(dependencyPath);
            }
            return null;
        };

        var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(hostPath);
        Console.WriteLine($"Host: {host.GetName().Name}, {host.GetName().Version}");

        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        CheckHostReferenceVersions(plugin, hostDirectory);

        Type[] types;
        try { types = plugin.GetTypes(); }
        catch (ReflectionTypeLoadException error)
        {
            var details = string.Join(Environment.NewLine, error.LoaderExceptions
                .Where(exception => exception is not null)
                .Select(exception => exception!.GetBaseException().Message));
            throw new InvalidOperationException("Plugin types could not be loaded:" + Environment.NewLine + details, error);
        }

        var entries = types.Where(type => !type.IsAbstract && !type.IsInterface
                && type.GetInterfaces().Any(contract => contract.FullName == WriterPluginInterface))
            .ToArray();
        if (entries.Length != 1 || entries[0].FullName != "NVEncVideoWriterPlugin.NvencVideoFileWriterPlugin")
            throw new InvalidOperationException($"Expected one NVENC writer plugin entry; found {string.Join(", ", entries.Select(type => type.FullName))}.");

        var pluginApi = AssemblyLoadContext.Default.Assemblies
            .SingleOrDefault(assembly => assembly.GetName().Name == "YukkuriMovieMaker.Plugin")
            ?? throw new InvalidOperationException("YukkuriMovieMaker.Plugin was not loaded from the target host.");
        var expectedPluginApiPath = Path.GetFullPath(Path.Combine(hostDirectory, "YukkuriMovieMaker.Plugin.dll"));
        if (!string.Equals(Path.GetFullPath(pluginApi.Location), expectedPluginApiPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Plugin API resolved outside the target host directory: {pluginApi.Location}");

        Console.WriteLine($"Loaded {plugin.GetName().Name} types and {entries[0].FullName} interface entry against {pluginApi.GetName().Version}.");
    }

    private static void CheckHostReferenceVersions(Assembly plugin, string hostDirectory)
    {
        foreach (var reference in plugin.GetReferencedAssemblies())
        {
            var hostDependencyPath = Path.Combine(hostDirectory, reference.Name + ".dll");
            if (!File.Exists(hostDependencyPath)) continue;

            var availableVersion = AssemblyName.GetAssemblyName(hostDependencyPath).Version;
            if (reference.Version is { } requestedVersion && availableVersion is { } hostVersion && requestedVersion > hostVersion)
                throw new FileLoadException(
                    $"Plugin requires {reference.Name} {requestedVersion}, newer than the host copy {hostVersion}. Build the plugin against this YMM4 version.",
                    reference.Name);
        }
    }
}
