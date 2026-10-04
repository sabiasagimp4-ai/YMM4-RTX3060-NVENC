using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class VoiceDescriptionChecks
{
    internal static void Run()
    {
        var timeline = new Timeline();
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        byte[] cache = VoiceDescriptionMeasurements.VoiceCache();
        var character = VoiceDescriptionMeasurements.Character("voice-hash-checks");
        var voices = Enumerable.Range(0, 200).Select(i => new VoiceItem(character)
        {
            Frame = i * 90, Length = 90, Layer = 1, Serif = "voice " + i, Font = "Arial",
            JimakuVisibility = JimakuVisibility.Custom,
            VoiceCache = (byte[])cache.Clone(),
        }).ToArray();
        timeline.Items = timeline.Items.AddRange(voices);
        var scene = new Scene(timeline, scenes, []);
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        FrameDependencyIndex Describe(out string model)
        {
            Check(FrameCacheKey.TryDescribe(scene, readers, out model, out _, out var frames, out var reason), reason);
            Check(model.Length < 16 * 1024 * 1024, "200 voices exceeded the model cap");
            return frames!;
        }
        var before = Describe(out string first);
        Check(Enumerable.Range(0, 200 * 90).All(frame => before.For(frame).Cacheable), "A voice frame bypassed");
        string[] keys = Enumerable.Range(0, 200).Select(i => before.For(i * 90).Content).ToArray();
        // Include an in-place edit: byte[] is public and is not intrinsically immutable.
        voices[27].VoiceCache[3] ^= 1;
        var changed = Describe(out _);
        for (int i = 0; i < 200; ++i)
            Check((changed.For(i * 90).Content == keys[i]) == (i != 27), "A voice edit affected the wrong range");
        voices[27].VoiceCache = (byte[])cache.Clone();
        var restored = Describe(out string model);
        Check(Enumerable.Range(0, 200).All(i => restored.For(i * 90).Content == keys[i]), "Equal new arrays did not restore keys");
        var snapshot = FrameDescriptionJson.Load<Snapshot>(model)!;
        var copies = snapshot.Timelines.Single().Items.OfType<VoiceItem>().ToArray();
        for (int i = 0; i < 200; ++i)
            Check(ReferenceEquals(copies[i].VoiceCache, voices[i].VoiceCache) && copies[i].VoiceCache.SequenceEqual(cache),
                "The idle copy lost the live voice payload");
        voices[0].VoiceCache[5] ^= 1;
        bool refused = false;
        try { FrameDescriptionJson.Load<Snapshot>(model); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { refused = true; }
        Check(refused, "The idle copy accepted a payload changed after description");
        string detached = new(model.ToCharArray());
        refused = false;
        try { FrameDescriptionJson.Load<Snapshot>(detached); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { refused = true; }
        Check(refused, "A hash description without its payload witness was decoded as audio");
        // Threshold boundaries and unrelated embedded arrays keep their exact contents on clone load.
        var arrays = new { Small = new byte[4095], Large = new byte[4096] };
        arrays.Large[12] = 17;
        string compact = FrameDescriptionJson.Serialize(arrays);
        var loaded = FrameDescriptionJson.Load<ArraySnapshot>(compact)!;
        Check(loaded.Small.SequenceEqual(arrays.Small) && loaded.Large.SequenceEqual(arrays.Large)
            && !ReferenceEquals(loaded.Large, arrays.Large), "An embedded parameter was lost or shared mutably");
        CheckUnserializedPath(cache);
        Console.WriteLine("Voice hashing: 200 voices/18000 frames, one-byte edits, range isolation, equal-content restore, verified clone payloads and mutable arrays passed.");
    }

    private static void CheckUnserializedPath(byte[] cache)
    {
        string folder = Path.Combine(Path.GetTempPath(), "voice-path-witness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string a = Path.Combine(folder, "a.wav"), b = Path.Combine(folder, "b.wav");
        File.WriteAllBytes(a, [1, 2, 3, 4]); File.WriteAllBytes(b, [1, 2, 3, 4]);
        var path = typeof(VoiceItem).GetField("customVoiceFilePath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var timeline = new Timeline();
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        var voice = new VoiceItem(VoiceDescriptionMeasurements.Character("path-witness"))
            { Frame = 0, Length = 90, VoiceCache = cache, Serif = "path", JimakuVisibility = JimakuVisibility.Custom, Font = "Arial" };
        timeline.Items = timeline.Items.Add(voice);
        path.SetValue(voice, a);
        try
        {
            using var tracker = new KeyDependencyTracker(new Scene(timeline, scenes, []));
            KeyCapture? before = null;
            Check(SpinWait.SpinUntil(() => tracker.TryCapture(0, out before, out _), TimeSpan.FromSeconds(20)), "Voice path witness did not key");
            string first;
            using (before)
            {
                first = before!.Key;
                path.SetValue(voice, b); // No PropertyChanged: the host assigns its temporary file outside JSON.
                Check(!before.Validate(), "A changed temporary voice path did not invalidate a held capture");
            }
            KeyCapture? after = null;
            Check(SpinWait.SpinUntil(() => tracker.TryCapture(0, out after, out _), TimeSpan.FromSeconds(20)), "The changed voice path did not recover");
            using (after) Check(after!.Key != first && after.Validate(), "A changed voice path reused the old key");
            Console.WriteLine("Voice path witness: a silent path change invalidates a held capture and re-describes the scene.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed class Snapshot { public TimelineSnapshot[] Timelines { get; set; } = []; }
    private sealed class TimelineSnapshot { public IItem[] Items { get; set; } = []; }
    private sealed class ArraySnapshot { public byte[] Small { get; set; } = []; public byte[] Large { get; set; } = []; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
