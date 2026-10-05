using System.Reflection;
using System.Runtime.InteropServices;

namespace NVEncVideoWriterPlugin;

internal static class DiagnosticEnvironmentCapture
{
    internal static DiagnosticEnvironment Capture()
    {
        var plugin = typeof(HostIntegration).Assembly;
        var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "YukkuriMovieMaker");
        DiagnosticFeatures features = DiagnosticFeatures.None;
        if (host is not null && HostIntegration.CacheAvailable)
        {
            var f = HostFeatures.For(host);
            if (f.Preview) features |= DiagnosticFeatures.Preview;
            if (f.SelectionRects) features |= DiagnosticFeatures.Selection;
            if (f.WrappedSources) features |= DiagnosticFeatures.WrappedSources;
            if (f.RulerBars) features |= DiagnosticFeatures.Ruler;
            if (f.SimpleTachie) features |= DiagnosticFeatures.SimpleTachie;
            if (f.LipSync) features |= DiagnosticFeatures.LipSync;
            if (f.AnimationTachie) features |= DiagnosticFeatures.AnimationTachie;
            if (f.PsdTachie) features |= DiagnosticFeatures.PsdTachie;
        }
        if (HostIntegration.ExportHooked) features |= DiagnosticFeatures.Export;
        var settings = FrameCacheToolSettings.Default;
        return new(host?.GetName().Version, host?.ManifestModule.ModuleVersionId ?? Guid.Empty,
            plugin.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? plugin.GetName().Version?.ToString() ?? "unknown",
            plugin.ManifestModule.ModuleVersionId, Environment.OSVersion.Version, Environment.Version, RuntimeInformation.ProcessArchitecture,
            features, settings.PreviewCache, settings.ExportCache, settings.NvencOutput, GpuMemoryController.DiagnosticAdapter);
    }
}
