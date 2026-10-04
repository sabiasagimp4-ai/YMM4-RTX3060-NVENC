using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using ItemRect = (YukkuriMovieMaker.Project.Items.IVideoItem item, Vortice.RawRectF rect, System.Numerics.Vector2[] quad,
    YukkuriMovieMaker.Player.Video.DrawDescription desc, System.Collections.Generic.IEnumerable<YukkuriMovieMaker.Player.Video.VideoController> itemControllers);

namespace NVEncVideoWriterPlugin;

// Storing rendered preview frames: the GPU readbacks waiting on a source, their completion on its render thread,
// and showing the pixels drawn for the store instead of drawing the frame again (drawn once).
internal static partial class TimelineFrameCache
{
    private sealed class DeferredStore(Pending pending, PreviewReadback readback) : IDisposable
    {
        internal readonly Pending Pending = pending;
        internal readonly PreviewReadback Readback = readback;
        public void Dispose() { Readback.Dispose(); Pending.Release(); }
    }

    // A preview frame the host rendered (playing, paused, seeking) is stored for its view, like an idle pre-rendered
    // one. Completion is polled on the render thread without waiting for the GPU. Up to ReadbacksInFlight copies per
    // source remain pending, so that a GPU a frame or two behind does not skip frames; a capture is skipped only
    // while all are busy. True when it took over `pending`.
    // The frame is drawn once: the pixels drawn for the store are also what the player shows (ShowRendered).
    private static bool StorePreview(object source, Pending pending, ID2D1CommandList output, PreviewViewport viewport)
    {
        var state = pending.State;
        bool full;
        lock (cacheGate) full = state.Deferred.Count >= ReadbacksInFlight;
        if (full)
        {
            CompleteDeferred(state);
            lock (cacheGate) full = state.Deferred.Count >= ReadbacksInFlight;
        }
        if (full)
        {
            using var trace = CacheTrace.Measure("cache-admission", "policy");
            if (trace is not null) trace.Outcome = "readback-busy";
            Interlocked.Increment(ref readbackBusySkips);
            return false;
        }
        if (!pending.State.Economics.ShouldAdmit(pending.CacheKey!, viewport.FrameBytes + PreviewRecordHeader, gpuRetentionEnabled))
        {
            using var trace = CacheTrace.Measure("cache-admission", "policy");
            if (trace is not null) trace.Outcome = "render-cheaper";
            return false;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var readback = BeginPreviewReadback(pending.Devices.DeviceContext, output, viewport, DrawOnce, state.Pool, out var shown, out var shownTarget);
        if (readback is null) return false;
        try
        {
            lock (cacheGate)
            {
                if (!StillCurrent(pending) || state.IsDisposed || state.Deferred.Count >= ReadbacksInFlight) { readback.Dispose(); return false; }
                pending.HandOver();
                state.Deferred.AddLast(new DeferredStore(pending, readback));
                if (shown is not null && ShowRendered(source, pending, shown, viewport.FrameBytes, output, viewport, shownTarget))
                {
                    shown = null;
                    shownTarget = null;
                }
            }
        }
        finally
        {
            if (shown is not null) DisposeShownCopy(shown, viewport, state.Pool, shownTarget);
        }
        Interlocked.Add(ref previewStoreTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (!pending.Playing && !refreshSupported) CompleteDeferred(state, limit: ReadbacksInFlight);
        return true;
    }

    // An export frame the host rendered is stored the same way, without the GPU stopping for it: its copy is finished
    // by a later Update of the export. With every copy still pending, the oldest one (drawn three frames before) is
    // waited for, so that every exported frame is stored. True when it took over `pending`.
    private static bool StoreExport(Pending pending, ID2D1CommandList output)
    {
        var state = pending.State;
        bool full;
        lock (cacheGate) full = state.Deferred.Count >= ReadbacksInFlight;
        if (full) CompleteOldestDeferred(state, waitForGpu: true);
        // Without the source's pool: export frames can be of any size (4K: 33 MB each), and pooled textures would stay
        // reserved against the GPU budget after the export.
        var readback = BeginSceneReadback(pending.Devices.DeviceContext, output, pending.Scene, null);
        if (readback is null) return false;
        lock (cacheGate)
        {
            if (!StillCurrent(pending) || state.IsDisposed || state.Deferred.Count >= ReadbacksInFlight) { readback.Dispose(); return false; }
            pending.HandOver();
            state.Deferred.AddLast(new DeferredStore(pending, readback));
        }
        return true;
    }

    // When the export's source is disposed: its last frames' copies are finished (the GPU has drawn them by then) and
    // stored. Preview copies are dropped as before, and so are export copies whose device context is already gone
    // (completing checks the context's state).
    private static void FinishExportStores(SourceState state)
    {
        try
        {
            while (true)
            {
                lock (cacheGate)
                    if (state.IsDisposed || state.Deferred.First?.Value is not { Readback.SceneOrigin: not null } oldest
                        || oldest.Pending.Devices.DeviceContext is not { NativePointer: not 0 }) return;
                if (!CompleteOldestDeferred(state, waitForGpu: true)) return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    // Under cacheGate, on the render thread, after the host's update of `pending` (CachePostfix). `shown` draws the
    // pixels BeginPreviewReadback drew from the host's output for `viewport`; the player's Draw then blits them
    // instead of evaluating the composition a second time. Pixels and accounting as for a hit (CommitReplacement),
    // but the host's output is kept, still owned by the host's disposer, for a Draw with another view.
    // `target` (null without a pool) is the drawing target `shown` draws; the source returns it to its pool when the
    // copy is gone.
    private static bool ShowRendered(object source, Pending pending, ID2D1CommandList shown, long bytes,
        ID2D1CommandList hostOutput, PreviewViewport viewport, PooledTarget? target)
    {
        // StorePreview has just checked StillCurrent under the same lock.
        if (!sources.TryGetValue(source, out var state) || !ReferenceEquals(state, pending.State)
            || !ReferenceEquals(outputField.GetValue(source), hostOutput) || state.HostOutput is { NativePointer: not 0 }
            || state.Bytes != 0 || state.ActiveGpuFrame is not null || state.ShownTarget is not null) return false;
        var collector = (DisposeCollector)collectorField.GetValue(source)!;
        collector.Collect(shown);
        outputField.SetValue(source, shown);
        state.HostOutput = hostOutput;
        state.ShownViewport = viewport;
        state.ShownTarget = target;
        state.LastOutput = shown;
        state.LastViewportKey = pending.CacheKey;
        state.Bytes = bytes;
        Interlocked.Increment(ref drawnOnce);
        return true;
    }

    // Before the player draws (its Draw prefix, on the render thread): a copy shown by ShowRendered is exact only for
    // the view it was drawn for. After a zoom, pan or resize the host's output is shown again, as without the copy.
    private static void ShowHostOutputIfViewChanged(object source, SourceState state, PreviewViewport viewport)
    {
        if (state.HostOutput is null) return; // every Draw: nothing shown from a copy
        lock (cacheGate)
        {
            if (state.HostOutput is not { NativePointer: not 0 } host || viewport.Normalized == state.ShownViewport.Normalized) return;
            if (outputField.GetValue(source) is not ID2D1CommandList shown || !ReferenceEquals(shown, state.LastOutput)) return;
            using var trace = CacheTrace.Measure("shown-copy", "state");
            if (trace is not null) trace.Outcome = "view-changed";
            var collector = (DisposeCollector)collectorField.GetValue(source)!;
            outputField.SetValue(source, host);
            collector.Remove(shown);
            shown.Dispose();
            var released = Interlocked.Exchange(ref state.Bytes, 0);
            if (released != 0) Interlocked.Add(ref gpuBytes, -released);
            state.ActiveGpuFrame?.Release(); state.ActiveGpuFrame = null;
            state.ReleaseShownTarget();
            state.HostOutput = null;
            state.LastOutput = host;
            state.LastViewportKey = null;
            Interlocked.Increment(ref shownCopiesDropped);
        }
    }

    // A copy from BeginPreviewReadback(show: true) that is not shown, with its part of the reservation and, with a
    // pool, its drawing target.
    internal static void DisposeShownCopy(ID2D1CommandList shown, PreviewViewport viewport, ReadbackPool? pool = null, PooledTarget? target = null)
    {
        shown.Dispose();
        Interlocked.Add(ref gpuBytes, -viewport.FrameBytes);
        if (target is not null && pool?.Return(target, viewport) != true) target.Dispose();
    }

    // Tests only: the player's Draw prefix with `viewport` as its view.
    internal static void ObserveDrawView(object source, PreviewViewport viewport)
    {
        if (sources.TryGetValue(source, out var state)) ShowHostOutputIfViewChanged(source, state, viewport);
    }

    // On the source's render thread. The frame is stored only if its capture is still current: an edit, purge or
    // file change in between drops it. Oldest first; the GPU runs the copies in order, so the first one still busy
    // ends the poll. At most `limit` frames are finished per call (each allocates and copies a frame on the render
    // thread); waitForGpu finishes all.
    private static void CompleteDeferred(SourceState state, bool waitForGpu = false, int limit = 1)
    {
        for (int finished = 0; waitForGpu || finished < limit; finished++)
            if (!CompleteOldestDeferred(state, waitForGpu)) return;
    }

    // False when nothing was pending or the oldest readback is still running.
    private static bool CompleteOldestDeferred(SourceState state, bool waitForGpu)
    {
        DeferredStore deferred;
        lock (cacheGate)
        {
            if (state.Deferred.First is not { } oldest) return false;
            deferred = oldest.Value;
            state.Deferred.RemoveFirst(); // exclusively owned here: purge must not dispose a mapped texture
        }
        bool retained = false, busy = false;
        using var trace = CacheTrace.Measure("deferred-store", frameTimeTicks: deferred.Pending.Time.Ticks,
            usage: deferred.Pending.UsageKey, operation: deferred.Pending.TraceOperation);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (cacheGate) if (!StillCurrent(deferred.Pending)) return true;
            byte[]? record;
            if (waitForGpu) record = FinishPreviewReadback(deferred.Readback);
            else if (!TryFinishPreviewReadback(deferred.Readback, out record))
            {
                busy = true;
                lock (cacheGate) if (StillCurrent(deferred.Pending) && !state.IsDisposed)
                {
                    state.Deferred.AddFirst(deferred);
                    retained = true;
                }
                if (trace is not null) trace.Outcome = retained ? "gpu-busy" : "cancelled";
                return false;
            }
            if (!StillCurrent(deferred.Pending, files: true)) return true; // file I/O, outside cacheGate
            bool preview = deferred.Readback.SceneOrigin is null;
            lock (cacheGate) if (StillCurrent(deferred.Pending))
            {
                if (preview && !deferred.Pending.State.Economics.ShouldAdmit(deferred.Pending.CacheKey!, record!.LongLength, gpuRetentionEnabled)) return true;
                if (deferred.Pending.Publication.Owner?.PutOwned(deferred.Pending.CacheKey!, record!, deferred.Pending.Publication) != true) return true;
                // Only a verified, still displayed copy can transfer its reservation and immutable target.
                // Copies already released by another update keep their RAM record without GPU retention.
                if (preview && state.ShownTarget is { } target && state.ActiveGpuFrame is null
                    && state.LastOutput is { NativePointer: not 0 } shown && state.Bytes == deferred.Pending.Viewport?.FrameBytes
                    && state.LastViewportKey == deferred.Pending.CacheKey)
                    RetainUploaded(deferred.Pending, shown, state.Bytes, target, state.ShownViewport);
                if (preview) Interlocked.Increment(ref previewStored);
                status = preview ? "描画したプレビューのフレームを保存しました。" : "描画したフレームを保存しました。";
            }
            return true;
        }
        catch (Exception error)
        {
            if (IsDeviceLoss(error)) deferred.Readback.Failed();
            ObserveDeviceLoss(state, error);
            status = "プレビューのフレームの保存に失敗しました: " + error.GetType().Name;
            return !busy;
        }
        finally
        {
            if (!retained) deferred.Dispose();
            if (deferred.Readback.SceneOrigin is null) Interlocked.Add(ref previewStoreTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    // Tests only (render thread): finishes the pending preview readback of a source.
    internal static void CompletePendingStore(object source)
    {
        if (sources.TryGetValue(source, out var state)) CompleteDeferred(state, waitForGpu: true);
    }
}
