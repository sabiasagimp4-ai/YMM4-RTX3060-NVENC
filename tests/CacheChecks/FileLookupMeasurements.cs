using System.Diagnostics;
using System.Text.Json;
using NVEncVideoWriterPlugin;

internal static class FileLookupMeasurements
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ymm-file-lookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = Enumerable.Range(0, 500).Select(i => Path.Combine(root, i + ".bin")).ToArray();
            foreach (string path in paths) File.WriteAllBytes(path, [1, 2, 3, 4]);
            var known = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in paths.Chunk(128))
            {
                Check(FileDependencyLease.TryAcquire(group, null, 1L << 20, out var verified), "Measurement file unavailable");
                using (verified) foreach (var pair in verified!.Fingerprints) known.Add(pair.Key, pair.Value);
            }
            foreach (int count in new[] { 24, 500 })
            for (int repeat = 1; repeat <= 2; repeat++)
            {
                int attempts = count == 24 ? 100 : 4, accepted = 0;
                long opens = FileDependencyLease.FileOpens;
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < attempts; i++)
                {
                    if (FileDependencyLease.TryAcquire(paths.Take(count), known, 0, out var lease)) accepted++;
                    lease?.Dispose();
                }
                Console.WriteLine("SPEEDUP4 " + JsonSerializer.Serialize(new
                {
                    count, repeat, lookup_ms = clock.Elapsed.TotalMilliseconds / attempts,
                    opens_per_lookup = (FileDependencyLease.FileOpens - opens) / (double)attempts,
                    eligible = accepted == attempts, attempts,
                }));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
