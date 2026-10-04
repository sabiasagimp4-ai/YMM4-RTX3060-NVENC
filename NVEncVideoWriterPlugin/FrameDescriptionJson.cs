using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using YukkuriMovieMaker.Json;

namespace NVEncVideoWriterPlugin;

// Only the cache description uses hashes. Project saving and the host's serializer settings stay untouched.
internal static class FrameDescriptionJson
{
    internal const int HashThreshold = 4096;
    private sealed record Payload(byte[] Bytes, byte[] Digest, bool SharedVoice);
    private sealed record Payloads(Dictionary<string, Payload> Paths);
    private static readonly ConditionalWeakTable<string, Payloads> payloads = new();
    private sealed record ComparableModel(string Text);
    private static readonly ConditionalWeakTable<string, ComparableModel> comparableModels = new();

    // The description contains process object identities for every random item, including inactive ones.
    // Clone identities necessarily differ. Keep all drawing fields and the identity entry count, and compare
    // frame keys separately: an active Session frame still cannot pass the clone path.
    internal static bool SameRenderModel(string live, string clone)
    {
        if (live == clone) return true;
        try { return comparableModels.GetValue(live, WithoutIdentities).Text == comparableModels.GetValue(clone, WithoutIdentities).Text; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    private static ComparableModel WithoutIdentities(string model)
    {
        var root = Newtonsoft.Json.Linq.JObject.Parse(model);
        if ((int?)root["Format"] != 3 || root["Resources"] is not Newtonsoft.Json.Linq.JArray resources
            || resources.Any(value => value.Type != Newtonsoft.Json.Linq.JTokenType.String))
            throw new InvalidDataException("描画記述の同一性情報を確認できません。");
        for (int index = 0; index < resources.Count; index++)
            if (((string)resources[index]!).StartsWith("identity://", StringComparison.Ordinal))
                resources[index] = "identity://[object identity]";
        return new(root.ToString(Formatting.None));
    }

    private static JsonSerializerSettings Settings() =>
        typeof(Json).GetField("settings", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            is JsonSerializerSettings original ? new(original)
            : throw new NotSupportedException("ホストの描画記述の設定を取得できません。");

    internal static string Serialize<T>(T snapshot, IEnumerable<byte[]>? voiceBuffers = null, IReadOnlySet<string>? voicePaths = null)
    {
        var paths = new Dictionary<string, Payload>(StringComparer.Ordinal);
        var settings = Settings();
        settings.Converters.Insert(0, new EmbeddedBytes(paths, writing: true,
            voiceBuffers is null ? [] : new HashSet<byte[]>(voiceBuffers, ReferenceEqualityComparer.Instance), voicePaths));
        string model = Json.GetJsonText(snapshot, settings);
        payloads.Add(model, new(paths));
        return model;
    }

    internal static long EmbeddedBytesCount(string model) =>
        payloads.TryGetValue(model, out var captured) ? captured.Paths.Values.Sum(value => (long)value.Bytes.Length) : 0;

    internal static byte[] Digest(string model, string path, byte[] bytes) =>
        payloads.TryGetValue(model, out var captured) && captured.Paths.TryGetValue(path, out var payload)
            && ReferenceEquals(payload.Bytes, bytes) ? payload.Digest : SHA256.HashData(bytes);

    internal static T? Load<T>(string model)
    {
        if (!payloads.TryGetValue(model, out var captured))
        {
            // Hash descriptions cannot be rendered without the original, verified payloads. A copied string or an
            // older description from outside this process must fail closed, rather than decode a digest as audio.
            if ((int?)Newtonsoft.Json.Linq.JObject.Parse(model)["Format"] == 3)
                throw new InvalidDataException("ハッシュ記述の元の埋め込みデータがありません。");
            return Json.LoadFromText<T>(model);
        }
        var settings = Settings();
        settings.Converters.Insert(0, new EmbeddedBytes(captured.Paths, writing: false));
        return JsonConvert.DeserializeObject<T>(model, settings);
    }

    private sealed class EmbeddedBytes(Dictionary<string, Payload> paths, bool writing,
        HashSet<byte[]>? voiceBuffers = null, IReadOnlySet<string>? voicePaths = null) : JsonConverter
    {
        // Public byte[] values can be modified in place by external code. Hash once within each description, then
        // recompute in the next description. A process-long byte[] hash memo would silently miss that edit.
        private readonly Dictionary<byte[], byte[]> hashes = new(ReferenceEqualityComparer.Instance);
        public override bool CanConvert(Type type) => type == typeof(byte[]);
        public override bool CanRead => !writing;
        public override bool CanWrite => writing;

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is not byte[] bytes) { writer.WriteNull(); return; }
            if (bytes.Length < HashThreshold) { writer.WriteValue(bytes); return; }
            if (!hashes.TryGetValue(bytes, out var digest)) hashes[bytes] = digest = SHA256.HashData(bytes);
            paths.Add(writer.Path, new(bytes, digest, voiceBuffers?.Contains(bytes) == true
                && (voicePaths is null || voicePaths.Contains(writer.Path))
                && writer.Path.EndsWith(".VoiceCache", StringComparison.Ordinal)));
            writer.WriteValue(digest);
        }

        public override object? ReadJson(JsonReader reader, Type type, object? existing, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            if (reader.TokenType != JsonToken.String || reader.Value is not string encoded)
                throw new InvalidDataException("埋め込みデータの形式が一致しません。");
            byte[] value = Convert.FromBase64String(encoded);
            if (!paths.TryGetValue(reader.Path, out var payload)) return value;
            if (!CryptographicOperations.FixedTimeEquals(value, payload.Digest)
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload.Bytes), payload.Digest))
                throw new InvalidDataException("記述後に埋め込みデータが変化しました。");
            // The audited host only reads VoiceCache. Other embedded arrays receive their own copy, so a renderer
            // of a clone cannot alter a mutable parameter of the live model.
            return payload.SharedVoice
                ? payload.Bytes : (byte[])payload.Bytes.Clone();
        }
    }
}
