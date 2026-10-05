using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

internal static class HostIntegration
{
    internal const string PatchId = "sabiasagimp4-ai.ymm4-rtx3060-nvenc.host";
    private static readonly object installLock = new();
    private static readonly Harmony harmony = new(PatchId);
    private static readonly Harmony cacheHarmony = new(PatchId + ".cache");
    private static Assembly? checkedHost;
    private static bool installed;
    private static bool cacheAvailable;
    private static bool exportHooked;
    private static Assembly? installedHost;
    private static string exportProblem = string.Empty;
    private static string status = "YMM4との連携はまだ初期化されていません。";

    internal static string Status { get { lock (installLock) return status; } }
    internal static bool CacheAvailable { get { lock (installLock) return cacheAvailable; } }
    internal static bool ExportHooked { get { lock (installLock) return exportHooked; } }

    // The NVENC output setting (FrameCacheToolSettings); outside YMM4 (tests, probes) the output is always on.
    internal static Func<bool> NvencOutputEnabled { get; set; } = () =>
    {
        if (!IsHostProcess) return true;
        try { return FrameCacheToolSettings.Default.NvencOutput; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return true; }
    };
    internal static bool IsHostProcess => Assembly.GetEntryAssembly()?.GetName().Name == "YukkuriMovieMaker";

    internal static bool EnsureInstalled()
    {
        lock (installLock)
        {
            var host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "YukkuriMovieMaker");
            if (host is null)
            {
                status = "YMM4が読み込まれていません（オフライン検査のみ利用できます）。";
                return false;
            }
            if (ReferenceEquals(checkedHost, host)) return installed;
            checkedHost = host;
            bool verified = VerifyHost(host, out var version, out _);
            return Install(host, verified, version);
        }
    }

    // verified: one of KnownHosts (VerifyHost). Separate from EnsureInstalled so that probes can install on the
    // real host as if it were another build.
    internal static bool Install(Assembly host, bool verified, string version)
    {
        lock (installLock)
        {
            try
            {
                // The export hook (for NVENC output only) needs its own contract: whatever the host does, an output
                // replaces the file only when every frame of the range arrived without cancellation. With NVENC
                // output switched off in the settings it is not installed and YMM4's export is left untouched. The
                // cache depends on how the host renders, so it is installed only on builds whose code was checked.
                string reason;
                installedHost = host;
                exportHooked = false;
                exportProblem = string.Empty;
                if (NvencOutputEnabled())
                {
                    if (HostExportScope.TryInstall(host, harmony, out reason)) exportHooked = true;
                    else { exportProblem = reason; DiagnosticReports.RecordUnavailable(DiagnosticComponent.ExportHook, DiagnosticKind.HookFailure); }
                }
                installed = true;
                string features = string.Empty;
                if (!verified)
                {
                    // A build that was not read: the cache only where its code is that of a read build.
                    version = HostVersion(host);
                    if (!TryMatchReadBuild(host, out var matched, out var detail))
                    {
                        cacheAvailable = false;
                        DiagnosticReports.RecordUnavailable(DiagnosticComponent.Host, DiagnosticKind.ContractMismatch);
                        status = $"YMM4 {version}（未確認の版）: {ExportStatus()}自動キャッシュは使いません: {detail}";
                        return true;
                    }
                    HostFeatures.Decide(host, matched);
                    if (!matched.SimpleTachie || !matched.AnimationTachie || !matched.PsdTachie || !matched.LipSync
                        || !matched.SelectionRects || !matched.WrappedSources || !matched.RulerBars)
                        DiagnosticReports.RecordUnavailable(DiagnosticComponent.CacheHook, DiagnosticKind.FeatureUnavailable);
                    features = $"キャッシュが前提とする本体のコードが検証済みの {matched.Basis} と同じため使います。{detail}";
                }
                cacheAvailable = TimelineFrameCache.TryInstall(host, cacheHarmony, out reason);
                if (!cacheAvailable)
                { DiagnosticReports.RecordUnavailable(DiagnosticComponent.CacheHook, DiagnosticKind.HookFailure); cacheHarmony.UnpatchAll(cacheHarmony.Id); }
                else TimelineCacheBars.TryInstall(host, out _);
                status = cacheAvailable
                    ? $"YMM4 {version}: {ExportStatus()}自動キャッシュの接続を確認しました。{features}"
                    : $"{ExportStatus()}自動キャッシュは利用できません: " + reason;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticReports.RecordException(DiagnosticComponent.Host, ex);
                installed = false;
                cacheAvailable = false;
                exportHooked = false;
                status = $"YMM4との連携を無効にしました: {ex.GetBaseException().Message}";
                try { harmony.UnpatchAll(PatchId); cacheHarmony.UnpatchAll(cacheHarmony.Id); }
                catch (Exception rollback) { DiagnosticReports.RecordException(DiagnosticComponent.Host, rollback); status += $"（フックの解除にも失敗しました: {rollback.GetBaseException().Message}）"; }
                return false;
            }
        }
    }

    // Under installLock.
    private static string ExportStatus() => exportHooked ? "NVENC 出力（取消保護つき）を使えます。"
        : exportProblem.Length != 0 ? $"NVENC 出力は使えません（{exportProblem}）。" : "NVENC 出力は設定で無効です。";

    // When NVENC output is switched on after start: installs the export hook now (before an export starts).
    internal static bool EnsureExportHooks(out string reason)
    {
        lock (installLock)
        {
            reason = string.Empty;
            if (exportHooked) return true;
            if (!installed || installedHost is null) { reason = status; return false; }
            if (!HostExportScope.TryInstall(installedHost, harmony, out reason))
            { DiagnosticReports.RecordUnavailable(DiagnosticComponent.ExportHook, DiagnosticKind.HookFailure); exportProblem = reason; return false; }
            exportHooked = true;
            exportProblem = string.Empty;
            status = status.Replace("NVENC 出力は設定で無効です。", "NVENC 出力（取消保護つき）を使えます。", StringComparison.Ordinal);
            return true;
        }
    }

    internal static void RequireExportScope()
    {
        // Direct/offline callers have no host cancellation contract. The real host must have one.
        if (!IsHostProcess) return;
        if (!NvencOutputEnabled())
            throw new InvalidOperationException("「NVIDIA NVENC 出力」は設定で無効になっています。ツール「描画キャッシュ」か、YMM4 の設定（その他）で有効にするか、YMM4 標準の出力形式を選んでください。");
        if (!EnsureInstalled() || !EnsureExportHooks(out var reason))
            throw new NotSupportedException($"このYMM4では安全な動画出力を開始できません。{Status}");
        if (HostExportScope.GetCurrent() is null)
        {
            DiagnosticReports.RecordUnavailable(DiagnosticComponent.ExportHook, DiagnosticKind.ContractMismatch);
            throw new InvalidOperationException("YMM4の出力範囲・取消状態を取得できなかったため、既存ファイルを保護して出力を中止しました。");
        }
    }

    private sealed record KnownBinary(string File, Guid Mvid, string Sha256);
    private sealed record KnownHost(string Version, KnownBinary Host, KnownBinary Plugin, KnownBinary Settings);

    // Exact host builds whose hooked internals were inspected. 4.56.1.0 was read with ILSpy from the official
    // Lite zip (see docs/HOST_CONTRACTS.md); other builds use per-feature contract evaluation for optional cache support.
    private static readonly KnownHost[] KnownHosts =
    [
        new("4.55.1.1",
            new("YukkuriMovieMaker.dll", Guid.Parse("5c07056d-022e-4d0f-a83d-ae0fa3b393f5"), "30E0B5E81FAA292F7968F3702446E54B3F4A5D319E33CC2EB38F2EF843606B9C"),
            new("YukkuriMovieMaker.Plugin.dll", Guid.Parse("78fce2a0-1106-4489-a080-938ae746bf7e"), "4713BBE55855A39D3BB340A7B8AB204711529929C2C12A69F1BEE54A9A7F60FF"),
            new("YukkuriMovieMaker.Settings.dll", Guid.Parse("5ddcb7f0-06f1-449a-bbc2-ecd8b3257b3c"), "F9339259B6C28C987DA0CBD416D983715A84A149F9D99D2ACA76F37879E0D0DF")),
        new("4.56.1.0",
            new("YukkuriMovieMaker.dll", Guid.Parse("23e5b5b5-adcf-43b7-b976-b6b63f8dadea"), "90D5022E2F4B46631254FAF788A1DF8A420071D9A190644FCC29892EC44F439D"),
            new("YukkuriMovieMaker.Plugin.dll", Guid.Parse("ddaa2ae6-046b-450c-9e9d-0c4e30ba9251"), "B99938260AA96A54DD2665D38A5A26889E70988CF8CC6293CD0EFDC88A81C0DF"),
            new("YukkuriMovieMaker.Settings.dll", Guid.Parse("88a1cec9-69bc-43bd-82ad-c9f3417cd271"), "8CB040510EF5E49289B47D60CF08E337CD9235D9577E49728D18404384CB22DA")),
    ];

    private static string KnownVersions => string.Join(" / ", KnownHosts.Select(known => known.Version));

    internal static bool VerifyHost(Assembly host, out string reason) => VerifyHost(host, out _, out reason);

    internal static bool VerifyHost(Assembly host, out string version, out string reason)
    {
        version = string.Empty;
        try
        {
            if (host.GetName().Name != "YukkuriMovieMaker") throw new NotSupportedException("Unexpected host assembly");
            var known = KnownHosts.FirstOrDefault(candidate => candidate.Host.Mvid == host.ManifestModule.ModuleVersionId)
                ?? throw new NotSupportedException($"読み込まれた YukkuriMovieMaker.dll は未検証の版です（検証済み: YMM4 {KnownVersions}）");
            var directory = Path.GetDirectoryName(Path.GetFullPath(host.Location))!;
            VerifyBinary(host, directory, known.Host);
            var loadContext = AssemblyLoadContext.GetLoadContext(host)!;
            VerifyBinary(FindOrLoad(loadContext, directory, "YukkuriMovieMaker.Plugin"), directory, known.Plugin);
            VerifyBinary(FindOrLoad(loadContext, directory, "YukkuriMovieMaker.Settings"), directory, known.Settings);
            version = known.Version;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetBaseException().Message;
            return false;
        }
    }

    private static string HostVersion(Assembly host) => host.GetName().Version?.ToString() ?? "?";

    // Where TryMatchReadBuild keeps its verdict (probes point it elsewhere).
    internal static string VerdictFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4-RTX3060-NVENC", "host-contracts.json");

    internal static string PluginIdentity => typeof(HostIntegration).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    internal static HostFeatures FeaturesFrom(HostContracts.Evaluation evaluation) => new(evaluation.Baseline ?? "?",
        evaluation.Has(HostContracts.Preview),
        evaluation.Has(HostContracts.SelectionRects),
        evaluation.Has(HostContracts.WrappedSources),
        evaluation.Has(HostContracts.RulerBars),
        evaluation.Features.Where(f => f.StartsWith(HostContracts.DecoderPrefix, StringComparison.Ordinal))
            .Select(f => f[HostContracts.DecoderPrefix.Length..]).ToHashSet(StringComparer.Ordinal))
        { SimpleTachie = evaluation.Has(HostContracts.SimpleTachie), LipSync = evaluation.Has(HostContracts.LipSync),
            AnimationTachie = evaluation.Has(HostContracts.AnimationTachie), PsdTachie = evaluation.Has(HostContracts.PsdTachie) };

    // HostContracts against the read builds. The verdict is kept per set of host binaries (and plugin build), so
    // only the first start after a YMM4 update spends the few seconds of reading them.
    internal static bool TryMatchReadBuild(Assembly host, out HostFeatures features, out string detail)
    {
        features = null!;
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(host.Location))!;
            if (HostFingerprint.ReadMvid(host.Location) != host.ManifestModule.ModuleVersionId)
            {
                detail = "読み込まれた YukkuriMovieMaker.dll がフォルダーのファイルと異なります。";
                return false;
            }
            var evaluation = HostContracts.EvaluateCached(directory, VerdictFile, PluginIdentity);
            if (evaluation.Baseline is null)
            {
                detail = "キャッシュが前提とする本体のコードが、検証済みの版と異なります"
                    + (evaluation.Problems.TryGetValue(HostContracts.Core, out var core) ? $"（{core}）。" : "。");
                return false;
            }
            features = FeaturesFrom(evaluation);
            detail = evaluation.Problems.Count == 0 ? string.Empty
                : "使わない機能: " + string.Join(" / ", evaluation.Problems.Select(p => $"{p.Key}（{p.Value}）"));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            detail = "本体のコードを照合できませんでした: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private static Assembly FindOrLoad(AssemblyLoadContext context, string directory, string name) =>
        context.Assemblies.SingleOrDefault(a => a.GetName().Name == name)
        ?? context.LoadFromAssemblyPath(Path.Combine(directory, name + ".dll"));

    private static void VerifyBinary(Assembly assembly, string directory, KnownBinary expected)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(directory, expected.File));
        if (!string.Equals(Path.GetFullPath(assembly.Location), expectedPath, StringComparison.OrdinalIgnoreCase)
            || assembly.ManifestModule.ModuleVersionId != expected.Mvid)
            throw new NotSupportedException($"読み込まれた {expected.File} は未検証の版です（検証済み: YMM4 {KnownVersions}）");
        using var stream = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(stream)) != expected.Sha256)
            throw new NotSupportedException($"{expected.File} のSHA-256が検証済みの版と一致しません（検証済み: YMM4 {KnownVersions}）");
    }
}
