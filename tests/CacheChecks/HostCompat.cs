using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// The checks also run on older YMM4 builds (ymm4-compat), which lack a few members the checks use.
internal static class HostCompat
{
    // Animation.SetFirstValue is YMM4 4.55 and later; the obsolete From setter sets the first value the same way in
    // every version.
#pragma warning disable CS0618
    internal static void SetFirst(this Animation animation, double value) => animation.From = value;
#pragma warning restore CS0618

    // Scenes(bool undo) is YMM4 4.49 and later; before, scenes always recorded undo (which the checks do not use).
    internal static readonly bool ScenesTakeUndoFlag = typeof(Scenes).GetConstructor([typeof(bool)]) is not null;

    internal static Scenes NewScenes(bool undo = false) => ScenesTakeUndoFlag ? WithFlag(undo) : WithoutFlag();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Scenes WithFlag(bool undo) => new(undo);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Scenes WithoutFlag() => (Scenes)Activator.CreateInstance(typeof(Scenes), BindingFlags.Public | BindingFlags.Instance, null, [], null)!;

    // VideoInfo.BackgroundColor is YMM4 4.52 and later; before, there is no background to set.
    internal static readonly bool HasBackgroundColor = typeof(VideoInfo).GetProperty("BackgroundColor") is { CanWrite: true };

    internal static void SetBackground(this VideoInfo info, Color color)
    {
        if (HasBackgroundColor) SetBackgroundColor(info, color);
    }

    // An edit of the project the cache must notice: its background color, or on older YMM4 the first shape's opacity.
    internal static void EditModel(Timeline timeline, Color color)
    {
        if (HasBackgroundColor) SetBackgroundColor(timeline.VideoInfo, color);
        else
        {
            var opacity = timeline.Items.OfType<ShapeItem>().First().Opacity;
            opacity.SetFirst(opacity.Values[0].Value == 37 ? 38 : 37);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetBackgroundColor(VideoInfo info, Color color) => info.BackgroundColor = color;
}
