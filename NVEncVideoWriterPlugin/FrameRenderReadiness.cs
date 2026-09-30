using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

// The host renders decoder timeouts/errors as transparent output and still returns normally from
// TimelineSource.Update, so a normal return is not proof that a frame is complete. A frame may only be
// stored when every decoder update attributed to its Update call verified that it holds the requested
// sample. Anything unverifiable (unknown source type, exception, broken attribution) fails closed.
internal static class FrameRenderReadiness
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly AsyncLocal<Scope?> current = new();
    private static readonly ConcurrentDictionary<Scope, byte> active = new();
    private static readonly ConditionalWeakTable<object, UpdateResult> lastResults = new();
    private static Dictionary<RuntimeMethodHandle, DecoderCheck> decoders = [];
    private static (MethodBase Target, MethodInfo Patch)[] installedPatches = [];
    private static readonly object patchGate = new();
    private static string[] coverage = [];
    private static HostBinding? binding;
    private static string? coverageProblem;
    private static int installed;
    // Odd while a late-loaded video source is being hooked. A frame is verified only if the epoch it
    // started in is even and still current, so no frame spanning a hook change counts as ready.
    private static int bindEpoch;

    // Returns whether the decoded frame the decoder now holds covers the requested time.
    internal sealed record DecoderCheck(string Name, MethodBase Update, Func<object, TimeSpan, bool> HoldsFrame);

    private sealed class Scope(object source, TimeSpan time, Scope? parent, int epoch)
    {
        internal readonly object Source = source;
        internal readonly TimeSpan Time = time;
        internal readonly Scope? Parent = parent;
        internal readonly int Epoch = epoch;
        internal int Failed;
        internal int Completed;
    }

    private sealed record UpdateResult(bool Ready, TimeSpan Time);

    private sealed class HostBinding(Harmony harmony, Type videoSource, MethodInfo interfaceUpdate, string hostDirectory)
    {
        internal readonly object Gate = new();
        internal readonly Harmony Harmony = harmony;
        internal readonly Type VideoSource = videoSource;
        internal readonly MethodInfo InterfaceUpdate = interfaceUpdate;
        internal readonly string HostDirectory = hostDirectory;
        internal readonly HashSet<string> Assemblies = new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<Type, Func<object, TimeSpan, bool>> Verified = new();
        internal readonly ConcurrentDictionary<Type, Func<object, TimeSpan, bool>> Classifiers = new();
        internal readonly List<string> Coverage = [];

        internal bool HoldsFrame(object instance, TimeSpan time) =>
            Classifiers.TryGetValue(instance.GetType(), out var holds) && holds(instance, time);
    }

    internal static bool Installed => Volatile.Read(ref installed) != 0;

    // Human-readable classification of every hooked host video source (probe/status output).
    internal static IReadOnlyList<string> Coverage => Volatile.Read(ref coverage);

    // Non-null once a video source implementation could not be hooked; no frame is ready after that.
    internal static string? CoverageProblem => Volatile.Read(ref coverageProblem);

    internal static string Summary
    {
        get
        {
            if (!Installed) return "動画ソースの完成判定は接続されていません。";
            var lines = Coverage;
            int unverified = lines.Count(line => line.Contains(": unverified", StringComparison.Ordinal));
            return CoverageProblem ?? $"動画ソースの完成判定: 検証可能 {lines.Count - unverified} 種 / 未検証 {unverified} 種（未検証の動画を含むフレームは保存しません）";
        }
    }

    // For TimelineSource.Update postfixes: the scope is still open (finalizers run after postfixes).
    internal static bool IsUpdateReady(object timelineSource) => Installed && CoverageProblem is null
        && current.Value is { } scope && ReferenceEquals(scope.Source, timelineSource)
        && Volatile.Read(ref scope.Completed) == 0 && Volatile.Read(ref scope.Failed) == 0 && Stable(scope);

    private static bool Stable(Scope scope) => (scope.Epoch & 1) == 0 && scope.Epoch == Volatile.Read(ref bindEpoch);

    // For callers that inspect a source after its Update returned (idle pre-render).
    internal static bool WasLastUpdateReady(object timelineSource, TimeSpan time) => Installed && CoverageProblem is null
        && lastResults.TryGetValue(timelineSource, out var result) && result.Ready && result.Time == time;

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        if (Installed)
        {
            reason = "動画の完成判定は既に接続されています。";
            return false;
        }
        try
        {
            var sourceType = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
            var update = sourceType.GetMethods(Instance).Single(m => m.Name == "Update" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(TimeSpan) && m.ReturnType == typeof(void));
            var hostBinding = CreateBinding(host, harmony, out var types);
            if (!TryInstall(update, Classify(hostBinding, types), harmony, out reason))
            {
                Volatile.Write(ref coverage, []);
                return false;
            }
            Volatile.Write(ref binding, hostBinding);
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            // Close the window between scanning loaded assemblies and subscribing.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) NoteAssemblyLoaded(assembly);
            return true;
        }
        catch (Exception error)
        {
            Volatile.Write(ref coverage, []);
            reason = "動画の完成判定を接続できません: " + error.GetBaseException().Message;
            return false;
        }
    }

    // Installs only when every hook applies; on failure removes exactly the patches added here.
    internal static bool TryInstall(MethodBase timelineUpdate, IReadOnlyList<DecoderCheck> checks, Harmony harmony, out string reason)
    {
        if (Installed)
        {
            // Never touch the live install from a rejected call.
            reason = "動画の完成判定は既に接続されています。";
            return false;
        }
        var added = new List<(MethodBase Target, MethodInfo Patch)>();
        try
        {
            if (checks.Count == 0) throw new NotSupportedException("No verified decoder contracts.");
            if (timelineUpdate.GetParameters() is not [{ ParameterType: var timeType }, _] || timeType != typeof(TimeSpan))
                throw new NotSupportedException("TimelineSource.Update(TimeSpan, usage) contract changed");
            foreach (var check in checks)
                if (check.Update.IsStatic || check.Update.IsAbstract || check.Update.GetParameters() is not [{ ParameterType: var t }]
                    || t != typeof(TimeSpan))
                    throw new NotSupportedException($"{check.Name}.Update(TimeSpan) contract changed");
            var targets = checks.Select(c => c.Update).Append(timelineUpdate).ToArray();
            if (targets.Select(t => t.MethodHandle).Distinct().Count() != targets.Length)
                throw new NotSupportedException("Duplicate readiness hook targets");
            foreach (var target in targets) EnsureNoExternalHarmonyOwners(target, harmony.Id);

            // Published before the first hook so no decoder update can observe a partial table.
            Volatile.Write(ref decoders, checks.ToDictionary(check => check.Update.MethodHandle, check => check));
            Volatile.Write(ref coverageProblem, null);
            var prefix = Method(nameof(UpdatePrefix));
            var finalizer = Method(nameof(UpdateFinalizer));
            harmony.Patch(timelineUpdate, prefix: new HarmonyMethod(prefix, Priority.First));
            added.Add((timelineUpdate, prefix));
            harmony.Patch(timelineUpdate, finalizer: new HarmonyMethod(finalizer, Priority.Last));
            added.Add((timelineUpdate, finalizer));
            var decoderFinalizer = Method(nameof(DecoderFinalizer));
            foreach (var check in checks)
            {
                harmony.Patch(check.Update, finalizer: new HarmonyMethod(decoderFinalizer, Priority.Last));
                added.Add((check.Update, decoderFinalizer));
            }
            lock (patchGate) installedPatches = [.. added];
            Volatile.Write(ref installed, 1);
            reason = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            for (int i = added.Count - 1; i >= 0; i--)
                try { harmony.Unpatch(added[i].Target, added[i].Patch); } catch { }
            Volatile.Write(ref decoders, []);
            reason = "動画の完成判定を接続できません: " + error.GetBaseException().Message;
            return false;
        }
    }

    // Removes exactly the patches added by the successful install (offline checks and host rollback).
    internal static void Uninstall(Harmony harmony)
    {
        Volatile.Write(ref installed, 0);
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        (MethodBase Target, MethodInfo Patch)[] patches;
        lock (patchGate) { patches = installedPatches; installedPatches = []; }
        for (int i = patches.Length - 1; i >= 0; i--)
            try { harmony.Unpatch(patches[i].Target, patches[i].Patch); } catch { }
        Volatile.Write(ref decoders, []);
        Volatile.Write(ref coverage, []);
        Volatile.Write(ref binding, null);
        Volatile.Write(ref coverageProblem, null);
        active.Clear();
    }

    private static MethodInfo Method(string name) =>
        typeof(FrameRenderReadiness).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void EnsureNoExternalHarmonyOwners(MethodBase target, string ownId)
    {
        var owners = Harmony.GetPatchInfo(target)?.Owners.Where(owner => owner != ownId).ToArray() ?? [];
        if (owners.Length != 0)
            throw new NotSupportedException($"Unverified Harmony owners on {target.DeclaringType?.FullName}.{target.Name}: {string.Join(", ", owners)}");
    }

    private static void UpdatePrefix(object __instance, object[] __args, out Scope? __state)
    {
        __state = null;
        if (!Installed || __args is not [TimeSpan time, ..]) return;
        var scope = new Scope(__instance, time, current.Value, Volatile.Read(ref bindEpoch));
        active[scope] = 0;
        current.Value = scope;
        __state = scope;
    }

    private static void UpdateFinalizer(object __instance, Exception? __exception, Scope? __state)
    {
        if (__state is null) return;
        try
        {
            if (__exception is not null) Fail(__state);
            Volatile.Write(ref __state.Completed, 1);
            active.TryRemove(__state, out _);
            lastResults.AddOrUpdate(__instance, new UpdateResult(Volatile.Read(ref __state.Failed) == 0 && Stable(__state), __state.Time));
        }
        catch { Fail(__state); lastResults.Remove(__instance); }
        finally { current.Value = __state.Parent; }
    }

    private static void DecoderFinalizer(object __instance, object[] __args, MethodBase __originalMethod, Exception? __exception)
    {
        var scope = current.Value;
        if (scope is null || Volatile.Read(ref scope.Completed) != 0)
        {
            // Decode outside any open render scope. If a render is in flight, attribution may have been
            // lost (execution context not flowed), so every in-flight frame is treated as unverified.
            if (!active.IsEmpty) FailAllActive();
            return;
        }
        bool holds;
        try
        {
            holds = __exception is null && __args is [TimeSpan time] && Volatile.Read(ref decoders).TryGetValue(__originalMethod.MethodHandle, out var check)
                && check.HoldsFrame(__instance, time);
        }
        catch { holds = false; }
        if (!holds) Fail(scope);
    }

    private static void Fail(Scope scope)
    {
        // A nested scene's incomplete frame is composited into every enclosing frame.
        for (var value = scope; value is not null; value = value.Parent) Volatile.Write(ref value.Failed, 1);
    }

    private static void FailAllActive()
    {
        foreach (var scope in active.Keys) Fail(scope);
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => NoteAssemblyLoaded(args.LoadedAssembly);

    // Built-in readers are trusted by FrameCacheKey, so a built-in assembly loaded after the scan could
    // decode through a video source that was never hooked. Its sources are hooked on load; frames that
    // overlap the change are unverified, and a source that cannot be hooked stops all storing.
    // (Non-built-in readers are excluded earlier: FrameCacheKey bypasses file-backed scenes when they exist.)
    internal static void NoteAssemblyLoaded(Assembly assembly)
    {
        if (Volatile.Read(ref binding) is not { } hostBinding || !IsBuiltInAssembly(assembly, hostBinding.HostDirectory)) return;
        lock (hostBinding.Gate)
        {
            if (!hostBinding.Assemblies.Add(assembly.FullName ?? string.Empty)) return;
            Interlocked.Increment(ref bindEpoch);
            try
            {
                var patch = Method(nameof(DecoderFinalizer));
                foreach (var check in Classify(hostBinding, Implementations(hostBinding.VideoSource, LoadableTypes(assembly, strict: true))))
                {
                    EnsureNoExternalHarmonyOwners(check.Update, hostBinding.Harmony.Id);
                    Volatile.Write(ref decoders, new Dictionary<RuntimeMethodHandle, DecoderCheck>(Volatile.Read(ref decoders))
                    {
                        [check.Update.MethodHandle] = check,
                    });
                    hostBinding.Harmony.Patch(check.Update, finalizer: new HarmonyMethod(patch, Priority.Last));
                    lock (patchGate) installedPatches = [.. installedPatches, (check.Update, patch)];
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Volatile.Write(ref coverageProblem, $"起動後に読み込まれた {assembly.GetName().Name} の動画ソースを検証できないため、キャッシュ保存を停止しました: {error.GetBaseException().Message}");
            }
            finally { Interlocked.Increment(ref bindEpoch); }
        }
    }

    private static HostBinding CreateBinding(Assembly host, Harmony harmony, out Type[] implementations)
    {
        string hostDirectory = Path.GetDirectoryName(Path.GetFullPath(host.Location))
            ?? throw new NotSupportedException("Host directory is unknown");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => ReferenceEquals(assembly, host) || IsBuiltInAssembly(assembly, hostDirectory))
            .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal).ToArray();
        var types = assemblies.SelectMany(assembly => LoadableTypes(assembly, strict: true)).ToArray();
        var interfaces = types.Where(type => type.IsInterface && type.Name == "IVideoFileSource").ToArray();
        if (interfaces.Length != 1) throw new NotSupportedException($"IVideoFileSource contract found {interfaces.Length} times");
        var videoSource = interfaces[0];
        var interfaceUpdate = new[] { videoSource }.Concat(videoSource.GetInterfaces()).SelectMany(i => i.GetMethods())
            .Single(m => m.Name == "Update" && m.ReturnType == typeof(void) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(TimeSpan));
        var hostBinding = new HostBinding(harmony, videoSource, interfaceUpdate, hostDirectory);
        foreach (var assembly in assemblies) hostBinding.Assemblies.Add(assembly.FullName ?? string.Empty);
        implementations = Implementations(videoSource, types);
        return hostBinding;
    }

    private static Type[] Implementations(Type videoSource, IEnumerable<Type> types)
    {
        var implementations = types.Where(type => !type.IsInterface && videoSource.IsAssignableFrom(type) && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        if (implementations.FirstOrDefault(type => !type.IsClass || type.ContainsGenericParameters) is { } unsupported)
            throw new NotSupportedException($"Video source {unsupported.FullName} cannot be hooked");
        return implementations;
    }

    // Every built-in IVideoFileSource implementation is hooked. Only shapes whose "holds the requested
    // sample" state is understood are verifiable; every other implementation makes its frames unverified.
    // Returns checks for Update bodies that are not hooked yet (inherited bodies are shared).
    private static List<DecoderCheck> Classify(HostBinding hostBinding, Type[] types)
    {
        var targets = types.ToDictionary(type => type, type => ImplementationOf(type, hostBinding.InterfaceUpdate));
        var names = new Dictionary<Type, string>();
        foreach (var type in types)
        {
            if (DescribeMf2(type) is { } mf2) { hostBinding.Verified[type] = mf2.Holds; names[type] = mf2.Name; }
            else if (DescribeLegacy(type) is { } legacy) { hostBinding.Verified[type] = legacy.Holds; names[type] = legacy.Name; }
        }
        foreach (var type in types)
        {
            if (hostBinding.Verified.TryGetValue(type, out var verified)) hostBinding.Classifiers[type] = verified;
            else if (DescribeWrapper(type, hostBinding.VideoSource, hostBinding.Verified) is { } wrapper)
            {
                hostBinding.Classifiers[type] = wrapper.Holds;
                names[type] = wrapper.Name;
            }
            else
            {
                hostBinding.Classifiers[type] = static (_, _) => false;
                names[type] = "unverified (frames using it are never stored)";
            }
        }
        hostBinding.Coverage.AddRange(types.Select(type => $"{type.FullName}: {names[type]}"));
        Volatile.Write(ref coverage, hostBinding.Coverage.ToArray());
        var hooked = Volatile.Read(ref decoders);
        return types.GroupBy(type => targets[type], MethodHandleComparer.Instance)
            .Where(group => !hooked.ContainsKey(group.Key.MethodHandle))
            .Select(group => new DecoderCheck(string.Join(", ", group.Select(type => type.FullName)), group.Key, hostBinding.HoldsFrame))
            .ToList();
    }

    private static bool IsBuiltInAssembly(Assembly assembly, string hostDirectory)
    {
        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return false;
        string name = assembly.GetName().Name ?? string.Empty;
        if (name is not ("YukkuriMovieMaker" or "YukkuriMovieMaker.Plugin")
            && !name.StartsWith("YukkuriMovieMaker.Plugin.FileSource.", StringComparison.Ordinal)) return false;
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(assembly.Location)), hostDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static Type[] LoadableTypes(Assembly assembly, bool strict)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) when (!strict) { return error.Types.OfType<Type>().ToArray(); }
        catch (ReflectionTypeLoadException error)
        {
            throw new NotSupportedException($"Cannot enumerate video sources in {assembly.GetName().Name}", error);
        }
    }

    private static MethodInfo ImplementationOf(Type type, MethodInfo interfaceMethod)
    {
        var map = type.GetInterfaceMap(interfaceMethod.DeclaringType!);
        var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, interfaceMethod)];
        if (target.IsAbstract || target.GetMethodBody() is null || target.DeclaringType is not { } declaring || declaring.ContainsGenericParameters)
            throw new NotSupportedException($"{type.FullName}.Update has no hookable body");
        // Normalize the reflected type so types sharing an inherited Update share one hook.
        return (MethodInfo)MethodBase.GetMethodFromHandle(target.MethodHandle, declaring.TypeHandle)!;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var value = type; value is not null; value = value.BaseType)
            if (value.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    // Sample positions are 100 ns ticks (long) or TimeSpan; anything else is not understood.
    private static Func<object, TimeSpan>? TimeReader(Type type, string name)
    {
        if (FindField(type, name) is { } field)
        {
            if (field.FieldType == typeof(long)) return value => TimeSpan.FromTicks((long)field.GetValue(value)!);
            if (field.FieldType == typeof(TimeSpan)) return value => (TimeSpan)field.GetValue(value)!;
            return null;
        }
        PropertyInfo? property;
        try { property = type.GetProperty(name, Instance); }
        catch (AmbiguousMatchException) { return null; }
        if (property is null || property.GetIndexParameters().Length != 0 || property.GetMethod is null) return null;
        if (property.PropertyType == typeof(long)) return value => TimeSpan.FromTicks((long)property.GetValue(value)!);
        if (property.PropertyType == typeof(TimeSpan)) return value => (TimeSpan)property.GetValue(value)!;
        return null;
    }

    private static bool Covers(TimeSpan start, TimeSpan duration, TimeSpan time) =>
        duration > TimeSpan.Zero && start <= time && time < start + duration;

    // MF2 source: the frame is valid only while decodedFrame is set and its sample interval contains t.
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeMf2(Type type)
    {
        if (FindField(type, "decodedFrame") is not { } frameField || frameField.FieldType.IsValueType) return null;
        if (TimeReader(frameField.FieldType, "SampleTime") is not { } start
            || TimeReader(frameField.FieldType, "SampleDuration") is not { } duration) return null;
        return ($"MF2 ({frameField.DeclaringType!.Name}.decodedFrame: {frameField.FieldType.Name}.SampleTime/SampleDuration)",
            (instance, time) => frameField.GetValue(instance) is { } frame && Covers(start(frame), duration(frame), time));
    }

    // Legacy source: timeout/error clears the frame (currentDuration = 0). How streamStartTime relates to t
    // is not verified, so a nonzero stream start must hold under both readings or the frame is unverified.
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeLegacy(Type type)
    {
        if (TimeReader(type, "currentTime") is not { } start || TimeReader(type, "currentDuration") is not { } duration
            || TimeReader(type, "streamStartTime") is not { } streamStart) return null;
        return ("legacy (currentTime/currentDuration/streamStartTime)", (instance, time) =>
        {
            TimeSpan from = start(instance), length = duration(instance), offset = streamStart(instance);
            return Covers(from, length, time) && (offset == TimeSpan.Zero || Covers(from, length, time + offset));
        });
    }

    // CachedVideoFileSource delegates to one inner source. The inner source's own sample state is checked
    // here too, because the wrapper may serve a frame without calling the inner Update.
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeWrapper(Type type, Type videoSource,
        IReadOnlyDictionary<Type, Func<object, TimeSpan, bool>> verified)
    {
        if (type.Name != "CachedVideoFileSource") return null;
        var fields = new List<FieldInfo>();
        for (var value = type; value is not null; value = value.BaseType)
            fields.AddRange(value.GetFields(Instance | BindingFlags.DeclaredOnly).Where(field => videoSource.IsAssignableFrom(field.FieldType)));
        if (fields is not [var inner]) return null;
        return ($"wrapper (inner {inner.Name} must be a verified source holding t)", (instance, time) =>
            inner.GetValue(instance) is { } source && verified.TryGetValue(source.GetType(), out var holds) && holds(source, time));
    }

    private sealed class MethodHandleComparer : IEqualityComparer<MethodInfo>
    {
        internal static readonly MethodHandleComparer Instance = new();
        public bool Equals(MethodInfo? x, MethodInfo? y) => x is not null && y is not null && x.MethodHandle == y.MethodHandle;
        public int GetHashCode(MethodInfo value) => value.MethodHandle.GetHashCode();
    }
}
