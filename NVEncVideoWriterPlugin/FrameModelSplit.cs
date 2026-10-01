using System.IO;
using Newtonsoft.Json;

namespace NVEncVideoWriterPlugin;

// The JSON texts FrameDependencyIndex hashes, from the project model: what every frame depends on (all but timeline
// items, keeping the root timeline's settings), the other timelines, and each root timeline item. Read in one
// streaming pass that also checks every "$type": a parsed tree, the scan over it and the per-item serialization
// took about two thirds of describing a project of 1000 items. The texts equal those the parsed tree gave
// (StoreChecks compares them).
internal static class FrameModelSplit
{
    internal readonly record struct Parts(string Global, string Nested, string[] RootItems);

    // allowType(type, property names from the model's root to the "$type"): false rejects the model, and the
    // rejected type is returned. Throws InvalidDataException for a model of another shape.
    internal static bool TrySplit(string model, Guid rootId, IEnumerable<string> globalResources,
        Func<string, IReadOnlyList<string>, bool> allowType, out Parts parts, out string? rejected)
    {
        var splitter = new Splitter(model, allowType);
        bool split = splitter.Run(rootId, globalResources, out parts);
        rejected = splitter.Rejected;
        return split;
    }

    private sealed class Splitter(string model, Func<string, IReadOnlyList<string>, bool> allowType)
    {
        private readonly JsonTextReader reader = new(new StringReader(model))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = null,
        };
        private readonly List<string> path = [];
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
                else if (name == "Resources")
                {
                    reader.Skip(); // replaced by the resources every frame depends on
                    global.WriteStartArray();
                    foreach (string resource in globalResources) global.WriteValue(resource);
                    global.WriteEndArray();
                }
                else if (!Copy(global, name)) return false;
            }
            Expect(reader.TokenType, JsonToken.EndObject);
            global.WriteEndObject();
            nested.WriteEndArray();
            if (!rootFound || reader.Read()) throw new InvalidDataException("The model has no root timeline or extra content");
            global.Flush();
            nested.Flush();
            parts = new(globalText.ToString(), nestedText.ToString(), [.. items]);
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
                        var text = new StringWriter();
                        using (var item = new JsonTextWriter(text))
                            if (!Copy(item, null)) return false;
                        items.Add(text.ToString());
                    }
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                writer.WritePropertyName(name);
                if (!Copy(writer, name)) return false;
            }
            Expect(reader.TokenType, JsonToken.EndObject);
            writer.WriteEndObject();
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
                            if (property == "$type" && (reader.TokenType != JsonToken.String || !allowType((string)reader.Value!, path)))
                            {
                                Rejected = reader.Value?.ToString() ?? string.Empty;
                                return false;
                            }
                            if (!Copy(writer, property)) return false;
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

        private JsonToken Next() => reader.Read() ? reader.TokenType : throw new InvalidDataException("The model ended early");

        private static void Expect(JsonToken actual, JsonToken expected)
        {
            if (actual != expected) throw new InvalidDataException($"Expected {expected}, found {actual}");
        }
    }
}
