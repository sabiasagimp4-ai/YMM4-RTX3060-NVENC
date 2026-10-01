using System.Reflection;

namespace NVEncVideoWriterPlugin;

// Which cache features the running YMM4 build supports (see HostContracts). Basis names what that was decided
// from: a build whose code was read, or the read build whose parts an unread build shares.
// VerifiedDecoders null: the decoders' own shapes decide (the builds that were read).
internal sealed record HostFeatures(string Basis, bool Preview, bool SelectionRects, bool WrappedSources, bool RulerBars,
    IReadOnlySet<string>? VerifiedDecoders)
{
    // YMM4 4.56.1.0, the build whose renderer, preview player, controllers and video sources were read.
    internal static readonly Guid ReadBuild = Guid.Parse("23e5b5b5-adcf-43b7-b976-b6b63f8dadea");
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
            ? new("4.56.1.0", true, true, true, true, null)
            // Other accepted builds (4.55.1.1): the cache and the preview, nothing that was read in 4.56.1.0 only.
            : new(host.GetName().Version?.ToString() ?? "?", true, false, false, false, null);
    }
}
