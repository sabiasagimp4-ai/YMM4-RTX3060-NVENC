using System.Reflection;

namespace NVEncVideoWriterPlugin;

// Which cache features the running YMM4 build supports (see HostContracts). Basis names what that was decided
// from: a build whose code was read, or the read build whose parts an unread build shares.
// VerifiedDecoders null: the decoders' own shapes decide (the builds that were read).
internal sealed record HostFeatures(string Basis, bool Preview, bool SelectionRects, bool WrappedSources, bool RulerBars,
    IReadOnlySet<string>? VerifiedDecoders)
{
    internal bool SimpleTachie { get; init; }
    // Identity-seeded randomness is drawn from the model objects the key names (HostContracts "identity-random").
    internal bool IdentityRandom { get; init; }
    internal bool LipSync { get; init; }
    internal bool AnimationTachie { get; init; }
    internal bool PsdTachie { get; init; }
    // YMM4 4.56.1.0, the build whose renderer, preview player, controllers and video sources were read.
    internal static readonly Guid ReadBuild = Guid.Parse("23e5b5b5-adcf-43b7-b976-b6b63f8dadea");
    // Its video factory/wrapper/resource and call-site witnesses match the read 4.56.1.0 contract.
    private static readonly Guid OlderWrappedBuild = Guid.Parse("5c07056d-022e-4d0f-a83d-ae0fa3b393f5");
    private static HostFeatures? decided;
    private static Assembly? decidedHost;

    internal bool DecoderVerified(Type type) => DecoderVerified(type.Assembly.GetName().Name ?? string.Empty);

    internal bool DecoderVerified(string assemblyName) => VerifiedDecoders?.Contains(assemblyName) ?? true;

    // Before the cache is installed on a build that was not read: what its contracts allow.
    internal static void Decide(Assembly host, HostFeatures features)
    {
        decided = features;
        decidedHost = host;
    }

    internal static HostFeatures For(Assembly host)
    {
        if (ReferenceEquals(decidedHost, host) && decided is { } features) return features;
        return host.ManifestModule.ModuleVersionId == ReadBuild
            ? new("4.56.1.0", true, true, true, true, null) { IdentityRandom = true, SimpleTachie = true, LipSync = true, AnimationTachie = true, PsdTachie = true }
            // 4.55.1.1 also wraps every video source, but its selection rects and ruler bars are not enabled.
            : new(host.GetName().Version?.ToString() ?? "?", true, false,
                host.ManifestModule.ModuleVersionId == OlderWrappedBuild, false, null);
    }
}
