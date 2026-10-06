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
        // --unread: a YMM4 build that is not one of the read builds (the ymm4-compat workflow). The cache checks run
        // with the features its contracts allow, as the plugin would use them.
        bool unread = args.Contains("--unread");
        bool known = HostIntegration.VerifyHost(host, out var hostReason);
        if (!unread) Check(known, "Host binary verification failed: " + hostReason);
        // An unread build runs every mode (the tachie checks too) with the features its contracts allow.
        HostFeatures? unreadFeatures = null;
        if (unread)
        {
            var evaluation = HostContracts.Evaluate(HostContracts.Describe(hostDir));
            Console.WriteLine($"Contracts verdict: same code as {evaluation.Baseline ?? "no read build"}; features: {string.Join(", ", evaluation.Features.Order(StringComparer.Ordinal))}");
            var problems = new SortedDictionary<string, string>(evaluation.Problems.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
            unreadFeatures = evaluation.Baseline is null ? null : HostIntegration.FeaturesFrom(evaluation, hostDir, problems);
            foreach (var (feature, problem) in problems) Console.WriteLine($"  off {feature}: {problem}");
            if (unreadFeatures is not null) HostFeatures.Decide(host, unreadFeatures);
        }
        if (args.Contains("--psd-duplicate-parser") || args.Contains("--psd-duplicate-source"))
        {
            PsdTachieChecks.RunDuplicate(host, parser: args.Contains("--psd-duplicate-parser"));
            return 0;
        }
        if (args.Contains("--psd-tachie-large"))
        {
            PsdTachieMeasurements.Run(host, large: true);
            return 0;
        }
        if (args.Contains("--psd-tachie-check"))
        {
            PsdTachieChecks.Run(host);
            return 0;
        }
        if (args.Contains("--psd-tachie-measure"))
        {
            PsdTachieMeasurements.Run(host);
            return 0;
        }
        if (args.Contains("--animation-tachie-large"))
        {
            AnimationTachieMeasurements.Run(host, large: true);
            return 0;
        }
        if (args.Contains("--animation-tachie-check"))
        {
            AnimationTachieChecks.Run(host);
            return 0;
        }
        if (args.Contains("--animation-tachie-measure"))
        {
            AnimationTachieMeasurements.Run(host);
            return 0;
        }
        if (args.Contains("--edit-description-check"))
        {
            EditDescriptionChecks.Run(host);
            return 0;
        }
        if (args.Contains("--edit-description-measure") || args.Contains("--edit-description-measure-incremental"))
        {
            EditDescriptionMeasurements.Run(host, args.Contains("--edit-description-measure-incremental"));
            return 0;
        }
        if (args.Contains("--idle-parallel-measure"))
        {
            IdleParallelMeasurements.Run(host);
            return 0;
        }
        if (args.Contains("--idle-parallel-check"))
        {
            IdleParallelMeasurements.RunChecks(host);
            return 0;
        }
        if (args.Contains("--gpu-first-revisit-measure"))
        {
            GpuFirstRevisitMeasurements.Run(host);
            return 0;
        }
        if (args.Contains("--gpu-retention-check"))
        {
            GpuRetentionChecks.Run(host);
            return 0;
        }
        if (args.Contains("--simple-tachie-measure"))
        {
            SimpleTachieMeasurements.Run(host);
            return 0;
        }
        if (args.Contains("--preview-performance"))
        {
            PreviewPerformanceChecks.Run(host);
            return 0;
        }
        if (args.Contains("--integration"))
        {
            ProcessingTraceChecks.Run();
            HostIntegrationChecks.Run(host);
            return 0;
        }
        HostFeatures? features;
        if (unread) features = unreadFeatures;
        else
        {
            HostIntegrationChecks.CheckContracts(host, hostDir);
            features = HostFeatures.For(host);
        }
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
            // The idle pre-renderer clones the project with Scenes(bool) (YMM4 4.49 and later); without it, it is off.
            if (features is { Preview: true } && HostCompat.ScenesTakeUndoFlag) IdleFramePreRendererChecks.Run();
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
        if (args.Contains("--gpu"))
        {
            if (features is null) Console.WriteLine("Cache checks skipped: the plugin does not use the cache on this build");
            else
            {
                int traceArg = Array.IndexOf(args, "--trace-output");
                bool trace = traceArg >= 0 && traceArg + 1 < args.Length;
                if (trace) { CacheTrace.Start(args[traceArg + 1], "host-gpu-checks"); ProcessingTraceHooks.Start(); ProcessingTraceHooks.Discover(); }
                TimelineFrameCache.GpuRetentionEnabled = false; // Existing RAM/disk regression counts stay isolated.
                try { FramePixelChecks.Run(host, video >= 0 && video + 1 < args.Length ? Path.GetFullPath(args[video + 1]) : null, features); }
                finally { if (trace) { ProcessingTraceHooks.Stop(); CacheTrace.StopAsync().GetAwaiter().GetResult(); } }
            }
        }
        return 0;
    }

    private static void GpuProbe(Assembly host, Assembly plugin, Type sourceType, MethodInfo update, object usage, FieldInfo collectorField)
    {
        using var devices = (IDisposable)Activator.CreateInstance(plugin.GetType("YukkuriMovieMaker.Commons.GraphicsDevices", true)!)!;
        using var context = (IDisposable)devices.GetType().GetMethod("CreateContext")!.Invoke(devices, null)!;
        var timeline = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Timeline", true)!)!;
        var scenesType = host.GetType("YukkuriMovieMaker.Project.Scenes", true)!;
        Console.WriteLine("Scenes constructors: " + string.Join("; ", scenesType.GetConstructors().Select(x => x.ToString())));
        var scenes = HostCompat.NewScenes();
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
// MediaFoundation and WIC readers so that video and image items decode through the real host readers.
internal static class ProbeLoader
{
    // Stands in for PluginAssemblyLoader's static constructor (skipped by the caller's Harmony prefix), which
    // would load plugins from the test executable's directory. 4.56.1.0 also reads IncompatiblePluginAssemblies.
    internal static void Stub(IEnumerable<Assembly> assemblies)
    {
        var loader = typeof(YukkuriMovieMaker.Plugin.PluginAssemblyLoader);
        AccessTools.StaticFieldRefAccess<IEnumerable<Assembly>>(AccessTools.Field(loader, "<Assemblies>k__BackingField"))() = assemblies;
        foreach (var name in new[] { "<IncompatiblePluginAssemblies>k__BackingField", "loadFailures" })
            if (AccessTools.Field(loader, name) is { } field) // init-only: FieldInfo.SetValue would throw
                AccessTools.StaticFieldRefAccess<object>(field)() ??= Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GetGenericArguments()))!;
    }

    // The MediaFoundation reader (videos) and the WIC readers (images and image sequences).
    internal static IEnumerable<Assembly> Assemblies(Assembly host)
    {
        var assemblies = new List<Assembly> { host, typeof(YukkuriMovieMaker.Plugin.CacheProvider).Assembly };
        foreach (string name in new[] { "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation", "YukkuriMovieMaker.Plugin.FileSource.WIC",
            "YukkuriMovieMaker.Plugin.Tachie.SimpleTachie" })
        {
            string reader = Path.Combine(Path.GetDirectoryName(host.Location)!, name + ".dll");
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (loaded is not null) assemblies.Add(loaded);
            else if (File.Exists(reader)) assemblies.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(reader));
        }
        return assemblies;
    }
}
