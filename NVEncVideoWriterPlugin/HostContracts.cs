using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NVEncVideoWriterPlugin;

// The parts of YMM4's code each cache feature relies on, as read in YMM4 4.56.1.0 (CLAUDE_HANDOFF.md). On a YMM4
// build that is not one of the read builds, a feature is used only when every one of its parts has the same
// fingerprint (HostFingerprint) as in a read build. Besides the hooked types, a part list contains "witnesses":
// every type whose code uses what the feature's rules are about (all code that reads other scenes, depends on
// the render usage, decides which items a frame draws, ...), found the same way they were found when the code was
// read. New code of that kind therefore changes the list and turns the feature off until it is read again.
// This repeats the checks that were made by reading the code; it does not prove more than they did.
internal static partial class HostContracts
{
    internal const string Core = "core";
    internal const string Preview = "preview";
    internal const string SelectionRects = "selection-rects";
    internal const string WrappedSources = "wrapped-sources";
    internal const string RulerBars = "ruler-bars";
    internal const string DecoderPrefix = "decoder:";
    internal const string Missing = "missing";

    // Types: "Assembly|Type" (exactly that type), "Assembly|Type+" (the type and its nested types) or
    // "Assembly|Type::Method" (that type's methods of that name only).
    // Assemblies: every type of those assemblies. Witnesses: types of the given assemblies (optionally only those
    // whose name matches TypePattern) that name something matching Pattern (HostFingerprint.References), or with
    // Defines, that define a method matching it.
    internal sealed record Rule(string Feature, string[] Requires, string[] Types, string[] Assemblies, Witness[] Witnesses);

    internal sealed record Witness(string Pattern, string[] Assemblies, string? TypePattern = null, bool Defines = false);

    // Built-in items, effects and parameters the cache renders come from these assemblies (FrameCacheKey bypasses
    // every other "$type"), so code elsewhere cannot take part in a cached frame.
    private static readonly string[] model = ["YukkuriMovieMaker", "YukkuriMovieMaker.Plugin"];
    private const string RenderNamespaces = @"^YukkuriMovieMaker\.(Player\.Video|Project\.Items|Project\.Effects|Shape|Brush|Plugin\.Brush|Transition)(\.|\+|$)";
    // The item editor and the views build their own sources for editing; they do not draw timeline frames. The
    // preview player is the Preview feature's part.
    private const string NotEditorUi = @"^(?!YukkuriMovieMaker\.(ViewModels|Views|ItemEditor)\.)(?!YukkuriMovieMaker\.Player\.TimelineVideoPlayer(\+|$))";
    // Where the items, effects and characters whose files the key fingerprints are defined.
    private const string ModelNamespaces = @"^YukkuriMovieMaker\.(Project|Player\.Video|Shape|Brush|Plugin\.Brush|Plugin\.Effects|Transition)(\.|\+|$)";
    private static readonly string[] decoderAssemblies =
    [
        "YukkuriMovieMaker.Plugin.FileSource.FFmpeg",
        "YukkuriMovieMaker.Plugin.FileSource.MediaFoundation",
        "YukkuriMovieMaker.Plugin.FileSource.WIC",
    ];

    internal static readonly Rule[] Rules =
    [
        new(Core, [],
            [
                // The hooked renderer, its item selection and the per-frame dependency rules (FrameDependencyIndex).
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.Video.TimelineSource+",
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.Video.EffectedItemSource+",
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.Video.IItemPicker",
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.Video.CompositeItemPicker+",
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.Video.TransitionItemPicker+",
                "YukkuriMovieMaker|YukkuriMovieMaker.ItemEditor.TimelineSourceAndDevices+",
                "YukkuriMovieMaker|YukkuriMovieMaker.Project.Scene",
                "YukkuriMovieMaker|YukkuriMovieMaker.Project.Scenes",
                "YukkuriMovieMaker|YukkuriMovieMaker.Project.Timeline",
                "YukkuriMovieMaker|YukkuriMovieMaker.Project.Items.IItem",
                "YukkuriMovieMaker|YukkuriMovieMaker.Project.Items.ItemEx::Contains",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.ITimelineSource",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.TimelineSourceUsage",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.TimelineSourceDescription",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.TimelineItemSourceDescription",
                // The key: the serialized project, time conversion and the settings read while rendering.
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Json.Json",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Commons.FrameTime",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Project.VideoInfo",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Settings.YMMSettings",
            ],
            [],
            [
                // Rendering that differs by usage (ShowOnlyPreviewEffect is the only Playing/Paused difference).
                new(@"^YukkuriMovieMaker\.Player\.Video\.TimelineSourceUsage$", model, NotEditorUi),
                // Reading other scenes or their audio (only scene items and audio spectrum shapes: "wide" frames).
                new(@"^YukkuriMovieMaker\.Player\.Video\.Timeline(Item)?SourceDescription::get_Scenes$", model),
                new(@"^YukkuriMovieMaker\.Player\.Audio\.Items\.SceneSource$", model),
                // Choosing the items of a frame (only TransitionItemPicker looks at another frame).
                new(@"^YukkuriMovieMaker\.Player\.Video\.IItemPicker$", model, NotEditorUi),
                // The files and resources that are fingerprinted for the key.
                new(@"::(GetFiles|GetResources)$", model, ModelNamespaces, Defines: true),
                // Global settings read by rendering code (only those in the key may be read).
                new(@"^YukkuriMovieMaker\.Settings\.\w+::", model, RenderNamespaces),
            ]),
        new(Preview, [Core],
            [
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.TimelineVideoPlayer+",
                "YukkuriMovieMaker|YukkuriMovieMaker.Player.TimelineVideoPlayerRenderTargetResources+",
            ],
            [], []),
        // Item rects are reused: the built-in controllers depend on the model and the frame only (except
        // MeshDeformation, which PreviewUsage excludes).
        new(SelectionRects, [Preview],
            [
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.VideoController+",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Player.Video.VideoEffectController+",
            ],
            [],
            [new(@"^YukkuriMovieMaker\.Player\.Video\.(VideoController|VideoEffectController)::\.ctor$", model)]),
        // Every video source the renderer uses is created by VideoFileSourceFactory and wrapped in
        // CachedVideoFileSource, so a source whose Update cannot be hooked is rejected by the wrapper.
        new(WrappedSources, [Core],
            [
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Plugin.VideoFileSourceFactory+",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Plugin.CachedVideoFileSource+",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Plugin.VideoResource+",
                "YukkuriMovieMaker.Plugin|YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource",
            ],
            [],
            [
                new(@"::CreateVideoFileSource$", model),
                new(@"^YukkuriMovieMaker\.Plugin\.CachedVideoFileSource::\.ctor$", model),
            ]),
        new(RulerBars, [Core],
            [
                "YukkuriMovieMaker|YukkuriMovieMaker.Views.TimelineScaleView",
                "YukkuriMovieMaker|YukkuriMovieMaker.ViewModels.TimelineScaleViewModel",
            ],
            [], []),
        // Whether a decoder holds the requested frame (FrameRenderReadiness) depends on its whole assembly.
        .. decoderAssemblies.Select(assembly => new Rule(DecoderPrefix + assembly, [Core], [], [assembly], [])),
    ];

    // Feature -> ("Assembly|Type" -> fingerprint, or Missing for a listed type the build does not have).
    internal static Dictionary<string, SortedDictionary<string, string>> Describe(string hostDirectory)
    {
        var fingerprints = new Dictionary<string, HostFingerprint>(StringComparer.Ordinal);
        var references = new Dictionary<(string Assembly, string Type), HashSet<string>>();
        try
        {
            HostFingerprint? Open(string assembly)
            {
                if (fingerprints.TryGetValue(assembly, out var open)) return open;
                string path = Path.Combine(hostDirectory, assembly + ".dll");
                if (!File.Exists(path)) return null;
                var fingerprint = new HostFingerprint(path);
                if (fingerprint.AssemblyName != assembly)
                {
                    fingerprint.Dispose();
                    throw new InvalidDataException($"{path} is {fingerprint.AssemblyName}");
                }
                return fingerprints[assembly] = fingerprint;
            }

            var result = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var rule in Rules)
            {
                var members = new SortedDictionary<string, string>(StringComparer.Ordinal);
                void Add(string assembly, string type) =>
                    members[assembly + "|" + type] = Open(assembly)?.Hash(type) ?? Missing;

                foreach (var spec in rule.Types)
                {
                    var (assembly, type) = Split(spec);
                    if (type.Split("::") is [var declaring, var method])
                    {
                        var methods = Open(assembly)?.MemberHashes(declaring)
                            .Where(member => Regex.IsMatch(member.Key, $@"^method [^(]*\b{Regex.Escape(method)}(<\d+>)?\("))
                            .Select(member => member.Key + "=" + member.Value).ToArray() ?? [];
                        members[spec] = methods.Length == 0 ? Missing : HostFingerprint.HashText(string.Join("\n", methods));
                        continue;
                    }
                    bool nested = type.EndsWith('+');
                    type = type.TrimEnd('+');
                    Add(assembly, type);
                    if (nested && Open(assembly) is { } fingerprint)
                        foreach (var inner in fingerprint.TypeNames.Where(name => name.StartsWith(type + "+", StringComparison.Ordinal)))
                            Add(assembly, inner);
                }
                foreach (var assembly in rule.Assemblies)
                {
                    if (Open(assembly) is not { } fingerprint) members[assembly + "|*"] = Missing;
                    else foreach (var type in fingerprint.TypeNames) Add(assembly, type);
                }
                foreach (var witness in rule.Witnesses)
                {
                    var pattern = new Regex(witness.Pattern, RegexOptions.CultureInvariant);
                    var typePattern = witness.TypePattern is null ? null : new Regex(witness.TypePattern, RegexOptions.CultureInvariant);
                    foreach (var assembly in witness.Assemblies)
                    {
                        if (Open(assembly) is not { } fingerprint) { members[assembly + "|*"] = Missing; continue; }
                        foreach (var type in fingerprint.TypeNames)
                        {
                            if (typePattern?.IsMatch(type) == false) continue;
                            if (!references.TryGetValue((assembly, type), out var named))
                                references[(assembly, type)] = named = fingerprint.References(type)!;
                            if (named.Any(name => pattern.IsMatch(name) && (!witness.Defines || name.StartsWith(type + "::", StringComparison.Ordinal))))
                                Add(assembly, type);
                        }
                    }
                }
                result[rule.Feature] = members;
            }
            return result;
        }
        finally
        {
            foreach (var fingerprint in fingerprints.Values) fingerprint.Dispose();
        }
    }

    private static (string Assembly, string Type) Split(string spec)
    {
        int bar = spec.IndexOf('|');
        return (spec[..bar], spec[(bar + 1)..]);
    }

    internal sealed record Baseline(string Version, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Features);

    // Baseline: the read build this one matches (null when even Core differs from every read build).
    // Features: the features whose parts are unchanged (and whose required features are). Problems: per
    // feature that is off, what differs from that build.
    internal sealed record Evaluation(string? Baseline, IReadOnlySet<string> Features, IReadOnlyDictionary<string, string> Problems)
    {
        internal bool Has(string feature) => Features.Contains(feature);
    }

    internal static Evaluation Evaluate(IReadOnlyDictionary<string, SortedDictionary<string, string>> current) =>
        Evaluate(current, Baselines);

    internal static Evaluation Evaluate(IReadOnlyDictionary<string, SortedDictionary<string, string>> current, IEnumerable<Baseline> baselines)
    {
        Evaluation? best = null;
        foreach (var baseline in baselines)
        {
            var features = new HashSet<string>(StringComparer.Ordinal);
            var problems = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var rule in Rules)
            {
                string? problem = rule.Requires.FirstOrDefault(required => !features.Contains(required)) is { } missing
                    ? $"{missing} が使えないため"
                    : !baseline.Features.TryGetValue(rule.Feature, out var expected) ? $"{baseline.Version} の記録がありません"
                    : !current.TryGetValue(rule.Feature, out var actual) ? "検査できませんでした"
                    : Difference(expected, actual);
                if (problem is null) features.Add(rule.Feature);
                else problems[rule.Feature] = problem;
            }
            var evaluation = new Evaluation(features.Contains(Core) ? baseline.Version : null, features, problems);
            if (evaluation.Baseline is not null) return evaluation;
            best ??= evaluation;
        }
        return best ?? new Evaluation(null, new HashSet<string>(), new Dictionary<string, string> { [Core] = "検証済みの版の記録がありません" });
    }

    private sealed record CachedVerdict(string Key, string? Baseline, string[] Features, Dictionary<string, string> Problems);

    // Evaluate(Describe(hostDirectory)), remembered in cacheFile for the same host binaries and plugin build.
    internal static Evaluation EvaluateCached(string hostDirectory, string cacheFile, string pluginIdentity)
    {
        string key = pluginIdentity + ";" + string.Join(";", Directory.GetFiles(hostDirectory, "YukkuriMovieMaker*.dll")
            .Order(StringComparer.OrdinalIgnoreCase).Select(path => $"{Path.GetFileName(path)}={Identity(path)}"));
        try
        {
            if (File.Exists(cacheFile) && JsonSerializer.Deserialize<CachedVerdict>(File.ReadAllText(cacheFile)) is { } cached && cached.Key == key)
                return new Evaluation(cached.Baseline, cached.Features.ToHashSet(StringComparer.Ordinal),
                    new SortedDictionary<string, string>(cached.Problems, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        var evaluation = Evaluate(Describe(hostDirectory));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            string temporary = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CachedVerdict(key, evaluation.Baseline,
                evaluation.Features.Order(StringComparer.Ordinal).ToArray(), evaluation.Problems.ToDictionary(p => p.Key, p => p.Value))));
            File.Move(temporary, cacheFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return evaluation;

        static string Identity(string path)
        {
            try { return HostFingerprint.ReadMvid(path).ToString("N"); }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException) { return "native:" + new FileInfo(path).Length; }
        }
    }

    // Null when equal; otherwise the first few differences, by type name.
    internal static string? Difference(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        var changed = expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)
            .Where(key => !expected.TryGetValue(key, out var a) || !actual.TryGetValue(key, out var b) || a != b)
            .Select(key => (expected.ContainsKey(key) ? actual.ContainsKey(key) && actual[key] != Missing ? "変更" : "削除" : "追加")
                + " " + key[(key.IndexOf('|') + 1)..])
            .ToArray();
        return changed.Length == 0 ? null : string.Join(", ", changed.Take(4)) + (changed.Length > 4 ? $" ほか {changed.Length - 4} 件" : string.Empty);
    }
}
