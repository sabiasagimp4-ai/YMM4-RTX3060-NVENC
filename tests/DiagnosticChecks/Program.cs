using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using NVEncVideoWriterPlugin;

const string secret = "SECRET_SENTINEL_NEVER_SEND";
var environment = new DiagnosticEnvironment(new(4, 56, 1, 0), Guid.NewGuid(), "0.2.0-preview.3", Guid.NewGuid(),
    new(10, 0, 22631), new(10, 0, 1), Architecture.X64, DiagnosticFeatures.Preview | DiagnosticFeatures.Export, true, true, true,
    new(0x10de, 0x2504, 12288, false));
var empty = DiagnosticReports.Build(DiagnosticReports.Snapshot(environment));
Check(empty.Detail.Contains("No captured problems"), "Manual environment report unavailable");
DiagnosticReports.RecordException(DiagnosticComponent.EncoderVideo, new OperationCanceledException(secret));
DiagnosticReports.RecordException(DiagnosticComponent.Idle, new AggregateException(new OperationCanceledException(secret)));
DiagnosticReports.RecordException(DiagnosticComponent.Host, new OutOfMemoryException(secret));
Check(DiagnosticReports.Snapshot(environment).Events.IsEmpty, "Cancellation/fatal errors became reportable problems");

var error = new PoisonException();
error.Data[secret] = @"C:\Users\SECRET_SENTINEL_NEVER_SEND\private-project.ymmp";
DiagnosticFault.Emit(error);
var snapshot = DiagnosticReports.Snapshot(environment);
Check(snapshot.Events.Length == 1 && snapshot.Events[0].Calls.Length > 0 && snapshot.Events[0].Calls.Length <= DiagnosticReports.MaximumCalls, "Own call-site metadata missing or unbounded");
Check(snapshot.Events[0].ExceptionType == DiagnosticExceptionKind.ExternalException, "External type name leaked");
var document = DiagnosticReports.Build(snapshot);
Check(!document.Detail.Contains(secret) && !document.Detail.Contains("C:\\") && !document.Detail.Contains(nameof(PoisonException)), "Private exception content leaked");
Check(!document.Detail.Contains("Program.cs") && !document.Detail.Contains("DiagnosticFault"), "Names or source paths became public call-site text");
Check(DecodeBody(document.IssueUri) == document.GitHubBody, "Preview and GitHub payload differ");
Check(DiagnosticReports.IsAllowedIssueUri(document.IssueUri), "Generated report URI rejected");
Check(document.Detail.Contains("vendor=0x10de") && document.Detail.Contains("driver=not collected"), "Typed GPU details missing");
var signature = Signature(document.Detail);
for (int i = 0; i < 100_000; i++) DiagnosticFault.Emit(error);
snapshot = DiagnosticReports.Snapshot(environment);
Check(snapshot.Events.Length == 1 && snapshot.Events[0].Count == 100_001, "Repeated problem not aggregated");
Check(Signature(DiagnosticReports.Build(snapshot).Detail) == signature, "Occurrence count changed the stable signature");

var weak = RecordEphemeral();
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
Check(!weak.IsAlive, "Collector retained the exception or its attached data");
for (int i = 1; i <= 500; i++) DiagnosticReports.RecordException(DiagnosticComponent.CacheStore, new CodeException(i));
snapshot = DiagnosticReports.Snapshot(environment);
Check(snapshot.Events.Length == DiagnosticReports.MaximumEvents && snapshot.Dropped > 0, "Event cardinality unbounded");
Parallel.For(0, 10_000, i =>
{
    DiagnosticReports.RecordException(DiagnosticComponent.Host, new CodeException(i));
    if (i % 10 == 0) Check(DiagnosticReports.Snapshot(environment).Events.Length <= DiagnosticReports.MaximumEvents, "Concurrent snapshot overflowed");
});
snapshot = DiagnosticReports.Snapshot(environment);
Check(snapshot.Events.Length == DiagnosticReports.MaximumEvents && snapshot.Events.All(e => e.Count > 0), "Concurrent collection corrupted state");
document = DiagnosticReports.Build(snapshot);
Check(document.SummaryOnly && document.IssueUri.AbsoluteUri.Length <= DiagnosticReports.MaximumUriLength, "Large report did not use bounded summary");
Check(DecodeBody(document.IssueUri) == document.GitHubBody && document.Detail.Contains("own method token"), "Summary or full report lost");
// Build input is a snapshot; callers use the collector's deterministically ordered snapshot.
Check(DiagnosticReports.Snapshot(environment).Events.SequenceEqual(snapshot.Events), "Snapshot order is unstable");
foreach (string malformed in new[] { "0.2.0-" + secret, "0.2.0+" + secret, "0.2.0&body=" + secret, "0.2.0\n" + secret, "https://evil.invalid/" + secret, new string('a', 10000) })
{
    var forged = DiagnosticReports.Build(snapshot with { Environment = environment with { PluginVersion = malformed } });
    Check(!forged.Detail.Contains(secret) && !forged.GitHubBody.Contains(secret), "Malformed version leaked into report");
}
foreach (string unsafeUrl in new[] { "http://github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/issues/new", "https://github.com.evil.invalid/sabiasagimp4-ai/YMM4-RTX3060-NVENC/issues/new", "https://user@github.com/sabiasagimp4-ai/YMM4-RTX3060-NVENC/issues/new", DiagnosticReports.IssueEndpoint + "#private", "https://github.com/other/repo/issues/new", DiagnosticReports.IssueEndpoint + "?body=" + new string('x', 2000) })
    Check(!DiagnosticReports.IsAllowedIssueUri(new(unsafeUrl)), "Unsafe destination accepted");
Check(NvencErrors.IsExpectedCapabilityError("NVENC driver too old: supports API 12.0, needs 13.0"), "Old driver should be an expected limit");
Check(NvencErrors.IsExpectedCapabilityError("NVENC codec unsupported: AV1"), "Unsupported codec should be an expected limit");
Check(!NvencErrors.IsExpectedCapabilityError("nvEncEncodePicture failed status=20"), "Unexpected native failure suppressed");
Console.WriteLine("DIAGNOSTIC_CHECKS: privacy, no raw messages/data/names/paths, cancellation, weak lifetime, 100000 duplicates, bounded/concurrent collection, exact preview payload, URI guard/summary, typed versions passed");

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string Signature(string text) => text.Split('\n').Single(s => s.StartsWith("Signature:"));
static string DecodeBody(Uri uri) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(s => s.StartsWith("body="))[5..]);
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference RecordEphemeral()
{
    var ex = new CodeException(-7);
    ex.Data["large"] = new byte[1024 * 1024];
    DiagnosticReports.RecordException(DiagnosticComponent.Settings, ex);
    return new(ex);
}
sealed class PoisonException : Exception
{
    public override string Message => throw new InvalidOperationException("Message must never be read");
    public override string? StackTrace => @"C:\Users\SECRET_SENTINEL_NEVER_SEND\secret.cs:line 1";
    public override string ToString() => throw new InvalidOperationException("ToString must never be read");
}
sealed class CodeException : Exception { internal CodeException(int code) => HResult = code; }

namespace NVEncVideoWriterPlugin
{
    internal static class DiagnosticFault
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void Emit(Exception error)
        {
            try { throw error; }
            catch (Exception caught) { DiagnosticReports.RecordException(DiagnosticComponent.EncoderVideo, caught); }
        }
    }
}
