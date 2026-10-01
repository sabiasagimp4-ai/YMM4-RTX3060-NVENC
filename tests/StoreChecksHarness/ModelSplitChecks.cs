using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVEncVideoWriterPlugin;

// FrameModelSplit streams the model; its texts must equal those of the parsed-tree version it replaced (they are
// hashed into every frame key, so any difference would silently drop all stored frames), and the owner it gives each
// foreign "$type" must equal the one found from the tree (an item missed there would be cached with code not read).
internal static class ModelSplitChecks
{
    internal static void Run()
    {
        var random = new Random(4561);
        int rejections = 0, attributed = 0, nestedForeign = 0, characterForeign = 0, audioOnly = 0;
        for (int sample = 0; sample < 400; sample++)
        {
            var rootId = Guid.NewGuid();
            string text = Model(random, rootId, timelines: 1 + random.Next(4), items: random.Next(40), foreignTypes: sample % 3 != 2);
            var resources = Enumerable.Range(0, random.Next(4)).Select(i => "font://F" + i).ToArray();
            var parsed = Parse(text);
            var expectedUse = TreeAttribution(parsed, rootId);
            bool split = FrameModelSplit.TrySplit(text, rootId, resources, Classify, out var actual, out string? rejected);
            Check(split == (expectedUse.Rejected is null) && rejected == expectedUse.Rejected,
                $"type check differs (sample {sample}): streaming {rejected ?? "none"}, tree {expectedUse.Rejected ?? "none"}");
            if (!split) { rejections++; continue; }
            var expected = Copying(parsed, rootId, resources);
            Check(actual.Global == expected.Global, $"global text differs (sample {sample})");
            Check(actual.Nested == expected.Nested, $"other timelines text differs (sample {sample})");
            Check(actual.RootItems.SequenceEqual(expected.RootItems), $"item texts differ (sample {sample})");
            Check(actual.ForeignItems.SequenceEqual(expectedUse.Items), $"foreign root items differ (sample {sample})");
            Check(actual.NestedForeign == expectedUse.Nested, $"foreign other timelines differ (sample {sample})");
            Check(actual.ForeignCharacters.SequenceEqual(expectedUse.Characters), $"foreign characters differ (sample {sample})");
            Check(actual.AudioForeign == expectedUse.Audio, $"audio-only types differ (sample {sample})");
            attributed += actual.ForeignItems.Count(foreign => foreign);
            nestedForeign += actual.NestedForeign ? 1 : 0;
            characterForeign += actual.ForeignCharacters.Length;
            audioOnly += actual.AudioForeign ? 1 : 0;
        }
        Check(rejections > 3 && attributed > 50 && nestedForeign > 20 && characterForeign > 20 && audioOnly > 20,
            $"premise: the models cover every owner ({rejections} rejected, {attributed} items, {nestedForeign} nested, {characterForeign} characters, {audioOnly} audio)");

        // A model about the size of 1000 shapes (several MB).
        var bigRoot = Guid.NewGuid();
        string big = Model(new Random(7), bigRoot, timelines: 2, items: 1000, foreignTypes: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) { var tree = Parse(big); TreeAttribution(tree, bigRoot); Copying(tree, bigRoot, []); }
        double treeMs = clock.Elapsed.TotalMilliseconds / 3;
        clock.Restart();
        for (int i = 0; i < 3; i++) FrameModelSplit.TrySplit(big, bigRoot, [], Classify, out _, out _);
        double streamMs = clock.Elapsed.TotalMilliseconds / 3;
        Console.WriteLine($"Model split: streaming texts and $type owners equal the parsed tree's (400 generated models, {rejections} rejected, "
            + $"{attributed} foreign items, {nestedForeign} with foreign other timelines, {characterForeign} foreign characters); "
            + $"{big.Length / 1024} KiB model: tree {treeMs:F0} ms, streaming {streamMs:F0} ms (Linux)");
    }

    // FrameCacheKey.ClassifyType's rule.
    private static FrameModelSplit.TypeUse Classify(string type, IReadOnlyList<string> path)
    {
        string assembly = type.Split(',').Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;
        if (assembly is "YukkuriMovieMaker" or "YukkuriMovieMaker.Plugin") return FrameModelSplit.TypeUse.Known;
        if (path.Any(name => name == "VoiceParameter"
            || name.StartsWith("Tachie", StringComparison.Ordinal) && name.EndsWith("Parameter", StringComparison.Ordinal)))
            return FrameModelSplit.TypeUse.Known;
        return path.Contains("AudioEffects") ? FrameModelSplit.TypeUse.AudioOnly : FrameModelSplit.TypeUse.Foreign;
    }

    // From the parsed tree: each foreign "$type" belongs to the root item, other timeline or character holding it;
    // the first one (document order) held by none of them rejects the model.
    private static (string? Rejected, bool[] Items, bool Nested, int[] Characters, bool Audio) TreeAttribution(JObject parsed, Guid rootId)
    {
        var timelines = (JArray)parsed["Timelines"]!;
        var root = timelines.OfType<JObject>().Single(t => Guid.TryParse(t["ID"]?.ToString(), out var id) && id == rootId);
        var rootItems = (JArray)root["Items"]!;
        var characters = parsed["Characters"] as JArray;
        var items = new bool[rootItems.Count];
        var foreignCharacters = new SortedSet<int>();
        bool nested = false, audio = false;
        foreach (var property in parsed.Descendants().OfType<JProperty>().Where(p => p.Name == "$type"))
        {
            string? type = property.Value.Type == JTokenType.String ? property.Value.Value<string>() : null;
            var path = property.Ancestors().OfType<JProperty>().Select(p => p.Name).Reverse().ToArray();
            var use = type is null ? FrameModelSplit.TypeUse.Foreign : Classify(type, path);
            audio |= use == FrameModelSplit.TypeUse.AudioOnly;
            if (use != FrameModelSplit.TypeUse.Foreign) continue;
            var ancestors = property.Ancestors().ToArray();
            var timeline = ancestors.FirstOrDefault(token => token.Parent == timelines);
            if (timeline is not null && !ReferenceEquals(timeline, root)) { nested = true; continue; }
            if (ancestors.FirstOrDefault(token => token.Parent == rootItems) is { } item) { items[rootItems.IndexOf(item)] = true; continue; }
            if (characters is not null && ancestors.FirstOrDefault(token => token.Parent == characters) is { } character)
            {
                foreignCharacters.Add(characters.IndexOf(character));
                continue;
            }
            return (type ?? property.Value.ToString(), items, nested, [.. foreignCharacters], audio);
        }
        return (null, items, nested, [.. foreignCharacters], audio);
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
            ["Timelines"] = new JArray(ids.Select(id => Timeline(random, id, items, foreignTypes))),
            ["Characters"] = new JArray(Enumerable.Range(0, random.Next(4)).Select(index => new JObject
            {
                ["Name"] = "れいむ" + index,
                ["Color"] = "#FFFF0000",
                ["TachieCharacterParameter"] = new JObject
                {
                    ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.CharacterParameter, YukkuriMovieMaker.Plugin.Tachie.AnimationTachie",
                    ["Parts"] = new JArray(new JObject { ["$type"] = "YukkuriMovieMaker.Plugin.Tachie.AnimationTachie.Part, YukkuriMovieMaker.Plugin.Tachie.AnimationTachie", ["Path"] = "C:\\a.png" }),
                },
                ["JimakuVideoEffects"] = new JArray(Enumerable.Range(0, random.Next(3)).Select(_ => Effect(random, foreignTypes))),
                ["AudioEffects"] = new JArray(Enumerable.Range(0, random.Next(2)).Select(_ => Effect(random, foreignTypes))),
            })),
            ["Resources"] = new JArray("font://Arial", "file:///C:/a.png"),
            ["Zoom"] = "HighQuality",
            ["FileTypes"] = new JArray("png=Image", "mp4=Video"),
        };
        if (foreignTypes && random.Next(60) == 0) model.AddFirst(new JProperty("$type", "Some.Plugin.Model, Some.Plugin"));
        return model.ToString(Formatting.None);
    }

    // The timeline's settings come before or after its items (the splitter must not depend on the order).
    private static JObject Timeline(Random random, Guid id, int items, bool foreignTypes)
    {
        var timeline = new JObject { ["ID"] = id.ToString() };
        var settings = new JProperty[]
        {
            new("VideoInfo", foreignTypes && random.Next(30) == 0
                ? new JObject { ["$type"] = "Some.Plugin.Info, Some.Plugin", ["Width"] = 1920 }
                : new JObject { ["Width"] = 1920, ["Height"] = 1080, ["FPS"] = 30, ["BackgroundColor"] = "#FF000000" }),
            new("LayerSettings", new JObject { ["Layers"] = new JArray(random.Next(3), random.Next(3)) }),
            new("Length", random.Next(1, 10000)),
        };
        bool settingsFirst = random.Next(2) == 0;
        if (settingsFirst) foreach (var setting in settings) timeline.Add(setting);
        timeline.Add(new JProperty("Items", new JArray(Enumerable.Range(0, items).Select(i => Item(random, i, foreignTypes)))));
        if (!settingsFirst) foreach (var setting in settings) timeline.Add(setting);
        return timeline;
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
        ["VoiceParameter"] = random.Next(5) == 0
            ? new JObject { ["$type"] = "Some.Voice.Parameter, Some.Voice", ["Speed"] = random.Next(5) }
            : JValue.CreateNull(),
        ["VideoEffects"] = new JArray(Enumerable.Range(0, random.Next(3)).Select(_ => Effect(random, foreignTypes && random.Next(4) == 0))),
        ["AudioEffects"] = new JArray(Enumerable.Range(0, random.Next(2)).Select(_ => Effect(random, foreignTypes))),
    };

    private static JObject Effect(Random random, bool foreignTypes) => new()
    {
        ["$type"] = foreignTypes && random.Next(10) == 0 ? "Some.Plugin.Effect, Some.Plugin"
            : foreignTypes && random.Next(10) == 0 ? "YukkuriMovieMaker.Plugin.Community.Effect.Video.Lut.LutEffect, YukkuriMovieMaker.Plugin.Community"
            : "YukkuriMovieMaker.Project.Effects.GaussianBlurEffect, YukkuriMovieMaker",
        ["Blur"] = random.NextDouble(),
        ["Nested"] = random.Next(8) == 0 ? new JObject { ["$type"] = foreignTypes ? "Some.Plugin.Inner, Some.Plugin" : "YukkuriMovieMaker.Commons.Animation, YukkuriMovieMaker" } : JValue.CreateNull(),
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Model split: " + message);
    }
}
