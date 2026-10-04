using System.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project.Items;

internal static class SimpleTachiePixelChecks
{
    internal static void Run(Assembly host)
    {
        foreach (var (hidden, video) in new[] { (false, false), (true, false), (false, true) })
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { RunCase(host, hidden, video); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Simple tachie pixel check" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), $"Simple tachie case hidden={hidden},video={video} exceeded 30 seconds");
            if (failure is not null) throw new InvalidOperationException($"Simple tachie case hidden={hidden},video={video}", failure);
        }
        Console.WriteLine("Simple tachie pixels: speech boundaries, highest face, no-speech hiding, looping GIF, idle clones and selective overwrite bypass passed.");
    }

    private static void RunCase(Assembly host, bool hidden, bool video)
    {
        using var fixture = new SimpleTachieFixture(hidden);
        if (video)
        {
            string gif = Path.Combine(fixture.Root, "loop.gif");
            File.WriteAllBytes(gif, Convert.FromBase64String(OwnGif));
            SimpleTachieFixture.Set(fixture.Tachies[0].TachieItemParameter, "DefaultFace", gif);
        }
        var voices = fixture.Timeline.Items.OfType<VoiceItem>().Take(2).ToArray();
        voices[0].Frame = 30; voices[0].Length = 30;
        voices[1].Frame = 90; voices[1].Length = 30;
        SimpleTachieFixture.Set(voices[0].TachieFaceParameter, "Face", fixture.Images[1]);
        string upper = Path.Combine(fixture.Root, "upper.png");
        File.WriteAllBytes(upper, FramePixelChecks.Png(80, 100, (_, _) => (20, 230, 30, 255)));
        var face = new TachieFaceItem(fixture.Characters[0]) { Frame = 45, Length = 30, Layer = 20 };
        SimpleTachieFixture.Set(face.TachieFaceParameter, "Face", upper);
        fixture.Timeline.Items = fixture.Timeline.Items.Where(item => item is TachieItem).ToImmutableList().AddRange(voices).Add(face);
        fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var player = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, fixture.Scene, null], null)!;
        var view = new TimelineFrameCache.PreviewViewport(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            fixture.Scene.ID, fixture.Timeline.ID, Stopwatch.GetTimestamp(), false);
        using var store = new FrameCacheStore(Path.Combine(fixture.Root, "store"), 64L << 20, 0);
        using var tracker = new KeyDependencyTracker(fixture.Scene);
        var oldStore = TimelineFrameCache.StoreIfCreated;
        int[] frames = [0, 3, 29, 30, 44, 45, 59, 60, 74, 75, 89, 90, 119, 120, 899, 900];
        void Update(int frame) => player.Update(fixture.Timeline.VideoInfo.GetTimeFrom(frame), TimelineSourceUsage.Paused);
        byte[] Pixels() => TimelineFrameCache.CapturePreview(dc, player.Output, view)!;
        try
        {
            TimelineFrameCache.UseStore(store);
            TimelineFrameCache.TestViewport = source => ReferenceEquals(source, player) ? view : null;
            TimelineFrameCache.Enabled = false;
            var baseline = new Dictionary<int, byte[]>();
            foreach (int frame in frames) { Update(frame); baseline[frame] = Pixels(); }
            Check(!baseline[30].SequenceEqual(baseline[0]), "Speech did not change the fixture's image");
            Check(!baseline[44].SequenceEqual(baseline[45]), "The upper face did not change the fixture's image");
            if (video) Check(!baseline[0].SequenceEqual(baseline[3]), "The GIF was not decoded as a moving image");
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(30); TimelineFrameCache.CompletePendingStore(player);
                long hits = TimelineFrameCache.Hits; Update(30);
                return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(10)), "The simple player never finished keying: " + TimelineFrameCache.Status);
            KeyCapture? capture = null;
            Check(SpinWait.SpinUntil(() => tracker.TryCapture(30, out capture, out _), TimeSpan.FromSeconds(10)), "The live simple tracker never keyed");
            IdleFramePreRenderer.BatchRenderer batch;
            using (capture) batch = new(tracker, capture!.Model);
            using (batch)
            {
                TimelineFrameCache.Clear();
                foreach (int frame in frames)
                {
                    var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, fixture.Scene, batch, frame, view,
                        () => true, CancellationToken.None, out var reason);
                    Check(result == IdleFramePreRenderer.IdleFrameResult.Rendered, $"Simple idle frame {frame}: {result}: {reason}");
                }
                long hits = TimelineFrameCache.Hits;
                foreach (int frame in frames)
                {
                    Update(frame); Check(Pixels().SequenceEqual(baseline[frame]), $"Simple cached pixels differ at {frame}");
                }
                Check(TimelineFrameCache.Hits - hits == frames.Length, "Not every idle simple frame was reused");
            }
            if (video)
            {
                // Reject completion at the real GIF decoder's attributed readiness callback. The rendered pixels
                // remain valid; this checks that an unverified decoder result cannot be published or reused.
                var rejection = new Harmony("ymm.tests.simple-unready-gif");
                try
                {
                    rejectedGifUpdates = 0;
                    rejection.Patch(typeof(FrameRenderReadiness).GetMethod("DecoderFinalizer", BindingFlags.Static | BindingFlags.NonPublic)!,
                        prefix: new HarmonyMethod(typeof(SimpleTachiePixelChecks), nameof(RejectGifCompletion)));
                    TimelineFrameCache.Clear();
                    long hits = TimelineFrameCache.Hits;
                    Update(0); Update(0); TimelineFrameCache.CompletePendingStore(player);
                    Check(rejectedGifUpdates > 0, "The GIF readiness rejection did not run on the real reader");
                    Check(!FrameRenderReadiness.WasLastUpdateReady(player, fixture.Timeline.VideoInfo.GetTimeFrom(0))
                        && TimelineFrameCache.Hits == hits, "An unverified simple video frame was retained");
                }
                finally { rejection.UnpatchAll(rejection.Id); }
                Update(0); TimelineFrameCache.CompletePendingStore(player);
                long restoredHits = TimelineFrameCache.Hits; Update(0);
                Check(TimelineFrameCache.Hits > restoredHits && Pixels().SequenceEqual(baseline[0]), "Ready simple video did not recover with matching pixels");
            }
            // Overwriting the selected upper face must reject its frames immediately, without disabling the
            // default face's range. Use a new plain-image case for this assertion; the GIF's own time stays separate.
            if (!hidden && !video)
            {
                Check(tracker.TryCapture(75, out var unaffected, out var reason), reason);
                string key;
                using (unaffected) key = unaffected!.Key;
                File.WriteAllBytes(upper, FramePixelChecks.Png(80, 100, (_, _) => (240, 230, 30, 255)));
                File.SetLastWriteTimeUtc(upper, DateTime.UtcNow.AddSeconds(2));
                Check(!tracker.TryCapture(45, out var stale, out _), "An overwritten upper face was captured"); stale?.Dispose();
                Check(tracker.TryCapture(75, out unaffected, out reason), "Unused-face overwrite disabled the default range: " + reason);
                using (unaffected) Check(unaffected!.Key == key && unaffected.Validate(), "The unrelated simple range's key changed");
                long hits = TimelineFrameCache.Hits;
                Update(45); Check(TimelineFrameCache.Hits == hits, "The player hit an overwritten face");
            }
        }
        finally
        {
            TimelineFrameCache.TestViewport = null; TimelineFrameCache.Enabled = false;
            player.Dispose(); context.CacheProvider.Clear();
            if (oldStore is not null) TimelineFrameCache.UseStore(oldStore);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "Simple pixel checks leaked GPU resources");
    }

    private static int rejectedGifUpdates;
    private static void RejectGifCompletion(object __0, ref Exception? __3)
    {
        if (__0.GetType().FullName != FrameRenderReadiness.WicGifTypeName) return;
        Interlocked.Increment(ref rejectedGifUpdates);
        __3 = new IOException("Test-only rejected GIF decoder completion");
    }

    // Generated for this test: two solid 80x100 frames, 200 ms each. No host assets are redistributed.
    private const string OwnGif = "R0lGODlhUABkAIEAAOYUKAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQAFAAAACwAAAAAUABkAAAIkAABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRo0iTKl3KtKnTp1CjSp1KtarVq1izat3KtavXr2DDih1LtqzZs2jTql3Ltq3bt3Djyp1Lt67du3jz6t3Lt2/fgAAh+QQBFAABACwAAAAAUABkAIEUKOYAAAAAAAAAAAAIkAABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzBjypxJs6bNmzhz6tzJs6fPn0CDCh1KtKjRo0iTKl3KtKnTp1CjSp1KtarVq1izat3KtavXr2DDih1LtqzZs2jTql3Ltq3bt3Djyp1Lt67du3jz6t3Lt2/fgAA7";
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
