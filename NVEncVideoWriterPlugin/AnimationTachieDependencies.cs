using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

internal static class AnimationTachieDependencies
{
    internal const string AssemblyName = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie";
    internal const string PluginName = AssemblyName + ".AnimationTachiePlugin";
    internal static readonly string SessionResource = "animation-blink-session://" + Guid.NewGuid().ToString("N");
    internal static Func<bool>? ReadinessInstalled { get; set; }
    internal static readonly Guid ReadBuild = new("8e5a1d93-983b-40cd-94f9-d3160cc7ea12");
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Parts = ["Body", "Eye", "Mouth", "Hair", "Eyebrow", "Complexion", "Back1", "Back2", "Back3", "Etc1", "Etc2", "Etc3"];
    // Paths: the part paths of the item, faces and defaults. Inis: the attached INI files the key holds.
    private sealed record Witness(string[] Paths, HashSet<string> Inis);
    private static readonly ConditionalWeakTable<TachieItem, Witness> witnesses = new();
    private static readonly ConcurrentDictionary<string, byte> changedListings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> firstListings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object listingGate = new();
    private static long listingCharacters;
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Blend, PropertyInfo Opacity, PropertyInfo PlaceOn, FieldInfo Ini)> configAccess = new();
    private const long MaximumListingCharacters = 8L << 20; // 16 MiB of UTF-16 listing content, besides bounded table metadata.

    internal static bool Verified(Type? plugin) => plugin?.FullName == PluginName
        && plugin.Assembly.GetName().Name == AssemblyName
        && HostFeatures.For(typeof(Scene).Assembly) is { AnimationTachie: true } features && features.TachieAssembly(plugin.Assembly, ReadBuild)
        && FrameCacheKey.IsBundledPluginAssembly(AssemblyName, plugin.Assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location));

    private static bool Parameter(object? parameter, Type plugin, string name, bool optional = false) => parameter is null ? optional
        : parameter.GetType().Assembly == plugin.Assembly && parameter.GetType().FullName == AssemblyName + "." + name;

    internal static bool Character(Character? character) => character?.TachieType is { } type && Verified(type)
        && Parameter(character.TachieCharacterParameter, type, "CharacterParameter")
        && Parameter(character.TachieDefaultItemParameter, type, "ItemParameter")
        && Parameter(character.TachieDefaultFaceParameter, type, "FaceParameter", optional: true);

    // Whether the character's tachie blinks alike in every run (BlinkSeedAlignment); otherwise its frames are keyed for
    // this run (SessionResource).
    internal static bool StableBlink(Character character) => Character(character) && AlignBlink(character.TachieType.Assembly);

    // The audited build (its MVID), loaded or used by a character.
    internal static bool AlignBlink(Assembly assembly) => assembly.GetName().Name == AssemblyName
        && HostFeatures.For(typeof(Scene).Assembly).TachieAssembly(assembly, ReadBuild) && BlinkSeedAlignment.Stable(assembly, AssemblyName + ".AnimationTachieSource", "Update");

    // All potential parts are dependencies. This deliberately trades fine invalidation for a smaller audited subset.
    internal static bool TryFiles(TachieItem item, Timeline timeline, out string[] files)
    {
        files = [];
        var character = item.Character;
        if (!Character(character) || !(ReadinessInstalled?.Invoke() == true) || item.Length <= 0
            || !Parameter(item.TachieItemParameter, character.TachieType, "ItemParameter")) return false;
        var parameters = new List<object> { item.TachieItemParameter, character.TachieDefaultItemParameter };
        if (character.TachieDefaultFaceParameter is { } defaultFace) parameters.Add(defaultFace);
        var faces = timeline.Items.Where(candidate => ReferenceEquals(FrameCacheKey.GetCharacter(candidate), character))
            .Where(candidate => candidate is VoiceItem or TachieFaceItem).ToArray();
        // The host sorts the shown faces by layer, those of one layer in item-list order, which keys the frame
        // (FrameDependencyIndex.Entry.FaceGroup). Differential composite takes each part from the first face that sets
        // it, else the item: every part of every face and of the item is a dependency below.
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
        witnesses.AddOrUpdate(item, new(paths.ToArray(), result.Where(IsIni).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    // Image lists are read synchronously. Watcher delivery can lag a host file read. The first list of images is never
    // replaced: an already-created native source can retain its old parts count. The attached INI files (X.ini beside
    // X.png, read by LayerConfig) are listed too, as dependencies; SafeSource compares what a source read with them.
    internal static bool Listing(string path, out string[] files, out int count) => ListingCore(path, out files, out count);

    private static bool ListingCore(string path, out string[] files, out int count)
    {
        files = []; count = 0;
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || SettingsBase<FileSettings>.Default.FileExtensions.GetFileType(path).HasFlag(FileType.動画)) return false;
        path = Path.GetFullPath(path);
        if (changedListings.ContainsKey(path)) return false;
        if (!File.Exists(path)) { if (firstListings.ContainsKey(path)) changedListings.TryAdd(path, 0); return false; }
        string directory = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path);
        // Query only this native part's possible names, synchronously on every validation.
        // A directory timestamp can be restored or delayed; it never authorizes reuse.
        string[] candidates = Directory.EnumerateFiles(directory, stem + "*").Take(16385).ToArray();
        if (candidates.Length > 16384) return false;
        var list = candidates.Where(file =>
        {
            string name = Path.GetFileNameWithoutExtension(file), suffix = name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ? name[(stem.Length + 1)..].ToLowerInvariant() : "";
            return name.Equals(stem, StringComparison.OrdinalIgnoreCase) || suffix is "a" or "i" or "u" or "e" or "o"
                || suffix.Length != 0 && suffix.All(char.IsAsciiDigit);
        }).Take(1025).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length > 1024 || list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length
            || list.Any(file => !Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase) && !IsIni(file)))
        { if (firstListings.ContainsKey(path)) changedListings.TryAdd(path, 0); return false; }
        var inis = list.Where(IsIni).ToArray();
        list = list.Where(file => !IsIni(file)).ToArray();
        string listing = string.Join("\n", list);
        if (!firstListings.TryGetValue(path, out string? first))
            lock (listingGate)
            {
                if (!firstListings.TryGetValue(path, out first))
                {
                    if (firstListings.Count >= 4096 || listing.Length > MaximumListingCharacters - listingCharacters) return false;
                    firstListings[path] = first = listing;
                    listingCharacters += listing.Length;
                }
            }
        if (first != listing) { changedListings.TryAdd(path, 0); return false; }
        int numbered = 0;
        var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (numbered < 1024 && set.Contains(Path.Combine(directory, stem + "." + numbered + ".png"))) numbered++;
        if (numbered == 1024) return false;
        count = numbered + 1;
        files = [.. list, .. inis];
        return true;
    }

    private static bool IsIni(string file) => Path.GetExtension(file).Equals(".ini", StringComparison.OrdinalIgnoreCase);

    // The settings LayerConfig.Load reads from `ini` starting from its defaults (Shift-JIS, "key=value" before a ';',
    // blend and opacity parsed with the thread's culture). False when the culture changes a number, so that the result
    // does not depend on the thread that read it.
    internal static bool TryReadIni(string ini, out (int Blend, double Opacity, string? PlaceOn) config)
    {
        config = (0, 100.0, null);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (string line in File.ReadAllLines(ini, Encoding.GetEncoding("shift-jis")))
        {
            string[] pair = line.Split(";")[0].Split("=");
            if (pair.Length != 2) continue;
            switch (pair[0])
            {
                case "blend":
                    bool local = int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.CurrentCulture, out int blend);
                    if (local != int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int invariant) || blend != invariant) return false;
                    if (local) config.Blend = blend;
                    break;
                case "opacity":
                    bool parsed = double.TryParse(pair[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double opacity);
                    if (parsed != double.TryParse(pair[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double neutral)
                        || BitConverter.DoubleToInt64Bits(opacity) != BitConverter.DoubleToInt64Bits(neutral)) return false;
                    if (parsed) config.Opacity = opacity;
                    break;
                case "placeon":
                    config.PlaceOn = pair[1];
                    break;
            }
        }
        return true;
    }

    internal static bool MayStartLipSync(Scene scene, int frame) => NestedTimelineSources.MayStartLipSync(scene, frame, PluginName);

    // Every animation tachie the frame drew (in `timelineSource` and the sources inside it: groups, transitions, scenes)
    // has its files as keyed and the part settings its attached INI files say now; every one of the root timeline at
    // the frame was drawn.
    internal static bool SafeSource(object timelineSource, Scene scene, int frame)
    {
        try
        {
            if (!NestedTimelineSources.AnyTachie(scene, PluginName)) return true;
            if (!(ReadinessInstalled?.Invoke() == true)) return false;
            var drawn = new List<(TachieItem Item, object Source)>();
            if (!NestedTimelineSources.TryTachieSources(timelineSource, drawn)) return false;
            if (NestedTimelineSources.TachieItems(scene.Timeline).Any(item => NestedTimelineSources.Shows(item, frame)
                && item.Character?.TachieType?.FullName == PluginName && !drawn.Any(pair => ReferenceEquals(pair.Item, item)))) return false;
            var verifiedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool CheckListing(string path, out int count)
            {
                if (verifiedCounts.TryGetValue(path, out count)) return true;
                if (!Listing(path, out _, out count)) return false;
                verifiedCounts.Add(path, count); return true;
            }
            var inis = new Dictionary<string, (int, double, string?)?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (item, coreSource) in drawn)
            {
                if (item.Character?.TachieType?.FullName != PluginName) continue;
                if (!witnesses.TryGetValue(item, out var witness) || witness.Paths.Any(path => !CheckListing(path, out _))
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
                    var access = configAccess.GetOrAdd(config.GetType(), static type =>
                        (type.GetProperty("blend")!, type.GetProperty("opacity")!, type.GetProperty("placeon")!,
                            type.GetField("iniPath", Instance) ?? throw new MissingFieldException(type.FullName, "iniPath")));
                    // The settings the layer read must be those of its INI as it is now, read from the defaults:
                    // LayerConfig.Load keeps values an earlier INI set, and reads a file only when the part changes.
                    (int Blend, double Opacity, string? PlaceOn) expected = (0, 100.0, null);
                    if (access.Ini.GetValue(config) is string ini && File.Exists(ini))
                    {
                        // An INI the key does not hold (created after the description).
                        if (!witness.Inis.Contains(Path.GetFullPath(ini))) return false;
                        if (!inis.TryGetValue(ini, out var read)) inis[ini] = read = TryReadIni(ini, out var value) ? value : null;
                        if (read is not { } current) return false;
                        expected = current;
                    }
                    if ((int)access.Blend.GetValue(config)! != expected.Blend
                        || BitConverter.DoubleToInt64Bits((double)access.Opacity.GetValue(config)!) != BitConverter.DoubleToInt64Bits(expected.Opacity)
                        || !string.Equals((string?)access.PlaceOn.GetValue(config), expected.PlaceOn, StringComparison.Ordinal)) return false;
                }
                if (total != 13) return false;
                foreach (string part in new[] { "eye", "mouth" })
                {
                    if (native.GetType().GetField(part + "File", Instance)!.GetValue(native) is string path && !string.IsNullOrEmpty(path)
                        && (!CheckListing(path, out int count) || (int)native.GetType().GetField(part + "PartsCount", Instance)!.GetValue(native)! != count)) return false;
                }
            }
            return true;
        }
        catch { return false; }
    }
}
