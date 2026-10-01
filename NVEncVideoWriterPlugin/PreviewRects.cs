using System.Diagnostics;

namespace NVEncVideoWriterPlugin;

internal static class PreviewUsage
{
    // Only this effect makes Playing and Paused previews differ (lip-synced Tachie, the other usage-dependent
    // renderer, is never cached), so without it both share one key and paused frames serve playback.
    // Matched loosely on the model text: any mention keeps the keys apart (fail-closed).
    internal const string ShowOnlyPreviewEffectName = "ShowOnlyPreviewEffect";

    internal static bool ModelUsesShowOnlyPreview(string model) => model.Contains(ShowOnlyPreviewEffectName, StringComparison.Ordinal);

    // MeshDeformation control points snapshot which mesh points are selected, and that selection changes without
    // an edit (no project revision), so rects of such projects are never reused. Loose match, fail-closed.
    // The other controllers of the inspected host (YMM4 4.56.1.0) depend only on the model and the frame.
    internal const string SelectionDependentControllerName = "MeshDeformation";

    internal static bool RectsReusable(string model) => !model.Contains(SelectionDependentControllerName, StringComparison.Ordinal);

    internal static string KeyFor(string usage, bool modelUsesShowOnlyPreview) =>
        usage is "Playing" or "Paused" && !modelUsesShowOnlyPreview ? "Preview" : usage;
}

// The item rectangles TimelineSource computes for the preview's selection, hover and drag UI. They are
// geometry plus references to model objects, so they are kept only while neither the cache generation nor the
// project revision has changed (any edit drops them all) and are restored when the frame comes from the pixel
// cache. A frame shown without them is re-rendered once the playhead has rested on it, so editing in the
// preview always works on host-computed rects.
internal sealed class PreviewRects<T>
{
    private const int Capacity = 512;
    private readonly object gate = new();
    private readonly Dictionary<string, (T[] Rects, LinkedListNode<string> Node)> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = new();
    private (long Generation, long Revision) epoch = (long.MinValue, long.MinValue);
    private long missingTicks = long.MinValue;
    private long missingSince;
    private bool refreshRequested;

    internal int Count { get { lock (gate) return entries.Count; } }

    internal void Remember(string key, T[] rects, long generation, long revision)
    {
        lock (gate)
        {
            Enter(generation, revision);
            if (entries.Remove(key, out var previous)) order.Remove(previous.Node);
            entries[key] = (rects, order.AddLast(key));
            while (entries.Count > Capacity && order.First is { } oldest)
            {
                entries.Remove(oldest.Value);
                order.RemoveFirst();
            }
        }
    }

    internal bool TryRecall(string key, long generation, long revision, out T[] rects)
    {
        lock (gate)
        {
            rects = [];
            Enter(generation, revision);
            if (!entries.TryGetValue(key, out var entry)) return false;
            order.Remove(entry.Node);
            order.AddLast(entry.Node);
            rects = entry.Rects;
            return true;
        }
    }

    // Rects hold model references; a new epoch releases every older entry at once.
    private void Enter(long generation, long revision)
    {
        if (epoch == (generation, revision)) return;
        entries.Clear();
        order.Clear();
        epoch = (generation, revision);
    }

    // A cached frame was shown at this time without rects.
    internal void MarkMissing(TimeSpan time, long timestamp)
    {
        lock (gate)
        {
            missingTicks = time.Ticks;
            missingSince = timestamp;
            refreshRequested = false;
        }
    }

    internal bool IsMissing(TimeSpan time)
    {
        lock (gate) return missingTicks == time.Ticks;
    }

    // A real render produced rects; nothing is missing any more.
    internal void Rendered()
    {
        lock (gate)
        {
            missingTicks = long.MinValue;
            refreshRequested = false;
        }
    }

    // True once per missing frame, after the playhead rested on it for the delay (fast scrubbing stays cached).
    internal bool ShouldRequestRefresh(long timestamp, TimeSpan delay)
    {
        lock (gate)
        {
            if (missingTicks == long.MinValue || refreshRequested
                || Stopwatch.GetElapsedTime(missingSince, timestamp) < delay) return false;
            refreshRequested = true;
            return true;
        }
    }
}
