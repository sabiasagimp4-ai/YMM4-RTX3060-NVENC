using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.MediaFoundation;
using YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.Source2;
using YukkuriMovieMaker.Plugin.FileSource.WIC;

// Host-independent checks for FrameRenderReadiness. Fake types mimic only the call shape that matters:
// TimelineSource.Update fans decoder updates (and nested scenes) out through Parallel.ForEach, swallows
// decoder failures the way the host renders them as transparent output, and returns normally.
// The video source fakes carry the host type names and reproduce the state transitions read from the
// YMM4 4.56.1.0 sources (see docs/HOST_CONTRACTS.md); they are not host code.
internal static class Program
{
    private const string Owner = "ymm.tests.readiness";
    private static readonly List<string> emitted = [];
    internal static readonly TimeSpan Frame = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    private static readonly MethodBase[] VideoSourceUpdates =
    [
        typeof(MFVideoFileSource2).GetMethod(nameof(MFVideoFileSource2.Update))!,
        typeof(MFVideoFileSource).GetMethod(nameof(MFVideoFileSource.Update))!,
        typeof(FFmpegVideoFileSource).GetMethod(nameof(FFmpegVideoFileSource.Update))!,
        typeof(FFmpegVideoFileSource).GetMethod("SeekTo", BindingFlags.Instance | BindingFlags.NonPublic)!,
        typeof(WICGifVideoSource).GetMethod(nameof(WICGifVideoSource.Update))!,
        typeof(WICSequentialImageVideoSource).GetMethod(nameof(WICSequentialImageVideoSource.Update))!,
        typeof(CachedVideoFileSource).GetMethod(nameof(CachedVideoFileSource.Update))!,
        typeof(ExplicitSource).GetInterfaceMap(typeof(IVideoFileSource)).TargetMethods.Single(),
        typeof(OverridingSource).GetMethod(nameof(OverridingSource.Update))!,
        typeof(OddSource).GetMethod(nameof(OddSource.Update))!,
    ];

    private static int Main()
    {
        var harmony = new Harmony(Owner);
        var update = typeof(TimelineSource).GetMethod(nameof(TimelineSource.Update))!;
        var decode = typeof(FakeDecoder).GetMethod(nameof(FakeDecoder.Update))!;
        var checks = new[] { new FrameRenderReadiness.DecoderCheck("FakeDecoder", decode, FakeDecoder.Holds) };
        try
        {
            // Mirrors TimelineFrameCache: its own prefix/postfix are installed first under the same owner.
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(CacheLike), nameof(CacheLike.Prefix)),
                postfix: new HarmonyMethod(typeof(CacheLike), nameof(CacheLike.Postfix)));

            Check(!FrameRenderReadiness.TryInstall(typeof(Harmony).Assembly, harmony, out var hostReason),
                "A host without the TimelineSource contract must reject install");
            CheckOnlyCacheLikePatches(update, [decode, .. VideoSourceUpdates], "rejected host install: " + hostReason);

            CheckRollbackOnExternalOwner(harmony, update);

            Check(FrameRenderReadiness.TryInstall(update, checks, harmony, out var reason), reason);
            Check(!FrameRenderReadiness.TryInstall(update, checks, harmony, out _), "Second install must be rejected");
            CheckReadyFrame();
            CheckAuxiliaryReadiness();
            CheckDecoderTimeoutPropagatesToParents();
            CheckSwallowedDecoderException();
            CheckUpdateExceptionIsPreserved();
            CheckSkippedOriginalKeepsScope();
            CheckUnattributedDecodeFailsInFlightFrames();
            CheckLatePrefetchDecodeIsIgnored();
            CheckIdleDecodeIsIgnored();
            CheckConcurrentRendersAreIsolated();
            FrameRenderReadiness.Uninstall(harmony);
            CheckOnlyCacheLikePatches(update, [decode], "core uninstall");
            var root = Scene(decoders: 1);
            Render(root, Frame);
            Check(CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame),
                "Uninstalled readiness must not report frames ready");
            Console.WriteLine("Render readiness core: attribution, propagation, fail-closed paths, rollback and uninstall OK");

            Check(FrameRenderReadiness.TryInstall(typeof(Program).Assembly, harmony, out reason), reason);
            CheckHostCoverage();
            CheckHostVideoSources();
            CheckLateBuiltInVideoSource();
            FrameRenderReadiness.Uninstall(harmony);
            CheckOnlyCacheLikePatches(update, [decode, .. VideoSourceUpdates], "host uninstall");
            Check(FrameRenderReadiness.CoverageProblem is null && FrameRenderReadiness.Coverage.Count == 0, "Uninstall kept host coverage state");
            Console.WriteLine("Render readiness host binding: MF2/MF-legacy/FFmpeg/WIC/wrapper/unverified sources and late-loaded coverage OK");
            RandomSeedChecks.Run();
            return 0;
        }
        finally
        {
            FrameRenderReadiness.Uninstall(harmony);
            harmony.UnpatchAll(Owner);
            // Loaded assemblies stay locked on Windows; leftovers in bin/ are harmless and never loaded.
            foreach (var path in emitted) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CheckRollbackOnExternalOwner(Harmony harmony, MethodBase update)
    {
        var foreign = new Harmony("someone.else");
        var target = typeof(OtherDecoder).GetMethod(nameof(OtherDecoder.Update))!;
        var decode = typeof(FakeDecoder).GetMethod(nameof(FakeDecoder.Update))!;
        foreign.Patch(target, postfix: new HarmonyMethod(typeof(CacheLike), nameof(CacheLike.Observe)));
        try
        {
            var checks = new[]
            {
                new FrameRenderReadiness.DecoderCheck("FakeDecoder", decode, FakeDecoder.Holds),
                new FrameRenderReadiness.DecoderCheck("OtherDecoder", target, (_, _) => true),
            };
            Check(!FrameRenderReadiness.TryInstall(update, checks, harmony, out var reason) && reason.Contains("someone.else"),
                "External Harmony owner must reject install: " + reason);
            CheckOnlyCacheLikePatches(update, [decode], "external-owner rollback");
            Check(Harmony.GetPatchInfo(target)!.Owners.SequenceEqual(["someone.else"]), "Rollback touched a foreign patch");
        }
        finally { foreign.UnpatchAll(foreign.Id); }

        // A target that cannot be patched fails after earlier hooks were applied: only those are removed.
        var bodyless = typeof(AbstractDecoder).GetMethod(nameof(AbstractDecoder.Update))!;
        var partial = new[]
        {
            new FrameRenderReadiness.DecoderCheck("FakeDecoder", decode, FakeDecoder.Holds),
            new FrameRenderReadiness.DecoderCheck("AbstractDecoder", bodyless, (_, _) => true),
        };
        Check(!FrameRenderReadiness.TryInstall(update, partial, harmony, out var partialReason) && !FrameRenderReadiness.Installed,
            "Unpatchable decoder target must reject install");
        CheckOnlyCacheLikePatches(update, [decode], "mid-install rollback: " + partialReason);

        // Harmony cannot rebuild a body with an exception filter that continues a loop (YMM4 4.56.1.0 DirectShow).
        var filtered = typeof(FilterDecoder).GetMethod(nameof(FilterDecoder.Update))!;
        var strict = new[]
        {
            new FrameRenderReadiness.DecoderCheck("FakeDecoder", decode, FakeDecoder.Holds),
            new FrameRenderReadiness.DecoderCheck("FilterDecoder", filtered, (_, _) => false),
        };
        Check(!FrameRenderReadiness.TryInstall(update, strict, harmony, out var filterReason) && !FrameRenderReadiness.Installed,
            "An unhookable source without another way to reject its frames must reject install");
        CheckOnlyCacheLikePatches(update, [decode, filtered], "unhookable rollback: " + filterReason);
        int demoted = 0;
        var tolerated = new[]
        {
            new FrameRenderReadiness.DecoderCheck("FakeDecoder", decode, FakeDecoder.Holds),
            new FrameRenderReadiness.DecoderCheck("FilterDecoder", filtered, (_, _) => false, () => demoted++),
        };
        Check(FrameRenderReadiness.TryInstall(update, tolerated, harmony, out var toleratedReason) && demoted == 1, toleratedReason);
        Check(Harmony.GetPatchInfo(filtered) is null && Harmony.GetPatchInfo(decode)!.Finalizers.Count == 1,
            "Only the hookable decoder may be patched");
        FrameRenderReadiness.Uninstall(harmony);
        CheckOnlyCacheLikePatches(update, [decode, filtered], "tolerated uninstall");
    }

    private static void CheckOnlyCacheLikePatches(MethodBase update, MethodBase[] decoders, string stage)
    {
        var info = Harmony.GetPatchInfo(update)!;
        Check(info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod.DeclaringType == typeof(CacheLike)
            && info.Postfixes.Count == 1 && info.Finalizers.Count == 0, $"Readiness patches left on Update after {stage}");
        foreach (var decoder in decoders)
            Check(Harmony.GetPatchInfo(decoder) is not { } decoderInfo || decoderInfo.Finalizers.Count == 0 && decoderInfo.Prefixes.Count == 0,
                $"Readiness patches left on {decoder.DeclaringType?.Name}.{decoder.Name} after {stage}");
    }

    private static void CheckReadyFrame()
    {
        var child = Scene(decoders: 2);
        var root = Scene(decoders: 3, children: [child]);
        Render(root, Frame * 5);
        Check(CacheLike.Last(child) == true && CacheLike.Last(root) == true, "Fully decoded frame must be ready in postfix");
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 5) && FrameRenderReadiness.WasLastUpdateReady(child, Frame * 5),
            "Fully decoded frame must be recorded ready");
        Check(!FrameRenderReadiness.WasLastUpdateReady(root, Frame * 6), "Readiness must be bound to the rendered time");
        Check(!FrameRenderReadiness.IsUpdateReady(root), "Scope leaked past Update");
    }

    private static void CheckAuxiliaryReadiness()
    {
        var child = Scene(decoders: 1);
        var root = Scene(decoders: 1, children: [child]);
        child.During = _ => FrameRenderReadiness.ObserveAuxiliary(false, "unpublished-lip-sync");
        Render(root, Frame);
        Check(CacheLike.Last(child) == false && CacheLike.Last(root) == false,
            "Unpublished asynchronous input must fail the nested frame and its parent");
        child.During = _ => FrameRenderReadiness.ObserveAuxiliary(true, "published-lip-sync");
        Render(root, Frame * 2);
        Check(CacheLike.Last(child) == true && CacheLike.Last(root) == true, "A published input must recover");
        FrameRenderReadiness.ObserveAuxiliary(false, "outside-render");
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 2), "An idle observation changed a completed frame");
    }

    private static void CheckDecoderTimeoutPropagatesToParents()
    {
        var child = Scene(decoders: 2);
        var root = Scene(decoders: 3, children: [child]);
        Render(root, Frame);
        child.Decoders[1].Behavior = FakeDecoder.Mode.Timeout;
        Render(root, Frame * 2);
        Check(CacheLike.Last(child) == false && CacheLike.Last(root) == false, "Stale decoder frame must fail child and parent");
        Check(!FrameRenderReadiness.WasLastUpdateReady(root, Frame * 2) && !FrameRenderReadiness.WasLastUpdateReady(child, Frame * 2),
            "Stale decoder frame must not be recorded ready");
        child.Decoders[1].Behavior = FakeDecoder.Mode.Decode;
        Render(root, Frame * 3);
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 3), "Failure latch must not outlive its frame");
    }

    private static void CheckSwallowedDecoderException()
    {
        var root = Scene(decoders: 2);
        root.Decoders[0].Behavior = FakeDecoder.Mode.Throw;
        Render(root, Frame);
        Check(CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame),
            "Decoder exception swallowed by the host must fail the frame");
    }

    private static void CheckUpdateExceptionIsPreserved()
    {
        var root = Scene(decoders: 1);
        root.Throw = true;
        try { Render(root, Frame); throw new Exception("Update exception was suppressed"); }
        catch (InvalidOperationException error) when (error.Message == TimelineSource.FailureMessage) { }
        Check(!FrameRenderReadiness.WasLastUpdateReady(root, Frame), "Throwing Update must not be recorded ready");
        Check(!FrameRenderReadiness.IsUpdateReady(root), "Scope leaked past throwing Update");
    }

    private static void CheckSkippedOriginalKeepsScope()
    {
        var root = Scene(decoders: 1);
        Render(root, Frame);
        CacheLike.SkipNext = true;
        root.Decoders[0].Behavior = FakeDecoder.Mode.Timeout;
        Render(root, Frame * 9);
        Check(root.Decoders[0].Updates == 1, "Skipped original still decoded");
        Check(CacheLike.Last(root) == true && FrameRenderReadiness.WasLastUpdateReady(root, Frame * 9),
            "A skipped (cache-served) update carries a stored ready frame");
    }

    private static void CheckUnattributedDecodeFailsInFlightFrames()
    {
        var root = Scene(decoders: 1);
        var stray = new FakeDecoder();
        root.During = time =>
        {
            Task task;
            var flow = ExecutionContext.SuppressFlow();
            try { task = Task.Run(() => stray.Update(time)); }
            finally { flow.Undo(); }
            task.Wait();
        };
        Render(root, Frame);
        Check(CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame),
            "Decode that lost its render attribution must fail in-flight frames");
    }

    // Mirrors TimelineSource.PrefetchResources: a Task.Run started during one frame decodes after it ended.
    // The adopting frame updates the source again in its own scope, so the late decode must not count.
    private static void CheckLatePrefetchDecodeIsIgnored()
    {
        var first = Scene(decoders: 1);
        var second = Scene(decoders: 1);
        var release = new ManualResetEventSlim();
        Task? late = null;
        var prefetched = new FakeDecoder { Behavior = FakeDecoder.Mode.Timeout };
        first.During = time => late = Task.Run(() => { release.Wait(); prefetched.Update(time + TimeSpan.FromSeconds(1)); });
        Render(first, Frame);
        Check(FrameRenderReadiness.WasLastUpdateReady(first, Frame), "First frame should be ready");
        second.During = _ => { release.Set(); late!.Wait(); };
        Render(second, Frame);
        Check(prefetched.Updates == 1 && CacheLike.Last(second) == true && FrameRenderReadiness.WasLastUpdateReady(second, Frame),
            "A late prefetch decode on a completed frame's context must not fail other frames");
    }

    private static void CheckIdleDecodeIsIgnored()
    {
        new FakeDecoder { Behavior = FakeDecoder.Mode.Timeout }.Update(Frame);
        var root = Scene(decoders: 1);
        Render(root, Frame * 4);
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 4), "Decode with no render in flight must not poison later frames");
    }

    private static void CheckConcurrentRendersAreIsolated()
    {
        var good = Scene(decoders: 3, children: [Scene(decoders: 2)]);
        var bad = Scene(decoders: 3);
        bad.Decoders[2].Behavior = FakeDecoder.Mode.Timeout;
        using var start = new Barrier(2);
        var a = Task.Run(() => { start.SignalAndWait(); for (int i = 1; i <= 20; i++) Render(good, Frame * i); });
        var b = Task.Run(() => { start.SignalAndWait(); for (int i = 1; i <= 20; i++) Render(bad, Frame * i); });
        Task.WaitAll(a, b);
        Check(FrameRenderReadiness.WasLastUpdateReady(good, Frame * 20), "Concurrent failure leaked into an independent render");
        Check(!FrameRenderReadiness.WasLastUpdateReady(bad, Frame * 20), "Concurrent failing render reported ready");
    }

    // FFmpeg: the interval a seek produced starting exactly at t may be a later frame shown from t on (a seek that landed
    // after t); it is not ready until another frame is decoded. Frames decoded on in order are.
    private static void CheckFFmpegSeekWatch()
    {
        foreach (var start in new[] { TimeSpan.Zero, Frame * 2 })
        {
            var source = new FFmpegVideoFileSource { StreamStart = start, Behavior = VideoMode.LateSeek };
            var root = Scene(decoders: 0);
            root.Sources.Add(source);
            bool ReadyAt(TimeSpan time) { Render(root, time); return CacheLike.Last(root) == true && FrameRenderReadiness.WasLastUpdateReady(root, time); }
            Check(!ReadyAt(Frame * 40) && source.Seeks == 1, "The interval a seek began at t reported ready");
            Check(!ReadyAt(Frame * 41) && source.Seeks == 1, "A later time inside the interval a seek began at t reported ready");
            Check(ReadyAt(Frame * 42) && source.Seeks == 1, "A frame decoded on in order after the seek was not ready");
            Check(ReadyAt(Frame * 43), "Frames decoded on in order were not ready");
            Check(!ReadyAt(Frame * 10) && source.Seeks == 2, "The interval a backward seek began at t reported ready");
            // A seek outside any frame's scope (prefetch, idle) is watched too.
            source.Update(Frame * 80);
            Check(source.Seeks == 3 && !ReadyAt(Frame * 81), "An interval a seek outside a frame began at t reported ready");
            Check(ReadyAt(Frame * 82), "Decoding on after an unattributed seek was not ready");
        }
    }

    private static void CheckHostCoverage()
    {
        var coverage = FrameRenderReadiness.Coverage;
        void Expect(Type type, string kind) => Check(coverage.Any(line => line.StartsWith(type.FullName + ": " + kind, StringComparison.Ordinal)),
            $"{type.Name} was not classified as {kind}: {string.Join(" | ", coverage)}");
        Expect(typeof(MFVideoFileSource2), "MF2");
        Expect(typeof(DerivedMf2Source), "unverified");
        Expect(typeof(MFVideoFileSource), "MF-legacy");
        Expect(typeof(FFmpegVideoFileSource), "FFmpeg");
        Expect(typeof(WICGifVideoSource), "WIC");
        Expect(typeof(WICSequentialImageVideoSource), "image");
        Expect(typeof(CachedVideoFileSource), "wrapper");
        Expect(typeof(ExplicitSource), "unverified");
        Expect(typeof(OverridingSource), "unverified");
        Expect(typeof(OddSource), "unverified");
        Check(coverage.Count == 10, "Unexpected video source coverage: " + string.Join(" | ", coverage));
        // The offline shape report must predict exactly what the binder decided.
        foreach (var line in coverage)
        {
            string typeName = line[..line.IndexOf(": ", StringComparison.Ordinal)];
            string kind = line[(typeName.Length + 2)..].Split(' ')[0];
            var type = typeof(Program).Assembly.GetType(typeName, true)!;
            Check(ShapeRules.Predict(type, typeof(IVideoFileSource)) == kind, $"Shape report predicts {ShapeRules.Predict(type, typeof(IVideoFileSource))} for {line}");
        }
        foreach (var method in VideoSourceUpdates)
            Check(method.Name == "SeekTo" ? Harmony.GetPatchInfo(method)?.Prefixes.Count == 1 : Harmony.GetPatchInfo(method)?.Finalizers.Count == 1,
                $"{method.DeclaringType?.Name}.{method.Name} was not hooked exactly once");
    }

    private static void CheckHostVideoSources()
    {
        // Renders frame 2, applies the change, then renders frame 3 (or frame 2 again).
        bool Ready(Func<IVideoFileSource[]> create, Action<IVideoFileSource[]>? before = null, bool repeatTime = false, string[]? images = null)
        {
            var sources = create();
            var root = Scene(decoders: 0);
            root.Sources.AddRange(sources);
            Render(root, Frame * 2);
            before?.Invoke(sources);
            var time = repeatTime ? Frame * 2 : Frame * 3;
            CacheLike.Images = images ?? [];
            try { Render(root, time); }
            finally { CacheLike.Images = []; }
            return CacheLike.Last(root) == true && FrameRenderReadiness.WasLastUpdateReady(root, time, images ?? []);
        }
        void Mode(IVideoFileSource source, VideoMode mode)
        {
            switch (source)
            {
                case MFVideoFileSource2 mf2: mf2.Behavior = mode; break;
                case MFVideoFileSource legacy: legacy.Behavior = mode; break;
                case FFmpegVideoFileSource ffmpeg: ffmpeg.Behavior = mode; break;
                case WICGifVideoSource gif: gif.Behavior = mode; break;
            }
        }
        var start = Frame * 2;
        Check(Ready(() => [new MFVideoFileSource2(), new MFVideoFileSource(), new MFVideoFileSource { StreamStart = start },
            new FFmpegVideoFileSource(), new FFmpegVideoFileSource { StreamStart = start }, new WICGifVideoSource(),
            new CachedVideoFileSource(new MFVideoFileSource2()), new CachedVideoFileSource(new FFmpegVideoFileSource())]),
            "Verified sources that decoded must be ready");

        foreach (var mode in new[] { VideoMode.Stale, VideoMode.Clear, VideoMode.Throw })
            Check(!Ready(() => [new MFVideoFileSource2()], s => Mode(s[0], mode)), $"MF2 {mode} frame reported ready");
        Check(!Ready(() => [new DerivedMf2Source()]), "Pinned classification must not vouch for a derived runtime type sharing the hook");
        foreach (var mode in new[] { VideoMode.Clear, VideoMode.Throw })
        {
            Check(!Ready(() => [new MFVideoFileSource()], s => Mode(s[0], mode)), $"MF-legacy {mode} frame reported ready");
            Check(!Ready(() => [new FFmpegVideoFileSource()], s => Mode(s[0], mode)), $"FFmpeg {mode} frame reported ready");
        }
        Check(!Ready(() => [new MFVideoFileSource { StreamStart = start }], s => Mode(s[0], VideoMode.Unshifted))
            && !Ready(() => [new FFmpegVideoFileSource { StreamStart = start }], s => Mode(s[0], VideoMode.Unshifted)),
            "A sample on the item clock instead of the stream clock (t + streamStartTime) reported ready");
        Check(!Ready(() => [new FFmpegVideoFileSource()], s => Mode(s[0], VideoMode.Stretch)),
            "FFmpeg frame stretched to the stream end after an early stop reported ready");
        CheckFFmpegSeekWatch();
        Check(!Ready(() => [new WICGifVideoSource()], s => Mode(s[0], VideoMode.Throw)), "WIC decode exception reported ready");
        // An image sequence holds t while the image of GetFrameIndex(t) is loaded; the image the key names must be the one
        // shown. Images shown besides (a source read ahead for a later item) do not matter.
        int index = new WICSequentialImageVideoSource().GetFrameIndex(Frame * 3);
        string shown = Path.GetFullPath($"seq{index}.png"), other = Path.GetFullPath($"seq{index - 1}.png");
        Check(Ready(() => [new WICSequentialImageVideoSource()], images: [shown]), "A loaded sequence image the key names was not ready");
        Check(!Ready(() => [new WICSequentialImageVideoSource()], images: [other]), "A key naming another image than the one shown reported ready");
        Check(!Ready(() => [new WICSequentialImageVideoSource()], images: [shown, other]), "A key naming an image not shown reported ready");
        Check(Ready(() => [new WICSequentialImageVideoSource()]), "An image shown besides those the key names made the frame not ready");
        Check(!Ready(() => [new WICSequentialImageVideoSource()], s => ((WICSequentialImageVideoSource)s[0]).Unreadable = true),
            "A sequence image no reader opened (silent empty bitmap) reported ready");
        Check(Ready(() => [new CachedVideoFileSource(new WICSequentialImageVideoSource())], images: [shown])
            && !Ready(() => [new CachedVideoFileSource(new WICSequentialImageVideoSource())], images: [other]),
            "A wrapped sequence did not pass on the image it showed");
        Check(!Ready(() => [new ExplicitSource()]) && !Ready(() => [new OverridingSource()]) && !Ready(() => [new OddSource()]),
            "Unverified video source reported ready");
        Check(!Ready(() => [new CachedVideoFileSource(new MFVideoFileSource2())], s => ((CachedVideoFileSource)s[0]).ServeWithoutInner = true),
            "Wrapper served a new time without its inner source holding it");
        Check(Ready(() => [new CachedVideoFileSource(new MFVideoFileSource2())], s => ((CachedVideoFileSource)s[0]).ServeWithoutInner = true, repeatTime: true),
            "Wrapper whose inner source holds the frame must be ready");
        Check(!Ready(() => [new CachedVideoFileSource(new OddSource())]) && !Ready(() => [new CachedVideoFileSource(new DerivedMf2Source())]),
            "Wrapper around an unverified source reported ready");
        Check(!Ready(() => [new CachedVideoFileSource(new CachedVideoFileSource(new MFVideoFileSource2()))]), "Nested wrapper reported ready");
    }

    private static void CheckLateBuiltInVideoSource()
    {
        var root = Scene(decoders: 0);
        root.Sources.Add(new MFVideoFileSource2());
        Render(root, Frame);
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame), "Precondition: verified source ready before late load");

        // A built-in assembly loaded mid-frame is hooked, but the frame that spans the change is unverified.
        string lateVerified = EmitLateAssembly(generic: false);
        Assembly? late = null;
        root.During = _ => late ??= Assembly.LoadFrom(lateVerified);
        Render(root, Frame * 2);
        root.During = null;
        Check(late is not null && CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame * 2),
            "A frame spanning a late hook change must be unverified");
        Check(FrameRenderReadiness.CoverageProblem is null, "Hookable late sources must not stop caching: " + FrameRenderReadiness.CoverageProblem);
        var coverage = FrameRenderReadiness.Coverage;
        Check(coverage.Any(line => line.StartsWith(FrameRenderReadiness.WicWebpTypeName + ": WIC", StringComparison.Ordinal))
            && coverage.Any(line => line.StartsWith("Late.LateOddSource: unverified", StringComparison.Ordinal)),
            "Late sources were not classified: " + string.Join(" | ", coverage));
        foreach (var name in new[] { FrameRenderReadiness.WicWebpTypeName, "Late.LateOddSource" })
            Check(Harmony.GetPatchInfo(late!.GetType(name, true)!.GetMethod("Update")!)?.Finalizers.Count == 1, name + " was not hooked");

        Render(root, Frame * 3);
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 3), "Frames after the late hook must be verifiable again");
        var lateWebp = (IVideoFileSource)Activator.CreateInstance(late!.GetType(FrameRenderReadiness.WicWebpTypeName, true)!)!;
        var lateOdd = (IVideoFileSource)Activator.CreateInstance(late.GetType("Late.LateOddSource", true)!)!;
        var mixed = Scene(decoders: 0);
        mixed.Sources.Add(lateWebp);
        Render(mixed, Frame * 4);
        Check(FrameRenderReadiness.WasLastUpdateReady(mixed, Frame * 4), "Late verified source that decoded must be ready");
        mixed.Sources.Add(lateOdd);
        Render(mixed, Frame * 5);
        Check(!FrameRenderReadiness.WasLastUpdateReady(mixed, Frame * 5), "Late unverified source reported ready");

        // A late source that cannot be hooked makes every later frame unverified.
        Assembly.LoadFrom(EmitLateAssembly(generic: true));
        Check(FrameRenderReadiness.CoverageProblem?.Contains("LateGenericSource") == true,
            "Unhookable late source was not reported: " + FrameRenderReadiness.CoverageProblem);
        Render(root, Frame * 6);
        Check(CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame * 6),
            "Frames must not be ready once an unhookable built-in video source can exist");
    }

    // Emits a real on-disk assembly that the binder treats as built-in (name prefix, host directory).
    private static string EmitLateAssembly(bool generic)
    {
        string directory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        string name = "YukkuriMovieMaker.Plugin.FileSource.Late" + Guid.NewGuid().ToString("N")[..8];
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        var interfaceUpdate = typeof(IVideoFileSource).GetMethod(nameof(IVideoFileSource.Update))!;
        const MethodAttributes implementation = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final
            | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
        TypeBuilder Source(string typeName)
        {
            var type = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            type.AddInterfaceImplementation(typeof(IVideoFileSource));
            type.DefineDefaultConstructor(MethodAttributes.Public);
            return type;
        }
        if (generic)
        {
            var type = Source("Late.LateGenericSource");
            type.DefineGenericParameters("T");
            var update = type.DefineMethod(nameof(IVideoFileSource.Update), implementation, typeof(void), [typeof(TimeSpan)]);
            update.GetILGenerator().Emit(OpCodes.Ret);
            type.DefineMethodOverride(update, interfaceUpdate);
            type.CreateType();
        }
        else
        {
            var odd = Source("Late.LateOddSource");
            var oddUpdate = odd.DefineMethod(nameof(IVideoFileSource.Update), implementation, typeof(void), [typeof(TimeSpan)]);
            oddUpdate.GetILGenerator().Emit(OpCodes.Ret);
            odd.DefineMethodOverride(oddUpdate, interfaceUpdate);
            odd.CreateType();

            // Carries a pinned host name, so the binder classifies it like the real WebP source.
            var webp = Source(FrameRenderReadiness.WicWebpTypeName);
            var webpUpdate = webp.DefineMethod(nameof(IVideoFileSource.Update), implementation, typeof(void), [typeof(TimeSpan)]);
            webpUpdate.GetILGenerator().Emit(OpCodes.Ret);
            webp.DefineMethodOverride(webpUpdate, interfaceUpdate);
            webp.CreateType();
        }
        string path = Path.Combine(directory, name + ".dll");
        builder.Save(path);
        emitted.Add(path);
        return path;
    }

    private static TimelineSource Scene(int decoders, TimelineSource[]? children = null)
    {
        var source = new TimelineSource();
        for (int i = 0; i < decoders; i++) source.Decoders.Add(new FakeDecoder());
        source.Children.AddRange(children ?? []);
        return source;
    }

    private static void Render(TimelineSource source, TimeSpan time) => source.Update(time, TimelineSourceUsage.Exporting);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Stand-in for TimelineFrameCache's patches: observes readiness where the real Postfix does.
    private static class CacheLike
    {
        internal static bool SkipNext;
        // The sequence images the cache's key says the frame shows (the capture's).
        internal static IReadOnlyCollection<string> Images = [];
        private static readonly ConditionalWeakTable<object, StrongBox<bool>> observed = new();

        internal static bool Prefix()
        {
            if (!SkipNext) return true;
            SkipNext = false;
            return false;
        }

        internal static void Postfix(object __instance) =>
            observed.AddOrUpdate(__instance, new StrongBox<bool>(FrameRenderReadiness.IsUpdateReady(__instance, Images)));

        internal static void Observe() { }

        internal static bool? Last(object source) => observed.TryGetValue(source, out var value) ? value.Value : null;
    }
}

internal sealed class FakeDecoder
{
    internal enum Mode { Decode, Timeout, Throw }
    internal Mode Behavior;
    internal TimeSpan? SampleTime;
    internal int Updates;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Update(TimeSpan time)
    {
        Interlocked.Increment(ref Updates);
        Thread.Sleep(1);
        switch (Behavior)
        {
            case Mode.Decode: SampleTime = time; break;
            case Mode.Timeout: break; // keeps the previous (stale) sample, like a timed-out read
            case Mode.Throw: throw new TimeoutException("decoder timeout");
        }
    }

    internal static bool Holds(object decoder, TimeSpan time) => decoder is FakeDecoder fake && fake.SampleTime is { } start
        && start <= time && time < start + Program.Frame;
}

internal sealed class FilterDecoder
{
    private int attempts;

    public void Update(TimeSpan time)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { Probe(); }
            catch (InvalidOperationException error) when (error.HResult == -2147220953 && clock.Elapsed < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(1);
                continue;
            }
            break;
        }
    }

    private void Probe() { if (attempts++ == 0) throw new InvalidOperationException { HResult = -2147220953 }; }
}

internal abstract class AbstractDecoder
{
    public abstract void Update(TimeSpan time);
}

internal sealed class OtherDecoder
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Update(TimeSpan time) { }
}

namespace YukkuriMovieMaker.Plugin.FileSource
{
    // Public so that the emitted late-loaded assembly can implement it.
    public interface IVideoFileSource
    {
        void Update(TimeSpan time);
    }

    internal enum VideoMode { Decode, Stale, Clear, Throw, Stretch, Unshifted, LateSeek }

    // Hook-shape fakes without a pinned name: always unverified, but still hooked.
    internal abstract class VideoSourceBase : IVideoFileSource
    {
        public abstract void Update(TimeSpan time);
    }

    internal sealed class OverridingSource : VideoSourceBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Update(TimeSpan time) => Thread.Sleep(1);
    }

    internal sealed class ExplicitSource : IVideoFileSource
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        void IVideoFileSource.Update(TimeSpan time) => Thread.Sleep(1);
    }

    internal sealed class OddSource : IVideoFileSource
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time) => Thread.Sleep(1);
    }
}

namespace YukkuriMovieMaker.Plugin.FileSource.MediaFoundation.Source2
{
    public sealed class DecodedFrame(TimeSpan sampleTime, TimeSpan sampleDuration)
    {
        public TimeSpan SampleTime { get; } = sampleTime;
        public TimeSpan SampleDuration { get; } = sampleDuration;
    }

    // Like the host: a failed TryDecodeAt leaves decodedFrame null and draws transparency.
    internal class MFVideoFileSource2 : IVideoFileSource
    {
        internal VideoMode Behavior;
        private DecodedFrame? decodedFrame;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            decodedFrame = Behavior switch
            {
                VideoMode.Decode => new DecodedFrame(time, global::Program.Frame),
                VideoMode.Stale => decodedFrame,
                VideoMode.Clear => null,
                _ => throw new TimeoutException("decoder timeout"),
            };
        }
    }

    // Shares the base Update hook under a different runtime type.
    internal sealed class DerivedMf2Source : MFVideoFileSource2;
}

namespace YukkuriMovieMaker.Plugin.FileSource.MediaFoundation
{
    // Like the host: Update moves t onto the stream clock; failures leave a zero duration.
    internal sealed class MFVideoFileSource : IVideoFileSource
    {
        internal VideoMode Behavior;
        private TimeSpan currentTime = TimeSpan.FromTicks(-1), currentDuration, streamStartTime;
        internal TimeSpan StreamStart { init => streamStartTime = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            time += streamStartTime;
            switch (Behavior)
            {
                case VideoMode.Decode: currentTime = time; currentDuration = global::Program.Frame; break;
                case VideoMode.Unshifted: currentTime = time - streamStartTime; currentDuration = global::Program.Frame; break;
                case VideoMode.Clear: currentTime = time; currentDuration = TimeSpan.Zero; break;
                case VideoMode.Throw: throw new TimeoutException("decoder timeout");
            }
        }
    }
}

namespace YukkuriMovieMaker.Plugin.FileSource.FFmpeg
{
    // Like the host, plus the early-stop fallback that stretches the last frame up to the stream end. It seeks like the
    // host (first, backwards or far ahead) and decodes on otherwise; LateSeek: a seek lands after t, and the first
    // frame decoded (one frame later) is shown from t on.
    internal sealed class FFmpegVideoFileSource : IVideoFileSource
    {
        internal VideoMode Behavior;
        private TimeSpan currentTime = TimeSpan.FromTicks(-1), currentDuration, streamStartTime;
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
        internal TimeSpan StreamStart { init => streamStartTime = value; }
        internal int Seeks;
        private bool sought;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SeekTo(TimeSpan time)
        {
            Seeks++;
            sought = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            time += streamStartTime;
            if (currentDuration > TimeSpan.Zero && currentTime <= time && time < currentTime + currentDuration) return;
            sought = false;
            if (currentDuration == TimeSpan.Zero || time < currentTime || currentTime + currentDuration + global::Program.Frame * 10 < time) SeekTo(time);
            switch (Behavior)
            {
                case VideoMode.Decode: currentTime = time; currentDuration = global::Program.Frame; break;
                case VideoMode.LateSeek when sought: currentTime = time; currentDuration = global::Program.Frame * 2; break;
                case VideoMode.LateSeek: currentTime = time; currentDuration = global::Program.Frame; break;
                case VideoMode.Unshifted: currentTime = time - streamStartTime; currentDuration = global::Program.Frame; break;
                case VideoMode.Stretch: currentTime = time - global::Program.Frame; currentDuration = streamStartTime + Duration - currentTime; break;
                case VideoMode.Clear: currentTime = time; currentDuration = TimeSpan.Zero; break;
                case VideoMode.Throw: throw new TimeoutException("decoder error");
            }
        }
    }
}

namespace YukkuriMovieMaker.Plugin.FileSource.WIC
{
    internal sealed class WICGifVideoSource : IVideoFileSource
    {
        internal VideoMode Behavior;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            if (Behavior == VideoMode.Throw) throw new TimeoutException("WIC decode failure");
        }
    }

    // Like the host: the image of GetFrameIndex(t) is loaded; one no reader opens leaves source null (an empty bitmap).
    internal sealed class WICSequentialImageVideoSource : IVideoFileSource
    {
        private readonly string[] frames = Enumerable.Range(0, 20).Select(index => "seq" + index + ".png").ToArray();
        private int currentFrame = -1;
        private object? source;
        internal bool Unreadable;

        public int GetFrameIndex(TimeSpan time) => (int)(time.Ticks * 60 / TimeSpan.TicksPerSecond);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            int index = GetFrameIndex(time);
            if (currentFrame == index) return;
            currentFrame = index;
            source = Unreadable ? null : frames[Math.Clamp(index, 0, frames.Length - 1)];
        }
    }
}

namespace YukkuriMovieMaker.Plugin
{
    internal sealed class VideoResource(IVideoFileSource source)
    {
        public IVideoFileSource Source { get; } = source;
    }

    // Like the host: every Update is delegated to resource.Source.
    internal sealed class CachedVideoFileSource(IVideoFileSource inner) : IVideoFileSource
    {
        private readonly VideoResource resource = new(inner);
        internal bool ServeWithoutInner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            if (!ServeWithoutInner) resource.Source.Update(time);
        }
    }
}

namespace YukkuriMovieMaker.Player.Video
{
    internal enum TimelineSourceUsage { Playing, Exporting }

    internal sealed class TimelineSource
    {
        internal const string FailureMessage = "fake render failure";
        internal readonly List<FakeDecoder> Decoders = [];
        internal readonly List<YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource> Sources = [];
        internal readonly List<TimelineSource> Children = [];
        internal Action<TimeSpan>? During;
        internal bool Throw;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time, TimelineSourceUsage usage)
        {
            var work = Decoders.Cast<object>().Concat(Sources).Concat(Children).ToArray();
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                try
                {
                    switch (item)
                    {
                        case FakeDecoder decoder: decoder.Update(time); break;
                        case YukkuriMovieMaker.Plugin.FileSource.IVideoFileSource video: video.Update(time); break;
                        default: ((TimelineSource)item).Update(time, usage); break;
                    }
                }
                catch (TimeoutException) { } // the host draws nothing for this item and continues
            });
            During?.Invoke(time);
            if (Throw) throw new InvalidOperationException(FailureMessage);
        }
    }
}
