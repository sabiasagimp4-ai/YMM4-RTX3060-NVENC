using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// A frame's items are drawn by its TimelineSource and by the TimelineSources inside it (YMM4 4.56.1.0): a composite
// group's children by CompositeSource.source, a transition's pictures before and after it by TransitionSource's two
// sources, and a scene item's scene by SceneSource.sceneSource. The native tachie checks walk them all to find every
// tachie source the frame drew, in groups, transitions and scenes alike.
internal static class NestedTimelineSources
{
    internal const string TimelineSourceType = "YukkuriMovieMaker.Player.Video.TimelineSource";
    private const string Items = "YukkuriMovieMaker.Player.Video.Items.";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int MaximumSources = 4096;
    private static readonly ConcurrentDictionary<(Type, string), MemberInfo?> members = new();
    private static readonly ConditionalWeakTable<object, TachieItem[]> tachieItems = new();

    // The tachie sources (TachieSource) `root` and the sources inside it drew, with their items. False when a source
    // cannot be read (a field YMM4 renamed, a scene's source of another type) or there are too many.
    internal static bool TryTachieSources(object root, List<(TachieItem Item, object Source)> found)
    {
        var pending = new Stack<object>();
        pending.Push(root);
        int visited = 0;
        while (pending.TryPop(out var source))
        {
            if (++visited > MaximumSources || source.GetType().FullName != TimelineSourceType
                || !TryRead(source, "timelineResources", out var value) || value is not IDictionary resources) return false;
            foreach (DictionaryEntry entry in resources)
            {
                if (!TryRead(entry.Value!, "Source", out var core)) return false;
                if (core is null) continue; // draws nothing
                switch (core.GetType().FullName)
                {
                    case Items + "TachieSource":
                        if (entry.Key is not TachieItem item) return false;
                        found.Add((item, core));
                        break;
                    case Items + "CompositeSource":
                        if (!TryRead(core, "source", out var composite) || composite is null) return false;
                        pending.Push(composite);
                        break;
                    case Items + "TransitionSource":
                        foreach (string side in new[] { "effectedBeforeTimelineSource", "effectedAfterTimelineSource" })
                        {
                            if (!TryRead(core, side, out var effected) || effected is null || !TryRead(effected, "timelineSource", out var picture) || picture is null) return false;
                            pending.Push(picture);
                        }
                        break;
                    case Items + "SceneSource":
                        // No source: the scene item names no scene, and draws nothing.
                        if (!TryRead(core, "sceneSource", out var scene)) return false;
                        if (scene is not null) pending.Push(scene);
                        break;
                }
            }
        }
        return true;
    }

    // Whether rendering the root frame can start a lip-sync calculation for a tachie of the plugin `pluginName`. YMM4
    // starts one for a tachie whose character speaks in a voice item shown at the frame (TachieSource.EnsureVolumeEnvelope
    // with the active voice): a tachie of the root timeline and a voice of its character shown at the frame, or at the
    // frame before a transition shown there (transitions draw it), or, while a scene item is shown, a tachie and a voice
    // of one character anywhere in another timeline.
    internal static bool MayStartLipSync(Scene scene, int frame, string pluginName)
    {
        var items = scene.Timeline.Items;
        var frames = TransitionFrames(scene.Timeline, frame);
        bool Speaks(Timeline timeline, Func<IItem, bool> shown) => TachieItems(timeline).Any(tachie =>
            tachie.Character?.TachieType?.FullName == pluginName && shown(tachie) && timeline.Items.OfType<VoiceItem>().Any(voice =>
                (ReferenceEquals(voice.Character, tachie.Character) || voice.CharacterName == tachie.CharacterName) && shown(voice)));
        if (frames.Any(at => Speaks(scene.Timeline, item => Shows(item, at)))) return true;
        return items.OfType<SceneItem>().Any(item => frames.Any(at => Shows(item, at)))
            && scene.Scenes.Timelines.Where(timeline => !ReferenceEquals(timeline, scene.Timeline)).Any(timeline => Speaks(timeline, _ => true));
    }

    // Whether the root frame can draw a tachie item of the plugin `pluginName` at all: one of the root timeline's at the
    // frame or at the frame before a transition shown there, or any of another timeline while a scene item is shown.
    internal static bool MayDraw(Scene scene, int frame, string pluginName)
    {
        var frames = TransitionFrames(scene.Timeline, frame);
        if (TachieItems(scene.Timeline).Any(item => item.Character?.TachieType?.FullName == pluginName && frames.Any(at => Shows(item, at)))) return true;
        return scene.Timeline.Items.OfType<SceneItem>().Any(item => frames.Any(at => Shows(item, at)))
            && scene.Scenes.Timelines.Where(timeline => !ReferenceEquals(timeline, scene.Timeline))
                .Any(timeline => TachieItems(timeline).Any(item => item.Character?.TachieType?.FullName == pluginName));
    }

    // The frame and, through the transitions shown there (recursively), the frames before them that it also draws.
    private static HashSet<long> TransitionFrames(Timeline timeline, int frame)
    {
        var frames = new HashSet<long> { frame };
        var pending = new Queue<long>(frames);
        while (pending.TryDequeue(out long at))
            foreach (var transition in timeline.Items.OfType<TransitionItem>())
                if (Shows(transition, at) && frames.Count < 1024 && frames.Add((long)transition.Frame - 1)) pending.Enqueue((long)transition.Frame - 1);
        return frames;
    }

    internal static bool Shows(IItem item, long frame) => item.Frame <= frame && frame < (long)item.Frame + item.Length;

    // Whether the root timeline's renderer draws `item` at `frame`, as CompositeItemPicker picks it: shown there, the
    // item not hidden, nor its layer.
    internal static bool Drawn(Timeline timeline, IItem item, long frame) =>
        Shows(item, frame) && !item.IsHidden && timeline.LayerSettings.IsVisibles[item.Layer];

    // Whether any timeline of the project holds a tachie item of the plugin `pluginName`.
    internal static bool AnyTachie(Scene scene, string pluginName) =>
        scene.Scenes.Timelines.Append(scene.Timeline).Any(timeline => TachieItems(timeline).Any(item => item.Character?.TachieType?.FullName == pluginName));

    // The tachie items of a timeline. YMM4 replaces its immutable item list at every edit, so they are kept with it.
    internal static TachieItem[] TachieItems(Timeline timeline) => timeline.Items is IImmutableList<IItem> list
        ? tachieItems.GetValue(list, static items => ((IEnumerable<IItem>)items).OfType<TachieItem>().ToArray())
        : timeline.Items.OfType<TachieItem>().ToArray();

    private static bool TryRead(object target, string name, out object? value)
    {
        value = null;
        var member = members.GetOrAdd((target.GetType(), name), static key =>
            (MemberInfo?)key.Item1.GetField(key.Item2, Instance) ?? key.Item1.GetProperty(key.Item2, Instance));
        switch (member)
        {
            case FieldInfo field: value = field.GetValue(target); return true;
            case PropertyInfo property: value = property.GetValue(target); return true;
            default: return false;
        }
    }
}
