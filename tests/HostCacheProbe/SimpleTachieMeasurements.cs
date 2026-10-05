using System.Diagnostics;
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

internal sealed class SimpleTachieFixture : IDisposable
{
    internal const int Fps = 15, Frames = 60 * Fps, Width = 321, Height = 181;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ymm-simple-tachie-" + Guid.NewGuid().ToString("N"));
    internal Scene Scene { get; }
    internal Timeline Timeline { get; } = new();
    internal Character[] Characters { get; }
    internal TachieItem[] Tachies { get; }
    internal string[] Images { get; }
    internal ITachiePlugin Plugin { get; }
    internal SimpleTachieFixture(bool hideWithoutVoice = false)
    {
        Directory.CreateDirectory(Root);
        Plugin = PluginLoader.TachiePlugins.Single(p => p.GetType().FullName == "YukkuriMovieMaker.Plugin.Tachie.SimpleTachie.SimpleTachiePlugin");
        Images = Enumerable.Range(0, 2).Select(i => Path.Combine(Root, (i == 0 ? "face-red.png" : "face-blue.png"))).ToArray();
        for (int i = 0; i < Images.Length; i++)
            File.WriteAllBytes(Images[i], FramePixelChecks.Png(80, 100, (x, y) =>
                ((byte)(i == 0 ? 230 : 20), (byte)(40 + x), (byte)(i == 0 ? 20 : 220), (byte)(x < 8 || y < 8 ? 90 : 255))));
        byte[] cachedAudio = VoiceDescriptionMeasurements.VoiceCache();
        string audioPath = Path.Combine(Root, "voice.wav");
        using (var input = new MemoryStream(cachedAudio))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        using (var output = File.Create(audioPath)) brotli.CopyTo(output);
        Characters = Enumerable.Range(0, 2).Select(i =>
        {
            var character = VoiceDescriptionMeasurements.Character("simple-" + i);
            character.IsJimakuVisible = false;
            // Simple tachie uses only the no-voice sentinel. Zero smoothing avoids starting unused envelope work.
            character.MouseSmooth = 0;
            character.TachieType = Plugin.GetType();
            character.TachieCharacterParameter = Plugin.CreateCharacterParameter();
            Set(character.TachieCharacterParameter, "Directory", Root);
            character.TachieDefaultItemParameter = Plugin.CreateItemParameter();
            Set(character.TachieDefaultItemParameter, "DefaultFace", Images[i]);
            Set(character.TachieDefaultItemParameter, "IsHiddenWhenNoSpeech", hideWithoutVoice);
            character.TachieDefaultFaceParameter = Plugin.CreateFaceParameter();
            return character;
        }).ToArray();
        Tachies = Characters.Select((character, i) =>
        {
            var item = new TachieItem(character) { Frame = 0, Length = Frames, Layer = i };
            item.X.SetFirst(i == 0 ? -55.25 : 55.25); item.Y.SetFirst(0);
            return item;
        }).ToArray();
        Timeline.VideoInfo.Width = Width; Timeline.VideoInfo.Height = Height; Timeline.VideoInfo.FPS = Fps;
        Timeline.Items = Timeline.Items.AddRange(Tachies);
        var pathField = typeof(VoiceItem).GetField("customVoiceFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int i = 0; i < 20; i++)
        {
            var voice = new VoiceItem(Characters[i % 2])
            {
                Frame = i * 3 * Fps, Length = 3 * Fps, Layer = 10 + i % 2,
                Serif = "voice " + i, VoiceCache = (byte[])cachedAudio.Clone(),
            };
            pathField.SetValue(voice, audioPath);
            Timeline.Items = Timeline.Items.Add(voice);
        }
        Timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = HostCompat.NewScenes(); scenes.AddScene(Timeline);
        Scene = new(Timeline, scenes, []);
    }
    internal static void Set(object parameter, string property, object value) => parameter.GetType().GetProperty(property)!.SetValue(parameter, value);
    public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
}

internal static class SimpleTachieMeasurements
{
    internal static void Run(Assembly host)
    {
        var harmony = new Harmony("ymm.tests.simple-tachie-measurements");
        harmony.Patch(typeof(PluginAssemblyLoader).TypeInitializer!, prefix: new HarmonyMethod(typeof(SimpleTachieMeasurements), nameof(SkipLoader)));
        ProbeLoader.Stub(ProbeLoader.Assemblies(host));
        using var fixture = new SimpleTachieFixture();
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var target = dc.CreateBitmap(new SizeI(SimpleTachieFixture.Width, SimpleTachieFixture.Height), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, fixture.Scene, null], null)!;
        using var store = new FrameCacheStore(Path.Combine(fixture.Root, "store"), 256L << 20, 0);
        var view = new TimelineFrameCache.PreviewViewport(SimpleTachieFixture.Width, SimpleTachieFixture.Height, Matrix3x2.Identity,
            new Vector2(SimpleTachieFixture.Width / 2f, SimpleTachieFixture.Height / 2f), 96, 96, target.PixelFormat,
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            fixture.Scene.ID, fixture.Timeline.ID, Stopwatch.GetTimestamp(), true);
        try
        {
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
                }, TimeSpan.FromSeconds(30)), "Simple benchmark did not finish keying: " + TimelineFrameCache.Status);
            }
            void Measure(string mode, bool enabled, int repeat)
            {
                TimelineFrameCache.Enabled = enabled;
                long hits = TimelineFrameCache.Hits, gpu = TimelineFrameCache.GpuHits;
                var clock = Stopwatch.StartNew();
                for (int frame = 0; frame < SimpleTachieFixture.Frames; frame++) { Update(frame); Draw(); }
                double elapsed = clock.Elapsed.TotalMilliseconds;
                TimelineFrameCache.CompletePendingStore(source);
                Console.WriteLine("SPEEDUP2A " + JsonSerializer.Serialize(new
                {
                    mode, repeat, frames = SimpleTachieFixture.Frames, fps = SimpleTachieFixture.Fps,
                    project_seconds = 60, tachies = 2, voices = 20, cacheable,
                    ms_per_frame = elapsed / SimpleTachieFixture.Frames,
                    cache_hits = TimelineFrameCache.Hits - hits, gpu_hits = TimelineFrameCache.GpuHits - gpu,
                }));
            }
            for (int repeat = 1; repeat <= 2; repeat++)
            {
                Measure("off", false, repeat);
                TimelineFrameCache.Enabled = true; TimelineFrameCache.Clear();
                for (int frame = 0; frame < SimpleTachieFixture.Frames; frame++)
                { Update(frame); Draw(); TimelineFrameCache.CompletePendingStore(source); }
                Measure("second-play", true, repeat);
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = false;
            source.Dispose(); context.CacheProvider.Clear();
            FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(harmony.Id);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "Simple benchmark leaked GPU resources");
    }
    private static bool SkipLoader() => false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
