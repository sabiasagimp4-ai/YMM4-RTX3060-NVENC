using System.Security.Cryptography;
using System.Text;

namespace NVEncVideoWriterPlugin;

internal static class FrameDependencyIdentity
{
    // Provider discovery has deterministic model traversal order. Retain each token's
    // slot: sorting would confuse two equal models whose hidden states were swapped.
    // Snapshot keys have fixed width (64 hex digits); the sequence is unambiguous.
    internal static string WithDynamicState(string modelKey, IEnumerable<CacheDependencySnapshot> snapshots) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "dynamic-frame-v2:" + modelKey + string.Concat(snapshots.Select(snapshot => snapshot.Key)))));
}
