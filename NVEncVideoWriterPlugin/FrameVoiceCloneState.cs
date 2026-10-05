using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// A voice's temporary WAV path is deliberately absent from project JSON. Keep it with the description's witness,
// never give a clone ownership of the live TemporaryFile, and still lease/fingerprint the actual WAV for each key.
internal static class FrameVoiceCloneState
{
    internal sealed record Input(Guid Timeline, int TimelineIndex, int ItemIndex, VoiceItem Live, byte[]? Cache, string? Path);
    private sealed record Witness(Input Input, byte[]? Digest);
    private sealed record Witnesses(Witness[] Voices);
    private static readonly ConditionalWeakTable<string, Witnesses> models = new();
    private static readonly FieldInfo? pathField = typeof(VoiceItem).GetField("customVoiceFilePath", BindingFlags.Instance | BindingFlags.NonPublic);

    // Sharing relies on the audited host's read-only use of these arrays. Other builds and subclasses get copies.
    internal static bool CanShare(VoiceItem voice) => voice.GetType() == typeof(VoiceItem)
        && typeof(VoiceItem).Assembly.ManifestModule.ModuleVersionId == new Guid("23e5b5b5-adcf-43b7-b976-b6b63f8dadea");

    internal static Input[] Capture(Timeline[] timelines) => timelines.SelectMany((timeline, index) =>
        timeline.Items.Select((item, itemIndex) => item is VoiceItem voice
            ? new Input(timeline.ID, index, itemIndex, voice, voice.VoiceCache, voice.FilePath) : null).OfType<Input>()).ToArray();

    internal static void Bind(string model, Input[] voices) => models.Add(model, new(voices.Select(input =>
        new Witness(input, input.Cache is null ? null : FrameDescriptionJson.Digest(model,
            $"Timelines[{input.TimelineIndex}].Items[{input.ItemIndex}].VoiceCache", input.Cache))).ToArray()));

    internal static bool Current(string model)
    {
        if (!models.TryGetValue(model, out var witness)) return true;
        try { return witness.Voices.All(voice => voice.Input.Live.FilePath == voice.Input.Path); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    internal static void Restore(Scene clone, string model)
    {
        if (!models.TryGetValue(model, out var captured)) return;
        if (!Current(model)) throw new InvalidDataException("記述後にボイスの音声パスが変化しました。");
        var timelines = clone.Scenes.Timelines.Append(clone.Timeline).Distinct().ToDictionary(timeline => timeline.ID);
        foreach (var witness in captured.Voices)
        {
            var input = witness.Input;
            if (!timelines.TryGetValue(input.Timeline, out var timeline) || input.ItemIndex >= timeline.Items.Count
                || timeline.Items[input.ItemIndex] is not VoiceItem voice || voice.GetType() != input.Live.GetType())
                throw new InvalidDataException("ボイスの複製位置が一致しません。");
            if (input.Cache is { } bytes)
            {
                if (voice.VoiceCache is not { } copy || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), witness.Digest!)
                    || !ReferenceEquals(copy, bytes) && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(copy), witness.Digest!))
                    throw new InvalidDataException("ボイスの複製と記述の内容が一致しません。");
                voice.VoiceCache = CanShare(input.Live) ? bytes : (byte[])bytes.Clone();
            }
            else if (voice.VoiceCache is not null) throw new InvalidDataException("ボイスの複製の内容が一致しません。");
            if (input.Path is not null)
            {
                if (pathField?.FieldType != typeof(string)) throw new NotSupportedException("ボイスの音声パスを複製できません。");
                pathField.SetValue(voice, input.Path);
            }
            if (voice.FilePath != input.Path) throw new InvalidDataException("ボイスの音声パスを複製できません。");
        }
    }
}
