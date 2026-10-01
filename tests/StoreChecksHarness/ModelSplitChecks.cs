using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVEncVideoWriterPlugin;

// FrameModelSplit splits the parsed model in place; its texts must equal those of the copying version it replaced
// (they are hashed into every frame key, so any difference would silently drop all stored frames).
internal static class ModelSplitChecks
{
    internal static void Run()
    {
        var random = new Random(4561);
        for (int sample = 0; sample < 200; sample++)
        {
            var rootId = Guid.NewGuid();
            string text = Model(random, rootId, timelines: 1 + random.Next(4), items: random.Next(40));
            var resources = Enumerable.Range(0, random.Next(4)).Select(i => "font://F" + i).ToArray();
            var expected = Copying(Parse(text), rootId, resources);
            var actual = FrameModelSplit.Split(Parse(text), rootId, resources);
            Check(actual.Global == expected.Global, $"global text differs (sample {sample})");
            Check(actual.Nested == expected.Nested, $"other timelines text differs (sample {sample})");
            Check(actual.RootItems.SequenceEqual(expected.RootItems), $"item texts differ (sample {sample})");
        }
        Console.WriteLine("Model split: in-place texts equal the copying version's (200 generated models)");
    }

    // The version FrameCacheKey.DescribeFrames used until the split was made in place.
    private static (string Global, string Nested, string[] RootItems) Copying(JObject parsed, Guid rootId, IEnumerable<string> resources)
    {
        var timelines = (JArray)parsed["Timelines"]!;
        var root = timelines.OfType<JObject>().Single(t => Guid.TryParse(t["ID"]?.ToString(), out var id) && id == rootId);
        var rootTokens = (JArray)root["Items"]!;
        var nested = new JArray(timelines.Where(t => !ReferenceEquals(t, root)).Select(t => t.DeepClone()));
        var global = (JObject)parsed.DeepClone();
        var rootSettings = (JObject)root.DeepClone();
        rootSettings.Remove("Items");
        global["Timelines"] = new JArray(rootSettings);
        global["Resources"] = new JArray(resources);
        return (global.ToString(Formatting.None), nested.ToString(Formatting.None), rootTokens.Select(t => t.ToString(Formatting.None)).ToArray());
    }

    private static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static string Model(Random random, Guid rootId, int timelines, int items)
    {
        var ids = Enumerable.Range(0, timelines).Select(i => i == 0 ? rootId : Guid.NewGuid()).OrderBy(_ => random.Next()).ToArray();
        var model = new JObject
        {
            ["Format"] = 2,
            ["Host"] = Guid.NewGuid().ToString(),
            ["Root"] = rootId.ToString(),
            ["ParentScenes"] = new JArray(),
            ["Timelines"] = new JArray(ids.Select(id => new JObject
            {
                ["ID"] = id.ToString(),
                ["Items"] = new JArray(Enumerable.Range(0, items).Select(i => Item(random, i))),
                ["VideoInfo"] = new JObject { ["Width"] = 1920, ["Height"] = 1080, ["FPS"] = 30, ["BackgroundColor"] = "#FF000000" },
                ["LayerSettings"] = new JObject { ["Layers"] = new JArray(random.Next(3), random.Next(3)) },
                ["Length"] = random.Next(1, 10000),
            })),
            ["Characters"] = new JArray(new JObject { ["Name"] = "れいむ", ["Color"] = "#FFFF0000" }),
            ["Resources"] = new JArray("font://Arial", "file:///C:/a.png"),
            ["Zoom"] = "HighQuality",
            ["FileTypes"] = new JArray("png=Image", "mp4=Video"),
        };
        return model.ToString(Formatting.None);
    }

    private static JObject Item(Random random, int index) => new()
    {
        ["$type"] = index % 3 == 0 ? "YukkuriMovieMaker.Project.Items.ShapeItem, YukkuriMovieMaker" : "YukkuriMovieMaker.Project.Items.TextItem, YukkuriMovieMaker",
        ["Frame"] = random.Next(0, 5000),
        ["Length"] = random.Next(1, 300),
        ["X"] = new JObject { ["Values"] = new JArray(new JObject { ["Value"] = random.NextDouble() * 1000 - 500 }), ["Span"] = 0.0 },
        ["Text"] = "テキスト \"引用\" \\ " + random.Next(),
        ["Opacity"] = random.Next(2) == 0 ? 100.0 : 0.1 + random.NextDouble(),
        ["VideoEffects"] = new JArray(Enumerable.Range(0, random.Next(3)).Select(_ => new JObject { ["$type"] = "YukkuriMovieMaker.Project.Effects.GaussianBlurEffect, YukkuriMovieMaker", ["Blur"] = random.NextDouble() })),
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Model split: " + message);
    }
}
