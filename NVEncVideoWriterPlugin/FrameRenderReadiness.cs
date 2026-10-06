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
    // Unhookable: when Harmony cannot rebuild Update, called instead of failing the install if (and only if)
    // frames using the source are rejected another way; it must make sure they are.
    // Shown: the file a source that holds its frame shows (an image sequence's current image), or null. A frame is
    // only ready for a caller whose key names images that were all shown in it. (Not the converse: TimelineSource
    // also updates the sources of items that start within a second, in the frame's scope, to read them ahead.)
    // Seeks: methods hooked to tell that an Update sought (SeekPrefix); Observe: called after every Update of the
    // source, in or out of a frame's scope, before HoldsFrame (the seek watch of FFmpeg).
    internal sealed record DecoderCheck(string Name, MethodBase Update, Func<object, TimeSpan, bool> HoldsFrame, Action? Unhookable = null,
        Func<object, string?>? Shown = null, MethodBase[]? Seeks = null, Action<object, TimeSpan>? Observe = null);

    private sealed class Scope(object source, TimeSpan time, Scope? parent, int epoch)
    {
        internal readonly object Source = source;
        internal readonly TimeSpan Time = time;
        internal readonly Scope? Parent = parent;
        internal readonly int Epoch = epoch;
        internal int Failed;
        internal int Completed;
        private List<string>? shown;

        internal void Show(string file) { lock (this) (shown ??= []).Add(file); }

        internal string[] Shown { get { lock (this) return shown?.ToArray() ?? []; } }
    }

    private sealed record UpdateResult(bool Ready, TimeSpan Time, string[] Shown);

    private sealed class HostBinding(Harmony harmony, Type videoSource, MethodInfo interfaceUpdate, string hostDirectory, HostFeatures features)
    {
        // The host creates every rendered video source through VideoFileSourceFactory, which wraps it in
        // CachedVideoFileSource (read in YMM4 4.56.1.0), so the wrapper's hook sees every decode.
        internal readonly bool SourcesAlwaysWrapped = features.WrappedSources;
        internal readonly HostFeatures Features = features;
        internal readonly object Gate = new();
        internal readonly Harmony Harmony = harmony;
        internal readonly Type VideoSource = videoSource;
        internal readonly MethodInfo InterfaceUpdate = interfaceUpdate;
        internal readonly string HostDirectory = hostDirectory;
        internal readonly HashSet<string> Assemblies = new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<Type, Func<object, TimeSpan, bool>> Verified = new();
        internal readonly ConcurrentDictionary<Type, Func<object, TimeSpan, bool>> Classifiers = new();
        internal readonly ConcurrentDictionary<Type, Func<object, string?>> ShownFiles = new();
        internal readonly ConcurrentDictionary<Type, MethodInfo> SeekMethods = new();
        internal readonly ConcurrentDictionary<Type, Action<object, TimeSpan>> Observers = new();
        internal readonly List<string> Coverage = [];

        internal bool HoldsFrame(object instance, TimeSpan time) =>
            Classifiers.TryGetValue(instance.GetType(), out var holds) && holds(instance, time);

        internal string? ShownFile(object instance) => ShownFiles.TryGetValue(instance.GetType(), out var shown) ? shown(instance) : null;

        internal void Observe(object instance, TimeSpan time)
        {
            if (Observers.TryGetValue(instance.GetType(), out var observe)) observe(instance, time);
        }
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
    // images: the images (full paths) the caller's key says the frame shows; each must have been shown.
    internal static bool IsUpdateReady(object timelineSource, IReadOnlyCollection<string> images) => Installed && CoverageProblem is null
        && current.Value is { } scope && ReferenceEquals(scope.Source, timelineSource)
        && Volatile.Read(ref scope.Completed) == 0 && Volatile.Read(ref scope.Failed) == 0 && Stable(scope) && AllShown(images, scope.Shown);

    internal static bool IsUpdateReady(object timelineSource) => IsUpdateReady(timelineSource, []);

    private static bool AllShown(IReadOnlyCollection<string> images, string[] shown) =>
        images.Count == 0 || images.All(image => shown.Contains(image, StringComparer.OrdinalIgnoreCase));

    private static bool Stable(Scope scope) => (scope.Epoch & 1) == 0 && scope.Epoch == Volatile.Read(ref bindEpoch);

    // For callers that inspect a source after its Update returned (idle pre-render).
    internal static bool WasLastUpdateReady(object timelineSource, TimeSpan time, IReadOnlyCollection<string> images) => Installed && CoverageProblem is null
        && lastResults.TryGetValue(timelineSource, out var result) && result.Ready && result.Time == time && AllShown(images, result.Shown);

    internal static bool WasLastUpdateReady(object timelineSource, TimeSpan time) => WasLastUpdateReady(timelineSource, time, []);

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
                if (TryPatchDecoder(harmony, check, decoderFinalizer) is { } patches) added.AddRange(patches);
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

    // Harmony 2.4.2 cannot rebuild some method bodies at all (an exception filter that continues a loop, as in
    // the DirectShow reader of YMM4 4.56.1.0, fails with "Incorrect code generation for exception block").
    // The patches added (null when the source could not be hooked and its fallback rejects it instead). The seek
    // hooks go first: an Update hooked without them would not tell that it sought.
    private static (MethodBase Target, MethodInfo Patch)[]? TryPatchDecoder(Harmony harmony, DecoderCheck check, MethodInfo finalizer)
    {
        var added = new List<(MethodBase Target, MethodInfo Patch)>();
        try
        {
            var seekPrefix = Method(nameof(SeekPrefix));
            foreach (var seek in check.Seeks ?? [])
            {
                EnsureNoExternalHarmonyOwners(seek, harmony.Id);
                harmony.Patch(seek, prefix: new HarmonyMethod(seekPrefix, Priority.First));
                added.Add((seek, seekPrefix));
            }
            harmony.Patch(check.Update, prefix: new HarmonyMethod(Method(nameof(DecoderTracePrefix))), finalizer: new HarmonyMethod(finalizer, Priority.Last));
            added.Add((check.Update, Method(nameof(DecoderTracePrefix))));
            added.Add((check.Update, finalizer));
            return [.. added];
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            for (int i = added.Count - 1; i >= 0; i--)
                try { harmony.Unpatch(added[i].Target, added[i].Patch); } catch { }
            if (check.Unhookable is not { } fallback) throw;
            fallback();
            return null;
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
            lastResults.AddOrUpdate(__instance, new UpdateResult(Volatile.Read(ref __state.Failed) == 0 && Stable(__state), __state.Time, __state.Shown));
        }
        catch { Fail(__state); lastResults.Remove(__instance); }
        finally { current.Value = __state.Parent; }
    }

    private static void DecoderTracePrefix(object __instance, TimeSpan __0, out CacheTrace.Span? __state) =>
        __state = CacheTrace.Measure("decoder-update", component: __instance.GetType().FullName,
            frameTimeTicks: __0.Ticks, operation: CacheTrace.OperationId);

    private static void DecoderFinalizer(object __instance, object[] __args, MethodBase __originalMethod, Exception? __exception, CacheTrace.Span? __state)
    {
        if (__state is not null)
        {
            if (__exception is not null) { __state.Outcome = "exception"; __state.Detail = __exception.GetType().Name; }
            __state.Dispose();
        }
        // The seek watch follows every Update of the source, also those that prove nothing for any frame.
        if (__args is [TimeSpan observed] && Volatile.Read(ref decoders).TryGetValue(__originalMethod.MethodHandle, out var observing)
            && observing.Observe is { } observe)
            try { observe(__instance, observed); } catch { }
        var scope = current.Value;
        // TimelineSource.Update prefetches items about a second ahead with Task.Run, which captures the
        // current scope; that decode can finish after the frame completed. The frame that later adopts the
        // prefetched source updates it again at its own time inside its own scope, so the late decode
        // proves nothing for any frame and is ignored.
        if (scope is not null && Volatile.Read(ref scope.Completed) != 0) return;
        if (scope is null)
        {
            // Decode outside any render scope. If a render is in flight, attribution may have been lost
            // (execution context not flowed), so every in-flight frame is treated as unverified.
            if (!active.IsEmpty) FailAllActive();
            return;
        }
        bool holds;
        try
        {
            DecoderCheck? check = null;
            holds = __exception is null && __args is [TimeSpan time] && Volatile.Read(ref decoders).TryGetValue(__originalMethod.MethodHandle, out check)
                && check.HoldsFrame(__instance, time);
            // The file shown goes to this frame and the frames composited from it (a nested scene's).
            if (holds && check!.Shown?.Invoke(__instance) is { } file)
                for (var value = scope; value is not null; value = value.Parent) value.Show(Path.GetFullPath(file));
        }
        catch { holds = false; }
        using var readiness = CacheTrace.Measure("decoder-readiness", "state", __instance.GetType().FullName);
        if (readiness is not null) readiness.Outcome = holds ? "ready" : "not-ready";
        if (!holds) Fail(scope);
    }

    // Other audited asynchronous render inputs share the same nested-frame failure propagation.
    internal static void ObserveAuxiliary(bool ready, string detail)
    {
        using var trace = CacheTrace.Measure("auxiliary-readiness", "state", detail);
        if (trace is not null) trace.Outcome = ready ? "ready" : "not-ready";
        if (ready) return;
        var scope = current.Value;
        if (scope is null) { if (!active.IsEmpty) FailAllActive(); }
        else if (Volatile.Read(ref scope.Completed) == 0) Fail(scope);
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
                    if (TryPatchDecoder(hostBinding.Harmony, check, patch) is { } patches)
                        lock (patchGate) installedPatches = [.. installedPatches, .. patches];
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
        var hostBinding = new HostBinding(harmony, videoSource, interfaceUpdate, hostDirectory, HostFeatures.For(host));
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
            // A decoder whose assembly differs from the read builds' is not trusted by its shape alone.
            if (hostBinding.Features.DecoderVerified(type) && DescribeVerified(type) is { } verified)
            {
                hostBinding.Verified[type] = verified.Holds;
                if (verified.Shown is { } shown) hostBinding.ShownFiles[type] = shown;
                if (verified.Seek is { } seek) hostBinding.SeekMethods[type] = seek;
                if (verified.Observe is { } observe) hostBinding.Observers[type] = observe;
                names[type] = verified.Name;
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
        bool wrapped = hostBinding.SourcesAlwaysWrapped && hostBinding.Classifiers.Keys.Any(type => type.FullName == WrapperTypeName);
        return types.GroupBy(type => targets[type], MethodHandleComparer.Instance)
            .Where(group => !hooked.ContainsKey(group.Key.MethodHandle))
            .Select(group => new DecoderCheck(string.Join(", ", group.Select(type => type.FullName)), group.Key, hostBinding.HoldsFrame,
                // The wrapper itself must always be hooked; any other source it holds is then rejected by it.
                wrapped && group.All(type => type.FullName != WrapperTypeName) ? () => Demote(hostBinding, group) : null,
                hostBinding.ShownFile,
                group.Select(type => hostBinding.SeekMethods.TryGetValue(type, out var seek) ? seek : null).OfType<MethodInfo>()
                    .Distinct(MethodHandleComparer.Instance).ToArray(),
                hostBinding.Observe))
            .ToList();
    }

    // The source's own hook could not be applied: the wrapper rejects it from now on.
    private static void Demote(HostBinding hostBinding, IEnumerable<Type> types)
    {
        lock (hostBinding.Gate)
            foreach (var type in types)
            {
                hostBinding.Verified.TryRemove(type, out _);
                hostBinding.ShownFiles.TryRemove(type, out _);
                hostBinding.Observers.TryRemove(type, out _);
                hostBinding.Classifiers[type] = static (_, _) => false;
                int line = hostBinding.Coverage.FindIndex(entry => entry.StartsWith(type.FullName + ": ", StringComparison.Ordinal));
                string text = $"{type.FullName}: unverified (its Update cannot be hooked; CachedVideoFileSource never accepts it)";
                if (line >= 0) hostBinding.Coverage[line] = text; else hostBinding.Coverage.Add(text);
            }
        Volatile.Write(ref coverage, hostBinding.Coverage.ToArray());
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

    private static bool HasProperty(Type type, string name)
    {
        try { return type.GetProperty(name, Instance) is not null; }
        catch (AmbiguousMatchException) { return true; }
    }

    private static bool Covers(TimeSpan start, TimeSpan duration, TimeSpan time) =>
        duration > TimeSpan.Zero && start <= time && time < start + duration;

    // Pinned to host types whose Update was read (ILSpy, YMM4 4.56.1.0; see docs/HOST_CONTRACTS.md). A pinned
    // type whose fields no longer match, and every other implementation, is unverified.
    internal const string Mf2TypeName = "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.Source2.MFVideoFileSource2";
    internal const string MfLegacyTypeName = "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.MFVideoFileSource";
    internal const string FFmpegTypeName = "YukkuriMovieMaker.Plugin.FileSource.FFmpeg.FFmpegVideoFileSource";
    internal const string WicGifTypeName = "YukkuriMovieMaker.Plugin.FileSource.WIC.WICGifVideoSource";
    internal const string WicWebpTypeName = "YukkuriMovieMaker.Plugin.FileSource.WIC.WICWebpVideoSource";
    internal const string WicSequenceTypeName = "YukkuriMovieMaker.Plugin.FileSource.WIC.WICSequentialImageVideoSource";
    internal const string WrapperTypeName = "YukkuriMovieMaker.Plugin.CachedVideoFileSource";

    private sealed record Verified(string Name, Func<object, TimeSpan, bool> Holds, Func<object, string?>? Shown = null,
        MethodInfo? Seek = null, Action<object, TimeSpan>? Observe = null);

    private static Verified? DescribeVerified(Type type) => type.FullName switch
    {
        Mf2TypeName => Plain(DescribeMf2(type)),
        MfLegacyTypeName => Plain(DescribeStreamClock(type, "MF-legacy", excludeStretchedToEnd: false)),
        FFmpegTypeName => DescribeFFmpeg(type),
        // Synchronous WIC decode: failures throw (the decoder finalizer fails the frame). The only swallowed
        // GIF error clears the frame deterministically for that file.
        WicGifTypeName or WicWebpTypeName => new("WIC (synchronous decode; an exception fails the frame)", static (_, _) => true),
        WicSequenceTypeName => DescribeSequence(type),
        _ => null,
    };

    private static Verified? Plain((string Name, Func<object, TimeSpan, bool> Holds)? described) =>
        described is { } value ? new(value.Name, value.Holds) : null;

    // WICSequentialImageVideoSource.Update(t) sets currentFrame to GetFrameIndex(t) (FrameTime.TimeToFrame(t, 60)) and
    // loads frames[clamp(currentFrame)] synchronously through ImageFileSourceFactory; a file no reader opens leaves
    // source null and draws an empty bitmap. It holds t while source is set for GetFrameIndex(t), and shows that file.
    private static Verified? DescribeSequence(Type type)
    {
        if (FindField(type, "frames") is not { } frames || frames.FieldType != typeof(string[])
            || FindField(type, "currentFrame") is not { } current || current.FieldType != typeof(int)
            || FindField(type, "source") is not { } source || source.FieldType.IsValueType
            || type.GetMethod("GetFrameIndex", BindingFlags.Public | BindingFlags.Instance, [typeof(TimeSpan)]) is not { } index
            || index.ReturnType != typeof(int)) return null;
        return new("image sequence (the image of GetFrameIndex(t) is loaded; it must be the one the key names)",
            (instance, time) => source.GetValue(instance) is not null && (int)current.GetValue(instance)! == (int)index.Invoke(instance, [time])!,
            instance => frames.GetValue(instance) is string[] { Length: > 0 } files ? files[Math.Clamp((int)current.GetValue(instance)!, 0, files.Length - 1)] : null);
    }

    // MFVideoFileSource2 itself treats the frame as valid exactly while decodedFrame is set and its sample
    // interval contains t; a failed TryDecodeAt leaves it null and draws transparency.
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeMf2(Type type)
    {
        if (FindField(type, "decodedFrame") is not { } frameField || frameField.FieldType.IsValueType) return null;
        if (TimeReader(frameField.FieldType, "SampleTime") is not { } start
            || TimeReader(frameField.FieldType, "SampleDuration") is not { } duration) return null;
        return ($"MF2 ({frameField.FieldType.Name}.SampleTime/SampleDuration contains t)",
            (instance, time) => frameField.GetValue(instance) is { } frame && Covers(start(frame), duration(frame), time));
    }

    // MFVideoFileSource and FFmpegVideoFileSource add streamStartTime to t and keep the displayed sample as
    // [currentTime, currentTime + currentDuration); timeouts, errors and out-of-range times set a zero duration.
    // FFmpeg also stretches the last decoded frame up to the stream end when decoding stops early (end of
    // file or a read error), which cannot be told apart from a transient failure, so that is rejected.
    // The FFmpeg reader of YMM4 4.52 has no streamStartTime and uses t as it is (startOptional).
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeStreamClock(Type type, string label, bool excludeStretchedToEnd,
        bool startOptional = false)
    {
        if (TimeReader(type, "currentTime") is not { } start || TimeReader(type, "currentDuration") is not { } duration) return null;
        var streamStart = TimeReader(type, "streamStartTime");
        if (streamStart is null && (!startOptional || FindField(type, "streamStartTime") is not null || HasProperty(type, "streamStartTime"))) return null;
        streamStart ??= static _ => TimeSpan.Zero;
        Func<object, TimeSpan>? length = null;
        if (excludeStretchedToEnd && (length = TimeReader(type, "Duration")) is null) return null;
        return ($"{label} (currentTime/currentDuration contains t + streamStartTime{(length is null ? string.Empty : ", not stretched to the end")})",
            (instance, time) =>
            {
                TimeSpan from = start(instance), span = duration(instance), offset = streamStart(instance);
                return Covers(from, span, time + offset) && (length is null || from + span < offset + length(instance));
            });
    }

    // FFmpegVideoFileSource seeks to a key frame before t and decodes on until the frame that holds t. When the seek
    // lands after t, the first frame decoded starts later, and the reader shows it from t on (currentTime = t) until
    // another frame is decoded; which frame t shows then depends on where decoding came from. YMM4 4.54.0.1 and later
    // seek again further back in that case, unless even the stream start does not reach t; 4.52.0.0 to 4.54.0.0 do not.
    // So an interval that an Update which sought produced starting exactly at t + streamStartTime is not ready, until
    // the interval changes (sequential decoding shows frames in order, exactly). A frame that truly starts at t loses
    // only its own interval after a seek. The reader's SeekTo is hooked to tell that an Update sought.
    private sealed class SeekState
    {
        internal bool Sought;
        internal (TimeSpan From, TimeSpan Span)? Suspect;
    }

    private static readonly ConditionalWeakTable<object, SeekState> seekStates = new();

    private static void SeekPrefix(object __instance)
    {
        var state = seekStates.GetValue(__instance, static _ => new());
        lock (state) state.Sought = true;
    }

    private static Verified? DescribeFFmpeg(Type type)
    {
        if (DescribeStreamClock(type, "FFmpeg", excludeStretchedToEnd: true, startOptional: true) is not { } clock) return null;
        var seek = type.GetMethods(Instance | BindingFlags.DeclaredOnly).Where(method => method.Name == "SeekTo" && !method.IsAbstract
            && method.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(TimeSpan)).ToArray();
        if (seek is not [var seekTo]) return null;
        Func<object, TimeSpan> start = TimeReader(type, "currentTime")!, duration = TimeReader(type, "currentDuration")!;
        Func<object, TimeSpan> streamStart = TimeReader(type, "streamStartTime") ?? (static _ => TimeSpan.Zero);
        return new(clock.Name + ", not the interval a seek began at t", (instance, time) =>
        {
            if (!clock.Holds(instance, time)) return false;
            if (!seekStates.TryGetValue(instance, out var state)) return true;
            var interval = (start(instance), duration(instance));
            lock (state) return state.Suspect != interval;
        }, Seek: seekTo, Observe: (instance, time) =>
        {
            var state = seekStates.GetValue(instance, static _ => new());
            TimeSpan from = start(instance), span = duration(instance);
            lock (state)
            {
                if (state.Sought)
                {
                    state.Sought = false;
                    state.Suspect = span > TimeSpan.Zero && from == time + streamStart(instance) ? (from, span) : null;
                }
                else if (state.Suspect is { } suspect && (suspect.From != from || suspect.Span != span)) state.Suspect = null;
            }
        });
    }

    // CachedVideoFileSource (the factory wraps every video source in it) delegates Update and Output to
    // resource.Source. The inner state is checked here too, so the wrapper never vouches for more than it holds.
    private static (string Name, Func<object, TimeSpan, bool> Holds)? DescribeWrapper(Type type, Type videoSource,
        IReadOnlyDictionary<Type, Func<object, TimeSpan, bool>> verified)
    {
        if (type.FullName != WrapperTypeName || FindField(type, "resource") is not { } resource || resource.FieldType.IsValueType) return null;
        PropertyInfo? source;
        try { source = resource.FieldType.GetProperty("Source", Instance); }
        catch (AmbiguousMatchException) { return null; }
        if (source?.GetMethod is null || source.GetIndexParameters().Length != 0 || !videoSource.IsAssignableFrom(source.PropertyType)) return null;
        return ("wrapper (resource.Source must be a verified source holding t)", (instance, time) =>
            resource.GetValue(instance) is { } held && source.GetValue(held) is { } inner
            && verified.TryGetValue(inner.GetType(), out var holds) && holds(inner, time));
    }

    private sealed class MethodHandleComparer : IEqualityComparer<MethodInfo>
    {
        internal static readonly MethodHandleComparer Instance = new();
        public bool Equals(MethodInfo? x, MethodInfo? y) => x is not null && y is not null && x.MethodHandle == y.MethodHandle;
        public int GetHashCode(MethodInfo value) => value.MethodHandle.GetHashCode();
    }
}

