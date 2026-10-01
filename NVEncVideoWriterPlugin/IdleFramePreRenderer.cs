using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private const int IdleDelayMilliseconds = 1200;
    private const int MaximumFrames = 30;
    // How far ahead of the playhead idle time is spent (shown by the cache status bars).
    private const int HorizonSeconds = 10;
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

    // The timeline of the attached timeline tool, if any.
    internal static YukkuriMovieMaker.Project.Timeline? CurrentTimeline => Volatile.Read(ref session)?.Info.Timeline;

    internal static void SetTimelineToolInfo(TimelineToolInfo info) => OnUi(() => Attach(info));

    internal static void ClearTimelineToolInfo() => OnUi(() => Attach(null));

    private static void SetEnabled(bool value)
    {
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
        next.NextFrame = Math.Max(0, next.ObservedFrame + 1);
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
        if (!Enabled || !TimelineFrameCache.Enabled || current is null || !HostIntegration.CacheAvailable) return;

        int frame = current.Info.Timeline.CurrentFrame;
        if (frame != current.ObservedFrame)
        {
            current.ObservedFrame = frame;
            current.NextFrame = Math.Max(0, frame + 1);
            MarkActivity(current, "タイムライン移動を検知したため、先読みを中断しました。");
            return;
        }

        bool busy = current.Info.AsyncAwaitStatus.IsBusy;
        Volatile.Write(ref current.IsBusy, busy ? 1 : 0);
        if (busy)
        {
            MarkActivity(current, "YMM4が処理中のため、先読みを中断しました。");
            return;
        }
        if (Environment.TickCount64 - Interlocked.Read(ref current.LastActivity) < IdleDelayMilliseconds) return;

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
        if (!IsViewportFresh(viewport))
        {
            SetStatus("最新のプレビュー描画を待っています。");
            return;
        }

        int fps = current.LiveScene.FPS;
        int frameCount = Math.Min(MaximumFrames, fps);
        int start = Math.Max(current.ObservedFrame + 1, Volatile.Read(ref current.NextFrame));
        int horizonEnd = current.ObservedFrame + Math.Max(1, fps) * HorizonSeconds;
        int end = Math.Min(current.Info.Timeline.Length - 1, horizonEnd);
        if (fps <= 0 || frameCount <= 0 || start > end)
        {
            SetStatus($"先読み範囲（{HorizonSeconds}秒分）に到達しました。");
            return;
        }
        end = Math.Min(end, start + frameCount - 1);

        var job = new Job(current);
        lock (gate)
        {
            if (!ReferenceEquals(session, current) || activeJob is not null) return;
            activeJob = job;
            _ = Task.Factory.StartNew(() => RenderBatch(current, job, viewport, current.ObservedFrame, start, end),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        SetStatus($"フレーム {start}～{end} を先読みしています。");
    }

    private static void RenderBatch(Session current, Job job, TimelineFrameCache.PreviewViewport viewport,
        int anchorFrame, int startFrame, int endFrame)
    {
        int rendered = 0;
        try
        {
            if (!current.Tracker.TryCapture(startFrame, out var initial, out string reason))
            {
                SetStatus(reason);
                return;
            }
            using (initial)
            {
                if (!initial!.Validate() || !CanContinue(current, job.Token, anchorFrame)) return;
                var snapshot = YukkuriMovieMaker.Json.Json.LoadFromText<ModelSnapshot>(initial.Model)
                    ?? throw new InvalidDataException("描画状態を読み込めませんでした。");
                var cloneScene = CloneScene(snapshot);
                using var cloneTracker = new KeyDependencyTracker(cloneScene);
                using var source = new TimelineSourceAndDevices(cloneScene);

                for (int frame = startFrame; frame <= endFrame; frame++)
                {
                    if (!CanContinue(current, job.Token, anchorFrame)
                        || !TimelineFrameCache.TryGetLatestPreviewViewport(current.Info.Timeline, current.Info.Scenes, out var latestViewport)
                        || !SameView(latestViewport, viewport) || !IsViewportFresh(latestViewport) || latestViewport.IsPlaying)
                        return;
                    if (!TryCapturePair(current.Tracker, cloneTracker, frame, out var liveCapture, out var cloneCapture, out reason))
                    {
                        SetStatus(reason);
                        return;
                    }
                    using (liveCapture)
                    using (cloneCapture)
                    {
                        if (!CanContinue(current, job.Token, anchorFrame) || !liveCapture!.Validate() || !cloneCapture!.Validate()) return;
                        // Same conversion as TimelineVideoPlayer, so the primed frame is rendered at the exact time
                        // the player will request (a one-tick difference can select another video sample).
                        var time = cloneScene.Timeline.VideoInfo.GetTimeFrom(frame);
                        source.Update(time, TimelineSourceUsage.Playing);
                        if (!CanContinue(current, job.Token, anchorFrame)) return;
                        if (TryPrimeIfCurrent(job.Token, current.LiveScene, cloneScene, source, time, latestViewport, liveCapture, cloneCapture))
                            rendered++;
                        Volatile.Write(ref current.NextFrame, frame + 1);
                    }
                    Thread.Sleep(8);
                }
            }
            SetStatus(rendered == 0
                ? "先読み範囲の確認が完了しました。"
                : $"プレビュー範囲の {rendered} フレームを先読みしました。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatus("先読みをスキップしました: " + error.GetBaseException().Message);
        }
        finally
        {
            lock (gate) if (ReferenceEquals(activeJob, job)) activeJob = null;
            job.Dispose();
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
            || !liveCapture.Validate() || !cloneCapture.Validate())
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
        TimelineSourceAndDevices source, TimeSpan time, TimelineFrameCache.PreviewViewport viewport,
        KeyCapture liveCapture, KeyCapture cloneCapture)
    {
        if (token.IsCancellationRequested || liveCapture.Key != cloneCapture.Key
            || liveCapture.Model != cloneCapture.Model || !liveCapture.Validate() || !cloneCapture.Validate()
            || liveScene.ID != cloneScene.ID || liveScene.Timeline.ID != cloneScene.Timeline.ID
            || viewport.SceneId != cloneScene.ID || viewport.TimelineId != cloneScene.Timeline.ID)
            return false;
        return TimelineFrameCache.TryPrimePreview(source, time, TimelineSourceUsage.Playing, viewport, liveCapture.Key);
    }

    private static bool CanContinue(Session current, CancellationToken token, int anchorFrame) =>
        !token.IsCancellationRequested && Enabled && TimelineFrameCache.Enabled
        && Volatile.Read(ref current.IsBusy) == 0
        && ReferenceEquals(Volatile.Read(ref session), current)
        && current.Info.Timeline.CurrentFrame == anchorFrame;

    // A redraw of the same view only refreshes the timestamp; it must not abort the batch.
    private static bool SameView(TimelineFrameCache.PreviewViewport latest, TimelineFrameCache.PreviewViewport expected) =>
        latest with { LastDrawTimestamp = 0 } == expected with { LastDrawTimestamp = 0 };

    private static bool IsViewportFresh(TimelineFrameCache.PreviewViewport viewport) =>
        viewport.LastDrawTimestamp != 0 && Stopwatch.GetElapsedTime(viewport.LastDrawTimestamp) <= TimeSpan.FromSeconds(10);

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
        if (args.StagingItem.Input is not null && session is { } current)
            MarkActivity(current, "操作を検知したため、先読みを中断しました。");
    }

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
        internal int NextFrame = Math.Max(0, info.Timeline.CurrentFrame + 1);
        internal int IsBusy;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal void OnInvalidated()
        {
            NextFrame = Math.Max(0, Info.Timeline.CurrentFrame + 1);
            ObservedFrame = Info.Timeline.CurrentFrame;
            MarkActivity(this, "プロジェクトの変更を検知したため、先読みを中断しました。");
        }
        internal void OnTimelineChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Timeline.CurrentFrame)) return;
            ObservedFrame = Info.Timeline.CurrentFrame;
            NextFrame = Math.Max(0, ObservedFrame + 1);
            MarkActivity(this, "タイムライン移動を検知したため、先読みを中断しました。");
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
