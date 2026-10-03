using NVEncVideoWriterPlugin;

// NvencErrors: the native texts pinned in tests/NativeChecks.cpp become Japanese guidance.
internal static class NvencErrorChecks
{
    internal static void Run()
    {
        Check(NvencErrors.Describe("NVENC driver too old: supports API 12.2, needs 13.0")
            .Contains("NVENC API 12.2 まで、このプラグインは 13.0 が必要"), "driver too old");
        Check(NvencErrors.Describe("NVENC codec unsupported: AV1").Contains("RTX 40 シリーズ以降"), "AV1 on an older GPU");
        Check(NvencErrors.Describe("NVENC codec unsupported: HEVC").Contains("HEVC に対応していません"), "other codec");
        Check(NvencErrors.Describe("NVENC size unsupported: 7680x4320 > 4096x4096 (H.264)")
            .Contains("H.264 で出力できる大きさは 4096×4096 まで") , "size limit");
        Check(NvencErrors.Describe("nvEncodeAPI64.dll not found. Check NVIDIA driver.").Contains("ドライバーを入れ直して"), "no driver");
        Check(NvencErrors.Describe("nvEncOpenEncodeSessionEx failed (10)").Contains("同時に使って"), "session");
        Check(NvencErrors.Describe("nvEncInitializeEncoder failed (8)") == "nvEncInitializeEncoder failed (8)", "unknown text kept");
        Check(NvencErrors.Describe("") == "NVENC の初期化に失敗しました。", "empty");
        Check(NvencErrors.NonNvidiaAdapter(0x10DE, "NVIDIA GeForce RTX 4070") is null, "NVIDIA adapter accepted");
        Check(NvencErrors.NonNvidiaAdapter(0x8086, "Intel(R) UHD Graphics") is { } intel && intel.Contains("Intel(R) UHD Graphics")
            && intel.Contains("高パフォーマンス"), "integrated adapter explained");
        Console.WriteLine("NVENC errors: driver, codec, size, session and adapter guidance passed.");
    }

    private static void Check(bool result, string message) { if (!result) throw new Exception("NVENC errors: " + message); }
}
