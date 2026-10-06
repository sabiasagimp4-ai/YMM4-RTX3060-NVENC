using System.Globalization;
using System.Text;
using NVEncVideoWriterPlugin;

// What the animation tachie's LayerConfig reads from an attached INI (AnimationTachieDependencies.TryReadIni): from
// the defaults, Shift-JIS, "key=value" before a ';', the last value of a key; refused when the thread's culture reads a
// number differently from the invariant culture.
internal static class AttachedIniChecks
{
    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ymm-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string Write(string text)
            {
                string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".ini");
                File.WriteAllText(path, text, Encoding.GetEncoding("shift-jis"));
                return path;
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Check(AnimationTachieDependencies.TryReadIni(Write("blend=3;screen\nopacity=50.5\nplaceon=face\nblend=4\n"), out var read)
                && read == (4, 50.5, "face"), $"Read {read}");
            Check(AnimationTachieDependencies.TryReadIni(Write("opacity=x\nunknown=1\nplaceon\n"), out read) && read == (0, 100.0, null),
                $"Unparsed and unknown lines must leave the defaults: {read}");
            Check(AnimationTachieDependencies.TryReadIni(Write("placeon=顔\n"), out read) && read.PlaceOn == "顔", "Shift-JIS text was not read");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check(!AnimationTachieDependencies.TryReadIni(Write("opacity=0,5\n"), out _), "A number the culture reads differently was admitted");
            Check(AnimationTachieDependencies.TryReadIni(Write("opacity=50\nblend=2\n"), out read) && read == (2, 50.0, null), "A plain number was refused");
            Console.WriteLine("Attached INI: defaults, comments, last value, Shift-JIS and culture-dependent numbers passed.");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Attached INI: " + message); }
}
