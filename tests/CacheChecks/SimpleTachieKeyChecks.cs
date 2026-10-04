using System.Reflection;
using System.Reflection.Emit;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class SimpleTachieKeyChecks
{
    internal static void Run()
    {
        var plugin = PluginLoader.TachiePlugins.Single(value => value.GetType().FullName == SimpleTachieDependencies.PluginName);
        Check(SimpleTachieDependencies.Verified(plugin.GetType()), "The bundled simple plugin was not verified");
        string root = Path.Combine(Path.GetTempPath(), "ymm-simple-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] files = [Path.Combine(root, "default.png"), Path.Combine(root, "upper.png"), Path.Combine(root, "voice.png")];
        foreach (string file in files) File.WriteAllBytes(file, [1, 2, 3, 4]);
        var character = VoiceDescriptionMeasurements.Character("simple-key-check");
        character.IsJimakuVisible = false; character.MouseSmooth = 0; character.TachieType = plugin.GetType();
        character.TachieCharacterParameter = plugin.CreateCharacterParameter();
        Set(character.TachieCharacterParameter, "Directory", root);
        character.TachieDefaultItemParameter = plugin.CreateItemParameter(); Set(character.TachieDefaultItemParameter, "DefaultFace", files[0]);
        character.TachieDefaultFaceParameter = plugin.CreateFaceParameter();
        var tachie = new TachieItem(character) { Frame = 0, Length = 90, Layer = 0 };
        var voice = new VoiceItem(character) { Frame = 20, Length = 20, Layer = 2 };
        Set(voice.TachieFaceParameter, "Face", files[2]);
        var face = new TachieFaceItem(character) { Frame = 30, Length = 30, Layer = 3 };
        Set(face.TachieFaceParameter, "Face", files[1]);
        var timeline = new Timeline(); timeline.Items = timeline.Items.Add(tachie).Add(voice).Add(face);
        timeline.RefreshTimelineLengthAndMaxLayer();
        var scenes = new Scenes(false); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        FrameDependencyIndex Describe()
        {
            Check(FrameCacheKey.TryDescribe(scene, FrameCacheKey.CaptureSourceReaderTypes(), out _, out var paths, out var index, out var reason), reason);
            if (SimpleTachieDependencies.Character(character))
                Check(!paths.Contains(root, StringComparer.OrdinalIgnoreCase), "The editor directory became a file dependency");
            return index!;
        }
        var host = typeof(Scene).Assembly;
        var features = HostFeatures.For(host);
        try
        {
            var index = Describe();
            Check(new[] { 0, 25, 35, 45, 65 }.All(frame => index.For(frame).Cacheable), "A valid simple frame was bypassed");
            Check(index.For(0).Files.Contains(files[0]) && !index.For(0).Files.Contains(files[1]), "The default face was not selected");
            Check(index.For(25).Files.Contains(files[2]) && !index.For(25).Files.Contains(files[0]), "The voice face did not override the default");
            Check(index.For(35).Files.Contains(files[1]) && !index.For(35).Files.Contains(files[2]) && !index.For(35).Files.Contains(files[0]),
                "An unused lower face remained a drawing dependency");
            Check(index.For(35).Shown is null, "A plain face was treated as a required decoder sequence image");
            using var tracker = new KeyDependencyTracker(scene);
            string Key(int frame)
            {
                KeyCapture? capture = null;
                Check(SpinWait.SpinUntil(() => tracker.TryCapture(frame, out capture, out _), TimeSpan.FromSeconds(20)), "Simple file key did not become available at " + frame);
                using (capture) { Check(capture!.Validate(), "The simple capture was not current"); return capture.Key; }
            }
            _ = Key(0); _ = Key(25); string upperKey = Key(35);
            File.WriteAllBytes(files[2], [4, 3, 2, 1]); File.SetLastWriteTimeUtc(files[2], DateTime.UtcNow.AddSeconds(2));
            Check(!tracker.TryCapture(25, out var stale, out _), "The overwritten displayed voice face was accepted"); stale?.Dispose();
            Check(Key(35) == upperKey, "An overwritten unused face disabled the upper face's frame");
            Set(tachie.TachieItemParameter, "IsHiddenWhenNoSpeech", true);
            index = Describe();
            Check(!index.For(0).Files.Contains(files[0]) && !index.For(45).Files.Contains(files[1])
                && index.For(35).Files.Contains(files[1]), "The no-voice visibility sentinel selected the wrong face");
            timeline.LayerSettings.IsVisibles[3] = false;
            index = Describe();
            Check(index.For(35).Files.Contains(files[2]) && !index.For(35).Files.Contains(files[1]), "A hidden face layer still won selection");
            timeline.LayerSettings.IsVisibles[3] = true;
            var original = voice.TachieFaceParameter;
            voice.TachieFaceParameter = new ForeignFace();
            Check(!Describe().For(25).Cacheable, "An unknown face parameter was admitted");
            voice.TachieFaceParameter = original;
            HostFeatures.Decide(host, features with { SimpleTachie = false });
            Check(!Describe().For(0).Cacheable, "A failed simple contract did not bypass");
            HostFeatures.Decide(host, features);
            var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(SimpleTachieDependencies.AssemblyName), AssemblyBuilderAccess.Run);
            var futureType = dynamicAssembly.DefineDynamicModule("future").DefineType(SimpleTachieDependencies.PluginName, TypeAttributes.Public).CreateType()!;
            Check(!SimpleTachieDependencies.Verified(futureType), "An unknown simple plugin MVID was admitted");
            character.TachieType = typeof(SimpleTachieKeyChecks);
            Check(!Describe().For(0).Cacheable, "An external tachie type was admitted");
            character.TachieType = plugin.GetType();
            timeline.Items = timeline.Items.Add(new GroupItem { Frame = 0, Length = 90, Layer = 10 });
            Check(!Describe().For(0).Cacheable, "Unverified grouped tachie time mapping was admitted");
            Console.WriteLine("Simple tachie keys: verified code, default/voice/upper faces, hidden layers/no voice, selective overwrites, unknown parameters/MVID/contracts and groups passed.");
        }
        finally { HostFeatures.Decide(host, features); Directory.Delete(root, recursive: true); }
    }
    private static void Set(object parameter, string property, object value) => parameter.GetType().GetProperty(property)!.SetValue(parameter, value);
    private sealed class ForeignFace : Animatable, ITachieFaceParameter
    {
        protected override IEnumerable<IAnimatable> GetAnimatables() => [];
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
