using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YukkuriMovieMaker.ItemEditor;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace NVEncVideoWriterPlugin;

// The batches' worker thread and the renderer it keeps from batch to batch (a clone of the live model, its tracker
// and TimelineSourceAndDevices).
internal static partial class IdleFramePreRenderer
{
    // Each worker creates, uses and disposes its own renderer (BatchRenderer) on its owning STA thread.
    // The renderer is kept from batch to batch and released once no batch has come for RendererIdleTime.
    private static readonly BlockingCollection<Action>[] work = Enumerable.Range(0, 4).Select(_ => new BlockingCollection<Action>()).ToArray();
    private static readonly Thread?[] workers = new Thread?[4];
    private static readonly int[] dropPending = new int[4];
    private static long measuredWorkerBytes;
    internal static void ResetWorkerMemory() => Interlocked.Exchange(ref measuredWorkerBytes, 0);
    internal static long MeasuredWorkerBytes => Interlocked.Read(ref measuredWorkerBytes);
    private static readonly TimeSpan RendererIdleTime = TimeSpan.FromSeconds(2);
    [ThreadStatic] private static BatchRenderer? renderer;
    [ThreadStatic] private static Session? rendererSession;

    // The renderer of an idle clone (BatchRenderer; tests drive batches through this and PrimeFrame). Its Updates
    // skip the live preview's cache: PrimeFrame keys and stores its frames itself.
    internal static TimelineSourceAndDevices CreateBatchSource(Scene cloneScene)
    {
        var source = new TimelineSourceAndDevices(cloneScene);
        try { TimelineFrameCache.ExcludeFromPreviewCache(source); }
        catch { ((IDisposable)source).Dispose(); throw; }
        return source;
    }

    // A clone of the live model with its own tracker and renderer (CreateBatchSource), for idle batches of that model.
    internal sealed class BatchRenderer : IDisposable
    {
        internal BatchRenderer(KeyDependencyTracker liveTracker, string model)
        {
            MemoryGeneration = GpuMemoryController.AdapterGeneration;
            MemoryBefore = GpuMemoryController.Probe()?.Sample;
            Model = model;
            Fingerprints = liveTracker.VerifiedFingerprints;
            CloneScene = CloneSceneFromModel(model);
            CloneTracker = new KeyDependencyTracker(CloneScene, Fingerprints);
            try { Source = CreateBatchSource(CloneScene); }
            catch { CloneTracker.Dispose(); throw; }
        }
        private long MemoryGeneration { get; }
        private GpuMemorySnapshot? MemoryBefore { get; }
        internal void ObserveMemory()
        {
            var now = GpuMemoryController.Probe()?.Sample;
            if (MemoryGeneration != GpuMemoryController.AdapterGeneration) return;
            if (MemoryBefore is not { Software: false } before || now is not { Software: false } after) return;
            long delta = after.CurrentUsage - before.CurrentUsage;
            if (delta <= 0) return;
            // Include concurrent process allocations and a 2x margin; never lower a measured high-water mark.
            long reserve = delta > long.MaxValue / 2 ? long.MaxValue : delta * 2;
            long previous;
            do
            {
                previous = Interlocked.Read(ref measuredWorkerBytes);
                if (reserve <= previous) return;
            } while (Interlocked.CompareExchange(ref measuredWorkerBytes, reserve, previous) != previous);
            if (MemoryGeneration != GpuMemoryController.AdapterGeneration) ResetWorkerMemory();
        }
        internal string Model { get; }
        // The live tracker's verified fingerprints the clone compares its files with (replaced, never changed, when
        // the live tracker verifies files again).
        internal IReadOnlyDictionary<string, FileFingerprint>? Fingerprints { get; }
        internal Scene CloneScene { get; }
        internal KeyDependencyTracker CloneTracker { get; }
        internal TimelineSourceAndDevices Source { get; }
        // A renderer of the live scene itself, made on the first frame keyed by the live objects' identities.
        internal TimelineSourceAndDevices? LiveSource { get; private set; }
        internal TimelineSourceAndDevices LiveSourceFor(Scene liveScene) => LiveSource ??= CreateBatchSource(liveScene);
        public void Dispose()
        {
            try { ((IDisposable)Source).Dispose(); }
            finally
            {
                try { if (LiveSource is not null) ((IDisposable)LiveSource).Dispose(); }
                finally { CloneTracker.Dispose(); }
            }
        }
    }

    // Worker thread: the last batch's renderer when it renders the same model for the same session with the same
    // verified files, else a new one. The model is the whole drawing state, so an equal model is an equal clone;
    // PrimeFrame still compares the live and clone keys of every frame.
    internal static bool Reusable(BatchRenderer? kept, KeyDependencyTracker liveTracker, string model) =>
        kept is not null && kept.Model == model && ReferenceEquals(kept.Fingerprints, liveTracker.VerifiedFingerprints);

    private static BatchRenderer RendererFor(Session current, string model)
    {
        if (renderer is { } kept && ReferenceEquals(rendererSession, current) && Reusable(kept, current.Tracker, model)) return kept;
        DropRenderer();
        renderer = new BatchRenderer(current.Tracker, model);
        rendererSession = current;
        return renderer;
    }

    // Worker thread.
    private static void DropRenderer()
    {
        var dropped = renderer;
        renderer = null;
        rendererSession = null;
        try { dropped?.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    private static void DropAllRenderers()
    {
        lock (gate)
            for (int index = 0; index < workers.Length; index++)
                if (workers[index] is not null && Interlocked.Exchange(ref dropPending[index], 1) == 0)
                {
                    int assigned = index;
                    Post(() =>
                    {
                        try { DropRenderer(); }
                        finally { Volatile.Write(ref dropPending[assigned], 0); }
                    }, assigned);
                }
    }

    private static void Post(Action action, int index = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= workers.Length) throw new ArgumentOutOfRangeException(nameof(index));
        lock (gate)
        {
            if (workers[index] is null)
            {
                // Below YMM4's own threads: more workers must not make editing or the player's own frames slower.
                var thread = new Thread(() => WorkerLoop(index))
                    { IsBackground = true, Name = "YMM4 idle pre-render " + index, Priority = ThreadPriority.BelowNormal };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                workers[index] = thread;
            }
        }
        work[index].Add(action);
    }

    private static void WorkerLoop(int index)
    {
        while (true)
        {
            if (!work[index].TryTake(out var action, RendererIdleTime))
            {
                DropRenderer(); // a pause in the work: give the renderer's GPU memory back
                continue;
            }
            try { action(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        }
    }

    // Called on worker 0. All participants finish before the job/token/cursor can be released.
    private static void DispatchWorkers(int count, Action<int> action)
    {
        count = Math.Clamp(count, 1, 4);
        using var done = new CountdownEvent(count);
        var failures = new ConcurrentQueue<Exception>();
        void Run(int index)
        {
            try { action(index); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { DropRenderer(); failures.Enqueue(error); }
            finally { done.Signal(); }
        }
        for (int index = 1; index < count; index++) { int assigned = index; Post(() => Run(assigned), assigned); }
        Run(0); done.Wait();
        if (failures.TryDequeue(out var failure)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void RunWorkersForTests(int count, Action<int> action)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        Post(() =>
        {
            try { DispatchWorkers(count, action); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        });
        if (!finished.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Parallel idle workers exceeded 30 seconds");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // A session frame uses the live objects and is assigned exclusively to worker 0.
    internal static IEnumerable<long> AssignedOrdinals(long first, long last, int worker, int count, Func<long, bool> session)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        if (count is < 1 or > 4 || worker < 0 || worker >= count) throw new ArgumentOutOfRangeException(nameof(count));
        for (long ordinal = first; ordinal <= last; ordinal++)
            if (session(ordinal) ? worker == 0 : (ordinal - first) % count == worker) yield return ordinal;
    }
}
