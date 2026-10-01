using System.Buffers.Binary;
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
    private static ReadOnlySpan<byte> Magic => "YMMFRM01"u8;
    private readonly object _gate = new();
    private long _version;
    private readonly object _clearGate = new();
    private readonly long _ramBudget;
    private readonly long _diskBudget;
    private readonly string _rootDirectory;
    private string _directory;
    private readonly Dictionary<string, (byte[] Pixels, LinkedListNode<string> Node, bool FromDisk)> _ram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Bytes, LinkedListNode<string> Node)> _disk = new(StringComparer.Ordinal);
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
                if (remaining <= 0 || _disposed || !_pendingReads.Contains(new PendingKey(_generation, key))) break;
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
                    bytes += entry.Bytes - HeaderBytes;
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
    internal void PutOwned(string key, byte[] pixels) => Put(key, pixels, owned: true);

    private void Put(string key, byte[] pixels, bool owned)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        lock (_gate)
        {
            if (_disposed || !ValidKey(key) || pixels.Length == 0 || pixels.Length > MaxFrameBytes) return;
            key = key.ToLowerInvariant();
            if (pixels.LongLength > _ramBudget)
            {
                if (_ram.Remove(key, out var previous))
                {
                    Interlocked.Increment(ref _version);
                    _ramBytes -= previous.Pixels.LongLength;
                    _ramLru.Remove(previous.Node);
                }
                return;
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
                if (_disposed || _diskWorker is null) return;
                if (_workerFailed) throw new IOException("The frame cache disk worker stopped before its purge could be persisted.");

                long generation = ++_generation;
                Monitor.PulseAll(_gate);
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
                try
                {
                    switch (operation.Kind)
                    {
                        case OperationKind.Read:
                            ProcessRead(operation);
                            break;
                        case OperationKind.Write:
                            ProcessWrite(operation);
                            break;
                        case OperationKind.Clear:
                            ProcessClear(operation);
                            operation.Completion!.TrySetResult();
                            break;
                    }
                }
                catch (Exception error)
                {
                    if (!IsFileFailure(error)) Volatile.Write(ref _diskBlocked, true);
                    if (operation.Kind == OperationKind.Clear)
                        operation.Completion!.TrySetException(new IOException("The frame cache could not persist its purge; disk caching is disabled.", error));
                }
                finally
                {
                    CompletePending(operation);
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

    private void ProcessRead(DiskOperation operation)
    {
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
                pixels = new byte[length];
                file.ReadExactly(pixels);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(pixels), header[16..])) throw new InvalidDataException();
            }
            catch (Exception error) when (IsFileFailure(error))
            {
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
    }

    private void ProcessWrite(DiskOperation operation)
    {
        byte[] snapshot = operation.Pixels!;
        if (Volatile.Read(ref _diskBlocked) || operation.Generation != _diskGeneration || operation.Generation != Volatile.Read(ref _generation)
            || snapshot.LongLength > _diskBudget - HeaderBytes) return;
        if (_disk.ContainsKey(operation.Key) && !RemoveDisk(operation.Key)) return;
        long bytes = snapshot.LongLength + HeaderBytes;
        while ((_diskBytes > _diskBudget - bytes || _disk.Count >= MaxDiskEntries) && _diskLru.First is not null)
            if (!RemoveDisk(_diskLru.First.Value)) return;
        // Undeletable retired records still consume physical disk budget.
        if (_diskBytes > _diskBudget - bytes) return;
        string temp = Path.Combine(_directory, $"{operation.Key}.{Guid.NewGuid():N}.ymmtmp");
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt64LittleEndian(header[8..], snapshot.LongLength);
            SHA256.HashData(snapshot, header[16..]);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(header);
                file.Write(snapshot);
            }
            File.Move(temp, RecordPath(operation.Key), true);
            lock (_gate)
            {
                _disk.Add(operation.Key, (bytes, _diskLru.AddLast(operation.Key)));
                _diskWrites++;
            }
            Interlocked.Increment(ref _version);
            Interlocked.Add(ref _diskBytes, bytes);
        }
        catch (Exception error) when (IsFileFailure(error)) { }
        finally { DeleteOrAccount(temp); }
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

    private void ScanDirectory(string directory, bool current, Span<byte> header)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            bool record = name.Length == 73 && name.EndsWith(".ymmframe", StringComparison.Ordinal) && ValidKey(name[..64]);
            if (!record && !IsOwnedTemp(name)) continue;
            if (!current || !record || name[..64] != name[..64].ToLowerInvariant()) { DeleteOrAccount(path); continue; }
            string key = name[..64];
            try
            {
                using var file = OpenRecord(key);
                file.ReadExactly(header);
                if (!ValidHeader(header, file.Length, out int length) || length > _ramBudget || file.Length > _diskBudget ||
                    _diskBytes > _diskBudget - file.Length || _disk.Count >= MaxDiskEntries)
                {
                    file.Dispose();
                    DeleteOrAccount(path);
                    continue;
                }
                lock (_gate) _disk.Add(key, (file.Length, _diskLru.AddLast(key)));
                Interlocked.Increment(ref _version);
                Interlocked.Add(ref _diskBytes, file.Length);
            }
            catch (Exception error) when (IsFileFailure(error)) { DeleteOrAccount(path); }
        }
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
    private static bool ValidHeader(ReadOnlySpan<byte> header, long fileLength, out int length)
    {
        long size = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        length = size > 0 && size <= MaxFrameBytes ? (int)size : 0;
        return header[..8].SequenceEqual(Magic) && length != 0 && fileLength == size + HeaderBytes;
    }
    private static bool ValidKey(string? key) => key is { Length: 64 } && key.All(char.IsAsciiHexDigit);
    private static bool IsOwnedTemp(string name) => name.Length == 104 && name[64] == '.' &&
        ValidKey(name[..64]) && name[65..97].All(char.IsAsciiHexDigit) && name.EndsWith(".ymmtmp", StringComparison.Ordinal);
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
    private sealed record DiskOperation(OperationKind Kind, long Generation, string Key, byte[]? Pixels, TaskCompletionSource? Completion);
}
