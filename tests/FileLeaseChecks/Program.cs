using NVEncVideoWriterPlugin;
using System.Diagnostics;

string dir = Path.Combine(Path.GetTempPath(), "ymm-file-lease-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
void Check(bool value, string message) { if (!value) throw new Exception(message); }
bool Acquire(string[] paths, IReadOnlyDictionary<string, FileFingerprint>? prior, out FileDependencyLease? result) =>
    FileDependencyLease.TryAcquire(paths, prior, 1024 * 1024, out result);
bool WriteBlocked(string path)
{
    try { using var file = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return false; }
    catch (IOException) { return true; }
}
try
{
    string path = Path.Combine(dir, "source.dat");
    File.WriteAllText(path, "AAAA");
    DateTime time = File.GetLastWriteTimeUtc(path);
    Check(Acquire([path], null, out var first), "acquire");
    var fingerprints = first!.Fingerprints;
    Check(first.VerifyPaths(), "verify");
    Check(WriteBlocked(path), "writer permitted while leased");
    Check(Acquire([path], fingerprints, out var second), "parallel readers");
    Check(ReferenceEquals(second!.Fingerprints[path].ContentHash, fingerprints[path].ContentHash), "unchanged stamp rehashed content");
    Check(Acquire([path], null, out var shared), "shared fingerprint reuse");
    Check(ReferenceEquals(shared!.Fingerprints[path].ContentHash, fingerprints[path].ContentHash), "independent lease did not reuse the exact-stamp fingerprint");
    shared.Dispose();
    Check(FileDependencyLease.TryAcquire([path], fingerprints, 0, out var metadataOnly), "unchanged file required a hash budget");
    Check(metadataOnly!.VerifyPaths(), "metadata-only lease failed verification");
    metadataOnly.Dispose();
    first.Dispose();
    Check(WriteBlocked(path), "second lease lost");
    second!.Dispose();
    Check(!WriteBlocked(path), "release did not close handle");
    File.WriteAllText(path, "BBBB");
    File.SetLastWriteTimeUtc(path, time);
    Check(!FileDependencyLease.TryAcquire([path], fingerprints, 0, out _), "changed file bypassed its zero hash budget");
    Check(!WriteBlocked(path), "zero-budget failure leaked handle");
    Check(Acquire([path], fingerprints, out var changed), "changed acquire");
    Check(changed!.Fingerprints[path].ContentHash != fingerprints[path].ContentHash, "same-size restored-mtime change undetected");
    Check(changed.Fingerprints[path].Stamp.ChangeTime != fingerprints[path].Stamp.ChangeTime, "change time invariant");
    changed.Dispose();
    string replacement = Path.Combine(dir, "replacement.dat");
    File.WriteAllText(replacement, "CCCC");
    File.Move(replacement, path, true);
    Check(Acquire([path], fingerprints, out var replaced), "replacement acquire");
    Check(replaced!.Fingerprints[path].Stamp.IdLow != fingerprints[path].Stamp.IdLow, "replacement identity invariant");
    bool blocked = false;
    try { File.Delete(path); } catch (IOException) { blocked = true; }
    Check(blocked, "replacement allowed during lease");
    replaced.Dispose();
    using (var writing = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        Check(!Acquire([path], null, out _), "active writer not bypassed");
    Check(!Acquire([path, Path.Combine(dir, "missing")], null, out _), "missing path not bypassed");
    Check(!WriteBlocked(path), "exception leaked earlier handle");
    string overBudget = Path.Combine(dir, "over-budget.dat");
    File.WriteAllBytes(overBudget, [1, 2]);
    Check(!FileDependencyLease.TryAcquire([overBudget], null, 1, out _), "limit not enforced");
    Check(!WriteBlocked(overBudget), "limit leaked handle");
    Check(!FileDependencyLease.TryAcquire([path], null, 100, out _, new CancellationToken(true)), "cancel not bypassed");
    Check(!Acquire([@"\\invalid-server\share\file"], null, out _), "UNC not bypassed");
    string target = Path.Combine(dir, "target");
    string junction = Path.Combine(dir, "junction");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "file"), "test");
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(target);
    using (var process = Process.Start(start)!) { process.WaitForExit(); Check(process.ExitCode == 0, "create test junction"); }
    Check(!Acquire([Path.Combine(junction, "file")], null, out _), "ancestor junction not bypassed");
    Directory.Delete(junction);
    string[] many = Enumerable.Range(0, 257).Select(i => Path.Combine(dir, $"file{i}")).ToArray();
    foreach (string file in many) File.WriteAllText(file, "x");
    Check(!Acquire(many, null, out _), "handle limit");
    Check(many.All(file => !WriteBlocked(file)), "handle limit leaked handles");
    string large = Path.Combine(dir, "large.dat");
    using (var output = File.Create(large)) output.SetLength(64L * 1024 * 1024);
    var elapsed = Stopwatch.StartNew();
    Check(FileDependencyLease.TryAcquire([large], null, 128L * 1024 * 1024, out var cold), "large cold acquire");
    double coldMs = elapsed.Elapsed.TotalMilliseconds;
    var largeFingerprint = cold!.Fingerprints;
    cold.Dispose();
    elapsed.Restart();
    for (int i = 0; i < 20; ++i)
    {
        Check(FileDependencyLease.TryAcquire([large], largeFingerprint, 128L * 1024 * 1024, out var warm), "large warm acquire");
        warm!.Dispose();
    }
    Console.WriteLine($"64MiB fingerprint cold={coldMs:F2}ms, warm average={elapsed.Elapsed.TotalMilliseconds / 20:F3}ms (20 runs)");
    Console.WriteLine("FileLeaseChecks: passed (write exclusion, parallel leases, restored mtime, replacement, failure cleanup, limits, cancellation, UNC, ancestor junction)");
}
finally { Directory.Delete(dir, true); }
