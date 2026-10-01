using System.Security.Cryptography;
using NVEncVideoWriterPlugin;

internal static class CacheBudgetChecks
{
    internal static void Run(string root)
    {
        CheckResize(root);
        CheckDiskSurvivesShrink(root);
        CheckConcurrentResize(root);
        CheckPolicy();
        Console.WriteLine("RAM budget: LRU resize, borrowed pixels, zero budget, disk retention/recovery, concurrent resize, pressure and recovery passed.");
    }

    private static string Key(int value) => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(value))).ToLowerInvariant();
    private static void Check(bool result, string message) { if (!result) throw new Exception("RAM budget: " + message); }
    private static void Wait(Func<bool> result, string message) => Check(SpinWait.SpinUntil(result, TimeSpan.FromSeconds(10)), message);

    private static void CheckResize(string root)
    {
        using var store = new FrameCacheStore(Path.Combine(root, "resize"), 64, 0);
        for (int i = 1; i <= 4; i++) store.Put(Key(i), Enumerable.Repeat((byte)i, 16).ToArray());
        Check(store.TryGet(Key(1), out var borrowed), "borrowed hit");
        long version = store.Version;
        store.SetRamBudget(32);
        Check(store.RamBytes == 32 && store.RamBudget == 32 && store.Version > version, "resize updates accounting and bars");
        Check(!store.TryGet(Key(2), out _) && !store.TryGet(Key(3), out _), "oldest entries evicted first");
        Check(store.TryGet(Key(4), out _) && store.TryGet(Key(1), out _), "recent entries retained");
        store.SetRamBudget(0);
        Check(store.RamBytes == 0 && !store.TryGet(Key(1), out _), "pressure can shrink to zero");
        Check(!store.PutOwned(Key(6), new byte[16]) && store.RamBytes == 0, "rejected ownership commit reports failure");
        Check(borrowed.Span.SequenceEqual(Enumerable.Repeat((byte)1, 16).ToArray()), "eviction does not mutate a borrowed hit");
        store.SetRamBudget(64);
        store.Put(Key(5), new byte[32]);
        Check(store.TryGet(Key(5), out _), "growth enables new frames");
        bool rejected = false;
        try { store.SetRamBudget(-1); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && store.RamBudget == 64, "negative budgets rejected atomically");
    }

    private static void CheckDiskSurvivesShrink(string root)
    {
        string path = Path.Combine(root, "resize-disk");
        byte[] pixels = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        using (var store = new FrameCacheStore(path, 64, 1024))
        {
            store.Put(Key(1), pixels);
            store.Put(Key(2), pixels);
            Wait(() => store.DiskWrites == 2, "writes completed");
            store.SetRamBudget(0);
            Check(store.RamBytes == 0 && store.DiskBytes == 160, "shrink retains disk accounting");
        }
        using (var store = new FrameCacheStore(path, 16, 1024))
        {
            byte[] residency = new byte[2];
            Wait(() => { store.GetResidency([Key(1), Key(2)], residency); return residency.All(v => v == 1); }, "restart retains records larger than current RAM");
            Check(!store.TryGet(Key(1), TimeSpan.FromSeconds(1), out _, out _), "over-budget disk read is a miss");
            Check(store.RamBytes == 0 && store.DiskReads == 0 && store.DiskBytes == 160, "rejected promotion neither allocates a stored frame nor deletes disk");
            Check(store.Prefetch([Key(1), Key(2)]) == 0, "read-ahead respects resized budget");
            store.SetRamBudget(64);
            Check(store.TryGet(Key(1), TimeSpan.FromSeconds(5), out var restored, out bool disk)
                && disk && restored.Span.SequenceEqual(pixels), "recovery delivers original verified pixels");
            store.SetRamBudget(0);
            store.SetRamBudget(64);
            Check(store.TryGet(Key(2), TimeSpan.FromSeconds(5), out restored, out disk)
                && disk && restored.Span.SequenceEqual(pixels), "zero-budget interval preserves disk records");
        }
        string record = Directory.EnumerateFiles(path, Key(1) + ".ymmframe", SearchOption.AllDirectories).Single();
        using (var file = new FileStream(record, FileMode.Open, FileAccess.Write)) { file.Position = 48; file.WriteByte(255); }
        using (var store = new FrameCacheStore(path, 16, 1024))
        {
            Wait(() => store.DiskBytes == 160, "corrupt record indexed without pixel allocation");
            Check(!store.TryGet(Key(1), TimeSpan.FromSeconds(1), out _, out _) && File.Exists(record), "smaller budget does not classify the record as corrupt");
            store.SetRamBudget(64);
            Check(!store.TryGet(Key(1), TimeSpan.FromSeconds(5), out _, out _), "checksum is still required after recovery");
            Wait(() => !File.Exists(record), "actual corruption removed on read");
        }
    }

    private static void CheckConcurrentResize(string root)
    {
        using var store = new FrameCacheStore(Path.Combine(root, "resize-race"), 64, 4096);
        var keys = Enumerable.Range(0, 20).Select(Key).ToArray();
        Parallel.Invoke(
            () => { for (int i = 0; i < 2000; i++) store.SetRamBudget((i % 5) * 16); },
            () => { for (int i = 0; i < 2000; i++) store.Put(keys[i % keys.Length], new byte[16]); },
            () => { for (int i = 0; i < 2000; i++) { store.TryGet(keys[i % keys.Length], out _); store.Prefetch(keys); } });
        store.SetRamBudget(32);
        Check(store.RamBytes <= 32, "concurrent put/read/resize respects final budget");
        store.Clear();
        Check(store.RamBytes == 0 && store.DiskBytes == 0, "resize retains purge barrier");
    }

    private static void CheckPolicy()
    {
        const long m = CacheMemoryPolicy.MiB;
        var policy = new CacheMemoryPolicy();
        var healthy = new CacheMemorySnapshot(16 * 1024 * m, 10 * 1024 * m, 2 * 1024 * m, 1024 * m, 12 * 1024 * m);
        long current = 256 * m;
        Check(policy.Next(current, current, 0, 2048 * m, healthy) == current, "first healthy sample holds");
        Check(policy.Next(current, current, 0, 2048 * m, healthy) == current, "second healthy sample holds");
        current = policy.Next(current, current, 0, 2048 * m, healthy);
        Check(current == 384 * m, "third sample grows by one step");
        var low = healthy with { AvailablePhysical = 300 * m };
        current = policy.Next(current, current, 128 * m, 2048 * m, low);
        Check(current == 192 * m, "physical pressure immediately halves budget");
        Check(policy.Next(current, current, 0, 2048 * m, healthy) == current, "recovery requires consecutive healthy samples");
        Check(policy.Next(current, current, 0, 2048 * m, null) == current, "unavailable sample holds and resets growth");
        Check(policy.Next(current, current, 0, 2048 * m, healthy) == current, "growth not carried over failed sample");
        Check(policy.Next(current, current, 0, 64 * m, null) == 64 * m, "manual upper limit wins even without a sample");
        Check(policy.Next(256 * m, 256 * m, 0, 2048 * m, healthy with { ProcessPrivate = 12 * 1024 * m }) == 0, "process pressure can drop to zero");
        Check(policy.Next(256 * m, 256 * m, 0, 2048 * m, healthy with { GcLimit = 512 * m }) == 0, "managed pressure can drop to zero");
        Check(policy.Next(256 * m, 0, 0, 2048 * m, healthy with { TotalPhysical = 0 }) == 256 * m, "invalid samples do not grow");
        current = 0;
        for (int i = 0; i < 100; i++) current = policy.Next(current, current, 0, 1024 * m, healthy);
        Check(current == 1024 * m, "stable recovery reaches but never exceeds maximum");
    }
}
