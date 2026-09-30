using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NVEncVideoWriterPlugin;

internal readonly record struct FileStamp(ulong Volume, ulong IdLow, ulong IdHigh, long Length, long LastWriteTime, long ChangeTime);
internal sealed record FileFingerprint(FileStamp Stamp, string ContentHash);

// Deny writes and replacement until the caller has finished using/committing the frame.
internal sealed class FileDependencyLease : IDisposable
{
    private const int SharedFingerprintCapacity = 512;
    // ponytail: one short global lock keeps the shared fingerprint index bounded and simple; shard by volume if contention is measurable.
    private static readonly object SharedFingerprintGate = new();
    private static readonly Dictionary<string, FileFingerprint> SharedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> SharedFingerprintOrder = new();
    private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileFingerprint> fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;
    public IReadOnlyDictionary<string, FileFingerprint> Fingerprints { get; }

    private FileDependencyLease() => Fingerprints = new ReadOnlyDictionary<string, FileFingerprint>(fingerprints);

    public static bool TryAcquire(IEnumerable<string> paths, IReadOnlyDictionary<string, FileFingerprint>? prior,
        long maxHashBytes, out FileDependencyLease? lease, CancellationToken cancellationToken = default)
        => TryAcquire(paths, prior, maxHashBytes, out lease, out _, cancellationToken);

    public static bool TryAcquire(IEnumerable<string> paths, IReadOnlyDictionary<string, FileFingerprint>? prior,
        long maxHashBytes, out FileDependencyLease? lease, out string reason, CancellationToken cancellationToken = default)
    {
        lease = null;
        reason = "外部素材を検証できませんでした。";
        var candidate = new FileDependencyLease();
        try
        {
            long bytes = 0;
            foreach (string suppliedPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(suppliedPath);
                if (candidate.files.ContainsKey(path)) continue;
                if (candidate.files.Count == 256) { reason = "External file count exceeds 256."; return false; }
                if (!IsLocalPlainPath(path)) { reason = "外部素材はリンクを含まないローカル固定ドライブ上にある必要があります。"; return false; }
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                candidate.files.Add(path, file);
                if (!IsNtfs(file.SafeFileHandle) || !TryStamp(file.SafeFileHandle, out var stamp))
                { reason = "外部素材はNTFS上の通常ファイルである必要があります。"; return false; }
                string hash;
                FileFingerprint? previous = null;
                bool reused = prior is not null && prior.TryGetValue(path, out previous) && previous.Stamp == stamp;
                if (reused)
                    hash = previous!.ContentHash;
                else if (TryGetSharedFingerprint(path, stamp, out hash))
                    reused = true;
                else
                {
                    bytes = checked(bytes + stamp.Length);
                    if (bytes > maxHashBytes) { reason = "外部素材のサイズが内容確認の上限を超えています。"; return false; }
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[64 * 1024];
                    int count;
                    while ((count = file.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        digest.AppendData(buffer, 0, count);
                    }
                    hash = Convert.ToHexString(digest.GetHashAndReset());
                }
                if (!TryStamp(file.SafeFileHandle, out var after) || after != stamp) return false;
                var fingerprint = new FileFingerprint(stamp, hash);
                candidate.fingerprints.Add(path, fingerprint);
                RememberSharedFingerprint(path, fingerprint);
            }
            if (!candidate.VerifyPaths()) return false;
            lease = candidate;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "External file unavailable or validation cancelled: " + ex.GetType().Name;
            return false;
        }
        finally
        {
            if (lease is null) candidate.Dispose();
        }
    }

    private static bool TryGetSharedFingerprint(string path, FileStamp stamp, out string hash)
    {
        lock (SharedFingerprintGate)
        {
            if (SharedFingerprints.TryGetValue(path, out var fingerprint) && fingerprint.Stamp == stamp)
            {
                hash = fingerprint.ContentHash;
                return true;
            }
        }
        hash = string.Empty;
        return false;
    }

    private static void RememberSharedFingerprint(string path, FileFingerprint fingerprint)
    {
        lock (SharedFingerprintGate)
        {
            if (!SharedFingerprints.ContainsKey(path)) SharedFingerprintOrder.Enqueue(path);
            SharedFingerprints[path] = fingerprint;
            while (SharedFingerprints.Count > SharedFingerprintCapacity)
                SharedFingerprints.Remove(SharedFingerprintOrder.Dequeue());
        }
    }

    public bool VerifyPaths()
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        try
        {
            foreach (var pair in files)
            {
                if (!IsLocalPlainPath(pair.Key)) return false;
                // Also resolve the name again: a parent-directory rename must not alias a cached file.
                using var current = File.OpenHandle(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!TryStamp(current, out var stamp) || stamp != fingerprints[pair.Key].Stamp) return false;
            }
            return Volatile.Read(ref disposed) == 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    private static bool IsLocalPlainPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 3 || path[1] != ':' || path[2] != '\\') return false;
        string root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static bool IsNtfs(SafeFileHandle handle)
    {
        var name = new StringBuilder(32);
        return GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, name, name.Capacity)
            && name.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryStamp(SafeFileHandle handle, out FileStamp stamp)
    {
        stamp = default;
        if (!GetBasicInfo(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())
            || !GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())
            || !GetIdInfo(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())
            || standard.Directory != 0 || standard.DeletePending != 0 || standard.EndOfFile < 0
            || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return false;
        stamp = new(id.Volume, id.Low, id.High, standard.EndOfFile, basic.LastWriteTime, basic.ChangeTime);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var file in files.Values) file.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInfo(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdInfo(SafeFileHandle handle, int infoClass, out IdInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName, int volumeNameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystemName, int fileSystemNameSize);
}
