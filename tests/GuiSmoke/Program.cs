using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Test-only: writes the project the GUI smoke test opens in YMM4 (tools/ci/gui-smoke.ps1): two shapes, a text and
// a Community number shape over 20 seconds at 1280x720 / 30 fps. Uses YMM4's own model and serializer; no application is started.
//   dotnet run --project tests/GuiSmoke -- <YMM4 dir> <out.ymmp>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string hostDir = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(hostDir, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return args.Length >= 4 && args[2] == "--stress"
            ? WriteStress(Path.GetFullPath(args[1]), Path.GetFullPath(args[3])) : Write(Path.GetFullPath(args[1]));
    }

    private static int Write(string output)
    {
        // The plugin loader's static constructor would load plugins from this executable's folder.
        var loader = typeof(PluginAssemblyLoader);
        new Harmony("ymm.tests.gui-smoke").Patch(loader.TypeInitializer!, prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        AccessTools.StaticFieldRefAccess<IEnumerable<Assembly>>(AccessTools.Field(loader, "<Assemblies>k__BackingField"))() =
            new[] { typeof(Scene).Assembly, typeof(CacheProvider).Assembly };
        foreach (var name in new[] { "<IncompatiblePluginAssemblies>k__BackingField", "loadFailures" })
            if (AccessTools.Field(loader, name) is { } field)
                AccessTools.StaticFieldRefAccess<object>(field)() ??= Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GetGenericArguments()))!;

        var timeline = new Timeline { Name = "gui-smoke" };
        timeline.VideoInfo.Width = 1280;
        timeline.VideoInfo.Height = 720;
        timeline.VideoInfo.FPS = 30;
        // 20 s: the idle pre-renderer covers 10 s from the playhead, so normal playback near the end renders new frames.
        var first = new ShapeItem { Frame = 0, Length = 600, Layer = 0 };
        first.X.SetFirstValue(-250);
        var second = new ShapeItem { Frame = 150, Length = 150, Layer = 1 };
        second.X.SetFirstValue(250);
        var text = new TextItem { Frame = 30, Length = 540, Layer = 2, Text = "cache smoke", Font = "Arial" };
        text.Y.SetFirstValue(200);
        timeline.Items = timeline.Items.Add(first).Add(second).Add(text);
        // A shape from the Community plugin YMM4 ships (code the cache does not read): only 2 s to 3 s render normally,
        // and the idle pre-renderer reads ahead past it.
        string communityFile = Path.Combine(Path.GetDirectoryName(typeof(Scene).Assembly.Location)!, "YukkuriMovieMaker.Plugin.Community.dll");
        if (File.Exists(communityFile)
            && Assembly.LoadFrom(communityFile).GetType("YukkuriMovieMaker.Plugin.Community.Shape.NumberText.NumberText") is { } numberType
            && Activator.CreateInstance(numberType, nonPublic: true) is YukkuriMovieMaker.Plugin.Shape.IShapePlugin numberShape)
        {
            var number = new ShapeItem { Frame = 60, Length = 30, Layer = 3, ShapeType2 = numberType, ShapeParameter = numberShape.CreateShapeParameter(null) };
            number.Y.SetFirstValue(-200);
            timeline.Items = timeline.Items.Add(number);
        }
        else Console.WriteLine("No Community number shape in this YMM4; the project has no plugin item");
        // A Community effect whose code was read (KnownCode): the second shape's frames stay cached.
        if (File.Exists(communityFile)
            && Assembly.LoadFrom(communityFile).GetType("YukkuriMovieMaker.Plugin.Community.Effect.Video.Bloom.BloomEffect") is { } bloomType
            && Activator.CreateInstance(bloomType, nonPublic: true) is YukkuriMovieMaker.Plugin.Effects.IVideoEffect bloom)
            second.VideoEffects = second.VideoEffects.Add(bloom);
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var project = new YukkuriMovieMaker.Project.Project(0, scenes, output, string.Empty, new Dictionary<string, SerializableToolState>());
        YukkuriMovieMaker.Json.Json.Save(project, output);
        Console.WriteLine($"Wrote {output}: {timeline.Items.Count} items, {timeline.Length} frames");
        return 0;
    }

    private static bool Skip() => false;

    // 30 s, 12 simultaneously decoded Full-HD streams, 120 video clips, 300 independently animated texts.
    private static int WriteStress(string output, string assets)
    {
        // Reuse the bootstrap, then replace its small timeline with the stress fixture.
        Write(output);
        var timeline = new Timeline { Name = "cache-stress-30s" };
        timeline.VideoInfo.Width = 1920; timeline.VideoInfo.Height = 1080; timeline.VideoInfo.FPS = 30;
        for (int track = 0; track < 12; track++)
        {
            string path = Path.Combine(assets, $"video-{track:00}.mp4");
            if (!File.Exists(path)) throw new FileNotFoundException("Stress video missing", path);
            for (int segment = 0; segment < 10; segment++)
            {
                var video = new VideoItem { FilePath = path, Frame = segment * 90, Length = 90, Layer = track };
                video.X.SetFirstValue((track % 4 - 1.5) * 480);
                video.Y.SetFirstValue((track / 4 - 1) * 270);
                video.Zoom.SetFirstValue(25);
                video.Opacity.SetFirstValue(90);
                timeline.Items = timeline.Items.Add(video);
            }
        }
        for (int row = 0; row < 10; row++) for (int second = 0; second < 30; second++)
        {
            var text = new TextItem { Frame = second * 30, Length = 30, Layer = 12 + row,
                Text = $"STRESS {row:00} / {second:00}s  動画と文字のキャッシュ検証 0123456789", Font = "Arial" };
            text.X.SetFirstValue((row % 2 == 0 ? -1 : 1) * (80 + second * 3));
            text.Y.SetFirstValue(-440 + row * 95);
            text.FontSize.SetFirstValue(32 + row % 3 * 4);
            if (row % 3 == 0) text.VideoEffects = text.VideoEffects.Add(new YukkuriMovieMaker.Project.Effects.GaussianBlurEffect());
            timeline.Items = timeline.Items.Add(text);
        }
        string audioFile = Path.Combine(assets, "clock.wav");
        if (!File.Exists(audioFile)) throw new FileNotFoundException("Audio clock missing", audioFile);
        timeline.Items = timeline.Items.Add(new AudioItem { FilePath = audioFile, Frame = 0, Length = 900, Layer = 22 });
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        var project = new YukkuriMovieMaker.Project.Project(0, scenes, output, string.Empty, new Dictionary<string, SerializableToolState>());
        YukkuriMovieMaker.Json.Json.Save(project, output);
        // User artifact uses relative media paths; the GUI copy keeps absolute paths for reliable host lookup.
        string portable = Path.Combine(Path.GetDirectoryName(output)!, "stress-30s-portable.ymmp");
        foreach (var item in timeline.Items.OfType<VideoItem>()) item.FilePath = "assets/" + Path.GetFileName(item.FilePath);
        foreach (var item in timeline.Items.OfType<AudioItem>()) item.FilePath = "assets/" + Path.GetFileName(item.FilePath);
        YukkuriMovieMaker.Json.Json.Save(new YukkuriMovieMaker.Project.Project(0, scenes, portable, string.Empty, new Dictionary<string, SerializableToolState>()), portable);
        var manifest = new { DurationSeconds = 30, Width = 1920, Height = 1080, FPS = 30, Frames = timeline.Length,
            VideoItems = 120, UniqueVideoFiles = 12, SimultaneousVideos = 12, TextItems = 300, SimultaneousTexts = 10, AudioItems = 1,
            Host = typeof(Scene).Assembly.GetName().Version?.ToString(), Files = Directory.GetFiles(assets).Select(path => new
            { Name = Path.GetFileName(path), Bytes = new FileInfo(path).Length, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }).ToArray() };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "stress-fixture.json"), System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (timeline.Length != 900 || timeline.Items.Count != 421) throw new InvalidDataException("Stress fixture shape changed");
        Console.WriteLine($"STRESS-FIXTURE {output}: 120 videos + 300 texts + 1 audio / 900 frames / 1920x1080 / 30 fps");
        return 0;
    }
}
