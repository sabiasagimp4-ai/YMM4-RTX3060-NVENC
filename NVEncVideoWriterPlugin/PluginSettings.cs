namespace NVEncVideoWriterPlugin;

// Applies FrameCacheToolSettings to the running plugin and saves changes, from the tool or YMM4's settings window.
internal static class PluginSettings
{
    private static int subscribed;

    internal static string? SaveError { get; private set; }

    internal static void Apply()
    {
        EnsureSaving();
        ApplyNow(FrameCacheToolSettings.Default);
    }

    // From now on every change is applied and saved (without applying the current settings now).
    internal static void EnsureSaving()
    {
        var settings = FrameCacheToolSettings.Default;
        if (Interlocked.Exchange(ref subscribed, 1) == 0)
            settings.PropertyChanged += (_, _) =>
            {
                ApplyNow(settings);
                try
                {
                    settings.Save();
                    SaveError = null;
                }
                catch (Exception exception) { SaveError = exception.GetBaseException().Message; }
            };
    }

    private static void ApplyNow(FrameCacheToolSettings settings)
    {
        bool available = HostIntegration.CacheAvailable;
        CacheMemoryController.Configure(settings.AutomaticRamBudget, settings.RamLimitMiB * CacheMemoryPolicy.MiB);
        GpuMemoryController.Configure(settings.AutomaticGpuBudget, settings.GpuLimitMiB * GpuMemoryPolicy.MiB);
        TimelineFrameCache.SetEnabled(available && settings.PreviewCache, available && settings.ExportCache);
        IdleFramePreRenderer.Configure(settings.IdleDelaySeconds, settings.IdleOrder, settings.IdleRangeStartFrame, settings.IdleRangeEndFrame);
        IdleFramePreRenderer.Enabled = available && settings.PreviewCache && settings.CacheFramesWhenIdle;
        KnownCode.Trusted = settings.TrustedPlugins;
        // Switched on after start: hook the export now, before the next one begins.
        if (settings.NvencOutput) HostIntegration.EnsureExportHooks(out _);
    }
}
