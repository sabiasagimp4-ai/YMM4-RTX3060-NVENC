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
        int rejections = 0;
        for (int sample = 0; sample < 300; sample++)
        {
            var rootId = Guid.NewGuid();
            string text = Model(random, rootId, timelines: 1 + random.Next(4), items: random.Next(40), foreignTypes: sample % 3 == 0);
            var resources = Enumerable.Range(0, random.Next(4)).Select(i => "font://F" + i).ToArray();
            var parsed = Parse(text);
            string? expectedRejection = TreeRejection(parsed);
            bool split = FrameModelSplit.TrySplit(text, rootId, resources, Allowed, out var actual, out string? rejected);
            Check(split == (expectedRejection is null) && rejected == expectedRejection,
                $"type check differs (sample {sample}): streaming {rejected ?? "none"}, tree {expectedRejection ?? "none"}");
            if (!split) { rejections++; continue; }
            var expected = Copying(parsed, rootId, resources);
            Check(actual.Global == expected.Global, $"global text differs (sample {sample})");
            Check(actual.Nested == expected.Nested, $"other timelines text differs (sample {sample})");
            Check(actual.RootItems.SequenceEqual(expected.RootItems), $"item texts differ (sample {sample})");
        }
        Check(rejections > 20, "premise: some models carry foreign types");

        // A model about the size of 1000 shapes (several MB).
        var bigRoot = Guid.NewGuid();
        string big = Model(new Random(7), bigRoot, timelines: 2, items: 1000, foreignTypes: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) { var tree = Parse(big); TreeRejection(tree); Copying(tree, bigRoot, []); }
        double treeMs = clock.Elapsed.TotalMilliseconds / 3;
        clock.Restart();
        for (int i = 0; i < 3; i++) FrameModelSplit.TrySplit(big, bigRoot, [], Allowed, out _, out _);
        double streamMs = clock.Elapsed.TotalMilliseconds / 3;
        Console.WriteLine($"Model split: streaming texts and $type checks equal the parsed tree's (300 generated models, {rejections} rejected); "
            + $"{big.Length / 1024} KiB model: tree {treeMs:F0} ms, streaming {streamMs:F0} ms (Linux)");
    }

    // Host and plugin types, and bundled tachie plugin types under a Tachie*Parameter property (FrameCacheKey's rule).
    private static bool Allowed(string type, IReadOnlyList<string> path)
    {
        string assembly = type.Split(',').Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
        return assembly is "YukkuriMovieMaker" or "YukkuriMovieMaker.Plugin"
            || assembly.StartsWith("YukkuriMovieMaker.Plugin.Tachie.", StringComparison.Ordinal)
                && path.Any(name => name.StartsWith("Tachie", StringComparison.Ordinal) && name.EndsWith("Parameter", StringComparison.Ordinal));
    }

    // The scan FrameCacheKey made over the parsed tree: the first "$type" (document order) that is not allowed.
    private static string? TreeRejection(JObject parsed)
    {
        foreach (var property in parsed.Descendants().OfType<JProperty>().Where(p => p.Name == "$type"))
        {
            string type = property.Value.Type == JTokenType.String ? property.Value.Value<string>()! : null!;
            var path = property.Ancestors().OfType<JProperty>().Select(p => p.Name).Reverse().ToArray();
            if (type is null) return property.Value.ToString();
            if (!Allowed(type, path)) return type;
        }
        return null;
    }

    // The version FrameCacheKey.DescribeFrames used until the split was made in place.
    private static (string Global, string Nested, string[] RootItems) Copying(JObject source, Guid rootId, IEnumerable<string> resources)
    {
        var parsed = (JObject)source.DeepClone();
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

    private static string Model(Random random, Guid rootId, int timelines, int items, bool foreignTypes)
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
                ["Items"] = new JArray(Enumerable.Range(0, items).Select(i => Item(random, i, foreignTypes))),
                ["VideoInfo"] = new JObject { ["Width"] = 1920, ["Height"] = 1080, ["FPS"] = 30, ["BackgroundColor"] = "#FF000000" },
                ["LayerSettings"] = new JObject { ["Layers"] = new JArray(random.Next(3), random.Next(3)) },
                ["Length"] = random.Next(1, 10000),
            })),
            ["Characters"] = new JArray(new JObject
            {
                ["Name"] = "れいむ",
                ["Color"] = "#FFFF0000",
                ["TachieCharacterParameter"] = new JObject
                {
                    ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.CharacterParameter, YukkuriMovieMaker.Plugin.Tachie.AnimationTachie",
                    ["Parts"] = new JArray(new JObject { ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.Part, YukkuriMovieMaker.Plugin.Tachie.AnimationTachie", ["Path"] = "C:\\a.png" }),
                },
            }),
            ["Resources"] = new JArray("font://Arial", "file:///C:/a.png"),
            ["Zoom"] = "HighQuality",
            ["FileTypes"] = new JArray("png=Image", "mp4=Video"),
        };
        return model.ToString(Formatting.None);
    }

    private static JObject Item(Random random, int index, bool foreignTypes) => new()
    {
        ["$type"] = foreignTypes && random.Next(30) == 0 ? "Some.Plugin.Item, Some.Plugin"
            : foreignTypes && random.Next(30) == 0 ? "YukkuriMovieMaker.Plugin.Tachie.Psd.Effect, YukkuriMovieMaker.Plugin.Tachie.Psd"
            : index % 3 == 0 ? "YukkuriMovieMaker.Project.Items.ShapeItem, YukkuriMovieMaker" : "YukkuriMovieMaker.Project.Items.TextItem, YukkuriMovieMaker",
        ["TachieFaceParameter"] = random.Next(4) == 0
            ? new JObject { ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.Psd.FaceParameter, YukkuriMovieMaker.Plugin.Tachie.Psd", ["Eye"] = random.Next(5) }
            : JValue.CreateNull(),
        ["Big"] = random.Next(50) == 0 ? 12345678901234567890m : random.Next(),
        ["Small"] = random.Next(3) == 0 ? 1e-300 : -0.5,
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
