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
        bool preview = TimelineFrameCache.PreviewEnabled, export = TimelineFrameCache.ExportEnabled;
        var viewport = TimelineFrameCache.TestViewport;
        var store = TimelineFrameCache.StoreIfCreated;
        // The nested layouts take the plain case through a group control, a composite group's source and another scene's.
        // "same-layer-faces": a second face on the upper face's layer, both shown at 60..74.
        foreach (var (hidden, video, layout) in new[] { (false, false, "root"), (true, false, "root"), (false, true, "root"), (false, false, "same-layer-faces") }
            .Concat(TachieLayouts.Nested.Select(layout => (false, false, layout))))
        {
            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try { RunCase(host, hidden, video, layout); }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Simple tachie pixel check" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            Check(finished.Wait(TimeSpan.FromSeconds(30)), $"Simple tachie case hidden={hidden},video={video},layout={layout} exceeded 30 seconds");
            if (failure is not null) throw new InvalidOperationException($"Simple tachie case hidden={hidden},video={video},layout={layout}", failure);
            Check(TimelineFrameCache.PreviewEnabled == preview && TimelineFrameCache.ExportEnabled == export
                && ReferenceEquals(TimelineFrameCache.TestViewport, viewport) && ReferenceEquals(TimelineFrameCache.StoreIfCreated, store),
                "Simple tachie checks changed the surrounding test's cache configuration");
        }
        Console.WriteLine("Simple tachie pixels: speech boundaries, highest face, no-speech hiding, looping GIF, idle clones, selective overwrite bypass, groups, composite groups and scenes passed.");
    }

    private static void RunCase(Assembly host, bool hidden, bool video, string variant)
    {
        string layout = TachieLayouts.Nested.Contains(variant) ? variant : "root";
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
        TachieFaceItem? second = null;
        if (variant == "same-layer-faces")
        {
            string blue = Path.Combine(fixture.Root, "second.png");
            File.WriteAllBytes(blue, FramePixelChecks.Png(80, 100, (_, _) => (30, 40, 230, 255)));
            second = new TachieFaceItem(fixture.Characters[0]) { Frame = 60, Length = 30, Layer = 20 };
            SimpleTachieFixture.Set(second.TachieFaceParameter, "Face", blue);
            fixture.Timeline.Items = fixture.Timeline.Items.Add(second);
        }
        fixture.Timeline.RefreshTimelineLengthAndMaxLayer();
        var scene = TachieLayouts.Apply(layout, fixture.Timeline, fixture.Scene);
        using var devices = new GraphicsDevices();
        using var context = devices.CreateContext();
        var dc = context.DeviceContext;
        using var player = (ITimelineSource)Activator.CreateInstance(host.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [context, scene, null], null)!;
        var view = new TimelineFrameCache.PreviewViewport(321, 181, Matrix3x2.Identity, new Vector2(160.5f, 90.5f), 96, 96,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dc.AntialiasMode, dc.TextAntialiasMode, dc.PrimitiveBlend, dc.UnitMode,
            scene.ID, scene.Timeline.ID, Stopwatch.GetTimestamp(), false);
        using var store = new FrameCacheStore(Path.Combine(fixture.Root, "store"), 64L << 20, 0);
        using var tracker = new KeyDependencyTracker(scene);
        var oldStore = TimelineFrameCache.StoreIfCreated;
        bool oldPreview = TimelineFrameCache.PreviewEnabled, oldExport = TimelineFrameCache.ExportEnabled;
        var oldViewport = TimelineFrameCache.TestViewport;
        int[] frames = [0, 3, 4, 6, 10, 29, 30, 44, 45, 59, 60, 74, 75, 89, 90, 119, 120, 899, 900];
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
            if (video)
            {
                // WIC includes the preceding frame at its ending time; frame3 is the 200ms boundary.
                Check(!baseline[0].SequenceEqual(baseline[4]), "The GIF was not decoded as a moving image");
                Check(baseline[0].SequenceEqual(baseline[6]) && baseline[4].SequenceEqual(baseline[10]),
                    "The GIF did not repeat its two images after looping");
            }
            TimelineFrameCache.Enabled = true;
            Check(SpinWait.SpinUntil(() =>
            {
                Update(30); TimelineFrameCache.CompletePendingStore(player);
                long hits = TimelineFrameCache.Hits; Update(30);
                return TimelineFrameCache.Hits > hits;
            }, TimeSpan.FromSeconds(10)), "The simple player never finished keying: " + TimelineFrameCache.Status);
            string preparationReason = string.Empty;
            Check(SpinWait.SpinUntil(() =>
            {
                bool complete = true;
                foreach (int frame in frames)
                {
                    if (tracker.TryCapture(frame, out var prepared, out preparationReason)) prepared!.Dispose();
                    else complete = false;
                    Update(frame); TimelineFrameCache.CompletePendingStore(player);
                    long previousHits = TimelineFrameCache.Hits; Update(frame);
                    if (TimelineFrameCache.Hits == previousHits) complete = false;
                }
                return complete;
            }, TimeSpan.FromSeconds(10)), "All simple frame dependencies did not become ready: " + preparationReason + "; " + TimelineFrameCache.Status);
            Check(tracker.TryCapture(30, out var capture, out preparationReason), preparationReason);
            IdleFramePreRenderer.BatchRenderer batch;
            using (capture) batch = new(tracker, capture!.Model);
            using (batch)
            {
                // The clone's copy of the fixture's timeline: the root, or the scene the root shows.
                var cloneTimeline = layout == "scene" ? batch.CloneScene.Scenes.Timelines.Single(timeline => timeline.ID == fixture.Timeline.ID) : batch.CloneScene.Timeline;
                var cloneFace = cloneTimeline.Items.OfType<TachieFaceItem>().First();
                var cloneTachie = cloneTimeline.Items.OfType<TachieItem>()
                    .Single(item => item.CharacterName == face.CharacterName);
                Check(ReferenceEquals(cloneFace.Character, cloneTachie.Character),
                    "The cloned face was not rebound to the cloned tachie's character");
                Check(FrameCacheKey.TryDescribe(batch.CloneScene, out _, out var cloneFiles, out var cloneReason), cloneReason);
                Check(cloneFiles.Contains(upper, StringComparer.OrdinalIgnoreCase), "The clone lost its selected upper face");
                TimelineFrameCache.Clear();
                foreach (int frame in frames)
                {
                    var result = IdleFramePreRenderer.PrimeBatchFrame(tracker, scene, batch, frame, view,
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
            if (second is not null)
            {
                // Of one layer, the first face in the item list is shown: swapping them changes the frames showing both.
                Check(baseline[60].SequenceEqual(baseline[45]) || !baseline[60].SequenceEqual(baseline[75]), "The first face of one layer was not the one shown");
                Check(tracker.TryCapture(60, out var first, out var reason), reason);
                string firstKey; using (first) firstKey = first!.Key;
                fixture.Timeline.Items = fixture.Timeline.Items.Remove(face).Remove(second).Add(second).Add(face);
                TimelineFrameCache.Enabled = false; Update(60); var swapped = Pixels();
                Check(!swapped.SequenceEqual(baseline[60]), "Swapping the faces of one layer did not change the picture");
                Check(SpinWait.SpinUntil(() => tracker.TryCapture(60, out var next, out _) && Dispose(next!) != firstKey, TimeSpan.FromSeconds(10)),
                    "Swapping the faces of one layer did not change the key");
                TimelineFrameCache.Enabled = true;
                Check(SpinWait.SpinUntil(() =>
                {
                    Update(60); TimelineFrameCache.CompletePendingStore(player);
                    long previous = TimelineFrameCache.Hits; Update(60);
                    return TimelineFrameCache.Hits > previous;
                }, TimeSpan.FromSeconds(10)), "The swapped face frame never became reusable: " + TimelineFrameCache.Status);
                Check(Pixels().SequenceEqual(swapped), "The swapped face frame was cached with another picture");
                fixture.Timeline.Items = fixture.Timeline.Items.Remove(face).Remove(second).Add(face).Add(second);
            }
            // Overwriting the selected upper face must reject its frames immediately, without disabling the
            // default face's range. Use a new plain-image case for this assertion; the GIF's own time stays separate. A
            // scene item's frames depend on every file of its scene, so there they all change.
            if (!hidden && !video && layout != "scene")
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
            TimelineFrameCache.TestViewport = oldViewport; TimelineFrameCache.Enabled = false;
            player.Dispose(); context.CacheProvider.Clear();
            if (oldStore is not null) TimelineFrameCache.UseStore(oldStore);
            TimelineFrameCache.SetEnabled(oldPreview, oldExport);
        }
        Check(TimelineFrameCache.GpuBytes == 0 && TimelineFrameCache.ReadbackPoolBytes == 0, "Simple pixel checks leaked GPU resources");
    }

    private static string Dispose(KeyCapture capture) { using (capture) return capture.Key; }

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
