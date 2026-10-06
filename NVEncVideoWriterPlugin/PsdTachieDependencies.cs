using System.Collections;
using System.Collections.Concurrent;
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
    internal static readonly Guid ReadBuild = new("7dc41b6b-858d-4af0-949f-c8c4e7f9dede");
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal sealed record Input(Character Character, string Path, object Settings, string Json);
    private sealed record Witness(Input[] Inputs);
    private sealed record LoadedContent(Task<string> Hash);
    private static readonly ConditionalWeakTable<object, LoadedContent> loadedFiles = new();
    private sealed record NormalizedWitness(WeakReference<object> Root, string SharedJson, object Normalized, string ExpectedJson);
    private static readonly ConditionalWeakTable<string, Witness> models = new();
    private static readonly ConditionalWeakTable<object, NormalizedWitness> normalized = new();

    private sealed record ModuleName(string? Value);
    private static readonly ConditionalWeakTable<Assembly, ModuleName> moduleNames = new();
    private static string? NameOf(Assembly assembly) => moduleNames.GetValue(assembly, static value => new(value.GetName().Name)).Value;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> settingsProperties = new();
    private static PropertyInfo[] PropertiesOf(Type type) => settingsProperties.GetOrAdd(type,
        static value => value.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    private static readonly (string Name, Guid Mvid)[] auxiliaryModules = [
        ("YukkuriMovieMaker.Plugin.FileSource.Psd", new("277031aa-0de0-415b-a9df-12a390227ec4")),
        ("PsdParser", new("d16c5a72-6eff-48f4-8735-0e5ce6ec73df")) ];

    private static long assemblyGeneration;
    private sealed record ModuleVerdict(long Generation, bool Valid);
    private sealed class ModuleState { internal ModuleVerdict? Verdict; }
    private static readonly ConditionalWeakTable<Assembly, ModuleState> moduleStates = new();
    static PsdTachieDependencies() => AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Interlocked.Increment(ref assemblyGeneration);

    internal static bool Verified(Type? plugin)
    {
        if (plugin?.FullName != PluginName || NameOf(plugin.Assembly) != AssemblyName
            || plugin.Assembly.ManifestModule.ModuleVersionId != ReadBuild || !HostFeatures.For(typeof(Scene).Assembly).PsdTachie
            || !FrameCacheKey.IsBundledPluginAssembly(AssemblyName, plugin.Assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location))) return false;
        // Assemblies and module metadata are immutable. Invalidate on every new loaded assembly,
        // including duplicate-name loads; collectible dependencies cannot use this fast path.
        long generation = Interlocked.Read(ref assemblyGeneration);
        var state = moduleStates.GetValue(plugin.Assembly, static _ => new());
        if (Volatile.Read(ref state.Verdict) is { } verdict && verdict.Generation == generation) return verdict.Valid;
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        string directory = Path.GetDirectoryName(typeof(Scene).Assembly.Location)!;
        foreach (var (name, mvid) in auxiliaryModules)
        {
            var candidates = loaded.Where(candidate => NameOf(candidate) == name).Take(2).ToArray();
            if (candidates.Length > 1) return false;
            var assembly = candidates.FirstOrDefault() ?? Assembly.LoadFrom(Path.Combine(directory, name + ".dll"));
            if (assembly.IsCollectible || assembly.ManifestModule.ModuleVersionId != mvid
                || !string.Equals(Path.GetFullPath(assembly.Location), Path.Combine(directory, name + ".dll"), StringComparison.OrdinalIgnoreCase)) return false;
        }
        Volatile.Write(ref state.Verdict, new(generation, true));
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
    // A fresh bounded witness reads plain non-notifying children on every validation. Only JSON
    // encoding is reused. Encode captured scalar values, never reread mutable objects after the scan.
    private static readonly string[] roots = ["Presets", "MouthAnimations", "MouthVowelAnimations", "EyeAnimations"];
    private sealed class SnapshotState
    {
        internal readonly object Gate = new();
        internal long Generation, EncodedGeneration = -1;
        internal object?[]? Tokens;
        internal string? Json;
    }
    private static readonly ConditionalWeakTable<object, SnapshotState> snapshots = new();
    private static bool SameToken(object? a, object? b) => ReferenceEquals(a, b)
        || a is string text && b is string other && string.Equals(text, other, StringComparison.Ordinal)
        || a is double number && b is double second && BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits(second)
        || a is int count && b is int otherCount && count == otherCount;

    internal static string Snapshot(object value)
    {
        var assembly = value.GetType().Assembly;
        if (value.GetType().FullName != AssemblyName + ".PsdFileSettings" || assembly.ManifestModule.ModuleVersionId != ReadBuild)
            throw new NotSupportedException("Unaudited PSD settings");
        var state = snapshots.GetValue(value, static settings =>
        {
            var created = new SnapshotState();
            ((System.ComponentModel.INotifyPropertyChanged)settings).PropertyChanged += (_, _) => Interlocked.Increment(ref created.Generation);
            return created;
        });
        lock (state.Gate)
        {
            long generation = Interlocked.Read(ref state.Generation);
            var tokens = new List<object?>();
            int remaining = 65536, nodes = 4096;
            void Visit(object? part, int depth)
            {
                if (depth > 5 || --nodes < 0) throw new InvalidDataException("PSD settings exceed the inspection bound");
                tokens.Add(part);
                if (part is null) return;
                if (part is string text) { if (text.Length > 4096 || (remaining -= text.Length) < 0) throw new InvalidDataException("PSD layer text exceeds the inspection bound"); return; }
                if (part is double number) { if (!double.IsFinite(number)) throw new InvalidDataException("Non-finite PSD animation"); return; }
                if (part is IEnumerable list)
                {
                    var listType = part.GetType();
                    if (!listType.IsGenericType || listType.GetGenericTypeDefinition() != typeof(System.Collections.Immutable.ImmutableList<>))
                        throw new InvalidDataException("Unknown PSD collection type");
                    int slot = tokens.Count, count = 0; tokens.Add(0);
                    foreach (var child in list) { if (++count > 1024) throw new InvalidDataException("Too many PSD animation layers"); Visit(child, depth + 1); }
                    tokens[slot] = count; return;
                }
                if (part.GetType().Assembly != assembly || part.GetType().FullName is not (
                    AssemblyName + ".PsdEyeAnimation" or AssemblyName + ".PsdMouthAnimation" or AssemblyName + ".PsdVowelMouthAnimation" or AssemblyName + ".PsdPreset"))
                    throw new InvalidDataException("Unknown PSD animation value");
                foreach (var property in PropertiesOf(part.GetType())) Visit(property.GetValue(part), depth + 1);
            }
            foreach (string name in roots) Visit(value.GetType().GetProperty(name)!.GetValue(value), 0);
            if (Interlocked.Read(ref state.Generation) != generation) throw new InvalidDataException("PSD settings changed during inspection");
            if (state.EncodedGeneration == generation && state.Tokens is { } previous && previous.Length == tokens.Count
                && previous.Zip(tokens).All(pair => SameToken(pair.First, pair.Second))) return state.Json!;
            using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using (var writer = new JsonTextWriter(output) { Formatting = Formatting.None })
            {
                int cursor = 0;
                void Write()
                {
                    object? part = tokens[cursor++];
                    if (part is null) { writer.WriteNull(); return; }
                    if (part is string text) { writer.WriteValue(text); return; }
                    if (part is double number) { writer.WriteValue(number); return; }
                    if (part is IEnumerable)
                    {
                        int count = (int)tokens[cursor++]!; writer.WriteStartArray();
                        for (int i = 0; i < count; i++) Write();
                        writer.WriteEndArray(); return;
                    }
                    writer.WriteStartObject();
                    foreach (var property in PropertiesOf(part.GetType())) { writer.WritePropertyName(property.Name); Write(); }
                    writer.WriteEndObject();
                }
                writer.WriteStartObject();
                foreach (string name in roots) { writer.WritePropertyName(name); Write(); }
                writer.WriteEndObject();
                if (cursor != tokens.Count) throw new InvalidDataException("PSD capture encoding did not consume every value");
            }
            string json = output.ToString();
            if (json.Length > 262144) throw new InvalidDataException("PSD settings JSON exceeds the inspection bound");
            state.Tokens = tokens.ToArray(); state.Json = json; state.EncodedGeneration = generation;
            return json;
        }
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
    internal static bool ContainsNative(Scene scene, int frame) => NestedTimelineSources.MayDraw(scene, frame, PluginName);
    // Every PSD tachie the frame drew (in `timelineSource` and the sources inside it: groups, transitions, scenes) holds
    // the keyed file and settings; every one of the root timeline at the frame was drawn.
    internal static bool SafeSource(object timelineSource, Scene scene, int frame, KeyCapture? capture = null)
    {
        try
        {
            if (!NestedTimelineSources.AnyTachie(scene, PluginName)) return true;
            if (!(ReadinessInstalled?.Invoke() == true)) return false;
            var drawn = new List<(TachieItem Item, object Source)>();
            if (!NestedTimelineSources.TryTachieSources(timelineSource, drawn)) return false;
            if (NestedTimelineSources.TachieItems(scene.Timeline).Any(item => NestedTimelineSources.Shows(item, frame)
                && item.Character?.TachieType?.FullName == PluginName && !drawn.Any(pair => ReferenceEquals(pair.Item, item)))) return false;
            foreach (var (item, core) in drawn)
            {
                if (item.Character?.TachieType?.FullName != PluginName) continue;
                if (!Character(item.Character) || !ReferenceEquals(core.GetType().GetField("item", Instance)?.GetValue(core), item)) return false;
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
                        // Hold the immutable parser buffer without a 100 MiB copy; no native object is used
                        // on the pool thread. No frame is admitted while its hash is pending or failed.
                        ReadOnlyMemory<byte> memory = new(bytes.Array!, bytes.Offset, bytes.Count);
                        Func<string> hash = () => Convert.ToHexString(SHA256.HashData(memory.Span));
                        if (ExecutionContext.IsFlowSuppressed()) return new(Task.Run(hash));
                        // Do not retain the host's render/readiness context on the pool thread.
                        using (ExecutionContext.SuppressFlow()) return new(Task.Run(hash));
                    });
                    if (content.Hash.Status != TaskStatus.RanToCompletion
                        || !capture.MatchesLoadedFile(PathOf(item.Character)!, content.Hash.Result)) return false;
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
