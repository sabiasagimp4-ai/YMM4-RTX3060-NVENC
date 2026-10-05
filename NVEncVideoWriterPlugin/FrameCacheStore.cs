using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NVEncVideoWriterPlugin;

// Put snapshots the caller's array; stored snapshots are never mutated afterwards, so hits share them
// read-only instead of copying a whole frame per hit.
// Locking: _gate guards RAM, the pending sets and the disk index (_disk, _diskLru, _diskGeneration). Only the disk
// worker changes the disk index, always under _gate and never while doing file I/O; it may read the index without
// the lock. Every other thread reads it under _gate.
internal sealed class FrameCacheStore : IDisposable
{
    internal const int MaxFrameBytes = 128 * 1024 * 1024;
    private const int HeaderBytes = 48;
    private const int MaxRamEntries = 1024;
    private const int MaxDiskEntries = 4096;
    private const int MaxQueuedOperations = 128;
    private const long MaxQueuedWriteBytes = MaxFrameBytes;
    // v2 binds the checksum to the requested content key as well as the decoded pixels.
    // v1 records cannot prove that association and are discarded during index loading.
    private static ReadOnlySpan<byte> Magic => "YMMFRM02"u8;
    private static ReadOnlySpan<byte> CompressedMagic => "YMMFRZ02"u8;
    private readonly object _gate = new();
    private long _version;
    private readonly object _clearGate = new();
    private long _ramBudget;
    private readonly long _diskBudget;
    private readonly string _rootDirectory;
    private string _directory;
    private readonly Dictionary<string, (byte[] Pixels, LinkedListNode<string> Node, bool FromDisk)> _ram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Bytes, int RawBytes, LinkedListNode<string> Node)> _disk = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _ramLru = new();
    private readonly LinkedList<string> _diskLru = new();
    private readonly BlockingCollection<DiskOperation> _operations = new(new ReadsFirst(), MaxQueuedOperations);
    private readonly HashSet<PendingKey> _pendingReads = [];
    private readonly HashSet<PendingKey> _pendingWrites = [];
    private readonly Thread? _diskWorker;
    private FileStream? _owner;
    private bool _disposed;
    private bool _diskBlocked;
    private bool _workerFailed;
    private bool _indexReady;
    private long _diskBytes, _ramBytes, _hits, _misses, _generation, _diskGeneration, _queuedWriteBytes;
    private long _diskDeliveries, _diskReads, _diskReadTicks, _diskWrites, _droppedWrites;

    internal FrameCacheStore(string path, long ramBudgetBytes = 256L * 1024 * 1024, long diskBudgetBytes = 4L * 1024 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ramBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(diskBudgetBytes);
        if (diskBudgetBytes > 0 && ramBudgetBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(ramBudgetBytes), "Disk cache promotion requires a nonzero RAM budget.");
        _ramBudget = ramBudgetBytes;
        _diskBudget = diskBudgetBytes;
        _rootDirectory = Path.Combine(Path.GetFullPath(path), "frames-v1");
        _directory = _rootDirectory;
        if (_diskBudget == 0) return;
        _diskWorker = new Thread(DiskWorker) { IsBackground = true, Name = "YMM frame cache disk" };
        _diskWorker.Start();
    }

    // Tests only: runs on the disk worker before it loads the index (a slow disk).
    internal static Action? IndexLoadingForTests { get; set; }

    // Changes whenever a frame enters or leaves RAM or disk (for the cache status bars).
    internal long Version => Interlocked.Read(ref _version);

    // 2: in RAM, 1: on disk only, 0: not stored. Never waits for I/O. Frames of a purge that is still being
    // persisted, or of a disk that stopped working, are not reported.
    internal void GetResidency(IReadOnlyList<string?> keys, Span<byte> residency)
    {
        lock (_gate)
        {
            bool disk = DiskReadable();
            for (int i = 0; i < keys.Count; i++)
            {
                string? key = keys[i];
                residency[i] = _disposed || key is null || !ValidKey(key) ? (byte)0
                    : _ram.ContainsKey(key.ToLowerInvariant()) ? (byte)2 : disk && _disk.ContainsKey(key.ToLowerInvariant()) ? (byte)1 : (byte)0;
            }
        }
    }

    internal long RamBytes { get { lock (_gate) return _ramBytes; } }
    internal long RamBudget { get { lock (_gate) return _ramBudget; } }
    internal long QueuedWriteBytes { get { lock (_gate) return _queuedWriteBytes; } }

    // A producer captures this before starting work. Content identity answers what the
    // pixels mean; this permit separately answers whether that work may still publish.
    internal readonly record struct Publication(FrameCacheStore? Owner, long Generation);
    // Read without the lock (every cached render takes one): Put compares it again under the lock, with Clear.
    internal Publication BeginPublication() => new(this, Interlocked.Read(ref _generation));

    // Eviction drops our references only: a borrowed hit or queued write still owns immutable pixels.
    // Disk records survive a smaller RAM budget and can be promoted again when memory recovers.
    internal void SetRamBudget(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            if (_disposed || _ramBudget == bytes) return;
            _ramBudget = bytes;
            while (_ramBytes > bytes && _ramLru.First is { } oldest)
            {
                _ramBytes -= _ram[oldest.Value].Pixels.LongLength;
                _ram.Remove(oldest.Value);
                _ramLru.RemoveFirst();
            }
            Interlocked.Increment(ref _version);
            Monitor.PulseAll(_gate);
        }
    }
    internal long DiskBytes => Interlocked.Read(ref _diskBytes);
    internal long Hits { get { lock (_gate) return _hits; } }
    internal long Misses { get { lock (_gate) return _misses; } }
    // Hits whose pixels were read back from disk (the first hit after the read; later hits are RAM hits).
    internal long DiskDeliveries { get { lock (_gate) return _diskDeliveries; } }
    internal long DiskReads { get { lock (_gate) return _diskReads; } }
    internal double DiskReadMilliseconds { get { lock (_gate) return _diskReads == 0 ? 0 : _diskReadTicks * 1000.0 / Stopwatch.Frequency / _diskReads; } }
    internal long DiskWrites { get { lock (_gate) return _diskWrites; } }
    // Frames kept in RAM only because the write queue was full (backpressure).
    internal long DroppedWrites { get { lock (_gate) return _droppedWrites; } }

    // Disk misses warm RAM in the background; this method never waits for file I/O.
    internal bool TryGet(string key, out ReadOnlyMemory<byte> pixels) => TryGet(key, TimeSpan.Zero, out pixels, out _);

    // Strict cached-only lookup for UI/observers: no read scheduling, waiting, LRU movement or hit accounting.
    internal bool TryGetCached(string key, out ReadOnlyMemory<byte> pixels)
    {
        pixels = default;
        lock (_gate)
        {
            if (_disposed || !ValidKey(key) || !_ram.TryGetValue(key.ToLowerInvariant(), out var memory)) return false;
            pixels = memory.Pixels;
            return true;
        }
    }

    // A frame stored on disk only is read before any queued write, and the caller waits at most `wait` for it. The
    // read runs on the disk worker; the wait is for callers off the UI thread that would otherwise render the frame.
    internal bool TryGet(string key, TimeSpan wait, out ReadOnlyMemory<byte> pixels, out bool fromDisk)
    {
        pixels = ReadOnlyMemory<byte>.Empty;
        fromDisk = false;
        long deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, wait.TotalSeconds) * Stopwatch.Frequency);
        lock (_gate)
        {
            if (_disposed || !ValidKey(key)) { _misses++; return false; }
            key = key.ToLowerInvariant();
            bool queued = false;
            while (true)
            {
                if (_ram.TryGetValue(key, out var memory))
                {
                    Touch(_ramLru, memory.Node);
                    pixels = memory.Pixels;
                    if (memory.FromDisk)
                    {
                        fromDisk = true;
                        _ram[key] = (memory.Pixels, memory.Node, false);
                        _diskDeliveries++;
                    }
                    _hits++;
                    return true;
                }
                if (!queued)
                {
                    queued = true;
                    // Before the index is loaded every key may be on disk.
                    if (DiskReadable() && (!_indexReady || _disk.ContainsKey(key))) QueueRead(key, _generation);
                }
                long remaining = deadline - Stopwatch.GetTimestamp();
                // Reads run only after the index is loaded (seconds for a full cache): until then a wait would delay
                // every paused frame, stored or not, by the whole wait. A key the index already holds is read soon.
                if (remaining <= 0 || _disposed || !(_indexReady || _disk.ContainsKey(key))
                    || !_pendingReads.Contains(new PendingKey(_generation, key))) break;
                Monitor.Wait(_gate, TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency));
            }
            _misses++;
            return false;
        }
    }

    // Read-ahead for frames about to be shown, in the order given: the stored ones up to half the RAM budget form the
    // window. Those in RAM are kept (moved to the recent end), those on disk only are read before queued writes.
    // Counting the frames already in RAM keeps the window from outgrowing RAM and evicting itself. Never waits.
    internal int Prefetch(IReadOnlyList<string?> keys)
    {
        lock (_gate)
        {
            if (_disposed || !_indexReady) return 0;
            bool disk = DiskReadable();
            long bytes = 0;
            int queued = 0;
            foreach (string? candidate in keys)
            {
                if (candidate is null || !ValidKey(candidate)) continue;
                string key = candidate.ToLowerInvariant();
                if (_ram.TryGetValue(key, out var memory))
                {
                    bytes += memory.Pixels.LongLength;
                    if (bytes > _ramBudget / 2) break;
                    Touch(_ramLru, memory.Node);
                }
                else if (disk && _disk.TryGetValue(key, out var entry))
                {
                    if (entry.RawBytes > _ramBudget) continue;
                    bytes += entry.RawBytes;
                    if (bytes > _ramBudget / 2) break;
                    if (QueueRead(key, _generation)) queued++;
                }
            }
            return queued;
        }
    }

    // Under _gate.
    private bool DiskReadable() => _diskWorker is not null && !_workerFailed && !Volatile.Read(ref _diskBlocked) && _diskGeneration == _generation;

    internal void Put(string key, byte[] pixels) => Put(key, pixels, owned: false);

    // For a freshly captured array the caller never touches again: stored without a snapshot copy.
    internal bool PutOwned(string key, byte[] pixels) => Put(key, pixels, owned: true);

    // Validation and insertion share _gate with Clear: no check-then-publish race.
    internal bool PutOwned(string key, byte[] pixels, Publication publication) =>
        Put(key, pixels, owned: true, publication);

    private bool Put(string key, byte[] pixels, bool owned, Publication? publication = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        lock (_gate)
        {
            if (publication is { } permit && (!ReferenceEquals(permit.Owner, this) || permit.Generation != _generation)) return false;
            if (_disposed || !ValidKey(key) || pixels.Length == 0 || pixels.Length > MaxFrameBytes) return false;
            key = key.ToLowerInvariant();
            if (pixels.LongLength > _ramBudget)
            {
                if (_ram.Remove(key, out var previous))
                {
                    Interlocked.Increment(ref _version);
                    _ramBytes -= previous.Pixels.LongLength;
                    _ramLru.Remove(previous.Node);
                }
                return false;
            }
            var snapshot = owned ? pixels : (byte[])pixels.Clone();
            using (PreviewPerformance.Measure(PreviewStage.RamCommit)) AddRam(key, snapshot, fromDisk: false);
            // ponytail: async disk reads only promote frames that fit RAM; streaming hits need a separate delivery API.
            if (_diskWorker is not null && !_workerFailed && !Volatile.Read(ref _diskBlocked)
                && snapshot.LongLength <= _ramBudget && snapshot.LongLength <= _diskBudget - HeaderBytes)
            {
                using var measurement = PreviewPerformance.Measure(PreviewStage.DiskEnqueue);
                QueueWrite(key, snapshot, _generation);
            }
            return true;
        }
    }

    // Purge is a bounded queue barrier. It invalidates RAM immediately and reports
    // failure only if the persistent epoch change could not be flushed.
    internal void Clear()
    {
        lock (_clearGate)
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                _ram.Clear();
                Interlocked.Increment(ref _version);
                _ramLru.Clear();
                _ramBytes = 0;
                long generation = Interlocked.Increment(ref _generation); // RAM-only producers obey the same purge barrier.
                Monitor.PulseAll(_gate);
                if (_disposed || _diskWorker is null) return;
                if (_workerFailed) throw new IOException("The frame cache disk worker stopped before its purge could be persisted.");

                var retainedClears = new List<DiskOperation>();
                while (_operations.TryTake(out var pending))
                {
                    if (pending.Kind == OperationKind.Clear) retainedClears.Add(pending);
                    else CancelQueued(pending);
                }
                foreach (var pending in retainedClears)
                    if (!_operations.TryAdd(pending)) pending.Completion!.TrySetException(new IOException("The frame cache could not retain a queued purge."));

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_operations.TryAdd(new DiskOperation(OperationKind.Clear, generation, string.Empty, null, completion)))
                {
                    _workerFailed = true;
                    Volatile.Write(ref _diskBlocked, true);
                    throw new IOException("The frame cache disk worker could not accept its purge.");
                }
            }
            completion.Task.GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_diskWorker is not null)
        {
            _operations.CompleteAdding();
            _diskWorker.Join();
        }
        lock (_gate)
        {
            _ram.Clear();
            Interlocked.Increment(ref _version);
            _ramLru.Clear();
            _ramBytes = _queuedWriteBytes = 0;
            _pendingReads.Clear();
            _pendingWrites.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    // Under _gate. True when a new read was queued.
    private bool QueueRead(string key, long generation)
    {
        var pending = new PendingKey(generation, key);
        if (!_pendingReads.Add(pending)) return false;
        if (_operations.TryAdd(new DiskOperation(OperationKind.Read, generation, key, null, null))) return true;
        _pendingReads.Remove(pending);
        return false;
    }

    private void QueueWrite(string key, byte[] pixels, long generation)
    {
        var pending = new PendingKey(generation, key);
        if (_pendingWrites.Contains(pending)) return;
        if (_queuedWriteBytes > MaxQueuedWriteBytes - pixels.LongLength) { _droppedWrites++; return; }
        _pendingWrites.Add(pending);
        _queuedWriteBytes += pixels.LongLength;
        if (!_operations.TryAdd(new DiskOperation(OperationKind.Write, generation, key, pixels, null)))
        {
            _pendingWrites.Remove(pending);
            _queuedWriteBytes -= pixels.LongLength;
            _droppedWrites++;
        }
    }

    private void CancelQueued(DiskOperation operation)
    {
        var pending = new PendingKey(operation.Generation, operation.Key);
        if (operation.Kind == OperationKind.Read) _pendingReads.Remove(pending);
        else if (operation.Kind == OperationKind.Write)
        {
            _pendingWrites.Remove(pending);
            _queuedWriteBytes -= operation.Pixels!.LongLength;
        }
    }

    private void DiskWorker()
    {
        DiskOperation? active = null;
        try
        {
            try
            {
                InitializeDisk();
                IndexLoadingForTests?.Invoke();
                if (_owner is not null) LoadDiskIndex();
            }
            catch (Exception)
            {
                Volatile.Write(ref _diskBlocked, true);
                lock (_gate)
                {
                    _disk.Clear();
                    _diskLru.Clear();
                }
                Interlocked.Increment(ref _version);
                Interlocked.Exchange(ref _diskBytes, 0);
            }
            lock (_gate) _indexReady = true;

            foreach (var operation in _operations.GetConsumingEnumerable())
            {
                active = operation;
                // Reads feed the preview; writes (compression, hashing, file I/O) yield the CPU to decoding and rendering.
                var priority = operation.Kind == OperationKind.Read ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
                if (Thread.CurrentThread.Priority != priority) Thread.CurrentThread.Priority = priority;
                var trace = CacheTrace.Measure("disk-" + operation.Kind.ToString().ToLowerInvariant(), "io-wall",
                    frameTimeTicks: operation.TraceTime, usage: operation.TraceUsage, operation: operation.TraceOperation);
                try
                {
                    if (operation.QueuedAt != 0)
                        CacheTrace.Timing("disk-queue-wait", operation.QueuedAt, Stopwatch.GetTimestamp(), "queue-wait", nested: false);
                    switch (operation.Kind)
                    {
                        case OperationKind.Read:
                            ProcessRead(operation, trace);
                            break;
                        case OperationKind.Write:
                            ProcessWrite(operation, trace);
                            break;
                        case OperationKind.Clear:
                            ProcessClear(operation);
                            operation.Completion!.TrySetResult();
                            break;
                    }
                }
                catch (Exception error)
                {
                    if (trace is not null) { trace.Outcome = "exception"; trace.Detail = error.GetType().Name; }
                    if (!IsFileFailure(error)) Volatile.Write(ref _diskBlocked, true);
                    if (operation.Kind == OperationKind.Clear)
                        operation.Completion!.TrySetException(new IOException("The frame cache could not persist its purge; disk caching is disabled.", error));
                }
                finally
                {
                    CompletePending(operation);
                    trace?.Dispose();
                    active = null;
                }
            }
        }
        catch (Exception error)
        {
            FailWorker(error, active);
        }
        finally
        {
            try { _owner?.Dispose(); }
            catch (Exception error) { FailWorker(error, active); }
            _owner = null;
        }
    }

    private void InitializeDisk()
    {
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            if ((File.GetAttributes(_rootDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache root cannot be a directory link.");
            string ownerPath = Path.Combine(_rootDirectory, ".owner.lock");
            if (File.Exists(ownerPath) && (File.GetAttributes(ownerPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache owner cannot be a file link.");
            _owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string? epoch = ReadEpoch();
            if (epoch is null) epoch = SaveNewEpoch();
            _directory = Path.Combine(_rootDirectory, "epoch-" + epoch);
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache epoch cannot be a directory link.");
            lock (_gate) _diskGeneration = _generation;
        }
        catch (Exception error) when (IsFileFailure(error))
        {
            try { _owner?.Dispose(); } catch (Exception) { }
            _owner = null;
            Volatile.Write(ref _diskBlocked, true);
        }
    }

    private void FailWorker(Exception error, DiskOperation? active)
    {
        lock (_gate)
        {
            _workerFailed = true;
            Volatile.Write(ref _diskBlocked, true);
        }
        var failure = new IOException("The frame cache disk worker stopped before its purge could be persisted.", error);
        active?.Completion?.TrySetException(failure);
        while (_operations.TryTake(out var pending))
        {
            if (pending.Kind == OperationKind.Clear) pending.Completion!.TrySetException(failure);
            try { CompletePending(pending); } catch (Exception) { }
        }
        lock (_gate)
        {
            _pendingReads.Clear();
            _pendingWrites.Clear();
            _queuedWriteBytes = 0;
            Monitor.PulseAll(_gate);
        }
    }

    private void ProcessRead(DiskOperation operation, CacheTrace.Span? trace)
    {
        if (trace is not null) trace.Outcome = "skipped";
        byte[]? pixels = null;
        long started = Stopwatch.GetTimestamp();
        if (!Volatile.Read(ref _diskBlocked) && operation.Generation == _diskGeneration && operation.Generation == Volatile.Read(ref _generation)
            && _disk.ContainsKey(operation.Key))
        {
            try
            {
                using var file = OpenRecord(operation.Key);
                Span<byte> header = stackalloc byte[HeaderBytes];
                file.ReadExactly(header);
                if (!ValidHeader(header, file.Length, out int length)) throw new InvalidDataException();
                // Check the current budget before allocating, separately from record validity. A resize during
                // I/O is checked again by AddRam; the read's temporary array is bounded by MaxFrameBytes.
                lock (_gate)
                    if (_disposed || operation.Generation != _generation || length > _ramBudget) return;
                // Fully overwritten by the read or the decoder (both are checked for the exact length).
                using (CacheTrace.Measure("disk-allocation")) pixels = GC.AllocateUninitializedArray<byte>(length);
                if (header[..8].SequenceEqual(CompressedMagic))
                {
                    int compressedLength = checked((int)(file.Length - HeaderBytes));
                    byte[] encoded = ArrayPool<byte>.Shared.Rent(compressedLength);
                    try
                    {
                        using (CacheTrace.Measure("disk-read-bytes", "io-wall")) file.ReadExactly(encoded.AsSpan(0, compressedLength));
                        using var decode = CacheTrace.Measure("disk-decompression");
                        using var decoder = new BrotliDecoder();
                        var status = decoder.Decompress(encoded.AsSpan(0, compressedLength), pixels, out int consumed, out int written);
                        if (status != OperationStatus.Done || consumed != compressedLength || written != length) throw new InvalidDataException();
                    }
                    finally { ArrayPool<byte>.Shared.Return(encoded); }
                }
                else using (CacheTrace.Measure("disk-read-bytes", "io-wall")) file.ReadExactly(pixels);
                using (CacheTrace.Measure("disk-checksum"))
                {
                    Span<byte> checksum = stackalloc byte[32];
                    HashRecord(operation.Key, pixels, checksum);
                    if (!CryptographicOperations.FixedTimeEquals(checksum, header[16..])) throw new InvalidDataException();
                }
                if (trace is not null) trace.Outcome = "verified";
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                if (trace is not null) { trace.Outcome = "read-failed"; trace.Detail = error.GetType().Name; }
                pixels = null;
                RemoveDisk(operation.Key);
            }
        }

        lock (_gate)
        {
            if (pixels is null) return;
            if (_disk.TryGetValue(operation.Key, out var entry)) Touch(_diskLru, entry.Node);
            if (!_disposed && operation.Generation == _generation)
            {
                AddRam(operation.Key, pixels, fromDisk: true);
                _diskReads++;
                _diskReadTicks += Stopwatch.GetTimestamp() - started;
            }
        }
        TouchRecord(operation.Key); // after the delivery: a waiting request never waits for it
    }

    private void ProcessWrite(DiskOperation operation, CacheTrace.Span? trace)
    {
        if (trace is not null) trace.Outcome = "skipped";
        byte[] snapshot = operation.Pixels!;
        if (Volatile.Read(ref _diskBlocked) || operation.Generation != _diskGeneration || operation.Generation != Volatile.Read(ref _generation)
            || snapshot.LongLength > _diskBudget - HeaderBytes) return;
        string temp = Path.Combine(_directory, $"{operation.Key}.{Guid.NewGuid():N}.ymmtmp");
        byte[]? compressed = null;
        try
        {
            int compressedLength = 0;
            // Worker only. Tiny records stay raw. Incompressible data falls back without expanding the file.
            if (snapshot.Length >= 1024)
            {
                compressed = ArrayPool<byte>.Shared.Rent(snapshot.Length);
                using var compression = CacheTrace.Measure("disk-compression");
                if (!BrotliEncoder.TryCompress(snapshot, compressed.AsSpan(0, snapshot.Length), out compressedLength, quality: 0, window: 22)
                    || compressedLength > snapshot.Length - snapshot.Length / 8) compressedLength = 0;
            }
            long bytes = (compressedLength > 0 ? compressedLength : snapshot.LongLength) + HeaderBytes;
            if (_disk.ContainsKey(operation.Key) && !RemoveDisk(operation.Key)) return;
            while ((_diskBytes > _diskBudget - bytes || _disk.Count >= MaxDiskEntries) && _diskLru.First is not null)
                if (!RemoveDisk(_diskLru.First.Value)) return;
            // Undeletable retired records still consume physical disk budget.
            if (_diskBytes > _diskBudget - bytes) return;
            Span<byte> header = stackalloc byte[HeaderBytes];
            (compressedLength > 0 ? CompressedMagic : Magic).CopyTo(header);
            BinaryPrimitives.WriteInt64LittleEndian(header[8..], snapshot.LongLength);
            using (CacheTrace.Measure("disk-checksum")) HashRecord(operation.Key, snapshot, header[16..]);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (CacheTrace.Measure("disk-write-bytes", "io-wall"))
                { file.Write(header); file.Write(compressedLength > 0 ? compressed.AsSpan(0, compressedLength) : snapshot.AsSpan()); }
            }
            File.Move(temp, RecordPath(operation.Key), true);
            lock (_gate)
            {
                _disk.Add(operation.Key, (bytes, snapshot.Length, _diskLru.AddLast(operation.Key)));
                _diskWrites++;
            }
            Interlocked.Increment(ref _version);
            Interlocked.Add(ref _diskBytes, bytes);
            if (trace is not null) trace.Outcome = "written";
        }
        catch (Exception error) when (IsFileFailure(error))
        { if (trace is not null) { trace.Outcome = "write-failed"; trace.Detail = error.GetType().Name; } }
        finally { if (compressed is not null) ArrayPool<byte>.Shared.Return(compressed); DeleteOrAccount(temp); }
    }

    private void ProcessClear(DiskOperation operation)
    {
        if (_owner is null) throw new IOException("The frame cache has no persistent owner.");
        lock (_gate)
        {
            _disk.Clear();
            _diskLru.Clear();
        }
        Interlocked.Increment(ref _version);
        try
        {
            _directory = Path.Combine(_rootDirectory, "epoch-" + SaveNewEpoch());
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache epoch cannot be a directory link.");
            LoadDiskIndex();
            lock (_gate) _diskGeneration = operation.Generation;
        }
        catch (Exception error) when (IsFileFailure(error))
        {
            Volatile.Write(ref _diskBlocked, true);
            throw;
        }
    }

    private void CompletePending(DiskOperation operation)
    {
        if (operation.Kind is not (OperationKind.Read or OperationKind.Write)) return;
        lock (_gate)
        {
            var pending = new PendingKey(operation.Generation, operation.Key);
            if (operation.Kind == OperationKind.Read)
            {
                _pendingReads.Remove(pending);
                Monitor.PulseAll(_gate);
            }
            else
            {
                _pendingWrites.Remove(pending);
                _queuedWriteBytes -= operation.Pixels!.LongLength;
            }
        }
    }

    // Under _gate.
    private void AddRam(string key, byte[] pixels, bool fromDisk)
    {
        if (_ram.Remove(key, out var previous))
        {
            Interlocked.Increment(ref _version);
            _ramBytes -= previous.Pixels.LongLength;
            _ramLru.Remove(previous.Node);
        }
        if (pixels.LongLength > _ramBudget) return;
        while ((_ramBytes > _ramBudget - pixels.LongLength || _ram.Count >= MaxRamEntries) && _ramLru.First is not null)
        {
            string oldest = _ramLru.First.Value;
            _ramBytes -= _ram[oldest].Pixels.LongLength;
            _ram.Remove(oldest);
            Interlocked.Increment(ref _version);
            _ramLru.RemoveFirst();
        }
        _ram.Add(key, (pixels, _ramLru.AddLast(key), fromDisk));
        Interlocked.Increment(ref _version);
        _ramBytes += pixels.LongLength;
    }

    private void LoadDiskIndex()
    {
        lock (_gate)
        {
            _disk.Clear();
            _diskLru.Clear();
        }
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _diskBytes, 0);
        Volatile.Write(ref _diskBlocked, false);
        Span<byte> header = stackalloc byte[HeaderBytes];
        ScanDirectory(_rootDirectory, false, header);
        foreach (string directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            string name = Path.GetFileName(directory);
            if (name.Length != 38 || !name.StartsWith("epoch-", StringComparison.Ordinal) || !name[6..].All(char.IsAsciiHexDigit)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { Volatile.Write(ref _diskBlocked, true); continue; }
            bool current = directory.Equals(_directory, StringComparison.OrdinalIgnoreCase);
            ScanDirectory(directory, current, header);
            if (!current)
            {
                try { Directory.Delete(directory); }
                catch (Exception error) when (IsFileFailure(error)) { }
            }
        }
    }

    // The disk LRU outlives the process through the records' write times (a disk read refreshes it, TouchRecord):
    // the most recently used records are kept within the budget and the rest deleted, and the LRU starts from the
    // least recently used, not in file name (key hash) order.
    private void ScanDirectory(string directory, bool current, Span<byte> header)
    {
        var records = new List<(string Key, string Path, long Length, int RawBytes, DateTime Written)>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFiles())
        {
            string name = info.Name, path = info.FullName;
            bool record = name.Length == 73 && name.EndsWith(".ymmframe", StringComparison.Ordinal) && ValidKey(name[..64]);
            if (!record && !IsOwnedTemp(name)) continue;
            if (!current || !record || name[..64] != name[..64].ToLowerInvariant()) { DeleteOrAccount(path); continue; }
            string key = name[..64];
            try
            {
                using var file = OpenRecord(key);
                file.ReadExactly(header);
                if (!ValidHeader(header, file.Length, out int rawBytes) || file.Length > _diskBudget)
                {
                    file.Dispose();
                    DeleteOrAccount(path);
                    continue;
                }
                records.Add((key, path, file.Length, rawBytes, info.LastWriteTimeUtc));
            }
            catch (Exception error) when (IsFileFailure(error)) { DeleteOrAccount(path); }
        }
        records.Sort((a, b) => b.Written.CompareTo(a.Written));
        int kept = 0;
        for (int i = 0; i < records.Count; i++)
        {
            var entry = records[i];
            if (_diskBytes > _diskBudget - entry.Length || _disk.Count + kept >= MaxDiskEntries) { DeleteOrAccount(entry.Path); continue; }
            Interlocked.Add(ref _diskBytes, entry.Length);
            records[kept++] = entry;
        }
        lock (_gate)
            for (int i = kept - 1; i >= 0; i--) _disk.Add(records[i].Key, (records[i].Length, records[i].RawBytes, _diskLru.AddLast(records[i].Key)));
        if (kept != 0) Interlocked.Increment(ref _version);
    }

    // Disk worker: marks a record as used now for the next start (ScanDirectory). Optional.
    private void TouchRecord(string key)
    {
        try { File.SetLastWriteTimeUtc(RecordPath(key), DateTime.UtcNow); }
        catch (Exception error) when (IsFileFailure(error)) { }
    }

    private string? ReadEpoch()
    {
        if (_owner!.Length != 64) return null;
        Span<byte> record = stackalloc byte[64];
        _owner.Position = 0;
        _owner.ReadExactly(record);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(record[..32]), record[32..])) return null;
        string epoch = Encoding.ASCII.GetString(record[..32]);
        return epoch.All(char.IsAsciiHexDigit) ? epoch : null;
    }

    private string SaveNewEpoch()
    {
        string epoch = Guid.NewGuid().ToString("N");
        Span<byte> record = stackalloc byte[64];
        Encoding.ASCII.GetBytes(epoch, record[..32]);
        SHA256.HashData(record[..32], record[32..]);
        _owner!.Position = 0;
        _owner.SetLength(0);
        _owner.Write(record);
        _owner.Flush(true);
        return epoch;
    }

    private void DeleteOrAccount(string path)
    {
        if (TryDelete(path)) return;
        try
        {
            long length = new FileInfo(path).Length;
            long bytes = Interlocked.Read(ref _diskBytes);
            Interlocked.Exchange(ref _diskBytes, bytes > long.MaxValue - length ? long.MaxValue : bytes + length);
        }
        catch (Exception error) when (IsFileFailure(error)) { Volatile.Write(ref _diskBlocked, true); }
    }

    private FileStream OpenRecord(string key)
    {
        string path = RecordPath(key);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache link is not a frame.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private bool RemoveDisk(string key)
    {
        if (!_disk.TryGetValue(key, out var entry)) return true;
        if (!TryDelete(RecordPath(key))) return false;
        lock (_gate)
        {
            _disk.Remove(key);
            _diskLru.Remove(entry.Node);
        }
        Interlocked.Increment(ref _version);
        Interlocked.Add(ref _diskBytes, -entry.Bytes);
        return true;
    }

    private string RecordPath(string key) => Path.Combine(_directory, key + ".ymmframe");
    // The key is exactly 32 bytes, so its boundary with the payload is unambiguous.
    // Compression is storage-only: the same decoded pixels have the same identity checksum.
    private static void HashRecord(string key, ReadOnlySpan<byte> pixels, Span<byte> checksum)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Convert.FromHexString(key));
        hash.AppendData(pixels);
        hash.GetHashAndReset(checksum);
    }
    private static bool ValidHeader(ReadOnlySpan<byte> header, long fileLength, out int length)
    {
        long size = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        length = size > 0 && size <= MaxFrameBytes ? (int)size : 0;
        return length != 0 && (header[..8].SequenceEqual(Magic) && fileLength == size + HeaderBytes
            || header[..8].SequenceEqual(CompressedMagic) && fileLength > HeaderBytes && fileLength < size + HeaderBytes);
    }
    // Every lookup checks its key (the cache bars ask for thousands of frames at a time): no enumerator per call.
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEFabcdef");
    private static bool IsHex(ReadOnlySpan<char> text) => !text.ContainsAnyExcept(HexDigits);
    private static bool ValidKey(string? key) => key is { Length: 64 } && IsHex(key);
    private static bool IsOwnedTemp(string name) => name.Length == 104 && name[64] == '.' &&
        IsHex(name.AsSpan(0, 64)) && IsHex(name.AsSpan(65, 32)) && name.EndsWith(".ymmtmp", StringComparison.Ordinal);
    private static void Touch(LinkedList<string> list, LinkedListNode<string> node) { list.Remove(node); list.AddLast(node); }
    private static bool IsFileFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception error) when (IsFileFailure(error)) { return false; }
    }

    private enum OperationKind { Read, Write, Clear }

    // Reads first: a frame about to be shown must not wait behind queued writes. Writes and purges keep their order.
    private sealed class ReadsFirst : IProducerConsumerCollection<DiskOperation>
    {
        private readonly ConcurrentQueue<DiskOperation> _reads = new(), _others = new();
        public int Count => _reads.Count + _others.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => throw new NotSupportedException();
        public bool TryAdd(DiskOperation item)
        {
            (item.Kind == OperationKind.Read ? _reads : _others).Enqueue(item);
            return true;
        }
        // BlockingCollection calls this only for an item already added, which a concurrent taker may have taken from
        // the other queue: look again until one is found.
        public bool TryTake([MaybeNullWhen(false)] out DiskOperation item)
        {
            for (var spin = new SpinWait(); ; spin.SpinOnce())
            {
                if (_reads.TryDequeue(out item) || _others.TryDequeue(out item)) return true;
                if (Count == 0 && spin.Count > 100) return false;
            }
        }
        public DiskOperation[] ToArray() => [.. _reads, .. _others];
        public void CopyTo(DiskOperation[] array, int index) => ToArray().CopyTo(array, index);
        void ICollection.CopyTo(Array array, int index) => ToArray().CopyTo(array, index);
        public IEnumerator<DiskOperation> GetEnumerator() => ((IEnumerable<DiskOperation>)ToArray()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private readonly record struct PendingKey(long Generation, string Key);
    private sealed record DiskOperation(OperationKind Kind, long Generation, string Key, byte[]? Pixels, TaskCompletionSource? Completion)
    {
        internal long TraceOperation { get; } = CacheTrace.OperationId;
        internal long? TraceTime { get; } = CacheTrace.FrameTimeTicks;
        internal string? TraceUsage { get; } = CacheTrace.Usage;
        internal long QueuedAt { get; } = CacheTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
    }
}
