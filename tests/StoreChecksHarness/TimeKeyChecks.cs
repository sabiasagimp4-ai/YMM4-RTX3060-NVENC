using NVEncVideoWriterPlugin;

internal static class TimeKeyChecks
{
    internal static void Run()
    {
        foreach (int fps in new[] { 1, 24, 25, 30, 50, 60, 120, 144, 240 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int frame = 0; frame < 200_000; frame += frame < 1000 ? 1 : 997)
            {
                // YukkuriMovieMaker.Commons.FrameTime.FrameToTime, which VideoInfo.GetTimeFrom calls.
                var time = TimeSpan.FromTicks((long)Math.Round((double)frame * 10000000.0 / fps, MidpointRounding.AwayFromZero));
                string key = FrameTimeKey.For(time, fps);
                Check(FrameTimeKey.For(TimeSpan.FromTicks(time.Ticks), fps) == key, $"frame {frame} at {fps} fps is not deterministic");
                Check(seen.Add(key), $"frame {frame} at {fps} fps collided with another frame");
                // Times one tick around a frame boundary can select another decoder sample: never the same key.
                Check(FrameTimeKey.For(time - TimeSpan.FromTicks(1), fps) != key && FrameTimeKey.For(time + TimeSpan.FromTicks(1), fps) != key,
                    $"a time one tick from frame {frame} at {fps} fps shares its key");
            }
        }
        // The 30 fps example from the v2 handoff: 9,999,999 / 10,000,000 / 10,000,001 ticks used to be one key (f30@30).
        var near = new[] { 9_999_999L, 10_000_000L, 10_000_001L }.Select(ticks => FrameTimeKey.For(TimeSpan.FromTicks(ticks), 30)).ToArray();
        Check(near.Distinct().Count() == 3, "times one tick apart around a frame boundary share a key");
        Check(FrameTimeKey.For(TimeSpan.FromTicks(123), 30) != FrameTimeKey.For(TimeSpan.FromTicks(123), 60), "the frame rate is part of the key");
        Console.WriteLine("Frame time keys: exact ticks, deterministic, no collisions, one tick around a boundary is another key.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Frame time key: " + message);
    }
}
