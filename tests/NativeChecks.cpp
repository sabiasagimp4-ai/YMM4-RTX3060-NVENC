// Exercise internal invariants without exposing test-only DLL entry points.
#include "../NvencNative/NvencNative.cpp"
#include <iostream>
#include <stdexcept>
#include <limits>
#include "HevcConfigurationChecks.h"

void Check(bool ok, const char* message)
{
    if (!ok) throw std::runtime_error(message);
}

uint32_t U32(const std::vector<uint8_t>& bytes, size_t at)
{
    return (uint32_t(bytes.at(at)) << 24) | (uint32_t(bytes.at(at + 1)) << 16) |
        (uint32_t(bytes.at(at + 2)) << 8) | bytes.at(at + 3);
}

uint64_t U64(const std::vector<uint8_t>& bytes, size_t at)
{
    return (uint64_t(U32(bytes, at)) << 32) | U32(bytes, at + 4);
}

size_t Box(const std::vector<uint8_t>& bytes, const char* type, size_t start, size_t end)
{
    for (size_t at = start; at + 8 <= end;)
    {
        auto size = U32(bytes, at);
        Check(size >= 8 && size <= end - at, "Invalid MP4 box length");
        if (memcmp(bytes.data() + at + 4, type, 4) == 0) return at;
        at += size;
    }
    throw std::runtime_error(type);
}

std::string cleanupCalls;
int cleanupFailure = 0;

void CheckCleanup()
{
    for (int failure : { 0, 1, 2 })
    {
        cleanupCalls.clear();
        cleanupFailure = failure;
        auto* state = new EncoderState();
        state->session = state;
        state->encoderInitialized = true;
        state->initParams.enableEncodeAsync = 1;
        state->asyncEnabled = true;
        state->asyncBitstreams = { reinterpret_cast<void*>(1) };
        state->asyncEvents = { CreateEventW(nullptr, FALSE, TRUE, nullptr) };
        state->asyncPending = { true };
        state->inputs.resize(1);
        state->inputs[0].mapped = reinterpret_cast<void*>(2);
        state->inputs[0].registered = reinterpret_cast<void*>(3);
        state->writerError = true; // Cleanup must not enqueue output into a failed writer.
        state->funcs.nvEncRegisterAsyncEvent = [](void*, NV_ENC_EVENT_PARAMS*) { return NV_ENC_SUCCESS; };
        state->funcs.nvEncEncodePicture = [](void*, NV_ENC_PIC_PARAMS* pic)
        {
            cleanupCalls += 'E';
            Check(pic->encodePicFlags == NV_ENC_PIC_FLAG_EOS, "Cleanup did not submit EOS");
            if (cleanupFailure == 1) return NV_ENC_ERR_GENERIC;
            SetEvent(pic->completionEvent);
            return NV_ENC_SUCCESS;
        };
        state->funcs.nvEncLockBitstream = [](void*, NV_ENC_LOCK_BITSTREAM* output)
        {
            cleanupCalls += 'L';
            if (cleanupFailure == 2) return NV_ENC_ERR_DEVICE_NOT_EXIST;
            // Invalid data deliberately proves discard does not parse/write output.
            output->bitstreamBufferPtr = reinterpret_cast<void*>(1);
            output->bitstreamSizeInBytes = 100;
            return NV_ENC_SUCCESS;
        };
        state->funcs.nvEncUnlockBitstream = [](void*, NV_ENC_OUTPUT_PTR) { cleanupCalls += 'K'; return NV_ENC_SUCCESS; };
        state->funcs.nvEncUnmapInputResource = [](void*, NV_ENC_INPUT_PTR) { cleanupCalls += 'U'; return NV_ENC_SUCCESS; };
        state->funcs.nvEncUnregisterResource = [](void*, NV_ENC_REGISTERED_PTR) { cleanupCalls += 'R'; return NV_ENC_SUCCESS; };
        state->funcs.nvEncDestroyBitstreamBuffer = [](void*, NV_ENC_OUTPUT_PTR) { cleanupCalls += 'B'; return NV_ENC_SUCCESS; };
        state->funcs.nvEncUnregisterAsyncEvent = [](void*, NV_ENC_EVENT_PARAMS*) { cleanupCalls += 'A'; return NV_ENC_SUCCESS; };
        state->funcs.nvEncDestroyEncoder = [](void*) { cleanupCalls += 'D'; return NV_ENC_SUCCESS; };
        NvencDestroy(state);
        Check(cleanupCalls == (failure == 1 ? "ED" : failure == 2 ? "ELD" : "ELKURBAAD"),
            "Unsafe cleanup order or failed writer was used");
    }
}

int main()
{
    try
    {
        CheckCleanup();
        CheckHevcConfiguration();
        Check(NvencFinalize(nullptr) == 0 && NvencWriteAudio(nullptr, nullptr, 0, 48000, 2) == 0,
            "Null handles accepted");
        EncoderState inactive;
        Check(NvencFinalize(&inactive) == 0 && NvencWriteAudio(&inactive, nullptr, 0, 48000, 2) == 0,
            "Uninitialized encoder was used");
        Check(ClampFloat(std::numeric_limits<float>::quiet_NaN(), -1, 1) == 0 &&
            ClampFloat(std::numeric_limits<float>::infinity(), -1, 1) == 1 &&
            ClampFloat(-std::numeric_limits<float>::infinity(), -1, 1) == -1,
            "Non-finite PCM conversion");
        const uint8_t data[] = { 0x0e, 1, 0x40, 1 };
        const std::vector<NalUnit> units{ { data, 2, 7 }, { data + 2, 2, 32 } };
        Check(ConvertToLengthPrefixed(units, true) == std::vector<uint8_t>({ 0, 0, 0, 2, 0x0e, 1 }),
            "HEVC VCL type 7 was removed");
        Check(ConvertToLengthPrefixed(units, false) == std::vector<uint8_t>({ 0, 0, 0, 2, 0x40, 1 }),
            "H264 parameter set was retained");

        EncoderState state;
        state.fps = 37;
        state.sampleSizes.resize(37, 1);
        state.audioSampleRate = 48000;
        state.audioChannels = 2;
        state.audioSampleSizes = { 1 };
        state.audioSpecificConfig = { 0x11, 0x90 };
        state.audioSampleTotal = 48000;
        auto bytes = BuildMoov(&state);
        auto mvhd = Box(bytes, "mvhd", 8, bytes.size());
        Check(bytes.at(mvhd + 8) == 1 && U32(bytes, mvhd + 28) == 37 && U64(bytes, mvhd + 32) == 37,
            "37 fps movie timing");
        auto video = Box(bytes, "trak", 8, bytes.size());
        auto audio = Box(bytes, "trak", video + U32(bytes, video), bytes.size());
        auto tkhd = Box(bytes, "tkhd", audio + 8, audio + U32(bytes, audio));
        Check(U64(bytes, tkhd + 36) == 37, "Audio track duration uses the wrong timescale");
        auto mdia = Box(bytes, "mdia", video + 8, video + U32(bytes, video));
        auto mdhd = Box(bytes, "mdhd", mdia + 8, mdia + U32(bytes, mdia));
        Check(U32(bytes, mdhd + 28) == 37 && U64(bytes, mdhd + 32) == 37, "Video media timing");
        state.audioSampleTotal = uint64_t(48000) * 60 * 60 * 30;
        bytes = BuildMoov(&state);
        mvhd = Box(bytes, "mvhd", 8, bytes.size());
        Check(U64(bytes, mvhd + 32) == uint64_t(37) * 60 * 60 * 30, "Long movie duration overflow");
        video = Box(bytes, "trak", 8, bytes.size());
        audio = Box(bytes, "trak", video + U32(bytes, video), bytes.size());
        mdia = Box(bytes, "mdia", audio + 8, audio + U32(bytes, audio));
        mdhd = Box(bytes, "mdhd", mdia + 8, mdia + U32(bytes, mdia));
        Check(U64(bytes, mdhd + 32) == state.audioSampleTotal, "Long audio duration overflow");

        // A full queue must wake on failure rather than waiting for its 30-second deadline.
        EncoderState queue;
        queue.writerStarted = true;
        queue.queuedBytes = 64 * 1024 * 1024;
        auto wake = std::thread([&]()
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            queue.writerError = true;
            queue.writerCv.notify_all();
        });
        auto before = std::chrono::steady_clock::now();
        const bool accepted = QueueSample(&queue, { { 1 }, false, false, 0 });
        wake.join();
        Check(!accepted && std::chrono::steady_clock::now() - before < std::chrono::seconds(2),
            "Full writer queue ignored failure");
        Check(queue.sampleQueue.empty(), "Failed writer accepted a sample");

        // Driver and GPU capability texts: the managed side (NvencErrors) parses these exact forms.
        const uint32_t needed = (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;
        Check(DriverSupportsApi(needed) && DriverSupportsApi(needed + 1) && DriverSupportsApi((NVENCAPI_MAJOR_VERSION + 1) << 4)
            && !DriverSupportsApi(needed - 1) && !DriverSupportsApi((12 << 4) | 2), "Driver API version comparison");
        Check(DriverTooOldMessage((12 << 4) | 2) == L"NVENC driver too old: supports API 12.2, needs "
            + std::to_wstring(NVENCAPI_MAJOR_VERSION) + L"." + std::to_wstring(NVENCAPI_MINOR_VERSION), "Driver message");
        Check(CodecUnsupportedMessage(kCodecAv1) == L"NVENC codec unsupported: AV1"
            && CodecUnsupportedMessage(kCodecHevc) == L"NVENC codec unsupported: HEVC", "Codec message");
        Check(SizeUnsupportedMessage(kCodecH264, 7680, 4320, 4096, 4096) == L"NVENC size unsupported: 7680x4320 > 4096x4096 (H.264)",
            "Size message");
        std::cout << "Native cache/queue, NAL and MP4 invariants OK\n";
        return 0;
    }
    catch (const std::exception& e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
