using System.Security.Cryptography;
using System.Text;

namespace NVEncVideoWriterPlugin;

// What one frame of the root timeline depends on, so that an edit only invalidates the frames it touches and a
// frame only verifies the files it uses. Built once per project revision; host-independent so that the rules
// are unit-tested without YMM4. The rules mirror the host renderers read in YMM4 4.56.1.0:
// - TimelineSource renders the items CompositeItemPicker picks at the frame, a subset of the items containing it
//   (hidden items and layers are excluded by the picker; including them only costs reuse).
// - A transition also renders the items at its first frame - 1 (TransitionItemPicker, isBefore), recursively.
// - Scene items and audio-reactive shapes read other timelines or the timeline's audio around the frame; such
//   frames depend on the whole project ("wide"), as every frame did before.
// - An image sequence shows one of its files per frame (ImageSequence): those frames depend on that file only, so
//   segments also end where the file shown changes.
internal sealed class FrameDependencyIndex
{
    internal const string Version = "frame-deps-v4";
    private const int MaximumCachedSegments = 65536;

    // Uncacheable: the item uses something that cannot be fingerprinted (a font file that is not local, a remote file)
    // or renders from asynchronous state (a tachie's lip sync); only the frames that contain it are rendered normally.
    // Session: its hash holds identity hashes of the objects YMM4 seeds randomness with, so its frames are keyed only
    // for these objects (this process, not a clone of the scene).
    // Culture: it formats numbers with the rendering thread's culture, so its frames are only keyed on a thread whose
    // culture matches the description's (Culture below).
    // FrameFiles: the file it shows at each of its frames (index: frame - Frame), besides Files.
    internal readonly record struct Entry(int Frame, int Length, bool IsTransition, bool IsWide, string Hash, string[] Files,
        bool Uncacheable = false, bool Session = false, bool Culture = false, string[]? FrameFiles = null,
        int? Layer = null, bool AlwaysOnTop = false, FileRange[]? FileRanges = null)
    {
        internal bool Contains(long frame) => Frame <= frame && frame < (long)Frame + Length;
    }

    // Ordinary files selected during a subrange of an item, without asserting that an image-sequence decoder
    // displayed that file. Used by the simple tachie's face selection (the decoder still validates its own time).
    internal readonly record struct FileRange(int Frame, int Length, string[] Files)
    {
        internal bool Contains(long frame) => Frame <= frame && frame < (long)Frame + Length;
    }

    // Shown: the images of image sequences the frame shows (also in Files); the reader must show each of them.
    internal sealed record Dependencies(string Content, string[] Files, bool Wide, bool Cacheable = true, bool Session = false,
        bool Culture = false, string[]? Shown = null);

    private readonly Entry[] entries;
    private readonly string globalHash;
    private readonly string nestedHash;
    private readonly string[] globalFiles;
    private readonly string[] nestedFiles;
    private readonly bool nestedUncacheable;
    private readonly bool nestedSession;
    private readonly bool nestedCulture;
    private readonly long[] boundaries;
    private readonly bool potentialOrderAmbiguity;
    private readonly Dictionary<int, Dependencies> segments = [];
    private Dependencies? whole;

    internal FrameDependencyIndex(string globalHash, IEnumerable<string> globalFiles, string nestedHash,
        IEnumerable<string> nestedFiles, IEnumerable<Entry> entries, bool nestedUncacheable = false, bool nestedSession = false,
        bool nestedCulture = false, string culture = "")
    {
        this.nestedUncacheable = nestedUncacheable;
        this.nestedSession = nestedSession;
        this.nestedCulture = nestedCulture;
        Culture = culture;
        this.globalHash = globalHash;
        this.nestedHash = nestedHash;
        this.globalFiles = Distinct(globalFiles);
        this.nestedFiles = Distinct(nestedFiles);
        this.entries = entries.ToArray();
        potentialOrderAmbiguity = HasPotentialOrderAmbiguity(this.entries);
        if (this.entries.FirstOrDefault(e => e.FrameFiles is { } files && files.Length != e.Length) is { FrameFiles: not null } wrong)
            throw new ArgumentException($"An entry at {wrong.Frame} has {wrong.FrameFiles.Length} frame files for {wrong.Length} frames");
        if (this.entries.Any(entry => entry.FileRanges?.Any(range => range.Length <= 0 || range.Frame < entry.Frame
            || (long)range.Frame + range.Length > (long)entry.Frame + entry.Length) == true))
            throw new ArgumentException("A file range lies outside its item");
        boundaries = this.entries.SelectMany(Boundaries).Distinct().Order().ToArray();
    }

    // The culture the description formatted numbers with (FrameCacheKey.CultureIdentity).
    internal string Culture { get; }

    private static IEnumerable<long> Boundaries(Entry entry)
    {
        yield return entry.Frame;
        if (entry.FrameFiles is { } files)
            for (int i = 1; i < files.Length; i++)
                if (!string.Equals(files[i], files[i - 1], StringComparison.OrdinalIgnoreCase)) yield return (long)entry.Frame + i;
        foreach (var range in entry.FileRanges ?? [])
        {
            yield return range.Frame;
            yield return (long)range.Frame + range.Length;
        }
        yield return (long)entry.Frame + entry.Length;
    }

    internal int Count => entries.Length;
    internal string[] AllFiles => Whole.Files;

    // Frames in one segment are contained by exactly the same entries, so they share their dependencies.
    internal int SegmentOf(int frame)
    {
        int index = Array.BinarySearch(boundaries, (long)frame);
        return index >= 0 ? index + 1 : ~index;
    }

    // The first frame after the segment of `frame` (int.MaxValue when the segment is unbounded).
    internal int SegmentEnd(int frame)
    {
        int segment = SegmentOf(frame);
        return segment < boundaries.Length ? (int)Math.Min(int.MaxValue, boundaries[segment]) : int.MaxValue;
    }

    // As For, with the frames that share them: [start, end) (int.MinValue / int.MaxValue when unbounded), so that a
    // caller walking many frames looks a segment up once.
    internal Dependencies For(int frame, out int start, out int end)
    {
        int segment = SegmentOf(frame);
        start = segment == 0 ? int.MinValue : (int)Math.Clamp(boundaries[segment - 1], int.MinValue, int.MaxValue);
        end = segment < boundaries.Length ? (int)Math.Clamp(boundaries[segment], int.MinValue, int.MaxValue) : int.MaxValue;
        return For(frame, segment);
    }

    internal Dependencies For(int frame) => For(frame, SegmentOf(frame));

    private Dependencies For(int frame, int segment)
    {
        lock (segments) if (segments.TryGetValue(segment, out var cached)) return cached;
        var dependencies = Compute(frame);
        lock (segments) if (segments.Count < MaximumCachedSegments) segments[segment] = dependencies;
        return dependencies;
    }

    // Every frame's dependencies at once (every file of every frame): the files fingerprinted for the project.
    internal Dependencies Whole
    {
        get
        {
            lock (segments)
                return whole ??= Create(Enumerable.Range(0, entries.Length), wide: true,
                    entries.SelectMany(entry => entry.FrameFiles ?? []), potentialOrderAmbiguity,
                    entries.SelectMany(entry => entry.FileRanges ?? []).SelectMany(range => range.Files)) with { Shown = null };
        }
    }

    private Dependencies Compute(int frame)
    {
        var included = new HashSet<int>();
        var shown = new List<string>();
        var ranged = new List<string>();
        var pending = new Queue<long>();
        pending.Enqueue(frame);
        var visited = new HashSet<long>();
        bool wide = false;
        bool ambiguousOrder = false;
        while (pending.Count != 0)
        {
            long at = pending.Dequeue();
            if (!visited.Add(at)) continue;
            var orders = new HashSet<(int Layer, bool AlwaysOnTop)>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Contains(at)) continue;
                // TimelineSource orders a resource dictionary by top/Z/layer. Equal
                // sort values retain insertion order, which prefetch/parallel creation
                // and seek history can change. We cannot certify Z ties before render;
                // overlapping equal layers/top states conservatively render normally.
                if (entries[i].Layer is int layer && !orders.Add((layer, entries[i].AlwaysOnTop))) ambiguousOrder = true;
                // An item a transition also draws at its first frame - 1 shows its image of that frame too.
                if (entries[i].FrameFiles is { } files) shown.Add(files[(int)(at - entries[i].Frame)]);
                foreach (var range in entries[i].FileRanges ?? []) if (range.Contains(at)) ranged.AddRange(range.Files);
                if (!included.Add(i)) continue;
                wide |= entries[i].IsWide;
                if (entries[i].IsTransition) pending.Enqueue((long)entries[i].Frame - 1);
            }
        }
        // A wide frame reads the whole project, but root items only draw the images of their frames.
        if (wide) return shown.Count == 0 && !ambiguousOrder ? Whole
            : Create(Enumerable.Range(0, entries.Length), wide: true, shown, ambiguousOrder || potentialOrderAmbiguity,
                entries.SelectMany(entry => entry.FileRanges ?? []).SelectMany(range => range.Files));
        return Create(included, wide: false, shown, ambiguousOrder, ranged);
    }

    private Dependencies Create(IEnumerable<int> included, bool wide, IEnumerable<string> shown, bool ambiguousOrder = false,
        IEnumerable<string>? ranged = null)
    {
        var hashes = included.Select(i => entries[i].Hash).Order(StringComparer.Ordinal).ToArray();
        var content = new StringBuilder(Version).Append("|global:").Append(globalHash)
            .Append("|nested:").Append(wide ? nestedHash : "-").Append("|items:").Append(hashes.Length);
        foreach (var hash in hashes) content.Append('|').Append(hash);
        var files = globalFiles.Concat(included.SelectMany(i => entries[i].Files)).Concat(shown).Concat(ranged ?? []);
        if (wide) files = files.Concat(nestedFiles);
        bool cacheable = !ambiguousOrder && !included.Any(i => entries[i].Uncacheable) && !(wide && nestedUncacheable);
        bool session = included.Any(i => entries[i].Session) || wide && nestedSession;
        bool culture = included.Any(i => entries[i].Culture) || wide && nestedCulture;
        string[] images = Distinct(shown);
        return new Dependencies(content.ToString(), Distinct(files), wide, cacheable, session, culture, images.Length == 0 ? null : images);
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static bool HasPotentialOrderAmbiguity(IEnumerable<Entry> entries)
    {
        foreach (var group in entries.Where(entry => entry.Layer is not null && entry.Length > 0)
            .GroupBy(entry => (entry.Layer, entry.AlwaysOnTop)))
        {
            long end = long.MinValue;
            foreach (var entry in group.OrderBy(entry => entry.Frame))
            {
                if (entry.Frame < end) return true;
                end = Math.Max(end, (long)entry.Frame + entry.Length);
            }
        }
        return false;
    }

    private static string[] Distinct(IEnumerable<string> files) =>
        files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
