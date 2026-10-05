using System.Text.RegularExpressions;

namespace NVEncVideoWriterPlugin;

// Japanese guidance for why NVENC cannot export on this PC. NvencNative.cpp writes the English forms parsed here
// (tests/NativeChecks.cpp pins them); anything else is shown as the native text.
internal static class NvencErrors
{
    private static readonly Regex DriverTooOld = new(@"^NVENC driver too old: supports API (\d+\.\d+), needs (\d+\.\d+)");
    private static readonly Regex CodecUnsupported = new(@"^NVENC codec unsupported: (\S+)");
    private static readonly Regex SizeUnsupported = new(@"^NVENC size unsupported: (\d+)x(\d+) > (\d+)x(\d+) \(([^)]+)\)");
    internal static bool IsExpectedCapabilityError(string native) => native.StartsWith("nvEncodeAPI64.dll not found", StringComparison.Ordinal)
        || DriverTooOld.IsMatch(native) || CodecUnsupported.IsMatch(native) || SizeUnsupported.IsMatch(native);

    internal static string Describe(string native)
    {
        if (string.IsNullOrWhiteSpace(native)) return "NVENC の初期化に失敗しました。";
        if (native.StartsWith("nvEncodeAPI64.dll not found", StringComparison.Ordinal))
            return "NVIDIA のドライバー（nvEncodeAPI64.dll）が見つかりません。NVENC 出力には NVIDIA の GPU とドライバーが必要です。ドライバーを入れ直してください。";
        if (DriverTooOld.Match(native) is { Success: true } old)
            return $"NVIDIA のドライバーが古いため NVENC を使えません（このドライバーは NVENC API {old.Groups[1].Value} まで、このプラグインは {old.Groups[2].Value} が必要）。ドライバーを更新してください（R570 以降）。";
        if (CodecUnsupported.Match(native) is { Success: true } codec)
            return codec.Groups[1].Value == "AV1"
                ? "この GPU の NVENC は AV1 に対応していません（AV1 の出力は GeForce RTX 40 シリーズ以降）。コーデックを H.264 か H.265 にしてください。"
                : $"この GPU の NVENC は {codec.Groups[1].Value} に対応していません。別のコーデックを選んでください。";
        if (SizeUnsupported.Match(native) is { Success: true } size)
            return $"この GPU の NVENC が {size.Groups[5].Value} で出力できる大きさは {size.Groups[3].Value}×{size.Groups[4].Value} までです（出力 {size.Groups[1].Value}×{size.Groups[2].Value}）。解像度を下げるか、別のコーデックを選んでください。";
        if (native.StartsWith("nvEncOpenEncodeSessionEx failed", StringComparison.Ordinal))
            return $"NVENC を開けませんでした（{native}）。録画・配信ソフトなどが NVENC を同時に使っているか、この GPU が NVENC に対応していない可能性があります。";
        return native;
    }

    // YMM4 renders with one adapter; NVENC must be on that adapter, since frames stay on the GPU.
    // The vendor id is a long: Vortice versions declare it int or uint.
    internal static string? NonNvidiaAdapter(long vendorId, string adapterName) => vendorId == 0x10DE ? null
        : $"YMM4 が描画に使っている GPU（{adapterName}）は NVIDIA ではないため、NVENC で出力できません。NVIDIA の GPU を積んだノートPCなどでは、Windows の設定 > システム > ディスプレイ > グラフィックス で YukkuriMovieMaker を「高パフォーマンス」にして、YMM4 を再起動してください。";
}
