using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Plugin.Update;

namespace NVEncVideoWriterPlugin;

public sealed class NvencVideoFileWriterPlugin : IVideoFileWriterPlugin
{
    public NvencVideoFileWriterPlugin() => HostIntegration.EnsureInstalled();

    private readonly NvencSettings _settings = new();
    private readonly PluginDetailsAttribute _details = new()
    {
        AuthorName = "sabiasagimp4-ai (based on tarutaru247)",
        ContentId = "sabiasagimp4-ai.YMM4Rtx3060Nvenc",
    };

    public string Name => "RTX 3060 NVENC 出力";

    public PluginDetailsAttribute Details => _details;

    public IPluginUpdater? Updater => null;

    public VideoFileWriterOutputPath OutputPathMode => VideoFileWriterOutputPath.File;

    public IVideoFileWriter CreateVideoFileWriter(string path, VideoInfo videoInfo)
    {
        HostIntegration.RequireExportScope();
        var snapshot = new NvencSettings
        {
            Codec = _settings.Codec,
            BitrateKbps = _settings.BitrateKbps,
            Quality = _settings.Quality,
            RateControl = _settings.RateControl,
            HevcAsync = _settings.HevcAsync,
            EnableDebugLog = _settings.EnableDebugLog,
        };
        return new NvencVideoFileWriter(path, videoInfo, snapshot);
    }

    public string GetFileExtention()
    {
        return ".mp4";
    }

    public System.Windows.UIElement GetVideoConfigView(string projectName, VideoInfo videoInfo, int length)
    {
        if (!HostIntegration.NvencOutputEnabled())
            return new System.Windows.Controls.TextBlock
            {
                Margin = new System.Windows.Thickness(8),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Text = "RTX 3060 NVENC 出力は設定で無効になっています。ツール「描画キャッシュ」か、YMM4 の設定（その他 > RTX 3060 NVENC・描画キャッシュ）で有効にしてください。",
            };
        return new NvencConfigView(_settings);
    }

    public bool NeedDownloadResources()
    {
        return false;
    }

    public Task DownloadResources(ProgressMessage progress, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
