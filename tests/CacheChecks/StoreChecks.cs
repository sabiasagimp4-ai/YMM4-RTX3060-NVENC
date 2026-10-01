using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using NVEncVideoWriterPlugin;

internal static class StoreChecks
{
    internal static void Run(string tempPath)
    {
        string Key(int value) => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(value))).ToLowerInvariant();
        string root = Path.Combine(tempPath, "store");
        string directory = Path.Combine(root, "frames-v1");
        string Record(int value) => Directory.EnumerateFiles(directory, Key(value) + ".ymmframe", SearchOption.AllDirectories).Single();
        byte[] frame = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        using (var cache = new FrameCacheStore(root, 32, 128))
        {
            cache.Put(Key(1), frame);
            frame[0] = 200;
            Check(cache.TryGet(Key(1), out var first) && first.Span[0] == 0, "Put must snapshot pixels");
            Check(cache.TryGet(Key(1), out first) && first.Span[0] == 0, "RAM hits must return the stored snapshot");
            cache.Put(Key(2), frame);
            cache.Put(Key(3), frame);
            WaitFor(() => Directory.Exists(directory) && Directory.EnumerateFiles(directory, Key(3) + ".ymmframe", SearchOption.AllDirectories).Any(), "background writes establish disk ownership");
            Check(cache.RamBytes <= 32 && cache.DiskBytes <= 128, "byte budgets");
            Check(!cache.TryGet(Key(1), out _), "oldest RAM and disk entry evicted");
            Check(cache.TryGet(Key(2), out _) && cache.TryGet(Key(3), out _), "recent entries retained");
            byte[] residency = new byte[3];
            cache.GetResidency([Key(1), Key(3), null], residency);
            Check(residency.SequenceEqual(new byte[] { 0, 2, 0 }), "residency reports RAM frames and nothing for evicted or missing keys");
            cache.Put("../../not-a-cache-key", frame);
            Check(!cache.TryGet("invalid", out _), "invalid keys are misses");
            using var secondOwner = new FrameCacheStore(root, 16, 128);
            Check(!secondOwner.TryGet(Key(2), out _), "one disk owner at a time");
        }
        using (var cache = new FrameCacheStore(root, 32, 128))
        {
            byte[] onDisk = new byte[1];
            WaitFor(() => { cache.GetResidency([Key(2)], onDisk); return onDisk[0] == 1; }, "residency reports disk-only frames after restart");
            long version = cache.Version;
            Check(!cache.TryGet(Key(2), out _), "first disk miss is nonblocking");
            ReadOnlyMemory<byte> restored = default;
            WaitFor(() => cache.TryGet(Key(2), out restored), "background disk read warms RAM");
            cache.GetResidency([Key(2)], onDisk);
            Check(onDisk[0] == 2 && cache.Version > version, "a warmed frame reports RAM and changes the store version");
            Check(restored.Span.SequenceEqual(frame), "restart disk reuse");
            Check(cache.TryGet(Key(2), out restored) && restored.Span.SequenceEqual(frame), "disk promotion keeps the verified pixels");
        }
        string corruptRecord = Record(2);
        using (var file = new FileStream(corruptRecord, FileMode.Open, FileAccess.Write))
        {
            file.Position = 48;
            file.WriteByte(99);
        }
        using (var cache = new FrameCacheStore(root, 16, 128))
        {
            Check(!cache.TryGet(Key(2), out var corrupt) && corrupt.Length == 0, "checksum corruption is a safe miss");
            WaitFor(() => !File.Exists(corruptRecord), "corrupt record is removed asynchronously");
            cache.Put(Key(4), frame);
            WaitFor(() => Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, Key(4) + ".ymmframe", SearchOption.AllDirectories).Any()
                && cache.DiskBytes == 128, "queued write is persisted asynchronously");
            // Deleting an open file fails only on Windows; elsewhere the purge simply succeeds.
            if (OperatingSystem.IsWindows())
            {
                string path = Record(4);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    cache.Clear();
                    Check(!cache.TryGet(Key(4), out _), "Clear logically purges even a locked file");
                    Check(cache.DiskBytes <= 128, "failed deletion retains budget accounting");
                    cache.Dispose();
                    using (var restarted = new FrameCacheStore(root, 16, 64))
                    {
                        WaitFor(() => restarted.DiskBytes == 64, "restart accounts locked retired physical bytes");
                        Check(!restarted.TryGet(Key(4), out _), "locked retired frame cannot resurrect after restart");
                        restarted.Put(Key(5), frame);
                    }
                    using var reopened = new FrameCacheStore(root, 16, 64);
                    WaitFor(() => reopened.DiskBytes == 64, "disk budget remains full after queued write drains");
                    Check(!reopened.TryGet(Key(5), out _), "retired file fills budget and prevents disk growth");
                }
            }
            else
            {
                cache.Clear();
                cache.Dispose();
            }
            using var reclaimed = new FrameCacheStore(root, 16, 128);
            reclaimed.Clear();
            Check(reclaimed.DiskBytes == 0 && reclaimed.RamBytes == 0, "Clear reclaims unlocked retired cache bytes");
        }
        string stale = Path.Combine(directory, Key(6) + "." + Guid.NewGuid().ToString("N") + ".ymmtmp");
        string unrelated = Path.Combine(directory, "user-document.txt");
        File.WriteAllText(stale, "interrupted atomic write");
        File.WriteAllText(unrelated, "preserve me");
        byte[] header = new byte[48];
        "YMMFRM01"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), long.MaxValue);
        string currentDirectory = Directory.GetDirectories(directory, "epoch-*").Single();
        File.WriteAllBytes(Path.Combine(currentDirectory, Key(7) + ".ymmframe"), header);
        using (var cache = new FrameCacheStore(root, 64, 256))
        {
            WaitFor(() => !File.Exists(stale) && !File.Exists(Path.Combine(currentDirectory, Key(7) + ".ymmframe")), "startup cleanup runs asynchronously");
            Check(File.Exists(unrelated), "cleanup only owned temporary files");
            Check(!cache.TryGet(Key(7), out _), "huge forged length rejected before allocating");
            Parallel.For(0, 200, index =>
            {
                if (index % 11 == 0) cache.Clear();
                else cache.Put(Key(index % 7), frame);
                cache.TryGet(Key(index % 7), out _);
                Check(cache.RamBytes <= 64 && cache.DiskBytes <= 256, "concurrent operations retain budgets");
            });
            cache.Clear();
            Check(File.Exists(unrelated), "Clear preserves unrelated files");
            cache.Dispose();
            cache.Put(Key(1), frame);
            Check(!cache.TryGet(Key(1), out _), "disposed store fails open");
        }
        string blocked = Path.Combine(tempPath, "not-a-directory");
        File.WriteAllText(blocked, "ordinary user file");
        using (var cache = new FrameCacheStore(blocked, 32, 128))
        {
            cache.Put(Key(1), frame);
            Check(cache.TryGet(Key(1), out _), "unavailable disk falls back to bounded RAM");
            Check(cache.DiskBytes == 0 && File.ReadAllText(blocked) == "ordinary user file", "disk failure preserves user data");
            bool clearFailed = false;
            try { cache.Clear(); } catch (IOException) { clearFailed = true; }
            Check(clearFailed && cache.RamBytes == 0 && File.ReadAllText(blocked) == "ordinary user file", "unavailable disk reports failed purge after clearing RAM");
        }
        using (var cache = new FrameCacheStore(Path.Combine(tempPath, "disabled-store"), 0, 0))
        {
            cache.Put(Key(1), frame);
            Check(cache.RamBytes == 0 && cache.DiskBytes == 0 && !cache.TryGet(Key(1), out _), "zero budgets allocate no persistent cache");
        }
        using (var cache = new FrameCacheStore(Path.Combine(tempPath, "ram-only-store"), 32, 0))
        {
            cache.Put(Key(1), frame);
            Check(cache.TryGet(Key(1), out _) && cache.DiskBytes == 0, "zero disk budget retains RAM cache");
            byte[] oversized = new byte[FrameCacheStore.MaxFrameBytes + 1];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            cache.Put(Key(10), oversized);
            Check(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024 * 1024, "over-cap input rejected before cloning");
            Check(!cache.TryGet(Key(10), out _), "over-cap input is never cached");
        }
        using (var cache = new FrameCacheStore(Path.Combine(tempPath, "shared-hit-store"), 4 * 1024 * 1024, 0))
        {
            byte[] large = new byte[1024 * 1024];
            large[5] = 7;
            cache.Put(Key(1), large);
            large[5] = 9;
            Check(cache.TryGet(Key(1), out _), "large frame hit");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Check(cache.TryGet(Key(1), out var shared) && shared.Span[5] == 7, "Put must snapshot pixels before hits share them");
            Check(GC.GetAllocatedBytesForCurrentThread() - allocated < 64 * 1024, "RAM hits must share, not copy, stored pixels");
            byte[] owned = new byte[1024 * 1024];
            allocated = GC.GetAllocatedBytesForCurrentThread();
            cache.PutOwned(Key(2), owned);
            Check(GC.GetAllocatedBytesForCurrentThread() - allocated < 64 * 1024, "PutOwned must not copy the handed-over pixels");
            Check(cache.TryGet(Key(2), out var ownedHit) && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(ownedHit, out var segment)
                && ReferenceEquals(segment.Array, owned), "PutOwned must store the handed-over array");
        }
        bool rejectedDiskOnly = false;
        try { using var _ = new FrameCacheStore(Path.Combine(tempPath, "disk-only-store"), 0, 64); }
        catch (ArgumentOutOfRangeException) { rejectedDiskOnly = true; }
        Check(rejectedDiskOnly, "disk promotion requires a nonzero RAM budget");
        string tooLargeForRam = Path.Combine(tempPath, "frame-over-ram");
        using (var cache = new FrameCacheStore(tooLargeForRam, 8, 128))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            cache.Put(Key(1), frame);
            Check(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024, "over-RAM input is rejected before cloning");
        }
        Check(!Directory.EnumerateFiles(tooLargeForRam, "*.ymmframe", SearchOption.AllDirectories).Any(), "frames larger than RAM are not written to unreachable disk cache");
        if (OperatingSystem.IsWindows())
        {
            // A record locked against reading is charged even though startup cannot validate it.
            string occupied = Path.Combine(tempPath, "occupied-store");
            using (var cache = new FrameCacheStore(occupied, 16, 64)) cache.Put(Key(1), frame);
            string occupiedRecord = Directory.EnumerateFiles(occupied, "*.ymmframe", SearchOption.AllDirectories).Single();
            using (var held = new FileStream(occupiedRecord, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using (var cache = new FrameCacheStore(occupied, 16, 64))
                {
                    WaitFor(() => cache.DiskBytes == 64, "unreadable startup record retains physical accounting");
                    Check(!cache.TryGet(Key(1), out _), "unreadable startup record is a miss");
                    cache.Put(Key(2), frame);
                }
                using var reopened = new FrameCacheStore(occupied, 16, 64);
                WaitFor(() => reopened.DiskBytes == 64, "locked record still accounts after write drains");
                Check(!reopened.TryGet(Key(2), out _), "unreadable record prevents budget overrun");
            }
            // Even an invalid, oversize file is accounted when Windows cannot delete it.
            string invalid = Path.Combine(tempPath, "invalid-occupied-store");
            using (var cache = new FrameCacheStore(invalid, 16, 64)) cache.Put(Key(1), frame);
            string invalidRecord = Directory.EnumerateFiles(invalid, "*.ymmframe", SearchOption.AllDirectories).Single();
            File.WriteAllBytes(invalidRecord, new byte[80]);
            using (var held = new FileStream(invalidRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var cache = new FrameCacheStore(invalid, 16, 64))
                {
                    WaitFor(() => cache.DiskBytes == 80, "invalid locked file larger than budget is reported honestly");
                    Check(!cache.TryGet(Key(1), out _), "invalid locked record is a miss");
                    cache.Put(Key(2), frame);
                }
                using var reopened = new FrameCacheStore(invalid, 16, 64);
                WaitFor(() => reopened.DiskBytes == 80, "oversubscribed file remains accounted after write drains");
                Check(!reopened.TryGet(Key(2), out _), "oversubscribed startup bypasses new disk writes");
            }
        }
        CheckDiskDelivery(tempPath, Key);
        CheckConcurrentIndex(tempPath, Key);
        Console.WriteLine("Cache store: ownership, budgets, cap, restart, checksum, locked-file epoch purge, physical accounting, concurrent purge and disk failure passed.");
    }

    // A frame stored on disk only reaches the caller: by a bounded wait, or by read-ahead within half the RAM budget.
    private static void CheckDiskDelivery(string tempPath, Func<int, string> key)
    {
        string root = Path.Combine(tempPath, "delivery-store");
        byte[] frame = Enumerable.Range(0, 16).Select(value => (byte)(value * 3)).ToArray();
        using (var cache = new FrameCacheStore(root, 64, 1024))
        {
            for (int i = 20; i < 24; i++) cache.Put(key(i), frame);
            byte[] residency = new byte[4];
            WaitFor(() => { cache.GetResidency([key(20), key(21), key(22), key(23)], residency); return cache.DiskWrites == 4; }, "frames are written to disk");
        }
        using (var cache = new FrameCacheStore(root, 32, 1024))
        {
            byte[] residency = new byte[1];
            WaitFor(() => { cache.GetResidency([key(20)], residency); return residency[0] == 1; }, "the index is loaded after restart");
            Check(cache.TryGet(key(20), TimeSpan.FromSeconds(10), out var pixels, out bool fromDisk) && fromDisk && pixels.Span.SequenceEqual(frame),
                "a waited request did not deliver the disk-only frame");
            Check(cache.DiskDeliveries == 1 && cache.DiskReads == 1, $"delivery counters: {cache.DiskDeliveries}/{cache.DiskReads}");
            Check(cache.TryGet(key(20), TimeSpan.Zero, out _, out fromDisk) && !fromDisk && cache.DiskDeliveries == 1, "a second hit counts as RAM");
            Check(!cache.TryGet(key(99), TimeSpan.FromSeconds(5), out _, out _), "a key that is not stored does not wait");
            // RAM budget 32: the read-ahead window is 16 bytes, one frame. key(20) in RAM fills it; then one read.
            Check(cache.Prefetch([key(20), key(21)]) == 0, "a frame already in RAM counts toward the read-ahead window");
            Check(cache.Prefetch([null, "not-a-key", key(99), key(21), key(22)]) == 1, "read-ahead stays within half the RAM budget");
            WaitFor(() => { cache.GetResidency([key(21)], residency); return residency[0] == 2; }, "read-ahead warms RAM");
            Check(cache.TryGet(key(21), out var prefetched) && prefetched.Span.SequenceEqual(frame) && cache.DiskDeliveries == 2,
                "a prefetched frame is a disk delivery");
            cache.Clear();
            cache.GetResidency([key(22)], residency);
            Check(residency[0] == 0 && cache.Prefetch([key(22)]) == 0 && !cache.TryGet(key(22), TimeSpan.FromSeconds(1), out _, out _),
                "purged frames are neither reported nor delivered");
        }
    }

    // The disk index is read by residency queries, requests and read-ahead while the worker writes, evicts and purges.
    private static void CheckConcurrentIndex(string tempPath, Func<int, string> key)
    {
        string root = Path.Combine(tempPath, "stress-store");
        byte[] frame = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var keys = Enumerable.Range(100, 96).Select(key).ToArray();
        // RAM holds 4 frames and disk 20, so every second writes, reads, evictions and index changes happen.
        using var cache = new FrameCacheStore(root, 64, 20 * (16 + 48));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long rounds = 0;
        void Loop(Action<int> body)
        {
            try { for (int i = 0; clock.ElapsedMilliseconds < 3000; i++) { body(i); Interlocked.Increment(ref rounds); } }
            catch (Exception error) { errors.Enqueue(error); }
        }
        Task.WaitAll(
            Task.Run(() => Loop(i => cache.Put(keys[i % keys.Length], frame))),
            Task.Run(() => Loop(i => cache.Put(keys[(i * 13) % keys.Length], frame))),
            Task.Run(() => Loop(_ => { var residency = new byte[keys.Length]; cache.GetResidency(keys, residency); })),
            Task.Run(() => Loop(i => { cache.TryGet(keys[(i * 7) % keys.Length], out _); cache.Prefetch(keys); })),
            Task.Run(() => Loop(i => cache.TryGet(keys[i % keys.Length], TimeSpan.FromMilliseconds(2), out _, out _))),
            Task.Run(() => Loop(i => { if (i % 40 == 0) cache.Clear(); Thread.Sleep(1); })));
        Check(errors.IsEmpty, "concurrent index access failed: " + string.Join("; ", errors.Select(error => error.GetType().Name + ": " + error.Message)));
        Check(cache.RamBytes <= 64 && cache.DiskBytes <= 20 * 64, "budgets under concurrency");
        // Quiesced: the purge barrier empties the index, and new records are counted once each.
        cache.Clear();
        var residency = new byte[keys.Length];
        cache.GetResidency(keys, residency);
        Check(cache.DiskBytes == 0 && residency.All(value => value == 0), "purge after the stress left disk frames");
        for (int i = 0; i < 3; i++) cache.Put(keys[i], frame);
        WaitFor(() => cache.DiskBytes == 3 * 64, "writes after the stress are accounted");
        int files = Directory.EnumerateFiles(Path.Combine(root, "frames-v1"), "*.ymmframe", SearchOption.AllDirectories).Count();
        Check(files == 3, $"index and files disagree after the stress: {files} files");
        Console.WriteLine($"Cache store index under concurrency: {Interlocked.Read(ref rounds)} operations in 3 s, {cache.DiskWrites} disk writes, {cache.DiskReads} reads, {cache.DroppedWrites} writes dropped by backpressure, no errors, accounting consistent.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Frame cache: " + message);
    }

    private static void WaitFor(Func<bool> predicate, string message)
    {
        Check(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(10)), message);
    }
}
