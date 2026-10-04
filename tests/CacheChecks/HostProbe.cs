using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// Temporary investigation (not a check, to be reverted): how YMM4 4.56.1.0 computes a tachie's lip sync (the voice
// volume envelope, vowel lip sync, the waits and their timeouts), which voice and faces a tachie draws at a frame,
// which thread the preview renders on, and what the bundled tachie plugins read. The text goes to the log gzip+base64
// encoded in a few long lines, decoded offline. Never fails.
internal static class HostProbe
{
    internal static void Run(string hostDir)
    {
        var output = new StringBuilder();
        void Section(string name, Action<StringBuilder> body)
        {
            output.AppendLine("=== " + name);
            try { body(output); }
            catch (Exception error) { output.AppendLine("section failed: " + error); }
        }
        Section("types", o => Types(hostDir, o));
        Section("reflection", Reflection);
        Section("decompile", o => Decompile(hostDir, o));
        byte[] raw = Encoding.UTF8.GetBytes(output.ToString());
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(raw);
        string text = Convert.ToBase64String(packed.ToArray());
        const int Chunk = 8000;
        Console.WriteLine($"HOSTPROBE-Z BEGIN raw={raw.Length} b64={text.Length}");
        for (int i = 0; i < text.Length; i += Chunk)
            Console.WriteLine($"HPZ|{i / Chunk}|{text.Substring(i, Math.Min(Chunk, text.Length - i))}");
        Console.WriteLine("HOSTPROBE-Z END");
        Console.Out.Flush();
    }

    private static IEnumerable<string> HostFiles(string hostDir) =>
        Directory.GetFiles(hostDir, "YukkuriMovieMaker*.dll").Order(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Interesting = new(
        "LipSync|Envelope|Tachie|Mouth|Kuchipaku|Mabataki|Blink|StatelessRandom|FaceItem|VoiceSource|EffectedItemSource|TimelineVideoPlayer|Lip|PsdFileSettings|Psd.*Animation|Vowel|Phoneme|Hatsuon|VoiceCache|AudioSource",
        RegexOptions.CultureInvariant);

    private static void Types(string hostDir, StringBuilder o)
    {
        foreach (var file in HostFiles(hostDir))
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                string name = TypeName(md, handle);
                if (!Interesting.IsMatch(name)) continue;
                var type = md.GetTypeDefinition(handle);
                o.AppendLine($"  {Path.GetFileName(file)}: {name} fields={type.GetFields().Count} methods={type.GetMethods().Count}");
            }
        }
    }

    private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        string name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? md.GetString(type.Namespace) + "." + name : TypeName(md, declaring) + "/" + name;
    }

    private static void Reflection(StringBuilder o)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var type in new[] { typeof(YukkuriMovieMaker.Project.Character), typeof(YukkuriMovieMaker.Project.Items.VoiceItem),
            typeof(YukkuriMovieMaker.Project.Items.TachieItem) })
        {
            o.AppendLine($"-- {type.FullName}");
            foreach (var property in type.GetProperties(all).OrderBy(p => p.Name, StringComparer.Ordinal))
                o.AppendLine($"  {property.Name} : {property.PropertyType.FullName} get={property.GetMethod?.Attributes}");
            foreach (var method in type.GetMethods(all).Where(m => m.DeclaringType == type && !m.IsSpecialName).OrderBy(m => m.Name, StringComparer.Ordinal))
                o.AppendLine($"  {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) : {method.ReturnType.Name} {method.Attributes}");
        }
        o.AppendLine($"  ProcessorCount={Environment.ProcessorCount}");
    }

    // Exact full names, or simple names (any namespace) in the host's assemblies.
    private static readonly string[] FullNames =
    [
        "YukkuriMovieMaker.Player.Video.Items.TachieSource",
        "YukkuriMovieMaker.Player.Video.TimelineSource",
        "YukkuriMovieMaker.Player.TimelineVideoPlayer",
        "YukkuriMovieMaker.Project.Items.VoiceItem",
        "YukkuriMovieMaker.Project.Items.TachieItem",
        "YukkuriMovieMaker.Project.Items.TachieFaceItem",
        "YukkuriMovieMaker.Player.Audio.EffectedItemSource",
        "YukkuriMovieMaker.Project.Character",
    ];

    private static readonly Regex SimpleNames = new(
        "^(.*LipSync.*|.*Envelope.*|TachieSourceFactory|StatelessRandom|.*TachieSource.*|PsdFileSettings|PsdMouthAnimation|PsdEyeAnimation|PsdVowelMouthAnimation|" +
        "CharacterParameter|ItemParameter|FaceParameter|ParameterBase|.*TachiePlugin|.*TachieCharacterParameter|.*TachieItemParameter|.*TachieFaceParameter|" +
        "MouthShape|.*VoiceSource|VoiceAudioSource|TimelineAudioSource|.*Mabataki.*|ITachieSource2|TachieDescription|TachieSourceDescription|TachieFaceDescription|IFaceItem)$",
        RegexOptions.CultureInvariant);

    private static void Decompile(string hostDir, StringBuilder o)
    {
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        foreach (var file in HostFiles(hostDir))
        {
            CSharpDecompiler decompiler;
            try { decompiler = new CSharpDecompiler(file, settings); }
            catch (Exception error) { o.AppendLine($"{Path.GetFileName(file)}: {error.GetType().Name} {error.Message}"); continue; }
            foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions)
            {
                if (type.DeclaringTypeDefinition is not null) continue;
                bool wanted = FullNames.Contains(type.FullName) || SimpleNames.IsMatch(type.Name)
                    || type.Name == "Layer" && type.Namespace.Contains("AnimationTachie", StringComparison.Ordinal);
                if (!wanted) continue;
                string code;
                try { code = decompiler.DecompileTypeAsString(type.FullTypeName); }
                catch (Exception error) { o.AppendLine($"{type.FullName}: {error.GetType().Name}"); continue; }
                o.AppendLine($">>> {Path.GetFileName(file)}: {type.FullName}");
                o.AppendLine(code);
                o.AppendLine($"<<< {type.FullName}");
            }
        }
    }
}
