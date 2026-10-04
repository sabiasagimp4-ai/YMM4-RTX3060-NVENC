#pragma once
#include "../NvencNative/HevcConfiguration.h"
#include <stdexcept>
#include <string>

inline std::vector<uint8_t> HevcHex(const std::string& text)
{
    std::vector<uint8_t> bytes;
    for (size_t i = 0; i < text.size(); i += 2)
        bytes.push_back(static_cast<uint8_t>(std::stoul(text.substr(i, 2), nullptr, 16)));
    return bytes;
}

inline void CheckHevcConfiguration()
{
    // Parameter sets from our libx265 3840x2160 Main/yuv420p/level 5.0 review reproduction.
    const auto vps = HevcHex("40010c01ffff016000000300900000030000030096959409");
    const auto sps = HevcHex("420101016000000300900000030000030096a001e020021c5965654a4c2f0168080000030008000003000840");
    const auto pps = HevcHex("4401c073c189");
    const auto record = HevcConfiguration::Build(vps, sps, pps);
    const auto expected = HevcHex("01016000000090000000000096f000fcfdf8f800000f03");
    if (record.size() <= expected.size() || !std::equal(expected.begin(), expected.end(), record.begin()))
        throw std::runtime_error("HEVC 4K configuration disagrees with the encoder's SPS");
    size_t position = 23;
    for (const auto& data : { vps, sps, pps })
    {
        if (record.at(position + 1) != 0 || record.at(position + 2) != 1
            || ((size_t(record.at(position + 3)) << 8) | record.at(position + 4)) != data.size()
            || !std::equal(data.begin(), data.end(), record.begin() + position + 5))
            throw std::runtime_error("HEVC parameter set changed in the decoder configuration");
        position += 5 + data.size();
    }
    if (position != record.size()) throw std::runtime_error("HEVC configuration has trailing data");
    for (size_t length = 0; length < 22; ++length)
        if (!HevcConfiguration::Build(vps, { sps.begin(), sps.begin() + length }, pps).empty())
            throw std::runtime_error("Truncated HEVC SPS accepted");
    auto bad = sps;
    bad[0] |= 0x80;
    if (!HevcConfiguration::Build(vps, bad, pps).empty()
        || !HevcConfiguration::Build({}, sps, pps).empty()
        || !HevcConfiguration::Build(vps, sps, std::vector<uint8_t>(65536, 0)).empty())
        throw std::runtime_error("Invalid HEVC parameter set accepted");
    bad = sps;
    bad[8] = 4; // invalid emulation-prevention byte followed by a value above 3
    if (!HevcConfiguration::Build(vps, bad, pps).empty())
        throw std::runtime_error("Invalid HEVC emulation prevention accepted");
}
