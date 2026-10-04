using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

internal static class AnimationTachieDependencies
{
    internal const string AssemblyName = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie";
    internal const string PluginName = AssemblyName + ".AnimationTachiePlugin";
    internal static readonly string SessionResource = "animation-blink-session://" + Guid.NewGuid().ToString("N");
    internal static Func<bool>? ReadinessInstalled { get; set; }
    private static readonly Guid ReadBuild = new("8e5a1d93-983b-40cd-94f9-d3160cc7ea12");
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Parts = ["Body", "Eye", "Mouth", "Hair", "Eyebrow", "Complexion", "Back1", "Back2", "Back3", "Etc1", "Etc2", "Etc3"];
    private sealed record Witness(string[] Paths);
    private static readonly ConditionalWeakTable<TachieItem, Witness> witnesses = new();
    private static readonly ConcurrentDictionary<string, byte> changedListings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> firstListings = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Verified(Type? plugin) => plugin?.FullName == PluginName
        && plugin.Assembly.GetName().Name == AssemblyName && plugin.Assembly.ManifestModule.ModuleVersionId == ReadBuild
        && HostFeatures.For(typeof(Scene).Assembly).AnimationTachie
        && FrameCacheKey.IsBundledPluginAssembly(AssemblyName, plugin.Assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location));

    private static bool Parameter(object? parameter, Type plugin, string name, bool optional = false) => parameter is null ? optional
        : parameter.GetType().Assembly == plugin.Assembly && parameter.GetType().FullName == AssemblyName + "." + name;

    internal static bool Character(Character? character) => character?.TachieType is { } type && Verified(type)
        && Parameter(character.TachieCharacterParameter, type, "CharacterParameter")
        && Parameter(character.TachieDefaultItemParameter, type, "ItemParameter")
        && Parameter(character.TachieDefaultFaceParameter, type, "FaceParameter", optional: true);

    // All potential parts are dependencies. This deliberately trades fine invalidation for a smaller audited subset.
    internal static bool TryFiles(TachieItem item, Timeline timeline, out string[] files)
    {
        files = [];
        var character = item.Character;
        if (!Character(character) || !(ReadinessInstalled?.Invoke() == true) || item.Length <= 0
            || timeline.Items.Any(candidate => candidate is GroupItem)
            || !Parameter(item.TachieItemParameter, character.TachieType, "ItemParameter")) return false;
        if ((bool)item.TachieItemParameter.GetType().GetProperty("IsDifferentialComposite")!.GetValue(item.TachieItemParameter)!) return false;
        var parameters = new List<object> { item.TachieItemParameter, character.TachieDefaultItemParameter };
        if (character.TachieDefaultFaceParameter is { } defaultFace) parameters.Add(defaultFace);
        var faces = timeline.Items.Where(candidate => ReferenceEquals(FrameCacheKey.GetCharacter(candidate), character))
            .Where(candidate => candidate is VoiceItem or TachieFaceItem).ToArray();
        // The host sorts selected faces by layer; an equal layer is not represented in the ordinary sorted item key.
        if (faces.GroupBy(face => face.Layer).Any(group => group.Count() > 1
            && group.Any(a => group.Any(b => !ReferenceEquals(a, b) && a.Frame < (long)b.Frame + b.Length && b.Frame < (long)a.Frame + a.Length)))) return false;
        foreach (var face in faces)
        {
            object? parameter = face is VoiceItem voice ? voice.TachieFaceParameter : ((TachieFaceItem)face).TachieFaceParameter;
            if (!Parameter(parameter, character.TachieType, "FaceParameter", optional: true)) return false;
            if (parameter is not null) parameters.Add(parameter);
        }
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
            foreach (string property in Parts)
                if (parameter.GetType().GetProperty(property)!.GetValue(parameter) is string path && !string.IsNullOrEmpty(path))
                {
                    if (!Listing(path, out var listed, out _)) return false;
                    paths.Add(path);
                    result.UnionWith(listed);
                }
        files = result.ToArray();
        witnesses.AddOrUpdate(item, new(paths.ToArray()));
        return true;
    }

    // Image lists and INI existence are read synchronously. Watcher delivery can lag a host file read.
    // The first list is never replaced: an already-created native source can retain its old parts count.
    internal static bool Listing(string path, out string[] files, out int count)
    {
        files = []; count = 0;
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) return false;
        path = Path.GetFullPath(path);
        if (changedListings.ContainsKey(path)) return false;
        string directory = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path);
        var list = Directory.EnumerateFiles(directory, stem + "*").Where(file =>
        {
            string name = Path.GetFileNameWithoutExtension(file), suffix = name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ? name[(stem.Length + 1)..] : "";
            return name.Equals(stem, StringComparison.OrdinalIgnoreCase) || suffix is "a" or "i" or "u" or "e" or "o"
                || suffix.Length != 0 && suffix.All(char.IsAsciiDigit);
        }).Take(1025).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length > 1024 || list.Any(file => !Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)))
        { if (firstListings.ContainsKey(path)) changedListings.TryAdd(path, 0); return false; }
        string listing = string.Join("\n", list);
        if (firstListings.Count >= 4096 && !firstListings.ContainsKey(path)) return false;
        if (firstListings.GetOrAdd(path, listing) != listing) { changedListings.TryAdd(path, 0); return false; }
        int numbered = 0;
        var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (numbered < 1024 && set.Contains(Path.Combine(directory, stem + "." + numbered + ".png"))) numbered++;
        if (numbered == 1024) return false;
        count = numbered + 1;
        files = list;
        return true;
    }

    internal static bool ContainsNative(Scene scene) => scene.Timeline.Items.OfType<TachieItem>()
        .Any(item => item.Character?.TachieType?.FullName == PluginName);

    internal static bool SafeSource(object timelineSource, Scene scene, int frame)
    {
        try
        {
            var active = scene.Timeline.Items.OfType<TachieItem>().Where(item => item.Frame <= frame && frame < (long)item.Frame + item.Length
                && item.Character?.TachieType?.FullName == PluginName).ToArray();
            if (active.Length == 0) return true;
            if (!(ReadinessInstalled?.Invoke() == true) || timelineSource.GetType().FullName != "YukkuriMovieMaker.Player.Video.TimelineSource") return false;
            var resources = timelineSource.GetType().GetField("timelineResources", Instance)?.GetValue(timelineSource) as IDictionary;
            if (resources is null) return false;
            foreach (var item in active)
            {
                if (!witnesses.TryGetValue(item, out var witness) || witness.Paths.Any(path => !Listing(path, out _, out _))
                    || resources[item] is not { } effected) return false;
                var coreSource = effected.GetType().GetProperty("Source", Instance)?.GetValue(effected);
                if (coreSource?.GetType().FullName != "YukkuriMovieMaker.Player.Video.Items.TachieSource"
                    || !ReferenceEquals(coreSource.GetType().GetField("item", Instance)?.GetValue(coreSource), item)) return false;
                var native = coreSource.GetType().GetField("source", Instance)?.GetValue(coreSource);
                if (native?.GetType().FullName != AssemblyName + ".AnimationTachieSource" || native.GetType().Assembly != item.Character.TachieType.Assembly) return false;
                var layers = native.GetType().GetProperty("Layers", Instance)?.GetValue(native) as IEnumerable;
                if (layers is null) return false;
                int total = 0;
                foreach (object layer in layers)
                {
                    total++;
                    var config = layer.GetType().GetProperty("Config")!.GetValue(layer)!;
                    if ((int)config.GetType().GetProperty("blend")!.GetValue(config)! != 0
                        || (double)config.GetType().GetProperty("opacity")!.GetValue(config)! != 100
                        || !string.IsNullOrWhiteSpace((string?)config.GetType().GetProperty("placeon")!.GetValue(config))) return false;
                }
                if (total != 13) return false;
                foreach (string part in new[] { "eye", "mouth" })
                {
                    if (native.GetType().GetField(part + "File", Instance)!.GetValue(native) is string path && !string.IsNullOrEmpty(path)
                        && (!Listing(path, out _, out int count) || (int)native.GetType().GetField(part + "PartsCount", Instance)!.GetValue(native)! != count)) return false;
                }
            }
            return true;
        }
        catch { return false; }
    }
}
