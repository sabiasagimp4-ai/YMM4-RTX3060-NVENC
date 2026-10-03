using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NVEncVideoWriterPlugin;

// The JSON texts FrameDependencyIndex hashes, from the project model: what every frame depends on (all but timeline
// items, keeping the root timeline's settings), the other timelines, and each root timeline item. Read in one
// streaming pass that also checks every "$type": a parsed tree, the scan over it and the per-item serialization
// took about two thirds of describing a project of 1000 items. The texts equal those the parsed tree gave
// (StoreChecks compares them).
// A timeline's layer settings lose what no frame draws: the layers' names and colors (the timeline's display). The
// root timeline's layer volumes only reach frames that read audio, so they go with the other timelines' part.
internal static class FrameModelSplit
{
    // Properties of a timeline's "LayerSettings" (any depth) that no frame draws, and those only audio uses.
    private static readonly string[] LayerDisplay = ["Label", "Labels", "Color", "Colors"];
    private static readonly string[] LayerAudio = ["Volume", "Volumes"];

    // A copy of the layer settings `value` without what no frame draws, and without the volumes unless `withAudio`.
    internal static JToken LayerView(JToken value, bool withAudio)
    {
        var copy = value.DeepClone();
        if (copy is not JContainer container) return copy;
        foreach (var property in container.Descendants().OfType<JProperty>()
            .Where(p => LayerDisplay.Contains(p.Name) || !withAudio && LayerAudio.Contains(p.Name)).ToArray())
            property.Remove();
        return copy;
    }

    // What a "$type" means for the frames: Known (code that was read, or data no cached frame reads), AudioOnly (only
    // frames that read audio, the wide ones), Foreign (code that was not read).
    internal enum TypeUse { Known, AudioOnly, Foreign }

    // ForeignItems: root items holding a foreign type; NestedForeign: one in another timeline; ForeignCharacters:
    // indexes into the model's "Characters"; AudioForeign: an audio-only type anywhere.
    internal readonly record struct Parts(string Global, string Nested, string[] RootItems,
        bool[] ForeignItems, bool NestedForeign, int[] ForeignCharacters, bool AudioForeign);

    // classify(type, property names from the model's root to the "$type"). A foreign type outside the items,
    // timelines and characters rejects the model and is returned. Throws InvalidDataException for a model of
    // another shape.
    internal static bool TrySplit(string model, Guid rootId, IEnumerable<string> globalResources,
        Func<string, IReadOnlyList<string>, TypeUse> classify, out Parts parts, out string? rejected)
    {
        var splitter = new Splitter(model, classify);
        bool split = splitter.Run(rootId, globalResources, out parts);
        rejected = splitter.Rejected;
        return split;
    }

    private enum Owner { Global, RootItem, Nested, Character }

    private sealed class Splitter(string model, Func<string, IReadOnlyList<string>, TypeUse> classify)
    {
        private readonly JsonTextReader reader = new(new StringReader(model))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = null,
        };
        private readonly List<string> path = [];
        private readonly List<bool> foreignItems = [];
        private readonly SortedSet<int> foreignCharacters = [];
        private bool nestedForeign, audioForeign;
        // The root timeline's layer settings with their volumes, for the frames that read audio (Nested).
        private JToken? rootLayers;
        private Owner owner;
        private int ownerIndex;
        internal string? Rejected { get; private set; }

        internal bool Run(Guid rootId, IEnumerable<string> globalResources, out Parts parts)
        {
            parts = default;
            var globalText = new StringWriter();
            var nestedText = new StringWriter();
            var items = new List<string>();
            using var global = new JsonTextWriter(globalText);
            using var nested = new JsonTextWriter(nestedText);
            nested.WriteStartArray();
            Expect(Next(), JsonToken.StartObject);
            global.WriteStartObject();
            bool rootFound = false;
            while (Next() == JsonToken.PropertyName)
            {
                string name = (string)reader.Value!;
                global.WritePropertyName(name);
                Next();
                if (name == "Timelines")
                {
                    Expect(reader.TokenType, JsonToken.StartArray);
                    global.WriteStartArray();
                    path.Add(name);
                    while (Next() == JsonToken.StartObject)
                        if (!Timeline(rootId, global, nested, items, ref rootFound)) return false;
                    path.RemoveAt(path.Count - 1);
                    Expect(reader.TokenType, JsonToken.EndArray);
                    global.WriteEndArray();
                }
                else if (name == "Characters" && reader.TokenType == JsonToken.StartArray)
                {
                    global.WriteStartArray();
                    path.Add(name);
                    for (int index = 0; Next() != JsonToken.EndArray; index++)
                    {
                        (owner, ownerIndex) = (Owner.Character, index);
                        if (!Copy(global, null)) return false;
                    }
                    owner = Owner.Global;
                    path.RemoveAt(path.Count - 1);
                    global.WriteEndArray();
                }
                else if (name == "Resources")
                {
                    reader.Skip(); // replaced by the resources every frame depends on
                    global.WriteStartArray();
                    foreach (string resource in globalResources) global.WriteValue(resource);
                    global.WriteEndArray();
                }
                else if (!CheckedCopy(global, name)) return false;
            }
            Expect(reader.TokenType, JsonToken.EndObject);
            global.WriteEndObject();
            if (rootLayers is not null) new JObject { ["RootLayerSettings"] = rootLayers }.WriteTo(nested);
            nested.WriteEndArray();
            if (!rootFound || reader.Read()) throw new InvalidDataException("The model has no root timeline or extra content");
            global.Flush();
            nested.Flush();
            parts = new(globalText.ToString(), nestedText.ToString(), [.. items], [.. foreignItems], nestedForeign,
                [.. foreignCharacters], audioForeign);
            return true;
        }

        // A timeline object (the reader on its StartObject). Its ID comes first and decides where it goes.
        private bool Timeline(Guid rootId, JsonWriter global, JsonWriter nested, List<string> items, ref bool rootFound)
        {
            Expect(Next(), JsonToken.PropertyName);
            if ((string)reader.Value! != "ID") throw new InvalidDataException("A timeline does not start with its ID");
            Next();
            bool root = reader.TokenType == JsonToken.String && Guid.TryParse((string)reader.Value!, out var id) && id == rootId;
            if (root && rootFound) throw new InvalidDataException("Two root timelines");
            rootFound |= root;
            var writer = root ? global : nested;
            owner = root ? Owner.Global : Owner.Nested;
            writer.WriteStartObject();
            writer.WritePropertyName("ID");
            if (!Copy(writer, "ID")) return false;
            while (Next() == JsonToken.PropertyName)
            {
                string name = (string)reader.Value!;
                Next();
                if (root && name == "Items")
                {
                    Expect(reader.TokenType, JsonToken.StartArray);
                    path.Add(name);
                    while (Next() != JsonToken.EndArray)
                    {
                        (owner, ownerIndex) = (Owner.RootItem, items.Count);
                        foreignItems.Add(false);
                        var text = new StringWriter();
                        using (var item = new JsonTextWriter(text))
                            if (!Copy(item, null)) return false;
                        items.Add(text.ToString());
                    }
                    owner = Owner.Global;
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                writer.WritePropertyName(name);
                if (name == "LayerSettings")
                {
                    var buffer = new JTokenWriter();
                    if (!CheckedCopy(buffer, name)) return false; // "$type" values are checked as anywhere else
                    var layers = buffer.Token!;
                    LayerView(layers, withAudio: !root).WriteTo(writer);
                    if (root) rootLayers = LayerView(layers, withAudio: true);
                    continue;
                }
                if (!CheckedCopy(writer, name)) return false;
            }
            Expect(reader.TokenType, JsonToken.EndObject);
            writer.WriteEndObject();
            owner = Owner.Global;
            return true;
        }

        // Copies the value the reader is on (and its children), checking "$type" values. `name`: its property.
        private bool Copy(JsonWriter writer, string? name)
        {
            if (name is not null) path.Add(name);
            try
            {
                switch (reader.TokenType)
                {
                    case JsonToken.StartObject:
                        writer.WriteStartObject();
                        while (Next() == JsonToken.PropertyName)
                        {
                            string property = (string)reader.Value!;
                            writer.WritePropertyName(property);
                            Next();
                            if (!CheckedCopy(writer, property)) return false;
                        }
                        Expect(reader.TokenType, JsonToken.EndObject);
                        writer.WriteEndObject();
                        return true;
                    case JsonToken.StartArray:
                        writer.WriteStartArray();
                        while (Next() != JsonToken.EndArray)
                            if (!Copy(writer, null)) return false;
                        writer.WriteEndArray();
                        return true;
                    case JsonToken.Integer or JsonToken.Float or JsonToken.String or JsonToken.Boolean or JsonToken.Null
                        or JsonToken.Undefined or JsonToken.Date or JsonToken.Bytes:
                        writer.WriteToken(reader, writeChildren: false);
                        return true;
                    default:
                        throw new InvalidDataException("Unexpected token " + reader.TokenType);
                }
            }
            finally { if (name is not null) path.RemoveAt(path.Count - 1); }
        }

        // Copy, checking the value first when it is a "$type".
        private bool CheckedCopy(JsonWriter writer, string name)
        {
            if (name == "$type" && !Attribute(reader.TokenType == JsonToken.String
                ? classify((string)reader.Value!, path) : TypeUse.Foreign)) return false;
            return Copy(writer, name);
        }

        // False when a foreign type belongs to no item, timeline or character (the model is rejected).
        private bool Attribute(TypeUse use)
        {
            if (use == TypeUse.AudioOnly) audioForeign = true;
            if (use != TypeUse.Foreign) return true;
            switch (owner)
            {
                case Owner.RootItem: foreignItems[ownerIndex] = true; return true;
                case Owner.Nested: nestedForeign = true; return true;
                case Owner.Character: foreignCharacters.Add(ownerIndex); return true;
                default:
                    Rejected = reader.Value?.ToString() ?? string.Empty;
                    return false;
            }
        }

        private JsonToken Next() => reader.Read() ? reader.TokenType : throw new InvalidDataException("The model ended early");

        private static void Expect(JsonToken actual, JsonToken expected)
        {
            if (actual != expected) throw new InvalidDataException($"Expected {expected}, found {actual}");
        }
    }
}
