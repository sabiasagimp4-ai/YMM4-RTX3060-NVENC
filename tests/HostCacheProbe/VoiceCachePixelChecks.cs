using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class VoiceCachePixelChecks
{
    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 321; timeline.VideoInfo.Height = 181; timeline.VideoInfo.FPS = 30;
        var character = VoiceDescriptionMeasurements.Character("voice-hash-pixel");
        byte[] audio = VoiceDescriptionMeasurements.VoiceCache();
        var voices = Enumerable.Range(0, 4).Select(i => new VoiceItem(character)
        {
            Frame = i * 90, Length = 90, Layer = 1, Serif = "voice " + i, Font = "Arial",
            JimakuVisibility = JimakuVisibility.Custom, VoiceCache = (byte[])audio.Clone(),
        }).ToArray();
        foreach (var voice in voices)
        {
            voice.JimakuX.SetFirst(-12.25); voice.JimakuY.SetFirst(8.75);
            voice.FontColor = System.Windows.Media.Colors.White;
        }
        timeline.Items = timeline.Items.AddRange(voices);
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        var dc = context.DeviceContext;
        var view = new TimelineFrameCache.PreviewViewport(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            scene.ID, timeline.ID, Stopwatch.GetTimestamp(), false);
        string root = Path.Combine(Path.GetTempPath(), "ymm-voice-pixels-" + Guid.NewGuid().ToString("N"));
        string audioFile = root + "-voice.wav";
        using (var input = new MemoryStream(audio))
        using (var compressed = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress))
        using (var output = File.Create(audioFile)) compressed.CopyTo(output);
        var voicePath = typeof(VoiceItem).GetField("customVoiceFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var voice in voices) voicePath.SetValue(voice, audioFile);
        using var store = new FrameCacheStore(root, 64L << 20, 0);
        var oldStore = TimelineFrameCache.StoreIfCreated;
        using var player = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        using var tracker = new KeyDependencyTracker(scene);
        int[] frames = [0, 1, 89, 90, 179, 180, 269, 270, 359, 360];
        try
        {
            TimelineFrameCache.UseStore(store);
            TimelineFrameCache.TestViewport = source => ReferenceEquals(source, player) ? view : null;
            TimelineFrameCache.Enabled = false;
            var baseline = new Dictionary<int, byte[]>();
            foreach (int frame in frames)
            {
                player.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
                baseline[frame] = TimelineFrameCache.CapturePreview(dc, player.Output, view)!;
            }
            Check(baseline[0].Where((value, i) => value != baseline[360][i]).Any(), "The voice pixel fixture drew no caption");
            // The player owns another tracker. Its first encounter with the WAV deliberately renders normally
            // while fingerprinting runs; keying the idle tracker above cannot make that asynchronous pass finish.
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                player.Update(TimeSpan.Zero, TimelineSourceUsage.Paused);
                long previousHits = TimelineFrameCache.Hits;
                player.Update(TimeSpan.Zero, TimelineSourceUsage.Paused);
                return TimelineFrameCache.Hits > previousHits;
            }, TimeSpan.FromSeconds(20)), "The player never finished verifying the voice WAV: " + TimelineFrameCache.Status);
            Check(SpinWait.SpinUntil(() =>
            {
                if (!tracker.TryCapture(0, out var capture, out _)) return false;
                capture!.Dispose(); return true;
            }, TimeSpan.FromSeconds(20)), "The live voice scene did not finish keying");
            Check(tracker.TryCapture(0, out var initial, out var reason), reason);
            IdleFramePreRenderer.BatchRenderer batch;
            using (initial) batch = new(tracker, initial!.Model);
            using (batch)
            {
                var copies = batch.CloneScene.Timeline.Items.OfType<VoiceItem>().ToArray();
                for (int i = 0; i < voices.Length; ++i)
                    Check(ReferenceEquals(copies[i].VoiceCache, voices[i].VoiceCache) && copies[i].FilePath == voices[i].FilePath,
                        "The idle clone did not receive the verified voice cache and its actual WAV path");
                TimelineFrameCache.Enabled = true;
                TimelineFrameCache.Clear();
                foreach (int frame in frames)
                {
                    var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, frame, view,
                        () => true, CancellationToken.None, out reason);
                    Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered, $"Idle voice frame {frame}: {result}: {reason}");
                }
                long hits = TimelineFrameCache.Hits;
                foreach (int frame in frames)
                {
                    player.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
                    Check(TimelineFrameCache.CapturePreview(dc, player.Output, view)!.SequenceEqual(baseline[frame]),
                        $"Cached voice pixels differ at frame {frame}");
                }
                Check(TimelineFrameCache.Hits - hits == frames.Length,
                    $"The player did not reuse every idle voice frame ({TimelineFrameCache.Hits - hits}/{frames.Length}): {TimelineFrameCache.Status}");
            }
            Console.WriteLine($"Voice cache: verified clone payloads, matching frame keys, {frames.Length} idle frames and exact cached-caption pixels passed.");
        }
        finally
        {
            TimelineFrameCache.TestViewport = null;
            TimelineFrameCache.Enabled = false;
            player.Dispose();
            if (oldStore is not null) TimelineFrameCache.UseStore(oldStore);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { File.Delete(audioFile); } catch (IOException) { }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
