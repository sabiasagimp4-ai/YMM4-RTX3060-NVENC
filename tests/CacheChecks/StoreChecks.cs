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
        Console.WriteLine("Cache store: ownership, budgets, cap, restart, checksum, locked-file epoch purge, physical accounting, concurrent purge and disk failure passed.");
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
