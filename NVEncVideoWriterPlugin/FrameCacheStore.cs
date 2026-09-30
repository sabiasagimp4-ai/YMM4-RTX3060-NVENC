using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NVEncVideoWriterPlugin;

// Pixel arrays cross the API boundary as copies: callers may reuse or mutate theirs.
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
    private readonly object _clearGate = new();
    private readonly long _ramBudget;
    private readonly long _diskBudget;
    private readonly string _rootDirectory;
    private string _directory;
    private readonly Dictionary<string, (byte[] Pixels, LinkedListNode<string> Node)> _ram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Bytes, LinkedListNode<string> Node)> _disk = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _ramLru = new();
    private readonly LinkedList<string> _diskLru = new();
    private readonly BlockingCollection<DiskOperation> _operations = new(new ConcurrentQueue<DiskOperation>(), MaxQueuedOperations);
    private readonly HashSet<PendingKey> _pendingReads = [];
    private readonly HashSet<PendingKey> _pendingWrites = [];
    private readonly Thread? _diskWorker;
    private FileStream? _owner;
    private bool _disposed;
    private bool _diskBlocked;
    private bool _workerFailed;
    private long _diskBytes, _ramBytes, _hits, _misses, _generation, _diskGeneration, _queuedWriteBytes;

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

    internal long RamBytes { get { lock (_gate) return _ramBytes; } }
    internal long DiskBytes => Interlocked.Read(ref _diskBytes);
    internal long Hits { get { lock (_gate) return _hits; } }
    internal long Misses { get { lock (_gate) return _misses; } }

    // Disk misses warm RAM in the background; this method never waits for file I/O.
    internal bool TryGet(string key, out byte[] pixels)
    {
        lock (_gate)
        {
            pixels = [];
            if (_disposed || !ValidKey(key)) { _misses++; return false; }
            key = key.ToLowerInvariant();
            if (_ram.TryGetValue(key, out var memory))
            {
                Touch(_ramLru, memory.Node);
                pixels = (byte[])memory.Pixels.Clone();
                _hits++;
                return true;
            }

            _misses++;
            if (_diskWorker is not null && !_workerFailed && !Volatile.Read(ref _diskBlocked)) QueueRead(key, _generation);
            return false;
        }
    }

    internal void Put(string key, byte[] pixels)
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
                    _ramBytes -= previous.Pixels.LongLength;
                    _ramLru.Remove(previous.Node);
                }
                return;
            }
            var snapshot = (byte[])pixels.Clone();
            AddRam(key, snapshot);
            // ponytail: async disk reads only promote frames that fit RAM; streaming hits need a separate delivery API.
            if (_diskWorker is not null && !_workerFailed && !Volatile.Read(ref _diskBlocked)
                && snapshot.LongLength <= _ramBudget && snapshot.LongLength <= _diskBudget - HeaderBytes)
                QueueWrite(key, snapshot, _generation);
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
                _ramLru.Clear();
                _ramBytes = 0;
                if (_disposed || _diskWorker is null) return;
                if (_workerFailed) throw new IOException("The frame cache disk worker stopped before its purge could be persisted.");

                long generation = ++_generation;
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
            _ramLru.Clear();
            _ramBytes = _queuedWriteBytes = 0;
            _pendingReads.Clear();
            _pendingWrites.Clear();
        }
    }

    private void QueueRead(string key, long generation)
    {
        var pending = new PendingKey(generation, key);
        if (!_pendingReads.Add(pending)) return;
        if (!_operations.TryAdd(new DiskOperation(OperationKind.Read, generation, key, null, null)))
            _pendingReads.Remove(pending);
    }

    private void QueueWrite(string key, byte[] pixels, long generation)
    {
        var pending = new PendingKey(generation, key);
        if (_queuedWriteBytes > MaxQueuedWriteBytes - pixels.LongLength || !_pendingWrites.Add(pending)) return;
        _queuedWriteBytes += pixels.LongLength;
        if (!_operations.TryAdd(new DiskOperation(OperationKind.Write, generation, key, pixels, null)))
        {
            _pendingWrites.Remove(pending);
            _queuedWriteBytes -= pixels.LongLength;
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
                _disk.Clear();
                _diskLru.Clear();
                Interlocked.Exchange(ref _diskBytes, 0);
            }

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
            _diskGeneration = Volatile.Read(ref _generation);
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
        }
    }

    private void ProcessRead(DiskOperation operation)
    {
        byte[]? pixels = null;
        if (!Volatile.Read(ref _diskBlocked) && operation.Generation == _diskGeneration && operation.Generation == Volatile.Read(ref _generation)
            && _disk.TryGetValue(operation.Key, out var entry))
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
                Touch(_diskLru, entry.Node);
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                pixels = null;
                RemoveDisk(operation.Key);
            }
        }

        lock (_gate)
        {
            if (pixels is not null && !_disposed && operation.Generation == _generation)
                AddRam(operation.Key, pixels);
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
            _disk.Add(operation.Key, (bytes, _diskLru.AddLast(operation.Key)));
            Interlocked.Add(ref _diskBytes, bytes);
        }
        catch (Exception error) when (IsFileFailure(error)) { }
        finally { DeleteOrAccount(temp); }
    }

    private void ProcessClear(DiskOperation operation)
    {
        if (_owner is null) throw new IOException("The frame cache has no persistent owner.");
        _disk.Clear();
        _diskLru.Clear();
        try
        {
            _directory = Path.Combine(_rootDirectory, "epoch-" + SaveNewEpoch());
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache epoch cannot be a directory link.");
            _diskGeneration = operation.Generation;
            LoadDiskIndex();
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
            if (operation.Kind == OperationKind.Read) _pendingReads.Remove(pending);
            else
            {
                _pendingWrites.Remove(pending);
                _queuedWriteBytes -= operation.Pixels!.LongLength;
            }
        }
    }

    private void AddRam(string key, byte[] pixels)
    {
        if (_ram.Remove(key, out var previous))
        {
            _ramBytes -= previous.Pixels.LongLength;
            _ramLru.Remove(previous.Node);
        }
        if (pixels.LongLength > _ramBudget) return;
        while ((_ramBytes > _ramBudget - pixels.LongLength || _ram.Count >= MaxRamEntries) && _ramLru.First is not null)
        {
            string oldest = _ramLru.First.Value;
            _ramBytes -= _ram[oldest].Pixels.LongLength;
            _ram.Remove(oldest);
            _ramLru.RemoveFirst();
        }
        _ram.Add(key, (pixels, _ramLru.AddLast(key)));
        _ramBytes += pixels.LongLength;
    }

    private void LoadDiskIndex()
    {
        _disk.Clear();
        _diskLru.Clear();
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
                _disk.Add(key, (file.Length, _diskLru.AddLast(key)));
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
        _disk.Remove(key);
        _diskLru.Remove(entry.Node);
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
    private readonly record struct PendingKey(long Generation, string Key);
    private sealed record DiskOperation(OperationKind Kind, long Generation, string Key, byte[]? Pixels, TaskCompletionSource? Completion);
}
