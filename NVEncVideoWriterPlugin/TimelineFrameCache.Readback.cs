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

// Pixels between the GPU and the store: drawing a frame for a view or the scene and reading it back, uploading a
// stored record again, the textures reused for that, and the GPU memory budget.
internal static partial class TimelineFrameCache
{
    private static byte[]? CaptureScene(ID2D1DeviceContext context, ID2D1Image output, Scene scene)
    {
        var bounds = context.GetImageLocalBounds(output);
        var half = new Vector2(scene.Width / 2f, scene.Height / 2f);
        var left = MathF.Floor(bounds.Left + half.X) - half.X;
        var top = MathF.Floor(bounds.Top + half.Y) - half.Y;
        var right = MathF.Ceiling(bounds.Right + half.X) - half.X;
        var bottom = MathF.Ceiling(bounds.Bottom + half.Y) - half.Y;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom)
            || right <= left || bottom <= top || right - left > context.MaximumBitmapSize || bottom - top > context.MaximumBitmapSize)
            return null;
        return Capture(context, output, checked((int)(right - left)), checked((int)(bottom - top)), new Vector2(left, top));
    }

    // Includes all image bounds, not just the scene rectangle. Origin matches the caller's pixel phase.
    internal static byte[]? Capture(ID2D1DeviceContext context, ID2D1Image output, int width, int height, Vector2 origin)
    {
        long bytes = checked((long)width * height * 4);
        if (width <= 0 || height <= 0 || bytes > FrameCacheStore.MaxFrameBytes - RecordHeader || !Reserve(bytes * 2)) return null;
        ContextScope? scope = null;
        try
        {
            scope = new ContextScope(context);
            using var target = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            using var readable = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                target.PixelFormat, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            context.Target = target;
            context.Transform = Matrix3x2.Identity;
            scope.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            context.DrawImage(output, -origin);
            scope.EndDraw();
            context.Target = null;
            readable.CopyFromBitmap(target).CheckError();
            var record = GC.AllocateUninitializedArray<byte>(checked((int)bytes + RecordHeader)); // fully written below
            "YMPX"u8.CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), height);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), origin.X);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), origin.Y);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 1);
            var mapped = readable.Map(MapOptions.Read);
            try
            {
                for (var row = 0; row < height; row++)
                    Marshal.Copy(mapped.Bits + row * mapped.Pitch, record, RecordHeader + row * width * 4, width * 4);
            }
            finally { readable.Unmap(); }
            return record;
        }
        finally
        {
            try { scope?.Dispose(); }
            finally { Interlocked.Add(ref gpuBytes, -bytes * 2); }
        }
    }

    internal static byte[]? CapturePreview(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport, ReadbackPool? pool = null)
    {
        using var readback = BeginPreviewReadback(context, output, viewport, false, pool, out _, out _);
        return readback is null ? null : FinishPreviewReadback(readback);
    }

    // A CPU-readable copy of a preview frame that the GPU may still be producing. Holds its GPU reservation; its staging
    // texture goes back to `pool`, if any, when it is disposed.
    internal sealed class PreviewReadback(ID3D11Texture2D readable, ID3D11DeviceContext immediate, PreviewViewport viewport, long bytes,
        ReadbackPool? pool = null) : IDisposable
    {
        private int disposed;
        internal readonly ID3D11Texture2D Readable = readable;
        internal readonly ID3D11DeviceContext Immediate = immediate;
        internal readonly PreviewViewport Viewport = viewport;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { if (pool?.Return(Readable, Viewport) != true) Readable.Dispose(); }
            finally { Immediate.Dispose(); Interlocked.Add(ref gpuBytes, -bytes); }
        }
    }

    // Draws the frame as the player would and queues its copy to a CPU-readable bitmap, without waiting for the GPU.
    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport)
        => BeginPreviewReadback(context, output, viewport, false, null, out _, out _);

    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport,
        bool show, out ID2D1CommandList? shown) => BeginPreviewReadback(context, output, viewport, show, null, out shown, out _);

    // show: also record `shown`, a command list that draws the drawn pixels for the view (as UploadPreview does), so
    // that the frame need not be drawn again. It holds the drawing target and `bytes` of the GPU reservation, both
    // the caller's from then on. Null when show is false or recording failed (the readback is still returned).
    // pool: the drawing target and staging texture come from it and go back to it (the target only once nothing
    // draws it: with `shown`, `shownTarget` is the caller's to return).
    internal static PreviewReadback? BeginPreviewReadback(ID2D1DeviceContext context, ID2D1Image output, PreviewViewport viewport,
        bool show, ReadbackPool? pool, out ID2D1CommandList? shown, out PooledTarget? shownTarget)
    {
        shown = null;
        shownTarget = null;
        using var measurement = PreviewPerformance.Measure(PreviewStage.BeginGpuCopy);
        long bytes = viewport.FrameBytes;
        if (!IsValidViewport(viewport, context.MaximumBitmapSize) || bytes > FrameCacheStore.MaxFrameBytes - PreviewRecordHeader
            || !Reserve(bytes * 2)) return null;
        ContextScope? scope = null;
        ID2D1Bitmap1? target = null;
        PooledTarget? pooled = null;
        ID3D11Texture2D? readable = null;
        ID3D11DeviceContext? immediate = null;
        var returned = false;
        try
        {
            scope = new ContextScope(context);
            target = TargetFor(context, viewport, pool, out pooled);
            context.Target = target;
            context.Transform = viewport.Transform;
            ApplyModes(context, viewport);
            scope.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 1));
            context.DrawImage(output, viewport.TargetOffset);
            scope.EndDraw();
            context.Target = null;
            if (!QueueStagingCopy(target, viewport, pool, ref readable, ref immediate)) return null;
            if (show)
            {
                // The target is not drawn to again while the copy is shown: like UploadPreview's bitmap, an unchanging
                // image (pooled, it is drawn to only after the copy is gone).
                try { shown = pooled is not null ? pooled.Show(context, viewport) : RecordPreviewImage(context, target, viewport); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { shown = null; }
            }
            var result = new PreviewReadback(readable, immediate, viewport, bytes, pool);
            readable = null;
            immediate = null;
            returned = true;
            return result;
        }
        finally
        {
            try { scope?.Dispose(); }
            finally
            {
                if (!returned) { shown?.Dispose(); shown = null; }
                // A returned readback keeps the staging half until it is disposed, a shown copy the target half.
                Interlocked.Add(ref gpuBytes, returned ? (shown is null ? -bytes : 0) : -bytes * 2);
                if (shown is not null && pooled is not null) shownTarget = pooled;
                // Copies queued from the target run before anything drawn to it next: it can go back at once.
                else if (pooled is not null) { if (!pool!.Return(pooled, viewport)) pooled.Dispose(); }
                else target?.Dispose();
                if (readable is not null && pool?.Return(readable, viewport) != true) readable.Dispose();
                immediate?.Dispose();
            }
        }
    }

    // The view-sized drawing target: the pool's next one, or a new one (with a pool, in an entry for returning it).
    private static ID2D1Bitmap1 TargetFor(ID2D1DeviceContext context, PreviewViewport viewport, ReadbackPool? pool, out PooledTarget? pooled)
    {
        pooled = pool?.TakeTarget(viewport);
        if (pooled is not null) return pooled.Bitmap;
        var target = context.CreateBitmap(new SizeI(viewport.Width, viewport.Height), new BitmapProperties1(
            viewport.BackBufferFormat, viewport.DpiX, viewport.DpiY, BitmapOptions.Target));
        Interlocked.Increment(ref readbackTexturesCreated);
        if (pool is not null) pooled = new PooledTarget(target);
        return target;
    }

    // Queues a copy of the drawn target to a CPU-readable staging texture, on the target's own device (never a guessed
    // host or global one). D2D's CPU-read Map has no DO_NOT_WAIT flag; the staging texture keeps GPU waits off
    // playback. False when the target is not one view-sized texture. The textures are the caller's as soon as they
    // are assigned.
    private static bool QueueStagingCopy(ID2D1Bitmap1 target, PreviewViewport viewport, ReadbackPool? pool,
        ref ID3D11Texture2D? readable, ref ID3D11DeviceContext? immediate)
    {
        using var surface = target.Surface;
        using var texture = surface.QueryInterface<ID3D11Texture2D>();
        using var device = texture.Device;
        var description = texture.Description;
        if (description.Width != viewport.Width || description.Height != viewport.Height
            || description.MipLevels != 1 || description.ArraySize != 1 || description.SampleDescription.Count != 1
            || description.Format != viewport.BackBufferFormat.Format) return false;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        readable = pool?.TakeStaging(viewport);
        if (readable is null)
        {
            readable = device.CreateTexture2D(description);
            Interlocked.Increment(ref readbackTexturesCreated);
        }
        immediate = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        immediate.CopyResource(readable, texture);
        immediate.Flush(); // submit the copy, including paused frames; does not wait for completion
        return true;
    }

    // Blocking completion is reserved for explicit capture and test helpers, never live cache updates.
    internal static byte[] FinishPreviewReadback(PreviewReadback readback)
    {
        ReadPreviewReadback(readback, MapFlags.None, out var record);
        return record!;
    }

    internal static bool TryFinishPreviewReadback(PreviewReadback readback, out byte[]? record)
        => ReadPreviewReadback(readback, MapFlags.DoNotWait, out record);

    private static bool ReadPreviewReadback(PreviewReadback readback, MapFlags flags, out byte[]? record)
    {
        record = null;
        using var trace = CacheTrace.Measure("readback-poll", "gpu-copy");
        long mapStarted = PreviewPerformance.Timestamp;
        var result = readback.Immediate.Map(readback.Readable, 0, MapMode.Read, flags, out var mapped);
        PreviewPerformance.End(PreviewStage.MapWait, mapStarted);
        if (result.Code == unchecked((int)0x887A000A)) // DXGI_ERROR_WAS_STILL_DRAWING
        {
            if (trace is not null) trace.Outcome = "gpu-busy";
            return false;
        }
        result.CheckError(); // device loss/other failures invalidate the optional capture
        try
        {
            var viewport = readback.Viewport;
            if (mapped.DataPointer == IntPtr.Zero || mapped.RowPitch < checked(viewport.Width * 4))
                throw new InvalidDataException("Invalid preview staging layout");
            long allocationStarted = PreviewPerformance.Timestamp;
            // The header and every pixel row are written below; zeroing 8 MB first would only cost time.
            record = GC.AllocateUninitializedArray<byte>(checked(viewport.Width * viewport.Height * 4 + PreviewRecordHeader));
            PreviewPerformance.End(PreviewStage.CpuAllocation, allocationStarted);
            "YMPX"u8.CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), viewport.Width);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), viewport.Height);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), 0);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 2);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(24), viewport.DpiX);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(28), viewport.DpiY);
            using var measurement = PreviewPerformance.Measure(PreviewStage.CpuMemcpy);
            for (var row = 0; row < viewport.Height; row++)
                Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, record,
                    PreviewRecordHeader + row * viewport.Width * 4, viewport.Width * 4);
            if (trace is not null) trace.Outcome = "ready";
            return true;
        }
        finally { readback.Immediate.Unmap(readback.Readable, 0); }
    }

    internal static ID2D1CommandList Upload(ID2D1DeviceContext context, ReadOnlySpan<byte> record)
    {
        if (!ParseRecord(record, out int width, out int height, out var origin, out int version, out _, out _) || version != 1)
            throw new InvalidDataException("Invalid scene pixel record");
        ID2D1Bitmap1 allocated;
        using (CacheTrace.Measure("restore-bitmap-allocation"))
            allocated = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
        using var bitmap = allocated;
        using (CacheTrace.Measure("restore-copy-from-memory")) bitmap.CopyFromMemory(record[RecordHeader..], width * 4).CheckError();
        using var recording = CacheTrace.Measure("restore-command-recording");
        return RecordImage(context, bitmap, origin, context.Transform, null);
    }

    internal static ID2D1CommandList UploadPreview(ID2D1DeviceContext context, ReadOnlySpan<byte> record, PreviewViewport viewport)
    {
        if (!ParseRecord(record, out int width, out int height, out _, out int version, out float dpiX, out float dpiY) || version != 2
            || width != viewport.Width || height != viewport.Height
            || BitConverter.SingleToInt32Bits(dpiX) != BitConverter.SingleToInt32Bits(viewport.DpiX)
            || BitConverter.SingleToInt32Bits(dpiY) != BitConverter.SingleToInt32Bits(viewport.DpiY)
            || !IsValidViewport(viewport, context.MaximumBitmapSize)) throw new InvalidDataException("Invalid preview pixel record");
        ID2D1Bitmap1 allocated;
        using (CacheTrace.Measure("restore-bitmap-allocation"))
            allocated = context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(viewport.BackBufferFormat, dpiX, dpiY));
        using var bitmap = allocated;
        using (CacheTrace.Measure("restore-copy-from-memory")) bitmap.CopyFromMemory(record[PreviewRecordHeader..], width * 4).CheckError();
        using var recording = CacheTrace.Measure("restore-command-recording");
        return RecordPreviewImage(context, bitmap, viewport);
    }

    // A command list that draws `image`, the view's pixels (viewport size), 1:1 onto the player's target when the
    // player draws it with the view's transform and offset.
    private static ID2D1CommandList RecordPreviewImage(ID2D1DeviceContext context, ID2D1Image image, PreviewViewport viewport)
    {
        var offsetTransform = Matrix3x2.CreateTranslation(viewport.TargetOffset) * viewport.Transform;
        if (!Matrix3x2.Invert(offsetTransform, out var inverse)) throw new InvalidDataException("Preview transform is not invertible");
        return RecordImage(context, image, Vector2.Zero, inverse, viewport);
    }

    // A closed command list that draws `image` at `offset` (nearest neighbor, source over) under `transform`, with the
    // view's drawing modes when one is given.
    private static ID2D1CommandList RecordImage(ID2D1DeviceContext context, ID2D1Image image, Vector2 offset, Matrix3x2 transform,
        PreviewViewport? modes)
    {
        var command = context.CreateCommandList();
        try
        {
            using var scope = new ContextScope(context);
            context.Target = command;
            context.Transform = transform;
            if (modes is { } view) ApplyModes(context, view);
            scope.BeginDraw();
            context.DrawImage(image, offset, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            scope.EndDraw();
            context.Target = null;
            command.Close().CheckError();
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private static void ApplyModes(ID2D1DeviceContext context, PreviewViewport viewport)
    {
        context.AntialiasMode = viewport.AntialiasMode;
        context.TextAntialiasMode = viewport.TextAntialiasMode;
        context.PrimitiveBlend = viewport.PrimitiveBlend;
        context.UnitMode = viewport.UnitMode;
    }

    // Draws on a device context the host draws with as well: saves its target, transform and drawing modes, and on
    // Dispose ends a draw an exception left open and restores them (releasing the saved target's wrapper).
    private sealed class ContextScope : IDisposable
    {
        private readonly ID2D1DeviceContext context;
        private readonly ID2D1Image? target;
        private readonly Matrix3x2 transform;
        private readonly AntialiasMode antialias;
        private readonly TextAntialiasMode textAntialias;
        private readonly PrimitiveBlend primitiveBlend;
        private readonly UnitMode unitMode;
        private bool drawing;

        internal ContextScope(ID2D1DeviceContext context)
        {
            this.context = context;
            antialias = context.AntialiasMode;
            textAntialias = context.TextAntialiasMode;
            primitiveBlend = context.PrimitiveBlend;
            unitMode = context.UnitMode;
            transform = context.Transform;
            target = context.Target;
        }

        internal void BeginDraw()
        {
            context.BeginDraw();
            drawing = true;
        }

        internal void EndDraw()
        {
            context.EndDraw().CheckError();
            drawing = false;
        }

        public void Dispose()
        {
            try
            {
                if (drawing) { try { context.EndDraw(); } catch { } }
                context.Target = target;
                context.Transform = transform;
                context.AntialiasMode = antialias;
                context.TextAntialiasMode = textAntialias;
                context.PrimitiveBlend = primitiveBlend;
                context.UnitMode = unitMode;
            }
            finally { target?.Dispose(); }
        }
    }

    internal static bool ParseRecord(ReadOnlySpan<byte> record, out int width, out int height, out Vector2 origin)
        => ParseRecord(record, out width, out height, out origin, out _, out _, out _);

    internal static bool ParseRecord(ReadOnlySpan<byte> record, out int width, out int height, out Vector2 origin,
        out int version, out float dpiX, out float dpiY)
    {
        width = height = version = 0; origin = default; dpiX = dpiY = 96;
        if (record.Length < RecordHeader || !record[..4].SequenceEqual("YMPX"u8)) return false;
        width = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        origin = new(BinaryPrimitives.ReadSingleLittleEndian(record[12..]), BinaryPrimitives.ReadSingleLittleEndian(record[16..]));
        version = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
        int header = version switch { 1 => RecordHeader, 2 => PreviewRecordHeader, _ => 0 };
        if (header == 0 || record.Length < header) return false;
        if (version == 2)
        {
            dpiX = BinaryPrimitives.ReadSingleLittleEndian(record[24..]);
            dpiY = BinaryPrimitives.ReadSingleLittleEndian(record[28..]);
        }
        return width > 0 && height > 0 && float.IsFinite(origin.X) && float.IsFinite(origin.Y)
            && float.IsFinite(dpiX) && float.IsFinite(dpiY) && dpiX > 0 && dpiY > 0
            && (long)width * height * 4 == record.Length - header && record.Length <= FrameCacheStore.MaxFrameBytes;
    }
    // A source's view-sized drawing targets and staging textures, reused by its readbacks instead of creating two
    // textures per stored frame (8 MB each at 1080p: driver allocation, and page faults on the first Map). Pooled
    // textures stay reserved (readbackPoolBytes). A new view size or format, and the source's disposal, release them.
    internal sealed class ReadbackPool
    {
        // Targets rotate oldest first through at least two: the one shown last (its copy may still be drawn by the
        // GPU) is not drawn to again in the very next frame.
        private const int MaxTargets = 3, MinRotation = 2, MaxStagings = ReadbacksInFlight;
        private readonly object gate = new();
        private readonly Queue<PooledTarget> targets = new();
        private readonly Stack<ID3D11Texture2D> stagings = new();
        private (int Width, int Height, Format Format, Vortice.DCommon.AlphaMode Alpha, float DpiX, float DpiY) shape;
        private bool cleared;

        private static (int, int, Format, Vortice.DCommon.AlphaMode, float, float) ShapeOf(PreviewViewport viewport) =>
            (viewport.Width, viewport.Height, viewport.BackBufferFormat.Format, viewport.BackBufferFormat.AlphaMode, viewport.DpiX, viewport.DpiY);
        private long Bytes => (long)shape.Width * shape.Height * 4;

        internal PooledTarget? TakeTarget(PreviewViewport viewport)
        {
            lock (gate) return Fits(viewport) && targets.Count >= MinRotation && targets.TryDequeue(out var target) ? Taken(target) : null;
        }
        internal ID3D11Texture2D? TakeStaging(PreviewViewport viewport)
        {
            lock (gate) return Fits(viewport) && stagings.TryPop(out var staging) ? Taken(staging) : null;
        }
        // False when the texture is not kept (the caller disposes it).
        internal bool Return(PooledTarget target, PreviewViewport viewport)
        {
            lock (gate)
            {
                if (!Admit(viewport, targets.Count, MaxTargets)) return false;
                targets.Enqueue(target);
                return true;
            }
        }
        internal bool Return(ID3D11Texture2D staging, PreviewViewport viewport)
        {
            lock (gate)
            {
                if (!Admit(viewport, stagings.Count, MaxStagings)) return false;
                stagings.Push(staging);
                return true;
            }
        }
        internal void Clear()
        {
            lock (gate)
            {
                cleared = true;
                Drop();
            }
        }

        private T Taken<T>(T texture)
        {
            Interlocked.Add(ref readbackPoolBytes, -Bytes);
            return texture;
        }
        // Whether a texture of `viewport` is kept (counted as pooled); a new shape drops the others first.
        private bool Admit(PreviewViewport viewport, int count, int maximum)
        {
            if (cleared) return false;
            if (!Fits(viewport))
            {
                Drop();
                shape = ShapeOf(viewport);
                count = 0;
            }
            if (count >= maximum) return false;
            Interlocked.Add(ref readbackPoolBytes, Bytes);
            return true;
        }
        private bool Fits(PreviewViewport viewport) => shape == ShapeOf(viewport);
        private void Drop()
        {
            int count = targets.Count + stagings.Count;
            while (targets.TryDequeue(out var target)) target.Dispose();
            while (stagings.TryPop(out var staging)) staging.Dispose();
            if (count != 0) Interlocked.Add(ref readbackPoolBytes, -count * Bytes);
        }
    }

    // A pooled drawing target and the command list that shows it 1:1 for a view (RecordPreviewImage), recorded once
    // and shown again whenever the target is reused for that view: the copy draws whatever the target holds now.
    internal sealed class PooledTarget(ID2D1Bitmap1 bitmap) : IDisposable
    {
        internal ID2D1Bitmap1 Bitmap { get; } = bitmap;
        private ID2D1CommandList? display;
        private PreviewViewport displayView;

        // A new reference to the target's copy for `viewport` (the caller owns it), recorded only for a new view.
        internal ID2D1CommandList Show(ID2D1DeviceContext context, PreviewViewport viewport)
        {
            var view = viewport.Normalized;
            if (display is not { NativePointer: not 0 } recorded || displayView != view)
            {
                display?.Dispose();
                display = null;
                recorded = RecordPreviewImage(context, Bitmap, viewport);
                display = recorded;
                displayView = view;
            }
            return recorded.QueryInterface<ID2D1CommandList>();
        }

        public void Dispose()
        {
            try { display?.Dispose(); }
            finally { Bitmap.Dispose(); }
        }
    }

    private static bool Reserve(long bytes)
    {
        while (true)
        {
            long current = Interlocked.Read(ref gpuBytes);
            if (bytes <= 0 || bytes > GpuBudget - current - Interlocked.Read(ref readbackPoolBytes)) return false;
            if (Interlocked.CompareExchange(ref gpuBytes, current + bytes, current) == current) return true;
        }
    }
}
