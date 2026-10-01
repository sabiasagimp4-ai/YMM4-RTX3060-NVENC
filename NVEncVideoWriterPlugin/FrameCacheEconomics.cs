namespace NVEncVideoWriterPlugin;

// CPU Update+Draw wall time, not GPU execution time. Unknown costs always admit. Original render observations
// stay separate from restoration observations; conservative hysteresis avoids rejecting uncertain savings.
internal sealed class FrameCacheEconomics
{
    private const int Limit = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, (long Ticks, int Samples, LinkedListNode<string> Node)> renders = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = [];
    private readonly Dictionary<int, (double Mean, int Samples)> restores = [];
    private readonly Dictionary<int, (double Mean, int Samples)> gpuRestores = [];
    private long rejected;
    internal long Rejected { get { lock (gate) return rejected; } }
    private static int Bucket(long bytes) => bytes <= 0 ? -1 : System.Numerics.BitOperations.Log2((ulong)bytes);
    internal void ObserveRender(string key, long ticks)
    {
        if (ticks <= 0) return;
        lock (gate)
        {
            if (renders.TryGetValue(key, out var previous))
            {
                lru.Remove(previous.Node); lru.AddLast(previous.Node);
                renders[key] = (Math.Max(ticks, previous.Ticks), Math.Min(16, previous.Samples + 1), previous.Node);
            }
            else
            {
                if (renders.Count >= Limit && lru.First is { } old) { renders.Remove(old.Value); lru.RemoveFirst(); }
                renders.Add(key, (ticks, 1, lru.AddLast(key)));
            }
        }
    }
    internal void ObserveRestore(long bytes, long ticks, bool gpu = false)
    {
        if (bytes <= 0 || ticks <= 0) return;
        lock (gate)
        {
            var observations = gpu ? gpuRestores : restores;
            int bucket = Bucket(bytes); observations.TryGetValue(bucket, out var previous);
            int n = Math.Min(32, previous.Samples + 1);
            observations[bucket] = (previous.Mean + (ticks - previous.Mean) / n, n);
        }
    }
    internal bool ShouldAdmit(string key, long bytes, bool gpuEnabled = false)
    {
        lock (gate)
        {
            // Require repeated renders and a warmed restoration path. Reject only at a 2x cost disadvantage.
            bool admit = !renders.TryGetValue(key, out var render) || render.Samples < 2
                || !restores.TryGetValue(Bucket(bytes), out var restore) || restore.Samples < 8
                || render.Ticks >= restore.Mean / 2
                || gpuEnabled && (!gpuRestores.TryGetValue(Bucket(bytes), out var gpu) || gpu.Samples < 8 || render.Ticks >= gpu.Mean / 2);
            if (!admit) rejected++;
            return admit;
        }
    }
    internal void Clear() { lock (gate) { renders.Clear(); lru.Clear(); restores.Clear(); gpuRestores.Clear(); rejected = 0; } }
}
