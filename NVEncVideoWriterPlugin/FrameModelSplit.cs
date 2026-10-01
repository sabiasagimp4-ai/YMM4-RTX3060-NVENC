using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NVEncVideoWriterPlugin;

// The JSON texts FrameDependencyIndex hashes, from the parsed project model: what every frame depends on (all but
// timeline items, keeping the root timeline's settings), the other timelines, and each root timeline item.
// Consumes `parsed`, splitting it in place: copying it cost two deep clones of a model that is several MB for a
// project of 1000 items. The texts are the same as those of the copying version (StoreChecks compares them).
internal static class FrameModelSplit
{
    internal static (string Global, string Nested, string[] RootItems) Split(JObject parsed, Guid rootId, IEnumerable<string> globalResources)
    {
        var timelines = (JArray)parsed["Timelines"]!;
        var root = timelines.OfType<JObject>().Single(t => Guid.TryParse(t["ID"]?.ToString(), out var id) && id == rootId);
        var items = (JArray)root["Items"]!;
        var texts = new string[items.Count];
        for (int i = 0; i < texts.Length; i++) texts[i] = items[i].ToString(Formatting.None);
        root.Remove("Items");
        timelines.Remove(root);
        string nested = timelines.ToString(Formatting.None);
        parsed["Timelines"] = new JArray(root);
        parsed["Resources"] = new JArray(globalResources);
        return (parsed.ToString(Formatting.None), nested, texts);
    }
}
