using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using YukkuriMovieMaker.ItemEditor;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

internal static partial class IdleFramePreRenderer
{
    private const int MaximumFrames = 30;
    private static double idleDelaySeconds = 1;
    private static IdleCacheOrder cacheOrder;
    private static int rangeStart, rangeEnd;
    private static int requestedWorkers;
    private static int workerCount = 1;
    private static readonly object gate = new();
    private static DispatcherTimer? timer;
    private static Session? session;
    private static Job? activeJob;
    private static int enabled;
    private static string status = "アイドル時の先読みは無効です。";

    internal static bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => OnUi(() => SetEnabled(value));
    }

    internal static string Status => Volatile.Read(ref status);
    internal static int WorkerCount => Volatile.Read(ref workerCount);

    internal static void Configure(double delaySeconds, IdleCacheOrder order, int startFrame = 0, int endFrameExclusive = 0,
        int workers = 0) => OnUi(() =>
    {
        lock (gate)
        {
            workers = Math.Clamp(workers, 0, 4);
            if (idleDelaySeconds == delaySeconds && cacheOrder == order && rangeStart == startFrame && rangeEnd == endFrameExclusive
                && requestedWorkers == workers) return;
            idleDelaySeconds = delaySeconds;
            cacheOrder = order;
            requestedWorkers = workers;
            Volatile.Write(ref workerCount, 1);
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
            Volatile.Write(ref workerCount, 1);
            CancelActiveJob();
            if (timer?.IsEnabled == true) timer.Stop();
            DropAllRenderers();
            SetStatus("アイドル時の先読みは無効です。");
            return;
        }

        if (session is null || !HostIntegration.CacheAvailable)
        {
            SetStatus("YMM4のタイムライン連携を待っています。");
            return;
        }
        StartTimer();
        Interlocked.Exchange(ref session.LastActivity, Environment.TickCount64);
        SetStatus("プレビューの描画を待っています。");
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
            Volatile.Write(ref workerCount, 1);
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
            StartTimer();
            SetStatus("プレビューの描画を待っています。");
        }
    }

    private static void StartTimer()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        timer ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            // The shortest wait after an edit is 0.1 s.
            Interval = TimeSpan.FromMilliseconds(100),
        };
        timer.Tick -= Tick;
        timer.Tick += Tick;
        if (!timer.IsEnabled) timer.Start();
    }

    // Runs while paused, while playing (ahead of the playhead) and after seeks. Only an edit of the project (after the
    // wait), YMM4 being busy, and a change of the view stop it: input that changes nothing in the preview (selecting,
    // scrolling, menus) does not.
    private static void Tick(object? sender, EventArgs args)
    {
        var current = session;
        if (!Enabled || !TimelineFrameCache.PreviewEnabled || current is null || !HostIntegration.CacheAvailable) return;

        bool viewed = TimelineFrameCache.TryGetLatestPreviewViewport(current.Info.Timeline, current.Info.Scenes, out var viewport);
        int frame = current.Info.Timeline.CurrentFrame;
        ObserveFrame(current, frame, viewed && viewport.IsPlaying);

        bool busy = current.Info.AsyncAwaitStatus.IsBusy;
        Volatile.Write(ref current.IsBusy, busy ? 1 : 0);
        if (busy)
        {
            Volatile.Write(ref workerCount, 1);
            MarkActivity(current, "YMM4が処理中のため、先読みを中断しました。");
            return;
        }
        if (Environment.TickCount64 - Interlocked.Read(ref current.LastActivity) < idleDelaySeconds * 1000) return;

        lock (gate)
        {
            if (!ReferenceEquals(session, current) || activeJob is not null) return;
        }
        if (!viewed)
        {
            SetStatus("アクティブなプレビュー画面を待っています。");
            return;
        }
        if (viewport.LastDrawTimestamp <= 0)
        {
            SetStatus("最新のプレビュー描画を待っています。");
            return;
        }

        bool playing = viewport.IsPlaying;
        var normalizedView = viewport.Normalized;
        var range = IdleFramePlan.Range(current.Info.Timeline.Length, rangeStart, rangeEnd);
        int length = range.End - range.Start;
        int anchor = frame;
        lock (gate)
        {
            long cacheGeneration = TimelineFrameCache.CacheGeneration;
            if (current.PlannedView != normalizedView || current.CacheGeneration != cacheGeneration)
            {
                CancelActiveJobLocked(dropRenderers: false);
                Interlocked.Exchange(ref current.NextOrdinal, 0);
                current.PlannedView = normalizedView;
                current.CacheGeneration = cacheGeneration;
            }
            if (playing)
            {
                // Forward from the frames the player reaches after a worker could finish one. The plan keeps its start
                // from batch to batch, and starts again ahead of the playhead once the playhead has passed its next
                // frame (before the plan wraps to the range's start).
                long ahead = frame + (long)IdleSchedule.Lead(current.LiveScene.FPS, Volatile.Read(ref current.FrameMilliseconds));
                long next = (long)current.PlaybackAnchor + Interlocked.Read(ref current.NextOrdinal);
                if (!current.PlannedPlaying || (next < ahead && next < range.End))
                {
                    current.PlaybackAnchor = (int)Math.Clamp(ahead, range.Start, Math.Max(range.Start, range.End - 1));
                    Interlocked.Exchange(ref current.NextOrdinal, 0);
                }
                anchor = current.PlaybackAnchor;
            }
            else if (current.PlannedPlaying) Interlocked.Exchange(ref current.NextOrdinal, 0);
            current.PlannedPlaying = playing;
        }
        if (TimelineFrameCache.StoreIfCreated is { } cache && cache.RamBudget < 32L + 4L * viewport.Width * viewport.Height)
        {
            SetStatus("1フレームを保存できるRAMの空きを待っています。");
            return;
        }

        long start = Interlocked.Read(ref current.NextOrdinal);
        long end = Math.Min(length - 1L, start + MaximumFrames - 1);
        if (current.LiveScene.FPS <= 0 || start > end)
        {
            SetStatus("指定範囲の停止中キャッシュを確認しました。");
            return;
        }
        int count = IdleWorkerPolicy.Count(requestedWorkers, Environment.ProcessorCount, GpuMemoryController.LatestSample, measuredWorkerBytes: MeasuredWorkerBytes);
        Volatile.Write(ref workerCount, count);
        var job = new Job(current, start, end, count, playing, frame, anchor);
        lock (gate)
        {
            if (!ReferenceEquals(session, current) || activeJob is not null) return;
            activeJob = job;
            Post(() => RenderBatch(current, job, viewport, start, end));
        }
        SetStatus(playing
            ? $"再生位置の先を描いています（描画器 {count} 本）。"
            : $"停止中キャッシュを進めています（描画器 {count} 本、{start:N0} / {length:N0} フレーム）。");
    }

    // A change of the current frame: playback goes on (the batch passes over what the player reaches); a seek ends the
    // batch, and the next tick plans around the new position at once. The renderers stay (the project did not change).
    private static void ObserveFrame(Session current, int frame, bool playing)
    {
        lock (gate)
        {
            switch (IdleSchedule.Classify(current.ObservedFrame, frame, playing, current.LiveScene.FPS))
            {
                case IdleSchedule.FrameChange.Playback:
                    current.ObservedFrame = frame;
                    return;
                case IdleSchedule.FrameChange.Seek:
                    current.ObservedFrame = frame;
                    current.PlannedPlaying = false; // a playing plan starts again at the new position
                    Interlocked.Exchange(ref current.NextOrdinal, 0);
                    if (activeJob is not null && ReferenceEquals(activeJob.Session, current))
                    {
                        CancelActiveJobLocked(dropRenderers: false);
                        SetStatus("シークを検知したため、新しい位置から先読みします。");
                    }
                    return;
            }
        }
    }

    private static void RenderBatch(Session current, Job job, TimelineFrameCache.PreviewViewport viewport,
        long startOrdinal, long endOrdinal)
    {
        int rendered = 0, skipped = 0, normal = 0, unavailable = 0, mismatched = 0, played = 0;
        int anchorFrame = job.Anchor;
        bool finished = false;
        try
        {
            if (!CanContinue(current, job)) return;
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
                    for (long skippedOrdinal = startOrdinal; skippedOrdinal <= endOrdinal; skippedOrdinal++) Advance(current, job, skippedOrdinal + 1);
                    SetStatus("通常描画が必要なフレームを飛ばし、停止中キャッシュを続けます。");
                }
                else SetStatus(reason);
                return;
            }
            normal += (int)(first - startOrdinal);
            using (initial)
            {
                if (!initial!.Validate() || !CanContinue(current, job)) return;
                for (long skippedOrdinal = startOrdinal; skippedOrdinal < first; skippedOrdinal++) Advance(current, job, skippedOrdinal + 1);
                int halted = 0;
                DispatchWorkers(job.Workers, worker =>
                {
                    if (!CanContinue(current, job)) { Interlocked.Exchange(ref halted, 1); return; }
                    var batch = RendererFor(current, initial.Model);
                    bool SessionOrdinal(long ordinal) => IdleFramePlan.TryGetFrame(range.Start, range.End, anchorFrame, order, ordinal, out int position)
                        && current.Tracker.IsSessionKeyed(position);
                    foreach (long ordinal in AssignedOrdinals(first, endOrdinal, worker, job.Workers, SessionOrdinal))
                    {
                        if (!IdleFramePlan.TryGetFrame(range.Start, range.End, anchorFrame, order, ordinal, out int frame))
                        { Interlocked.Exchange(ref halted, 1); return; }
                        if (TimelineFrameCache.StoreIfCreated is { } cache && cache.RamBudget < 32L + 4L * viewport.Width * viewport.Height)
                        {
                            SetStatus("1フレームを保存できるRAMの空きを待っています。");
                            Interlocked.Exchange(ref halted, 1); return;
                        }
                        if (requestedWorkers == 0 && IdleWorkerPolicy.Count(0, Environment.ProcessorCount,
                            GpuMemoryController.LatestSample, measuredWorkerBytes: MeasuredWorkerBytes) < job.Workers)
                        {
                            lock (gate) { if (ReferenceEquals(activeJob, job)) CancelActiveJobLocked(); }
                            Volatile.Write(ref workerCount, 1);
                            Interlocked.Exchange(ref halted, 1); return;
                        }
                        if (!CanContinue(current, job)
                            || !TimelineFrameCache.TryGetLatestPreviewViewport(current.Info.Timeline, current.Info.Scenes, out var latestViewport)
                            || latestViewport.Normalized != viewport.Normalized || latestViewport.IsPlaying != job.Playing)
                        { Interlocked.Exchange(ref halted, 1); return; }
                        // Playing: the player draws the frames it reaches before this worker could.
                        if (job.Playing && IdleSchedule.LeftToPlayer(frame, current.Info.Timeline.CurrentFrame,
                            IdleSchedule.Lead(current.LiveScene.FPS, Volatile.Read(ref current.FrameMilliseconds))))
                        {
                            Interlocked.Increment(ref played);
                            Advance(current, job, ordinal + 1);
                            continue;
                        }
                        long renderStarted = Stopwatch.GetTimestamp();
                        using var frameTrace = CacheTrace.Enabled ? CacheTrace.Measure("idle-frame", component: $"worker {worker}",
                            frameTimeTicks: current.Info.Timeline.VideoInfo.GetTimeFrom(frame).Ticks, usage: "Idle") : null;
                        var result = PrimeBatchFrame(current.Tracker, current.LiveScene, batch, frame, latestViewport,
                            () => CanContinue(current, job), job.Token, out string workerReason, allowLive: worker == 0);
                        batch.ObserveMemory();
                        if (result == IdleFrameResult.Rendered)
                            Volatile.Write(ref current.FrameMilliseconds, IdleSchedule.Smooth(Volatile.Read(ref current.FrameMilliseconds),
                                Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds));
                        if (frameTrace is not null)
                        {
                            frameTrace.Outcome = result.ToString();
                            frameTrace.Detail = $"workers={job.Workers}; measured-worker-reserve={MeasuredWorkerBytes}; playing={job.Playing}";
                        }
                        switch (result)
                        {
                            case IdleFrameResult.Normal: Interlocked.Increment(ref normal); break;
                            case IdleFrameResult.Stored: Interlocked.Increment(ref skipped); break;
                            case IdleFrameResult.NotKeyed when workerReason == CloneMismatch: Interlocked.Increment(ref mismatched); break;
                            case IdleFrameResult.NotKeyed:
                                SetStatus(workerReason); Interlocked.Exchange(ref halted, 1); return;
                            case IdleFrameResult.Stopped: Interlocked.Exchange(ref halted, 1); return;
                            case IdleFrameResult.Rendered: Interlocked.Increment(ref rendered); break;
                            default: Interlocked.Increment(ref unavailable); break;
                        }
                        Advance(current, job, ordinal + 1);
                        Thread.Yield();
                    }
                });
                finished = Volatile.Read(ref halted) == 0;
                if (!finished) return;
            }
            finished = true;
            string stored = (skipped == 0 ? string.Empty : $"（保存済みの {skipped} フレームは描画せず）")
                + (normal == 0 ? string.Empty : $"（通常描画の {normal} フレームは対象外）")
                + (unavailable == 0 ? string.Empty : $"（保存できない {unavailable} フレームは見送り）")
                + (mismatched == 0 ? string.Empty : $"（複製すると状態が変わる {mismatched} フレームは対象外）")
                + (played == 0 ? string.Empty : $"（再生が先に描く {played} フレームは任せました）");
            SetStatus(rendered == 0
                ? "先読み範囲の確認が完了しました。" + stored
                : (job.Playing ? $"再生位置の先の {rendered} フレームを描きました。" : $"プレビュー範囲の {rendered} フレームを先読みしました。") + stored);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            DropRenderer(); // its state after the failure is unknown
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

    // One frame of a batch: the live and clone keys must agree; a frame already stored (in RAM, or on disk where the
    // preview reads it ahead) is not rendered again; otherwise the clone renders it and it is primed for the view.
    // A frame either tracker renders normally is passed over (passing one is never wrong, only not cached): the clone
    // has not even described the model when the live capture fails first, as for a file that failed verification.
    internal static IdleFrameResult PrimeFrame(KeyDependencyTracker liveTracker, Scene liveScene, KeyDependencyTracker cloneTracker,
        Scene cloneScene, object source, Action<TimeSpan> render, int frame, TimelineFrameCache.PreviewViewport viewport,
        Func<bool> canContinue, CancellationToken token, out string reason)
    {
        if ((AnimationTachieDependencies.LeaveToHost(liveScene, frame) || PsdTachieDependencies.LeaveToHost(liveScene, frame)))
        { reason = "口パクの計算枠を再生に残す、または PSD をもう一つ読み込まないため、立ち絵の先読みを見送ります。"; return IdleFrameResult.Normal; }
        if (!TryCapturePair(liveTracker, cloneTracker, frame, out var liveCapture, out var cloneCapture, out reason))
            return liveTracker.RendersNormally(frame) || cloneTracker.RendersNormally(frame) ? IdleFrameResult.Normal : IdleFrameResult.NotKeyed;
        using (liveCapture)
        using (cloneCapture)
        {
            if (!canContinue() || !liveCapture!.Validate(files: false) || !cloneCapture!.Validate(files: false)) return IdleFrameResult.Stopped;
            // Same conversion as TimelineVideoPlayer, so the primed frame is rendered at the exact time
            // the player will request (a one-tick difference can select another video sample).
            var time = cloneScene.Timeline.VideoInfo.GetTimeFrom(frame);
            if (TimelineFrameCache.IsPreviewStored(source, time, cloneCapture, viewport)) return IdleFrameResult.Stored;
            // Before the render: a purge while it renders rejects the frame.
            var ticket = TimelineFrameCache.BeginPrime();
            render(time);
            if (!canContinue()) return IdleFrameResult.Stopped;
            return TryPrimeIfCurrent(token, liveScene, cloneScene, source, time, viewport, liveCapture, cloneCapture, ticket)
                ? IdleFrameResult.Rendered : IdleFrameResult.Unavailable;
        }
    }

    // One frame of a batch, as RenderBatch renders it: a frame keyed by the live objects' identities (identity-seeded
    // randomness) from the live scene, since the clone would draw other random values; any other from the clone.
    internal static IdleFrameResult PrimeBatchFrame(KeyDependencyTracker liveTracker, Scene liveScene, BatchRenderer batch, int frame,
        TimelineFrameCache.PreviewViewport viewport, Func<bool> canContinue, CancellationToken token, out string reason, bool allowLive = true)
    {
        reason = string.Empty;
        if ((AnimationTachieDependencies.LeaveToHost(liveScene, frame) || PsdTachieDependencies.LeaveToHost(liveScene, frame))) return IdleFrameResult.Normal;
        if (liveTracker.IsSessionKeyed(frame))
        {
            if (!allowLive) return IdleFrameResult.Unavailable;
            var live = batch.LiveSourceFor(liveScene);
            return PrimeLiveFrame(liveTracker, liveScene, live, time => live.Update(time, TimelineSourceUsage.Playing),
                frame, viewport, canContinue, token);
        }
        var source = batch.Source;
        return PrimeFrame(liveTracker, liveScene, batch.CloneTracker, batch.CloneScene, source,
            time => source.Update(time, TimelineSourceUsage.Playing), frame, viewport, canContinue, token, out reason);
    }

    // A frame keyed by the live objects' identities (identity-seeded randomness) from a renderer of the live scene
    // itself, stored under the live key: a clone has other objects and so draws other random values. The live model
    // is read off the UI thread as the player's own render thread reads it; the capture is validated again after the
    // render and at the store commit, so an edit meanwhile discards the frame.
    internal static IdleFrameResult PrimeLiveFrame(KeyDependencyTracker liveTracker, Scene liveScene, object source, Action<TimeSpan> render,
        int frame, TimelineFrameCache.PreviewViewport viewport, Func<bool> canContinue, CancellationToken token)
    {
        if ((AnimationTachieDependencies.LeaveToHost(liveScene, frame) || PsdTachieDependencies.LeaveToHost(liveScene, frame))) return IdleFrameResult.Normal;
        if (!liveTracker.TryCapture(frame, out var capture, out _))
            return liveTracker.RendersNormally(frame) && !liveTracker.IsSessionKeyed(frame) ? IdleFrameResult.Normal : IdleFrameResult.Unavailable;
        using (capture)
        {
            if (!canContinue() || !capture!.Validate(files: false)) return IdleFrameResult.Stopped;
            var time = liveScene.Timeline.VideoInfo.GetTimeFrom(frame);
            if (TimelineFrameCache.IsPreviewStored(source, time, capture, viewport)) return IdleFrameResult.Stored;
            var ticket = TimelineFrameCache.BeginPrime();
            render(time);
            if (!canContinue()) return IdleFrameResult.Stopped;
            if (token.IsCancellationRequested || !capture.Validate(files: false)
                || viewport.SceneId != liveScene.ID || viewport.TimelineId != liveScene.Timeline.ID)
                return IdleFrameResult.Unavailable;
            return TimelineFrameCache.TryPrimePreviewIfCurrent(source, time, TimelineSourceUsage.Playing, viewport, capture.Key, token, capture, ticket)
                ? IdleFrameResult.Rendered : IdleFrameResult.Unavailable;
        }
    }

    private const string CloneMismatch = "ライブ状態と複製状態が一致しないため、先読みをスキップしました。";

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
        if (!FrameDescriptionJson.SameRenderModel(liveCapture!.Model, cloneCapture!.Model) || liveCapture.Key != cloneCapture.Key
            || !liveCapture.Validate(files: false) || !cloneCapture.Validate(files: false))
        {
            liveCapture.Dispose();
            cloneCapture.Dispose();
            liveCapture = cloneCapture = null;
            reason = CloneMismatch;
            return false;
        }
        return true;
    }

    internal static bool TryPrimeIfCurrent(CancellationToken token, Scene liveScene, Scene cloneScene,
        object source, TimeSpan time, TimelineFrameCache.PreviewViewport viewport,
        KeyCapture liveCapture, KeyCapture cloneCapture, TimelineFrameCache.PrimeTicket? ticket = null)
    {
        if (token.IsCancellationRequested || liveCapture.Key != cloneCapture.Key
            || !FrameDescriptionJson.SameRenderModel(liveCapture.Model, cloneCapture.Model) || !liveCapture.Validate(files: false) || !cloneCapture.Validate(files: false)
            || liveScene.ID != cloneScene.ID || liveScene.Timeline.ID != cloneScene.Timeline.ID
            || viewport.SceneId != cloneScene.ID || viewport.TimelineId != cloneScene.Timeline.ID)
            return false;
        // The clone's capture keys the frame (its key is the live one's): no third tracker describes and verifies the
        // clone per batch. Its files are resolved again once, right before the store commit.
        return TimelineFrameCache.TryPrimePreviewIfCurrent(source, time, TimelineSourceUsage.Playing, viewport, liveCapture.Key, token, cloneCapture, ticket);
    }

    // A paused batch also ends when the current frame moved (a seek the tick has not seen yet); a playing one when the
    // tick saw a seek (it cancels the batch).
    private static bool CanContinue(Session current, Job job) =>
        !job.Token.IsCancellationRequested && Enabled && TimelineFrameCache.PreviewEnabled
        && Volatile.Read(ref current.IsBusy) == 0
        && Interlocked.Read(ref current.CacheGeneration) == TimelineFrameCache.CacheGeneration
        && ReferenceEquals(Volatile.Read(ref session), current)
        && (job.Playing || current.Info.Timeline.CurrentFrame == job.Playhead);

    // A cancelled worker must not overwrite the cursor reset by an edit, seek or settings change.
    private static void Advance(Session current, Job job, long ordinal)
    {
        lock (gate)
            if (ReferenceEquals(session, current) && ReferenceEquals(activeJob, job) && !job.Token.IsCancellationRequested)
                Interlocked.Exchange(ref current.NextOrdinal, job.Cursor.Complete(ordinal - 1));
    }

    private static readonly MethodInfo? TimelineLength = typeof(Timeline).GetProperty(nameof(Timeline.Length))?.GetSetMethod(nonPublic: true);

    private static Scene CloneScene(ModelSnapshot snapshot)
    {
        if (snapshot.Root == Guid.Empty || snapshot.ParentScenes.Length != 0 || snapshot.Timelines.Length == 0)
            throw new NotSupportedException("アイドル時の先読みはルートシーンのみ対応しています。");
        var cloneScenes = HostApi.NewScenes();
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
            HostApi.CopyBackgroundColor(model.VideoInfo, timeline.VideoInfo);
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
                case TachieFaceItem face when characters.TryGetValue(face.CharacterName, out var faceCharacter):
                    face.Character = faceCharacter;
                    break;
            }
        }
        return new Scene(root, cloneScenes, snapshot.ParentScenes);
    }

    internal static Scene CloneSceneFromModel(string model)
    {
        var clone = CloneScene(FrameDescriptionJson.Load<ModelSnapshot>(model)
            ?? throw new InvalidDataException("描画状態を読み込めませんでした。"));
        FrameVoiceCloneState.Restore(clone, model);
        return clone;
    }

    private static void MarkActivity(Session current, string message)
    {
        Volatile.Write(ref workerCount, 1);
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

    // An edit, a session change or disabling also releases the renderers; a seek or a change of the view keeps them for
    // the next batch (same model).
    private static void CancelActiveJobLocked(bool dropRenderers = true)
    {
        try { activeJob?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        // Queue behind each worker's in-flight action: release devices on their owning thread after cancellation.
        if (dropRenderers) DropAllRenderers();
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
        // Whether the last plan was a playing one, and where it started.
        internal bool PlannedPlaying;
        internal int PlaybackAnchor;
        internal long CacheGeneration = -1;
        internal int IsBusy;
        // A worker's smoothed time per rendered frame (IdleSchedule.Lead).
        internal double FrameMilliseconds;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal void OnInvalidated()
        {
            lock (gate)
            {
                MarkActivity(this, "プロジェクトの変更を検知したため、先読みを中断しました。");
                PlannedPlaying = false;
                Interlocked.Exchange(ref NextOrdinal, 0);
                ObservedFrame = Info.Timeline.CurrentFrame;
            }
        }
        internal void OnTimelineChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Timeline.CurrentFrame)) return;
            bool playing = TimelineFrameCache.TryGetLatestPreviewViewport(Info.Timeline, Info.Scenes, out var view) && view.IsPlaying;
            ObserveFrame(this, Info.Timeline.CurrentFrame, playing);
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

    // Playing: frames ahead of the playhead, forward whatever the order setting; paused: around Playhead in the set order.
    private sealed class Job(Session session, long first, long last, int workers, bool playing, int playhead, int anchor) : IDisposable
    {
        internal int Workers { get; } = workers;
        internal IdleCompletionCursor Cursor { get; } = new(first, last);
        internal bool Playing { get; } = playing;
        internal int Playhead { get; } = playhead;
        internal int Anchor { get; } = anchor;
        internal IdleCacheOrder Order { get; } = playing ? IdleCacheOrder.FromCurrentTime : cacheOrder;
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
