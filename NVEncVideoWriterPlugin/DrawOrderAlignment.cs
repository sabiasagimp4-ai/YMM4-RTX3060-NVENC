using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using YukkuriMovieMaker.Project;

namespace NVEncVideoWriterPlugin;

// YMM4 draws a frame's items in the order TimelineSource.GetOrderedTimelineResources gives: always-on-top, then Z (when
// the item enables it), then layer (YMM4 4.47.0.0 to 4.56.1.0). Items equal in all three come in the order of the
// renderer's resource dictionary, which read-ahead, parallel creation and removals change from one render to the next,
// so YMM4 itself may stack two overlapping items of one layer either way. The plugin orders such items by the timeline's
// item list, so that every renderer (preview, export, idle, the renderers of composite groups and scenes) stacks them
// alike, and FrameDependencyIndex keys the frames by that order. Only the order YMM4 leaves undecided changes.
internal static class DrawOrderAlignment
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object gate = new();
    private static MethodInfo? target;
    private static Func<object, Scene>? sceneOf;
    private static Func<object, YukkuriMovieMaker.Project.Items.IVideoItem>? keyOf;
    private static Func<object, float>? zOf;
    private static Func<int, IList>? newList;
    private static readonly ConditionalWeakTable<object, Dictionary<object, int>> positions = new();
    private static string? owner;

    internal static bool Installed
    {
        get { lock (gate) return owner is { } id && target is not null && (Harmony.GetPatchInfo(target)?.Postfixes.Any(patch => patch.owner == id) ?? false); }
    }

    // Never throws: without it, frames whose items tie keep rendering normally.
    internal static bool TryInstall(Assembly host, Harmony harmony, out string reason)
    {
        lock (gate)
        {
            reason = string.Empty;
            if (owner is not null) return owner == harmony.Id;
            try
            {
                var type = host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
                var method = type.GetMethod("GetOrderedTimelineResources", Instance, Type.EmptyTypes)
                    ?? throw new MissingMethodException(type.FullName, "GetOrderedTimelineResources");
                if (method.ReturnType is not { IsGenericType: true } enumerable || enumerable.GetGenericTypeDefinition() != typeof(IEnumerable<>)
                    || enumerable.GetGenericArguments()[0] is not { IsGenericType: true } pair || pair.GetGenericTypeDefinition() != typeof(KeyValuePair<,>)
                    || pair.GetGenericArguments()[0] != typeof(YukkuriMovieMaker.Project.Items.IVideoItem))
                    throw new NotSupportedException("GetOrderedTimelineResources contract changed");
                var resource = pair.GetGenericArguments()[1];
                var z = resource.GetMethod("GetZIndex", Instance, Type.EmptyTypes);
                var scene = type.GetField("scene", Instance);
                if (z?.ReturnType != typeof(float) || scene?.FieldType != typeof(Scene))
                    throw new NotSupportedException("TimelineSource draw order contract changed");
                if (Harmony.GetPatchInfo(method)?.Owners.Any(id => id != harmony.Id) ?? false)
                    throw new NotSupportedException("GetOrderedTimelineResources has an external Harmony owner");
                var boxed = Expression.Parameter(typeof(object));
                var unboxed = Expression.Unbox(boxed, pair);
                keyOf = Expression.Lambda<Func<object, YukkuriMovieMaker.Project.Items.IVideoItem>>(Expression.Property(unboxed, "Key"), boxed).Compile();
                zOf = Expression.Lambda<Func<object, float>>(Expression.Call(Expression.Property(unboxed, "Value"), z), boxed).Compile();
                var source = Expression.Parameter(typeof(object));
                sceneOf = Expression.Lambda<Func<object, Scene>>(Expression.Field(Expression.Convert(source, type), scene), source).Compile();
                var capacity = Expression.Parameter(typeof(int));
                var list = typeof(List<>).MakeGenericType(pair);
                newList = Expression.Lambda<Func<int, IList>>(Expression.Convert(Expression.New(list.GetConstructor([typeof(int)])!, capacity), typeof(IList)), capacity).Compile();
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(DrawOrderAlignment), nameof(OrderTies)));
                target = method;
                owner = harmony.Id;
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                reason = "同じレイヤーの重なりの描画順をそろえられません: " + error.GetBaseException().Message;
                return false;
            }
        }
    }

    internal static void Uninstall(Harmony harmony)
    {
        lock (gate)
        {
            if (owner != harmony.Id || target is null) return;
            try { harmony.Unpatch(target, HarmonyPatchType.Postfix, harmony.Id); } catch (Exception error) when (error is not OutOfMemoryException) { }
            owner = null;
        }
    }

    private static void OrderTies(object __instance, ref object __result)
    {
        try
        {
            var key = keyOf!; var z = zOf!;
            var pairs = new List<object>();
            foreach (object pair in (IEnumerable)__result) pairs.Add(pair);
            var ranks = new (int Top, float Z, int Layer, int Index)[pairs.Count];
            bool ties = false;
            for (int i = 0; i < pairs.Count; i++)
            {
                var item = key(pairs[i]);
                ranks[i] = (item.IsAlwaysOnTop ? 1 : 0, item.IsZOrderEnabled ? z(pairs[i]) : 0f, item.Layer, 0);
                ties |= i != 0 && ranks[i].Top == ranks[i - 1].Top && ranks[i].Z.Equals(ranks[i - 1].Z) && ranks[i].Layer == ranks[i - 1].Layer;
            }
            var ordered = newList!(pairs.Count);
            if (ties)
            {
                var positions = Positions(sceneOf!(__instance).Timeline.Items);
                for (int i = 0; i < pairs.Count; i++) ranks[i].Index = positions.GetValueOrDefault(key(pairs[i]), int.MaxValue);
                // Stable: YMM4's order (top, Z, layer), then the item list.
                foreach (int i in Enumerable.Range(0, pairs.Count).OrderBy(i => ranks[i].Top).ThenBy(i => ranks[i].Z).ThenBy(i => ranks[i].Layer).ThenBy(i => ranks[i].Index))
                    ordered.Add(pairs[i]);
            }
            else foreach (object pair in pairs) ordered.Add(pair);
            __result = ordered;
        }
        // The host's own order (an edit changed the item list while it was read, for example); the edit invalidates
        // what the frame stores.
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // The item list's positions. YMM4 replaces the immutable list at every edit, so its positions are kept with it;
    // another list is read again.
    private static Dictionary<object, int> Positions(IEnumerable<YukkuriMovieMaker.Project.Items.IItem> items) =>
        items is System.Collections.Immutable.IImmutableList<YukkuriMovieMaker.Project.Items.IItem> ? positions.GetValue(items, Read) : Read(items);

    private static Dictionary<object, int> Read(object items)
    {
        var map = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int position = 0;
        foreach (var item in (IEnumerable<YukkuriMovieMaker.Project.Items.IItem>)items) map.TryAdd(item, position++);
        return map;
    }
}
