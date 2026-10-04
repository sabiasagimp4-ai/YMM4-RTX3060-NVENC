using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// Temporary investigation (not a check, to be reverted): round 2 of reading YMM4 4.56.1.0 for tachie lip sync: the
// parts' layer settings, temporary voice files, the plugin list, the face picker, the audio readers and resamplers, the
// PSD reader, and the voice cache settings. The text goes to the log gzip+base64 encoded in a few long lines, decoded
// offline. Never fails.
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
        Section("reflection", Reflection);
        Section("members", o => Members(hostDir, o));
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
        Directory.GetFiles(hostDir, "YukkuriMovieMaker*.dll").Concat(Directory.GetFiles(hostDir, "Psd*.dll")).Order(StringComparer.OrdinalIgnoreCase);

    private static void Reflection(StringBuilder o)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        o.AppendLine($"  ProcessorCount={Environment.ProcessorCount}");
        var settings = YukkuriMovieMaker.Plugin.SettingsBase<YukkuriMovieMaker.Settings.YMMSettings>.Default;
        foreach (var property in settings.GetType().GetProperties(all).Where(p => Regex.IsMatch(p.Name, "Voice|Cache|Upsampl|Resampl|LipSync|Kuchipaku|Tachie")))
        {
            object? value;
            try { value = property.GetIndexParameters().Length == 0 ? property.GetValue(settings) : "(indexed)"; }
            catch (Exception error) { value = error.GetType().Name; }
            o.AppendLine($"  YMMSettings.{property.Name} = {value}");
        }
        string root = Path.GetDirectoryName(typeof(YukkuriMovieMaker.Project.Scene).Assembly.Location)!;
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(f, "rhubarb|Psd|Tachie|Aques|Voice|\\\\bin\\\\", RegexOptions.IgnoreCase)).Take(200))
            o.AppendLine($"  file {Path.GetRelativePath(root, file)} {new FileInfo(file).Length}");
    }

    // Single members by name (any type): whether a voice engine requires the project voice cache, the setting itself.
    private static void Members(string hostDir, StringBuilder o)
    {
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        string[] names = ["IsVoiceDataCachingRequired", "IsProjectVoiceCacheEnabled", "VoiceUpsamplingMode", "PickFaceItems"];
        foreach (var file in HostFiles(hostDir))
        {
            CSharpDecompiler decompiler;
            try { decompiler = new CSharpDecompiler(file, settings); }
            catch (Exception error) { o.AppendLine($"{Path.GetFileName(file)}: {error.GetType().Name} {error.Message}"); continue; }
            foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions)
            {
                var members = type.Properties.Where(p => names.Contains(p.Name)).Select(p => (IEntity)p)
                    .Concat(type.Methods.Where(m => names.Contains(m.Name)));
                foreach (var member in members)
                {
                    string code;
                    try { code = decompiler.DecompileAsString(member.MetadataToken); }
                    catch (Exception error) { code = error.GetType().Name; }
                    o.AppendLine($"--- {Path.GetFileName(file)}: {type.FullName}.{member.Name}");
                    o.AppendLine(code.Trim());
                }
            }
        }
    }

    // Exact full names, or simple names (any namespace) in the host's assemblies.
    private static readonly string[] FullNames =
    [
        "YukkuriMovieMaker.Player.Audio.Items.AudioSource",
        "YukkuriMovieMaker.Player.Video.CompositeItemPicker",
        "YukkuriMovieMaker.Plugin.PluginLoader",
    ];

    private static readonly Regex SimpleNames = new(
        "^(LayerConfig|TemporaryFile|CompositeItemPicker|AudioFileSourceFactory|PsdFileSourcePlugin|PsdFolder|PsdLayer|PsdFile|PsdPreset|Resampler|Upsampler|TachieVoiceItemExoDescription)$",
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
                bool wanted = FullNames.Contains(type.FullName) || SimpleNames.IsMatch(type.Name);
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
