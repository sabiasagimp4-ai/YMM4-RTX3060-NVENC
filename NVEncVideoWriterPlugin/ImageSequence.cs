using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;

namespace NVEncVideoWriterPlugin;

// YMM4's image sequence reader (WICSequentialImageVideoSource, 4.56.1.0) plays a video item's file when it is not a
// video by its extension and its name ends in a number: the files with the same name before the number, in the order
// of their numbers from the lowest, as long as they continue from the file's own number (so only the lowest-numbered
// file of a name starts a sequence), one image per 1/60 s. Update(t) shows the image at FrameTime.TimeToFrame(t, 60),
// clamped to the list, and loads it synchronously. A root frame of the item depends only on the image it shows, which
// the key finds with YMM4's own time mapping (VideoSource.CalculateSourceTime: playback rate and its animation,
// content offset, loop); FrameRenderReadiness checks after the render that the image shown was that one.
internal static partial class ImageSequence
{
    private const int ImagesPerSecond = 60;
    private const int MaximumFrames = 1_000_000;

    [GeneratedRegex("[0-9]+$")]
    private static partial Regex TailNumber();

    // The files the reader plays for `path`, as it lists them (full paths); null when it does not read `path`.
    internal static string[]? Files(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            || SettingsBase<FileSettings>.Default.FileExtensions.GetFileType(path).HasFlag(FileType.動画)) return null;
        string name = Path.GetFileNameWithoutExtension(path);
        var match = TailNumber().Match(name);
        if (!match.Success || !int.TryParse(match.Value, out int start)) return null;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return null;
        string prefix = TailNumber().Replace(name, "");
        string extension = Path.GetExtension(path);
        string stem = directory + Path.DirectorySeparatorChar + prefix;
        var numbered = Directory.GetFiles(directory, prefix + "*" + extension)
            .Select(file => (Path: file, Valid: int.TryParse(file.Substring(stem.Length, file.Length - extension.Length - stem.Length), out int index), Index: index))
            .Where(file => file.Valid)
            .OrderBy(file => file.Index);
        int next = start;
        var files = numbered.TakeWhile(file => file.Index == next++).Select(file => Path.GetFullPath(file.Path)).ToArray();
        return files.Length == 0 ? null : files;
    }

    // The reader lists its files once, and YMM4 keeps the reader for the file path while it is in use, so after the
    // files of a sequence change it may still draw from the old list. A sequence whose list differs from the first
    // one seen in this process is not keyed until YMM4 restarts.
    private static readonly ConcurrentDictionary<string, string> firstListings = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Unchanged(string path, string[] files)
    {
        string listing = string.Join("\n", files);
        if (firstListings.Count > 4096) firstListings.Clear();
        return firstListings.GetOrAdd(Path.GetFullPath(path), listing) == listing;
    }

    // The file each frame of a root video item shows (index: the frame within the item), or null when YMM4's time
    // mapping cannot be used.
    internal static string[]? FrameFiles(VideoItem item, string[] files, int fps)
    {
        if (Mapping.Value is not { } mapping || mapping.RateMap.GetValue(item) is not { } rateMap
            || files.Length == 0 || fps <= 0 || item.Length <= 0 || item.Length > MaximumFrames) return null;
        TimeSpan duration = FrameTime.FrameToTime(files.Length, ImagesPerSecond);
        var shown = new string[item.Length];
        for (int frame = 0; frame < shown.Length; frame++)
        {
            TimeSpan time = mapping.SourceTime(rateMap, FrameTime.FrameToTime(frame, fps), item.Length, fps, item.ContentOffset, item.ContentLength,
                item.IsLooped, duration);
            shown[frame] = files[Math.Clamp(FrameTime.TimeToFrame(time, ImagesPerSecond), 0, files.Length - 1)];
        }
        return shown;
    }

    private sealed record HostMapping(PropertyInfo RateMap,
        Func<object, TimeSpan, int, int, TimeSpan, TimeSpan, bool, TimeSpan?, TimeSpan> SourceTime);

    // VideoSource.CalculateSourceTime(PlaybackRateMap, item time, item length, fps, content offset, content length,
    // looped, source duration) and VideoItem.PlaybackRateMap, both internal (4.56.1.0).
    private static readonly Lazy<HostMapping?> Mapping = new(() =>
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
        var method = typeof(VideoItem).Assembly.GetType("YukkuriMovieMaker.Player.Video.Items.VideoSource")
            ?.GetMethod("CalculateSourceTime", any | BindingFlags.Static);
        var rateMap = typeof(VideoItem).GetProperty("PlaybackRateMap", any | BindingFlags.Instance);
        if (method is null || rateMap?.GetMethod is null || method.ReturnType != typeof(TimeSpan)) return null;
        Type[] expected = [rateMap.PropertyType, typeof(TimeSpan), typeof(int), typeof(int), typeof(TimeSpan), typeof(TimeSpan), typeof(bool), typeof(TimeSpan?)];
        if (!method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(expected)) return null;
        var parameters = expected.Select((type, index) => Expression.Parameter(index == 0 ? typeof(object) : type)).ToArray();
        var call = Expression.Call(method, parameters.Select((parameter, index) => index == 0 ? Expression.Convert(parameter, expected[0]) : (Expression)parameter));
        return new HostMapping(rateMap,
            Expression.Lambda<Func<object, TimeSpan, int, int, TimeSpan, TimeSpan, bool, TimeSpan?, TimeSpan>>(call, parameters).Compile());
    });
}
