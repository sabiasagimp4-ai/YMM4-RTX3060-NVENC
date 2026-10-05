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

// Frames kept on the GPU after an upload, and replacing a source's output with a restored frame.
internal static partial class TimelineFrameCache
{
    // Cache owner and active-output borrower share one accounted allocation. Each native wrapper
    // owns a distinct COM reference; eviction never disposes the source's wrapper.
    private sealed class GpuFrame(SourceState owner, string key, ID2D1DeviceContext context,
        ID2D1CommandList command, long bytes, long generation, PooledTarget? target = null,
        PreviewViewport? viewport = null)
    {
        internal readonly WeakReference<SourceState> Owner = new(owner);
        internal readonly string Key = key;
        internal readonly ID2D1DeviceContext Context = context;
        internal readonly ID2D1CommandList Command = command;
        internal readonly long Bytes = bytes, Generation = generation;
        internal LinkedListNode<GpuFrame>? Node;
        internal int Owners = 2; // retention + currently displayed output
        internal bool RecycleTarget = true;
        internal void Release()
        {
            if (--Owners != 0) return;
            try { Command.Dispose(); }
            finally
            {
                Interlocked.Add(ref gpuBytes, -Bytes);
                if (target is not null && (!RecycleTarget || Context.NativePointer == 0 || target.Bitmap.NativePointer == 0 || viewport is not { } view
                    || !Owner.TryGetTarget(out var state) || state.IsDisposed || !state.Pool.Return(target, view))) target.Dispose();
            }
        }
    }

    // All retention operations run under cacheGate. Global LRU is bounded by bytes and entry count;
    // weak owners do not keep a disposed/unreachable TimelineSource alive.
    private static void RemoveGpuFrame(GpuFrame frame)
    {
        if (frame.Node is null) return;
        gpuLru.Remove(frame.Node); frame.Node = null;
        if (frame.Owner.TryGetTarget(out var owner)) owner.GpuFrames.Remove(frame.Key);
        gpuRetainedBytes -= frame.Bytes;
        frame.Release();
    }
    private static void ClearGpuFrames()
    {
        while (gpuLru.First is { } node) RemoveGpuFrame(node.Value);
    }
    // Cheap when within budget: the render path calls it on every Update to apply a budget lowered elsewhere.
    private static void TrimGpuFrames(long incoming)
    {
        int limit = GpuMemoryPolicy.EntryLimit(gpuRetentionBudget);
        while (gpuLru.First is { } node && (gpuRetainedBytes + incoming > gpuRetentionBudget
            || gpuLru.Count + (incoming > 0 ? 1 : 0) > limit)) RemoveGpuFrame(node.Value);
    }
    private static void RetainUploaded(Pending pending, ID2D1CommandList command, long bytes,
        PooledTarget? target = null, PreviewViewport? viewport = null)
    {
        if (!gpuRetentionEnabled || bytes > gpuRetentionBudget || pending.CacheKey is null) return;
        // Check every LRU victim required to make room. A one-use scan cannot displace equally
        // frequent residents; repeated requests can. Count aging lets a new working set take over.
        long remainingBytes = gpuRetainedBytes;
        int remainingCount = gpuLru.Count, limit = GpuMemoryPolicy.EntryLimit(gpuRetentionBudget);
        int frequency = pending.State.GpuAdmission.Frequency(pending.CacheKey);
        for (var node = gpuLru.First; node is not null && (remainingBytes + bytes > gpuRetentionBudget || remainingCount >= limit); node = node.Next)
        {
            int residentFrequency = node.Value.Owner.TryGetTarget(out var owner) ? owner.GpuAdmission.Frequency(node.Value.Key) : 0;
            if (frequency <= residentFrequency) return;
            remainingBytes -= node.Value.Bytes; remainingCount--;
        }
        ID2D1CommandList? retained = null;
        try
        {
            retained = command.QueryInterface<ID2D1CommandList>();
            if (pending.State.GpuFrames.TryGetValue(pending.CacheKey, out var previous)) RemoveGpuFrame(previous);
            TrimGpuFrames(bytes);
            var frame = new GpuFrame(pending.State, pending.CacheKey, pending.Devices.DeviceContext, retained, bytes, pending.Generation, target, viewport);
            frame.Node = gpuLru.AddLast(frame);
            pending.State.GpuFrames.Add(frame.Key, frame);
            gpuRetainedBytes += bytes;
            pending.State.Bytes = 0; // transfer the existing reservation; do not double-count aliases
            pending.State.ActiveGpuFrame = frame;
            if (target is not null) pending.State.ShownTarget = null;
            retained = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            ObserveDeviceLoss(pending.State, error);
            using var failure = CacheTrace.Measure("gpu-retention", "state");
            if (failure is not null) { failure.Outcome = "failed"; failure.Detail = error.GetType().Name; }
        }
        finally { retained?.Dispose(); }
    }
    private static bool TryRestoreGpu(object source, Pending pending)
    {
        using var restore = CacheTrace.Measure("gpu-resident-restore");
        if (restore is not null) restore.Outcome = "miss";
        lock (cacheGate)
        {
            if (!gpuRetentionEnabled || pending.CacheKey is null || !pending.State.GpuFrames.TryGetValue(pending.CacheKey, out var frame)) return false;
            if (frame.Generation != pending.Generation || !ReferenceEquals(frame.Context, pending.Devices.DeviceContext)
                || frame.Context.NativePointer == 0 || !StillCurrent(pending)) { RemoveGpuFrame(frame); return false; }
            ID2D1CommandList? borrowed = null;
            bool transferred = false;
            try
            {
                using (CacheTrace.Measure("gpu-command-borrow")) borrowed = frame.Command.QueryInterface<ID2D1CommandList>();
                frame.Owners++;
                if (!CommitReplacement(source, pending, borrowed, 0, frame)) return false;
                transferred = true;
                if (restore is not null) restore.Outcome = "hit";
                gpuLru.Remove(frame.Node!); gpuLru.AddLast(frame.Node!);
                return true;
            }
            finally
            {
                if (!transferred && borrowed is not null) { borrowed.Dispose(); frame.Release(); }
            }
        }
    }

    // One cached RAM frame per playing update, on the context's owning thread. A peek cannot validate
    // files, so this only warms storage: serving it still requires TryRestoreGpu's validated capture.
    private static void WarmNextGpuFrame(SourceState state, IGraphicsDevicesAndContext devices,
        IEnumerable<string?> keys, PreviewViewport view, int fps)
    {
        long bytes = view.FrameBytes;
        if (!gpuRetentionEnabled || state.DeviceLost || state.RecentUpdateTicks <= 0
            || state.RecentUpdateTicks > System.Diagnostics.Stopwatch.Frequency / Math.Max(1, fps) / 2) return;
        long currentGeneration = Interlocked.Read(ref generation), revision = state.Tracker.Revision;
        foreach (var key in keys.Take(4))
        {
            if (key is null) continue;
            lock (cacheGate)
                if (state.GpuFrames.ContainsKey(key)) continue;
                else if (gpuRetainedBytes + bytes > gpuRetentionBudget
                    || gpuLru.Count >= GpuMemoryPolicy.EntryLimit(gpuRetentionBudget)) return;
            if (!store.Value.TryGetCached(key, out var record) || !ParseRecord(record.Span, out int width,
                out int height, out _, out int version, out float dpiX, out float dpiY)
                || version != 2 || width != view.Width || height != view.Height || dpiX != view.DpiX || dpiY != view.DpiY) continue;
            if (!Reserve(bytes)) return;
            ID2D1CommandList? command = null;
            bool transferred = false;
            try
            {
                using var trace = CacheTrace.Measure("gpu-read-ahead");
                command = UploadPreview(devices.DeviceContext, record.Span, view);
                lock (cacheGate)
                {
                    if (!Enabled || state.DeviceLost || state.IsDisposed || state.Tracker.Revision != revision
                        || generation != currentGeneration || state.GpuFrames.ContainsKey(key)
                        || gpuRetainedBytes + bytes > gpuRetentionBudget
                        || gpuLru.Count >= GpuMemoryPolicy.EntryLimit(gpuRetentionBudget)) return;
                    var frame = new GpuFrame(state, key, devices.DeviceContext, command, bytes, currentGeneration) { Owners = 1 };
                    frame.Node = gpuLru.AddLast(frame); state.GpuFrames.Add(key, frame);
                    gpuRetainedBytes += bytes; transferred = true;
                    Interlocked.Increment(ref gpuReadAheads);
                }
            }
            finally { if (!transferred) { command?.Dispose(); Interlocked.Add(ref gpuBytes, -bytes); } }
            return;
        }
    }

    private static bool IsDeviceLoss(Exception error) => unchecked((uint)error.HResult) is
        0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020 or 0x8899000C;

    // Native output references remain with the host until its normal update/disposal. Lost targets
    // never return to a drawing pool; a new source/context is required before caching resumes.
    private static void ObserveDeviceLoss(SourceState? state, Exception? error)
    {
        if (state is null || error is null || !IsDeviceLoss(error)) return;
        lock (cacheGate)
        {
            state.DeviceLost = true; state.LastKey = null; state.LastViewportKey = null;
            foreach (var frame in gpuLru) frame.RecycleTarget = false;
            ClearGpuFrames(); state.Pool.Reset();
            foreach (var deferred in state.Deferred) deferred.Readback.Failed();
            foreach (var deferred in state.Deferred) deferred.Dispose();
            state.Deferred.Clear();
            if (state.ShownTarget is { } target) { state.ShownTarget = null; target.Dispose(); }
            status = "描画デバイスが失われたため、描画器が作り直されるまで通常描画を使用します。";
        }
    }

    internal static void NotifyDeviceLossForTests(object source, Exception error)
    { if (sources.TryGetValue(source, out var state)) ObserveDeviceLoss(state, error); }
    private static bool CommitReplacement(object source, Pending pending, ID2D1CommandList replacement, long bytes, GpuFrame? borrowed)
    {
        if (!StillCurrent(pending) || !sources.TryGetValue(source, out var currentState)
            || !ReferenceEquals(currentState, pending.State)
            || !ReferenceEquals(outputField.GetValue(source), pending.PreviousOutput)) return false;
        using var swap = CacheTrace.Measure("cache-output-commit");
        var collector = (DisposeCollector)collectorField.GetValue(source)!;
        var previous = (ID2D1CommandList?)outputField.GetValue(source);
        collector.Collect(replacement);
        outputField.SetValue(source, replacement);
        if (previous != null) { collector.Remove(previous); previous.Dispose(); }
        // No host update runs: the host's output kept behind a shown copy (ShowRendered) is released here.
        if (pending.State.HostOutput is { NativePointer: not 0 } host && !ReferenceEquals(host, previous)) { collector.Remove(host); host.Dispose(); }
        pending.State.Released();
        pending.State.LastOutput = replacement;
        pending.State.LastKey = pending.LiveKey;
        pending.State.LastViewportKey = pending.Viewport is null ? null : pending.CacheKey;
        pending.State.Bytes = bytes;
        pending.State.ActiveGpuFrame = borrowed;
        pending.State.Generation = pending.Generation;
        return true;
    }

    private static bool TryReplaceFrame(object source, Pending pending, ReadOnlyMemory<byte> record)
    {
        if (!StillCurrent(pending) || !ParseRecord(record.Span, out int width, out int height, out var origin, out int version, out float dpiX, out float dpiY))
            return false;
        bool exporting = pending.UsageKey == "Exporting";
        if (exporting && version != 1) return false;
        if (!exporting)
        {
            if (version != 2 || pending.Viewport is not PreviewViewport viewport || viewport.Width != width || viewport.Height != height
                || BitConverter.SingleToInt32Bits(viewport.DpiX) != BitConverter.SingleToInt32Bits(dpiX)
                || BitConverter.SingleToInt32Bits(viewport.DpiY) != BitConverter.SingleToInt32Bits(dpiY)) return false;
        }
        long bytes = (long)width * height * 4;
        if (!Reserve(bytes)) return false;
        ID2D1CommandList? replacement = null;
        bool transferred = false;
        try
        {
            var context = pending.Devices.DeviceContext;
            replacement = version == 1 ? Upload(context, record.Span) : UploadPreview(context, record.Span, pending.Viewport!.Value);
            var gateWait = CacheTrace.Measure("cache-output-lock-wait");
            try
            {
                lock (cacheGate)
                {
                    gateWait?.Dispose();
                    if (!CommitReplacement(source, pending, replacement, bytes, null)) return false;
                    transferred = true;
                    if (version == 2) RetainUploaded(pending, replacement, bytes);
                    return true;
                }
            }
            finally { gateWait?.Dispose(); }
        }
        finally
        {
            if (!transferred) { replacement?.Dispose(); Interlocked.Add(ref gpuBytes, -bytes); }
        }
    }
}
