namespace NVEncVideoWriterPlugin;

// The output options of one export (the writer takes a copy). Kept between YMM4 sessions in FrameCacheToolSettings.
internal sealed class NvencSettings
{
    public NvencCodec Codec { get; set; } = NvencCodec.H264;
    public int BitrateKbps { get; set; } = 12000;
    public NvencQuality Quality { get; set; } = NvencQuality.Balanced;
    public NvencRateControl RateControl { get; set; } = NvencRateControl.YouTubeRecommended;
    public bool HevcAsync { get; set; } = true;
    public bool EnableDebugLog { get; set; }

    internal static NvencSettings From(FrameCacheToolSettings saved) => new()
    {
        Codec = saved.NvencCodec,
        BitrateKbps = saved.NvencBitrateKbps,
        Quality = saved.NvencQuality,
        RateControl = saved.NvencRateControl,
        HevcAsync = saved.NvencHevcAsync,
        EnableDebugLog = saved.NvencDebugLog,
    };

    internal void SaveTo(FrameCacheToolSettings saved)
    {
        saved.NvencCodec = Codec;
        saved.NvencBitrateKbps = BitrateKbps;
        saved.NvencQuality = Quality;
        saved.NvencRateControl = RateControl;
        saved.NvencHevcAsync = HevcAsync;
        saved.NvencDebugLog = EnableDebugLog;
    }

    // The saved options in YMM4 (PluginSettings writes every change to the settings file); the defaults elsewhere.
    internal static NvencSettings Load()
    {
        if (!HostIntegration.IsHostProcess) return new();
        try
        {
            PluginSettings.EnsureSaving();
            return From(FrameCacheToolSettings.Default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return new(); }
    }

    internal void Save()
    {
        if (!HostIntegration.IsHostProcess) return;
        try { SaveTo(FrameCacheToolSettings.Default); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }
}

public enum NvencCodec
{
    H264,
    H265,
}

public enum NvencQuality
{
    Speed,
    Balanced,
    Quality,
}

public enum NvencRateControl
{
    Fixed,
    Variable,
    YouTubeRecommended,
}
