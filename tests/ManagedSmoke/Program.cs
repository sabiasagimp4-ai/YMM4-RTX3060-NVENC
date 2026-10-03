using System.IO;
using System.Reflection;
using NVEncVideoWriterPlugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Plugin.FileWriter;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

var path = Path.Combine(Path.GetTempPath(), $"ymm4-rtx3060-smoke-{Guid.NewGuid():N}.mp4");
var videoInfo = new VideoInfo { Width = 320, Height = 180, FPS = 30, Hz = 48000 };
var plugin = new NvencVideoFileWriterPlugin();

// Exercise the real writer's dispatcher without a GPU: the owner must remain MTA,
// propagate exceptions synchronously, and terminate even after Dispose fails.
var threadWriter = plugin.CreateVideoFileWriter(path, videoInfo);
var dispatcher = threadWriter.GetType().GetField("_encoderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(threadWriter)!;
var ownerThread = (Thread)dispatcher.GetType().GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;
var invoke = dispatcher.GetType().GetMethod("Invoke")!;
Task.WaitAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
{
    invoke.Invoke(dispatcher, [new Action(() =>
    {
        if (Thread.CurrentThread != ownerThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            throw new Exception("Encoder operations changed their owning MTA thread.");
    })]);
})).ToArray());
try
{
    invoke.Invoke(dispatcher, [new Action(() => throw new IOException("dispatcher exception proof"))]);
    throw new Exception("Dispatcher swallowed an exception.");
}
catch (TargetInvocationException error) when (error.InnerException is IOException { Message: "dispatcher exception proof" }) { }
Task.Run(() => threadWriter.WriteAudio([0f, 0f])).GetAwaiter().GetResult();
try { Task.Run(threadWriter.Dispose).GetAwaiter().GetResult(); throw new Exception("Audio-only output accepted."); }
catch (InvalidOperationException) { }
if (ownerThread.IsAlive) throw new Exception("Encoder thread leaked after failed Dispose.");
threadWriter.Dispose();
Console.WriteLine("Dedicated MTA thread, exception propagation and failure cleanup OK");

using (var writer = plugin.CreateVideoFileWriter(path, videoInfo))
{
    if (writer is not IVideoFileWriter3 { IsGpuFrameSupported: true })
        throw new Exception("GPU support was not advertised through the current host interface.");
    try
    {
#pragma warning disable CS0618 // Deliberately exercise the legacy host fallback.
        writer.WriteVideo(Array.Empty<byte>());
#pragma warning restore CS0618
        throw new Exception("CPU frame fallback was silently accepted.");
    }
    catch (NotSupportedException) { }
}

using (var writer = plugin.CreateVideoFileWriter(path, videoInfo))
{
    writer.WriteAudio([0f, 0f]);
    try
    {
        writer.Dispose();
        throw new Exception("Audio without video was silently discarded.");
    }
    catch (InvalidOperationException) { }
}

if (File.Exists(path))
    throw new Exception("Failed export replaced the final MP4 path.");
Console.WriteLine("Managed failure paths OK");

using (var cancellation = new CancellationTokenSource())
{
    var scope = plugin.GetType().Assembly.GetType("NVEncVideoWriterPlugin.HostExportScope", true)!;
    var snapshotType = scope.GetNestedType("Snapshot", BindingFlags.NonPublic)!;
    var snapshot = Activator.CreateInstance(snapshotType, cancellation.Token, 1)!;
    var previous = scope.GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [snapshot]);
    IVideoFileWriter cancelledWriter;
    try { cancelledWriter = plugin.CreateVideoFileWriter(path, videoInfo); }
    finally { scope.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [previous]); }
    using (cancelledWriter)
    {
        cancellation.Cancel();
        try { cancelledWriter.WriteAudio([0f, 0f]); throw new Exception("Cancelled writer accepted audio."); }
        catch (OperationCanceledException) { }
    }
    if (File.Exists(path)) throw new Exception("Cancelled writer published an output.");
}
Console.WriteLine("Cancellation stops pending writer calls before encoding");

var existingSpools = Directory.GetFiles(Path.GetTempPath(), "ymm4-nvenc-audio-*.tmp").ToHashSet();
using (var writer = plugin.CreateVideoFileWriter(path, videoInfo))
{
    var chunk = new float[262144];
    var before = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < 32; ++i) writer.WriteAudio(chunk);
    // Include allocations on the encoder thread; measuring only the caller hides them.
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    if (allocated > 2 * 1024 * 1024)
        throw new Exception($"Pending audio retained project-sized RAM: {allocated} bytes.");
    try { writer.Dispose(); throw new Exception("Audio-only output was accepted."); }
    catch (InvalidOperationException) { }
    try { writer.WriteAudio(chunk); throw new Exception("Disposed writer was reused."); }
    catch (ObjectDisposedException) { }
    Console.WriteLine($"32 MiB pending audio: managed allocations={allocated} bytes");
}
if (Directory.GetFiles(Path.GetTempPath(), "ymm4-nvenc-audio-*.tmp").Any(p => !existingSpools.Contains(p)))
    throw new Exception("Pending audio temporary file leaked.");

if (args.Length == 1)
{
    D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
        [Vortice.Direct3D.FeatureLevel.Level_11_0], out var device, out var immediate).CheckError();
    using (device)
    using (immediate)
    using (var dxgi = device.QueryInterface<IDXGIDevice>())
    using (var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>())
    using (var d2d = factory.CreateDevice(dxgi))
    using (var context = d2d.CreateDeviceContext(DeviceContextOptions.None))
    using (var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
        320, 180, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource)))
    using (var target = device.CreateRenderTargetView(texture))
    using (var surface = texture.QueryInterface<IDXGISurface>())
    using (var bitmap = context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore))))
    {
        var audio = new float[96000];
        for (var i = 0; i < audio.Length / 2; ++i)
            audio[i * 2] = audio[i * 2 + 1] = (float)(0.1 * Math.Sin(i * 440 * 2 * Math.PI / 48000));
        var destination = Path.Combine(args[0], "managed-audio-first.mp4");
        using (var writer = plugin.CreateVideoFileWriter(destination, videoInfo))
        {
            writer.WriteAudio(audio);
            Array.Fill(audio, 0f); // WriteAudio must already own the sample bytes.
            for (var frame = 0; frame < 30; ++frame)
            {
                immediate.ClearRenderTargetView(target, new Color4(frame / 30f, 0.25f, 0.5f, 1));
                ((IVideoFileWriter2)writer).WriteVideo(bitmap);
            }
        }
        if (!File.Exists(destination)) throw new Exception("Audio-first output was not published.");
        Console.WriteLine($"Managed audio-first GPU output OK: {destination}");

        var threadedDestination = Path.Combine(args[0], "managed-threaded.mp4");
        var threadedWriter = plugin.CreateVideoFileWriter(threadedDestination, videoInfo);
        var threadedDispatcher = threadedWriter.GetType().GetField("_encoderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(threadedWriter)!;
        var threadedOwner = (Thread)threadedDispatcher.GetType().GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(threadedDispatcher)!;
        try
        {
            var frameAudio = new float[3200];
            for (var frame = 0; frame < 30; ++frame)
            {
                immediate.ClearRenderTargetView(target, new Color4(frame / 30f, 0.25f, 0.5f, 1));
                // Match YMM4: each frame sends audio/video through separate Task.Run calls.
                Task.WaitAll(Task.Run(() => threadedWriter.WriteAudio(frameAudio)),
                    Task.Run(() => ((IVideoFileWriter2)threadedWriter).WriteVideo(bitmap)));
                Array.Fill(frameAudio, 0f); // Both borrowed inputs may be reused immediately.
            }
            using var race = new Barrier(3);
            var audioRace = Task.Run(() =>
            {
                race.SignalAndWait();
                try { threadedWriter.WriteAudio([0f, 0f]); }
                catch (ObjectDisposedException) { }
            });
            var disposeRace = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                race.SignalAndWait();
                threadedWriter.Dispose();
            })).ToArray();
            Task.WaitAll([audioRace, .. disposeRace]);
            if (threadedOwner.IsAlive) throw new Exception("Encoder thread leaked after concurrent Dispose.");
            if (!File.Exists(threadedDestination)) throw new Exception("Threaded GPU output was not published.");
            Console.WriteLine($"Concurrent host audio/video and Dispose OK: {threadedDestination}");
        }
        finally { threadedWriter.Dispose(); }

        foreach (var (acceptedFrames, expectedFrames, cancel) in new[] { (2, 2, true), (1, 2, false), (2, 2, false) })
        {
            var guardedPath = Path.Combine(Path.GetTempPath(), $"ymm4-cancel-protected-{Guid.NewGuid():N}.mp4");
            try
            {
                const string sentinel = "previous complete output";
                File.WriteAllText(guardedPath, sentinel);
                using var cancellation = new CancellationTokenSource();
                var scope = plugin.GetType().Assembly.GetType("NVEncVideoWriterPlugin.HostExportScope", true)!;
                var snapshotType = scope.GetNestedType("Snapshot", BindingFlags.NonPublic)!;
                var snapshot = Activator.CreateInstance(snapshotType, cancellation.Token, expectedFrames)!;
                var previous = scope.GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [snapshot]);
                IVideoFileWriter guardedWriter;
                try { guardedWriter = plugin.CreateVideoFileWriter(guardedPath, videoInfo); }
                finally { scope.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [previous]); }
                using (guardedWriter)
                {
                    for (var frame = 0; frame < acceptedFrames; ++frame)
                        Task.Run(() => ((IVideoFileWriter2)guardedWriter).WriteVideo(bitmap)).GetAwaiter().GetResult();
                    if (cancel)
                    {
                        cancellation.Cancel();
                        try
                        {
                            Task.Run(() => ((IVideoFileWriter2)guardedWriter).WriteVideo(bitmap)).GetAwaiter().GetResult();
                            throw new Exception("Cancelled writer accepted another video frame.");
                        }
                        catch (OperationCanceledException) { }
                    }
                    Task.Run(guardedWriter.Dispose).GetAwaiter().GetResult();
                }
                var shouldPublish = !cancel && acceptedFrames == expectedFrames;
                if ((File.ReadAllText(guardedPath) != sentinel) != shouldPublish)
                    throw new Exception("Cancelled/incomplete export publication contract failed.");
                var partials = Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(guardedPath)}.*.partial");
                if (partials.Length != 0)
                    throw new Exception("A cancelled, incomplete or published export left its partial output.");
            }
            finally
            {
                File.Delete(guardedPath);
                foreach (var partial in Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(guardedPath)}.*.partial"))
                    File.Delete(partial);
            }
        }
        Console.WriteLine("Cancelled and incomplete GPU exports preserved existing output; complete export published OK");

        var protectedPath = Path.Combine(Path.GetTempPath(), $"ymm4-protected-{Guid.NewGuid():N}.mp4");
        try
        {
            const string sentinel = "previous complete output";
            File.WriteAllText(protectedPath, sentinel);
            using (var writer = plugin.CreateVideoFileWriter(protectedPath,
                new VideoInfo { Width = 318, Height = 180, FPS = 30, Hz = 48000 }))
            {
                try { Task.Run(() => ((IVideoFileWriter2)writer).WriteVideo(bitmap)).GetAwaiter().GetResult(); throw new Exception("Wrong-sized texture accepted."); }
                catch (InvalidOperationException) { }
                try { writer.WriteAudio([0f, 0f]); throw new Exception("Failed writer accepted audio."); }
                catch (InvalidOperationException) { }
                var failedDispatcher = writer.GetType().GetField("_encoderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
                var failedOwner = (Thread)failedDispatcher.GetType().GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failedDispatcher)!;
                Task.Run(writer.Dispose).GetAwaiter().GetResult();
                if (failedOwner.IsAlive) throw new Exception("Encoder thread leaked after initialization failed.");
            }
            if (File.ReadAllText(protectedPath) != sentinel) throw new Exception("Failed export replaced an existing file.");
            if (Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(protectedPath)}.*.partial").Length != 0)
                throw new Exception("Failed export left its partial output.");
            Console.WriteLine("Invalid GPU input preserved existing output OK");
        }
        finally
        {
            File.Delete(protectedPath);
            foreach (var failed in Directory.GetFiles(Path.GetTempPath(), $".{Path.GetFileName(protectedPath)}.*.partial"))
                File.Delete(failed);
        }
    }
}
