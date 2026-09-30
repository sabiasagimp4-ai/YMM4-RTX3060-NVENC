using System.Reflection;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

// Installed only after the shared host compatibility gate has accepted the exact host binary.
internal static class HostExportScope
{
    internal sealed record Snapshot(CancellationToken CancellationToken, int ExpectedFrames)
    {
        internal bool CanPublish(long acceptedFrames) =>
            !CancellationToken.IsCancellationRequested && acceptedFrames == ExpectedFrames;
    }

    internal sealed class ScopeState(Snapshot? previous)
    {
        internal readonly Snapshot? Previous = previous;
        internal bool Restored;
    }

    private sealed record Contract(FieldInfo Scene, PropertyInfo Timeline, PropertyInfo Length,
        PropertyInfo SettingsDefault, PropertyInfo EncodeFrom, PropertyInfo EncodeTo);

    [ThreadStatic] private static Snapshot? current;
    private static readonly object installLock = new();
    private static Contract? contract;
    private static MethodInfo? patchedMethod;
    private static string? owner;

    internal static Snapshot? GetCurrent() => current;

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        lock (installLock)
        {
            try
            {
                var writer = host.GetType("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", true)!;
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var method = writer.GetMethods(instance).Single(m => m.Name == "CreateFileAsync"
                    && m.ReturnType == typeof(Task) && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType.FullName == "YukkuriMovieMaker.Commons.ProgressMessage"
                    && m.GetParameters()[1].ParameterType == typeof(CancellationToken));
                if (patchedMethod == method && owner == harmony.Id
                    && Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true)
                {
                    reason = string.Empty;
                    return true;
                }
                var scene = writer.GetField("scene", instance) ?? throw new MissingFieldException("Export scene");
                var timeline = scene.FieldType.GetProperty("Timeline") ?? throw new MissingMemberException("Scene.Timeline");
                var length = timeline.PropertyType.GetProperty("Length") ?? throw new MissingMemberException("Timeline.Length");
                var settings = writer.GetField("settings", instance)?.FieldType ?? throw new MissingFieldException("Export settings");
                var settingsDefault = settings.GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?? throw new MissingMemberException("Export settings default");
                var from = settings.GetProperty("EncodeFrom") ?? throw new MissingMemberException("EncodeFrom");
                var to = settings.GetProperty("EncodeTo") ?? throw new MissingMemberException("EncodeTo");
                if (scene.FieldType.FullName != "YukkuriMovieMaker.Project.Scene"
                    || length.PropertyType != typeof(int) || from.PropertyType != typeof(int) || to.PropertyType != typeof(int))
                    throw new NotSupportedException("Export range contract changed");
                contract = new Contract(scene, timeline, length, settingsDefault, from, to);
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(HostExportScope), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(HostExportScope), nameof(Postfix)),
                    finalizer: new HarmonyMethod(typeof(HostExportScope), nameof(Finalizer)));
                patchedMethod = method;
                owner = harmony.Id;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Export cancellation hook unavailable: {ex.GetBaseException().Message}";
                return false;
            }
        }
    }

    internal static ScopeState Enter(Snapshot? snapshot)
    {
        var state = new ScopeState(current);
        current = snapshot;
        return state;
    }

    internal static void Restore(ScopeState? state)
    {
        if (state is null || state.Restored) return;
        current = state.Previous;
        state.Restored = true;
    }

    private static void Prefix(object __instance, CancellationToken cancellationToken, out ScopeState __state)
    {
        Snapshot? snapshot = null;
        try
        {
            var c = contract!;
            var scene = c.Scene.GetValue(__instance)!;
            var timeline = c.Timeline.GetValue(scene)!;
            var length = (int)c.Length.GetValue(timeline)!;
            var settings = c.SettingsDefault.GetValue(null)!;
            var from = (int)c.EncodeFrom.GetValue(settings)!;
            var to = (int)c.EncodeTo.GetValue(settings)!;
            var start = Math.Max(0, Math.Min(Math.Min(from, to), length - 1));
            var end = Math.Min(length, Math.Max(from, to));
            snapshot = new Snapshot(cancellationToken, end - start);
        }
        catch
        {
            // Factory refuses a host export without a snapshot; never inherit an outer export's token.
        }
        // The pinned host creates its writer before its first await. The writer owns this snapshot,
        // not ambient context: later audio/video tasks may execute on any thread.
        __state = Enter(snapshot);
    }

    private static void Postfix(ScopeState __state) => Restore(__state);
    private static void Finalizer(ScopeState __state) => Restore(__state);
}
