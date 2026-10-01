using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.UndoRedo;

namespace NVEncVideoWriterPlugin;

internal static class FrameCacheKey
{
    // Files are fingerprinted in the background and each frame only verifies its own (see FrameDependencyIndex).
    private const int MaximumFiles = 4096;
    private const int MaximumModelCharacters = 16 * 1024 * 1024;

    public static bool TryCreate(Scene scene, out string key) => TryCreate(scene, out key, out _);

    public static bool TryCreate(Scene scene, out string key, out string reason) =>
        TryCreate(scene, out key, out reason, out _);

    internal static bool TryCreate(Scene scene, out string key, out string reason, out bool hasExternalDependencies)
    {
        key = string.Empty;
        hasExternalDependencies = false;
        string model;
        string[] paths;
        FrameDependencyIndex? frames;
        try { if (!TryDescribe(scene, CaptureSourceReaderTypes(), out model, out paths, out frames, out reason)) return false; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
        if (!frames!.Whole.Cacheable) return Bypass("立ち絵（非同期の口パク）か、確認できない素材を使うアイテムがあります。", out reason);
        hasExternalDependencies = paths.Length != 0;
        if (hasExternalDependencies)
            return Bypass("外部素材は背景での内容確認が必要です。", out reason);
        try
        {
            key = FromFingerprints(model, new Dictionary<string, FileFingerprint>());
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
    }

    internal static bool TryDescribe(Scene scene, out string model, out string[] dependencies, out string reason)
    {
        model = string.Empty;
        dependencies = [];
        reason = string.Empty;
        try { return TryDescribe(scene, CaptureSourceReaderTypes(), out model, out dependencies, out reason); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
    }

    internal static bool TryDescribe(Scene scene, Type[][] sourceReaders, out string model, out string[] dependencies, out string reason)
        => TryDescribe(scene, sourceReaders, out model, out dependencies, out _, out reason);

    internal static bool TryDescribe(Scene scene, Type[][] sourceReaders, out string model, out string[] dependencies,
        out FrameDependencyIndex? frames, out string reason)
    {
        model = string.Empty;
        dependencies = [];
        frames = null;
        reason = string.Empty;
        try
        {
            var timelines = scene.Scenes.Timelines.Append(scene.Timeline).Distinct().OrderBy(t => t.ID).ToArray();
            var items = timelines.SelectMany(t => t.Items).ToArray();
            if (items.Length > 100_000) return Bypass("プロジェクトがキャッシュ検査の上限を超えています。", out reason);
            var characters = items.Select(GetCharacter).OfType<Character>().Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var resources = new SortedSet<string>(StringComparer.Ordinal);
            var nestedPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootItems = scene.Timeline.Items.ToArray();
            var rootDependencies = new List<(SortedSet<string> Paths, SortedSet<string> Resources, bool Uncacheable)>(rootItems.Length);
            bool nestedUncacheable = false;
            foreach (var timeline in timelines)
            {
                bool root = ReferenceEquals(timeline, scene.Timeline);
                foreach (var item in timeline.Items)
                {
                    if (item.GetType().Assembly != typeof(Scene).Assembly)
                        return Bypass("外部アイテムの描画状態を検証できません: " + item.GetType().FullName, out reason);
                    if (item is ShapeItem shape && !IsBuiltIn(shape.ShapeType2))
                        return Bypass("外部図形プラグインの描画状態を検証できません。", out reason);
                    var itemPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var itemResources = new SortedSet<string>(StringComparer.Ordinal);
                    // A tachie's mouth follows a volume envelope computed asynchronously (TachieSource), so its frames
                    // are rendered normally. Faces and voices only reach tachie items drawn at the same frame
                    // (TimelineSource picks them per frame, 4.56.1.0), so other frames stay cacheable; in another
                    // timeline it disables the scene item frames that draw it. Its files are then never needed.
                    bool tachie = item is TachieItem;
                    bool uncacheable = tachie;
                    try
                    {
                        if (!tachie)
                        {
                            foreach (var file in item.GetFiles()) AddPath(file, itemPaths);
                            if (item is VoiceItem voice && !string.IsNullOrWhiteSpace(voice.FilePath)) AddPath(voice.FilePath, itemPaths);
                        }
                        foreach (var resource in item.GetResources()) AddResource(resource, tachie ? Unused() : itemPaths, itemResources);
                    }
                    catch (NotSupportedException)
                    {
                        // An uninstalled font or a remote file cannot be fingerprinted: only frames showing the item bypass.
                        uncacheable = true;
                    }
                    paths.UnionWith(itemPaths);
                    resources.UnionWith(itemResources);
                    if (root) rootDependencies.Add((itemPaths, itemResources, uncacheable));
                    else
                    {
                        nestedPaths.UnionWith(itemPaths);
                        nestedUncacheable |= uncacheable;
                    }
                }
            }
            if (rootDependencies.Count != rootItems.Length) return Bypass("タイムラインの状態が検査中に変化しました。", out reason);
            var characterPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var characterResources = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var character in characters)
            {
                // A character's tachie settings only reach TachieSource, whose frames are never cached: their files
                // are not dependencies of any frame (the settings themselves stay in the key, in the character).
                var (shared, tachieOnly) = SplitCharacter<IFileItem>(character);
                var files = shared.SelectMany(part => part.GetFiles()).ToList();
                var tachieFiles = tachieOnly.SelectMany(part => part.GetFiles());
                // If YMM4 adds another kind of character file, all of them stay dependencies of every frame.
                if (!new HashSet<string>(character.GetFiles()).SetEquals(files.Concat(tachieFiles))) files = character.GetFiles().ToList();
                foreach (var file in files) AddPath(file, characterPaths);
                var (sharedResources, tachieResources) = SplitCharacter<IResourceItem>(character);
                foreach (var resource in sharedResources.SelectMany(part => part.GetResources())) AddResource(resource, characterPaths, characterResources);
                foreach (var resource in tachieResources.SelectMany(part => part.GetResources())) AddResource(resource, Unused(), characterResources);
            }
            paths.UnionWith(characterPaths);
            resources.UnionWith(characterResources);
            if (resources.Any(IsExternalPluginResource))
                return Bypass("外部エフェクト・プラグインの描画状態は通常描画を使用します。", out reason);
            if (sourceReaders.Length != 3) return Bypass("読み込みプラグインの状態を確認できません。", out reason);
            if (paths.Count != 0 && sourceReaders.SelectMany(readers => readers).Any(type => !IsBuiltInSourceReader(type)))
                return Bypass("外部素材でカスタム読み込みプラグインが有効なため、通常描画を使用します。", out reason);
            var settings = SettingsBase<YMMSettings>.Default;
            var loaders = SettingsBase<PluginLoaderSettings>.Default;
            var snapshot = new
            {
                Format = 2,
                Host = typeof(Scene).Assembly.ManifestModule.ModuleVersionId,
                PluginApi = typeof(CacheProvider).Assembly.ManifestModule.ModuleVersionId,
                Root = scene.ID,
                scene.ParentScenes,
                Timelines = timelines.Select(t => new { t.ID, t.Items, t.VideoInfo, t.LayerSettings, t.Length }).ToArray(),
                Characters = characters,
                Resources = resources.ToArray(),
                Zoom = settings.GetZoomMode(),
                SourceReader = settings.GetMFSourceReaderMode2(),
                VoiceUpsampling = settings.GetVoiceUpsamplingMode(),
                // Background image, texture and image brush files load as video or image by these (GetFileType).
                FileTypes = FileTypes(),
                loaders.VideoFileSourcePlugins,
                loaders.ImageFileSourcePlugins,
                loaders.AudioFileSourcePlugins,
                SourceReaders = new
                {
                    Video = SourceReaderIdentities(sourceReaders[0]),
                    Image = SourceReaderIdentities(sourceReaders[1]),
                    Audio = SourceReaderIdentities(sourceReaders[2]),
                },
            };
            model = YukkuriMovieMaker.Json.Json.GetJsonText(snapshot);
            if (model.Length > MaximumModelCharacters)
                return Bypass("プロジェクトの描画状態がキャッシュ検査の上限を超えています。", out reason);
            // Strings stay strings (no date parsing), so distinct texts never serialize to the same token.
            JObject parsed;
            using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(model)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                parsed = JObject.Load(reader);
            // Runtime types in polymorphic parameters/effects must also belong to the inspected host.
            foreach (var typeProperty in parsed.Descendants().OfType<JProperty>().Where(p => p.Name == "$type"))
            {
                string type = typeProperty.Value.Value<string>() ?? string.Empty;
                string assembly = type.Split(',').Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
                if (assembly != "YukkuriMovieMaker" && assembly != "YukkuriMovieMaker.Plugin" && !IsBundledTachieParameter(typeProperty, assembly))
                    return Bypass("外部描画パラメーターを検証できません: " + type, out reason);
            }
            if (paths.Count > MaximumFiles) return Bypass("外部素材の数がキャッシュ検査の上限を超えています。", out reason);
            frames = DescribeFrames(parsed, scene.Timeline.ID, rootItems, rootDependencies, characterPaths, characterResources, nestedPaths, nestedUncacheable);
            dependencies = paths.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
    }

    // Parameters of the tachie plugins YMM4 ships (loaded from its folder) only reach TachieSource, whose frames are
    // rendered normally, so they do not disable the other frames. Elsewhere, or from a tachie plugin a user added,
    // a foreign type still bypasses.
    private static bool IsBundledTachieParameter(JProperty typeProperty, string assemblyName)
    {
        if (!assemblyName.StartsWith("YukkuriMovieMaker.Plugin.Tachie.", StringComparison.Ordinal)
            || !typeProperty.Ancestors().OfType<JProperty>().Any(property =>
                property.Name.StartsWith("Tachie", StringComparison.Ordinal) && property.Name.EndsWith("Parameter", StringComparison.Ordinal)))
            return false;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(loaded => loaded.GetName().Name == assemblyName);
        return assembly is not null
            && IsBundledPluginAssembly(assemblyName, assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location));
    }

    // Character.GetFiles/GetResources in 4.56.1.0: subtitle and audio effects, and the tachie effects and parameters.
    private static (IEnumerable<T> Shared, IEnumerable<T> TachieOnly) SplitCharacter<T>(Character character) =>
        (character.JimakuVideoEffects.OfType<T>().Concat(character.AudioEffects.OfType<T>()),
         character.TachieItemVideoEffects.OfType<T>().Concat(character.TachieDefaultFaceEffects.OfType<T>())
            .Concat(new object?[] { character.TachieCharacterParameter, character.TachieDefaultItemParameter, character.TachieDefaultFaceParameter }.OfType<T>()));

    private static SortedSet<string> Unused() => new(StringComparer.OrdinalIgnoreCase);

    internal static string[] FileTypes() =>
        SettingsBase<FileSettings>.Default.FileExtensions.Select(extension => $"{extension.Extention}={extension.FileType}").ToArray();

    // Splits the serialized model into the part every frame depends on (everything but timeline items), the
    // other timelines (only read by frames with a scene item), and one hash per root timeline item.
    private static FrameDependencyIndex DescribeFrames(JObject parsed, Guid rootId, IItem[] rootItems,
        List<(SortedSet<string> Paths, SortedSet<string> Resources, bool Uncacheable)> rootDependencies,
        SortedSet<string> characterPaths, SortedSet<string> characterResources, SortedSet<string> nestedPaths, bool nestedUncacheable)
    {
        var timelines = (JArray)parsed["Timelines"]!;
        var root = timelines.OfType<JObject>().Single(t => Guid.TryParse(t["ID"]?.ToString(), out var id) && id == rootId);
        var rootTokens = (JArray)root["Items"]!;
        if (rootTokens.Count != rootItems.Length) throw new InvalidDataException("Serialized root items do not match the timeline");
        var nested = new JArray(timelines.Where(t => !ReferenceEquals(t, root)).Select(t => t.DeepClone()));
        var global = (JObject)parsed.DeepClone();
        var rootSettings = (JObject)root.DeepClone();
        rootSettings.Remove("Items");
        global["Timelines"] = new JArray(rootSettings);
        global["Resources"] = new JArray(characterResources);
        var entries = new FrameDependencyIndex.Entry[rootItems.Length];
        for (int i = 0; i < rootItems.Length; i++)
        {
            var item = rootItems[i];
            string text = rootTokens[i].ToString(Newtonsoft.Json.Formatting.None);
            string identity = item.GetType().FullName + "\n" + text + "\n" + string.Join("\n", rootDependencies[i].Resources);
            // Scene items render other timelines; audio spectrum shapes read the timeline's or a scene's audio.
            bool wide = item is SceneItem || text.Contains("AudioSpectrum", StringComparison.Ordinal);
            entries[i] = new(item.Frame, item.Length, item is TransitionItem, wide, FrameDependencyIndex.Hash(identity),
                rootDependencies[i].Paths.ToArray(), rootDependencies[i].Uncacheable);
        }
        return new FrameDependencyIndex(FrameDependencyIndex.Hash(global.ToString(Newtonsoft.Json.Formatting.None)), characterPaths,
            FrameDependencyIndex.Hash(nested.ToString(Newtonsoft.Json.Formatting.None)), nestedPaths, entries, nestedUncacheable);
    }

    internal static Character? GetCharacter(IItem item) => item switch
    {
        VoiceItem voice => voice.Character,
        TachieItem tachie => tachie.Character,
        _ => null,
    };

    private static bool IsBuiltIn(Type? type) => type != null &&
        (type.Assembly == typeof(Scene).Assembly || type.Assembly == typeof(CacheProvider).Assembly);

    internal static Type[][] CaptureSourceReaderTypes() =>
    [
        PluginLoader.VideoFileSourcePlugins.Select(p => p.GetType()).ToArray(),
        PluginLoader.ImageFileSourcePlugins.Select(p => p.GetType()).ToArray(),
        PluginLoader.AudioFileSourcePlugins.Select(p => p.GetType()).ToArray(),
    ];

    internal static bool SourceReadersMatch(Type[][] expected)
    {
        try
        {
            return expected.Length == 3
                && SourceReadersMatch(expected[0], PluginLoader.VideoFileSourcePlugins)
                && SourceReadersMatch(expected[1], PluginLoader.ImageFileSourcePlugins)
                && SourceReadersMatch(expected[2], PluginLoader.AudioFileSourcePlugins);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    private static bool SourceReadersMatch<T>(Type[] expected, IEnumerable<T> plugins) where T : class
    {
        int index = 0;
        foreach (var plugin in plugins)
        {
            if (index >= expected.Length) return false;
            var current = plugin.GetType();
            var previous = expected[index++];
            if (previous != current || previous.Assembly.ManifestModule.ModuleVersionId != current.Assembly.ManifestModule.ModuleVersionId)
                return false;
        }
        return index == expected.Length;
    }

    internal static bool IsBuiltInSourceReader(Type type)
    {
        var assembly = type.Assembly;
        if (assembly == typeof(Scene).Assembly || assembly == typeof(CacheProvider).Assembly) return true;
        return IsBundledPluginAssembly(assembly.GetName().Name, assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location));
    }

    // YMM4 loads the plugin assemblies it ships from its own folder: the file sources, and Community with its MIDI
    // audio reader, which every install has. Plugins users add load from user\plugin and stay external. Each
    // reader's type, assembly and MVID are also part of the key.
    internal static bool IsBundledPluginAssembly(string? name, string? location, string? hostDirectory) =>
        (name ?? string.Empty).StartsWith("YukkuriMovieMaker.Plugin.", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(hostDirectory)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(location)), Path.GetFullPath(hostDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string[] SourceReaderIdentities(IEnumerable<Type> types) => types
        .Select(type => $"{type.FullName}|{type.Assembly.GetName().Name}|{type.Assembly.ManifestModule.ModuleVersionId:D}")
        .ToArray();

    private static bool IsExternalPluginResource(string resource)
    {
        if (!(resource.StartsWith("ve://", StringComparison.Ordinal) || resource.StartsWith("ae://", StringComparison.Ordinal) || resource.StartsWith("plugin://", StringComparison.Ordinal))) return false;
        string typeName = resource[(resource.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return !IsBuiltIn(typeof(Scene).Assembly.GetType(typeName) ?? typeof(CacheProvider).Assembly.GetType(typeName));
    }

    internal static string FromFingerprints(string model, IReadOnlyDictionary<string, FileFingerprint> fingerprints)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, model);
        foreach (var pair in fingerprints.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, pair.Key.ToUpperInvariant());
            hash.AppendData(Convert.FromHexString(pair.Value.ContentHash));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddPath(string value, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) throw new NotSupportedException("Remote resource");
            value = uri.LocalPath;
        }
        paths.Add(Path.GetFullPath(value));
    }

    private static void AddResource(TimelineResource resource, ISet<string> paths, ISet<string> resources)
    {
        resources.Add(resource.Key);
        if (Uri.TryCreate(resource.Key, UriKind.Absolute, out var uri) && uri.IsFile)
            AddPath(uri.LocalPath, paths);
        else if (resource.ResourceType == TimelineResourceType.Font)
        {
            // Font family names alone do not identify installed font content.
            var family = new System.Windows.Media.FontFamily(resource.Key["font://".Length..]);
            foreach (var typeface in family.GetTypefaces())
            {
                if (!typeface.TryGetGlyphTypeface(out var glyph) || !glyph.FontUri.IsFile)
                    throw new NotSupportedException("Unresolved font");
                AddPath(glyph.FontUri.LocalPath, paths);
            }
        }
        else if (resource.ResourceType is TimelineResourceType.Video or TimelineResourceType.Image or TimelineResourceType.Audio or TimelineResourceType.CustomVoice or TimelineResourceType.Tachie)
            AddPath(resource.Key, paths);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static bool Bypass(string message, out string reason) { reason = message; return false; }
}
