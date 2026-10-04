using System.Reflection;
using HarmonyLib;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// Observe the value actually consumed by the audited native tachie, without replacing its calculation or wait.
// A successfully completed Task is insufficient: the host catches cancellation and calculation failures.
internal static class NativeTachieReadiness
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly AsyncLocal<Call?> current = new();
    private static MethodInfo? update, read;
    private static FieldInfo? itemField, sessionField, taskField, cancellationField, sessionGate, terminated, published, values;
    private static FieldInfo? cancellationGate, cancellationCurrent, cancellationDisposed;
    private static string? owner;
    private sealed class Call(object source, TachieItem item, Call? parent)
    {
        internal readonly object Source = source;
        internal readonly TachieItem Item = item;
        internal readonly Call? Parent = parent;
        internal bool Observed, Closed;
    }

    internal static bool Installed => owner is { } id && update is not null && read is not null
        && (Harmony.GetPatchInfo(update)?.Owners.Contains(id) ?? false)
        && (Harmony.GetPatchInfo(read)?.Owners.Contains(id) ?? false);

    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        reason = string.Empty;
        if (!HostFeatures.For(host).LipSync) return true;
        if (Installed) return owner == harmony.Id;
        var patched = new List<MethodInfo>();
        try
        {
            var type = host.GetType("YukkuriMovieMaker.Player.Video.Items.TachieSource", true)!;
            var session = host.GetType("YukkuriMovieMaker.Player.Audio.LipSyncEnvelopeSession", true)!;
            var cancellation = host.GetType("YukkuriMovieMaker.Player.Audio.EnvelopeCancellationSlot", true)!;
            update = type.GetMethod("Update", Instance)!;
            read = type.GetMethod("ReadVolumeAfterRequiredWait", BindingFlags.Static | BindingFlags.NonPublic)!;
            if (update.ReturnType != typeof(void) || update.GetParameters() is not [var description]
                || description.ParameterType.FullName != "YukkuriMovieMaker.Player.Video.TimelineItemSourceDescription"
                || read.ReturnType != typeof(double) || !read.GetParameters().Select(p => p.ParameterType).SequenceEqual([
                    description.ParameterType.Assembly.GetType("YukkuriMovieMaker.Player.Video.TimelineSourceUsage")!,
                    typeof(int?), typeof(int), session, typeof(Task), typeof(TimeSpan),
                    host.GetType("YukkuriMovieMaker.Player.Video.Items.EnvelopeWaitTimeoutLatch")!, typeof(string)]))
                throw new NotSupportedException("Native lip sync method contract changed");
            itemField = Field(type, "item", typeof(TachieItem));
            sessionField = Field(type, "volumeEnvelopeSession", session);
            taskField = Field(type, "envelopeTask", typeof(Task));
            cancellationField = Field(type, "envelopeCancellation", cancellation);
            sessionGate = Field(session, "gate", typeof(object));
            terminated = Field(session, "terminated", typeof(bool));
            published = Field(session, "publishedFrame", typeof(int));
            values = Field(session, "values", typeof(double[]));
            cancellationGate = Field(cancellation, "gate", typeof(object));
            cancellationCurrent = Field(cancellation, "current", typeof(CancellationTokenSource));
            cancellationDisposed = Field(cancellation, "disposed", typeof(bool));
            foreach (var target in new[] { update, read })
                if (Harmony.GetPatchInfo(target)?.Owners.Any(id => id != harmony.Id) ?? false)
                    throw new NotSupportedException("Native lip sync has an external Harmony owner");
            harmony.Patch(update, prefix: new(typeof(NativeTachieReadiness), nameof(Begin)),
                finalizer: new(typeof(NativeTachieReadiness), nameof(End)));
            patched.Add(update);
            harmony.Patch(read, finalizer: new(typeof(NativeTachieReadiness), nameof(Observe)));
            patched.Add(read);
            owner = harmony.Id;
            AnimationTachieDependencies.ReadinessInstalled = () => Installed;
            return true;
        }
        catch (Exception error)
        {
            foreach (var target in patched) harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
            owner = null;
            reason = "口パクの完了確認を接続できません: " + error.GetBaseException().Message;
            return false;
        }
    }

    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, Instance);
        return field?.FieldType == expected ? field : throw new NotSupportedException($"{type.FullName}.{name} changed");
    }

    private static void Begin(object __instance, out Call? __state)
    {
        __state = null;
        try
        {
            if (itemField?.GetValue(__instance) is TachieItem item && AnimationTachieDependencies.Character(item.Character))
                current.Value = __state = new(__instance, item, current.Value);
        }
        catch { FrameRenderReadiness.ObserveAuxiliary(false, "lip-sync-source-inspection"); }
    }

    private static void End(Exception? __exception, Call? __state)
    {
        if (__state is null) return;
        if (__exception is not null || !__state.Observed) FrameRenderReadiness.ObserveAuxiliary(false, "lip-sync-update");
        __state.Closed = true;
        current.Value = __state.Parent;
    }

    private static void Observe(object[] __args, double __result, Exception? __exception)
    {
        var call = current.Value;
        if (call is null || call.Closed) return;
        call.Observed = true;
        bool ready = false;
        try { ready = __exception is null && ValueReady(call.Source, call.Item.Character.MouseSmooth, __args, __result); }
        catch { }
        FrameRenderReadiness.ObserveAuxiliary(ready, "lip-sync-published-value");
    }

    internal static bool ValueReady(object source, int smooth, object[] args, double result)
    {
        if (args.Length != 8 || args[2] is not int length || !double.IsFinite(result)) return false;
        if (args[1] is not int frame || frame < 0 || frame >= length) return result == -1;
        if (smooth <= 0) return result == 0 && args[3] is null && args[4] is null;
        if (args[3] is not { } session || args[4] is not Task task || task.IsCanceled || task.IsFaulted
            || !ReferenceEquals(sessionField!.GetValue(source), session) || !ReferenceEquals(taskField!.GetValue(source), task)) return false;
        var slot = cancellationField!.GetValue(source)!;
        lock (cancellationGate!.GetValue(slot)!)
        {
            if ((bool)cancellationDisposed!.GetValue(slot)!
                || cancellationCurrent!.GetValue(slot) is CancellationTokenSource token && token.IsCancellationRequested) return false;
        }
        lock (sessionGate!.GetValue(session)!)
        {
            var samples = (double[])values!.GetValue(session)!;
            int last = (int)published!.GetValue(session)!;
            if (samples.Length != checked(length + 1) || frame > last || frame >= samples.Length
                || (bool)terminated!.GetValue(session)! && last != samples.Length - 1) return false;
            double sample = samples[frame];
            return sample >= 0 && double.IsFinite(sample) && BitConverter.DoubleToInt64Bits(sample) == BitConverter.DoubleToInt64Bits(result);
        }
    }
}
