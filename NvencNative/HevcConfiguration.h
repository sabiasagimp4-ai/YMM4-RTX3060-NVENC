#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace HevcConfiguration
{
    // ISO/IEC 14496-15 configuration fields come from the encoder's SPS, not the requested resolution.
    struct Bits
    {
        const std::vector<uint8_t>& bytes;
        size_t position = 0;
        bool Read(unsigned count, uint32_t& value)
        {
            if (count > 32 || position > bytes.size() * 8 || count > bytes.size() * 8 - position) return false;
            value = 0;
            for (unsigned i = 0; i < count; ++i, ++position)
                value = (value << 1) | ((bytes[position / 8] >> (7 - position % 8)) & 1);
            return true;
        }
        bool Skip(unsigned count)
        {
            uint32_t ignored;
            while (count > 32) { if (!Read(32, ignored)) return false; count -= 32; }
            return Read(count, ignored);
        }
        bool UE(uint32_t& value)
        {
            uint32_t bit, suffix;
            unsigned zeroes = 0;
            while (true)
            {
                if (!Read(1, bit)) return false;
                if (bit) break;
                if (++zeroes > 31) return false;
            }
            if (!Read(zeroes, suffix)) return false;
            value = ((uint32_t(1) << zeroes) - 1) + suffix;
            return true;
        }
    };

    inline bool Nal(const std::vector<uint8_t>& data, unsigned type)
    {
        return data.size() >= 3 && data.size() <= 65535 && (data[0] & 0x80) == 0
            && ((data[0] >> 1) & 63) == type && (data[1] & 7) != 0;
    }

    inline std::vector<uint8_t> Build(const std::vector<uint8_t>& vps,
        const std::vector<uint8_t>& sps, const std::vector<uint8_t>& pps)
    {
        if (!Nal(vps, 32) || !Nal(sps, 33) || !Nal(pps, 34)) return {};
        std::vector<uint8_t> rbsp;
        unsigned zeroes = 0;
        for (size_t i = 2; i < sps.size(); ++i)
        {
            uint8_t byte = sps[i];
            if (zeroes >= 2 && byte == 3)
            {
                if (i + 1 == sps.size() || sps[i + 1] > 3) return {};
                zeroes = 0;
                continue;
            }
            rbsp.push_back(byte);
            zeroes = byte == 0 ? zeroes + 1 : 0;
        }
        Bits bits{ rbsp };
        uint32_t ignored, subLayers, nested;
        if (!bits.Read(4, ignored) || !bits.Read(3, subLayers) || subLayers > 6 || !bits.Read(1, nested)) return {};
        std::vector<uint8_t> record{ 1 };
        // general_profile_space/tier/profile_idc, compatibility flags, constraints and level (96 bits).
        for (unsigned i = 0; i < 12; ++i)
        {
            uint32_t byte;
            if (!bits.Read(8, byte)) return {};
            record.push_back(static_cast<uint8_t>(byte));
        }
        uint32_t profile[7]{}, level[7]{};
        for (unsigned i = 0; i < subLayers; ++i)
            if (!bits.Read(1, profile[i]) || !bits.Read(1, level[i])) return {};
        if (subLayers && !bits.Skip(2 * (8 - subLayers))) return {};
        for (unsigned i = 0; i < subLayers; ++i)
        {
            if (profile[i] && !bits.Skip(88)) return {};
            if (level[i] && !bits.Skip(8)) return {};
        }
        uint32_t chroma, width, height, window, depthLuma, depthChroma;
        if (!bits.UE(ignored) || ignored > 15 || !bits.UE(chroma) || chroma > 3) return {};
        if (chroma == 3 && !bits.Skip(1)) return {};
        if (!bits.UE(width) || !width || !bits.UE(height) || !height || !bits.Read(1, window)) return {};
        if (window)
            for (unsigned i = 0; i < 4; ++i) if (!bits.UE(ignored)) return {};
        if (!bits.UE(depthLuma) || depthLuma > 6 || !bits.UE(depthChroma) || depthChroma > 6) return {};
        record.insert(record.end(), { 0xf0, 0, 0xfc, static_cast<uint8_t>(0xfc | chroma),
            static_cast<uint8_t>(0xf8 | depthLuma), static_cast<uint8_t>(0xf8 | depthChroma), 0, 0,
            static_cast<uint8_t>(((subLayers + 1) << 3) | (nested << 2) | 3), 3 });
        auto append = [&](unsigned type, const std::vector<uint8_t>& data)
        {
            record.insert(record.end(), { static_cast<uint8_t>(0x80 | type), 0, 1,
                static_cast<uint8_t>(data.size() >> 8), static_cast<uint8_t>(data.size()) });
            record.insert(record.end(), data.begin(), data.end());
        };
        append(32, vps); append(33, sps); append(34, pps);
        return record;
    }
}
