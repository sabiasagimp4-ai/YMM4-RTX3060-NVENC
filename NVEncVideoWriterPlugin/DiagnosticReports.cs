using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace NVEncVideoWriterPlugin;

internal enum DiagnosticComponent { Host, ExportHook, CacheHook, CacheRead, CacheStore, CacheRestore, Idle, EncoderAudio, EncoderVideo, EncoderFinish, Settings, Diagnostics }
internal enum DiagnosticKind { ContractMismatch, FeatureUnavailable, HookFailure, UnexpectedException }
internal enum DiagnosticExceptionKind { None, ArgumentException, InvalidOperationException, IOException, UnauthorizedAccessException, NotSupportedException, ObjectDisposedException, TimeoutException, COMException, NullReferenceException, TypeLoadException, MissingMemberException, AggregateException, ExternalException }
[Flags]
internal enum DiagnosticFeatures { None = 0, Preview = 1, Selection = 2, WrappedSources = 4, Ruler = 8, SimpleTachie = 16, LipSync = 32, AnimationTachie = 64, PsdTachie = 128, Export = 256 }
internal sealed record DiagnosticGpu(uint Vendor, uint Device, long DedicatedMiB, bool Software);
internal sealed record DiagnosticEnvironment(Version? HostVersion, Guid HostMvid, string PluginVersion, Guid PluginMvid,
    Version WindowsVersion, Version RuntimeVersion, Architecture Architecture, DiagnosticFeatures Features,
    bool PreviewEnabled, bool ExportEnabled, bool NvencEnabled, DiagnosticGpu? Gpu);
internal sealed record DiagnosticEvent(DiagnosticComponent Component, DiagnosticKind Kind, DiagnosticExceptionKind ExceptionType, int HResult,
    long Count, ImmutableArray<int> Calls);
internal sealed record DiagnosticSnapshot(DiagnosticEnvironment Environment, ImmutableArray<DiagnosticEvent> Events, long Dropped);
internal sealed record DiagnosticDocument(string Detail, string GitHubBody, Uri IssueUri, bool SummaryOnly);

// Session-only, bounded and never retains an Exception, scene, device, raw message or file path.
internal static class DiagnosticReports
{
    internal const int MaximumEvents = 32, MaximumCalls = 8, MaximumUriLength = 1800;
    internal const string IssueEndpoint = "https://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/issues/new";
    private readonly record struct Key(DiagnosticComponent Component, DiagnosticKind Kind, DiagnosticExceptionKind Type, int HResult);
    private static readonly object gate = new();
    private static readonly Dictionary<Key, DiagnosticEvent> events = new();
    private static long dropped;

    internal static void RecordUnavailable(DiagnosticComponent component, DiagnosticKind kind) => Record(component, kind, null);
    internal static void RecordException(DiagnosticComponent component, Exception error) => Record(component, DiagnosticKind.UnexpectedException, error);
    private static void Record(DiagnosticComponent component, DiagnosticKind kind, Exception? error)
    {
        try
        {
            if (!Enum.IsDefined(component) || !Enum.IsDefined(kind)) return;
            for (int depth = 0; depth < 4 && error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1; depth++)
                error = aggregate.InnerExceptions[0];
            if (error is OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException) return;
            var type = error switch
            {
                null => DiagnosticExceptionKind.None,
                ObjectDisposedException => DiagnosticExceptionKind.ObjectDisposedException,
                ArgumentException => DiagnosticExceptionKind.ArgumentException,
                InvalidOperationException => DiagnosticExceptionKind.InvalidOperationException,
                System.IO.IOException => DiagnosticExceptionKind.IOException,
                UnauthorizedAccessException => DiagnosticExceptionKind.UnauthorizedAccessException,
                NotSupportedException => DiagnosticExceptionKind.NotSupportedException,
                TimeoutException => DiagnosticExceptionKind.TimeoutException,
                COMException => DiagnosticExceptionKind.COMException,
                NullReferenceException => DiagnosticExceptionKind.NullReferenceException,
                TypeLoadException => DiagnosticExceptionKind.TypeLoadException,
                MissingMemberException => DiagnosticExceptionKind.MissingMemberException,
                AggregateException => DiagnosticExceptionKind.AggregateException,
                _ => DiagnosticExceptionKind.ExternalException,
            };
            var key = new Key(component, kind, type, error?.HResult ?? 0);
            // Snapshot only copies at most 32 immutable entries; writers never wait for it or each other.
            if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref dropped); return; }
            try
            {
                if (events.TryGetValue(key, out var existing))
                { events[key] = existing with { Count = existing.Count == long.MaxValue ? long.MaxValue : existing.Count + 1 }; return; }
                if (events.Count >= MaximumEvents) { Interlocked.Increment(ref dropped); return; }
                var calls = ImmutableArray.CreateBuilder<int>();
                if (error is not null)
                {
                    var stack = new StackTrace(error, fNeedFileInfo: false);
                    for (int i = 0; i < stack.FrameCount && calls.Count < MaximumCalls; i++)
                    {
                        var method = stack.GetFrame(i)?.GetMethod();
                        if (method is null || method.DeclaringType?.Namespace != typeof(DiagnosticReports).Namespace
                            || method.Module.Assembly != typeof(DiagnosticReports).Assembly) continue;
                        calls.Add(method.MetadataToken);
                    }
                }
                events.Add(key, new(component, kind, type, key.HResult, 1, calls.ToImmutable()));
            }
            finally { Monitor.Exit(gate); }
        }
        catch { } // Optional diagnostics cannot replace the original error or change its recovery.
    }

    internal static DiagnosticSnapshot Snapshot(DiagnosticEnvironment environment)
    {
        lock (gate) return new(environment, events.Values.OrderBy(v => v.Component).ThenBy(v => v.Kind)
            .ThenBy(v => v.ExceptionType).ThenBy(v => v.HResult).ToImmutableArray(), Interlocked.Read(ref dropped));
    }

    internal static DiagnosticDocument Build(DiagnosticSnapshot snapshot)
    {
        var e = snapshot.Environment;
        string plugin = SafeVersion(e.PluginVersion);
        string host = e.HostVersion?.ToString() ?? "unknown";
        string primary = snapshot.Events.IsEmpty ? "Manual" : snapshot.Events[0].Component + "/" + snapshot.Events[0].Kind;
        string identity = $"{host}|{e.HostMvid:N}|{plugin}|{e.PluginMvid:N}|{primary}|" + string.Join(";", snapshot.Events.Select(v => $"{v.Component}/{v.Kind}/{v.ExceptionType}/{v.HResult}"));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var body = new StringBuilder();
        body.AppendLine("## Diagnostic report / 診断レポート");
        body.AppendLine($"Signature: `{fingerprint}`");
        body.AppendLine("```text");
        body.AppendLine($"YMM4: {host}; MVID: {e.HostMvid:N}");
        body.AppendLine($"Plugin: {plugin}; MVID: {e.PluginMvid:N}");
        body.AppendLine($"Windows: {e.WindowsVersion}; .NET: {e.RuntimeVersion}; Architecture: {(Enum.IsDefined(e.Architecture) ? e.Architecture.ToString() : "unknown")}");
        body.AppendLine($"Features: {((int)e.Features & 511):x3}; preview={e.PreviewEnabled}; export={e.ExportEnabled}; nvenc={e.NvencEnabled}");
        body.AppendLine(e.Gpu is { } gpu && gpu.DedicatedMiB >= 0 && gpu.DedicatedMiB <= 1_048_576
            ? $"GPU: vendor=0x{gpu.Vendor:x4}; device=0x{gpu.Device:x4}; dedicated={gpu.DedicatedMiB} MiB; software={gpu.Software}; driver=not collected"
            : "GPU: not observed; driver=not collected");
        body.AppendLine($"Dropped diagnostic records: {Math.Max(0, snapshot.Dropped)}");
        if (snapshot.Events.IsEmpty) body.AppendLine("No captured problems. Add symptoms and reproduction steps below.");
        foreach (var v in snapshot.Events)
        {
            body.AppendLine($"{v.Component}/{v.Kind}: {v.ExceptionType}; HRESULT=0x{v.HResult:x8}; count={v.Count}");
            foreach (int call in v.Calls) body.AppendLine($"  own method token: 0x{call:x8}");
        }
        body.AppendLine("```");
        body.AppendLine("Call tokens refer only to this plugin build. Paths, messages, project contents and raw logs are excluded.");
        body.AppendLine("\n## Symptoms / 症状\n\n## Steps to reproduce / 再現手順\n");
        string detail = body.ToString();
        string title = $"Diagnostic: YMM4 {host} / {primary}";
        Uri uri = MakeUri(title, detail);
        bool summaryOnly = uri.AbsoluteUri.Length > MaximumUriLength;
        string githubBody = summaryOnly
            ? $"Diagnostic signature: {fingerprint}\nYMM4: {host}\nPlugin: {plugin}\nProblem: {primary}\n\nPaste the reviewed diagnostic report here, then describe symptoms and steps to reproduce."
            : detail;
        if (summaryOnly) uri = MakeUri(title, githubBody);
        if (!IsAllowedIssueUri(uri)) throw new InvalidOperationException("Invalid diagnostic issue URL");
        return new(detail, githubBody, uri, summaryOnly);
    }

    internal static bool IsAllowedIssueUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "github.com" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == "/sabiasagimp4-ai/YMM4-RTX3060-NVENC/issues/new" && uri.AbsoluteUri.Length <= MaximumUriLength;
    private static Uri MakeUri(string title, string body) => new(IssueEndpoint + "?template=diagnostic-report.md&title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body));
    private static string SafeVersion(string value) => value is { Length: > 0 and <= 64 }
        && System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^[0-9]{1,5}(\.[0-9]{1,5}){1,3}(-(preview|alpha|beta|rc)(\.[0-9]{1,5})?)?(\+[0-9a-fA-F]{7,40})?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant) ? value : "unknown";
}
