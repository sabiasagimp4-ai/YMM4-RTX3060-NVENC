using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.FileSource;

// Host-independent checks for FrameRenderReadiness. Fake types mimic only the call shape that matters:
// TimelineSource.Update fans decoder updates (and nested scenes) out through Parallel.ForEach, swallows
// decoder failures the way the host renders them as transparent output, and returns normally.
// The video source fakes follow the shapes recorded in CLAUDE_HANDOFF.md; they are not host code.
internal static class Program
{
    private const string Owner = "ymm.tests.readiness";
    private static readonly List<string> emitted = [];
    internal static readonly TimeSpan Frame = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);
    private static readonly MethodBase[] VideoSourceUpdates =
    [
        typeof(Mf2Source).GetMethod(nameof(Mf2Source.Update))!,
        typeof(ExplicitMf2Source).GetInterfaceMap(typeof(IVideoFileSource)).TargetMethods.Single(),
        typeof(OverridingMf2Source).GetMethod(nameof(OverridingMf2Source.Update))!,
        typeof(LegacySource).GetMethod(nameof(LegacySource.Update))!,
        typeof(CachedVideoFileSource).GetMethod(nameof(CachedVideoFileSource.Update))!,
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
            CheckDecoderTimeoutPropagatesToParents();
            CheckSwallowedDecoderException();
            CheckUpdateExceptionIsPreserved();
            CheckSkippedOriginalKeepsScope();
            CheckUnattributedDecodeFailsInFlightFrames();
            CheckCompletedScopeCapturedByLateTask();
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
            Console.WriteLine("Render readiness host binding: MF2/legacy/wrapper/unverified sources and late-loaded coverage OK");
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
    }

    private static void CheckOnlyCacheLikePatches(MethodBase update, MethodBase[] decoders, string stage)
    {
        var info = Harmony.GetPatchInfo(update)!;
        Check(info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod.DeclaringType == typeof(CacheLike)
            && info.Postfixes.Count == 1 && info.Finalizers.Count == 0, $"Readiness patches left on Update after {stage}");
        foreach (var decoder in decoders)
            Check(Harmony.GetPatchInfo(decoder) is not { } decoderInfo || decoderInfo.Finalizers.Count == 0,
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

    private static void CheckHostCoverage()
    {
        var coverage = FrameRenderReadiness.Coverage;
        void Expect(Type type, string kind) => Check(coverage.Any(line => line.StartsWith(type.FullName + ": " + kind, StringComparison.Ordinal)),
            $"{type.Name} was not classified as {kind}: {string.Join(" | ", coverage)}");
        Expect(typeof(Mf2Source), "MF2");
        Expect(typeof(DerivedMf2Source), "MF2");
        Expect(typeof(ExplicitMf2Source), "MF2");
        Expect(typeof(OverridingMf2Source), "MF2");
        Expect(typeof(LegacySource), "legacy");
        Expect(typeof(CachedVideoFileSource), "wrapper");
        Expect(typeof(OddSource), "unverified");
        Check(coverage.Count == 7, "Unexpected video source coverage: " + string.Join(" | ", coverage));
        // The offline shape report must predict exactly what the binder decided.
        foreach (var line in coverage)
        {
            string typeName = line[..line.IndexOf(": ", StringComparison.Ordinal)];
            string kind = line[(typeName.Length + 2)..].Split(' ')[0];
            var type = typeof(Program).Assembly.GetType(typeName, true)!;
            Check(ShapeRules.Predict(type, typeof(IVideoFileSource)) == kind, $"Shape report predicts {ShapeRules.Predict(type, typeof(IVideoFileSource))} for {line}");
        }
        foreach (var method in VideoSourceUpdates)
            Check(Harmony.GetPatchInfo(method)?.Finalizers.Count == 1, $"{method.DeclaringType?.Name}.Update was not hooked exactly once");
    }

    private static void CheckHostVideoSources()
    {
        // Renders frame 2, applies the change, then renders frame 3 (or frame 2 again).
        bool Ready(Func<IVideoFileSource[]> create, Action<IVideoFileSource[]>? before = null, bool repeatTime = false)
        {
            var sources = create();
            var root = Scene(decoders: 0);
            root.Sources.AddRange(sources);
            Render(root, Frame * 2);
            before?.Invoke(sources);
            var time = repeatTime ? Frame * 2 : Frame * 3;
            Render(root, time);
            return CacheLike.Last(root) == true && FrameRenderReadiness.WasLastUpdateReady(root, time);
        }
        Check(Ready(() => [new Mf2Source(), new DerivedMf2Source(), new ExplicitMf2Source(), new OverridingMf2Source(),
            new LegacySource(), new CachedVideoFileSource(new Mf2Source())]), "Verified sources that decoded must be ready");

        Check(!Ready(() => [new Mf2Source()], s => ((Mf2Source)s[0]).Behavior = VideoMode.Stale), "MF2 stale frame reported ready");
        Check(!Ready(() => [new Mf2Source()], s => ((Mf2Source)s[0]).Behavior = VideoMode.Clear), "MF2 cleared frame reported ready");
        Check(!Ready(() => [new Mf2Source()], s => ((Mf2Source)s[0]).Behavior = VideoMode.Throw), "MF2 swallowed exception reported ready");
        Check(!Ready(() => [new DerivedMf2Source()], s => ((Mf2Source)s[0]).Behavior = VideoMode.Stale),
            "Shared inherited hook did not check the derived instance");
        Check(!Ready(() => [new ExplicitMf2Source()], s => ((ExplicitMf2Source)s[0]).Behavior = VideoMode.Stale),
            "Explicit interface implementation was not checked");
        Check(!Ready(() => [new OverridingMf2Source()], s => ((OverridingMf2Source)s[0]).Behavior = VideoMode.Stale),
            "Abstract-base override was not checked");
        Check(!Ready(() => [new LegacySource()], s => ((LegacySource)s[0]).Behavior = VideoMode.Clear),
            "Legacy error (currentDuration = 0) reported ready");
        Check(!Ready(() => [new LegacySource { StreamStart = Frame * 2 }]) && !Ready(() => [new LegacySource { StreamStart = Frame * 2, Normalized = true }]),
            "Legacy nonzero stream start is unverified until host semantics are confirmed");
        Check(Ready(() => [new LegacySource { Normalized = true }]), "Legacy with zero stream start must be ready under either reading");
        Check(!Ready(() => [new CachedVideoFileSource(new Mf2Source())], s => ((CachedVideoFileSource)s[0]).ServeWithoutInner = true),
            "Wrapper served a new time without its inner source holding it");
        Check(Ready(() => [new CachedVideoFileSource(new Mf2Source())], s => ((CachedVideoFileSource)s[0]).ServeWithoutInner = true, repeatTime: true)
            && Ready(() => [new CachedVideoFileSource(new Mf2Source())]), "Wrapper whose inner source holds the frame must be ready");
        Check(!Ready(() => [new CachedVideoFileSource(new OddSource())]), "Wrapper around an unverified source reported ready");
        Check(!Ready(() => [new CachedVideoFileSource(new CachedVideoFileSource(new Mf2Source()))]), "Nested wrapper reported ready");
        Check(!Ready(() => [new OddSource()]), "Unverified video source reported ready");
    }

    private static void CheckLateBuiltInVideoSource()
    {
        var root = Scene(decoders: 0);
        root.Sources.Add(new Mf2Source());
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
        Check(coverage.Any(line => line.StartsWith("Late.LateMf2Source: MF2", StringComparison.Ordinal))
            && coverage.Any(line => line.StartsWith("Late.LateOddSource: unverified", StringComparison.Ordinal)),
            "Late sources were not classified: " + string.Join(" | ", coverage));
        foreach (var name in new[] { "Late.LateMf2Source", "Late.LateOddSource" })
            Check(Harmony.GetPatchInfo(late!.GetType(name, true)!.GetMethod("Update")!)?.Finalizers.Count == 1, name + " was not hooked");

        Render(root, Frame * 3);
        Check(FrameRenderReadiness.WasLastUpdateReady(root, Frame * 3), "Frames after the late hook must be verifiable again");
        var lateMf2 = (IVideoFileSource)Activator.CreateInstance(late!.GetType("Late.LateMf2Source", true)!)!;
        var lateOdd = (IVideoFileSource)Activator.CreateInstance(late.GetType("Late.LateOddSource", true)!)!;
        var mixed = Scene(decoders: 0);
        mixed.Sources.Add(lateMf2);
        Render(mixed, Frame * 4);
        Check(FrameRenderReadiness.WasLastUpdateReady(mixed, Frame * 4), "Late MF2-shaped source that decoded must be ready");
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

            var mf2 = Source("Late.LateMf2Source");
            var frame = mf2.DefineField("decodedFrame", typeof(DecodedFrame), FieldAttributes.Private);
            var update = mf2.DefineMethod(nameof(IVideoFileSource.Update), implementation, typeof(void), [typeof(TimeSpan)]);
            var il = update.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarga_S, (byte)1);
            il.Emit(OpCodes.Call, typeof(TimeSpan).GetProperty(nameof(TimeSpan.Ticks))!.GetMethod!);
            il.Emit(OpCodes.Ldc_I8, Frame.Ticks);
            il.Emit(OpCodes.Newobj, typeof(DecodedFrame).GetConstructor([typeof(long), typeof(long)])!);
            il.Emit(OpCodes.Stfld, frame);
            il.Emit(OpCodes.Ret);
            mf2.DefineMethodOverride(update, interfaceUpdate);
            mf2.CreateType();
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

    internal enum VideoMode { Decode, Stale, Clear, Throw }

    // Public so that emitted late-loaded sources can construct it.
    public sealed class DecodedFrame(long sampleTime, long sampleDuration)
    {
        public long SampleTime { get; } = sampleTime;
        public long SampleDuration { get; } = sampleDuration;
    }

    internal static class FakeMf2
    {
        internal static DecodedFrame? Next(DecodedFrame? current, VideoMode mode, TimeSpan time)
        {
            Thread.Sleep(1);
            return mode switch
            {
                VideoMode.Decode => new DecodedFrame(time.Ticks, global::Program.Frame.Ticks),
                VideoMode.Stale => current,
                VideoMode.Clear => null,
                _ => throw new TimeoutException("decoder timeout"),
            };
        }
    }

    internal class Mf2Source : IVideoFileSource
    {
        internal VideoMode Behavior;
        private DecodedFrame? decodedFrame;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time) => decodedFrame = FakeMf2.Next(decodedFrame, Behavior, time);
    }

    // Inherits Update: one hook must dispatch on the runtime type.
    internal sealed class DerivedMf2Source : Mf2Source;

    internal sealed class ExplicitMf2Source : IVideoFileSource
    {
        internal VideoMode Behavior;
        private DecodedFrame? decodedFrame;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void IVideoFileSource.Update(TimeSpan time) => decodedFrame = FakeMf2.Next(decodedFrame, Behavior, time);
    }

    internal abstract class VideoSourceBase : IVideoFileSource
    {
        public abstract void Update(TimeSpan time);
    }

    internal sealed class OverridingMf2Source : VideoSourceBase
    {
        internal VideoMode Behavior;
        private DecodedFrame? decodedFrame;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Update(TimeSpan time) => decodedFrame = FakeMf2.Next(decodedFrame, Behavior, time);
    }

    internal sealed class LegacySource : IVideoFileSource
    {
        internal VideoMode Behavior;
        internal TimeSpan StreamStart { get => TimeSpan.FromTicks(streamStartTime); init => streamStartTime = value.Ticks; }
        internal bool Normalized { get; init; } // sample times already relative to the stream start
        private long currentTime, currentDuration, streamStartTime;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            Thread.Sleep(1);
            switch (Behavior)
            {
                // Either reading of the stream start is plausible until the host code is confirmed.
                case VideoMode.Decode:
                    currentTime = time.Ticks + (Normalized ? 0 : streamStartTime);
                    currentDuration = global::Program.Frame.Ticks;
                    break;
                case VideoMode.Clear: currentDuration = 0; break;
                case VideoMode.Throw: throw new TimeoutException("decoder timeout");
            }
        }
    }

    internal sealed class CachedVideoFileSource(IVideoFileSource inner) : IVideoFileSource
    {
        private readonly IVideoFileSource source = inner;
        internal bool ServeWithoutInner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time)
        {
            if (!ServeWithoutInner) source.Update(time);
        }
    }

    internal sealed class OddSource : IVideoFileSource
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(TimeSpan time) => Thread.Sleep(1);
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
