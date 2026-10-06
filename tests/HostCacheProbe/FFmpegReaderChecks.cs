using System.IO;
using System.Reflection;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// The FFmpeg reader on the real host, with seeks: a timeline video read by FFmpeg (an MP4, and the same clip in an
// MPEG-TS container named .m2ts, whose seeks can land after the requested time on YMM4 before 4.54.0.1) is rendered
// in an order that seeks back and forth, twice with the cache. Every frame the cache hands out must be the pixels the
// host renders for that time when it decodes in order from the start (a frame shown after a seek that landed late
// must not be stored). The clips are <video>-ffmpeg.mp4 / .m2ts next to the --video clip (ymm4-compat makes them).
internal static class FFmpegReaderChecks
{
    private const string AssemblyName = "YukkuriMovieMaker.Plugin.FileSource.FFmpeg";
    private const int Width = 320, Height = 180, Length = 120;
    private static IVideoFileSourcePlugin? reader;
    private static string[] files = [];
    // The FFmpeg sources this check created, newest last (for the trace when nothing was stored).
    private static readonly List<object> created = [];

    internal static void Run(Assembly host, IGraphicsDevicesAndContext context, string? videoPath, HostFeatures features)
    {
        if (videoPath is null) { Console.WriteLine("FFmpeg reader check skipped (pass --video <mp4>)"); return; }
        string stem = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + "-ffmpeg");
        files = new[] { stem + ".mp4", stem + ".m2ts" }.Where(File.Exists).Select(Path.GetFullPath).ToArray();
        if (files.Length == 0) { Console.WriteLine("FFmpeg reader check skipped (no <video>-ffmpeg.mp4 / .m2ts)"); return; }
        string directory = Path.GetDirectoryName(host.Location)!;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName)
            ?? (File.Exists(Path.Combine(directory, AssemblyName + ".dll")) ? Assembly.LoadFrom(Path.Combine(directory, AssemblyName + ".dll")) : null);
        var pluginType = assembly?.GetType(AssemblyName + ".FFmpegVideoFileSourcePlugin");
        if (pluginType is null) { Console.WriteLine("FFmpeg reader check skipped: this YMM4 has no FFmpeg video reader"); return; }
        if (!features.DecoderVerified(AssemblyName)
            || !FrameRenderReadiness.Coverage.Any(line => line.StartsWith(FrameRenderReadiness.FFmpegTypeName + ": FFmpeg", StringComparison.Ordinal)))
        {
            Console.WriteLine("FFmpeg reader check skipped: the FFmpeg reader is not trusted on this build");
            return;
        }
        // YMM4 finds its FFmpeg libraries next to the running program (AppDirectories, Environment.ProcessPath), which
        // here is the probe: point FFmpeg at the app's folder first, as YMM4 does at its own (the first call wins).
        string libraries = Path.Combine(directory, "Resources", "bin", "x64", "ffmpeg");
        if (!Directory.Exists(libraries) || !Directory.EnumerateFiles(libraries, "avformat-*.dll").Any())
        {
            Console.WriteLine("FFmpeg reader check skipped: no FFmpeg libraries in " + libraries);
            return;
        }
        const string Interop = "YukkuriMovieMaker.Interop.FFmpeg";
        var module = (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == Interop)
            ?? Assembly.LoadFrom(Path.Combine(directory, Interop + ".dll"))).GetType("YukkuriMovieMaker.Plugin.FileSource.FFmpeg.Utilities.FFmpegModule", true)!;
        module.GetMethod("EnsureInitialized", [typeof(string)])!.Invoke(null, [libraries]);
        reader = (IVideoFileSourcePlugin)Activator.CreateInstance(pluginType, nonPublic: true)!;
        var harmony = new Harmony("ymm.tests.ffmpeg-reader");
        harmony.Patch(typeof(VideoFileSourceFactory).GetMethod(nameof(VideoFileSourceFactory.Create))!,
            prefix: new HarmonyMethod(typeof(FFmpegReaderChecks), nameof(CreateWithFFmpeg)));
        try
        {
            foreach (string file in files) Check(host, context, file);
        }
        finally { harmony.UnpatchAll(harmony.Id); reader = null; files = []; }
    }

    // VideoFileSourceFactory.Create as the host does it, with the FFmpeg reader for these files.
    private static bool CreateWithFFmpeg(IGraphicsDevices devices, string filePath, ref IVideoFileSource? __result)
    {
        if (reader is null || !files.Contains(Path.GetFullPath(filePath), StringComparer.OrdinalIgnoreCase)) return true;
        var context = devices.CreateContext();
        var source = reader.CreateVideoFileSource(context, filePath) ?? throw new InvalidOperationException("FFmpeg could not open " + filePath);
        lock (created) created.Add(source);
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var resource = Activator.CreateInstance(typeof(CachedVideoFileSource).Assembly.GetType("YukkuriMovieMaker.Plugin.VideoResource", true)!,
            any, null, [context, source], null)!;
        __result = (IVideoFileSource)Activator.CreateInstance(typeof(CachedVideoFileSource), any, null, [filePath, resource], null)!;
        return false;
    }

    // The newest FFmpeg source's clock, as FrameRenderReadiness reads it.
    private static string Clock()
    {
        object? source;
        lock (created) source = created.LastOrDefault();
        if (source is null) return "?";
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string Read(string name) => source.GetType().GetField(name, any)?.GetValue(source) is TimeSpan value ? value.TotalSeconds.ToString("0.0000") : "-";
        return $"{Read("currentTime")} +{Read("currentDuration")} ({Read("streamStartTime")})";
    }

    private static void Check(Assembly host, IGraphicsDevicesAndContext context, string file)
    {
        var timeline = new Timeline();
        timeline.VideoInfo.Width = Width; timeline.VideoInfo.Height = Height; timeline.VideoInfo.FPS = 30;
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        timeline.Items = timeline.Items.Add(new VideoItem { FilePath = file, Frame = 0, Length = Length });
        var scene = new Scene(timeline, scenes, []);
        var dc = context.DeviceContext;
        ITimelineSource NewSource() => (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        byte[] Render(ITimelineSource source, int frame)
        {
            source.Update(timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
            return TimelineFrameCache.Capture(dc, source.Output, Width, Height, new(-Width / 2f, -Height / 2f))!;
        }

        // The host's frames decoded in order from the start, cache off.
        TimelineFrameCache.Enabled = false;
        byte[][] reference;
        using (var source = NewSource()) reference = Enumerable.Range(0, Length).Select(frame => Render(source, frame)).ToArray();
        int moving = Enumerable.Range(1, Length - 1).Count(frame => !reference[frame].SequenceEqual(reference[frame - 1]));
        if (moving < Length / 2) throw new InvalidOperationException($"The FFmpeg clip hardly moves ({moving} of {Length - 1} frames)");

        // Back and forth (backwards and far ahead seek; short steps ahead decode on), twice with the cache.
        int[] order = [0, 1, 2, 3, 70, 71, 72, 20, 21, 22, 23, 100, 101, 5, 6, 7, 55, 56, 57, 58, 110, 111, 112, 30, 31, 90, 91, 92, 15, 16];
        TimelineFrameCache.Enabled = true;
        TimelineFrameCache.Clear();
        int served = 0, hostDiffers = 0;
        var trace = new List<string>();
        for (int pass = 0; pass < 4; pass++)
        {
            using var source = NewSource();
            foreach (int frame in order)
            {
                long hits = TimelineFrameCache.Hits;
                var pixels = Render(source, frame);
                bool fromCache = TimelineFrameCache.Hits > hits;
                if (pass == 1 && trace.Count < order.Length) trace.Add($"  {frame}: {(fromCache ? "cache" : pixels.SequenceEqual(reference[frame]) ? "host" : "host, other pixels")} {Clock()} {TimelineFrameCache.Status}");
                if (fromCache)
                {
                    served++;
                    if (!pixels.SequenceEqual(reference[frame]))
                        throw new InvalidOperationException($"{Path.GetFileName(file)}: the cache handed out frame {frame} unlike the host's in-order render");
                }
                else if (!pixels.SequenceEqual(reference[frame])) hostDiffers++;
            }
            if (pass == 0) Thread.Sleep(500); // the clip is fingerprinted in the background before its frames are stored
        }
        if (served == 0)
        {
            Console.WriteLine($"{Path.GetFileName(file)}, second pass (frame: shown, the reader's currentTime +currentDuration (streamStartTime), cache status):");
            foreach (string line in trace) Console.WriteLine(line);
            // MPEG-TS on YMM4 4.54.0.0 (no re-seek): every interval a seek produced begins at the requested time and lasts
            // past the frames read after it (the host shows other frames there), so none is verifiable. An MP4 always is.
            if (Path.GetExtension(file) != ".m2ts")
                throw new InvalidOperationException($"{Path.GetFileName(file)}: no FFmpeg frame was ever stored: " + TimelineFrameCache.Status);
            Console.WriteLine($"FFmpeg reader ({Path.GetExtension(file)}): none of {order.Length * 4} seek-order frames was verifiable, none stored"
                + $" (the host drew {hostDiffers} other frames after seeks)");
            return;
        }
        Console.WriteLine($"FFmpeg reader ({Path.GetExtension(file)}): {served} of {order.Length * 4} seek-order frames from the cache, all equal to the in-order render"
            + (hostDiffers == 0 ? "" : $"; the host itself drew {hostDiffers} other frames after seeks (not stored)"));
    }
}
