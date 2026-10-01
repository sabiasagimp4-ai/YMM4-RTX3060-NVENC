namespace NVEncVideoWriterPlugin;

// Bounded, aging frequency history. Admission ties keep resident frames, preventing a sequential
// scan larger than the GPU cache from evicting every frame before the next playback pass.
// This predicts reuse only; it never changes cache validity. The caller serializes access.
internal sealed class FrameAdmissionHistory
{
    private const int Capacity = 256, AgingInterval = 512;
    private readonly Dictionary<string, int> frequencies = [];
    private readonly Queue<string> insertionOrder = [];
    private int observations;
    internal int Count => frequencies.Count;
    internal int Frequency(string key) => frequencies.GetValueOrDefault(key);
    internal void Observe(string key)
    {
        if (++observations == AgingInterval)
        {
            foreach (var existing in frequencies.Keys.ToArray()) frequencies[existing] /= 2;
            observations = 0;
        }
        if (!frequencies.TryGetValue(key, out int count))
        {
            if (frequencies.Count == Capacity) frequencies.Remove(insertionOrder.Dequeue());
            insertionOrder.Enqueue(key);
        }
        frequencies[key] = Math.Min(65535, count + 1);
    }
}
