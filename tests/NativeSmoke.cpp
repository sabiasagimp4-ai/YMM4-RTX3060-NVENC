#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <cmath>
#include <iostream>
#include <vector>
#include "NvencNative.h"
#include "nvEncodeAPI.h"

#pragma comment(lib, "d3d11.lib")

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3)
    {
        std::wcerr << L"usage: NativeSmoke.exe output.mp4 codec(0=h264,1=hevc)\n";
        return 2;
    }

    constexpr int width = 320, height = 180, fps = 30;
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

    const int codec = _wtoi(argv[2]);
    void* encoder = NvencCreate(device, width, height, fps, 8000, codec, 1, 0, 1, 12000,
        NV_ENC_BUFFER_FORMAT_ARGB, codec == 1, 1, argv[1]);
    bool ok = encoder && !*NvencGetLastError(encoder);
    std::vector<unsigned char> pixels(width * height * 4, 255);
    for (int frame = 0; ok && frame < fps; ++frame)
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
        ok = NvencEncode(encoder, texture) != 0;
    }

    if (ok)
    {
        std::vector<float> audio(48000 * 2);
        for (int i = 0; i < 48000; ++i)
        {
            const float sample = static_cast<float>(0.1 * std::sin(i * 440.0 * 6.283185307179586 / 48000.0));
            audio[i * 2] = sample;
            audio[i * 2 + 1] = sample;
        }
        ok = NvencWriteAudio(encoder, audio.data(), static_cast<int>(audio.size()), 48000, 2) != 0;
    }
    if (ok)
        ok = NvencFinalize(encoder) != 0;
    if (!ok)
        std::wcerr << (encoder ? NvencGetLastError(encoder) : L"NvencCreate returned null") << L'\n';

    NvencDestroy(encoder);
    texture->Release();
    context->Release();
    device->Release();
    return ok ? 0 : 1;
}
