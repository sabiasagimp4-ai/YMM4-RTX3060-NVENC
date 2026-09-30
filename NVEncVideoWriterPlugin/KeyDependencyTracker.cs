using System.ComponentModel;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

namespace NVEncVideoWriterPlugin;

internal sealed class KeyDependencyTracker : IDisposable
{
    private const long MaximumFingerprintBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim FingerprintSlot = new(1, 1);
    private readonly Scene scene;
    private readonly object gate = new();
    private readonly List<Action> unsubscribe = [];
    private long revision;
    private long cachedRevision = -1;
    private string cachedKey = string.Empty;
    private string cachedReason = string.Empty;
    private bool cachedEligible;
    private string cachedModel = string.Empty;
    private string[] cachedPaths = [];
    private Type[][] cachedSourceReaders = [[], [], []];
    private Guid[] cachedParents = [];
    private IReadOnlyDictionary<string, FileFingerprint>? fingerprints;
    private Task<(IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason)>? fingerprintTask;
    private CancellationTokenSource? fingerprintCancellation;
    private long fingerprintRevision;
    private long nextFingerprintAttempt;
    private bool disposed;

    public KeyDependencyTracker(Scene scene) => this.scene = scene;

    internal event Action? Invalidated;
    public long Revision => Interlocked.Read(ref revision);
    public long CaptureRevision() => Revision;
    public bool ValidateRevision(long capturedRevision) => !Volatile.Read(ref disposed) && Revision == capturedRevision;

    public bool TryGetKey(out string key, out string reason)
    {
        key = string.Empty;
        if (!TryCapture(out var capture, out reason)) return false;
        using (capture) { key = capture!.Key; return capture.Validate(); }
    }

    public bool TryCapture(out KeyCapture? capture, out string reason)
    {
        lock (gate)
        {
            capture = null;
            reason = "描画キャッシュの状態監視は終了しています。";
            if (disposed) return false;
            if (cachedRevision >= 0 && (!scene.ParentScenes.AsSpan().SequenceEqual(cachedParents)
                || !FrameCacheKey.SourceReadersMatch(cachedSourceReaders))) Invalidate();
            long before = Revision;
            if (cachedRevision != before)
            {
                Type[][] sourceReaders;
                try { sourceReaders = FrameCacheKey.CaptureSourceReaderTypes(); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { reason = "読み込みプラグインの状態を確認できません。"; return false; }
                RebuildSubscriptions();
                bool eligible = FrameCacheKey.TryDescribe(scene, sourceReaders, out string model, out string[] paths, out string createdReason);
                if (before != Revision || !FrameCacheKey.SourceReadersMatch(sourceReaders))
                {
                    if (before == Revision) Invalidate();
                    reason = "検査中にプロジェクトまたは読み込みプラグインが変更されたため、通常描画を使用します。";
                    return false;
                }
                cachedKey = string.Empty;
                cachedModel = model;
                cachedPaths = paths;
                cachedSourceReaders = sourceReaders;
                cachedParents = scene.ParentScenes.ToArray();
                cachedReason = createdReason;
                cachedEligible = eligible;
                cachedRevision = before;
            }
            reason = cachedReason;
            if (!cachedEligible) return false;
            if (cachedPaths.Length == 0)
            {
                if (cachedKey.Length == 0) cachedKey = FrameCacheKey.FromFingerprints(cachedModel, new Dictionary<string, FileFingerprint>());
                capture = new KeyCapture(this, cachedKey, cachedModel, before, cachedParents, null);
                return true;
            }
            if (fingerprintTask is not null)
            {
                if (!fingerprintTask.IsCompleted)
                {
                    reason = "外部素材の内容を背景で検査しています。通常描画を使用します。";
                    return false;
                }
                if (fingerprintRevision == before)
                {
                    if (fingerprintTask.IsCompletedSuccessfully)
                    {
                        var result = fingerprintTask.Result;
                        if (result.Files is not null) { fingerprints = result.Files; cachedKey = string.Empty; cachedReason = string.Empty; }
                        else { cachedReason = result.Reason; nextFingerprintAttempt = Environment.TickCount64 + 1000; }
                    }
                    else
                    {
                        cachedReason = "External file fingerprinting failed; retrying shortly.";
                        nextFingerprintAttempt = Environment.TickCount64 + 1000;
                    }
                }
                fingerprintTask = null;
                fingerprintCancellation = null;
            }
            // Zero hash budget makes this a metadata-only lease. Cold/changed files are hashed once off-thread.
            if (fingerprints is not null && FileDependencyLease.TryAcquire(cachedPaths, fingerprints, 0, out var lease, out _))
            {
                if (!ValidateRevision(before)) { lease!.Dispose(); reason = "検査中にプロジェクトが変更されました。"; return false; }
                if (cachedKey.Length == 0) cachedKey = FrameCacheKey.FromFingerprints(cachedModel, fingerprints);
                capture = new KeyCapture(this, cachedKey, cachedModel, before, cachedParents, lease);
                reason = string.Empty;
                return true;
            }
            long now = Environment.TickCount64;
            if (now >= nextFingerprintAttempt && FingerprintSlot.Wait(0))
            {
                var cancellation = new CancellationTokenSource();
                fingerprintCancellation = cancellation;
                fingerprintRevision = before;
                string[] paths = cachedPaths;
                var previous = fingerprints;
                CancellationToken token = cancellation.Token;
                fingerprintTask = Task.Run(() =>
                {
                    try { return Fingerprint(paths, previous, token); }
                    finally { FingerprintSlot.Release(); cancellation.Dispose(); }
                });
            }
            else if (now >= nextFingerprintAttempt)
                nextFingerprintAttempt = now + 250;
            reason = now < nextFingerprintAttempt && !string.IsNullOrEmpty(cachedReason)
                ? cachedReason : "External file fingerprinting is being prepared; using the host renderer.";
            return false;
        }
    }

    private static (IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason) Fingerprint(string[] paths, IReadOnlyDictionary<string, FileFingerprint>? previous, CancellationToken token)
    {
        if (!FileDependencyLease.TryAcquire(paths, previous, MaximumFingerprintBytes, out var lease, out string reason, token)) return (null, reason);
        using (lease) return (new Dictionary<string, FileFingerprint>(lease!.Fingerprints, StringComparer.OrdinalIgnoreCase), string.Empty);
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref revision);
        // Cancellation is checked between 64 KiB reads; never wait for file I/O from an editor event.
        try { Volatile.Read(ref fingerprintCancellation)?.Cancel(); } catch (ObjectDisposedException) { }
        try { Invalidated?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }

    private void RebuildSubscriptions()
    {
        ClearSubscriptions();
        Subscribe(scene.Scenes);
        Subscribe(SettingsBase<YukkuriMovieMaker.Settings.YMMSettings>.Default);
        Subscribe(SettingsBase<PluginLoaderSettings>.Default);
        var timelines = scene.Scenes.Timelines.Append(scene.Timeline).Distinct().ToArray();
        foreach (var timeline in timelines)
        {
            Subscribe(timeline);
            Subscribe(timeline.VideoInfo);
            Subscribe(timeline.LayerSettings);
            foreach (var item in timeline.Items) Subscribe(item);
        }
        foreach (var character in timelines.SelectMany(t => t.Items).Select(FrameCacheKey.GetCharacter).OfType<Character>().Distinct())
            Subscribe(character);
        var manager = scene.Scenes.UndoRedoManager;
        manager.Undoed += HistoryChanged;
        manager.Redoed += HistoryChanged;
        manager.HistoryChanged += HistoryChanged;
        unsubscribe.Add(() => { manager.Undoed -= HistoryChanged; manager.Redoed -= HistoryChanged; manager.HistoryChanged -= HistoryChanged; });
    }

    private void Subscribe(object value)
    {
        if (value is INotifyPropertyChanged changed)
        {
            changed.PropertyChanged += PropertyChanged;
            unsubscribe.Add(() => changed.PropertyChanged -= PropertyChanged);
        }
        if (value is INotifyPropertyChanging changing)
        {
            changing.PropertyChanging += PropertyChanging;
            unsubscribe.Add(() => changing.PropertyChanging -= PropertyChanging);
        }
        if (value is IUndoRedoable undo)
        {
            undo.UndoRedoCommandCreated += UndoCommandCreated;
            unsubscribe.Add(() => undo.UndoRedoCommandCreated -= UndoCommandCreated);
        }
    }

    private static bool IsTimelineUiProperty(object? sender, string? property) => sender is Timeline && property is
        "CurrentFrame" or "SelectedItems" or "SelectedItem" or "GroupedItems" or "SelectedAndGroupedItems";
    private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsTimelineUiProperty(sender, args.PropertyName)) Invalidate();
    }
    private void PropertyChanging(object? sender, PropertyChangingEventArgs args)
    {
        if (!IsTimelineUiProperty(sender, args.PropertyName)) Invalidate();
    }
    private void UndoCommandCreated(object? sender, UndoRedoEventArgs args) => Invalidate();
    private void HistoryChanged(object? sender, EventArgs args) => Invalidate();
    private void ClearSubscriptions() { foreach (var remove in unsubscribe) remove(); unsubscribe.Clear(); }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Volatile.Write(ref disposed, true);
            Invalidate();
            fingerprintCancellation = null;
            ClearSubscriptions();
        }
    }

    internal bool HasParents(ReadOnlySpan<Guid> parents) => scene.ParentScenes.AsSpan().SequenceEqual(parents);
}

internal sealed class KeyCapture : IDisposable
{
    private readonly KeyDependencyTracker tracker;
    private readonly Guid[] parents;
    private readonly FileDependencyLease? lease;
    private int disposed;
    public string Key { get; }
    public string Model { get; }
    public long Revision { get; }
    internal KeyCapture(KeyDependencyTracker tracker, string key, string model, long revision, Guid[] parents, FileDependencyLease? lease)
    { this.tracker = tracker; Key = key; Model = model; Revision = revision; this.parents = parents; this.lease = lease; }
    public bool Validate() => Volatile.Read(ref disposed) == 0 && tracker.ValidateRevision(Revision)
        && tracker.HasParents(parents) && (lease?.VerifyPaths() ?? true) && Volatile.Read(ref disposed) == 0;
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lease?.Dispose(); }
}
