using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace NVEncVideoWriterPlugin;

// AE-style cache bar: green where the preview frames are in RAM, blue where they are only on disk.
// States are computed off the UI thread for the frames under each pixel column (CacheBarLayout).
internal sealed class CacheStatusBar : FrameworkElement
{
    internal static readonly Brush RamBrush = Frozen(Color.FromRgb(0x3C, 0xC8, 0x3C));
    internal static readonly Brush DiskBrush = Frozen(Color.FromRgb(0x3C, 0x7C, 0xFF));
    private readonly Func<Timeline?> timeline;
    private readonly Func<Timeline, double, (double Offset, double PixelsPerFrame)?> mapping;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    // What the bar shows was computed for these frames and this state; it is computed again only when they change, and
    // at least every FullPollMilliseconds (keys the cache adopts lazily, files verified in the background).
    private const long FullPollMilliseconds = 2000;
    private TimelineFrameCache.ResidencyStamp? shownStamp;
    private int[] shownFrames = [];
    private long shownAt;
    private List<(int X, int Width, byte State)> runs = [];
    private bool busy;

    // mapping(timeline, width): the bar's view of the timeline, or null to show nothing.
    internal CacheStatusBar(Func<Timeline?> timeline, Func<Timeline, double, (double Offset, double PixelsPerFrame)?> mapping)
    {
        this.timeline = timeline;
        this.mapping = mapping;
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }

    private void Poll()
    {
        if (busy) return;
        try
        {
            var current = timeline();
            int width = (int)Math.Ceiling(ActualWidth);
            if (current is null || !TimelineFrameCache.PreviewEnabled || width <= 0 || mapping(current, ActualWidth) is not { } view)
            {
                shownStamp = null;
                Show([]);
                return;
            }
            int[] frames = CacheBarLayout.SampleFrames(view.Offset, view.PixelsPerFrame, width, current.Length, out int[] starts);
            var stamp = TimelineFrameCache.PreviewResidencyStamp(current);
            long now = Environment.TickCount64;
            if (stamp is not null && stamp == shownStamp && now - shownAt < FullPollMilliseconds && frames.AsSpan().SequenceEqual(shownFrames))
                return;
            busy = true;
            Task.Run(() =>
            {
                List<(int, int, byte)> result = [];
                bool known = false;
                try
                {
                    var residency = new byte[frames.Length];
                    if (known = TimelineFrameCache.TryGetPreviewResidency(current, frames, residency))
                        result = CacheBarLayout.Runs(CacheBarLayout.ColumnStates(residency, starts));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
                Dispatcher.BeginInvoke(() =>
                {
                    busy = false;
                    (shownStamp, shownFrames, shownAt) = known ? (stamp, frames, now) : (null, [], 0);
                    Show(result);
                });
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            busy = false;
            shownStamp = null;
            Show([]);
        }
    }

    private void Show(List<(int X, int Width, byte State)> next)
    {
        if (next.SequenceEqual(runs)) return;
        runs = next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var (x, width, state) in runs)
            drawingContext.DrawRectangle(state == 2 ? RamBrush : DiskBrush, null, new Rect(x, 0, width, ActualHeight));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

// Puts a cache bar along the bottom edge of YMM4's timeline ruler. Only where the ruler's code is that of the
// build whose views were read (YMM4 4.56.1.0; HostFeatures.RulerBars): x = frame * TimelineZoom / 100 -
// TimelineViewModel.Viewport.X, as TimelineScaleViewModel places the playhead. Anything unexpected leaves the
// ruler untouched.
internal static class TimelineCacheBars
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<FrameworkElement, CacheStatusBar> attached = new();
    private static Type scaleViewType = null!, timelineViewModelType = null!;
    private static FieldInfo timelineField = null!;
    private static PropertyInfo viewportProperty = null!;
    private static int installed;

    internal static bool TryInstall(Assembly host, out string reason)
    {
        try
        {
            if (!HostFeatures.For(host).RulerBars)
                throw new NotSupportedException("タイムラインの表示を確認していない版です。");
            scaleViewType = host.GetType("YukkuriMovieMaker.Views.TimelineScaleView", true)!;
            timelineViewModelType = host.GetType("YukkuriMovieMaker.ViewModels.TimelineViewModel", true)!;
            timelineField = timelineViewModelType.GetField("timeline", Instance)!;
            viewportProperty = timelineViewModelType.GetProperty("Viewport", Instance)!;
            if (!typeof(UserControl).IsAssignableFrom(scaleViewType) || timelineField?.FieldType != typeof(Timeline)
                || viewportProperty?.PropertyType.GetProperty("Value")?.PropertyType != typeof(Rect))
                throw new NotSupportedException("タイムラインの表示構造が想定と異なります。");
            if (Interlocked.Exchange(ref installed, 1) == 0)
            {
                EventManager.RegisterClassHandler(scaleViewType, FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
                Application.Current?.Dispatcher.BeginInvoke(AttachExisting);
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "タイムラインのキャッシュバーは表示しません: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { if (sender is FrameworkElement view) Attach(view); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }

    private static void AttachExisting()
    {
        try
        {
            foreach (Window window in Application.Current.Windows) Walk(window);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }

        static void Walk(DependencyObject node)
        {
            if (scaleViewType.IsInstanceOfType(node) && node is FrameworkElement view) Attach(view);
            for (int i = 0, count = VisualTreeHelper.GetChildrenCount(node); i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void Attach(FrameworkElement view)
    {
        if (attached.TryGetValue(view, out _) || view is not UserControl { Content: Grid grid }) return;
        var bar = new CacheStatusBar(() => FindTimeline(view), (timeline, _) => Map(view, timeline))
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.Children.Add(bar);
        attached.Add(view, bar);
    }

    private static object? FindViewModel(DependencyObject view)
    {
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: { } context } && timelineViewModelType.IsInstanceOfType(context))
                return context;
        return null;
    }

    private static Timeline? FindTimeline(DependencyObject view) =>
        FindViewModel(view) is { } model ? timelineField.GetValue(model) as Timeline : null;

    private static (double Offset, double PixelsPerFrame)? Map(DependencyObject view, Timeline timeline)
    {
        if (FindViewModel(view) is not { } model || viewportProperty.GetValue(model) is not { } viewport) return null;
        if (viewport.GetType().GetProperty("Value")?.GetValue(viewport) is not Rect rect) return null;
        double pixelsPerFrame = SettingsBase<YukkuriMovieMaker.Settings.YMMSettings>.Default.TimelineZoom / 100.0;
        return double.IsFinite(rect.X) && pixelsPerFrame > 0 ? (rect.X, pixelsPerFrame) : null;
    }
}
