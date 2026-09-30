using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace NVEncVideoWriterPlugin;

internal sealed class NvencVideoFileWriter : IVideoFileWriter3, IDisposable
{
    private readonly string _outputPath;
    private readonly string _stagingPath;
    private readonly VideoInfo _videoInfo;
    private readonly NvencSettings _settings;
    private readonly int _audioChannels;
    private IntPtr _encoderHandle = IntPtr.Zero;
    private bool _disposed;
    private bool _failed;
    private readonly object _encodeLock = new();
    private readonly EncoderThread _encoderThread;
    private readonly HostExportScope.Snapshot? _exportScope;
    private long _acceptedVideoFrames;

    public NvencVideoFileWriter(string outputPath, VideoInfo videoInfo, NvencSettings settings)
    {
        _outputPath = Path.GetFullPath(outputPath);
        _stagingPath = Path.Combine(Path.GetDirectoryName(_outputPath)!,
            $".{Path.GetFileName(_outputPath)}.{Guid.NewGuid():N}.partial");
        _videoInfo = videoInfo;
        _settings = settings;
        _audioChannels = ResolveAudioChannels(videoInfo);
        _exportScope = HostExportScope.GetCurrent();
        _encoderThread = new EncoderThread();
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Audio | VideoFileWriterSupportedStreams.Video;
    public bool IsGpuFrameSupported => true;

    private FileStream? _pendingAudio;

    public void WriteAudio(float[] samples)
    {
        lock (_encodeLock)
        {
            EnsureNotDisposed();
            _encoderThread.Invoke(() => WriteAudioCore(samples));
        }
    }

    private void WriteAudioCore(float[] samples)
    {
        if (samples == null || samples.Length == 0)
        {
            return;
        }
        try
        {
            _exportScope?.CancellationToken.ThrowIfCancellationRequested();
            if (_encoderHandle == IntPtr.Zero)
            {
                // The host can deliver all audio before its first video frame.
                // Spool to disk instead of retaining a project-sized float array.
                _pendingAudio ??= new FileStream(Path.Combine(Path.GetTempPath(),
                    $"ymm4-nvenc-audio-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 65536,
                    FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                _pendingAudio.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
            }
            else
                WriteAudioInternal(samples);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    public void WriteVideo(byte[] frame)
    {
        lock (_encodeLock)
        {
            EnsureNotDisposed();
            _failed = true;
            throw new NotSupportedException("このプラグインは YMM4 の GPU フレーム出力が必要です。");
        }
    }

    public void WriteVideo(ID2D1Bitmap1 frame)
    {
        lock (_encodeLock)
        {
            EnsureNotDisposed();
            // Invocation is synchronous: the host still owns the bitmap until it returns.
            _encoderThread.Invoke(() => WriteVideoCore(frame));
        }
    }

    private void WriteVideoCore(ID2D1Bitmap1 frame)
    {
        try
        {
            _exportScope?.CancellationToken.ThrowIfCancellationRequested();
            if (_videoInfo.HasErrors || _videoInfo.Width <= 0 || _videoInfo.Height <= 0)
                throw new InvalidOperationException("YMM4 の出力設定にエラーがあります。");

            using var surface = frame.Surface;
            using var texture = surface.QueryInterface<ID3D11Texture2D>();
            if (texture is null)
                throw new InvalidOperationException("D3D11 テクスチャを取得できませんでした。");

            if (_encoderHandle == IntPtr.Zero)
                InitializeEncoder(texture);

            if (NvencNativeMethods.NvencEncode(_encoderHandle, texture.NativePointer) == 0)
                throw new InvalidOperationException(GetNativeError());
            ++_acceptedVideoFrames;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    public void Dispose()
    {
        lock (_encodeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _encoderThread.Invoke(DisposeCore);
            }
            finally
            {
                _encoderThread.Dispose();
            }
        }
    }

    private void DisposeCore()
    {
        var hadEncoder = _encoderHandle != IntPtr.Zero;
        if (_exportScope is not null && !_exportScope.CanPublish(_acceptedVideoFrames))
            _failed = true;
        try
        {
            if (!_failed && !hadEncoder && _pendingAudio is { Length: > 0 })
                throw new InvalidOperationException("YMM4 から映像フレームが届かなかったため、音声を保存できませんでした。");
            if (!_failed && _encoderHandle != IntPtr.Zero && NvencNativeMethods.NvencFinalize(_encoderHandle) == 0)
            {
                var error = GetNativeError();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "NVENC 出力の終了処理に失敗しました。" : error);
            }
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally
        {
            try
            {
                _pendingAudio?.Dispose();
                _pendingAudio = null;
            }
            finally
            {
                if (_encoderHandle != IntPtr.Zero)
                    NvencNativeMethods.NvencDestroy(_encoderHandle);
                _encoderHandle = IntPtr.Zero;
            }
        }

        // Cancellation may arrive while native finalization is draining its queue.
        if (_exportScope is not null && !_exportScope.CanPublish(_acceptedVideoFrames))
            _failed = true;
        if (!_failed && hadEncoder && !File.Exists(_stagingPath))
            throw new IOException($"NVENC の出力ファイルが見つかりません: {_stagingPath}");
        if (!_failed && hadEncoder)
            File.Move(_stagingPath, _outputPath, true);
    }

    private void InitializeEncoder(ID3D11Texture2D texture)
    {
        using var device = texture.Device;
        if (device is null)
        {
            throw new InvalidOperationException("D3D11 デバイスを取得できませんでした。");
        }

        if ((_videoInfo.Width & 1) != 0 || (_videoInfo.Height & 1) != 0)
        {
            throw new InvalidOperationException("NVENC は偶数サイズの解像度が必要です。");
        }

        var fps = Math.Max(1, _videoInfo.FPS);
        var bitrate = GetTargetBitrateKbps();
        var codec = _settings.Codec switch
        {
            NvencCodec.H265 => 1,
            _ => 0,
        };
        var quality = (int)_settings.Quality;
        var rateControl = _settings.RateControl == NvencRateControl.Variable ? 1 : 0;
        if (_settings.RateControl == NvencRateControl.YouTubeRecommended)
        {
            rateControl = 1;
        }
        var maxBitrate = rateControl == 1
            ? Math.Clamp((int)(bitrate * 1.2), 100, 300000)
            : bitrate;
        var bufferFormat = ResolveBufferFormat(texture);

        _encoderHandle = NvencNativeMethods.NvencCreate(
            device.NativePointer,
            _videoInfo.Width,
            _videoInfo.Height,
            fps,
            bitrate,
            codec,
            quality,
            0,
            rateControl,
            maxBitrate,
            bufferFormat,
            _settings.HevcAsync && _settings.Codec == NvencCodec.H265 ? 1 : 0,
            _settings.EnableDebugLog ? 1 : 0,
            _stagingPath);

        if (_encoderHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("NVENC 初期化に失敗しました。");
        }

        var error = GetNativeError();
        if (!string.IsNullOrWhiteSpace(error))
        {
            NvencNativeMethods.NvencDestroy(_encoderHandle);
            _encoderHandle = IntPtr.Zero;
            throw new InvalidOperationException(error);
        }

        if (_pendingAudio is not null)
        {
            using var pending = _pendingAudio;
            _pendingAudio = null;
            pending.Position = 0;
            var buffer = new float[16384];
            var bytes = MemoryMarshal.AsBytes(buffer.AsSpan());
            while (pending.Position < pending.Length)
            {
                var count = (int)Math.Min(bytes.Length, pending.Length - pending.Position);
                pending.ReadExactly(bytes[..count]);
                WriteAudioInternal(buffer, count / sizeof(float));
            }
        }
    }

    private void WriteAudioInternal(float[] samples, int? sampleCount = null)
    {
        var sampleRate = Math.Max(8000, _videoInfo.Hz);
        var result = NvencNativeMethods.NvencWriteAudio(_encoderHandle, samples, sampleCount ?? samples.Length, sampleRate, _audioChannels);
        if (result == 0)
        {
            throw new InvalidOperationException(GetNativeError());
        }
    }

    private string GetNativeError()
    {
        if (_encoderHandle == IntPtr.Zero)
        {
            return string.Empty;
        }
        var ptr = NvencNativeMethods.NvencGetLastError(_encoderHandle);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(ptr) ?? string.Empty;
    }

    private static int ResolveBufferFormat(ID3D11Texture2D texture)
    {
        var format = texture.Description.Format;
        return format switch
        {
            Format.B8G8R8A8_UNorm => NvencBufferFormat.ARGB,
            Format.B8G8R8A8_UNorm_SRgb => NvencBufferFormat.ARGB,
            Format.R8G8B8A8_UNorm => NvencBufferFormat.ABGR,
            Format.R8G8B8A8_UNorm_SRgb => NvencBufferFormat.ABGR,
            _ => throw new NotSupportedException($"NVENC が対応していないフレーム形式です: {format}"),
        };
    }

    private static int ResolveAudioChannels(VideoInfo videoInfo)
    {
        const int fallback = 2;
        var type = videoInfo.GetType();
        var prop = type.GetProperty("Channels")
            ?? type.GetProperty("ChannelCount")
            ?? type.GetProperty("AudioChannels")
            ?? type.GetProperty("AudioChannelCount");
        if (prop?.GetValue(videoInfo) is int value && value > 0)
        {
            return value;
        }
        return fallback;
    }

    private int GetTargetBitrateKbps()
    {
        if (_settings.RateControl != NvencRateControl.YouTubeRecommended)
        {
            return Math.Clamp(_settings.BitrateKbps, 100, 200000);
        }

        var height = Math.Max(1, _videoInfo.Height);
        var highFps = _videoInfo.FPS >= 48;
        return height switch
        {
            >= 2160 => (highFps ? 60 : 40) * 1000,
            >= 1440 => (highFps ? 24 : 16) * 1000,
            >= 1080 => (highFps ? 12 : 8) * 1000,
            >= 720 => highFps ? 7500 : 5000,
            _ => (highFps ? 4 : 3) * 1000,
        };
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NvencVideoFileWriter));
        }
        if (_failed)
        {
            throw new InvalidOperationException("NVENC 出力は既に失敗しています。");
        }
    }

    private static class NvencBufferFormat
    {
        public const int ARGB = 0x01000000;
        public const int ABGR = 0x10000000;
    }

    // The host dispatches video, audio and Dispose on different threads. A lock alone
    // cannot pair native CoInitializeEx/CoUninitialize or preserve COM thread ownership.
    private sealed class EncoderThread : IDisposable
    {
        private readonly BlockingCollection<Action> _requests = new(1);
        private readonly Thread _thread;

        public EncoderThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var request in _requests.GetConsumingEnumerable())
                    request();
            })
            { IsBackground = true, Name = "YMM4 NVENC encoder" };
            _thread.SetApartmentState(ApartmentState.MTA);
            try { _thread.Start(); }
            catch { _requests.Dispose(); throw; }
        }

        public void Invoke(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            });
            completion.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _requests.CompleteAdding();
            _thread.Join();
            _requests.Dispose();
        }
    }
}
