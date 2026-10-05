using System.IO;
using System.Reflection;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// Only the audited bundled simple tachie is admitted. Its volume input distinguishes absence of a voice from
// presence; envelope completion cannot change its image. The host picker supplies the visible faces, while
// file ranges avoid treating an unused default image or the editor's directory as a drawing dependency.
internal static class SimpleTachieDependencies
{
    internal const string AssemblyName = "YukkuriMovieMaker.Plugin.Tachie.SimpleTachie";
    internal const string PluginName = AssemblyName + ".SimpleTachiePlugin";
    internal static readonly Guid ReadBuild = new("be62ee72-e935-4cca-9bba-eb9de4a27cde");

    internal static bool Verified(Type? plugin) => plugin?.FullName == PluginName
        && plugin.Assembly.GetName().Name == AssemblyName
        && plugin.Assembly.ManifestModule.ModuleVersionId == ReadBuild
        && HostFeatures.For(typeof(Scene).Assembly).SimpleTachie
        && FrameCacheKey.IsBundledPluginAssembly(AssemblyName, plugin.Assembly.Location, Path.GetDirectoryName(typeof(Scene).Assembly.Location));

    private static bool Parameter(object? value, Type plugin, string name, bool optional = false) => value is null ? optional
        : value.GetType().Assembly == plugin.Assembly && value.GetType().FullName == AssemblyName + "." + name;

    internal static bool Character(Character? character) => character?.TachieType is { } type && Verified(type)
        && Parameter(character.TachieCharacterParameter, type, "CharacterParameter")
        && Parameter(character.TachieDefaultItemParameter, type, "ItemParameter")
        && Parameter(character.TachieDefaultFaceParameter, type, "FaceParameter", optional: true);

    private static object? FaceParameter(IItem item) => item switch
    {
        VoiceItem voice => voice.TachieFaceParameter,
        TachieFaceItem face => face.TachieFaceParameter,
        _ => null,
    };

    private static IEnumerable<object> FaceParts(IItem item) => item switch
    {
        VoiceItem voice => voice.TachieFaceEffects.Cast<object>().Append(voice.TachieFaceParameter!).Where(value => value is not null),
        TachieFaceItem face => face.TachieFaceEffects.Cast<object>().Append(face.TachieFaceParameter!).Where(value => value is not null),
        _ => [],
    };

    private static IEnumerable<object> SharedParts(IItem item) => item is VoiceItem voice
        ? voice.JimakuVideoEffects.Cast<object>().Concat(voice.AudioEffects) : [];

    internal static IEnumerable<string> FilesWithoutFace(IItem item)
    {
        var omitted = FaceParts(item).OfType<IFileItem>().SelectMany(part => part.GetFiles()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = SharedParts(item).OfType<IFileItem>().SelectMany(part => part.GetFiles());
        if (item is VoiceItem voice && Uri.TryCreate(voice.Hatsuon, UriKind.Absolute, out var uri) && uri.IsFile)
            shared = shared.Append(voice.Hatsuon);
        return item.GetFiles().Where(file => !omitted.Contains(file)).Concat(shared);
    }

    internal static IEnumerable<TimelineResource> ResourcesWithoutFace(IItem item)
    {
        var omitted = FaceParts(item).OfType<IResourceItem>().SelectMany(part => part.GetResources())
            .Where(resource => resource.ResourceType is TimelineResourceType.Image or TimelineResourceType.Video or TimelineResourceType.Audio
                or TimelineResourceType.Tachie || Uri.TryCreate(resource.Key, UriKind.Absolute, out var uri) && uri.IsFile)
            .Select(resource => resource.Key).ToHashSet(StringComparer.Ordinal);
        return item.GetResources().Where(resource => !omitted.Contains(resource.Key) || resource.ResourceType == TimelineResourceType.CustomVoice)
            .Concat(SharedParts(item).OfType<IResourceItem>().SelectMany(part => part.GetResources()));
    }

    internal static bool TryRanges(TachieItem item, Timeline timeline, out FrameDependencyIndex.FileRange[] ranges)
    {
        ranges = [];
        var character = item.Character;
        if (!Character(character) || !Parameter(item.TachieItemParameter, character.TachieType, "ItemParameter")
            || item.Length <= 0 || timeline.Items.Any(candidate => candidate is GroupItem)) return false;
        var pickerType = typeof(Scene).Assembly.GetType("YukkuriMovieMaker.Player.Video.CompositeItemPicker");
        var faceType = typeof(Scene).Assembly.GetType("YukkuriMovieMaker.Project.Items.IFaceItem");
        var pick = pickerType?.GetMethod("PickFaceItems", [typeof(Timeline), typeof(int)]);
        if (faceType is null || pick?.ReturnType != typeof(IEnumerable<>).MakeGenericType(faceType)) return false;
        object picker = Activator.CreateInstance(pickerType!, nonPublic: true)!;
        var allFaces = timeline.Items.Where(faceType.IsInstanceOfType).ToArray();
        if (allFaces.Any(face => face.GetType() != typeof(VoiceItem) && face.GetType() != typeof(TachieFaceItem))) return false;
        var faces = allFaces.Where(face => ReferenceEquals(FrameCacheKey.GetCharacter(face), character)).ToArray();
        long start = item.Frame, end = start + item.Length;
        var boundaries = faces.SelectMany(face => new long[] { face.Frame, (long)face.Frame + face.Length })
            .Append(start).Append(end).Where(frame => start <= frame && frame <= end).Distinct().Order().ToArray();
        if (boundaries.Length > 65536 || end > int.MaxValue) return false;
        var result = new List<FrameDependencyIndex.FileRange>();
        for (int i = 0; i + 1 < boundaries.Length; i++)
        {
            int at = checked((int)boundaries[i]);
            var active = ((System.Collections.IEnumerable)pick.Invoke(picker, [timeline, at])!).Cast<IItem>()
                .Where(face => ReferenceEquals(FrameCacheKey.GetCharacter(face), character))
                .OrderByDescending(face => face.Layer).ToArray();
            // Equal layers lose their ordering in the ordinary per-item hash. Unknown face parameters must never
            // inherit the exemption previously intended only for frames whose tachie was bypassed.
            if (active.GroupBy(face => face.Layer).Any(group => group.Count() > 1)
                || active.Any(face => face.GetType() != typeof(VoiceItem) && face.GetType() != typeof(TachieFaceItem))
                || active.Any(face => !Parameter(FaceParameter(face), character.TachieType, "FaceParameter", optional: true))) return false;
            bool hidden = (bool)item.TachieItemParameter.GetType().GetProperty("IsHiddenWhenNoSpeech")!.GetValue(item.TachieItemParameter)!;
            string? selected = null;
            if (!hidden || active.Any(face => face is VoiceItem))
            {
                selected = active.FirstOrDefault() is { } first && FaceParameter(first) is { } parameter
                    ? parameter.GetType().GetProperty("Face")!.GetValue(parameter) as string : null;
                if (string.IsNullOrEmpty(selected)) selected = item.TachieItemParameter.GetType().GetProperty("DefaultFace")!.GetValue(item.TachieItemParameter) as string;
            }
            var selectedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                if (Uri.TryCreate(selected, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile) return false;
                    selected = uri.LocalPath;
                }
                selected = Path.GetFullPath(selected);
                // Simple tachie loops a video at its own time, unlike VideoItem's rate/offset mapping. Numbered
                // image sequences need a distinct exact mapping; leave those frames to the host for now.
                if (ImageSequence.Files(selected) is not null) return false;
                selectedFiles.Add(selected);
            }
            if (active.FirstOrDefault() is { } selectedFace)
            {
                var effects = FaceParts(selectedFace).Where(part => !ReferenceEquals(part, FaceParameter(selectedFace))).ToArray();
                foreach (string file in effects.OfType<IFileItem>().SelectMany(effect => effect.GetFiles())) AddFile(file);
                foreach (var resource in effects.OfType<IResourceItem>().SelectMany(effect => effect.GetResources()))
                    if (Uri.TryCreate(resource.Key, UriKind.Absolute, out var uri) && uri.IsFile) AddFile(uri.LocalPath);
                    else if (resource.ResourceType is TimelineResourceType.Image or TimelineResourceType.Video or TimelineResourceType.Audio or TimelineResourceType.CustomVoice)
                        AddFile(resource.Key);
            }
            void AddFile(string file)
            {
                if (string.IsNullOrWhiteSpace(file)) return;
                if (Uri.TryCreate(file, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile) throw new NotSupportedException("Nonlocal face effect");
                    file = uri.LocalPath;
                }
                selectedFiles.Add(Path.GetFullPath(file));
            }
            string[] files = selectedFiles.ToArray();
            int length = checked((int)(boundaries[i + 1] - boundaries[i]));
            if (result.LastOrDefault() is { } previous && result.Count != 0 && previous.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
                result[^1] = previous with { Length = checked(previous.Length + length) };
            else result.Add(new(at, length, files));
        }
        ranges = result.ToArray();
        return true;
    }
}
