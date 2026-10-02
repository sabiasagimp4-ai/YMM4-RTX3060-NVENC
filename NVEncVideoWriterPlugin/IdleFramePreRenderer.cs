using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YukkuriMovieMaker.ItemEditor;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

internal static class IdleFramePreRenderer
{
    private const int MaximumFrames = 30;
    private static double idleDelaySeconds = 8;
    private static IdleCacheOrder cacheOrder;
    private static int rangeStart, rangeEnd;
    private static readonly object gate = new();
    private static DispatcherTimer? timer;
    private static Session? session;
    private static Job? activeJob;
    private static int enabled;
    private static string status = "アイドル時の先読みは無効です。";
    private static bool inputSubscribed;

    internal static bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => OnUi(() => SetEnabled(value));
    }

    internal static string Status => Volatile.Read(ref status);

    internal static void Configure(double delaySeconds, IdleCacheOrder order, int startFrame = 0, int endFrameExclusive = 0) => OnUi(() =>
    {
        lock (gate)
        {
            if (idleDelaySeconds == delaySeconds && cacheOrder == order && rangeStart == startFrame && rangeEnd == endFrameExclusive) return;
            idleDelaySeconds = delaySeconds;
            cacheOrder = order;
            rangeStart = Math.Max(0, startFrame); rangeEnd = Math.Max(0, endFrameExclusive);
            CancelActiveJobLocked();
            if (session is { } current)
            {
                Interlocked.Exchange(ref current.NextOrdinal, 0);
                Interlocked.Exchange(ref current.LastActivity, Environment.TickCount64);
            }
        }
    });

    // The timeline of the attached timeline tool, if any.
    internal static YukkuriMovieMaker.Project.Timeline? CurrentTimeline => Volatile.Read(ref session)?.Info.Timeline;

    internal static void SetTimelineToolInfo(TimelineToolInfo info) => OnUi(() => Attach(info));

    internal static void ClearTimelineToolInfo() => OnUi(() => Attach(null));

    private static void SetEnabled(bool value)
    {
        if (Enabled == value) return;
        Volatile.Write(ref enabled, value ? 1 : 0);
        if (!value)
        {
            CancelActiveJob();
            if (timer?.IsEnabled == true) timer.Stop();
            UnsubscribeInput();
            SetStatus("アイドル時の先読みは無効です。");
            return;
        }

        if (session is null || !HostIntegration.CacheAvailable)
        {
            SetStatus("YMM4のタイムライン連携を待っています。");
            return;
        }
        SubscribeInput();
        StartTimer();
        Interlocked.Exchange(ref session.LastActivity, Environment.TickCount64);
        SetStatus("プレビューがアイドル状態になるのを待っています。");
    }

    private static void Attach(TimelineToolInfo? info)
    {
        Session? previous;
        Session? next = null;
        if (info is not null)
        {
            var scene = new Scene(info.Timeline, info.Scenes, []);
            next = new Session(info, scene);
            next.Tracker.Invalidated += next.OnInvalidated;
            info.Timeline.PropertyChanged += next.OnTimelineChanged;
        }

        lock (gate)
        {
            previous = session;
            session = next;
            CancelActiveJobLocked();
        }
        if (previous is not null) _ = Task.Run(previous.Dispose);
        if (next is null)
        {
            if (timer?.IsEnabled == true) timer.Stop();
            SetStatus("タイムラインを待っています。");
            return;
        }

        next.ObservedFrame = info!.Timeline.CurrentFrame;
        next.NextOrdinal = 0;
        if (Enabled)
        {
            SubscribeInput();
            StartTimer();
            SetStatus("プレビューがアイドル状態になるのを待っています。");
        }
    }

    private static void StartTimer()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        timer ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick -= Tick;
        timer.Tick += Tick;
        if (!timer.IsEnabled) timer.Start();
    }

    private static void Tick(object? sender, EventArgs args)
    {
        var current = session;
        if (!Enabled || !TimelineFrameCache.PreviewEnabled || current is null || !HostIntegration.CacheAvailable) return;

        int frame = current.Info.Timeline.CurrentFrame;
        if (frame != current.ObservedFrame)
        {
            lock (gate)
            {
                MarkActivity(current, "タイムライン移動を検知したため、先読みを中断しました。");
                current.ObservedFrame = frame;
                Interlocked.Exchange(ref current.NextOrdinal, 0);
            }
            return;
        }

        bool busy = current.Info.AsyncAwaitStatus.IsBusy;
        Volatile.Write(ref current.IsBusy, busy ? 1 : 0);
        if (busy)
        {
            MarkActivity(current, "YMM4が処理中のため、先読みを中断しました。");
            return;
        }
        if (Environment.TickCount64 - Interlocked.Read(ref current.LastActivity) < idleDelaySeconds * 1000) return;

        lock (gate)
        {
            if (!ReferenceEquals(session, current) || activeJob is not null) return;
        }
        if (!TimelineFrameCache.TryGetLatestPreviewViewport(current.Info.Timeline, current.Info.Scenes, out var viewport))
        {
            SetStatus("アクティブなプレビュー画面を待っています。");
            return;
        }
        if (viewport.IsPlaying)
        {
            MarkActivity(current, "プレビュー再生中のため、先読みを中断しました。");
            return;
        }
        if (viewport.LastDrawTimestamp <= 0)
        {
            SetStatus("最新のプレビュー描画を待っています。");
            return;
        }

        var normalizedView = viewport with { LastDrawTimestamp = 0, IsPlaying = false };
        lock (gate)
        {
            long cacheGeneration = TimelineFrameCache.CacheGeneration;
            if (current.PlannedView != normalizedView || current.CacheGeneration != cacheGeneration)
            {
                CancelActiveJobLocked();
                Interlocked.Exchange(ref current.NextOrdinal, 0);
                current.PlannedView = normalizedView;
                current.CacheGeneration = cacheGeneration;
            }
        }
        if (TimelineFrameCache.StoreIfCreated is { } cache && cache.RamBudget < 32L + 4L * viewport.Width * viewport.Height)
        {
            SetStatus("1フレームを保存できるRAMの空きを待っています。");
            return;
        }

        var range = IdleFramePlan.Range(current.Info.Timeline.Length, rangeStart, rangeEnd);
        int length = range.End - range.Start;
        long start = Interlocked.Read(ref current.NextOrdinal);
        long end = Math.Min(length - 1L, start + MaximumFrames - 1);
        if (current.LiveScene.FPS <= 0 || start > end)
        {
            SetStatus("指定範囲の停止中キャッシュを確認しました。");
            return;
        }
        var job = new Job(current);
        lock (gate)
        {
            if (!ReferenceEquals(session, current) || activeJob is not null) return;
            activeJob = job;
            _ = Task.Factory.StartNew(() => RenderBatch(current, job, viewport, frame, start, end),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        SetStatus($"停止中キャッシュを進めています（{start:N0} / {length:N0} フレーム）。");
    }

    private static void RenderBatch(Session current, Job job, TimelineFrameCache.PreviewViewport viewport,
        int anchorFrame, long startOrdinal, long endOrdinal)
    {
        int rendered = 0, skipped = 0, normal = 0, unavailable = 0;
        bool finished = false;
        try
        {
            if (!CanContinue(current, job.Token, anchorFrame)) return;
            // Frames that always render normally (a tachie, a plugin's code) are passed over, here and below:
            // stopping at one would retry it on every idle tick and never read further ahead.
            KeyCapture? initial = null;
            string reason = string.Empty;
            long first = startOrdinal;
            var range = IdleFramePlan.Range(current.Info.Timeline.Length, job.RangeStart, job.RangeEnd);
            var order = job.Order;
            while (first <= endOrdinal && IdleFramePlan.TryGetFrame(range.Start, range.End, anchorFrame, order, first, out int candidate)
                && !current.Tracker.TryCapture(candidate, out initial, out reason) && current.Tracker.RendersNormally(candidate)) first++;
            if (initial is null)
            {
                if (first > endOrdinal)
                {
                    Advance(current, job, endOrdinal + 1);
                    SetStatus("通常描画が必要なフレームを飛ばし、停止中キャッシュを続けます。");
                }
                else SetStatus(reason);
                return;
            }
            normal += (int)(first - startOrdinal);
            using (initial)
            {
                if (!initial!.Validate() || !CanContinue(current, job.Token, anchorFrame)) return;
                var snapshot = YukkuriMovieMaker.Json.Json.LoadFromText<ModelSnapshot>(initial.Model)
                    ?? throw new InvalidDataException("描画状態を読み込めませんでした。");
                var cloneScene = CloneScene(snapshot);
                using var cloneTracker = new KeyDependencyTracker(cloneScene, current.Tracker.VerifiedFingerprints);
                using var source = CreateBatchSource(cloneScene);

                for (long ordinal = first; ordinal <= endOrdinal; ordinal++)
                {
                    if (!IdleFramePlan.TryGetFrame(range.Start, range.End, anchorFrame, order, ordinal, out int frame)) return;
                    if (TimelineFrameCache.StoreIfCreated is { } cache && cache.RamBudget < 32L + 4L * viewport.Width * viewport.Height)
                    {
                        SetStatus("1フレームを保存できるRAMの空きを待っています。");
                        return;
                    }
                    if (!CanContinue(current, job.Token, anchorFrame)
                        || !TimelineFrameCache.TryGetLatestPreviewViewport(current.Info.Timeline, current.Info.Scenes, out var latestViewport)
                        || !SameView(latestViewport, viewport) || latestViewport.IsPlaying)
                        return;
                    switch (PrimeFrame(current.Tracker, current.LiveScene, cloneTracker, cloneScene, source,
                        time => source.Update(time, TimelineSourceUsage.Playing), frame, latestViewport,
                        () => CanContinue(current, job.Token, anchorFrame), job.Token, out reason))
                    {
                        case IdleFrameResult.Normal:
                            normal++;
                            Advance(current, job, ordinal + 1);
                            continue;
                        case IdleFrameResult.Stored:
                            skipped++;
                            Advance(current, job, ordinal + 1);
                            continue;
                        case IdleFrameResult.NotKeyed:
                            SetStatus(reason);
                            return;
                        case IdleFrameResult.Stopped:
                            return;
                        case IdleFrameResult.Rendered:
                            rendered++;
                            break;
                        default:
                            unavailable++;
                            break;
                    }
                    Advance(current, job, ordinal + 1);
                    Thread.Yield();
                }
            }
            finished = true;
            string stored = (skipped == 0 ? string.Empty : $"（保存済みの {skipped} フレームは描画せず）")
                + (normal == 0 ? string.Empty : $"（通常描画の {normal} フレームは対象外）")
                + (unavailable == 0 ? string.Empty : $"（保存できない {unavailable} フレームは見送り）");
            SetStatus(rendered == 0
                ? "先読み範囲の確認が完了しました。" + stored
                : $"プレビュー範囲の {rendered} フレームを先読みしました。" + stored);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatus("先読みをスキップしました: " + error.GetBaseException().Message);
        }
        finally
        {
            bool next;
            lock (gate)
            {
                next = finished && ReferenceEquals(activeJob, job) && !job.Token.IsCancellationRequested;
                if (ReferenceEquals(activeJob, job)) activeJob = null;
            }
            job.Dispose();
            // A batch that ran to its end continues with the next one now instead of on the next timer tick (up to
            // 250 ms later). Tick checks every idle condition again.
            if (next) OnUi(() => Tick(null, EventArgs.Empty));
        }
    }

    internal enum IdleFrameResult { Rendered, Unavailable, Stored, Normal, NotKeyed, Stopped }

    // The renderer of one batch's clone (tests drive batches through this and PrimeFrame). Its Updates skip the live
    // preview's cache: PrimeFrame keys and stores its frames itself.
    internal static TimelineSourceAndDevices CreateBatchSource(Scene cloneScene)
    {
        var source = new TimelineSourceAndDevices(cloneScene);
        try { TimelineFrameCache.ExcludeFromPreviewCache(source); }
        catch { source.Dispose(); throw; }
        return source;
    }

    // One frame of a batch: the live and clone keys must agree; a frame already stored (in RAM, or on disk where the
    // preview reads it ahead) is not rendered again; otherwise the clone renders it and it is primed for the view.
    internal static IdleFrameResult PrimeFrame(KeyDependencyTracker liveTracker, Scene liveScene, KeyDependencyTracker cloneTracker,
        Scene cloneScene, object source, Action<TimeSpan> render, int frame, TimelineFrameCache.PreviewViewport viewport,
        Func<bool> canContinue, CancellationToken token, out string reason)
    {
        if (!TryCapturePair(liveTracker, cloneTracker, frame, out var liveCapture, out var cloneCapture, out reason))
            return liveTracker.RendersNormally(frame) && cloneTracker.RendersNormally(frame) ? IdleFrameResult.Normal : IdleFrameResult.NotKeyed;
        using (liveCapture)
        using (cloneCapture)
        {
            if (!canContinue() || !liveCapture!.Validate(files: false) || !cloneCapture!.Validate(files: false)) return IdleFrameResult.Stopped;
            // Same conversion as TimelineVideoPlayer, so the primed frame is rendered at the exact time
            // the player will request (a one-tick difference can select another video sample).
            var time = cloneScene.Timeline.VideoInfo.GetTimeFrom(frame);
            if (TimelineFrameCache.IsPreviewStored(source, time, cloneCapture, viewport)) return IdleFrameResult.Stored;
            render(time);
            if (!canContinue()) return IdleFrameResult.Stopped;
            return TryPrimeIfCurrent(token, liveScene, cloneScene, source, time, viewport, liveCapture, cloneCapture)
                ? IdleFrameResult.Rendered : IdleFrameResult.Unavailable;
        }
    }

    private static bool TryCapturePair(KeyDependencyTracker liveTracker, KeyDependencyTracker cloneTracker, int frame,
        out KeyCapture? liveCapture, out KeyCapture? cloneCapture, out string reason)
    {
        liveCapture = cloneCapture = null;
        if (!liveTracker.TryCapture(frame, out liveCapture, out reason)) return false;
        if (!cloneTracker.TryCapture(frame, out cloneCapture, out reason))
        {
            liveCapture!.Dispose();
            liveCapture = null;
            return false;
        }
        if (liveCapture!.Model != cloneCapture!.Model || liveCapture.Key != cloneCapture.Key
            || !liveCapture.Validate(files: false) || !cloneCapture.Validate(files: false))
        {
            liveCapture.Dispose();
            cloneCapture.Dispose();
            liveCapture = cloneCapture = null;
            reason = "ライブ状態と複製状態が一致しないため、先読みをスキップしました。";
            return false;
        }
        return true;
    }

    internal static bool TryPrimeIfCurrent(CancellationToken token, Scene liveScene, Scene cloneScene,
        object source, TimeSpan time, TimelineFrameCache.PreviewViewport viewport,
        KeyCapture liveCapture, KeyCapture cloneCapture)
    {
        if (token.IsCancellationRequested || liveCapture.Key != cloneCapture.Key
            || liveCapture.Model != cloneCapture.Model || !liveCapture.Validate(files: false) || !cloneCapture.Validate(files: false)
            || liveScene.ID != cloneScene.ID || liveScene.Timeline.ID != cloneScene.Timeline.ID
            || viewport.SceneId != cloneScene.ID || viewport.TimelineId != cloneScene.Timeline.ID)
            return false;
        // The clone's capture keys the frame (its key is the live one's): no third tracker describes and verifies the
        // clone per batch. Its files are resolved again once, right before the store commit.
        return TimelineFrameCache.TryPrimePreviewIfCurrent(source, time, TimelineSourceUsage.Playing, viewport, liveCapture.Key, token, cloneCapture);
    }

    private static bool CanContinue(Session current, CancellationToken token, int anchorFrame) =>
        !token.IsCancellationRequested && Enabled && TimelineFrameCache.PreviewEnabled
        && Volatile.Read(ref current.IsBusy) == 0
        && Interlocked.Read(ref current.CacheGeneration) == TimelineFrameCache.CacheGeneration
        && ReferenceEquals(Volatile.Read(ref session), current)
        && current.Info.Timeline.CurrentFrame == anchorFrame;

    // A cancelled worker must not overwrite the cursor reset by an edit, seek or settings change.
    private static void Advance(Session current, Job job, long ordinal)
    {
        lock (gate)
            if (ReferenceEquals(session, current) && ReferenceEquals(activeJob, job) && !job.Token.IsCancellationRequested)
                Interlocked.Exchange(ref current.NextOrdinal, ordinal);
    }

    // A redraw of the same view only refreshes the timestamp; it must not abort the batch.
    private static bool SameView(TimelineFrameCache.PreviewViewport latest, TimelineFrameCache.PreviewViewport expected) =>
        latest with { LastDrawTimestamp = 0 } == expected with { LastDrawTimestamp = 0 };

    private static readonly MethodInfo? TimelineLength = typeof(Timeline).GetProperty(nameof(Timeline.Length))?.GetSetMethod(nonPublic: true);

    private static Scene CloneScene(ModelSnapshot snapshot)
    {
        if (snapshot.Root == Guid.Empty || snapshot.ParentScenes.Length != 0 || snapshot.Timelines.Length == 0)
            throw new NotSupportedException("アイドル時の先読みはルートシーンのみ対応しています。");
        var cloneScenes = new Scenes(false);
        var timelines = new Dictionary<Guid, Timeline>();
        foreach (var model in snapshot.Timelines)
        {
            if (timelines.ContainsKey(model.ID)) throw new InvalidDataException("描画状態に重複したタイムラインIDがあります。");
            var timeline = new Timeline { ID = model.ID };
            timeline.Items = model.Items.ToImmutableList();
            timeline.VideoInfo.Width = model.VideoInfo.Width;
            timeline.VideoInfo.Height = model.VideoInfo.Height;
            timeline.VideoInfo.FPS = model.VideoInfo.FPS;
            timeline.VideoInfo.Hz = model.VideoInfo.Hz;
            timeline.VideoInfo.BackgroundColor = model.VideoInfo.BackgroundColor;
            timeline.LayerSettings.CopyFrom(model.LayerSettings);
            // Setting Items leaves Length at 1: YMM4 refreshes it on load and edits (private setter), and it can stay
            // longer than the items. It is part of the drawing state, so the clone takes the live value.
            TimelineLength?.Invoke(timeline, [model.Length]);
            if (timeline.Length != model.Length) throw new NotSupportedException("タイムラインの長さを複製できません。");
            timelines.Add(timeline.ID, timeline);
            cloneScenes.AddScene(timeline);
        }
        if (!timelines.TryGetValue(snapshot.Root, out var root)) throw new InvalidDataException("描画状態にルートタイムラインがありません。");

        var characters = snapshot.Characters.ToDictionary(character => character.Name, StringComparer.Ordinal);
        foreach (var item in cloneScenes.Timelines.SelectMany(timeline => timeline.Items))
        {
            switch (item)
            {
                case VoiceItem voice when characters.TryGetValue(voice.CharacterName, out var voiceCharacter):
                    voice.Character = voiceCharacter;
                    break;
                case TachieItem tachie when characters.TryGetValue(tachie.CharacterName, out var tachieCharacter):
                    tachie.Character = tachieCharacter;
                    break;
            }
        }
        return new Scene(root, cloneScenes, snapshot.ParentScenes);
    }

    internal static Scene CloneSceneFromModel(string model) =>
        CloneScene(YukkuriMovieMaker.Json.Json.LoadFromText<ModelSnapshot>(model)
            ?? throw new InvalidDataException("描画状態を読み込めませんでした。"));

    private static void OnInput(object sender, PreProcessInputEventArgs args)
    {
        if (session is { } current && IsUserInput(args.StagingItem.Input))
            MarkActivity(current, "操作を検知したため、先読みを中断しました。");
    }

    private static CursorPoint lastCursor;

    // Keys, text, buttons, wheel, pen and touch, and mouse moves that move the cursor. WPF also raises mouse moves
    // without any input whenever the layout under the cursor may have changed, for example when the cache bars
    // repaint because a frame was stored; counting those let the pre-renderer cancel itself.
    private static bool IsUserInput(InputEventArgs? input)
    {
        switch (input)
        {
            case MouseButtonEventArgs or MouseWheelEventArgs:
                return true;
            case MouseEventArgs when input.RoutedEvent == Mouse.PreviewMouseMoveEvent:
                if (!GetCursorPos(out var cursor)) return true;
                bool moved = cursor.X != lastCursor.X || cursor.Y != lastCursor.Y;
                lastCursor = cursor;
                return moved;
            case MouseEventArgs or KeyboardFocusChangedEventArgs:
                return false;
            case KeyboardEventArgs or TextCompositionEventArgs or StylusEventArgs or TouchEventArgs:
                return true;
            default:
                return false;
        }
    }

    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private static void SubscribeInput()
    {
        if (inputSubscribed) return;
        InputManager.Current.PreProcessInput += OnInput;
        inputSubscribed = true;
    }

    private static void UnsubscribeInput()
    {
        if (!inputSubscribed) return;
        InputManager.Current.PreProcessInput -= OnInput;
        inputSubscribed = false;
    }

    private static void MarkActivity(Session current, string message)
    {
        Interlocked.Exchange(ref current.LastActivity, Environment.TickCount64);
        CancelActiveJob(current);
        SetStatus(message);
    }

    private static void CancelActiveJob(Session? expected = null)
    {
        lock (gate)
        {
            if (expected is null || ReferenceEquals(activeJob?.Session, expected)) CancelActiveJobLocked();
        }
    }

    private static void CancelActiveJobLocked()
    {
        try { activeJob?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            return;
        }
        action();
    }

    private static void SetStatus(string value) => Volatile.Write(ref status, value);

    private sealed class Session(TimelineToolInfo info, Scene scene) : IDisposable
    {
        internal TimelineToolInfo Info { get; } = info;
        internal Scene LiveScene { get; } = scene;
        internal KeyDependencyTracker Tracker { get; } = new(scene);
        internal long LastActivity = Environment.TickCount64;
        internal int ObservedFrame = info.Timeline.CurrentFrame;
        internal long NextOrdinal;
        internal TimelineFrameCache.PreviewViewport? PlannedView;
        internal long CacheGeneration = -1;
        internal int IsBusy;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal void OnInvalidated()
        {
            lock (gate)
            {
                MarkActivity(this, "プロジェクトの変更を検知したため、先読みを中断しました。");
                Interlocked.Exchange(ref NextOrdinal, 0);
                ObservedFrame = Info.Timeline.CurrentFrame;
            }
        }
        internal void OnTimelineChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Timeline.CurrentFrame)) return;
            lock (gate)
            {
                MarkActivity(this, "タイムライン移動を検知したため、先読みを中断しました。");
                ObservedFrame = Info.Timeline.CurrentFrame;
                Interlocked.Exchange(ref NextOrdinal, 0);
            }
        }
        public void Dispose()
        {
            try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
            Tracker.Invalidated -= OnInvalidated;
            Info.Timeline.PropertyChanged -= OnTimelineChanged;
            Tracker.Dispose();
            Lifetime.Dispose();
        }
    }

    private sealed class Job(Session session) : IDisposable
    {
        internal IdleCacheOrder Order { get; } = cacheOrder;
        internal int RangeStart { get; } = rangeStart;
        internal int RangeEnd { get; } = rangeEnd;
        internal Session Session { get; } = session;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token);
        internal CancellationToken Token => Cancellation.Token;
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ModelSnapshot
    {
        public Guid Root { get; set; }
        public Guid[] ParentScenes { get; set; } = [];
        public TimelineSnapshot[] Timelines { get; set; } = [];
        public Character[] Characters { get; set; } = [];
    }

    private sealed class TimelineSnapshot
    {
        public Guid ID { get; set; }
        public IItem[] Items { get; set; } = [];
        public VideoInfo VideoInfo { get; set; } = new();
        public LayerSettings LayerSettings { get; set; } = new();
        public int Length { get; set; }
    }
}
