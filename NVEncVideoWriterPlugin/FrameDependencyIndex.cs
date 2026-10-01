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
internal sealed class FrameDependencyIndex
{
    internal const string Version = "frame-deps-v1";
    private const int MaximumCachedSegments = 65536;

    // Uncacheable: the item uses something that cannot be fingerprinted (an uninstalled font, a remote file) or
    // renders from asynchronous state (a tachie's lip sync); only the frames that contain it are rendered normally.
    // Session: its hash holds identity hashes of the objects YMM4 seeds randomness with, so its frames are keyed only
    // for these objects (this process, not a clone of the scene).
    internal readonly record struct Entry(int Frame, int Length, bool IsTransition, bool IsWide, string Hash, string[] Files,
        bool Uncacheable = false, bool Session = false)
    {
        internal bool Contains(long frame) => Frame <= frame && frame < (long)Frame + Length;
    }

    internal sealed record Dependencies(string Content, string[] Files, bool Wide, bool Cacheable = true, bool Session = false);

    private readonly Entry[] entries;
    private readonly string globalHash;
    private readonly string nestedHash;
    private readonly string[] globalFiles;
    private readonly string[] nestedFiles;
    private readonly bool nestedUncacheable;
    private readonly bool nestedSession;
    private readonly long[] boundaries;
    private readonly Dictionary<int, Dependencies> segments = [];
    private Dependencies? whole;

    internal FrameDependencyIndex(string globalHash, IEnumerable<string> globalFiles, string nestedHash,
        IEnumerable<string> nestedFiles, IEnumerable<Entry> entries, bool nestedUncacheable = false, bool nestedSession = false)
    {
        this.nestedUncacheable = nestedUncacheable;
        this.nestedSession = nestedSession;
        this.globalHash = globalHash;
        this.nestedHash = nestedHash;
        this.globalFiles = Distinct(globalFiles);
        this.nestedFiles = Distinct(nestedFiles);
        this.entries = entries.ToArray();
        boundaries = this.entries.SelectMany(e => new[] { (long)e.Frame, (long)e.Frame + e.Length }).Distinct().Order().ToArray();
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

    internal Dependencies For(int frame)
    {
        int segment = SegmentOf(frame);
        lock (segments) if (segments.TryGetValue(segment, out var cached)) return cached;
        var dependencies = Compute(frame);
        lock (segments) if (segments.Count < MaximumCachedSegments) segments[segment] = dependencies;
        return dependencies;
    }

    internal Dependencies Whole
    {
        get
        {
            lock (segments)
                return whole ??= Create(Enumerable.Range(0, entries.Length), wide: true);
        }
    }

    private Dependencies Compute(int frame)
    {
        var included = new HashSet<int>();
        var pending = new Queue<long>();
        pending.Enqueue(frame);
        var visited = new HashSet<long>();
        while (pending.Count != 0)
        {
            long at = pending.Dequeue();
            if (!visited.Add(at)) continue;
            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Contains(at) || !included.Add(i)) continue;
                if (entries[i].IsWide) return Whole;
                if (entries[i].IsTransition) pending.Enqueue((long)entries[i].Frame - 1);
            }
        }
        return Create(included, wide: false);
    }

    private Dependencies Create(IEnumerable<int> included, bool wide)
    {
        var hashes = included.Select(i => entries[i].Hash).Order(StringComparer.Ordinal).ToArray();
        var content = new StringBuilder(Version).Append("|global:").Append(globalHash)
            .Append("|nested:").Append(wide ? nestedHash : "-").Append("|items:").Append(hashes.Length);
        foreach (var hash in hashes) content.Append('|').Append(hash);
        var files = globalFiles.Concat(included.SelectMany(i => entries[i].Files));
        if (wide) files = files.Concat(nestedFiles);
        bool cacheable = !included.Any(i => entries[i].Uncacheable) && !(wide && nestedUncacheable);
        bool session = included.Any(i => entries[i].Session) || wide && nestedSession;
        return new Dependencies(content.ToString(), Distinct(files), wide, cacheable, session);
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string[] Distinct(IEnumerable<string> files) =>
        files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
