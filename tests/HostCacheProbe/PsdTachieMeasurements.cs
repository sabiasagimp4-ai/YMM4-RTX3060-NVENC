using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal sealed class PsdTachieFixture : IDisposable
{
    internal const int Fps = 15, Frames = 60 * Fps, Width = 321, Height = 181;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ymm-psd-tachie-" + Guid.NewGuid().ToString("N"));
    internal Scene Scene { get; }
    internal Timeline Timeline { get; } = new();
    internal Character[] Characters { get; }
    internal TachieItem[] Tachies { get; }
    internal string[] Images { get; }
    internal ITachiePlugin Plugin { get; }
    internal PsdTachieFixture(bool hideWithoutVoice = false, bool large = false)
    {
        Directory.CreateDirectory(Root);
        Plugin = PluginLoader.TachiePlugins.Single(p => p.GetType().FullName == "YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachiePlugin");
        Images = Enumerable.Range(0, 2).Select(i => Path.Combine(Root, "face-" + i + ".psd")).ToArray();
        for (int i = 0; i < Images.Length; i++)
        {
            File.WriteAllBytes(Images[i], OwnPsd(i == 0, large ? 1920 : 80, large ? 1920 : 100));
            // Own sidecar, including a substantial shared settings graph for the large fixture.
            string eyes = string.Join(",", Enumerable.Repeat("{\"Layers\":[\"i1\",\"i2\",\"i3\"],\"Offset\":0,\"Interval\":0}", large ? 200 : 1));
            string mouths = string.Join(",", Enumerable.Repeat("{\"Layers\":[\"i4\",\"i5\",\"i6\"]}", large ? 100 : 1));
            File.WriteAllText(Path.Combine(Root, "face-" + i + "-ymm.json"), "{\"EyeAnimations\":[" + eyes + "],\"MouthAnimations\":[" + mouths + "]}");
        }
        byte[] cachedAudio = VoiceDescriptionMeasurements.VoiceCache();
        string audioPath = Path.Combine(Root, "voice.wav");
        using (var input = new MemoryStream(cachedAudio))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        using (var output = File.Create(audioPath)) brotli.CopyTo(output);
        Characters = Enumerable.Range(0, 2).Select(i =>
        {
            var character = VoiceDescriptionMeasurements.Character("psd-" + i);
            character.IsJimakuVisible = false; character.MouseSmooth = 4;
            character.TachieType = Plugin.GetType();
            character.TachieCharacterParameter = Plugin.CreateCharacterParameter();
            Set(character.TachieCharacterParameter, "FilePath", Images[i]);
            character.TachieDefaultItemParameter = Plugin.CreateItemParameter();
            Set(character.TachieDefaultItemParameter, "FilePath", Images[i]);
            Set(character.TachieDefaultItemParameter, "EnableLayers", ImmutableList.Create("i1", "i4", "i7"));
            Set(character.TachieDefaultItemParameter, "IsHiddenWhenNoSpeech", hideWithoutVoice);
            character.TachieDefaultFaceParameter = Plugin.CreateFaceParameter();
            return character;
        }).ToArray();
        Tachies = Characters.Select((character, i) =>
        {
            var item = new TachieItem(character) { Frame = 0, Length = Frames, Layer = i };
            if (large) item.Zoom.SetFirstValue(100.0 * 80 / 1920);
            item.X.SetFirstValue(i == 0 ? -55.25 : 55.25); item.Y.SetFirstValue(0);
            return item;
        }).ToArray();
        Timeline.VideoInfo.Width = Width; Timeline.VideoInfo.Height = Height; Timeline.VideoInfo.FPS = Fps;
        Timeline.Items = Timeline.Items.AddRange(Tachies);
        var pathField = typeof(VoiceItem).GetField("customVoiceFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int i = 0; i < 20; i++)
        {
            var voice = new VoiceItem(Characters[i % 2]) { Frame = i * 3 * Fps, Length = 3 * Fps, Layer = 10 + i % 2,
                Serif = "voice " + i, VoiceCache = (byte[])cachedAudio.Clone() };
            pathField.SetValue(voice, audioPath); Timeline.Items = Timeline.Items.Add(voice);
        }
        Timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(Timeline); Scene = new(Timeline, scenes, []);
    }
    internal static void Set(object parameter, string property, object value) => parameter.GetType().GetProperty(property)!.SetValue(parameter, value);

    // Original fixture writer: Adobe 8BPS v1, RGB, raw RGBA planes, seven layers with explicit lyid metadata.
    // No host code or host binary is embedded. All pixels are generated here.
    internal static byte[] OwnPsd(bool red, int width = 80, int height = 100)
    {
        int pixels = checked(width * height);
        using var records = new MemoryStream(); using var planes = new MemoryStream();
        using var rw = new BinaryWriter(records); using var pw = new BinaryWriter(planes);
        U16(rw, 7);
        string[] names = ["eye.closed", "eye.half", "eye.open", "mouth.closed", "mouth.half", "mouth.open", "body"];
        // The host composites records in this order. Put the own opaque body behind all facial layers.
        foreach (int layer in new[] { 6, 0, 1, 2, 3, 4, 5 })
        {
            U32(rw, 0); U32(rw, 0); U32(rw, height); U32(rw, width); U16(rw, 4);
            foreach (int channel in new[] { 0, 1, 2, -1 }) { U16(rw, unchecked((ushort)channel)); U32(rw, pixels + 2); }
            rw.Write(Encoding.ASCII.GetBytes("8BIMnorm")); rw.Write((byte)255); rw.Write((byte)0);
            rw.Write((byte)(layer is 0 or 3 or 6 ? 0 : 2)); rw.Write((byte)0);
            using var extra = new MemoryStream(); using var ew = new BinaryWriter(extra);
            U32(ew, 0); U32(ew, 0);
            byte[] name = Encoding.ASCII.GetBytes(names[layer]); ew.Write((byte)name.Length); ew.Write(name);
            while (extra.Length % 4 != 0) ew.Write((byte)0);
            ew.Write(Encoding.ASCII.GetBytes("8BIMlyid")); U32(ew, 4); U32(ew, layer + 1);
            U32(rw, checked((int)extra.Length)); rw.Write(extra.ToArray());
            foreach (int channel in new[] { 0, 1, 2, -1 })
            {
                U16(pw, 0);
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int sx = x * 80 / width, sy = y * 100 / height;
                    bool body = layer == 6;
                    bool visible = body || (layer < 3 ? sx >= 20 && sx < 60 && sy >= 24 && sy < 25 + layer * 4
                        : sx >= 28 && sx < 52 && sy >= 64 && sy < 65 + (layer - 3) * 4);
                    byte value = channel == -1 ? (byte)(visible ? 255 : 0) : body
                        ? channel == 0 ? (byte)(red ? 220 : 30) : channel == 1 ? (byte)60 : (byte)(red ? 30 : 220)
                        : (byte)20;
                    pw.Write(value);
                }
            }
        }
        using var layerData = new MemoryStream(); using var lw = new BinaryWriter(layerData);
        int layerLength = checked((int)(records.Length + planes.Length)); U32(lw, layerLength);
        lw.Write(records.ToArray()); lw.Write(planes.ToArray()); U32(lw, 0);
        using var file = new MemoryStream(); using var writer = new BinaryWriter(file);
        writer.Write(Encoding.ASCII.GetBytes("8BPS")); U16(writer, 1); writer.Write(new byte[6]);
        U16(writer, 3); U32(writer, height); U32(writer, width); U16(writer, 8); U16(writer, 3);
        U32(writer, 0); U32(writer, 0); U32(writer, checked((int)layerData.Length)); writer.Write(layerData.ToArray());
        U16(writer, 0);
        foreach (byte channel in new byte[] { (byte)(red ? 220 : 30), 60, (byte)(red ? 30 : 220) })
            writer.Write(Enumerable.Repeat(channel, pixels).ToArray());
        return file.ToArray();
    }
    private static void U16(BinaryWriter writer, int value) { Span<byte> data = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, checked((ushort)value)); writer.Write(data); }
    private static void U32(BinaryWriter writer, int value) { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, value); writer.Write(data); }
    public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
}

internal static class PsdTachieMeasurements
{
    internal static void Run(Assembly host, bool large = false)
    {
        for (int repeat = 1; repeat <= 2; repeat++)
        {
            int sample = repeat;
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try { RunCase(host, sample, large); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "PSD tachie measurement" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(large ? 180 : 30)), "PSD tachie measurement exceeded 30 seconds");
            if (failure is not null) throw new InvalidOperationException("PSD tachie measurement", failure);
        }
    }
    private static void RunCase(Assembly host, int repeat, bool large)
    {
        var harmony = new Harmony("ymm.tests.animation-tachie-measurements");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(PsdTachieMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host).Append(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(host.Location)!,
            "YukkuriMovieMaker.Plugin.Tachie.Psd.dll"))));
        using var fixture = new PsdTachieFixture(large: large);
        int frames = large ? 120 : PsdTachieFixture.Frames;
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var target = dc.CreateBitmap(new SizeI(PsdTachieFixture.Width, PsdTachieFixture.Height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, fixture.Scene, null], null)!;
        using var store = new FrameCacheStore(Path.Combine(fixture.Root, "store"), 256L << 20, 0);
        var view = new TimelineFrameCache.PreviewViewport(PsdTachieFixture.Width, PsdTachieFixture.Height, Matrix3x2.Identity,
            new Vector2(PsdTachieFixture.Width / 2f, PsdTachieFixture.Height / 2f), 96, 96, target.PixelFormat,
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            fixture.Scene.ID, fixture.Timeline.ID, Stopwatch.GetTimestamp(), true);
        bool oldEnabled = TimelineFrameCache.Enabled;
        bool oldGpu = TimelineFrameCache.GpuRetentionEnabled;
        try
        {
            TimelineFrameCache.GpuRetentionEnabled = false;
            Check(TimelineFrameCache.TryInstall(host, harmony, out string reason), reason);
            TimelineFrameCache.UseStore(store);
            TimelineFrameCache.TestViewport = value => ReferenceEquals(source, value) ? view : null;
            void Update(int frame) => source.Update(fixture.Timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Playing);
            void Draw()
            {
                using var old = dc.Target;
                var transform = dc.Transform;
                try
                {
                    dc.Target = target; dc.Transform = view.Transform;
                    dc.BeginDraw(); dc.Clear(new Color4(0, 0, 0, 1)); dc.DrawImage(source.Output, view.TargetOffset); dc.EndDraw().CheckError();
                }
                finally { dc.Target = old; dc.Transform = transform; }
            }
            TimelineFrameCache.Enabled = false;
            for (int frame = 0; frame < 60; frame++) { Update(frame); Draw(); }
            bool cacheable = FrameCacheKey.TryDescribe(fixture.Scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out _, out var deps, out _)
                && deps!.For(0).Cacheable;
            if (cacheable)
            {
                TimelineFrameCache.Enabled = true;
                Check(SpinWait.SpinUntil(() =>
                {
                    Update(0); TimelineFrameCache.CompletePendingStore(source);
                    long hits = TimelineFrameCache.Hits; Update(0);
                    return TimelineFrameCache.Hits > hits;
                }, TimeSpan.FromSeconds(30)), "PSD benchmark did not finish keying: " + TimelineFrameCache.Status);
            }
            void Measure(string mode, bool enabled, int repeat)
            {
                TimelineFrameCache.Enabled = enabled;
                long hits = TimelineFrameCache.Hits, gpu = TimelineFrameCache.GpuHits;
                var clock = Stopwatch.StartNew();
                for (int frame = 0; frame < frames; frame++) { Update(frame); Draw(); }
                double elapsed = clock.Elapsed.TotalMilliseconds;
                TimelineFrameCache.CompletePendingStore(source);
                Console.WriteLine("SPEEDUP2C " + JsonSerializer.Serialize(new
                {
                    mode, repeat, frames = frames, fps = PsdTachieFixture.Fps,
                    project_seconds = (double)frames / PsdTachieFixture.Fps, tachies = 2, voices = 20, cacheable,
                    fixture = large ? "1920-square-7-raw-layers-300-animation-settings" : "small",
                    psd_bytes = new FileInfo(fixture.Images[0]).Length,
                    ms_per_frame = elapsed / frames,
                    cache_hits = TimelineFrameCache.Hits - hits, gpu_hits = TimelineFrameCache.GpuHits - gpu,
                }));
            }
            {
                Measure("off", false, repeat);
                var phase = Stopwatch.StartNew();
                var reference = new byte[frames][];
                for (int frame = 0; frame < reference.Length; frame++)
                {
                    Update(frame);
                    Check(FrameRenderReadiness.WasLastUpdateReady(source, fixture.Timeline.VideoInfo.GetTimeFrom(frame)),
                        "PSD ordinary reference was not completed at frame " + frame);
                    reference[frame] = TimelineFrameCache.CapturePreview(dc, source.Output, view)!;
                    Check(reference[frame].Any(value => value != 0), "PSD reference was empty");
                }
                Check(reference.Any(pixels => !pixels.SequenceEqual(reference[0])), "PSD eye or mouth never changed pixels");
                Console.WriteLine($"SPEEDUP2C_PHASE repeat={repeat}; reference_ms={phase.Elapsed.TotalMilliseconds:R}");
                phase.Restart();
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                for (int frame = 0; frame < frames; frame++)
                { Update(frame); Draw(); TimelineFrameCache.CompletePendingStore(source); }
                Console.WriteLine($"SPEEDUP2C_PHASE repeat={repeat}; cold_warm_ms={phase.Elapsed.TotalMilliseconds:R}");
                Measure("second-play", true, repeat);
                for (int frame = 0; frame < reference.Length; frame++)
                {
                    Update(frame);
                    var actual = TimelineFrameCache.CapturePreview(dc, source.Output, view)!;
                    Check(actual.SequenceEqual(reference[frame]), "PSD pixel mismatch at frame " + frame);
                }
                Console.WriteLine("SPEEDUP2C_PIXELS repeat=" + repeat + "; frames=" + frames + "; exact=true; smoothing=4; psd_eyes=3; psd_mouths=3; default_blink=true");
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = oldEnabled;
            TimelineFrameCache.GpuRetentionEnabled = oldGpu;
            source.Dispose(); context.CacheProvider.Clear();
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "PSD benchmark leaked GPU resources");
    }
    private static bool SkipLoader() => false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
