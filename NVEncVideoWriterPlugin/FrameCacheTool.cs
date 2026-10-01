using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;

namespace NVEncVideoWriterPlugin;

public sealed class FrameCacheToolPlugin : IToolPlugin
{
    public FrameCacheToolPlugin()
    {
        HostIntegration.EnsureInstalled();
        bool enabled = HostIntegration.CacheAvailable && FrameCacheToolSettings.Default.Enabled;
        TimelineFrameCache.Enabled = enabled;
        IdleFramePreRenderer.Enabled = enabled;
    }
    public string Name => "描画キャッシュ";
    public Type ViewModelType => typeof(FrameCacheToolViewModel);
    public Type ViewType => typeof(FrameCacheToolView);
    public bool AllowMultipleInstances => false;
}

public sealed class FrameCacheToolSettings : SettingsBase<FrameCacheToolSettings>
{
    private bool enabled;

    public bool Enabled
    {
        get => enabled;
        set => Set(ref enabled, value);
    }

    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "描画キャッシュ";
    public override bool HasSettingView => false;
    public override object? SettingView => null;
    public override void Initialize() { }
}

public sealed class FrameCacheToolViewModel : ITimelineToolViewModel, IDisposable
{
    public void SetTimelineToolInfo(TimelineToolInfo info) => IdleFramePreRenderer.SetTimelineToolInfo(info);
    public void Dispose() => IdleFramePreRenderer.ClearTimelineToolInfo();
}

public sealed class FrameCacheToolView : UserControl
{
    private readonly CheckBox enabled = new() { Content = "描画キャッシュを有効にする", Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock counts = new() { Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button purge = new() { Content = "保存したキャッシュを消去", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public FrameCacheToolView()
    {
        HostIntegration.EnsureInstalled();
        bool initiallyEnabled = HostIntegration.CacheAvailable && FrameCacheToolSettings.Default.Enabled;
        TimelineFrameCache.Enabled = initiallyEnabled;
        IdleFramePreRenderer.Enabled = initiallyEnabled;

        var panel = new StackPanel { Margin = new Thickness(12), MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = "描画キャッシュ", FontSize = 18 });
        panel.Children.Add(enabled);
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "キャッシュ状況（タイムライン全体）", Margin = new Thickness(0, 12, 0, 4) });
        var bar = new CacheStatusBar(() => IdleFramePreRenderer.CurrentTimeline,
            (timeline, width) => (0, width / Math.Max(1, timeline.Length))) { Height = 10 };
        panel.Children.Add(new Border
        {
            Child = bar,
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ControlDarkBrush,
            Background = SystemColors.ControlBrush,
        });
        var legend = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        legend.Inlines.Add(new System.Windows.Documents.Run("■") { Foreground = CacheStatusBar.RamBrush });
        legend.Inlines.Add(" RAM　");
        legend.Inlines.Add(new System.Windows.Documents.Run("■") { Foreground = CacheStatusBar.DiskBrush });
        legend.Inlines.Add(" ディスク　（プレビューの現在の表示倍率・位置で保存されたフレーム。タイムラインの目盛りの下端にも表示します）");
        panel.Children.Add(legend);
        panel.Children.Add(counts);
        panel.Children.Add(purge);
        panel.Children.Add(error);
        Content = panel;
        enabled.IsChecked = initiallyEnabled;
        enabled.Checked += (_, _) => SetEnabled(true);
        enabled.Unchecked += (_, _) => SetEnabled(false);
        purge.Click += async (_, _) =>
        {
            purge.IsEnabled = false;
            error.Text = string.Empty;
            try { await Task.Run(TimelineFrameCache.Clear); }
            catch (Exception exception) { error.Text = "キャッシュを完全に消去できませんでした: " + exception.GetBaseException().Message; }
            finally { purge.IsEnabled = true; Refresh(); }
        };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { HostIntegration.EnsureInstalled(); Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private void SetEnabled(bool value)
    {
        if (value && !HostIntegration.CacheAvailable) { enabled.IsChecked = false; return; }
        FrameCacheToolSettings.Default.Enabled = value;
        TimelineFrameCache.Enabled = value;
        IdleFramePreRenderer.Enabled = value;
        try
        {
            FrameCacheToolSettings.Default.Save();
            error.Text = string.Empty;
        }
        catch (Exception exception)
        {
            error.Text = "設定はこのセッションでは有効ですが、保存できませんでした: " + exception.GetBaseException().Message;
        }
        Refresh();
    }

    private void Refresh()
    {
        enabled.IsEnabled = HostIntegration.CacheAvailable;
        enabled.IsChecked = TimelineFrameCache.Enabled;
        status.Text = HostIntegration.Status + Environment.NewLine + FrameRenderReadiness.Summary
            + Environment.NewLine + TimelineFrameCache.Status + Environment.NewLine + IdleFramePreRenderer.Status;
        var store = TimelineFrameCache.StoreIfCreated;
        counts.Text = $"再利用 {TimelineFrameCache.Hits:N0}（同じ画像 {TimelineFrameCache.LiveReuses:N0} / RAM {TimelineFrameCache.RamHits:N0} / ディスク {TimelineFrameCache.DiskHits:N0}）"
            + $" / 新規描画 {TimelineFrameCache.Misses:N0} / 対象外 {TimelineFrameCache.Bypasses:N0}\n"
            + $"プレビュー保存 {TimelineFrameCache.PreviewStored:N0}（描画スレッド {TimelineFrameCache.PreviewStoreMilliseconds:N1} ms/枚）/ 先読み読込 {TimelineFrameCache.ReadAheads:N0}"
            + (store is null ? "\n" : $" / ディスク読込 {store.DiskReads:N0}（{store.DiskReadMilliseconds:N1} ms/枚）/ 書込 {store.DiskWrites:N0}（混雑で見送り {store.DroppedWrites:N0}）\n")
            + $"GPU {TimelineFrameCache.GpuBytes / 1048576.0:N1} MiB / RAM 上限 256 MiB / ディスク上限 4 GiB";
    }
}
