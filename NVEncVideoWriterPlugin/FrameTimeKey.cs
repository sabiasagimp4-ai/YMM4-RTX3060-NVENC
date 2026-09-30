namespace NVEncVideoWriterPlugin;

// The host player, the exporter and the idle pre-renderer may turn a frame number into a TimeSpan with
// different rounding (integer ticks, double seconds, whole milliseconds). A time close to a frame
// boundary is therefore keyed by its frame number; anything else keeps its exact ticks.
internal static class FrameTimeKey
{
    // 1/8 frame, at most 1 ms: absorbs millisecond rounding yet stays far from neighboring frames.
    internal static string For(TimeSpan time, int fps)
    {
        if (fps > 0)
        {
            long frame = (long)Math.Round(time.Ticks * (double)fps / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
            long boundary = (long)Math.Round(frame * (double)TimeSpan.TicksPerSecond / fps, MidpointRounding.AwayFromZero);
            long tolerance = Math.Min(TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond / fps / 8);
            if (Math.Abs(time.Ticks - boundary) <= tolerance) return $"f{frame}@{fps}";
        }
        return $"t{time.Ticks}";
    }
}
