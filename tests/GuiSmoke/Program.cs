using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Test-only: writes the project the GUI smoke test opens in YMM4 (tools/ci/gui-smoke.ps1): two shapes and a text
// over 10 seconds at 1280x720 / 30 fps. Uses YMM4's own model and serializer; no application is started.
//   dotnet run --project tests/GuiSmoke -- <YMM4 dir> <out.ymmp>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string hostDir = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(hostDir, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return Write(Path.GetFullPath(args[1]));
    }

    private static int Write(string output)
    {
        // The plugin loader's static constructor would load plugins from this executable's folder.
        var loader = typeof(PluginAssemblyLoader);
        new Harmony("ymm.tests.gui-smoke").Patch(loader.TypeInitializer!, prefix: new HarmonyMethod(typeof(Program), nameof(Skip)));
        AccessTools.StaticFieldRefAccess<IEnumerable<Assembly>>(AccessTools.Field(loader, "<Assemblies>k__BackingField"))() =
            new[] { typeof(Scene).Assembly, typeof(CacheProvider).Assembly };
        foreach (var name in new[] { "<IncompatiblePluginAssemblies>k__BackingField", "loadFailures" })
            if (AccessTools.Field(loader, name) is { } field)
                AccessTools.StaticFieldRefAccess<object>(field)() ??= Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GetGenericArguments()))!;

        var timeline = new Timeline { Name = "gui-smoke" };
        timeline.VideoInfo.Width = 1280;
        timeline.VideoInfo.Height = 720;
        timeline.VideoInfo.FPS = 30;
        var first = new ShapeItem { Frame = 0, Length = 300, Layer = 0 };
        first.X.SetFirstValue(-250);
        var second = new ShapeItem { Frame = 150, Length = 150, Layer = 1 };
        second.X.SetFirstValue(250);
        var text = new TextItem { Frame = 30, Length = 240, Layer = 2, Text = "cache smoke", Font = "Arial" };
        text.Y.SetFirstValue(200);
        timeline.Items = timeline.Items.Add(first).Add(second).Add(text);
        var scenes = new Scenes(false);
        scenes.AddScene(timeline);
        var project = new YukkuriMovieMaker.Project.Project(0, scenes, output, string.Empty, new Dictionary<string, SerializableToolState>());
        YukkuriMovieMaker.Json.Json.Save(project, output);
        Console.WriteLine($"Wrote {output}: {timeline.Items.Count} items, {timeline.Length} frames");
        return 0;
    }

    private static bool Skip() => false;
}
