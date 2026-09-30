using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Player.Video;

// Host-independent checks for FrameRenderReadiness. Fake types mimic only the call shape that matters:
// TimelineSource.Update fans decoder updates (and nested scenes) out through Parallel.ForEach, swallows
// decoder failures the way the host renders them as transparent output, and returns normally.
internal static class Program
{
    private const string Owner = "ymm.tests.readiness";
    private static readonly TimeSpan Frame = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

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

            Check(!FrameRenderReadiness.TryInstall(typeof(Program).Assembly, harmony, out var hostReason)
                && hostReason.Contains("not been verified"), "Unverified host decoder contract must reject install: " + hostReason);
            CheckOnlyCacheLikePatches(update, decode, "unverified host install");

            CheckRollbackOnExternalOwner(harmony, update);

            Check(FrameRenderReadiness.TryInstall(update, checks, harmony, out var reason), reason);
            Check(!FrameRenderReadiness.TryInstall(update, checks, harmony, out _), "Second install must be rejected");

            CheckReadyFrame();
            CheckDecoderTimeoutPropagatesToParents();
            CheckSwallowedDecoderException();
            CheckUpdateExceptionIsPreserved();
            CheckSkippedOriginalKeepsScope();
            CheckUnattributedDecodeFailsInFlightFrames();
            CheckCompletedScopeCapturedByLateTask();
            CheckIdleDecodeIsIgnored();
            CheckConcurrentRendersAreIsolated();

            FrameRenderReadiness.Uninstall(harmony);
            CheckOnlyCacheLikePatches(update, decode, "uninstall");
            var root = Scene(decoders: 1);
            Render(root, Frame);
            Check(CacheLike.Last(root) == false && !FrameRenderReadiness.WasLastUpdateReady(root, Frame),
                "Uninstalled readiness must not report frames ready");
            Console.WriteLine("Render readiness: attribution, propagation, fail-closed paths, rollback and uninstall OK");
            return 0;
        }
        finally { FrameRenderReadiness.Uninstall(harmony); harmony.UnpatchAll(Owner); }
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
            CheckOnlyCacheLikePatches(update, decode, "external-owner rollback");
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
        CheckOnlyCacheLikePatches(update, decode, "mid-install rollback: " + partialReason);
    }

    private static void CheckOnlyCacheLikePatches(MethodBase update, MethodBase decode, string stage)
    {
        var info = Harmony.GetPatchInfo(update)!;
        Check(info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod.DeclaringType == typeof(CacheLike)
            && info.Postfixes.Count == 1 && info.Finalizers.Count == 0, $"Readiness patches left on Update after {stage}");
        Check(Harmony.GetPatchInfo(decode) is not { } decoderInfo || decoderInfo.Finalizers.Count == 0,
            $"Readiness patches left on decoder after {stage}");
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

    private static void CheckCompletedScopeCapturedByLateTask()
    {
        var first = Scene(decoders: 1);
        var second = Scene(decoders: 1);
        var release = new ManualResetEventSlim();
        Task? late = null;
        var stray = new FakeDecoder();
        first.During = time => late = Task.Run(() => { release.Wait(); stray.Update(time); });
        Render(first, Frame);
        Check(FrameRenderReadiness.WasLastUpdateReady(first, Frame), "First frame should be ready");
        second.During = _ => { release.Set(); late!.Wait(); };
        Render(second, Frame);
        Check(CacheLike.Last(second) == false, "Decode on a completed scope's captured context must fail in-flight frames");
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
        private static readonly ConditionalWeakTable<object, StrongBox<bool>> observed = new();

        internal static bool Prefix()
        {
            if (!SkipNext) return true;
            SkipNext = false;
            return false;
        }

        internal static void Postfix(object __instance) =>
            observed.AddOrUpdate(__instance, new StrongBox<bool>(FrameRenderReadiness.IsUpdateReady(__instance)));

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
    private static readonly TimeSpan SampleDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

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
        && start <= time && time < start + SampleDuration;
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

namespace YukkuriMovieMaker.Player.Video
{
    internal enum TimelineSourceUsage { Playing, Exporting }

    internal sealed class TimelineSource
    {
        internal const string FailureMessage = "fake render failure";
        internal readonly List<FakeDecoder> Decoders = [];
        internal readonly List<TimelineSource> Children = [];
        internal Action<TimeSpan>? During;
        internal bool Throw;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time, TimelineSourceUsage usage)
        {
            var work = Decoders.Cast<object>().Concat(Children).ToArray();
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                if (item is FakeDecoder decoder)
                {
                    try { decoder.Update(time); }
                    catch (TimeoutException) { } // the host draws nothing for this item and continues
                }
                else ((TimelineSource)item).Update(time, usage);
            });
            During?.Invoke(time);
            if (Throw) throw new InvalidOperationException(FailureMessage);
        }
    }
}
