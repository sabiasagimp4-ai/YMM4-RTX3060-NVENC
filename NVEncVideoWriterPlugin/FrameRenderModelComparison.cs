using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace NVEncVideoWriterPlugin;

// Preserve the exact JSON bytes outside the root object-identity resource strings.
internal static class FrameRenderModelComparison
{
    private sealed record ComparableModel(string Text);
    private static readonly ConditionalWeakTable<string, ComparableModel> comparableModels = new();

    // The description contains process object identities for every random item, including inactive ones.
    // Clone identities necessarily differ. Keep all drawing fields and the identity entry count, and compare
    // frame keys separately: an active Session frame still cannot pass the clone path.
    internal static bool Matches(string live, string clone)
    {
        if (live == clone) return true;
        try { return comparableModels.GetValue(live, WithoutIdentities).Text == comparableModels.GetValue(clone, WithoutIdentities).Text; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    private static ComparableModel WithoutIdentities(string model)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(model);
        if (Encoding.UTF8.GetString(bytes) != model) throw new InvalidDataException("描画記述の文字を保持できません。");
        var reader = new System.Text.Json.Utf8JsonReader(bytes, new System.Text.Json.JsonReaderOptions { MaxDepth = 256 });
        bool format = false, resources = false;
        int resourceDepth = -1, copied = 0;
        using var output = new MemoryStream(bytes.Length);
        while (reader.Read())
        {
            if (resourceDepth >= 0)
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.EndArray && reader.CurrentDepth == resourceDepth)
                { resourceDepth = -1; continue; }
                if (reader.TokenType != System.Text.Json.JsonTokenType.String || reader.CurrentDepth != resourceDepth + 1)
                    throw new InvalidDataException("描画記述の同一性情報を確認できません。");
                if (reader.GetString()!.StartsWith("identity://", StringComparison.Ordinal))
                {
                    int start = checked((int)reader.TokenStartIndex);
                    output.Write(bytes.AsSpan(copied, start - copied));
                    output.Write("\"identity://[object identity]\""u8);
                    copied = checked((int)reader.BytesConsumed);
                }
            }
            else if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                if (reader.ValueTextEquals("Format"))
                {
                    if (format || !reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.Number
                        || !reader.TryGetInt32(out int version) || version is not (2 or 3))
                        throw new InvalidDataException("描画記述の版を確認できません。");
                    format = true;
                }
                else if (reader.ValueTextEquals("Resources"))
                {
                    if (resources || !reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
                        throw new InvalidDataException("描画記述の同一性情報を確認できません。");
                    resources = true; resourceDepth = reader.CurrentDepth;
                }
            }
        }
        if (!format || !resources || resourceDepth >= 0) throw new InvalidDataException("描画記述の同一性情報を確認できません。");
        output.Write(bytes.AsSpan(copied));
        return new(Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)));
    }

}
