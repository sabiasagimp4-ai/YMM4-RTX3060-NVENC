using System.IO;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;

var path = Path.Combine(Path.GetTempPath(), $"ymm4-rtx3060-smoke-{Guid.NewGuid():N}.mp4");
var videoInfo = new VideoInfo { Width = 320, Height = 180, FPS = 30, Hz = 48000 };
var plugin = new NvencVideoFileWriterPlugin();

using (var writer = plugin.CreateVideoFileWriter(path, videoInfo))
{
    try
    {
#pragma warning disable CS0618 // Deliberately exercise the legacy host fallback.
        writer.WriteVideo(Array.Empty<byte>());
#pragma warning restore CS0618
        throw new Exception("CPU frame fallback was silently accepted.");
    }
    catch (NotSupportedException) { }
}

using (var writer = plugin.CreateVideoFileWriter(path, videoInfo))
{
    writer.WriteAudio([0f, 0f]);
    try
    {
        writer.Dispose();
        throw new Exception("Audio without video was silently discarded.");
    }
    catch (InvalidOperationException) { }
}

if (File.Exists(path))
    throw new Exception("Failed export replaced the final MP4 path.");
Console.WriteLine("Managed failure paths OK");
