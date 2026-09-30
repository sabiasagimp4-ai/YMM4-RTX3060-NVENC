using NVEncVideoWriterPlugin;

internal static class TimeKeyChecks
{
    internal static void Run()
    {
        foreach (int fps in new[] { 1, 24, 25, 30, 50, 60, 120, 144, 240 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (long frame = 0; frame < 200_000; frame += frame < 1000 ? 1 : 997)
            {
                var integer = TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / fps);
                var rounded = TimeSpan.FromTicks((long)Math.Round(frame * (double)TimeSpan.TicksPerSecond / fps));
                var seconds = TimeSpan.FromSeconds((double)frame / fps);
                var milliseconds = TimeSpan.FromMilliseconds(Math.Round(frame * 1000.0 / fps));
                string key = FrameTimeKey.For(integer, fps);
                Check(key == $"f{frame}@{fps}", $"frame {frame} at {fps} fps was not keyed by frame: {key}");
                Check(FrameTimeKey.For(rounded, fps) == key && FrameTimeKey.For(seconds, fps) == key
                    && FrameTimeKey.For(milliseconds, fps) == key, $"frame {frame} at {fps} fps depends on time rounding");
                Check(seen.Add(key), $"frame {frame} at {fps} fps collided with another frame");
                // A time a quarter frame away is a different sample and must keep its exact ticks.
                var between = integer + TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps / 4);
                Check(FrameTimeKey.For(between, fps) == $"t{between.Ticks}", $"sub-frame time near frame {frame} at {fps} fps was snapped");
            }
        }
        Check(FrameTimeKey.For(TimeSpan.FromTicks(123), 0) == "t123", "unknown fps must keep exact ticks");
        Console.WriteLine("Frame time keys: integer/double/millisecond rounding agree, no collisions, sub-frame times exact.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Frame time key: " + message);
    }
}
