using System.Security.Cryptography;
using System.Text;

namespace NVEncVideoWriterPlugin;

/// <summary>A provider supplies tokens without rendering. Tokens must change before any affected result can be requested.</summary>
public interface ICacheDependencyProvider
{
    // Check actual callback context, including idle workers. Observation never implies background safety.
    bool CanCaptureOnCurrentThread => false;
    CacheDependencySnapshot CaptureDependencies(long requestedTimeTicks);
    bool IsCurrent(CacheDependencySnapshot snapshot);
}

public sealed record CacheInputDependency(string SourceIdentity, string StateToken, long StartTicks, long EndTicks);

/// <summary>Immutable snapshot of input/time/hidden-state dependencies. File tokens must be content tokens or verified leases,
/// not merely file names. A context token identifies a device/context unless the result is explicitly portable.</summary>
public sealed class CacheDependencySnapshot
{
    private readonly CacheInputDependency[] inputs;
    public string ClassIdentity { get; }
    public string Schema { get; }
    public string StateToken { get; }
    public string ContextToken { get; }
    public IReadOnlyList<CacheInputDependency> Inputs { get; }
    public string Key { get; }
    public CacheDependencySnapshot(string classIdentity, string schema, string stateToken, string contextToken,
        IEnumerable<CacheInputDependency> dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classIdentity); ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(stateToken); ArgumentNullException.ThrowIfNull(contextToken);
        ArgumentNullException.ThrowIfNull(dependencies);
        ClassIdentity = classIdentity; Schema = schema; StateToken = stateToken; ContextToken = contextToken;
        inputs = dependencies.Take(4097).ToArray();
        if (inputs.Length > 4096) throw new ArgumentException("Too many input dependencies");
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input); ArgumentException.ThrowIfNullOrWhiteSpace(input.SourceIdentity);
            ArgumentNullException.ThrowIfNull(input.StateToken);
            if (input.EndTicks < input.StartTicks) throw new ArgumentException("Invalid input time range");
        }
        // Input order is semantic (e.g. foreground/background); retain it. Length-prefixed serialization avoids collisions.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            if (value.Length > 1024 * 1024) throw new ArgumentException("Dependency token too large");
            byte[] data = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
            hash.AppendData(length); hash.AppendData(data);
        }
        Add("dynamic-dependencies-v1"); Add(classIdentity); Add(schema); Add(stateToken); Add(contextToken);
        foreach (var input in inputs)
        { Add(input.SourceIdentity); Add(input.StateToken); Add(input.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)); Add(input.EndTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        Key = Convert.ToHexStringLower(hash.GetHashAndReset());
        Inputs = Array.AsReadOnly(inputs);
    }
}

/// <summary>Separate image validity from request sufficiency. Reuse requires identical pixel interpretation and context.</summary>
public readonly record struct CacheImageRequest(int Left, int Top, int Right, int Bottom, int BitDepth,
    uint ChannelMask, bool PreserveRgbOfZeroAlpha, string PixelFormat, string ContextToken)
{
    public bool IsValid => Right > Left && Bottom > Top && BitDepth is 8 or 16 or 32
        && ChannelMask != 0 && !string.IsNullOrEmpty(PixelFormat) && ContextToken is not null;
    public bool IsSatisfiedBy(CacheImageRequest available) => IsValid && available.IsValid
        && available.Left <= Left && available.Top <= Top && available.Right >= Right && available.Bottom >= Bottom
        && available.BitDepth == BitDepth && available.PixelFormat == PixelFormat && available.ContextToken == ContextToken
        && (available.ChannelMask & ChannelMask) == ChannelMask
        && (!PreserveRgbOfZeroAlpha || available.PreserveRgbOfZeroAlpha);
}
