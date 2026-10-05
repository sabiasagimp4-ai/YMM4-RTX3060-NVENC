using System.Reflection;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;

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
}
