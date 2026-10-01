using System.Reflection;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

// Discover interface implementations, not effect names. Hooks are diagnostic observers only.
internal static class ProcessingTraceHooks
{
    private static readonly object gate = new();
    private static readonly Harmony harmony = new("ymm4.cache.processing-trace");
    private static readonly HashSet<MethodInfo> hooked = [];
    private static readonly HashSet<Assembly> scanned = [];
    private static Timer? timer;
    private static bool enabled;

    internal static void Start()
    {
        lock (gate)
        {
            if (enabled) return;
            enabled = true;
            timer = new Timer(_ => SafeDiscover(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }
    internal static void Stop()
    {
        lock (gate)
        {
            enabled = false;
            timer?.Dispose(); timer = null;
            foreach (var method in hooked) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            hooked.Clear(); scanned.Clear();
        }
    }
    private static void SafeDiscover()
    {
        try { Discover(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            using var failure = CacheTrace.Measure("processor-discovery", "coverage");
            if (failure is not null) { failure.Outcome = "error"; failure.Detail = error.GetType().Name; }
        }
    }
    internal static void Discover()
    {
        lock (gate)
        {
            if (!enabled) return;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || scanned.Contains(assembly)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { continue; }
                scanned.Add(assembly);
                foreach (var type in types)
                {
                    if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters) continue;
                    foreach (var method in Targets(type))
                    {
                        if (hooked.Contains(method)) continue;
                        if (Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                        {
                            using var existing = CacheTrace.Measure("processor-hook", "coverage", Identity(method));
                            if (existing is not null) existing.Outcome = "existing-detour-skipped";
                            continue;
                        }
                        try
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProcessingTraceHooks), nameof(Before)),
                                finalizer: new HarmonyMethod(typeof(ProcessingTraceHooks), nameof(After)));
                            hooked.Add(method);
                            using var registration = CacheTrace.Measure("processor-hook", "coverage", Identity(method));
                        }
                        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                        {
                            try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); } catch { }
                            using var failure = CacheTrace.Measure("processor-hook", "coverage", Identity(method));
                            if (failure is not null) { failure.Outcome = "unhookable"; failure.Detail = error.GetType().Name; }
                        }
                    }
                }
            }
        }
    }

    internal static IEnumerable<MethodInfo> Targets(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            // Decoder and timeline hooks have readiness ownership contracts. Their built-in instrumentation
            // records them; adding an observer detour could change late-load readiness verdicts.
            if (contract.FullName?.Contains(".FileSource.", StringComparison.Ordinal) == true
                || contract.Name.Contains("Timeline", StringComparison.Ordinal)) continue;
            if (!(contract.Namespace?.StartsWith("YukkuriMovieMaker", StringComparison.Ordinal) ?? false)
                || !(contract.Name.Contains("Processor", StringComparison.Ordinal) || contract.Name.Contains("Source", StringComparison.Ordinal))) continue;
            InterfaceMapping map;
            try { map = type.GetInterfaceMap(contract); } catch (ArgumentException) { continue; }
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var name = map.InterfaceMethods[i].Name;
                var method = map.TargetMethods[i];
                if (name is not ("Update" or "Draw" or "Read" or "GetFrame" or "GetFrameAsync")) continue;
                if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() is not null)
                    yield return method;
            }
        }
    }
    private static string Identity(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}|{method.Module.ModuleVersionId:D}|{method.MetadataToken}";
    private static void Before(MethodBase __originalMethod, object __instance, out CacheTrace.Span? __state) =>
        __state = CacheTrace.Measure("processor-call", __originalMethod is MethodInfo method && typeof(Task).IsAssignableFrom(method.ReturnType) ? "async-submit" : "cpu-wall", __instance.GetType().FullName + "|" + Identity(__originalMethod));
    private static Exception? After(Exception? __exception, CacheTrace.Span? __state)
    {
        if (__state is not null)
        {
            if (__exception is not null) { __state.Outcome = "exception"; __state.Detail = __exception.GetType().Name; }
            __state.Dispose();
        }
        return __exception;
    }
}
