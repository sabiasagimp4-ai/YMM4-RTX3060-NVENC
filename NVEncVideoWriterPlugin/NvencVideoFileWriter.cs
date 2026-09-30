using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace NVEncVideoWriterPlugin;

internal sealed class NvencVideoFileWriter : IVideoFileWriter2, IDisposable
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

    public NvencVideoFileWriter(string outputPath, VideoInfo videoInfo, NvencSettings settings)
    {
        _outputPath = Path.GetFullPath(outputPath);
        _stagingPath = Path.Combine(Path.GetDirectoryName(_outputPath)!,
            $".{Path.GetFileName(_outputPath)}.{Guid.NewGuid():N}.partial");
        _videoInfo = videoInfo;
        _settings = settings;
        _audioChannels = ResolveAudioChannels(videoInfo);
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Audio | VideoFileWriterSupportedStreams.Video;

    private readonly List<float> _pendingAudio = new();

    public void WriteAudio(float[] samples)
    {
        lock (_encodeLock)
        {
            EnsureNotDisposed();
            if (samples == null || samples.Length == 0)
            {
                return;
            }
            try
            {
                if (_encoderHandle == IntPtr.Zero)
                    _pendingAudio.AddRange(samples);
                else
                    WriteAudioInternal(samples);
            }
            catch
            {
                _failed = true;
                throw;
            }
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
            try
            {
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
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_encodeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            var hadEncoder = _encoderHandle != IntPtr.Zero;
            try
            {
                if (!_failed && !hadEncoder && _pendingAudio.Count > 0)
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
                if (_encoderHandle != IntPtr.Zero)
                    NvencNativeMethods.NvencDestroy(_encoderHandle);
                _encoderHandle = IntPtr.Zero;
            }

            if (!_failed && hadEncoder && !File.Exists(_stagingPath))
                throw new IOException($"NVENC の出力ファイルが見つかりません: {_stagingPath}");
            if (!_failed && hadEncoder)
                File.Move(_stagingPath, _outputPath, true);
        }
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

        if (_pendingAudio.Count > 0)
        {
            var buffer = _pendingAudio.ToArray();
            _pendingAudio.Clear();
            WriteAudioInternal(buffer);
        }
    }

    private void WriteAudioInternal(float[] samples)
    {
        var sampleRate = Math.Max(8000, _videoInfo.Hz);
        var result = NvencNativeMethods.NvencWriteAudio(_encoderHandle, samples, samples.Length, sampleRate, _audioChannels);
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

    
}
