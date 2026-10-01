using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using NVEncVideoWriterPlugin;

// Test-only: host binaries are loaded in place, never modified or redistributed.
internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int updates, updatePostfixes, disposals;
    private static bool skip = true;
    private static object? replacement;
    private static FieldInfo commandList = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        var hostDir = Path.GetFullPath(args.FirstOrDefault() ?? @"D:\YukkuriMovieMaker_v4_Lite");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(hostDir, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDir, "YukkuriMovieMaker.dll"));
        var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDir, "YukkuriMovieMaker.Plugin.dll"));
        Check(HostIntegration.VerifyHost(host, out var hostReason), "Host binary verification failed: " + hostReason);
        var sourceType = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
        var update = sourceType.GetMethods(All).Single(m => m.Name == "Update" && m.GetParameters().Length == 2);
        var dispose = sourceType.GetMethod("Dispose", All, [typeof(bool)])!;
        commandList = sourceType.GetField("commandList", All)!;
        var collectorField = sourceType.GetField("disposer", All)!;
        Check(update.GetParameters()[0].ParameterType == typeof(TimeSpan), "Update time signature changed");
        Check(update.GetParameters()[1].ParameterType.FullName == "YukkuriMovieMaker.Player.Video.TimelineSourceUsage", "Update usage signature changed");
        Check(commandList.FieldType.FullName == "Vortice.Direct2D1.ID2D1CommandList", "Output field signature changed");
        Check(collectorField.FieldType.FullName == "YukkuriMovieMaker.Commons.DisposeCollector", "Collector signature changed");
        Check(collectorField.FieldType.GetMethod("Collect", [typeof(IDisposable)]) != null, "Collect missing");
        Check(collectorField.FieldType.GetMethod("Remove", [typeof(IDisposable)]) != null, "Remove missing");
        Console.WriteLine($"Host: {host.GetName().Version}; MVID={host.ManifestModule.ModuleVersionId}");

        var harmony = new Harmony("ymm4.tests.host-cache-probe");
        try
        {
            ExportScopeChecks.Run(host, harmony);
            IdleFramePreRendererChecks.Run();
            harmony.Patch(update, new HarmonyMethod(typeof(Program), nameof(UpdatePrefix)), new HarmonyMethod(typeof(Program), nameof(UpdatePostfix)));
            harmony.Patch(dispose, new HarmonyMethod(typeof(Program), nameof(DisposePrefix)));
            var uninitialized = RuntimeHelpers.GetUninitializedObject(sourceType);
            var usage = Enum.Parse(update.GetParameters()[1].ParameterType, "Exporting");
            update.Invoke(uninitialized, [TimeSpan.Zero, usage]);
            dispose.Invoke(uninitialized, [true]);
            Check(updates == 1 && updatePostfixes == 1 && disposals == 1, "Actual target hooks did not run");
            Console.WriteLine("Actual TimelineSource.Update + Dispose(bool) detour/prefix/postfix/skip OK");
            if (args.Contains("--gpu")) GpuProbe(host, plugin, sourceType, update, usage, collectorField);
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Check(Harmony.GetPatchInfo(update)?.Owners.Contains(harmony.Id) != true, "Update patch was not removed");
        Check(Harmony.GetPatchInfo(dispose)?.Owners.Contains(harmony.Id) != true, "Dispose patch was not removed");
        var afterUnpatch = RuntimeHelpers.GetUninitializedObject(sourceType);
        var beforeUnpatchCall = disposals;
        dispose.Invoke(afterUnpatch, [false]);
        Check(disposals == beforeUnpatchCall && (bool)sourceType.GetField("disposedValue", All)!.GetValue(afterUnpatch)!,
            "Unpatch did not restore the original Dispose method");
        Console.WriteLine("Patch/unpatch and reflection contracts OK");
        int video = Array.IndexOf(args, "--video");
        if (args.Contains("--gpu")) FramePixelChecks.Run(host, video >= 0 && video + 1 < args.Length ? Path.GetFullPath(args[video + 1]) : null);
        return 0;
    }

    private static void GpuProbe(Assembly host, Assembly plugin, Type sourceType, MethodInfo update, object usage, FieldInfo collectorField)
    {
        using var devices = (IDisposable)Activator.CreateInstance(plugin.GetType("YukkuriMovieMaker.Commons.GraphicsDevices", true)!)!;
        using var context = (IDisposable)devices.GetType().GetMethod("CreateContext")!.Invoke(devices, null)!;
        var timeline = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Timeline", true)!)!;
        var scenesType = host.GetType("YukkuriMovieMaker.Project.Scenes", true)!;
        Console.WriteLine("Scenes constructors: " + string.Join("; ", scenesType.GetConstructors().Select(x => x.ToString())));
        var scenes = Activator.CreateInstance(scenesType, [false])!;
        var scene = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Scene", true)!, [timeline, scenes, Array.Empty<Guid>()])!;
        using var source = (IDisposable)Activator.CreateInstance(sourceType, All, null, [context, scene, null], null)!;
        skip = false;
        update.Invoke(source, [TimeSpan.Zero, usage]);
        var original = (IDisposable)commandList.GetValue(source)!;
        var deviceContext = context.GetType().GetProperty("DeviceContext")!.GetValue(context)!;
        replacement = deviceContext.GetType().GetMethod("CreateCommandList")!.Invoke(deviceContext, null)!;
        replacement.GetType().GetMethod("Close")!.Invoke(replacement, null);
        var collector = collectorField.GetValue(source)!;
        collector.GetType().GetMethod("Remove", [typeof(IDisposable)])!.Invoke(collector, [original]);
        collector.GetType().GetMethod("Collect", [typeof(IDisposable)])!.Invoke(collector, [replacement]);
        original.Dispose();
        skip = true;
        update.Invoke(source, [TimeSpan.FromMilliseconds(33), usage]);
        Check(ReferenceEquals(sourceType.GetProperty("Output")!.GetValue(source), replacement), "Output getter did not expose substituted command list");
        // The replacement is source-owned: next normal Update must dispose it once.
        var previous = replacement;
        replacement = null;
        skip = false;
        update.Invoke(source, [TimeSpan.FromMilliseconds(66), usage]);
        Check((nint)previous.GetType().GetProperty("NativePointer")!.GetValue(previous)! == 0, "Normal update leaked substituted command list");
        var finalCommandList = commandList.GetValue(source)!;
        var beforeDisposal = disposals;
        source.Dispose();
        Check(disposals == beforeDisposal + 1 && (nint)finalCommandList.GetType().GetProperty("NativePointer")!.GetValue(finalCommandList)! == 0,
            "Actual Dispose hook did not allow normal GPU cleanup");
        Console.WriteLine("Real GPU empty Scene render + Output substitution + next Update/Dispose release OK");
    }

    private static bool UpdatePrefix(object __instance)
    {
        updates++;
        if (skip && replacement != null) commandList.SetValue(__instance, replacement);
        return !skip;
    }
    private static void UpdatePostfix() => updatePostfixes++;
    private static bool DisposePrefix() { disposals++; return !skip; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

// The probes bypass the plugin loader; they register the host, the plugin API and the built-in
// MediaFoundation reader so that video items decode through the real host readers.
internal static class ProbeLoader
{
    internal static IEnumerable<Assembly> Assemblies(Assembly host)
    {
        string reader = Path.Combine(Path.GetDirectoryName(host.Location)!, "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.dll");
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == Path.GetFileNameWithoutExtension(reader));
        var assemblies = new List<Assembly> { host, typeof(YukkuriMovieMaker.Plugin.CacheProvider).Assembly };
        if (loaded is not null) assemblies.Add(loaded);
        else if (File.Exists(reader)) assemblies.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(reader));
        return assemblies;
    }
}
