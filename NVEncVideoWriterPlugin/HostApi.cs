using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// The plugin is built against the YMM4 whose code was read, and its reference to YukkuriMovieMaker is lowered to the
// oldest YMM4 it supports (HostReferenceVersion.targets), so it also loads on YMM4 builds that lack members it uses.
// Those members are used only through here. A method that names a member the running YMM4 lacks fails when it is
// compiled (MissingMethodException, TypeLoadException), so each use sits in a method of its own that runs only when
// the member exists. CI lists what the oldest supported YMM4 lacks and accepts only the members named in this file
// (tools/compat/guarded-host-apis.txt).
internal static class HostApi
{
    private static readonly Assembly PluginApi = typeof(IVideoFileWriter).Assembly;

    // IVideoFileWriter3 (YMM4 4.54): the writer declares that it takes GPU frames. Older builds hand GPU frames to an
    // IVideoFileWriter2. No type of the plugin implements it (YMM4 loads every type of a plugin): GpuWriterProxy does,
    // at run time.
    internal static readonly Type? VideoFileWriter3 = PluginApi.GetType("YukkuriMovieMaker.Plugin.FileWriter.IVideoFileWriter3");

    // CacheProvider.InvalidateIfSourceSettingsChanged (4.54). The cache clears the provider right after it.
    private static readonly bool InvalidateIfSourceSettingsChangedExists =
        typeof(CacheProvider).GetMethod("InvalidateIfSourceSettingsChanged", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null;

    // Scenes(bool) (4.49) and VideoInfo.BackgroundColor (4.52), for the idle pre-renderer's clone of a project.
    private static readonly bool ScenesWithFlagExists = typeof(Scenes).GetConstructor([typeof(bool)]) is not null;
    private static readonly bool BackgroundColorExists = typeof(VideoInfo).GetProperty("BackgroundColor") is { CanRead: true, CanWrite: true };

    // ControlTagParser.Parse with the signature of 4.52 (the type itself is 4.51, with another signature there).
    private static int controlTagParser; // 0: not tried, 1: available, -1: missing

    internal static void InvalidateIfSourceSettingsChanged(CacheProvider provider)
    {
        if (InvalidateIfSourceSettingsChangedExists) Invalidate(provider);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Invalidate(CacheProvider provider) => provider.InvalidateIfSourceSettingsChanged();

    // The scenes of a project clone without YMM4's default scene.
    internal static Scenes NewScenes() => ScenesWithFlagExists ? NewScenesWithoutDefault()
        : throw new NotSupportedException("このYMM4では先読み用にプロジェクトを複製できません。");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Scenes NewScenesWithoutDefault() => new(false);

    internal static void CopyBackgroundColor(VideoInfo from, VideoInfo to)
    {
        if (BackgroundColorExists) CopyBackground(from, to);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyBackground(VideoInfo from, VideoInfo to) => to.BackgroundColor = from.BackgroundColor;

    // The decorations control tags in a text produce. Throws NotSupportedException when this YMM4 cannot parse them
    // the way the plugin was built for (the text is then not cached).
    internal static ImmutableList<TextDecoration> ControlTagDecorations(string text, string font)
    {
        if (Volatile.Read(ref controlTagParser) >= 0)
        {
            try
            {
                var decorations = ParseControlTags(text, font);
                Volatile.Write(ref controlTagParser, 1);
                return decorations;
            }
            catch (Exception ex) when (Volatile.Read(ref controlTagParser) == 0 && ex is MissingMemberException or TypeLoadException)
            {
                Volatile.Write(ref controlTagParser, -1);
            }
        }
        throw new NotSupportedException("このYMM4では文字の制御タグを解析できません。");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ImmutableList<TextDecoration> ParseControlTags(string text, string font) =>
        ControlTagParser.Parse(text, ImmutableList<TextDecoration>.Empty, 1.0, font, false, false).decorations;
}
