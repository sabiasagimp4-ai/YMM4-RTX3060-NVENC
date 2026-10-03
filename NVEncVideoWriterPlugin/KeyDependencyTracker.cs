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
    // Describing the whole project again after an edit took 0.5 s for 100 items and 2.1 s for 1000 on the CI runner
    // (before the streaming split). The preview's render thread does it inline for small projects, and for larger ones
    // while the last description stayed short; otherwise in the background, rendering normally until it is done.
    private static readonly long InlineDescribeTicks = System.Diagnostics.Stopwatch.Frequency / 20;
    private const int InlineDescribeItems = 200;
    private Task<Description?>? describeTask;
    private long describeRevision = -1;
    private long lastDescribeTicks = -1;
    private static readonly SemaphoreSlim FingerprintSlot = new(1, 1);
    // The YMMSettings properties the drawing model holds (FrameCacheKey.DrawingSettings), with the older ones they may
    // fall back to. YMM4's renderers read no other property of YMMSettings (4.56.1.0): the rest is the UI's (timeline
    // zoom, volume, layout, ...), which changes often while previewing and must not describe the project again.
    private static readonly HashSet<string> DrawingSettingNames = new(StringComparer.Ordinal)
        { "ZoomMode", "MFSourceReaderMode", "MFSourceReaderMode2", "HardwareDecodeMode", "VoiceUpsamplingMode" };
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
    private ICacheDependencyProvider[] dynamicProviders = [];
    private readonly Dictionary<FrameDependencyIndex.Dependencies, string> frameKeys = new(ReferenceEqualityComparer.Instance);
    private bool cachedEligible;
    private string cachedModel = string.Empty;
    private string[] cachedPaths = [];
    private Type[][] cachedSourceReaders = [[], [], []];
    // KnownCode.Generation the description was made with: trusting another plugin describes the project again.
    private long cachedCode = -1;
    private Guid[] cachedParents = [];
    // FrameCacheKey.DrawingSettings of the description (null before the first one).
    private volatile string? cachedSettings;
    private IReadOnlyDictionary<string, FileFingerprint>? fingerprints;
    private Task<(IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason, IReadOnlyDictionary<string, Unverified> Failed)>? fingerprintTask;
    private CancellationTokenSource? fingerprintCancellation;
    private long fingerprintRevision;
    private long nextFingerprintAttempt;
    // Files a pass could not verify (a link or cloud placeholder, a volume other than a local NTFS one, over the size
    // limit, missing, unreadable), with the reason. Their frames render normally (RendersNormally: the idle
    // pre-renderer passes them), and they are not verified again with the whole project, only on their own: every
    // UnverifiableRetry, or at once when the file looks different (it appeared, its size or write time changed; looked
    // at once a second without opening it).
    internal readonly record struct Unverified(string Reason, (bool Exists, long Length, long Written) Seen);
    private readonly Dictionary<string, Unverified> unverifiable = new(StringComparer.OrdinalIgnoreCase);
    private long nextUnverifiableRetry, nextUnverifiableLook;
    internal static TimeSpan UnverifiableRetry { get; set; } = TimeSpan.FromSeconds(30); // tests shorten it
    private bool disposed;

    public KeyDependencyTracker(Scene scene) => this.scene = scene;

    // For a copy of a scene whose files another tracker has verified (the idle pre-renderer's clone, which lives for
    // the batches of one model and would otherwise not finish hashing first): captures still lease every file and
    // compare it with these fingerprints, so a file changed since then bypasses.
    internal KeyDependencyTracker(Scene scene, IReadOnlyDictionary<string, FileFingerprint>? verified) : this(scene) => fingerprints = verified;

    // Never changed in place: a new verification replaces the whole dictionary.
    internal IReadOnlyDictionary<string, FileFingerprint>? VerifiedFingerprints { get { lock (gate) return fingerprints; } }

    // Tests: whether a fingerprint pass is still running.
    internal bool FingerprintPassRunning { get { lock (gate) return fingerprintTask is { IsCompleted: false }; } }

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
    public bool TryCapture(out KeyCapture? capture, out string reason, bool settle) => Capture(null, out capture, out reason, settle, false);

    // The key of one root-timeline frame: only what that frame depends on (FrameDependencyIndex), and only its
    // files are verified and leased, so a cost no longer grows with every file of the project.
    // background: the caller must not wait for a long description of the project (the preview's render thread).
    public bool TryCapture(int frame, out KeyCapture? capture, out string reason, bool settle = false, bool background = false) =>
        Capture(frame, out capture, out reason, settle, background);

    // True when the current description renders this frame normally whatever its files' state (a tachie, a plugin's
    // code, a file or font that cannot be verified), a file of it was overwritten while YMM4 runs (HostContent), or a
    // file of it failed verification (unverifiable). It stays so until an edit (or a restart, or the file passes a
    // retry), so the idle pre-renderer passes it. So does a frame keyed by the live objects' identities
    // (Dependencies.Session): a clone of the scene draws its randomness otherwise.
    internal bool RendersNormally(int frame)
    {
        lock (gate)
            return !disposed && cachedRevision >= 0 && cachedRevision == Revision && cachedEligible
                && cachedFrames is { } frames && frames.For(frame) is var dependencies
                && (!dependencies.Cacheable || dependencies.Session || dependencies.Files.Any(HostContent.Changed)
                    || dependencies.Files.Any(unverifiable.ContainsKey));
    }

    private sealed record Description(bool Eligible, string Model, string[] Paths, FrameDependencyIndex? Frames, string Reason,
        Type[][] SourceReaders, long Ticks, long Code, string Settings);

    private bool Capture(int? frame, out KeyCapture? capture, out string reason, bool settle, bool background = false)
    {
        lock (gate)
        {
            capture = null;
            reason = "描画キャッシュの状態監視は終了しています。";
            if (disposed) return false;
            // Only when still current: repeating it would keep refreshing the settle window forever.
            if (cachedRevision >= 0 && cachedRevision == Revision && (!scene.ParentScenes.AsSpan().SequenceEqual(cachedParents)
                || !FrameCacheKey.SourceReadersMatch(cachedSourceReaders) || cachedCode != KnownCode.Generation)) Invalidate();
            long before = Revision;
            if (cachedRevision != before)
            {
                if (settle && cachedRevision >= 0 && Environment.TickCount64 - Volatile.Read(ref lastInvalidated) < SettleMilliseconds)
                {
                    reason = "編集中のため、通常描画を使用します。";
                    return false;
                }
                if (describeTask is { IsCompleted: true }) AdoptDescription(before);
            }
            if (cachedRevision != before)
            {
                const string describing = "編集後のプロジェクトを背景で検査しています。通常描画を使用します。";
                if (describeTask is not null) { reason = describing; return false; }
                RebuildSubscriptions();
                if (background && !DescribeInline())
                {
                    describeRevision = before;
                    describeTask = Task.Run(Describe);
                    reason = describing;
                    return false;
                }
                if (Describe() is not { } description) { reason = "読み込みプラグインの状態を確認できません。"; return false; }
                if (!Apply(description, before))
                {
                    reason = "検査中にプロジェクトまたは読み込みプラグインが変更されたため、通常描画を使用します。";
                    return false;
                }
            }
            reason = cachedReason;
            if (!cachedEligible) return false;
            var dependencies = frame is int at ? cachedFrames!.For(at) : null;
            if (!(dependencies ?? cachedFrames!.Whole).Cacheable)
            {
                reason = "立ち絵（非同期の口パク）、外部プラグインのコード（エフェクト・図形・アイテム・トランジション）、確認できない素材（DirectWrite にないフォントや外部の場所のファイル）のいずれかを使うアイテムが映るため、通常描画を使用します。";
                return false;
            }
            string[] files = dependencies?.Files ?? cachedPaths;
            (ICacheDependencyProvider Provider, CacheDependencySnapshot Snapshot)[] dynamicSnapshots;
            try
            {
                long ticks = frame is int position ? scene.Timeline.VideoInfo.GetTimeFrom(position).Ticks : 0;
                if (dynamicProviders.Any(provider => !provider.CanCaptureOnCurrentThread))
                { reason = "動的な入力依存をこのスレッドでは確認できません。"; return false; }
                dynamicSnapshots = dynamicProviders.Select(provider => (provider, provider.CaptureDependencies(ticks))).ToArray();
                if (dynamicSnapshots.Any(pair => pair.Item2 is null || !pair.Item1.IsCurrent(pair.Item2)))
                { reason = "動的な入力依存が検査中に変化しました。"; return false; }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { reason = "動的な入力依存を確認できません: " + error.GetType().Name; return false; }
            string DynamicKey() => dynamicSnapshots.Length == 0 ? KeyFor(dependencies, files)
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    "dynamic-frame-v1:" + KeyFor(dependencies, files) + string.Concat(dynamicSnapshots.Select(pair => pair.Snapshot.Key).Order(StringComparer.Ordinal)))));
            if (files.Length == 0)
            {
                capture = new KeyCapture(this, DynamicKey(), cachedModel, before, cachedParents, null, dynamicSnapshots);
                return true;
            }
            if (fingerprintTask is { IsCompleted: true }) AdoptFingerprints(before);
            // While a pass runs, a frame whose own files it has already verified is keyed (results arrive per chunk).
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
                    if (!HostContent.Matches(lease.Fingerprints))
                    {
                        lease.Dispose();
                        reason = HostContent.Reason;
                        return false;
                    }
                    capture = new KeyCapture(this, DynamicKey(), cachedModel, before, cachedParents, lease, dynamicSnapshots);
                    reason = string.Empty;
                    return true;
                }
                lease.Dispose();
            }
            if (fingerprintTask is not null)
            {
                reason = "外部素材の内容を背景で検査しています。通常描画を使用します。";
                return false;
            }
            long now = Environment.TickCount64;
            var verified = fingerprints;
            string[] missing = verified is null ? files : files.Where(file => !verified.ContainsKey(file)).ToArray();
            // Files that failed before are tried again on their own, not with the whole project.
            if (missing.Length != 0 && missing.All(unverifiable.ContainsKey))
            {
                bool changed = false;
                if (now >= nextUnverifiableLook)
                {
                    nextUnverifiableLook = now + 1000;
                    changed = missing.Any(file => Look(file) != unverifiable[file].Seen);
                }
                if ((changed || now >= nextUnverifiableRetry) && now >= nextFingerprintAttempt)
                {
                    nextUnverifiableRetry = now + (long)UnverifiableRetry.TotalMilliseconds;
                    StartFingerprinting(before, now, missing, onlyFirst: true);
                }
                reason = "外部素材の一部を検証できません: " + unverifiable[missing[0]].Reason;
                return false;
            }
            // This frame's own unverified files first, then the rest of the project.
            StartFingerprinting(before, now, missing);
            reason = cachedPartialReason.Length != 0 ? "外部素材の一部を検証できません: " + cachedPartialReason
                : now < nextFingerprintAttempt && !string.IsNullOrEmpty(cachedReason)
                ? cachedReason : "外部素材の内容確認を準備中のため、通常描画を使用します。";
            return false;
        }
    }

    // Off the gate (a background task, or inline): what the project currently is. Null if the readers are unknown.
    private Description? Describe()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Type[][] sourceReaders;
        string settings;
        try
        {
            sourceReaders = FrameCacheKey.CaptureSourceReaderTypes();
            settings = FrameCacheKey.DrawingSettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return null; }
        long code = KnownCode.Generation;
        bool eligible = FrameCacheKey.TryDescribe(scene, sourceReaders, out string model, out string[] paths, out var frames, out string reason);
        return new(eligible, model, paths, frames, reason, sourceReaders, System.Diagnostics.Stopwatch.GetTimestamp() - started, code, settings);
    }

    // Under gate: adopts a description of revision `current` unless the project or the readers changed since.
    private bool Apply(Description description, long current)
    {
        lastDescribeTicks = description.Ticks;
        if (current != Revision || !FrameCacheKey.SourceReadersMatch(description.SourceReaders))
        {
            if (current == Revision) Invalidate();
            return false;
        }
        cachedKey = string.Empty;
        cachedModel = description.Model;
        cachedPaths = description.Paths;
        cachedFrames = description.Frames;
        frameKeys.Clear();
        cachedSourceReaders = description.SourceReaders;
        cachedCode = description.Code;
        cachedSettings = description.Settings;
        cachedParents = scene.ParentScenes.ToArray();
        cachedReason = description.Reason;
        cachedEligible = description.Eligible && description.Frames is not null;
        try { dynamicProviders = cachedEligible ? FrameCacheKey.CaptureDynamicProviders(scene) : []; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { dynamicProviders = []; cachedEligible = false; cachedReason = "動的な入力依存の列挙に失敗しました: " + error.GetType().Name; }
        cachedRevision = current;
        return true;
    }

    // Under gate, with a finished describeTask.
    private void AdoptDescription(long current)
    {
        var task = describeTask!;
        describeTask = null;
        if (describeRevision == current && task.IsCompletedSuccessfully && task.Result is { } description) Apply(description, current);
    }

    private bool DescribeInline() =>
        scene.Scenes.Timelines.Append(scene.Timeline).Distinct().Sum(timeline => timeline.Items.Count) <= InlineDescribeItems
        || lastDescribeTicks >= 0 && lastDescribeTicks <= InlineDescribeTicks;

    // Tests: whether a background description is running.
    internal bool Describing { get { lock (gate) return describeTask is { IsCompleted: false }; } }

    // For display only (cache status bars): the keys of these frames from the current description, without
    // verifying or leasing files. False while an edit is not described yet; null for frames with unhashed files.
    // Frames another tracker stored (the idle pre-renderer, export) can be ones this tracker never captured, so
    // their files are verified here in the background too.
    internal bool TryPeekFrameKeys(IReadOnlyList<int> frames, string?[] keys, out string model)
    {
        lock (gate)
        {
            model = string.Empty;
            if (!disposed && cachedRevision != Revision && describeTask is { IsCompleted: true }) AdoptDescription(Revision);
            if (disposed || cachedRevision != Revision || !cachedEligible || cachedFrames is null) return false;
            // Providers run on capture's owning render context. UI status/read-ahead cannot call them safely.
            if (dynamicProviders.Length != 0) return false;
            if (fingerprintTask is { IsCompleted: true }) AdoptFingerprints(cachedRevision);
            bool unverified = false;
            for (int i = 0; i < frames.Count; i++)
            {
                var dependencies = cachedFrames.For(frames[i]);
                bool verified = dependencies.Files.Length == 0
                    || (fingerprints is not null && dependencies.Files.All(fingerprints.ContainsKey));
                // A file that failed verification is retried by captures only (Capture), not with the project.
                unverified |= dependencies.Cacheable && !verified && dependencies.Files.Any(file =>
                    (fingerprints is null || !fingerprints.ContainsKey(file)) && !unverifiable.ContainsKey(file));
                keys[i] = dependencies.Cacheable && verified ? KeyFor(dependencies, dependencies.Files) : null;
            }
            if (unverified && fingerprintTask is null) StartFingerprinting(cachedRevision, Environment.TickCount64);
            model = cachedModel;
            return true;
        }
    }

    // Under gate, with a finished fingerprintTask: keeps its fingerprints if they describe this revision's files.
    // The verified files are added to those known before (a pass can cover only some files); the failed ones are
    // removed from them and remembered as unverifiable.
    private void AdoptFingerprints(long current)
    {
        if (fingerprintTask!.IsCompletedSuccessfully)
        {
            var (files, _, failed) = fingerprintTask.Result;
            foreach (var path in files?.Keys ?? []) unverifiable.Remove(path);
            foreach (var (path, failure) in failed) unverifiable[path] = failure;
        }
        if (fingerprintRevision == current)
        {
            if (fingerprintTask!.IsCompletedSuccessfully && (fingerprintTask.Result.Files is not null || fingerprintTask.Result.Failed.Count != 0))
            {
                var merged = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
                if (fingerprints is not null) foreach (var pair in fingerprints) merged[pair.Key] = pair.Value;
                foreach (var pair in fingerprintTask.Result.Files ?? EmptyFingerprints) merged[pair.Key] = pair.Value;
                foreach (var path in fingerprintTask.Result.Failed.Keys) merged.Remove(path);
                // Unchanged (a retry that failed again): the same dictionary, so the idle renderer stays reusable.
                var known = fingerprints;
                if (known is null || merged.Count != known.Count || merged.Any(pair => !known.TryGetValue(pair.Key, out var own) || own != pair.Value))
                {
                    fingerprints = merged;
                    cachedKey = string.Empty;
                    frameKeys.Clear();
                }
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

    // Under gate, with no fingerprintTask: verifies the project's files in the background (one tracker at a time),
    // `first` before the others (only `first` with onlyFirst). Fingerprints are published as each chunk finishes.
    private void StartFingerprinting(long current, long now, string[]? first = null, bool onlyFirst = false)
    {
        if (now >= nextFingerprintAttempt && FingerprintSlot.Wait(0))
        {
            var cancellation = new CancellationTokenSource();
            fingerprintCancellation = cancellation;
            fingerprintRevision = current;
            cachedPartialReason = string.Empty;
            string[] paths = onlyFirst ? first ?? [] : cachedPaths;
            int leading = 0;
            if (!onlyFirst && first is { Length: > 0 })
            {
                var set = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase);
                paths = first.Concat(paths.Where(path => !set.Contains(path))).ToArray();
                leading = first.Length;
            }
            var previous = fingerprints;
            CancellationToken token = cancellation.Token;
            fingerprintTask = Task.Run(() =>
            {
                try { return FingerprintSafely(paths, previous, token, Publish, leading); }
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
    // Editing/disposal cancels optional validation. Return an unavailable result instead of faulting a
    // fire-and-forget Task: abandoned faulted tasks otherwise reach the host's unobserved-exception UI.
    // Failed: the files that failed on their own, with the reason (not those of a cancelled or failed pass).
    internal static (IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason, IReadOnlyDictionary<string, Unverified> Failed) FingerprintSafely(string[] paths,
        IReadOnlyDictionary<string, FileFingerprint>? previous, CancellationToken token,
        Action<IReadOnlyDictionary<string, FileFingerprint>>? publish = null, int leading = 0)
    {
        try { return Fingerprint(paths, previous, token, publish, leading); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return (null, "外部素材の内容確認を中断しました。", NoFailures); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return (null, "外部素材の内容確認に失敗しました: " + error.GetType().Name, NoFailures); }
    }

    private static readonly IReadOnlyDictionary<string, Unverified> NoFailures = new Dictionary<string, Unverified>();

    // What a file looks like without opening it (attributes only), to notice that an unverifiable one changed.
    private static (bool Exists, long Length, long Written) Look(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists ? (true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return default; }
    }

    private static (IReadOnlyDictionary<string, FileFingerprint>? Files, string Reason, IReadOnlyDictionary<string, Unverified> Failed) Fingerprint(string[] paths,
        IReadOnlyDictionary<string, FileFingerprint>? previous, CancellationToken token,
        Action<IReadOnlyDictionary<string, FileFingerprint>>? publish = null, int leading = 0)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, Unverified>(StringComparer.OrdinalIgnoreCase);
        string reason = string.Empty;
        // The leading files (a waiting frame's) are chunked on their own, so they are published first.
        foreach (var chunk in paths[..leading].Chunk(FingerprintChunk).Concat(paths[leading..].Chunk(FingerprintChunk)))
        {
            token.ThrowIfCancellationRequested();
            var added = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
            if (!TryAdd(chunk, added))
                foreach (var path in chunk)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TryAdd([path], added)) failed[path] = new(reason, Look(path));
                }
            if (added.Count != 0) publish?.Invoke(added);
        }
        token.ThrowIfCancellationRequested(); // a failure caused by the cancellation is not the file's
        return (result.Count == 0 ? null : result, reason, failed);

        bool TryAdd(string[] group, Dictionary<string, FileFingerprint> added)
        {
            if (!FileDependencyLease.TryAcquire(group, previous, MaximumFingerprintBytes, out var lease, out string failure, token))
            {
                reason = failure;
                return false;
            }
            using (lease) foreach (var pair in lease!.Fingerprints) result[pair.Key] = added[pair.Key] = pair.Value;
            return true;
        }
    }

    // A chunk's fingerprints, while the pass goes on. They describe files, not a project revision: a capture still
    // leases its files and compares their stamps, and the finished pass drops the files it could not verify.
    private void Publish(IReadOnlyDictionary<string, FileFingerprint> added)
    {
        // The project's files as verified in the background, usually before YMM4 reads them for a frame: a later
        // overwrite is seen even if the first frame using the file is keyed after it.
        HostContent.Matches(added);
        lock (gate)
        {
            if (disposed) return;
            var merged = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
            if (fingerprints is not null) foreach (var pair in fingerprints) merged[pair.Key] = pair.Value;
            foreach (var pair in added) merged[pair.Key] = pair.Value;
            fingerprints = merged;
            cachedKey = string.Empty;
            frameKeys.Clear();
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
        // Font names map to faces through these (FrameCacheKey.ResolveFont).
        var fonts = SettingsBase<YukkuriMovieMaker.Settings.FontSettings>.Default;
        Subscribe(fonts);
        Subscribe(fonts.CustomFonts);
        foreach (var font in fonts.CustomFonts) Subscribe(font);
        // The asterisk word sets rewrite the text of text items and subtitles (FrameCacheKey.AsteriskWordSets).
        foreach (var source in FrameCacheKey.AsteriskSources()) Subscribe(source);
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
    // A YMMSettings property other than DrawingSettingNames (an unnamed change counts as one of them).
    private static bool IsUiSetting(object? sender, string? property) =>
        sender is YukkuriMovieMaker.Settings.YMMSettings && !string.IsNullOrEmpty(property) && !DrawingSettingNames.Contains(property);
    private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (IsTimelineUiProperty(sender, args.PropertyName)) return;
        // Should a drawing setting depend on another property after all, its value changed: describe again.
        if (IsUiSetting(sender, args.PropertyName) && SafeDrawingSettings() == cachedSettings) return;
        Invalidate();
    }
    private void PropertyChanging(object? sender, PropertyChangingEventArgs args)
    {
        if (!IsTimelineUiProperty(sender, args.PropertyName) && !IsUiSetting(sender, args.PropertyName)) Invalidate();
    }
    private static string? SafeDrawingSettings()
    {
        try { return FrameCacheKey.DrawingSettings(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return null; }
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
    private readonly (ICacheDependencyProvider Provider, CacheDependencySnapshot Snapshot)[] dynamicSnapshots;
    private int disposed;
    public string Key { get; }
    public string Model { get; }
    public long Revision { get; }
    internal KeyCapture(KeyDependencyTracker tracker, string key, string model, long revision, Guid[] parents, FileDependencyLease? lease,
        (ICacheDependencyProvider Provider, CacheDependencySnapshot Snapshot)[]? dynamicSnapshots = null)
    { this.tracker = tracker; Key = key; Model = model; Revision = revision; this.parents = parents; this.lease = lease; this.dynamicSnapshots = dynamicSnapshots ?? []; }
    private bool DynamicCurrent()
    {
        try { return dynamicSnapshots.All(pair => pair.Provider.CanCaptureOnCurrentThread && pair.Provider.IsCurrent(pair.Snapshot)); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    // files: also resolve the leased paths again. The lease's open handles (FileShare.Read, no delete sharing) deny
    // writes, deletes and renames of the files themselves while it is held, so only the last check before a result
    // becomes visible (a store commit, an output swap made after the capture's Update) needs it. Every other check
    // (edits, settings, scene parents, dynamic inputs) stays on every call.
    public bool Validate(bool files = true) => Volatile.Read(ref disposed) == 0 && tracker.ValidateRevision(Revision)
        && tracker.HasParents(parents) && DynamicCurrent() && (!files || (lease?.VerifyPaths() ?? true)) && Volatile.Read(ref disposed) == 0;
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lease?.Dispose(); }
}
