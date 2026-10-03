#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <cmath>
#include <chrono>
#include <iostream>
#include <vector>
#include "NvencNative.h"
#include "nvEncodeAPI.h"

#pragma comment(lib, "d3d11.lib")

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3 && argc != 4 && argc != 9)
    {
        std::wcerr << L"usage: NativeSmoke.exe output.mp4 codec(0=h264,1=hevc,2=av1) [cancel|failure|frames width height quality fastPreset hevcAsync]\n";
        return 2;
    }

    const bool benchmark = argc == 9;
    const bool cancel = argc == 4 && wcscmp(argv[3], L"cancel") == 0;
    const bool failure = argc == 4 && wcscmp(argv[3], L"failure") == 0;
    if (argc == 4 && !cancel && !failure) return 2;
    const int codec = _wtoi(argv[2]);
    const int frames = benchmark ? _wtoi(argv[3]) : 30;
    const int width = benchmark ? _wtoi(argv[4]) : 320;
    const int height = benchmark ? _wtoi(argv[5]) : 180;
    const int quality = benchmark ? _wtoi(argv[6]) : 1;
    const int fastPreset = benchmark ? _wtoi(argv[7]) : 0;
    const int hevcAsync = benchmark ? _wtoi(argv[8]) : codec == 1;
    constexpr int fps = 30;
    if (codec < 0 || codec > 2 || frames < 1 || frames > 1800 || width < 2 || width > 3840 ||
        height < 2 || height > 2160 || (width & 1) || (height & 1) || quality < 0 || quality > 2 ||
        fastPreset < 0 || fastPreset > 1 || hevcAsync < 0 || hevcAsync > 1)
    {
        std::wcerr << L"invalid benchmark arguments\n";
        return 2;
    }
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &device, nullptr, &context)))
    {
        std::wcerr << L"D3D11 hardware device unavailable\n";
        return 1;
    }

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    ID3D11Texture2D* texture = nullptr;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &texture)))
    {
        std::wcerr << L"D3D11 texture creation failed\n";
        context->Release();
        device->Release();
        return 1;
    }

    std::vector<ID3D11Texture2D*> sources{ texture };
    if (benchmark)
    {
        std::vector<unsigned char> pattern(width * height * 4);
        for (int image = 0; image < 8; ++image)
        {
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    const auto offset = (y * width + x) * 4;
                    pattern[offset] = static_cast<unsigned char>(x * 3 + y * 5 + image * 17);
                    pattern[offset + 1] = static_cast<unsigned char>((x * 11) ^ (y * 13) ^ (image * 29));
                    pattern[offset + 2] = static_cast<unsigned char>(((x * y) >> 4) + image * 47);
                    pattern[offset + 3] = 255;
                }
            if (image == 0)
                context->UpdateSubresource(texture, 0, nullptr, pattern.data(), width * 4, 0);
            else
            {
                ID3D11Texture2D* source = nullptr;
                D3D11_SUBRESOURCE_DATA initial{ pattern.data(), static_cast<UINT>(width * 4), 0 };
                if (FAILED(device->CreateTexture2D(&desc, &initial, &source)))
                {
                    std::wcerr << L"D3D11 benchmark texture unavailable\n";
                    for (auto* item : sources)
                        item->Release();
                    context->Release();
                    device->Release();
                    return 1;
                }
                sources.push_back(source);
            }
        }
    }

    const auto start = std::chrono::steady_clock::now();
    void* encoder = NvencCreate(device, width, height, fps, 8000, codec, quality, fastPreset, 1, 12000,
        NV_ENC_BUFFER_FORMAT_ARGB, hevcAsync, benchmark ? 0 : 1, argv[1]);
    bool ok = encoder && !*NvencGetLastError(encoder);
    std::vector<unsigned char> pixels;
    if (!benchmark)
        pixels.resize(width * height * 4, 255);
    for (int frame = 0; ok && frame < frames; ++frame)
    {
        if (!benchmark)
        {
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    const auto offset = (y * width + x) * 4;
                    pixels[offset] = static_cast<unsigned char>(x + frame);
                    pixels[offset + 1] = static_cast<unsigned char>(y + frame);
                    pixels[offset + 2] = static_cast<unsigned char>(frame * 7);
                }
            context->UpdateSubresource(texture, 0, nullptr, pixels.data(), width * 4, 0);
        }
        ok = NvencEncode(encoder, sources[benchmark ? frame % sources.size() : 0]) != 0;
    }

    if (ok && !cancel && !failure)
    {
        const int audioFrames = 48000 * frames / fps;
        std::vector<float> audio(audioFrames * 2);
        for (int i = 0; i < audioFrames; ++i)
        {
            const float sample = static_cast<float>(0.1 * std::sin(i * 440.0 * 6.283185307179586 / 48000.0));
            audio[i * 2] = sample;
            audio[i * 2 + 1] = sample;
        }
        ok = NvencWriteAudio(encoder, audio.data(), static_cast<int>(audio.size()), 48000, 2) != 0;
    }
    if (ok && failure)
    {
        // An invalid input after accepted frames leaves the async ring in flight.
        auto badDesc = desc;
        badDesc.Width += 2;
        ID3D11Texture2D* invalid = nullptr;
        ok = SUCCEEDED(device->CreateTexture2D(&badDesc, nullptr, &invalid));
        if (ok) ok = NvencEncode(encoder, invalid) == 0 && *NvencGetLastError(encoder);
        if (invalid) invalid->Release();
    }
    if (ok && !cancel && !failure)
        ok = NvencFinalize(encoder) != 0;
    const auto end = std::chrono::steady_clock::now();
    if (ok && benchmark)
    {
        const double seconds = std::chrono::duration<double>(end - start).count();
        std::wcout << L"elapsed_seconds=" << seconds << L" frames_per_second=" << frames / seconds << L'\n';
    }
    if (!ok)
        std::wcerr << (encoder ? NvencGetLastError(encoder) : L"NvencCreate returned null") << L'\n';

    NvencDestroy(encoder);
    for (auto* item : sources)
        item->Release();
    context->Release();
    device->Release();
    return ok ? 0 : 1;
}
