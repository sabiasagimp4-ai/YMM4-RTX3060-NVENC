using System.Text.Json;
using NVEncVideoWriterPlugin;

internal static class RenderModelComparisonChecks
{
    internal static void Run()
    {
        foreach (int version in new[] { 2, 3 })
        {
            string a = $"{{\"Format\":{version},\"Resources\":[\"identity://live\"],\"Number\":1.0}}";
            string b = a.Replace("identity://live", "identity://clone");
            Check(FrameRenderModelComparison.Matches(a, b), "Audited main/speedup model version rejected: " + version);
            Check(!FrameRenderModelComparison.Matches(a, b.Replace($"\"Format\":{version}", $"\"Format\":{5 - version}")),
                "The model's actual version was ignored");
        }
        for (int index = 0; index < 100; index++)
        {
            string text = JsonSerializer.Serialize("日本語／😀 identity://" + index);
            string left = $"{{\"Format\":3,\"Resources\":[\"font://Arial\",\"identity://{index},1\"],\"Text\":{text},\"Nested\":{{\"Resources\":[\"identity://original\"]}},\"Value\":1000000000000000.0001}}";
            string right = left.Replace($"identity://{index},1", $"identity://{index + 100},2");
            Check(FrameRenderModelComparison.Matches(left, right), "Equivalent clone identities were rejected");
            Check(!FrameRenderModelComparison.Matches(left, right.Replace(".0001", ".0002")), "Precision in another field was rewritten");
            Check(!FrameRenderModelComparison.Matches(left, right.Replace("original", "different")), "Nested identity resources were ignored");
            Check(!FrameRenderModelComparison.Matches(left, right.Replace("Arial", "Tahoma")), "Another resource was ignored");
            Check(!FrameRenderModelComparison.Matches(left, right.Replace(text, JsonSerializer.Serialize("other text"))), "Item text was ignored");
            Check(!FrameRenderModelComparison.Matches(left, right.Replace("identity://", "other://")), "Non-identity resources were ignored");
        }
        string model = "{\"Format\":3,\"Resources\":[\"identity://1\"],\"Number\":1.0}";
        Check(!FrameRenderModelComparison.Matches(model, model.Replace("1.0", "1.00")), "Numeric spelling outside identities changed");
        Check(!FrameRenderModelComparison.Matches(model, model.Replace("[\"identity://1\"]", "[\"identity://2\",\"identity://3\"]")), "Identity entry count changed");
        Check(!FrameRenderModelComparison.Matches(model, model.Replace("\"Format\":3", "\"Format\":4")), "Unknown model version was accepted");
        Check(!FrameRenderModelComparison.Matches(model, "{broken"), "Malformed model was accepted");
        Check(!FrameRenderModelComparison.Matches(model, model.Replace("[\"identity://1\"]", "[12]")), "Non-string resources were accepted");
        Console.WriteLine("Clone model comparison: only root identity values differ; numeric precision, text, nested/other resources and counts remain exact.");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
