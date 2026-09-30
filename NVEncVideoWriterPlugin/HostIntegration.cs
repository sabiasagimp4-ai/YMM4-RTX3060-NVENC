using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;

namespace NVEncVideoWriterPlugin;

internal static class HostIntegration
{
    private const string PatchId = "sabiasagimp4-ai.ymm4-rtx3060-nvenc.host";
    private static readonly object installLock = new();
    private static readonly Harmony harmony = new(PatchId);
    private static readonly Harmony cacheHarmony = new(PatchId + ".cache");
    private static Assembly? checkedHost;
    private static bool installed;
    private static bool cacheAvailable;
    private static string status = "YMM4との連携はまだ初期化されていません。";

    internal static string Status { get { lock (installLock) return status; } }
    internal static bool CacheAvailable { get { lock (installLock) return cacheAvailable; } }
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
            try
            {
                if (!VerifyHost(host, out var reason)) throw new NotSupportedException(reason);
                if (!HostExportScope.TryInstall(host, harmony, out reason)) throw new NotSupportedException(reason);
                installed = true;
                cacheAvailable = TimelineFrameCache.TryInstall(host, cacheHarmony, out reason);
                if (!cacheAvailable) cacheHarmony.UnpatchAll(cacheHarmony.Id);
                status = cacheAvailable
                    ? "YMM4 4.55.1.1: 取消保護・自動キャッシュの接続を確認しました。"
                    : "取消保護は有効です。自動キャッシュは利用できません: " + reason;
                return true;
            }
            catch (Exception ex)
            {
                installed = false;
                cacheAvailable = false;
                status = $"YMM4との連携を無効にしました: {ex.GetBaseException().Message}";
                try { harmony.UnpatchAll(PatchId); cacheHarmony.UnpatchAll(cacheHarmony.Id); }
                catch (Exception rollback) { status += $"（フックの解除にも失敗しました: {rollback.GetBaseException().Message}）"; }
                return false;
            }
        }
    }

    internal static void RequireExportScope()
    {
        // Direct/offline callers have no host cancellation contract. The real host must have one.
        if (!IsHostProcess) return;
        if (!EnsureInstalled())
            throw new NotSupportedException($"このYMM4では安全な動画出力を開始できません。{Status}");
        if (HostExportScope.GetCurrent() is null)
            throw new InvalidOperationException("YMM4の出力範囲・取消状態を取得できなかったため、既存ファイルを保護して出力を中止しました。");
    }

    internal static bool VerifyHost(Assembly host, out string reason)
    {
        try
        {
            if (host.GetName().Name != "YukkuriMovieMaker") throw new NotSupportedException("Unexpected host assembly");
            var directory = Path.GetDirectoryName(Path.GetFullPath(host.Location))!;
            VerifyBinary(host, directory, "YukkuriMovieMaker.dll", "5c07056d-022e-4d0f-a83d-ae0fa3b393f5",
                "30E0B5E81FAA292F7968F3702446E54B3F4A5D319E33CC2EB38F2EF843606B9C");
            var loadContext = AssemblyLoadContext.GetLoadContext(host)!;
            var plugin = FindOrLoad(loadContext, directory, "YukkuriMovieMaker.Plugin");
            var settings = FindOrLoad(loadContext, directory, "YukkuriMovieMaker.Settings");
            VerifyBinary(plugin, directory, "YukkuriMovieMaker.Plugin.dll", "78fce2a0-1106-4489-a080-938ae746bf7e",
                "4713BBE55855A39D3BB340A7B8AB204711529929C2C12A69F1BEE54A9A7F60FF");
            VerifyBinary(settings, directory, "YukkuriMovieMaker.Settings.dll", "5ddcb7f0-06f1-449a-bbc2-ecd8b3257b3c",
                "F9339259B6C28C987DA0CBD416D983715A84A149F9D99D2ACA76F37879E0D0DF");
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetBaseException().Message;
            return false;
        }
    }

    private static Assembly FindOrLoad(AssemblyLoadContext context, string directory, string name) =>
        context.Assemblies.SingleOrDefault(a => a.GetName().Name == name)
        ?? context.LoadFromAssemblyPath(Path.Combine(directory, name + ".dll"));

    private static void VerifyBinary(Assembly assembly, string directory, string file, string mvid, string sha256)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(directory, file));
        if (!string.Equals(Path.GetFullPath(assembly.Location), expectedPath, StringComparison.OrdinalIgnoreCase)
            || assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid))
            throw new NotSupportedException($"読み込まれた {file} は未検証の版です（検証済み: YMM4 4.55.1.1）");
        using var stream = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(stream)) != sha256)
            throw new NotSupportedException($"{file} のSHA-256が検証済みの版と一致しません（検証済み: YMM4 4.55.1.1）");
    }
}
