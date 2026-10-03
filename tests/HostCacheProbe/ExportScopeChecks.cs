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
        CheckStagingCleanup();
        CheckSavedOptions();
    }

    // The NVENC options outlive a restart: they go through the plugin's settings and YMM4's JSON unchanged.
    private static void CheckSavedOptions()
    {
        var plugin = typeof(NvencVideoFileWriterPlugin).Assembly;
        var optionsType = plugin.GetType("NVEncVideoWriterPlugin.NvencSettings", true)!;
        var options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("Codec")!.SetValue(options, NvencCodec.H265);
        optionsType.GetProperty("BitrateKbps")!.SetValue(options, 34567);
        optionsType.GetProperty("Quality")!.SetValue(options, NvencQuality.Quality);
        optionsType.GetProperty("RateControl")!.SetValue(options, NvencRateControl.Fixed);
        optionsType.GetProperty("HevcAsync")!.SetValue(options, false);
        optionsType.GetProperty("EnableDebugLog")!.SetValue(options, true);
        var saved = new FrameCacheToolSettings();
        optionsType.GetMethod("SaveTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(options, [saved]);
        var loaded = YukkuriMovieMaker.Json.Json.LoadFromText<FrameCacheToolSettings>(YukkuriMovieMaker.Json.Json.GetJsonText(saved))!;
        var restored = optionsType.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [loaded])!;
        foreach (var property in optionsType.GetProperties())
            Check(Equals(property.GetValue(restored), property.GetValue(options)), $"The NVENC option {property.Name} was not kept");
        var defaults = optionsType.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new FrameCacheToolSettings()])!;
        var fresh = Activator.CreateInstance(optionsType)!;
        foreach (var property in optionsType.GetProperties())
            Check(Equals(property.GetValue(defaults), property.GetValue(fresh)), $"The saved default of the NVENC option {property.Name} differs");
        Console.WriteLine("NVENC options: saved and loaded through the plugin settings and YMM4's JSON OK");
    }

    // A cancelled or failed NVENC export leaves no partial file next to the output: the writer deletes its staging file
    // (written here by hand, as the native encoder would have) and never creates the output. No GPU or NVENC needed.
    private static void CheckStagingCleanup()
    {
        var plugin = typeof(NvencVideoFileWriterPlugin).Assembly;
        var writerType = plugin.GetType("NVEncVideoWriterPlugin.NvencVideoFileWriter", true)!;
        var settings = Activator.CreateInstance(plugin.GetType("NVEncVideoWriterPlugin.NvencSettings", true)!)!;
        var scope = plugin.GetType("NVEncVideoWriterPlugin.HostExportScope", true)!;
        var snapshotType = scope.GetNestedType("Snapshot", BindingFlags.NonPublic)!;
        var staging = writerType.GetField("_stagingPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var videoInfo = new YukkuriMovieMaker.Project.VideoInfo { Width = 320, Height = 180, FPS = 30, Hz = 48000 };
        string folder = Path.Combine(Path.GetTempPath(), "ymm-nvenc-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // (case, expected frames, cancelled, audio only): no frame of one arrives; cancelled; audio but no video.
            foreach (var (name, expected, cancel, audioOnly) in new[] { ("missing", 1, false, false), ("cancelled", 1, true, false), ("audio-only", 0, false, true) })
            {
                using var cancellation = new CancellationTokenSource();
                if (cancel) cancellation.Cancel();
                string output = Path.Combine(folder, name + ".mp4");
                var previous = scope.GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [Activator.CreateInstance(snapshotType, cancellation.Token, expected)]);
                YukkuriMovieMaker.Plugin.FileWriter.IVideoFileWriter writer;
                try { writer = (YukkuriMovieMaker.Plugin.FileWriter.IVideoFileWriter)Activator.CreateInstance(writerType, output, videoInfo, settings)!; }
                finally { scope.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [previous]); }
                string partial = (string)staging.GetValue(writer)!;
                File.WriteAllBytes(partial, [0, 0, 0, 1]);
                if (audioOnly) writer.WriteAudio([0f, 0f]);
                try { writer.Dispose(); Check(!audioOnly, "Audio without video was accepted"); }
                catch (InvalidOperationException) when (audioOnly) { }
                Check(!File.Exists(partial), $"A {name} NVENC export left its partial file");
                Check(!File.Exists(output), $"A {name} NVENC export created the output");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
        Console.WriteLine("NVENC export: missing frames, cancellation and failure delete the partial file OK");
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
