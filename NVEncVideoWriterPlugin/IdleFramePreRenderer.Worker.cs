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
    // Every batch runs on one worker thread, which creates, uses and disposes the batches' renderer (BatchRenderer).
    // The renderer is kept from batch to batch and released once no batch has come for RendererIdleTime.
    private static readonly BlockingCollection<Action> work = new();
    private static Thread? worker;
    private static readonly TimeSpan RendererIdleTime = TimeSpan.FromSeconds(2);
    private static BatchRenderer? renderer; // worker thread only
    private static Session? rendererSession; // worker thread only

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
            Model = model;
            Fingerprints = liveTracker.VerifiedFingerprints;
            CloneScene = CloneSceneFromModel(model);
            CloneTracker = new KeyDependencyTracker(CloneScene, Fingerprints);
            try { Source = CreateBatchSource(CloneScene); }
            catch { CloneTracker.Dispose(); throw; }
        }
        internal string Model { get; }
        // The live tracker's verified fingerprints the clone compares its files with (replaced, never changed, when
        // the live tracker verifies files again).
        internal IReadOnlyDictionary<string, FileFingerprint>? Fingerprints { get; }
        internal Scene CloneScene { get; }
        internal KeyDependencyTracker CloneTracker { get; }
        internal TimelineSourceAndDevices Source { get; }
        public void Dispose()
        {
            try { ((IDisposable)Source).Dispose(); }
            finally { CloneTracker.Dispose(); }
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

    private static void Post(Action action)
    {
        lock (gate)
        {
            if (worker is null)
            {
                var thread = new Thread(WorkerLoop) { IsBackground = true, Name = "YMM4-RTX3060-NVENC idle pre-render" };
                thread.Start();
                Volatile.Write(ref worker, thread);
            }
        }
        work.Add(action);
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            if (!work.TryTake(out var action, RendererIdleTime))
            {
                DropRenderer(); // a pause in the work: give the renderer's GPU memory back
                continue;
            }
            try { action(); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        }
    }
}
