using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// Audited bundled PSD renderer only. The shared settings are not part of the project JSON;
// plain children do not notify, and a native source may retain an older normalized copy.
internal static class PsdTachieDependencies
{
    internal const string AssemblyName = "YukkuriMovieMaker.Plugin.Tachie.Psd";
    internal const string PluginName = AssemblyName + ".PsdTachiePlugin";
    internal static readonly string SessionResource = "psd-blink-session://" + Guid.NewGuid().ToString("N");
    internal static Func<bool>? ReadinessInstalled { get; set; }
    private static readonly Guid ReadBuild = new("7dc41b6b-858d-4af0-949f-c8c4e7f9dede");
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal sealed record Input(Character Character, string Path, object Settings, string Json);
    private sealed record Witness(Input[] Inputs);
    private sealed record LoadedContent(string Hash);
    private static readonly ConditionalWeakTable<object, LoadedContent> loadedFiles = new();
    private sealed record NormalizedWitness(WeakReference<object> Root, string SharedJson, object Normalized, string ExpectedJson);
    private static readonly ConditionalWeakTable<string, Witness> models = new();
    private static readonly ConditionalWeakTable<object, NormalizedWitness> normalized = new();

    internal static bool Verified(Type? plugin)
    {
        if (plugin?.FullName != PluginName || plugin.Assembly.GetName().Name != AssemblyName
            || plugin.Assembly.ManifestModule.ModuleVersionId != ReadBuild || !HostFeatures.For(typeof(Scene).Assembly).PsdTachie
            || !FrameCacheKey.IsBundledPluginAssembly(AssemblyName, plugin.Assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location))) return false;
        // These dependencies also appear in the PSD contract. The loaded module must be the audited one,
        // including on the read host path where HostFeatures need not evaluate an unread build.
        foreach (var (name, mvid) in new[] {
            ("YukkuriMovieMaker.Plugin.FileSource.Psd", new Guid("277031aa-0de0-415b-a9df-12a390227ec4")),
            ("PsdParser", new Guid("d16c5a72-6eff-48f4-8735-0e5ce6ec73df")) })
        {
            string directory = Path.GetDirectoryName(typeof(Scene).Assembly.Location)!;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
                ?? Assembly.LoadFrom(Path.Combine(directory, name + ".dll"));
            if (assembly.ManifestModule.ModuleVersionId != mvid
                || !string.Equals(Path.GetFullPath(assembly.Location), Path.Combine(directory, name + ".dll"), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    private static bool Parameter(object? value, Type plugin, string name, bool optional = false) => value is null ? optional
        : value.GetType().Assembly == plugin.Assembly && value.GetType().FullName == AssemblyName + "." + name;
    internal static bool Character(Character? value) => value?.TachieType is { } type && Verified(type)
        && Parameter(value.TachieCharacterParameter, type, "PsdTachieCharacterParameter")
        && Parameter(value.TachieDefaultItemParameter, type, "PsdTachieItemParameter")
        && Parameter(value.TachieDefaultFaceParameter, type, "PsdTachieFaceParameter", optional: true);
    private static string? PathOf(Character character) => character.TachieCharacterParameter?.GetType().GetProperty("FilePath")?.GetValue(character.TachieCharacterParameter) as string;
    internal static object Settings(Character character)
    {
        if (!Character(character)) throw new NotSupportedException("Unaudited PSD character");
        return character.TachieType.Assembly.GetType(AssemblyName + ".PsdFileSettings", true)!
            .GetMethod("LoadFromPsdFilePath", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [PathOf(character)])!;
    }
    // Bounded own serialization of the actual shared object, never a new read of its sidecar.
    internal static string Snapshot(object value)
    {
        var assembly = value.GetType().Assembly;
        if (value.GetType().FullName != AssemblyName + ".PsdFileSettings" || assembly.ManifestModule.ModuleVersionId != ReadBuild)
            throw new NotSupportedException("Unaudited PSD settings");
        int remaining = 65536, nodes = 4096;
        void Visit(object? part, int depth)
        {
            if (depth > 5 || --nodes < 0) throw new InvalidDataException("PSD settings exceed the inspection bound");
            if (part is null) return;
            if (part is string text) { if (text.Length > 4096 || (remaining -= text.Length) < 0) throw new InvalidDataException("PSD layer text exceeds the inspection bound"); return; }
            if (part is double number) { if (!double.IsFinite(number)) throw new InvalidDataException("Non-finite PSD animation"); return; }
            if (part is IEnumerable list)
            {
                int count = 0;
                foreach (var child in list) { if (++count > 1024) throw new InvalidDataException("Too many PSD animation layers"); Visit(child, depth + 1); }
                return;
            }
            if (part.GetType().Assembly != assembly || part.GetType().FullName is not (
                AssemblyName + ".PsdEyeAnimation" or AssemblyName + ".PsdMouthAnimation" or AssemblyName + ".PsdVowelMouthAnimation" or AssemblyName + ".PsdPreset"))
                throw new InvalidDataException("Unknown PSD animation value");
            foreach (var property in part.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) Visit(property.GetValue(part), depth + 1);
        }
        foreach (string name in new[] { "EyeAnimations", "MouthAnimations", "MouthVowelAnimations", "Presets" })
            Visit(value.GetType().GetProperty(name)!.GetValue(value), 0);
        string json = JsonConvert.SerializeObject(value, Formatting.None);
        if (json.Length > 262144) throw new InvalidDataException("PSD settings JSON exceeds the inspection bound");
        return json;
    }
    internal static object[] SharedObjects(IEnumerable<Character> characters) => characters.Where(Character).Select(Settings).Distinct().ToArray();
    internal static Input[] Capture(IEnumerable<Character> characters) => characters.Where(Character).Select(character =>
    {
        string path = PathOf(character) ?? throw new InvalidDataException("Missing PSD path");
        object settings = Settings(character); return new Input(character, path, settings, Snapshot(settings));
    }).ToArray();
    internal static string Resource(Input input) => "psd-settings://" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Json)));
    internal static void Bind(string model, Input[] inputs) { if (inputs.Length != 0) models.Add(model, new(inputs)); }
    internal static bool Current(string model)
    {
        if (!models.TryGetValue(model, out var witness)) return true;
        try { return witness.Inputs.All(input => PathOf(input.Character) == input.Path
            && ReferenceEquals(Settings(input.Character), input.Settings) && Snapshot(input.Settings) == input.Json); }
        catch { return false; }
    }
    internal static bool TryFiles(TachieItem item, Timeline timeline, out string[] files)
    {
        files = [];
        var character = item.Character;
        if (!Character(character) || !(ReadinessInstalled?.Invoke() == true) || item.Length <= 0
            || timeline.Items.Any(candidate => candidate is GroupItem)
            || !Parameter(item.TachieItemParameter, character.TachieType, "PsdTachieItemParameter")) return false;
        var faces = timeline.Items.Where(candidate => ReferenceEquals(FrameCacheKey.GetCharacter(candidate), character))
            .Where(candidate => candidate is VoiceItem or TachieFaceItem).ToArray();
        if (faces.GroupBy(face => face.Layer).Any(group => group.Count() > 1 && group.Any(a => group.Any(b =>
            !ReferenceEquals(a, b) && a.Frame < (long)b.Frame + b.Length && b.Frame < (long)a.Frame + a.Length)))) return false;
        foreach (var face in faces)
            if (!Parameter(face is VoiceItem voice ? voice.TachieFaceParameter : ((TachieFaceItem)face).TachieFaceParameter,
                character.TachieType, "PsdTachieFaceParameter", optional: true)) return false;
        string? path = PathOf(character);
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
            || Path.GetExtension(path).ToLowerInvariant() is not (".psd" or ".psb")) return false;
        files = [Path.GetFullPath(path)];
        return true;
    }
    internal static bool ContainsNative(Scene scene) => scene.Timeline.Items.OfType<TachieItem>()
        .Any(item => item.Character?.TachieType?.FullName == PluginName);
    internal static bool SafeSource(object timelineSource, Scene scene, int frame, KeyCapture? capture = null)
    {
        try
        {
            var active = scene.Timeline.Items.OfType<TachieItem>().Where(item => item.Frame <= frame && frame < (long)item.Frame + item.Length
                && item.Character?.TachieType?.FullName == PluginName).ToArray();
            if (active.Length == 0) return true;
            if (!(ReadinessInstalled?.Invoke() == true) || timelineSource.GetType().FullName != "YukkuriMovieMaker.Player.Video.TimelineSource"
                || timelineSource.GetType().GetField("timelineResources", Instance)?.GetValue(timelineSource) is not IDictionary resources) return false;
            foreach (var item in active)
            {
                if (!Character(item.Character) || resources[item] is not { } effected) return false;
                var core = effected.GetType().GetProperty("Source", Instance)?.GetValue(effected);
                if (core?.GetType().FullName != "YukkuriMovieMaker.Player.Video.Items.TachieSource"
                    || !ReferenceEquals(core.GetType().GetField("item", Instance)?.GetValue(core), item)) return false;
                var native = core.GetType().GetField("source", Instance)?.GetValue(core);
                if (native?.GetType().FullName != AssemblyName + ".PsdTachieSource" || native.GetType().Assembly != item.Character.TachieType.Assembly) return false;
                object? Field(string name) => native.GetType().GetField(name, Instance)?.GetValue(native);
                object shared = Settings(item.Character);
                if (!Equals(Field("filePath"), PathOf(item.Character)) || Field("psdFile") is not { } file || Field("psdRoot") is not { } root
                    || !ReferenceEquals(Field("psdFileSettings"), shared) || Field("normalizedPsdFileSettings") is not { } actual
                    || Field("disposedValue") is not false || Field("bitmap") is not ID2D1Bitmap bitmap) return false;
                if (file.GetType().Assembly.ManifestModule.ModuleVersionId != new Guid("d16c5a72-6eff-48f4-8735-0e5ce6ec73df")
                    || root.GetType().Assembly.ManifestModule.ModuleVersionId != new Guid("277031aa-0de0-415b-a9df-12a390227ec4")
                    || file.GetType().GetField("disposedValue", Instance)?.GetValue(file) is not false) return false;
                // The host absorbs CPU compositing errors and substitutes an empty bitmap. That is not a completed PSD.
                var header = file.GetType().GetProperty("Header")!.GetValue(file)!;
                int width = (int)header.GetType().GetProperty("Width")!.GetValue(header)!;
                int height = (int)header.GetType().GetProperty("Height")!.GetValue(header)!;
                if (width <= 0 || height <= 0 || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height) return false;
                if (capture is not null)
                {
                    var content = loadedFiles.GetValue(file, static value =>
                    {
                        if (value.GetType().FullName != "PsdParser.PsdFile" || value.GetType().GetField("stream", Instance)?.GetValue(value) is not MemoryStream stream
                            || stream.CanWrite || !stream.TryGetBuffer(out var bytes)) throw new InvalidDataException("PSD loaded bytes are unavailable");
                        // The audited parser owns this read-only array for its lifetime. Hash once per parsed file.
                        return new(Convert.ToHexString(SHA256.HashData(bytes.AsSpan())));
                    });
                    if (!capture.MatchesLoadedFile(PathOf(item.Character)!, content.Hash)) return false;
                }
                string json = Snapshot(shared);
                if (!normalized.TryGetValue(native, out var expected) || (!expected.Root.TryGetTarget(out var capturedRoot) || !ReferenceEquals(capturedRoot, root))
                    || !ReferenceEquals(expected.Normalized, actual) || expected.SharedJson != json)
                {
                    var resolved = shared.GetType().GetMethod("ResolveAgainst")!.Invoke(shared, [root])!;
                    expected = new(new(root), json, actual, Snapshot(resolved)); normalized.AddOrUpdate(native, expected);
                }
                if (Snapshot(actual) != expected.ExpectedJson) return false;
            }
            return true;
        }
        catch { return false; }
    }
}
