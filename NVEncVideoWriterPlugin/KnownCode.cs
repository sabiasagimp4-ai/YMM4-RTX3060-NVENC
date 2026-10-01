using System.IO;
using System.Reflection;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace NVEncVideoWriterPlugin;

// Code the cache may key frames with, besides YMM4's own: the bundled Community plugin's namespaces that were read
// for this (4.56.1.0, by its MVID), and plugin assemblies the user trusts in the settings. A trusted plugin is
// assumed to draw its item from the item's parameters and time only, like an After Effects plugin; its assembly
// MVID goes into every key, so a rebuilt or updated plugin starts over.
internal sealed class KnownCode
{
    internal const string CommunityAssembly = "YukkuriMovieMaker.Plugin.Community";

    // The Community assembly of YMM4 4.56.1.0 whose code was read (docs/HOST_CONTRACTS.md).
    private static readonly Guid AuditedCommunity = new("ac765de8-d44f-44f1-a094-961becf4d22e");

    // Namespaces (after "YukkuriMovieMaker.Plugin.Community.") that draw only their own item from its parameters and
    // time: no other items, scenes or audio, no files they do not report, no clock, no state kept from the frames
    // drawn before. CameraShake, RectangleGlitchNoise, StripeGlitchNoise and WaveClipping seed with the effect's
    // identity and are keyed by it (FrameCacheKey.IdentitySeeds). Left out: AfterImage and MotionBlur (draw from
    // the frames drawn before), AudioVolume (audio), ArrangeGroupItems, RadialArrangeGroupItems, TilingGroupItems,
    // Container and the Scene brush (other items or scenes), OpenFx (external binaries), Lut and GradientMap (files
    // not reported), ShuffleText, ShuffleTextInOut and NumberText (fonts not resolved for the key), and
    // DirectionalColorKey, FillSameground, FillSametype, ParticleOutput, Particlize, PuppetDeformation,
    // VectorFieldWarp and Pen (not read in full).
    internal static readonly string[] VerifiedCommunity =
    [
        "Effect.Video.AlphaMask", "Effect.Video.AmbientOcclusion", "Effect.Video.AnisotropicKuwahara", "Effect.Video.Binarization",
        "Effect.Video.Bloom", "Effect.Video.BlurMap", "Effect.Video.CameraShake", "Effect.Video.Caustics", "Effect.Video.CircularBlur",
        "Effect.Video.ColorBlindness", "Effect.Video.ColorCorrection", "Effect.Video.ColorShift", "Effect.Video.CrossFilter",
        "Effect.Video.CrossHatchShading", "Effect.Video.Dithering", "Effect.Video.EdgeDetection", "Effect.Video.EdgeGlow",
        "Effect.Video.EdgeTrimming", "Effect.Video.FacetedGlass", "Effect.Video.FishEyeLens", "Effect.Video.Flip", "Effect.Video.Fog",
        "Effect.Video.GodRay", "Effect.Video.HeatHaze", "Effect.Video.InOutCrop", "Effect.Video.InnerOutline", "Effect.Video.Kaleidoscope",
        "Effect.Video.LensBlur", "Effect.Video.LineHighlight", "Effect.Video.LongShadow", "Effect.Video.LuminanceKey",
        "Effect.Video.LuminanceMask", "Effect.Video.NtscComposite", "Effect.Video.OutputBranch", "Effect.Video.OutputChannelRouter",
        "Effect.Video.OutputComposite", "Effect.Video.OutputMapComposite", "Effect.Video.OutputSwitch", "Effect.Video.PageTurn",
        "Effect.Video.PartialOutline", "Effect.Video.PerspectiveShadow", "Effect.Video.PixelSort", "Effect.Video.Radiance",
        "Effect.Video.RatioCrop", "Effect.Video.RectangleGlitchNoise", "Effect.Video.ReelSpin", "Effect.Video.ReflectionAndExtrusion",
        "Effect.Video.Ripple", "Effect.Video.SpiralTransform", "Effect.Video.SpreadPageTurn", "Effect.Video.Stretch",
        "Effect.Video.StripeGlitchNoise", "Effect.Video.ThreeDimensional", "Effect.Video.TrimMargin", "Effect.Video.Tritone",
        "Effect.Video.UnidirectionalBlur", "Effect.Video.VignetteBlur", "Effect.Video.Wave", "Effect.Video.WaveClipping",
        "Effect.Video.ZoomPixel",
        "Shape.LensFlare", "Shape.PdfPage",
        "Brush.Pattern", "Brush.Rainbow",
        "Transition.PageTurn", "Transition.Pixelize", "Transition.ReelSpin", "Transition.SpreadPageTurn",
    ];

    private static volatile HashSet<string> trusted = new(StringComparer.OrdinalIgnoreCase);
    private static long generation;

    // Assembly names (settings); changing them re-describes every project.
    internal static IReadOnlyCollection<string> Trusted
    {
        get => trusted;
        set
        {
            var next = new HashSet<string>(value.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
            if (next.SetEquals(trusted)) return;
            trusted = next;
            Interlocked.Increment(ref generation);
        }
    }

    internal static long Generation => Interlocked.Read(ref generation);

    private readonly Assembly? community;
    private readonly HashSet<string> loadedTrusted;

    // For the key: the audited Community build when used, and the trusted assemblies loaded (name and MVID).
    internal string Identity { get; }

    private KnownCode(Assembly? community, IEnumerable<Assembly> trustedAssemblies)
    {
        this.community = community;
        var identities = trustedAssemblies
            .Select(assembly => $"{assembly.GetName().Name}={assembly.ManifestModule.ModuleVersionId:D}")
            .Order(StringComparer.Ordinal).ToArray();
        loadedTrusted = new HashSet<string>(trustedAssemblies.Select(assembly => assembly.GetName().Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        Identity = (community is null ? "community:-" : "community:" + AuditedCommunity.ToString("D")) + "|trusted:" + string.Join(";", identities);
    }

    // What is known now (one look at the loaded assemblies per project description).
    internal static KnownCode Capture()
    {
        var names = trusted;
        Assembly? audited = null;
        var trustedAssemblies = new List<Assembly>();
        string? hostDirectory = Path.GetDirectoryName(typeof(Scene).Assembly.Location);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            string? name = assembly.GetName().Name;
            if (name is null) continue;
            if (name == CommunityAssembly && assembly.ManifestModule.ModuleVersionId == AuditedCommunity
                && FrameCacheKey.IsBundledPluginAssembly(name, assembly.Location, hostDirectory))
                audited = assembly;
            else if (names.Contains(name) && assembly != typeof(Scene).Assembly && assembly != typeof(CacheProvider).Assembly)
                trustedAssemblies.Add(assembly);
        }
        return new KnownCode(audited, trustedAssemblies);
    }

    internal bool Knows(Type? type)
    {
        if (type is null) return false;
        var assembly = type.Assembly;
        if (assembly == typeof(Scene).Assembly || assembly == typeof(CacheProvider).Assembly) return true;
        if (community is not null && assembly == community) return IsVerifiedCommunity(type.FullName);
        return loadedTrusted.Contains(assembly.GetName().Name ?? string.Empty) && !ExternalBinaries(type.FullName);
    }

    // A "$type" value's type and assembly names.
    internal bool Knows(string typeName, string assemblyName)
    {
        if (assemblyName is "YukkuriMovieMaker" or "YukkuriMovieMaker.Plugin") return true;
        if (assemblyName == CommunityAssembly) return community is not null && IsVerifiedCommunity(typeName);
        return loadedTrusted.Contains(assemblyName) && !ExternalBinaries(typeName);
    }

    private static bool IsVerifiedCommunity(string? typeName)
    {
        const string prefix = CommunityAssembly + ".";
        if (typeName is null || !typeName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string rest = typeName[prefix.Length..];
        return VerifiedCommunity.Any(space => rest.StartsWith(space + ".", StringComparison.Ordinal));
    }

    // Plugins that run other binaries (OpenFX plugins, VST3) are never keyed, trusted or not.
    private static bool ExternalBinaries(string? typeName) => typeName is not null
        && (typeName.Contains(".OpenFx.", StringComparison.Ordinal) || typeName.Contains(".Vst3.", StringComparison.Ordinal));

    // The plugin assemblies a user added (shown in the settings to trust), by name, with what they provide.
    internal static IReadOnlyList<(string Assembly, string Provides)> ExternalPlugins()
    {
        var provided = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(Type type, string what)
        {
            string? name = type.Assembly.GetName().Name;
            if (name is null || type.Assembly == typeof(Scene).Assembly || type.Assembly == typeof(CacheProvider).Assembly) return;
            if (!provided.TryGetValue(name, out var set)) provided[name] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(what);
        }
        try
        {
            foreach (var plugin in PluginLoader.UserPlugins) Add(plugin.GetType(), plugin.Name);
            foreach (var type in PluginLoader.UserVideoEffects) Add(type, type.Name);
            foreach (var type in PluginLoader.UserAudioEffects) Add(type, type.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
        return provided.Select(pair => (pair.Key, string.Join("、", pair.Value.Take(6)) + (pair.Value.Count > 6 ? " ほか" : string.Empty))).ToArray();
    }
}
