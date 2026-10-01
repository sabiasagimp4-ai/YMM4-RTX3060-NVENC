using NVEncVideoWriterPlugin;

internal static class FrameAdmissionChecks
{
    internal static void Run()
    {
        // Repeat a working set larger than the cache. Plain LRU gets zero hits on later scans;
        // frequency admission preserves useful residents under the same byte/entry capacity.
        int Simulate(bool admission)
        {
            var history = new FrameAdmissionHistory();
            var residents = new LinkedList<string>();
            int hits = 0;
            for (int pass = 0; pass < 4; pass++) for (int frame = 0; frame < 90; frame++)
            {
                string key = frame.ToString(); history.Observe(key);
                if (residents.Find(key) is { } node) { hits++; residents.Remove(node); residents.AddLast(node); }
                else
                {
                    if (residents.Count == 36 && admission && history.Frequency(key) <= history.Frequency(residents.First!.Value)) continue;
                    if (residents.Count == 36) residents.RemoveFirst();
                    residents.AddLast(key);
                }
            }
            return hits;
        }
        Check(Simulate(false) == 0 && Simulate(true) == 108, "Scan resistance failed");
        var aged = new FrameAdmissionHistory();
        for (int i = 0; i < 80; i++) aged.Observe("old-hot");
        for (int i = 0; i < 8192; i++) aged.Observe("new-hot");
        Check(aged.Frequency("new-hot") > aged.Frequency("old-hot"), "Old access pattern did not decay");
        for (int i = 0; i < 10000; i++) aged.Observe("distinct-" + i);
        Check(aged.Count == 256, "History grew without bound");
        Console.WriteLine("GPU admission: scan resistance, frequency aging, bounded metadata passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
