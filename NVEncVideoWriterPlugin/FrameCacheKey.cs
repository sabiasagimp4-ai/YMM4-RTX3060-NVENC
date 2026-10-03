using System.ComponentModel;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpGen.Runtime;
using Vortice.DirectWrite;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.KanjiToYomi;
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
        if (!frames!.Whole.Cacheable) return Bypass("立ち絵、外部プラグインのコード、確認できない素材のいずれかを使うアイテムがあります。", out reason);
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
            var nestedResources = new SortedSet<string>(StringComparer.Ordinal);
            var rootItems = scene.Timeline.Items.ToArray();
            var rootDependencies = new List<(SortedSet<string> Paths, SortedSet<string> Resources, bool Uncacheable, bool Session)>(rootItems.Length);
            bool nestedUncacheable = false, nestedSession = false, audioForeign = false;
            if (sourceReaders.Length != 3) return Bypass("読み込みプラグインの状態を確認できません。", out reason);
            // Files are read by the file source readers (fonts by DirectWrite). With a reader whose code was not
            // read, the items that read files are rendered normally.
            // YMM4's code, the audited Community namespaces and the plugins the user trusts (KnownCode).
            var code = KnownCode.Capture();
            bool customReaders = sourceReaders.SelectMany(readers => readers).Any(type => !IsBuiltInSourceReader(type) && !code.Knows(type));

            var asterisk = AsteriskWordSets();

            // Characters first: what in a character is a plugin's or cannot be resolved disables the items using it.
            var characterPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var characterResources = new SortedSet<string>(StringComparer.Ordinal);
            var foreignCharacters = new HashSet<Character>();
            foreach (var character in characters)
            {
                var ownPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var ownFonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // A character's tachie settings only reach TachieSource, whose frames are never cached: their
                    // files are not dependencies of any frame (the settings themselves stay in the key).
                    var (shared, tachieOnly) = SplitCharacter<IFileItem>(character);
                    var files = shared.SelectMany(part => part.GetFiles()).ToList();
                    var tachieFiles = tachieOnly.SelectMany(part => part.GetFiles());
                    // If YMM4 adds another kind of character file, all of them stay dependencies of every frame.
                    if (!new HashSet<string>(character.GetFiles()).SetEquals(files.Concat(tachieFiles))) files = character.GetFiles().ToList();
                    foreach (var file in files) AddPath(file, ownPaths);
                    var (sharedResources, tachieResources) = SplitCharacter<IResourceItem>(character);
                    foreach (var resource in sharedResources.SelectMany(part => part.GetResources()))
                    {
                        if (Note(ClassifyResource(resource.Key, code), ref audioForeign)) foreignCharacters.Add(character);
                        AddResource(resource, ownPaths, characterResources, ownFonts);
                    }
                    foreach (var resource in tachieResources.SelectMany(part => part.GetResources())) AddResource(resource, Unused(), characterResources, null);
                    if (customReaders && ownPaths.Any(path => !ownFonts.Contains(path))) foreignCharacters.Add(character);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    foreignCharacters.Add(character); // e.g. a remote file
                }
                characterPaths.UnionWith(ownPaths);
            }

            foreach (var timeline in timelines)
            {
                bool root = ReferenceEquals(timeline, scene.Timeline);
                foreach (var item in timeline.Items)
                {
                    var itemPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var itemFonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    var itemResources = new SortedSet<string>(StringComparer.Ordinal);
                    // A tachie's mouth follows a volume envelope computed asynchronously (TachieSource), so its frames
                    // are rendered normally. Faces and voices only reach tachie items drawn at the same frame
                    // (TimelineSource picks them per frame, 4.56.1.0), so other frames stay cacheable; in another
                    // timeline it disables the scene item frames that draw it. Its files are then never needed.
                    bool tachie = item is TachieItem;
                    // Code this plugin did not read renders a plugin's item type, a plugin's shape, and (below) a
                    // plugin's effect, brush or transition: only the frames showing such an item are rendered normally
                    // (CompositeItemPicker draws an item only at its own frames; transitions and scene items are
                    // followed by FrameDependencyIndex).
                    bool uncacheable = tachie || !code.Knows(item.GetType())
                        || item is ShapeItem shape && !code.Knows(shape.ShapeType2)
                        || item is TransitionItem transition && transition.TransitionType is { } transitionType && !code.Knows(transitionType)
                        || GetCharacter(item) is { } character && foreignCharacters.Contains(character);
                    bool session = false;
                    try
                    {
                        if (!tachie)
                        {
                            foreach (var file in item.GetFiles()) AddPath(file, itemPaths);
                            if (item is VoiceItem voice && !string.IsNullOrWhiteSpace(voice.FilePath)) AddPath(voice.FilePath, itemPaths);
                        }
                        foreach (var resource in item.GetResources())
                        {
                            uncacheable |= Note(ClassifyResource(resource.Key, code), ref audioForeign);
                            AddResource(resource, tachie ? Unused() : itemPaths, itemResources, itemFonts);
                        }
                        // Randomness YMM4 seeds with object identities (see IdentitySeeds).
                        var seeds = IdentitySeeds(item, out bool randomOrder);
                        uncacheable |= randomOrder;
                        if (seeds.Count != 0)
                        {
                            itemResources.Add("identity://" + string.Join(",", seeds));
                            session = true;
                        }
                        if (item is TextItem or VoiceItem)
                        {
                            if (asterisk is not { } words) uncacheable = true;
                            else
                            {
                                if (words.Resource is not null) itemResources.Add(words.Resource);
                                foreach (string font in DecorationFonts(item, words.Replacements)) AddFont(font, itemPaths, itemResources, itemFonts);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        // An uninstalled font, a remote file, or a plugin item failing to list its files.
                        uncacheable = true;
                    }
                    if (customReaders && itemPaths.Any(path => !itemFonts.Contains(path))) uncacheable = true;
                    paths.UnionWith(itemPaths);
                    resources.UnionWith(itemResources);
                    if (root) rootDependencies.Add((itemPaths, itemResources, uncacheable, session));
                    else
                    {
                        nestedPaths.UnionWith(itemPaths);
                        nestedResources.UnionWith(itemResources);
                        nestedUncacheable |= uncacheable;
                        nestedSession |= session;
                    }
                }
            }
            if (rootDependencies.Count != rootItems.Length) return Bypass("タイムラインの状態が検査中に変化しました。", out reason);
            paths.UnionWith(characterPaths);
            resources.UnionWith(characterResources);
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
                // Which code beyond YMM4's own may draw (audited Community build, trusted plugins with their MVIDs).
                Code = code.Identity,
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
            // Runtime types in polymorphic parameters and effects are checked while the model is split (strings stay
            // strings, so distinct texts never serialize to the same token): a plugin's type disables the item,
            // timeline or character holding it; elsewhere (project-wide settings) the whole project.
            if (!FrameModelSplit.TrySplit(model, scene.Timeline.ID, characterResources, (type, path) => ClassifyType(type, path, code), out var split, out string? rejected))
                return Bypass("プロジェクト全体の描画設定に外部プラグインの型があります: " + rejected, out reason);
            if (split.ForeignItems.Length != rootItems.Length) throw new InvalidDataException("Serialized root items do not match the timeline");
            for (int i = 0; i < rootItems.Length; i++)
            {
                bool foreignCharacter = GetCharacter(rootItems[i]) is { } character
                    && split.ForeignCharacters.Any(index => ReferenceEquals(characters[index], character));
                if (split.ForeignItems[i] || foreignCharacter)
                    rootDependencies[i] = (rootDependencies[i].Paths, rootDependencies[i].Resources, true, rootDependencies[i].Session);
                // A random move the identity walk did not reach (GetAnimatables does not list it): not keyed.
                if (!rootDependencies[i].Session && split.RootItems[i].Contains(RandomMoveJson, StringComparison.Ordinal))
                    rootDependencies[i] = (rootDependencies[i].Paths, rootDependencies[i].Resources, true, false);
            }
            nestedUncacheable |= !nestedSession && split.Nested.Contains(RandomMoveJson, StringComparison.Ordinal);
            // A character's random move the crawl did not reach: its voice items are not keyed.
            if (split.Global.Contains(RandomMoveJson, StringComparison.Ordinal) && !characters.Any(c => Seeds(c).Count != 0))
            {
                for (int i = 0; i < rootItems.Length; i++)
                    if (rootItems[i] is VoiceItem) rootDependencies[i] = (rootDependencies[i].Paths, rootDependencies[i].Resources, true, rootDependencies[i].Session);
                nestedUncacheable |= timelines.Where(t => !ReferenceEquals(t, scene.Timeline)).SelectMany(t => t.Items).Any(item => item is VoiceItem);
            }
            nestedUncacheable |= split.NestedForeign || timelines.Where(t => !ReferenceEquals(t, scene.Timeline)).SelectMany(t => t.Items)
                .Any(item => GetCharacter(item) is { } character && split.ForeignCharacters.Any(index => ReferenceEquals(characters[index], character)));
            if (paths.Count > MaximumFiles) return Bypass("外部素材の数がキャッシュ検査の上限を超えています。", out reason);
            // The MIDI reader YMM4 ships (Community) synthesizes with its own settings and SoundFont files, which the
            // key does not hold: frames that read audio render normally.
            audioForeign |= paths.Any(IsMidi);
            // Wide frames (scene items, audio spectrum) read other timelines and the audio: a plugin's audio effect
            // anywhere reaches them.
            frames = DescribeFrames(split, rootItems, rootDependencies, characterPaths, nestedPaths, nestedResources,
                nestedUncacheable || audioForeign || split.AudioForeign, nestedSession);
            dependencies = paths.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = "描画キャッシュの状態検査を省略しました: " + ex.GetType().Name;
            return false;
        }
    }

    // A "$type" outside the host and plugin API is a plugin's code, except where no cached frame reads it: tachie
    // parameters only reach TachieSource (tachie frames are never cached), and voice parameters only make the voice's
    // audio, a fingerprinted file (no video renderer reads them: TimelineSource, JimakuSource, 4.56.1.0). Audio
    // effects only reach frames that read audio.
    private static FrameModelSplit.TypeUse ClassifyType(string type, IReadOnlyList<string> path, KnownCode code)
    {
        string assembly = type.Split(',').Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
        // YMM4's noise audio effect draws from an unseeded Random (ColoredNoiseAudioStream, BrownNoiseStream).
        if (type.StartsWith("YukkuriMovieMaker.Project.Effects.Audio.NoiseEffect,", StringComparison.Ordinal)) return FrameModelSplit.TypeUse.AudioOnly;
        if (code.Knows(type.Split(',')[0].Trim(), assembly)) return FrameModelSplit.TypeUse.Known;
        if (path.Any(property => property == "VoiceParameter"
            || property.StartsWith("Tachie", StringComparison.Ordinal) && property.EndsWith("Parameter", StringComparison.Ordinal)))
            return FrameModelSplit.TypeUse.Known;
        return path.Contains("AudioEffects") ? FrameModelSplit.TypeUse.AudioOnly : FrameModelSplit.TypeUse.Foreign;
    }

    // Resources naming a plugin's code: ve:// (video effect), ae:// (audio effect), plugin:// (shape, transition,
    // brush, voice, tachie). Voice and tachie plugins draw nothing a cached frame shows (see ClassifyType).
    private static FrameModelSplit.TypeUse ClassifyResource(string resource, KnownCode code)
    {
        int scheme = resource.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0 || resource[..scheme] is not ("ve" or "ae" or "plugin")) return FrameModelSplit.TypeUse.Known;
        string typeName = resource[(scheme + 3)..];
        if (IsBuiltIn(typeof(Scene).Assembly.GetType(typeName) ?? typeof(CacheProvider).Assembly.GetType(typeName))) return FrameModelSplit.TypeUse.Known;
        var pluginType = PluginLoader.UserVideoEffects.Concat(PluginLoader.UserAudioEffects).FirstOrDefault(type => type.FullName == typeName)
            ?? PluginLoader.UserPlugins.FirstOrDefault(plugin => plugin.GetType().FullName == typeName)?.GetType();
        if (code.Knows(pluginType)) return FrameModelSplit.TypeUse.Known;
        if (resource.StartsWith("ae://", StringComparison.Ordinal)) return FrameModelSplit.TypeUse.AudioOnly;
        if (resource.StartsWith("plugin://", StringComparison.Ordinal)
            && PluginLoader.UserPlugins.FirstOrDefault(plugin => plugin.GetType().FullName == typeName)
                is YukkuriMovieMaker.Plugin.Voice.IVoicePlugin or YukkuriMovieMaker.Plugin.Tachie.ITachiePlugin)
            return FrameModelSplit.TypeUse.Known;
        return FrameModelSplit.TypeUse.Foreign;
    }

    // True for Foreign; records AudioOnly.
    private static bool Note(FrameModelSplit.TypeUse use, ref bool audioForeign)
    {
        audioForeign |= use == FrameModelSplit.TypeUse.AudioOnly;
        return use == FrameModelSplit.TypeUse.Foreign;
    }

    // Character.GetFiles/GetResources in 4.56.1.0: subtitle and audio effects, and the tachie effects and parameters.
    private static (IEnumerable<T> Shared, IEnumerable<T> TachieOnly) SplitCharacter<T>(Character character) =>
        (character.JimakuVideoEffects.OfType<T>().Concat(character.AudioEffects.OfType<T>()),
         character.TachieItemVideoEffects.OfType<T>().Concat(character.TachieDefaultFaceEffects.OfType<T>())
            .Concat(new object?[] { character.TachieCharacterParameter, character.TachieDefaultItemParameter, character.TachieDefaultFaceParameter }.OfType<T>()));

    private static SortedSet<string> Unused() => new(StringComparer.OrdinalIgnoreCase);

    // YMM4 seeds some randomness with an object's identity hash (GetHashCode is not overridden, 4.56.1.0): random-move
    // animations (Animation.GetValue), the Random*Effect family (RandomEffectBase) and the RandomDuplicator, Crash,
    // InOutCrash, RandomLine, InOutRandomLine and Noise (unique seed) effects, and the Community effects listed below.
    // They draw otherwise in another process, after the project is loaded again, and in the idle pre-renderer's clone:
    // the item's key holds those objects' identity hashes (its frames are keyed for these objects only). Text revealed
    // or hidden in random order is seeded by YMM4's text source, created again when the item comes back into the frame
    // (TextSource, JimakuSource): those items render normally (randomOrder).
    internal static List<int> IdentitySeeds(IItem item, out bool randomOrder)
    {
        randomOrder = item switch
        {
            TextItem text => text.DisplayDirection == TypewriterAnimationDirection.Random || text.HideDirection == TypewriterAnimationDirection.Random,
            VoiceItem voice when voice.JimakuVisibility == JimakuVisibility.Custom =>
                voice.DisplayDirection == TypewriterAnimationDirection.Random || voice.HideDirection == TypewriterAnimationDirection.Random,
            VoiceItem voice => voice.Character is { } owner
                && (owner.DisplayDirection == TypewriterAnimationDirection.Random || owner.HideDirection == TypewriterAnimationDirection.Random),
            _ => false,
        };
        return GetCharacter(item) is { } character ? Seeds(item, character) : Seeds(item);
    }

    private static List<int> Seeds(params object[] roots)
    {
        var seeds = new SortedSet<int>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>(roots);
        while (pending.Count != 0)
        {
            object value = pending.Pop();
            if (!visited.Add(value)) continue;
            if (value is Animation animation)
            {
                if (animation.AnimationType == AnimationType.ランダム移動) seeds.Add(RuntimeHelpers.GetHashCode(animation));
                continue;
            }
            var type = value.GetType();
            if (IsIdentitySeeded(type))
            {
                seeds.Add(RuntimeHelpers.GetHashCode(value));
                // NoiseEffect with a unique seed uses its parameter object's hash.
                if (type.GetProperty("NoiseParameter")?.GetValue(value) is { } noise) seeds.Add(RuntimeHelpers.GetHashCode(noise));
            }
            foreach (var child in value is Character owner ? CharacterParts(owner) : Animatables(value))
                if (child is not null) pending.Push(child);
        }
        return [.. seeds];
    }

    // Character has no GetAnimatables: its animations and effect lists (subtitles, audio, tachie).
    private static IEnumerable<object?> CharacterParts(Character character)
    {
        foreach (var property in typeof(Character).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead) continue;
            object? value;
            try { value = property.GetValue(character); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { continue; }
            if (value is Animation or YukkuriMovieMaker.Commons.IAnimatable) yield return value;
            else if (value is System.Collections.IEnumerable list and not string)
                foreach (var part in list) if (part is YukkuriMovieMaker.Commons.IAnimatable) yield return part;
        }
    }

    // Discover providers through the host's animatable tree. This adds dependencies; it never grants trust to
    // unknown plugin code or certifies its processor/thread safety. Rebuilt with each project description.
    internal static ICacheDependencyProvider[] CaptureDynamicProviders(Scene scene)
    {
        var roots = scene.Scenes.Timelines.Append(scene.Timeline).Distinct().SelectMany(t => t.Items).Cast<object>().ToArray();
        var pending = new Stack<object>(roots.Concat(roots.OfType<IItem>().Select(GetCharacter).OfType<Character>()));
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = new List<ICacheDependencyProvider>();
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (visited.Count > 100_000) throw new InvalidDataException("Dynamic dependency tree exceeds bounds");
            if (value is ICacheDependencyProvider provider)
            {
                if (result.Count >= 4096) throw new InvalidDataException("Too many dependency providers");
                result.Add(provider);
            }
            foreach (var child in value is Character owner ? CharacterParts(owner) : Animatables(value))
                if (child is not null) pending.Push(child);
        }
        return result.ToArray();
    }

    // How a random move serializes (StringEnumConverter): the safety net for one the walk did not reach.
    private const string RandomMoveJson = "\"ランダム移動\"";

    private static readonly string[] IdentitySeededEffects =
    [
        "YukkuriMovieMaker.Project.Effects.RandomDuplicatorEffect",
        "YukkuriMovieMaker.Project.Effects.CrashEffect",
        "YukkuriMovieMaker.Project.Effects.InOutCrashEffect",
        "YukkuriMovieMaker.Project.Effects.RandomLineEffect",
        "YukkuriMovieMaker.Project.Effects.InOutRandomLineEffect",
        "YukkuriMovieMaker.Project.Effects.NoiseEffect",
        "YukkuriMovieMaker.Plugin.Community.Effect.Video.CameraShake.CameraShakeEffect",
        "YukkuriMovieMaker.Plugin.Community.Effect.Video.RectangleGlitchNoise.RectangleGlitchNoiseEffect",
        "YukkuriMovieMaker.Plugin.Community.Effect.Video.StripeGlitchNoise.StripeGlitchNoiseEffect",
        "YukkuriMovieMaker.Plugin.Community.Effect.Video.WaveClipping.WaveClippingEffect",
    ];

    private static bool IsIdentitySeeded(Type type)
    {
        if (IdentitySeededEffects.Contains(type.FullName)) return true;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.FullName == "YukkuriMovieMaker.Project.Effects.RandomEffectBase") return true;
        return false;
    }

    // The animatable parts YMM4 itself enumerates (the protected GetAnimatables of items, effects and parameters).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo?> animatables = new();

    private static IEnumerable<object?> Animatables(object value)
    {
        var method = animatables.GetOrAdd(value.GetType(), static type =>
            type.GetMethod("GetAnimatables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } found
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(found.ReturnType) ? found : null);
        return method?.Invoke(value, null) is System.Collections.IEnumerable children ? children.Cast<object?>() : [];
    }

    internal static bool IsMidi(string path) =>
        Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase);

    // The YMMSettings values the drawing model holds (TryDescribe), as one text. KeyDependencyTracker compares it when
    // another property of YMMSettings changes.
    internal static string DrawingSettings()
    {
        var settings = SettingsBase<YMMSettings>.Default;
        return $"{settings.GetZoomMode()}|{settings.GetMFSourceReaderMode2()}|{settings.GetVoiceUpsamplingMode()}";
    }

    internal static string[] FileTypes() =>
        SettingsBase<FileSettings>.Default.FileExtensions.Select(extension => $"{extension.Extention}={extension.FileType}").ToArray();

    // Splits the serialized model into the part every frame depends on (everything but timeline items), the
    // other timelines (only read by frames with a scene item), and one hash per root timeline item.
    private static FrameDependencyIndex DescribeFrames(FrameModelSplit.Parts split, IItem[] rootItems,
        List<(SortedSet<string> Paths, SortedSet<string> Resources, bool Uncacheable, bool Session)> rootDependencies,
        SortedSet<string> characterPaths, SortedSet<string> nestedPaths, SortedSet<string> nestedResources, bool nestedUncacheable,
        bool nestedSession)
    {
        var (global, nested, texts) = (split.Global, split.Nested, split.RootItems);
        if (texts.Length != rootItems.Length) throw new InvalidDataException("Serialized root items do not match the timeline");
        var entries = new FrameDependencyIndex.Entry[rootItems.Length];
        for (int i = 0; i < rootItems.Length; i++)
        {
            var item = rootItems[i];
            string text = texts[i];
            string identity = item.GetType().FullName + "\n" + text + "\n" + string.Join("\n", rootDependencies[i].Resources);
            // Scene items render other timelines; audio spectrum shapes read the timeline's or a scene's audio.
            bool wide = item is SceneItem || text.Contains("AudioSpectrum", StringComparison.Ordinal);
            entries[i] = new(item.Frame, item.Length, item is TransitionItem, wide, FrameDependencyIndex.Hash(identity),
                rootDependencies[i].Paths.ToArray(), rootDependencies[i].Uncacheable, rootDependencies[i].Session);
        }
        return new FrameDependencyIndex(FrameDependencyIndex.Hash(global), characterPaths,
            FrameDependencyIndex.Hash(nested + "\n" + string.Join("\n", nestedResources)), nestedPaths, entries, nestedUncacheable, nestedSession);
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

    // `fonts`: the files of fonts are also added there (they are read by DirectWrite, not by a source reader).
    private static void AddResource(TimelineResource resource, ISet<string> paths, ISet<string> resources, ISet<string>? fonts)
    {
        resources.Add(resource.Key);
        if (Uri.TryCreate(resource.Key, UriKind.Absolute, out var uri) && uri.IsFile)
            AddPath(uri.LocalPath, paths);
        else if (resource.ResourceType == TimelineResourceType.Font)
            AddFont(resource.Key["font://".Length..], paths, resources, fonts);
        else if (resource.ResourceType is TimelineResourceType.Video or TimelineResourceType.Image or TimelineResourceType.Audio or TimelineResourceType.CustomVoice or TimelineResourceType.Tachie)
            AddPath(resource.Key, paths);
    }

    private static void AddFont(string name, ISet<string> paths, ISet<string> resources, ISet<string>? fonts)
    {
        var (face, files) = ResolveFont(name);
        resources.Add(face);
        foreach (string file in files)
        {
            AddPath(file, paths);
            fonts?.Add(Path.GetFullPath(file));
        }
    }

    // YMM4 draws a font name through its font settings (TextFormatDescription, TextSource, JimakuSource, 4.56.1.0):
    // the first of SystemFonts then CustomFonts with that name, else Arial, gives a family, weight, style and stretch
    // that DirectWrite finds in the system font collection (bold and italic pick other faces of the family). The
    // mapping goes into the key and the family's files are dependencies. A family DirectWrite does not have would be
    // drawn by font fallback, which is not followed: the item is rendered normally.
    internal static (string Face, string[] Files) ResolveFont(string name)
    {
        var settings = SettingsBase<FontSettings>.Default;
        var font = settings.SystemFonts.Concat(settings.CustomFonts).FirstOrDefault(f => f.FontName == name) ?? new Font();
        string family = font.CanonicalFontName ?? string.Empty;
        string face = $"fontface://{name}\n{family}|{(int)font.CanonicalFontWeight}|{(int)font.CanonicalFontStyle}|{(int)font.CanonicalFontStretch}";
        return (face, FamilyFiles(family));
    }

    // Fonts named by an item's text decorations and control tags (TextSource, JimakuSource, 4.56.1.0), including tags
    // that the asterisk word sets write. A name the font settings do not have is drawn in the item's font (already a
    // resource), so only names they have are returned.
    private static IEnumerable<string> DecorationFonts(IItem item, string[] replacements)
    {
        var (text, decorations, font) = item switch
        {
            TextItem textItem => (textItem.Text, textItem.Decorations, textItem.Font),
            VoiceItem voice => (voice.Serif, voice.Decorations, voice.JimakuVisibility == JimakuVisibility.Custom ? voice.Font : voice.Character?.Font),
            _ => (null, null, null),
        };
        if (string.IsNullOrEmpty(text)) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decoration in decorations ?? ImmutableList<TextDecoration>.Empty)
            if (decoration?.Font is { Length: > 0 } name) names.Add(name);
        foreach (string source in replacements.Prepend(text))
            foreach (var decoration in ControlTagParser.Parse(source, ImmutableList<TextDecoration>.Empty, 1.0, font ?? string.Empty, false, false).decorations)
                if (decoration.Font is { Length: > 0 } name) names.Add(name);
        names.Remove(font ?? string.Empty);
        if (names.Count == 0) return [];
        var settings = SettingsBase<FontSettings>.Default;
        var known = settings.SystemFonts.Concat(settings.CustomFonts).Select(entry => entry.FontName).ToHashSet(StringComparer.Ordinal);
        return names.Where(known.Contains).Order(StringComparer.Ordinal).ToArray();
    }

    // The user dictionary's asterisk word sets rewrite the text that text items and subtitles draw
    // (TextHelper.ApplyAsterisk, 4.56.1.0): a resource of those items (null when none is enabled), and the texts they
    // write (which can hold control tags). Null when the dictionary cannot be read (UserDictionary is internal).
    internal static (string? Resource, string[] Replacements)? AsteriskWordSets()
    {
        if (UserDictionary() is not { } dictionary
            || dictionary.GetType().GetProperty("AsteriskWordSets", BindingFlags.Public | BindingFlags.Instance) is not { } property)
            return null;
        var sets = (property.GetValue(dictionary) as IEnumerable<WordSet> ?? [])
            .Where(set => set is { IsEnabled: true } && !string.IsNullOrEmpty(set.From)).ToArray();
        if (sets.Length == 0) return (null, []);
        string identity = string.Join("\n", sets.Select(set =>
            $"{set.IsRegex}|{set.IgnoreCase}|{set.From.Length}|{set.From}|{(set.To ?? string.Empty).Length}|{set.To}"));
        return ("asterisk://" + FrameDependencyIndex.Hash(identity), sets.Select(set => set.To ?? string.Empty).ToArray());
    }

    // What the tracker follows for AsteriskWordSets: the dictionary and its word sets.
    internal static IEnumerable<object> AsteriskSources()
    {
        if (UserDictionary() is not { } dictionary) return [];
        var sets = dictionary.GetType().GetProperty("AsteriskWordSets", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dictionary) as IEnumerable<WordSet>;
        return new object[] { dictionary }.Concat((sets ?? []).OfType<object>()).ToArray();
    }

    private static object? UserDictionary() =>
        typeof(Scene).Assembly.GetType("YukkuriMovieMaker.KanjiToYomi.UserDictionary") is { } type
            ? typeof(SettingsBase<>).MakeGenericType(type).GetProperty("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
            : null;

    // The files only change when fonts are installed or removed, so a family's are kept for a short time; a font
    // file whose content changes is still caught by its fingerprint.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[]? Files, long Until)> familyFiles = new(StringComparer.Ordinal);
    private const long FamilyFilesMilliseconds = 30_000;

    private static string[] FamilyFiles(string family)
    {
        long now = Environment.TickCount64;
        if (!familyFiles.TryGetValue(family, out var known) || now >= known.Until)
        {
            known = (FindFamilyFiles(family), now + FamilyFilesMilliseconds);
            if (familyFiles.Count > 4096) familyFiles.Clear();
            familyFiles[family] = known;
        }
        return known.Files ?? throw new NotSupportedException("Unresolved font");
    }

    // Null when the family is not in the system collection or a file of it is not local.
    private static string[]? FindFamilyFiles(string family)
    {
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var collection = factory.GetSystemFontCollection(false);
        if (!collection.FindFamilyName(family, out int index)) return null;
        using var fonts = collection.GetFontFamily(index);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fonts.FontCount; i++)
        {
            using var font = fonts.GetFont(i);
            using var face = font.CreateFontFace();
            foreach (var file in face.GetFiles())
                using (file)
                {
                    string? path = LocalPath(file);
                    if (path is null) return null;
                    files.Add(path);
                }
        }
        return files.Count == 0 ? null : [.. files];
    }

    private static string? LocalPath(IDWriteFontFile file)
    {
        using var loader = file.Loader as ComObject;
        using var local = loader?.QueryInterfaceOrNull<IDWriteLocalFontFileLoader>();
        if (local is null) return null;
        byte[] key = file.GetReferenceKey().ToArray();
        nint keyMemory = Marshal.AllocHGlobal(Math.Max(1, key.Length));
        try
        {
            Marshal.Copy(key, 0, keyMemory, key.Length);
            int length = local.GetFilePathLengthFromKey(keyMemory, key.Length) + 1;
            nint pathMemory = Marshal.AllocHGlobal(length * sizeof(char));
            try
            {
                local.GetFilePathFromKey(keyMemory, key.Length, pathMemory, length);
                return Marshal.PtrToStringUni(pathMemory);
            }
            finally { Marshal.FreeHGlobal(pathMemory); }
        }
        finally { Marshal.FreeHGlobal(keyMemory); }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static bool Bypass(string message, out string reason) { reason = message; return false; }
}
