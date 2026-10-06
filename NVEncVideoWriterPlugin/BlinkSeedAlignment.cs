using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

// The bundled animation and PSD tachie seed their blinking with the GetHashCode of the parts folder or PSD path
// (AnimationTachieSource.Update, PsdTachieSource.ApplyAnimation; YMM4 4.56.1.0), which .NET randomizes per process: the
// blink times change at every start of YMM4, so their frames were keyed for one run only. The plugin seeds them with a
// stable hash of the same string (one call replaced, nothing else), so that every run blinks alike and their frames
// keep their keys across restarts and in clones of the scene. Installed for the audited plugin builds only (their
// MVIDs), when a verified character first uses one.
internal static class BlinkSeedAlignment
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo objectHash = typeof(object).GetMethod(nameof(GetHashCode))!;
    private static readonly MethodInfo seed = typeof(BlinkSeedAlignment).GetMethod(nameof(Seed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<MethodBase, bool> patched = new();
    private static readonly object gate = new();
    private static Harmony? harmony;

    // The Harmony instance of the installed cache (TimelineFrameCache.TryInstall); null when the cache is off.
    internal static void Use(Harmony? value)
    {
        lock (gate) harmony = value;
    }

    internal static void Uninstall(Harmony value)
    {
        lock (gate)
        {
            foreach (var method in patched.Where(pair => pair.Value).Select(pair => pair.Key))
                try { value.Unpatch(method, HarmonyPatchType.Transpiler, value.Id); } catch (Exception error) when (error is not OutOfMemoryException) { }
            patched.Clear();
            if (ReferenceEquals(harmony, value)) harmony = null;
        }
    }

    // Whether `type`.`method` of `assembly` seeds its blinking with the stable hash (patching it the first time).
    // Never throws: false keeps the frames keyed for this run.
    internal static bool Stable(Assembly assembly, string type, string method)
    {
        try
        {
            var target = assembly.GetType(type, false)?.GetMethods(Declared).SingleOrDefault(candidate => candidate.Name == method);
            if (target is null) return false;
            if (patched.TryGetValue(target, out bool done) && done && Installed(target)) return true;
            lock (gate)
            {
                if (harmony is not { } owner) return false;
                if (Installed(target)) return true;
                // Refused before (the body or another owner); a patch someone removed is applied again.
                if (patched.TryGetValue(target, out done) && !done) return false;
                return patched[target] = TryPatch(owner, target);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static bool Installed(MethodBase target) => harmony is { } owner && (Harmony.GetPatchInfo(target)?.Transpilers.Any(patch => patch.owner == owner.Id) ?? false);

    private static bool TryPatch(Harmony owner, MethodInfo target)
    {
        try
        {
            if (Harmony.GetPatchInfo(target)?.Owners.Any(id => id != owner.Id) ?? false) return false;
            // The audited body hashes exactly one value.
            if (PatchProcessor.GetOriginalInstructions(target).Count(instruction => instruction.Calls(objectHash)) != 1) return false;
            owner.Patch(target, transpiler: new HarmonyMethod(typeof(BlinkSeedAlignment), nameof(Rewrite)));
            return Installed(target);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            // Same stack effect (the string in, an int out); labels and blocks stay on the instruction.
            if (instruction.Calls(objectHash)) { instruction.opcode = OpCodes.Call; instruction.operand = seed; }
            yield return instruction;
        }
    }

    // Names the seed function in the keys of the frames it seeds.
    internal const string Resource = "blink-seed://fnv1a-utf16-v1";

    // FNV-1a over the UTF-16 code units: the same value in every process.
    internal static int Seed(object? value)
    {
        if (value is not string text) return value?.GetHashCode() ?? 0;
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return unchecked((int)hash);
    }
}
