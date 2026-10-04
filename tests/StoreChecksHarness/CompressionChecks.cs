using System.Diagnostics;
using System.Security.Cryptography;
using NVEncVideoWriterPlugin;

internal static class CompressionChecks
{
    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "compression");
        string key1 = Convert.ToHexStringLower(SHA256.HashData("compressible"u8));
        string key2 = Convert.ToHexStringLower(SHA256.HashData("noise"u8));
        byte[] plain = new byte[16 * 1024], noise = new byte[16 * 1024];
        for (int i = 0; i < plain.Length; i++) plain[i] = (byte)(i % 8);
        RandomNumberGenerator.Fill(noise);
        using (var store = new FrameCacheStore(root, 32768, 65536))
        {
            store.Put(key1, plain); store.Put(key2, noise);
            Wait(() => store.DiskWrites == 2, "write completion");
            Check(store.DiskBytes < plain.Length + noise.Length, "compressed physical budget");
        }
        string Record(string key) => Directory.EnumerateFiles(root, key + ".ymmframe", SearchOption.AllDirectories).Single();
        Check(File.ReadAllBytes(Record(key1)).AsSpan(0, 8).SequenceEqual("YMMFRZ02"u8), "compressible frame uses lossless record");
        Check(File.ReadAllBytes(Record(key2)).AsSpan(0, 8).SequenceEqual("YMMFRM02"u8), "noise uses raw fallback");
        using (var store = new FrameCacheStore(root, 32768, 65536))
        {
            Wait(() => store.DiskBytes > 0, "index ready");
            Check(!store.TryGetCached(key1, out _) && store.DiskReads == 0, "cached-only does not deliver disk records");
            Check(store.TryGet(key1, TimeSpan.FromSeconds(5), out var restored, out bool disk) && disk && restored.Span.SequenceEqual(plain), "lossless compressed roundtrip");
            Check(store.TryGet(key2, TimeSpan.FromSeconds(5), out restored, out disk) && disk && restored.Span.SequenceEqual(noise), "legacy raw roundtrip");
            Check(store.TryGetCached(key1, out restored) && restored.Span.SequenceEqual(plain), "cached-only returns immutable RAM result");
        }
        byte[] corrupt = File.ReadAllBytes(Record(key1)); corrupt[16] ^= 0xff; File.WriteAllBytes(Record(key1), corrupt);
        using (var store = new FrameCacheStore(root, 32768, 65536))
        {
            Wait(() => store.DiskBytes > 0, "corrupt index ready");
            Check(!store.TryGet(key1, TimeSpan.FromSeconds(5), out _, out _), "decoded checksum corruption rejected");
            Check(!File.Exists(RecordOrNull(root, key1)), "corrupt compressed record removed");
        }
        Console.WriteLine("Disk compression: lossless parity, raw fallback, physical budgets, cached-only and corruption checks passed.");
    }
    private static string? RecordOrNull(string root, string key) => Directory.EnumerateFiles(root, key + ".ymmframe", SearchOption.AllDirectories).FirstOrDefault();
    private static void Wait(Func<bool> condition, string message)
    { if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10))) throw new Exception(message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Compression: " + message); }
}
