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
    private const int MaximumFiles = 256;
    private const int MaximumModelCharacters = 16 * 1024 * 1024;

    public static bool TryCreate(Scene scene, out string key) => TryCreate(scene, out key, out _);

    public static bool TryCreate(Scene scene, out string key, out string reason) =>
        TryCreate(scene, out key, out reason, out _);

    internal static bool TryCreate(Scene scene, out string key, out string reason, out bool hasExternalDependencies)
    {
        key = string.Empty;
        hasExternalDependencies = false;
        if (!TryDescribe(scene, out string model, out string[] paths, out reason)) return false;
        hasExternalDependencies = paths.Length != 0;
        if (hasExternalDependencies)
            return Bypass("External dependencies require background fingerprinting through KeyDependencyTracker.", out reason);
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
    {
        model = string.Empty;
        dependencies = [];
        reason = string.Empty;
        try
        {
            var timelines = scene.Scenes.Timelines.Append(scene.Timeline).Distinct().OrderBy(t => t.ID).ToArray();
            var items = timelines.SelectMany(t => t.Items).ToArray();
            if (items.Length > 100_000) return Bypass("プロジェクトがキャッシュ検査の上限を超えています。", out reason);
            var characters = items.Select(GetCharacter).OfType<Character>().Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var resources = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.GetType().Assembly != typeof(Scene).Assembly)
                    return Bypass("外部アイテムの描画状態を検証できません: " + item.GetType().FullName, out reason);
                if (item is TachieItem)
                    return Bypass("立ち絵の非同期口パクと外部描画状態は通常描画を使用します。", out reason);
                if (item is ShapeItem shape && !IsBuiltIn(shape.ShapeType2))
                    return Bypass("外部図形プラグインの描画状態を検証できません。", out reason);
                foreach (var file in item.GetFiles()) AddPath(file, paths);
                if (item is VoiceItem voice && !string.IsNullOrWhiteSpace(voice.FilePath)) AddPath(voice.FilePath, paths);
                foreach (var resource in item.GetResources()) AddResource(resource, paths, resources);
            }
            foreach (var character in characters)
            {
                foreach (var file in character.GetFiles()) AddPath(file, paths);
                foreach (var resource in character.GetResources()) AddResource(resource, paths, resources);
            }
            if (resources.Any(IsExternalPluginResource))
                return Bypass("外部エフェクト・プラグインの描画状態は通常描画を使用します。", out reason);
            if (sourceReaders.Length != 3) return Bypass("読み込みプラグインの状態を確認できません。", out reason);
            if (paths.Count != 0 && sourceReaders.SelectMany(readers => readers).Any(type => !IsBuiltInSourceReader(type)))
                return Bypass("外部素材でカスタム読み込みプラグインが有効なため、通常描画を使用します。", out reason);
            var settings = SettingsBase<YMMSettings>.Default;
            var loaders = SettingsBase<PluginLoaderSettings>.Default;
            var snapshot = new
            {
                Format = 1,
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
            // Runtime types in polymorphic parameters/effects must also belong to the inspected host.
            foreach (var typeProperty in JObject.Parse(model).Descendants().OfType<JProperty>().Where(p => p.Name == "$type"))
            {
                string type = typeProperty.Value.Value<string>() ?? string.Empty;
                string assembly = type.Split(',').Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
                if (assembly != "YukkuriMovieMaker" && assembly != "YukkuriMovieMaker.Plugin")
                    return Bypass("外部描画パラメーターを検証できません: " + type, out reason);
            }
            if (paths.Count > MaximumFiles) return Bypass("外部素材の数がキャッシュ検査の上限を超えています。", out reason);
            dependencies = paths.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
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
        if (!(assembly.GetName().Name ?? string.Empty).StartsWith("YukkuriMovieMaker.Plugin.FileSource.", StringComparison.Ordinal)) return false;
        string location = assembly.Location;
        if (string.IsNullOrEmpty(location)) return false;
        string? hostDirectory = Path.GetDirectoryName(typeof(Scene).Assembly.Location);
        return !string.IsNullOrEmpty(hostDirectory)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(location)), Path.GetFullPath(hostDirectory), StringComparison.OrdinalIgnoreCase);
    }

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
