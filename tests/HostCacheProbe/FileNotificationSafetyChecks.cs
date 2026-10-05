using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// An event-only lookup is an experimental candidate, never the production hit guard. Delaying its event handler
// makes the notification gap deterministic and demonstrates why a periodic reconciliation cannot close it.
internal static class FileNotificationSafetyChecks
{
    internal static void Run(Assembly host, IGraphicsDevicesAndContext context)
    {
        string root = Path.Combine(Path.GetTempPath(), "ymm-file-notification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "face.png");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(root, "face.png")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Attributes,
        };
        long generation = 0;
        watcher.Changed += (_, _) =>
        {
            entered.Set();
            if (release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Increment(ref generation);
        };
        var timeline = new Timeline();
        timeline.VideoInfo.Width = 160; timeline.VideoInfo.Height = 90;
        timeline.Items = timeline.Items.Add(new ImageItem { FilePath = path, Frame = 0, Length = 30 });
        var scenes = HostCompat.NewScenes(); scenes.AddScene(timeline);
        var scene = new Scene(timeline, scenes, []);
        byte[] RenderFresh()
        {
            context.CacheProvider.Clear();
            using var source = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
            source.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
            return TimelineFrameCache.Capture(context.DeviceContext, source.Output, 160, 90, new Vector2(-80, -45))!;
        }
        bool enabled = TimelineFrameCache.Enabled;
        try
        {
            TimelineFrameCache.Enabled = false;
            File.WriteAllBytes(path, FramePixelChecks.Png(48, 32, (_, _) => (255, 20, 30, 255)));
            byte[] a = RenderFresh();
            Check(FileDependencyLease.TryAcquire([path], null, 1L << 20, out var first), "Initial file was unverifiable");
            IReadOnlyDictionary<string, FileFingerprint> known;
            using (first) { known = first!.Fingerprints; Check(HostContent.Matches(known), "Initial content was already marked changed"); }
            long pollGeneration = 0;
            var pollingClock = Stopwatch.StartNew();
            using var poll = new Timer(_ =>
            {
                if (FileDependencyLease.TryAcquire([path], known, 1L << 20, out var current))
                    using (current)
                        if (current!.Fingerprints[path] != known[path]) Interlocked.Increment(ref pollGeneration);
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            long captured = Interlocked.Read(ref generation);
            watcher.EnableRaisingEvents = true;
            File.WriteAllBytes(path, FramePixelChecks.Png(48, 32, (_, _) => (20, 30, 255, 255)));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "The watcher did not observe the replacement");
            byte[] b = RenderFresh();
            bool different = !a.SequenceEqual(b);
            bool eventOnlyAccepted = captured == Interlocked.Read(ref generation);
            Check(different && eventOnlyAccepted, "The notification-gap counterexample was not reproduced");
            bool leased = FileDependencyLease.TryAcquire([path], known, 0, out var stale);
            stale?.Dispose();
            Check(!leased, "The current synchronous hit guard accepted changed content");
            // Exercise the proposed 30-second reconciliation too. The first poll has not run in this bounded gap;
            // combining it with the watcher still accepts the old pixels that a fresh host source no longer draws.
            bool pollWouldHit = captured == Interlocked.Read(ref generation) && Interlocked.Read(ref pollGeneration) == 0;
            double gapMilliseconds = pollingClock.Elapsed.TotalMilliseconds;
            Check(pollWouldHit && gapMilliseconds < 30_000, "The periodic-reconciliation gap was not reproduced");
            Check(FileDependencyLease.TryAcquire([path], known, 1L << 20, out var changed), "Changed file could not be fingerprinted");
            using (changed) Check(!HostContent.Matches(changed!.Fingerprints), "An overwrite did not bypass until restart");
            release.Set();
            Check(SpinWait.SpinUntil(() => Interlocked.Read(ref generation) > captured, TimeSpan.FromSeconds(5)), "Event generation did not advance");
            Console.WriteLine("SPEEDUP4_RACE " + JsonSerializer.Serialize(new
            {
                fresh_host_pixels_differ = different, event_only_would_hit = eventOnlyAccepted,
                periodic_poll_closes_gap = !pollWouldHit, gap_ms = gapMilliseconds,
                synchronous_lease_rejected = !leased, restart_bypass = HostContent.Changed(path),
            }));
        }
        finally
        {
            release.Set(); watcher.EnableRaisingEvents = false;
            context.CacheProvider.Clear();
            TimelineFrameCache.Enabled = enabled;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
