using System.Collections.Concurrent;
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
    private static Dictionary<MethodBase, DecoderCheck> decoders = [];
    private static (MethodBase Target, MethodInfo Patch)[] installedPatches = [];
    private static int installed;

    // Returns whether the decoded frame the decoder now holds covers the requested time.
    internal sealed record DecoderCheck(string Name, MethodBase Update, Func<object, TimeSpan, bool> HoldsFrame);

    private sealed class Scope(object source, TimeSpan time, Scope? parent)
    {
        internal readonly object Source = source;
        internal readonly TimeSpan Time = time;
        internal readonly Scope? Parent = parent;
        internal int Failed;
        internal int Completed;
    }

    private sealed record UpdateResult(bool Ready, TimeSpan Time);

    internal static bool Installed => Volatile.Read(ref installed) != 0;

    // For TimelineSource.Update postfixes: the scope is still open (finalizers run after postfixes).
    internal static bool IsUpdateReady(object timelineSource) => Installed && current.Value is { } scope
        && ReferenceEquals(scope.Source, timelineSource) && Volatile.Read(ref scope.Completed) == 0
        && Volatile.Read(ref scope.Failed) == 0;

    // For callers that inspect a source after its Update returned (idle pre-render).
    internal static bool WasLastUpdateReady(object timelineSource, TimeSpan time) =>
        Installed && lastResults.TryGetValue(timelineSource, out var result) && result.Ready && result.Time == time;

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        try
        {
            var sourceType = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
            var update = sourceType.GetMethods(Instance).Single(m => m.Name == "Update" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(TimeSpan) && m.ReturnType == typeof(void));
            return TryInstall(update, BindHostDecoders(host), harmony, out reason);
        }
        catch (Exception error)
        {
            reason = "Render readiness hook rejected: " + error.GetBaseException().Message;
            return false;
        }
    }

    // Installs only when every hook applies; on failure removes exactly the patches added here.
    internal static bool TryInstall(MethodBase timelineUpdate, IReadOnlyList<DecoderCheck> checks, Harmony harmony, out string reason)
    {
        if (Installed)
        {
            // Never touch the live install from a rejected call.
            reason = "Render readiness hook rejected: already installed.";
            return false;
        }
        var added = new List<(MethodBase Target, MethodInfo Patch)>();
        try
        {
            if (checks.Count == 0) throw new NotSupportedException("No verified decoder contracts.");
            if (timelineUpdate.GetParameters() is not [{ ParameterType: var timeType }, _] || timeType != typeof(TimeSpan))
                throw new NotSupportedException("TimelineSource.Update(TimeSpan, usage) contract changed");
            foreach (var check in checks)
                if (check.Update.IsStatic || check.Update.GetParameters() is not [{ ParameterType: var t }] || t != typeof(TimeSpan))
                    throw new NotSupportedException($"{check.Name}.Update(TimeSpan) contract changed");
            var targets = checks.Select(c => c.Update).Append(timelineUpdate).ToArray();
            if (targets.Distinct().Count() != targets.Length) throw new NotSupportedException("Duplicate readiness hook targets");
            foreach (var target in targets) EnsureNoExternalHarmonyOwners(target, harmony.Id);

            // Published before the first hook so no decoder update can observe a partial table.
            Volatile.Write(ref decoders, checks.ToDictionary(check => check.Update, check => check));
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
            installedPatches = [.. added];
            Volatile.Write(ref installed, 1);
            reason = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            for (int i = added.Count - 1; i >= 0; i--)
                try { harmony.Unpatch(added[i].Target, added[i].Patch); } catch { }
            Volatile.Write(ref decoders, []);
            reason = "Render readiness hook rejected: " + error.GetBaseException().Message;
            return false;
        }
    }

    // Removes exactly the patches added by the successful install (offline checks and host rollback).
    internal static void Uninstall(Harmony harmony)
    {
        Volatile.Write(ref installed, 0);
        var patches = Interlocked.Exchange(ref installedPatches, []);
        for (int i = patches.Length - 1; i >= 0; i--)
            try { harmony.Unpatch(patches[i].Target, patches[i].Patch); } catch { }
        Volatile.Write(ref decoders, []);
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
        var scope = new Scope(__instance, time, current.Value);
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
            lastResults.AddOrUpdate(__instance, new UpdateResult(Volatile.Read(ref __state.Failed) == 0, __state.Time));
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
            holds = __exception is null && __args is [TimeSpan time] && Volatile.Read(ref decoders).TryGetValue(__originalMethod, out var check)
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

    // Filled from verified host shapes only. Unverified hosts get no cache instead of a guessed contract.
    private static IReadOnlyList<DecoderCheck> BindHostDecoders(Assembly host) =>
        throw new NotSupportedException("Decoder readiness contracts for this host have not been verified yet.");
}
