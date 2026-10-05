using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class VoiceDescriptionMeasurements
{
    internal static void Run()
    {
        var field = typeof(YukkuriMovieMaker.Json.Json).GetField("settings", BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.GetValue(null) is not JsonSerializerSettings settings)
            throw new InvalidOperationException("The host description serializer settings are unavailable");
        var copy = new JsonSerializerSettings(settings);
        var sample = new { Value = 0.00049, Bytes = new byte[] { 1, 2, 3 }, Item = (IItem)new ShapeItem() };
        if (YukkuriMovieMaker.Json.Json.GetJsonText(sample) != YukkuriMovieMaker.Json.Json.GetJsonText(sample, copy))
            throw new InvalidOperationException("Copying the host settings changed its JSON");
        Console.WriteLine($"VOICE_SETTINGS: private settings can be copied; converters={copy.Converters.Count}; ordinary JSON is identical");
        byte[] cache = VoiceCache();
        var readers = FrameCacheKey.CaptureSourceReaderTypes();
        foreach (int count in new[] { 50, 200, 500 })
        {
            var timeline = new Timeline();
            var scenes = new Scenes(false); scenes.AddScene(timeline);
            var character = Character("voice-description");
            timeline.Items = timeline.Items.AddRange(Enumerable.Range(0, count).Select(index => new VoiceItem(character)
            {
                Frame = index * 90, Length = 90, Layer = 1, Serif = "voice " + index, Font = "Arial",
                JimakuVisibility = JimakuVisibility.Custom,
                VoiceCache = (byte[])cache.Clone(),
            }));
            var scene = new Scene(timeline, scenes, []);
            // Warm the serializer, font resolution and dependency split before recording two separate observations.
            FrameCacheKey.TryDescribe(scene, readers, out _, out _, out _, out _);
            for (int repeat = 1; repeat <= 2; ++repeat)
            {
                var clock = Stopwatch.StartNew();
                bool eligible = FrameCacheKey.TryDescribe(scene, readers, out string model, out _, out var frames, out string reason);
                clock.Stop();
                if (eligible && !frames!.For(0).Cacheable)
                    throw new InvalidOperationException("The voice benchmark fixture itself bypassed despite a bounded description");
                double? recovery = null;
                if (eligible && count == 50)
                {
                    using var tracker = new KeyDependencyTracker(scene);
                    Wait(tracker);
                    ((VoiceItem)timeline.Items[0]).Serif += ".";
                    var edit = Stopwatch.StartNew();
                    Wait(tracker, settle: true);
                    recovery = edit.Elapsed.TotalMilliseconds;
                }
                Console.WriteLine("SPEEDUP1 " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    count, repeat, eligible, description_ms = clock.Elapsed.TotalMilliseconds,
                    chars = model.Length, voice_bytes = (long)cache.Length * count,
                    recovery_ms = recovery, reason,
                }));
            }
        }
    }

    internal static Character Character(string name) => new()
    {
        Name = name, Font = "Arial",
        Voice = new YukkuriMovieMaker.Plugin.Voice.VoiceDescription(YukkuriMovieMaker.Plugin.PluginLoader.VoicePlugins
            .SelectMany(plugin => plugin.Voices).First(speaker => speaker.API == "None")),
    };

    private static void Wait(KeyDependencyTracker tracker, bool settle = false)
    {
        string reason = "";
        if (!SpinWait.SpinUntil(() =>
        {
            if (!tracker.TryCapture(0, out var capture, out reason, settle)) return false;
            using (capture) return capture!.Validate();
        }, TimeSpan.FromSeconds(30))) throw new TimeoutException("Voice description did not recover: " + reason);
    }

    internal static byte[] VoiceCache()
    {
        // Three seconds, mono PCM16, 24 kHz. Noise avoids giving the serializer an unrealistically tiny silence WAV.
        using var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true))
        {
            const int samples = 24000 * 3;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            var random = new Random(104);
            for (int i = 0; i < samples; ++i) writer.Write((short)random.Next(-4096, 4096));
        }
        using var output = new MemoryStream();
        using (var compressed = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressed.Write(wav.ToArray());
        return output.ToArray();
    }
}
