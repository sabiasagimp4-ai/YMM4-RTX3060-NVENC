using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NVEncVideoWriterPlugin;

internal static class ExportScopeChecks
{
    internal static void Run(Assembly host, Harmony harmony)
    {
        Check(HostExportScope.GetCurrent() is null, "Unexpected outer export scope");
        using var outerCancel = new CancellationTokenSource();
        using var innerCancel = new CancellationTokenSource();
        var outer = new HostExportScope.Snapshot(outerCancel.Token, 7);
        var inner = new HostExportScope.Snapshot(innerCancel.Token, 2);
        var state = HostExportScope.Enter(outer);
        try
        {
            var task = FakeExport(inner, () =>
            {
                Check(ReferenceEquals(HostExportScope.GetCurrent(), inner), "Factory missed synchronous scope");
                var nested = HostExportScope.Enter(null);
                Check(HostExportScope.GetCurrent() is null, "Missing context inherited outer scope");
                HostExportScope.Restore(nested);
                HostExportScope.Restore(nested);
                Check(ReferenceEquals(HostExportScope.GetCurrent(), inner), "Nested restoration failed");
            });
            Check(ReferenceEquals(HostExportScope.GetCurrent(), outer), "Async return leaked inner scope");
            task.GetAwaiter().GetResult();
            Check(outer.CanPublish(7) && !outer.CanPublish(6) && !outer.CanPublish(8), "Frame completeness guard failed");
            outerCancel.Cancel();
            Check(!outer.CanPublish(7), "Final-frame cancellation allowed publish");
            try { FakeExport(inner, () => throw new ApplicationException("factory failure")).GetAwaiter().GetResult(); }
            catch (ApplicationException) { }
            Check(ReferenceEquals(HostExportScope.GetCurrent(), outer), "Throwing factory leaked scope");
        }
        finally { HostExportScope.Restore(state); }
        Check(HostExportScope.GetCurrent() is null, "Scope not cleared");

        Check(HostExportScope.TryInstall(host, harmony, out var reason), reason);
        var writerType = host.GetType("YukkuriMovieMaker.VideoFileWriter.VideoFileWriter", true)!;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var export = RuntimeHelpers.GetUninitializedObject(writerType);
        var timeline = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Timeline", true)!)!;
        var scenes = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Scenes", true)!, [false])!;
        var scene = Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Project.Scene", true)!, [timeline, scenes, Array.Empty<Guid>()])!;
        writerType.GetField("scene", instance)!.SetValue(export, scene);
        var settingsField = writerType.GetField("settings", instance)!;
        var settings = settingsField.FieldType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null);
        settingsField.SetValue(export, settings);
        var pluginType = writerType.GetField("plugin", instance)!.FieldType;
        var proxy = DispatchProxy.Create(pluginType, typeof(ExportFactoryProbe));
        writerType.GetField("plugin", instance)!.SetValue(export, proxy);
        writerType.GetField("path", instance)!.SetValue(export, "not-written.mp4");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var method = writerType.GetMethod("CreateFileAsync", instance)!;
        // An already cancelled token makes the real host return immediately after its real factory
        // call, before GPU setup, rendering, file creation, or subtitle side effects.
        ((Task)method.Invoke(export, [null, cancelled.Token])!).GetAwaiter().GetResult();
        var captured = ((ExportFactoryProbe)proxy).Captured;
        Check(captured is not null && captured.CancellationToken == cancelled.Token, "Actual host factory missed token snapshot");
        Check(HostExportScope.GetCurrent() is null, "Actual method scope leaked after async return");
        Check(!captured!.CanPublish(captured.ExpectedFrames), "Cancelled actual-host export can publish");
        Console.WriteLine("Export scope: real factory-before-await/token capture, nested restoration, frame/cancel guards OK");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FakeExport(HostExportScope.Snapshot snapshot, Action factory)
    {
        var state = HostExportScope.Enter(snapshot);
        try { return FakeExportBody(factory); }
        finally { HostExportScope.Restore(state); }
    }

    private static async Task FakeExportBody(Action factory)
    {
        factory();
        await Task.Yield();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class ExportFactoryProbe : DispatchProxy
{
    internal HostExportScope.Snapshot? Captured;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "CreateVideoFileWriter") throw new NotSupportedException(targetMethod?.Name);
        Captured = HostExportScope.GetCurrent();
        return null;
    }
}
