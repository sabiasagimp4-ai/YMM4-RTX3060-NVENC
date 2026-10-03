using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vortice.DirectWrite;

namespace NVEncVideoWriterPlugin;

// The installed fonts as DirectWrite sees them. Text draws in faces other than the named family's: a family
// DirectWrite does not have is drawn by font fallback, and characters a font lacks fall back to other installed
// fonts. Neither is followed face by face; instead every item that draws text holds this identity of all the system
// font files (path, size, write time, face index, simulations), so installing, removing or updating any font
// re-keys those frames. The named family's own files are still fingerprinted (FrameCacheKey.ResolveFont).
internal static class FontEnvironment
{
    // A font install is noticed within this time (and on every description).
    private const long RefreshMilliseconds = 10_000;

    private static string? stamp;
    private static long generation;
    private static long nextRefresh;
    private static int refreshing;
    private static readonly object gate = new();

    // Changes whenever the stamp changes; trackers describe again when it does.
    internal static long Generation => Interlocked.Read(ref generation);

    // A resource ("fonts://" and a hash). Null when DirectWrite cannot list the fonts (text is then not cached).
    internal static string? Stamp
    {
        get
        {
            if (Volatile.Read(ref stamp) is { } known && Environment.TickCount64 < Interlocked.Read(ref nextRefresh)) return known;
            Refresh();
            return Volatile.Read(ref stamp);
        }
    }

    // For each frame capture: re-reads the fonts in the background when due, once the stamp has been used.
    internal static void RefreshIfDue()
    {
        if (Volatile.Read(ref stamp) is null || Environment.TickCount64 < Interlocked.Read(ref nextRefresh)
            || Interlocked.Exchange(ref refreshing, 1) != 0) return;
        Task.Run(() =>
        {
            try { Refresh(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            finally { Volatile.Write(ref refreshing, 0); }
        });
    }

    private static void Refresh()
    {
        lock (gate)
        {
            if (Volatile.Read(ref stamp) is not null && Environment.TickCount64 < Interlocked.Read(ref nextRefresh)) return;
            string? next;
            try { next = Compute(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { next = null; }
            Interlocked.Exchange(ref nextRefresh, Environment.TickCount64 + RefreshMilliseconds);
            if (next == Volatile.Read(ref stamp)) return;
            Volatile.Write(ref stamp, next);
            Interlocked.Increment(ref generation);
        }
    }

    private static string Compute()
    {
        // As YMM4 lists its fonts (FontSettings.GetDirectWriteFontsFromFontSet): the local system font set.
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory6>();
        using var fonts = factory.GetSystemFontSet(false);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>(fonts.FontCount);
        for (int i = 0; i < fonts.FontCount; i++)
        {
            using var reference = fonts.GetFontFaceReference(i);
            using var file = reference.FontFile;
            string identity;
            if (FrameCacheKey.LocalPath(file) is { } path)
            {
                if (!files.TryGetValue(path, out var known))
                {
                    var info = new FileInfo(path);
                    files[path] = known = info.Exists ? $"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path.ToUpperInvariant() + "|-";
                }
                identity = known;
            }
            else identity = "key:" + Convert.ToHexString(file.GetReferenceKey().ToArray());
            entries.Add($"{identity}#{reference.FontFaceIndex}|{(int)reference.Simulations}");
        }
        entries.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string entry in entries) hash.AppendData(Encoding.UTF8.GetBytes(entry + "\n"));
        return "fonts://" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
