using System.ComponentModel;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

namespace NVEncVideoWriterPlugin;

internal sealed class KeyDependencyTracker : IDisposable
{
    private const long MaximumFingerprintBytes = 4L * 1024 * 1024 * 1024;
    private const int FingerprintChunk = 128;
    private static readonly IReadOnlyDictionary<string, FileFingerprint> EmptyFingerprints = new Dictionary<string, FileFingerprint>();
    private const long SettleMilliseconds = 250;
    private static readonly SemaphoreSlim FingerprintSlot = new(1, 1);
    private readonly Scene scene;
    private readonly object gate = new();
    private readonly List<Action> unsubscribe = [];
    private long revision;
    private long lastInvalidated = long.MinValue / 2;
    private long cachedRevision = -1;
    private string cachedKey = string.Empty;
    private string cachedReason = string.Empty;
    private string cachedPartialReason = string.Empty;
    private FrameDependencyIndex? cachedFrames;
    private readonly Dictionary<FrameDependencyIndex.Dependencies, string> frameKeys = new(ReferenceEqualityComparer.Instance);
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

    // For a copy of a scene whose files another tracker has verified (the idle pre-renderer's clone, which lives for
    // one batch and would otherwise never finish hashing): captures still lease every file and compare it with these
    // fingerprints, so a file changed since then bypasses.
    internal KeyDependencyTracker(Scene scene, IReadOnlyDictionary<string, FileFingerprint>? verified) : this(scene) => fingerprints = verified;

    // Never changed in place: a new verification replaces the whole dictionary.
    internal IReadOnlyDictionary<string, FileFingerprint>? VerifiedFingerprints { get { lock (gate) return fingerprints; } }

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

    public bool TryCapture(out KeyCapture? capture, out string reason) => TryCapture(out capture, out reason, settle: false);

    // settle: the render path passes true so that continuous edits (every one changes the whole-project key)
    // do not re-describe the model on every frame; it bypasses until edits have paused for a moment.
    public bool TryCapture(out KeyCapture? capture, out string reason, bool settle) => Capture(null, out capture, out reason, settle);

    // The key of one root-timeline frame: only what that frame depends on (FrameDependencyIndex), and only its
    // files are verified and leased, so a cost no longer grows with every file of the project.
    public bool TryCapture(int frame, out KeyCapture? capture, out string reason, bool settle = false) =>
        Capture(frame, out capture, out reason, settle);

    private bool Capture(int? frame, out KeyCapture? capture, out string reason, bool settle)
    {
        lock (gate)
        {
            capture = null;
            reason = "描画キャッシュの状態監視は終了しています。";
            if (disposed) return false;
            // Only when still current: repeating it would keep refreshing the settle window forever.
            if (cachedRevision >= 0 && cachedRevision == Revision && (!scene.ParentScenes.AsSpan().SequenceEqual(cachedParents)
                || !FrameCacheKey.SourceReadersMatch(cachedSourceReaders))) Invalidate();
            long before = Revision;
            if (cachedRevision != before)
            {
                if (settle && cachedRevision >= 0 && Environment.TickCount64 - Volatile.Read(ref lastInvalidated) < SettleMilliseconds)
                {
                    reason = "編集中のため、通常描画を使用します。";
                    return false;
                }
                Type[][] sourceReaders;
                try { sourceReaders = FrameCacheKey.CaptureSourceReaderTypes(); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { reason = "読み込みプラグインの状態を確認できません。"; return false; }
                RebuildSubscriptions();
                bool eligible = FrameCacheKey.TryDescribe(scene, sourceReaders, out string model, out string[] paths,
                    out var frames, out string createdReason);
                if (before != Revision || !FrameCacheKey.SourceReadersMatch(sourceReaders))
                {
                    if (before == Revision) Invalidate();
                    reason = "検査中にプロジェクトまたは読み込みプラグインが変更されたため、通常描画を使用します。";
                    return false;
                }
                cachedKey = string.Empty;
                cachedModel = model;
                cachedPaths = paths;
                cachedFrames = frames;
                frameKeys.Clear();
                cachedSourceReaders = sourceReaders;
                cachedParents = scene.ParentScenes.ToArray();
                cachedReason = createdReason;
                cachedEligible = eligible && frames is not null;
                cachedRevision = before;
            }
            reason = cachedReason;
            if (!cachedEligible) return false;
            var dependencies = frame is int at ? cachedFrames!.For(at) : null;
            if (!(dependencies ?? cachedFrames!.Whole).Cacheable)
            {
                reason = "立ち絵（非同期の口パク）か、確認できない素材（未インストールのフォントや外部の場所のファイル）を使うアイテムが映るため、通常描画を使用します。";
                return false;
            }
            string[] files = dependencies?.Files ?? cachedPaths;
            if (files.Length == 0)
            {
                capture = new KeyCapture(this, KeyFor(dependencies, files), cachedModel, before, cachedParents, null);
                return true;
            }
            if (fingerprintTask is not null)
            {
                if (!fingerprintTask.IsCompleted)
                {
                    reason = "外部素材の内容を背景で検査しています。通常描画を使用します。";
                    return false;
                }
                AdoptFingerprints(before);
            }
            // Zero hash budget makes this a metadata-only lease. Cold/changed files are hashed once off-thread.
            if (fingerprints is not null && files.All(fingerprints.ContainsKey)
                && FileDependencyLease.TryAcquire(files, fingerprints, 0, out var lease, out _))
            {
                if (!ValidateRevision(before)) { lease!.Dispose(); reason = "検査中にプロジェクトが変更されました。"; return false; }
                // The key is built from this tracker's fingerprints. The lease can also accept a file by a newer
                // fingerprint that another tracker recorded after the file changed; then this one is out of date.
                var known = fingerprints;
                if (lease!.Fingerprints.All(pair => known.TryGetValue(pair.Key, out var own) && own == pair.Value))
                {
                    capture = new KeyCapture(this, KeyFor(dependencies, files), cachedModel, before, cachedParents, lease);
                    reason = string.Empty;
                    return true;
                }
                lease.Dispose();
            }
            long now = Environment.TickCount64;
            StartFingerprinting(before, now);
            reason = cachedPartialReason.Length != 0 ? "外部素材の一部を検証できません: " + cachedPartialReason
                : now < nextFingerprintAttempt && !string.IsNullOrEmpty(cachedReason)
                ? cachedReason : "外部素材の内容確認を準備中のため、通常描画を使用します。";
            return false;
        }
    }

    // For display only (cache status bars): the keys of these frames from the current description, without
    // verifying or leasing files. False while an edit is not described yet; null for frames with unhashed files.
    // Frames another tracker stored (the idle pre-renderer, export) can be ones this tracker never captured, so
    // their files are verified here in the background too.
    internal bool TryPeekFrameKeys(IReadOnlyList<int> frames, string?[] keys, out string model)
    {
        lock (gate)
        {
            model = string.Empty;
            if (disposed || cachedRevision != Revision || !cachedEligible || cachedFrames is null) return false;
            if (fingerprintTask is { IsCompleted: true }) AdoptFingerprints(cachedRevision);
            bool unverified = false;
            for (int i = 0; i < frames.Count; i++)
            {
                var dependencies = cachedFrames.For(frames[i]);
                bool verified = dependencies.Files.Length == 0
                    || (fingerprints is not null && dependencies.Files.All(fingerprints.ContainsKey));
                unverified |= dependencies.Cacheable && !verified;
                keys[i] = dependencies.Cacheable && verified ? KeyFor(dependencies, dependencies.Files) : null;
            }
            if (unverified && fingerprintTask is null) StartFingerprinting(cachedRevision, Environment.TickCount64);
            model = cachedModel;
            return true;
        }
    }

    // Under gate, with a finished fingerprintTask: keeps its fingerprints if they describe this revision's files.
    private void AdoptFingerprints(long current)
    {
        if (fingerprintRevision == current)
        {
            if (fingerprintTask!.IsCompletedSuccessfully && fingerprintTask.Result.Files is { } fingerprinted)
            {
                fingerprints = fingerprinted;
                cachedKey = string.Empty;
                frameKeys.Clear();
                cachedReason = string.Empty;
                // Files that could not be verified only disable the frames that use them; retry later.
                if (fingerprintTask.Result.Reason.Length != 0)
                {
                    cachedPartialReason = fingerprintTask.Result.Reason;
                    nextFingerprintAttempt = Environment.TickCount64 + 5000;
                }
            }
            else
            {
                cachedReason = fingerprintTask.IsCompletedSuccessfully ? fingerprintTask.Result.Reason
                    : "外部素材の内容確認に失敗しました。しばらくして再試行します。";
                nextFingerprintAttempt = Environment.TickCount64 + 1000;
            }
        }
        fingerprintTask = null;
        fingerprintCancellation = null;
    }

    // Under gate, with no fingerprintTask: verifies the project's files in the background (one tracker at a time).
    private void StartFingerprinting(long current, long now)
    {
        if (now >= nextFingerprintAttempt && FingerprintSlot.Wait(0))
        {
            var cancellation = new CancellationTokenSource();
            fingerprintCancellation = cancellation;
            fingerprintRevision = current;
            cachedPartialReason = string.Empty;
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
    }

    // Called under gate with verified fingerprints for `files`.
    private string KeyFor(FrameDependencyIndex.Dependencies? dependencies, string[] files)
    {
        var known = fingerprints ?? EmptyFingerprints;
        if (dependencies is null)
        {
            if (cachedKey.Length == 0)
                cachedKey = FrameCacheKey.FromFingerprints(cachedModel, files.Length == 0 ? EmptyFingerprints : Subset(known, files));
            return cachedKey;
        }
        if (frameKeys.TryGetValue(dependencies, out var key)) return key;
        key = FrameCacheKey.FromFingerprints(dependencies.Content, files.Length == 0 ? EmptyFingerprints : Subset(known, files));
        if (frameKeys.Count < 4096) frameKeys[dependencies] = key;
        return key;
    }

    private static Dictionary<string, FileFingerprint> Subset(IReadOnlyDictionary<string, FileFingerprint> all, string[] files) =>
        files.ToDictionary(path => path, path => all[path], StringComparer.OrdinalIgnoreCase);

    // Chunks keep each lease under FileDependencyLease's per-lease limit; a failing chunk is retried file by file
    // so that one unverifiable file only disables the frames that use it.
    private static (IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason) Fingerprint(string[] paths, IReadOnlyDictionary<string, FileFingerprint>? previous, CancellationToken token)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        string reason = string.Empty;
        foreach (var chunk in paths.Chunk(FingerprintChunk))
        {
            token.ThrowIfCancellationRequested();
            if (TryAdd(chunk)) continue;
            foreach (var path in chunk)
            {
                token.ThrowIfCancellationRequested();
                TryAdd([path]);
            }
        }
        return (result.Count == 0 ? null : result, reason);

        bool TryAdd(string[] group)
        {
            if (!FileDependencyLease.TryAcquire(group, previous, MaximumFingerprintBytes, out var lease, out string failure, token))
            {
                reason = failure;
                return false;
            }
            using (lease) foreach (var pair in lease!.Fingerprints) result[pair.Key] = pair.Value;
            return true;
        }
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref revision);
        Volatile.Write(ref lastInvalidated, Environment.TickCount64);
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
        var fileTypes = SettingsBase<YukkuriMovieMaker.Settings.FileSettings>.Default.FileExtensions;
        Subscribe(fileTypes);
        foreach (var extension in fileTypes) Subscribe(extension);
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
        if (value is System.Collections.Specialized.INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += CollectionChanged;
            unsubscribe.Add(() => collection.CollectionChanged -= CollectionChanged);
        }
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
    private void CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => Invalidate();
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
