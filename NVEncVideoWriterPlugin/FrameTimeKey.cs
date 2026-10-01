namespace NVEncVideoWriterPlugin;

// Keys the exact requested time. A time one tick away from a frame boundary can make a decoder or a time-dependent
// effect pick another sample, so near-equal times are never merged. This loses no reuse: every renderer in
// YMM4 4.56.1.0 that the cache serves (TimelineVideoPlayer playing and paused, VideoFileWriter, the player's
// current-frame export) and the plugin's own producers (idle pre-render, cache bars) turn frame numbers into
// times with VideoInfo.GetTimeFrom, so the same frame always arrives with the same ticks.
internal static class FrameTimeKey
{
    internal static string For(TimeSpan time, int fps) => $"t{time.Ticks}@{fps}";
}
