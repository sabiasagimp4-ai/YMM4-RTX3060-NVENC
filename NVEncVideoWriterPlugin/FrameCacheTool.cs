using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;

namespace NVEncVideoWriterPlugin;

public sealed class FrameCacheToolPlugin : IToolPlugin
{
    public FrameCacheToolPlugin()
    {
        HostIntegration.EnsureInstalled();
        PluginSettings.Apply();
        CacheDiagnostics.TryStartEnvironment();
    }
    public string Name => "描画キャッシュ";
    public Type ViewModelType => typeof(FrameCacheToolViewModel);
    public Type ViewType => typeof(FrameCacheToolView);
    public bool AllowMultipleInstances => false;
}

// Which parts of the plugin run: NVENC output, and the cache in YMM4's own preview and in exports, separately.
// Edited in the tool and in YMM4's settings window (Other); the file keeps this type's name for older settings.
public sealed class FrameCacheToolSettings : SettingsBase<FrameCacheToolSettings>
{
    private bool enabled, previewCache, exportCache, nvencOutput = true;
    private int settingsVersion;
    private bool automaticRamBudget = true, cacheFramesWhenIdle = true;
    private int ramLimitMiB = 2048;
    private double idleDelaySeconds = 8;
    private IdleCacheOrder idleOrder;
    private int idleRangeStartFrame, idleRangeEndFrame;
    public int IdleRangeStartFrame { get => idleRangeStartFrame; set => Set(ref idleRangeStartFrame, Math.Max(0, value)); }
    // Exclusive end. Zero means the timeline's end; this is an explicit cache range, not YMM4 selection mirroring.
    public int IdleRangeEndFrame { get => idleRangeEndFrame; set => Set(ref idleRangeEndFrame, Math.Max(0, value)); }

    public bool AutomaticRamBudget { get => automaticRamBudget; set => Set(ref automaticRamBudget, value); }
    public int RamLimitMiB { get => ramLimitMiB; set => Set(ref ramLimitMiB, Math.Clamp(value, 64, 16384)); }

    // Restored preview frames kept on the GPU (VRAM): sized from the adapter's video memory up to the limit, or fixed
    // at the limit. Zero keeps none.
    private bool automaticGpuBudget = true;
    private int gpuLimitMiB = -1;
    private int gpuBudgetMigrationVersion;
    public int GpuBudgetMigrationVersion { get => gpuBudgetMigrationVersion; set => Set(ref gpuBudgetMigrationVersion, value); }
    internal bool GpuBudgetMigrationPending { get; set; }
    public bool AutomaticGpuBudget
    {
        get => automaticGpuBudget;
        set { if (!value && gpuLimitMiB == -1) GpuLimitMiB = 2048; Set(ref automaticGpuBudget, value); }
    }
    public int GpuLimitMiB
    {
        get => gpuLimitMiB;
        set { if (value < 0) AutomaticGpuBudget = true; Set(ref gpuLimitMiB, Math.Clamp(value, -1, 8192)); }
    }
    public bool CacheFramesWhenIdle { get => cacheFramesWhenIdle; set => Set(ref cacheFramesWhenIdle, value); }
    public double IdleDelaySeconds
    {
        get => idleDelaySeconds;
        set => Set(ref idleDelaySeconds, double.IsFinite(value) ? Math.Clamp(value, 0.25, 120) : 8);
    }
    public IdleCacheOrder IdleOrder
    {
        get => idleOrder;
        set => Set(ref idleOrder, Enum.IsDefined(value) ? value : IdleCacheOrder.FromCurrentTime);
    }

    // Settings version 0: one switch for the whole cache, carried over to both cache settings.
    public bool Enabled
    {
        get => enabled;
        set => Set(ref enabled, value);
    }

    // YMM4's own preview (playing, paused, seeking) uses the cache, with idle pre-rendering and the cache bars.
    public bool PreviewCache
    {
        get => previewCache;
        set => Set(ref previewCache, value);
    }

    // Exports in any output format (YMM4's or NVENC) store and reuse frames.
    public bool ExportCache
    {
        get => exportCache;
        set => Set(ref exportCache, value);
    }

    // The output format "NVIDIA NVENC 出力" and the export hook it needs.
    public bool NvencOutput
    {
        get => nvencOutput;
        set => Set(ref nvencOutput, value);
    }

    // The NVENC output's options in YMM4's export dialog (NvencConfigView), kept between sessions.
    private NvencCodec nvencCodec = NvencCodec.H264;
    private int nvencBitrateKbps = 12000;
    private NvencQuality nvencQuality = NvencQuality.Balanced;
    private NvencRateControl nvencRateControl = NvencRateControl.YouTubeRecommended;
    private bool nvencHevcAsync = true, nvencDebugLog;
    public NvencCodec NvencCodec
    {
        get => nvencCodec;
        set => Set(ref nvencCodec, Enum.IsDefined(value) ? value : NvencCodec.H264);
    }
    public int NvencBitrateKbps { get => nvencBitrateKbps; set => Set(ref nvencBitrateKbps, Math.Clamp(value, 100, 200000)); }
    public NvencQuality NvencQuality
    {
        get => nvencQuality;
        set => Set(ref nvencQuality, Enum.IsDefined(value) ? value : NvencQuality.Balanced);
    }
    public NvencRateControl NvencRateControl
    {
        get => nvencRateControl;
        set => Set(ref nvencRateControl, Enum.IsDefined(value) ? value : NvencRateControl.YouTubeRecommended);
    }
    public bool NvencHevcAsync { get => nvencHevcAsync; set => Set(ref nvencHevcAsync, value); }
    public bool NvencDebugLog { get => nvencDebugLog; set => Set(ref nvencDebugLog, value); }

    public int SettingsVersion
    {
        get => settingsVersion;
        set => Set(ref settingsVersion, value);
    }

    // Plugin assemblies (names) whose effects, shapes and items the cache may key like YMM4's own (KnownCode).
    private string[] trustedPlugins = [];
    public string[] TrustedPlugins
    {
        get => trustedPlugins;
        set => Set(ref trustedPlugins, value ?? []);
    }

    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "NVENC・描画キャッシュ";
    public override bool HasSettingView => true;
    public override object? SettingView => new PluginSettingsPanel();
    public override void Initialize()
    {
        if (SettingsVersion < 1)
        {
            PreviewCache = ExportCache = Enabled;
            SettingsVersion = 1;
        }
        // Only the old automatic default is lifted; manual and other limits remain user choices.
        // Persist a separate marker, so selecting Auto + 2048 later is never migrated again.
        if (GpuBudgetMigrationVersion >= 1) return;
        if (AutomaticGpuBudget && GpuLimitMiB == 2048) GpuLimitMiB = -1;
        GpuBudgetMigrationVersion = 1;
        GpuBudgetMigrationPending = true;
    }
}

public sealed class FrameCacheToolViewModel : ITimelineToolViewModel, IDisposable
{
    public void SetTimelineToolInfo(TimelineToolInfo info) => IdleFramePreRenderer.SetTimelineToolInfo(info);
    public void Dispose() => IdleFramePreRenderer.ClearTimelineToolInfo();
}

// The three switches, bound to FrameCacheToolSettings.Default (the tool and YMM4's settings window show the same).
public sealed class PluginSettingsPanel : StackPanel
{
    private readonly CheckBox preview = new() { Content = "プレビューで描画キャッシュを使う（YMM4 標準のプレビューのまま。先読み・キャッシュの帯を含む）" };
    private readonly CheckBox export = new() { Content = "動画出力で描画キャッシュを使う（YMM4 標準・NVENC どちらの出力形式でも）", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox nvenc = new() { Content = "NVIDIA NVENC 出力を使う", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = SystemColors.GrayTextBrush };
    private readonly StackPanel plugins = new() { Margin = new Thickness(12, 2, 0, 0) };
    internal IEnumerable<UIElement> AutomationControls => [preview, export, nvenc];

    public PluginSettingsPanel()
    {
        HostIntegration.EnsureInstalled();
        PluginSettings.Apply();
        Margin = new Thickness(0, 8, 0, 12);
        foreach (var box in new[] { preview, export, nvenc })
            box.Content = new TextBlock { Text = (string)box.Content, TextWrapping = TextWrapping.Wrap };
        Children.Add(preview);
        Children.Add(export);
        Children.Add(nvenc);
        Children.Add(note);
        Children.Add(new TextBlock
        {
            Text = "外部プラグインを使うフレームもキャッシュする（チェックしたプラグインだけ）",
            Margin = new Thickness(0, 10, 0, 0),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(new TextBlock
        {
            Text = "プラグインがアイテムの設定と時刻だけで描くことを前提にします（After Effects のプラグインと同じ前提）。時刻・乱数・報告しないファイル・前のフレーム・他のアイテムを使うプラグインでは、古い絵や違う絵が出ることがあります。そのときはチェックを外し、ツール「描画キャッシュ」の「保存したキャッシュを消去」を押してください。プラグインを更新するとそのフレームは作り直します。YMM4 同梱の Community プラグインは、中身を確認したものだけ自動で対象です。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = SystemColors.GrayTextBrush,
        });
        Children.Add(plugins);
        var settings = FrameCacheToolSettings.Default;
        Bind(preview, nameof(FrameCacheToolSettings.PreviewCache));
        System.Windows.Automation.AutomationProperties.SetAutomationId(preview, "FrameCachePreviewEnabled");
        Bind(export, nameof(FrameCacheToolSettings.ExportCache));
        Bind(nvenc, nameof(FrameCacheToolSettings.NvencOutput));
        int at = 3; // the memory and idle rows go between the three switches and the note
        var automatic = new CheckBox { Content = "空きメモリに応じてRAMを自動配分する", Margin = new Thickness(0, 10, 0, 0) };
        Bind(automatic, nameof(FrameCacheToolSettings.AutomaticRamBudget));
        Children.Insert(at++, automatic);
        AddChoice("RAM上限", nameof(FrameCacheToolSettings.RamLimitMiB),
            new[] { 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 }.Append(settings.RamLimitMiB).Distinct().Order()
                .Select(value => ($"{value:N0} MiB", (object)value)));
        var automaticGpu = new CheckBox { Content = "GPUの空きVRAMに応じてGPU保持を自動配分する", Margin = new Thickness(0, 8, 0, 0) };
        Bind(automaticGpu, nameof(FrameCacheToolSettings.AutomaticGpuBudget));
        Children.Insert(at++, automaticGpu);
        AddChoice("VRAM上限", nameof(FrameCacheToolSettings.GpuLimitMiB),
            new[] { -1, 0, 128, 256, 512, 1024, 2048, 4096, 8192 }.Append(settings.GpuLimitMiB).Distinct().Order()
                .Select(value => (value == -1 ? "Auto（GPU予算から配分）" : value == 0 ? "使わない" : $"{value:N0} MiB", (object)value)));
        // The pre-renderer reads the timeline from the tool (IdleFramePreRenderer.SetTimelineToolInfo).
        var idle = new CheckBox
        {
            Content = new TextBlock { Text = "停止中にフレームをキャッシュする（ツール「描画キャッシュ」を開いている間）", TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 8, 0, 0),
        };
        Bind(idle, nameof(FrameCacheToolSettings.CacheFramesWhenIdle));
        Children.Insert(at++, idle);
        AddChoice("操作後の待ち時間", nameof(FrameCacheToolSettings.IdleDelaySeconds),
            new double[] { 1, 2, 4, 8, 15, 30, 60, 120 }.Append(settings.IdleDelaySeconds).Distinct().Order()
                .Select(value => ($"{value:g} 秒", (object)value)));
        AddChoice("キャッシュする順序", nameof(FrameCacheToolSettings.IdleOrder),
            [("現在位置から末尾、先頭へ", (object)IdleCacheOrder.FromCurrentTime),
             ("現在位置の前後から", (object)IdleCacheOrder.AroundCurrentTime),
             ("タイムラインの先頭から", (object)IdleCacheOrder.FromStart)]);
        AddFrameRange("先読み開始フレーム", nameof(FrameCacheToolSettings.IdleRangeStartFrame));
        AddFrameRange("終了フレーム（含まない・0は末尾）", nameof(FrameCacheToolSettings.IdleRangeEndFrame));
        // YMM4's settings window creates a panel each time it opens: listen only while shown.
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) => { settings.PropertyChanged += changed; Refresh(); ListPlugins(); };
        Unloaded += (_, _) => settings.PropertyChanged -= changed;

        void Bind(CheckBox box, string property) => box.SetBinding(ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(property) { Source = settings, Mode = System.Windows.Data.BindingMode.TwoWay });

        void AddFrameRange(string label, string property)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { MinWidth = 100 };
            input.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(property)
                { Source = settings, Mode = System.Windows.Data.BindingMode.TwoWay, ValidatesOnExceptions = true });
            row.Children.Add(input); Children.Add(row);
        }

        void AddChoice(string label, string property, IEnumerable<(string Label, object Value)> choices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Width = 140, VerticalAlignment = VerticalAlignment.Center });
            var box = new ComboBox { MinWidth = 180, SelectedValuePath = nameof(ComboBoxItem.Tag) };
            foreach (var choice in choices) box.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            box.SetBinding(Selector.SelectedValueProperty, new System.Windows.Data.Binding(property)
                { Source = settings, Mode = System.Windows.Data.BindingMode.TwoWay });
            row.Children.Add(box);
            Children.Insert(at++, row);
        }
    }

    // One check box per plugin assembly a user added, and per trusted name no longer loaded (so it can be removed).
    private void ListPlugins()
    {
        var settings = FrameCacheToolSettings.Default;
        plugins.Children.Clear();
        var found = KnownCode.ExternalPlugins().ToList();
        foreach (string name in settings.TrustedPlugins)
            if (!found.Any(plugin => string.Equals(plugin.Assembly, name, StringComparison.OrdinalIgnoreCase)))
                found.Add((name, "読み込まれていません"));
        if (found.Count == 0)
        {
            plugins.Children.Add(new TextBlock { Text = "外部プラグインは読み込まれていません。", Foreground = SystemColors.GrayTextBrush });
            return;
        }
        foreach (var (assembly, provides) in found)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = $"{assembly}（{provides}）", TextWrapping = TextWrapping.Wrap },
                IsChecked = settings.TrustedPlugins.Contains(assembly, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 2, 0, 0),
            };
            box.Checked += (_, _) => settings.TrustedPlugins = [.. settings.TrustedPlugins.Append(assembly).Distinct(StringComparer.OrdinalIgnoreCase)];
            box.Unchecked += (_, _) => settings.TrustedPlugins = [.. settings.TrustedPlugins.Where(name => !string.Equals(name, assembly, StringComparison.OrdinalIgnoreCase))];
            plugins.Children.Add(box);
        }
    }

    private void Refresh()
    {
        note.Text = (HostIntegration.CacheAvailable ? string.Empty : "このYMM4では自動キャッシュを使えません（理由はツール「描画キャッシュ」に表示）。設定は保存され、使える版で有効になります。")
            + (FrameCacheToolSettings.Default.NvencOutput ? string.Empty
                : "NVENC 出力を切ると、出力形式「NVIDIA NVENC 出力」は選んでも出力できません（一覧には残ります）。次回の起動からは出力用のフックも入れません。")
            + (PluginSettings.SaveError is { } error ? "設定を保存できませんでした: " + error : string.Empty);
    }
}

public sealed class FrameCacheToolView : UserControl
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new CacheToolAutomationPeer(this);

    private sealed class CacheToolAutomationPeer(FrameCacheToolView view) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(view)
    {
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? [];
            foreach (var element in new UIElement[] { view.trace, view.scenario, view.purge, view.status, view.counts }.Concat(view.settingsPanel.AutomationControls))
                if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element) is { } peer && !children.Contains(peer))
                    children.Add(peer);
            return children;
        }
    }
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock counts = new() { Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button trace = new() { Content = "詳細ログを開始", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBox scenario = new() { Text = "manual", Width = 180, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock traceInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button purge = new() { Content = "保存したキャッシュを消去", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PluginSettingsPanel settingsPanel = new();
    private long metricsAt;

    public FrameCacheToolView()
    {
        HostIntegration.EnsureInstalled();
        PluginSettings.Apply();

        var panel = new StackPanel { Margin = new Thickness(12), MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = "描画キャッシュ", FontSize = 18 });
        var traceRow = new StackPanel { Orientation = Orientation.Horizontal };
        System.Windows.Automation.AutomationProperties.SetAutomationId(trace, "CacheTraceToggle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(scenario, "CacheTraceScenario");
        System.Windows.Automation.AutomationProperties.SetAutomationId(purge, "FrameCachePurge");
        System.Windows.Automation.AutomationProperties.SetAutomationId(status, "FrameCacheStatus");
        System.Windows.Automation.AutomationProperties.SetAutomationId(counts, "FrameCacheCounts");
        traceRow.Children.Add(trace); traceRow.Children.Add(scenario);
        panel.Children.Add(traceRow); panel.Children.Add(traceInfo);
        trace.Click += async (_, _) =>
        {
            trace.IsEnabled = false;
            var path = CacheDiagnostics.OutputPath;
            try
            {
                if (CacheDiagnostics.IsRecording) { await CacheDiagnostics.StopAsync(); traceInfo.Text = "ログを保存しました: " + path; }
                else CacheDiagnostics.StartDefault(scenario.Text);
            }
            catch (Exception exception) { traceInfo.Text = "ログを保存できませんでした: " + exception.GetBaseException().Message; }
            finally { trace.IsEnabled = true; Refresh(); }
        };
        scenario.TextChanged += (_, _) => { if (CacheDiagnostics.IsRecording) CacheDiagnostics.MarkScenario(scenario.Text); };
        panel.Children.Add(settingsPanel);
        panel.Children.Add(purge);
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
        panel.Children.Add(error);
        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        purge.Click += async (_, _) =>
        {
            purge.IsEnabled = false;
            error.Text = string.Empty;
            try
            {
                await Task.Run(() =>
                {
                    using var span = CacheTrace.Measure("cache-purge");
                    try { TimelineFrameCache.Clear(); }
                    catch { if (span is not null) span.Outcome = "exception"; throw; }
                });
            }
            catch (Exception exception) { error.Text = "キャッシュを完全に消去できませんでした: " + exception.GetBaseException().Message; }
            finally { purge.IsEnabled = true; Refresh(); }
        };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { HostIntegration.EnsureInstalled(); Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private void Refresh()
    {
        trace.Content = CacheDiagnostics.IsRecording ? "詳細ログを停止" : "詳細ログを開始";
        if (CacheDiagnostics.IsRecording) traceInfo.Text = "記録中: " + CacheDiagnostics.OutputPath + "\n混雑による欠落: " + CacheDiagnostics.DroppedRecords;
        status.Text = HostIntegration.Status + Environment.NewLine + FrameRenderReadiness.Summary
            + Environment.NewLine + TimelineFrameCache.Status + Environment.NewLine + IdleFramePreRenderer.Status
            + Environment.NewLine + CacheMemoryController.Status + Environment.NewLine + GpuMemoryController.Status;
        var store = TimelineFrameCache.StoreIfCreated;
        counts.Text = $"再利用 {TimelineFrameCache.Hits:N0}（同じ画像 {TimelineFrameCache.LiveReuses:N0} / GPU {TimelineFrameCache.GpuHits:N0} / RAM {TimelineFrameCache.RamHits:N0} / ディスク {TimelineFrameCache.DiskHits:N0}）"
            + $" / 新規描画 {TimelineFrameCache.Misses:N0} / 対象外 {TimelineFrameCache.Bypasses:N0}\n"
            + $"プレビュー保存 {TimelineFrameCache.PreviewStored:N0}（描画スレッド {TimelineFrameCache.PreviewStoreMilliseconds:N1} ms/枚）/ 先読み読込 {TimelineFrameCache.ReadAheads:N0}"
            + (store is null ? "\n" : $" / ディスク読込 {store.DiskReads:N0}（{store.DiskReadMilliseconds:N1} ms/枚）/ 書込 {store.DiskWrites:N0}（混雑で見送り {store.DroppedWrites:N0}）\n")
            + $"描画の所要時間 p50/p95: 新規描画 {TimelineFrameCache.RenderTimes} / GPU {TimelineFrameCache.GpuTimes} / RAM {TimelineFrameCache.RamTimes} / ディスク {TimelineFrameCache.DiskTimes} / 同じ画像 {TimelineFrameCache.LiveTimes}\n"
            + $"GPU {TimelineFrameCache.GpuBytes / 1048576.0:N1} MiB（保持 {TimelineFrameCache.GpuRetainedBytesNow / 1048576.0:N0} / {TimelineFrameCache.GpuRetentionBudgetNow / 1048576.0:N0} MiB、先回り転送 {TimelineFrameCache.GpuReadAheads:N0}）/ RAM {(store?.RamBytes ?? 0) / 1048576.0:N0} / {(store?.RamBudget ?? 0) / 1048576.0:N0} MiB（設定上限 {CacheMemoryController.Maximum / 1048576.0:N0} MiB）"
            + $" / ディスク {(store?.DiskBytes ?? 0) / 1048576.0:N0} MiB / 4 GiB\n"
            + $"ディスク書込待ち {(store?.QueuedWriteBytes ?? 0) / 1048576.0:N0} MiB（RAMの使用量表示とは別に保持）";
        if (CacheTrace.Enabled && Environment.TickCount64 >= metricsAt)
        {
            metricsAt = Environment.TickCount64 + 5000;
            using var metrics = CacheTrace.Measure("cache-metrics", "state");
            if (metrics is not null) metrics.Detail = counts.Text + Environment.NewLine + status.Text;
        }
    }
}
