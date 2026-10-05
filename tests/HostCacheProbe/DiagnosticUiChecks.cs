using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using NVEncVideoWriterPlugin;

internal static class DiagnosticUiChecks
{
    internal static DiagnosticEnvironment Environment => new(new(4, 56, 1, 0), Guid.Empty, "0.2.0-preview.3", Guid.Empty,
        new(10, 0), new(10, 0), Architecture.X64, DiagnosticFeatures.Preview, true, false, true, null);

    internal static void Run()
    {
        var document = DiagnosticReports.Build(DiagnosticReports.Snapshot(Environment));
        int copies = 0, saves = 0, browsers = 0;
        string? copied = null, saved = null, savePath = null;
        Uri? opened = null;
        bool failCopy = false, failSave = false, failBrowser = false;
        TaskCompletionSource? pending = null;
        var actions = new DiagnosticReportActions(
            text => { copies++; if (failCopy) throw new IOException("PRIVATE_COPY_PATH"); copied = text; },
            () => savePath,
            (path, text) => { saves++; Check(path == savePath, "Save destination changed"); saved = text;
                return failSave ? Task.FromException(new IOException("PRIVATE_SAVE_PATH")) : pending?.Task ?? Task.CompletedTask; },
            uri => { browsers++; if (failBrowser) throw new IOException("PRIVATE_BROWSER_PATH"); opened = uri; });
        var window = new DiagnosticReportWindow(document, actions);
        try
        {
            window.Show(); window.UpdateLayout();
            Check(copies + saves + browsers == 0, "Opening preview performed an external action");
            var detail = Find<TextBox>(window, "DiagnosticDetail");
            var body = Find<TextBox>(window, "DiagnosticGitHubBody");
            Check(detail.IsReadOnly && body.IsReadOnly && detail.Text == document.Detail && body.Text == document.GitHubBody,
                "Preview is editable or differs from the payload");
            var copy = Find<Button>(window, "DiagnosticCopy");
            var save = Find<Button>(window, "DiagnosticSave");
            var open = Find<Button>(window, "DiagnosticOpenGitHub");
            var feedback = Find<TextBlock>(window, "DiagnosticFeedback");
            Click(copy); Check(copied == document.Detail && copies == 1 && browsers == 0, "Copy also opened a browser or changed detail");
            failCopy = true; Click(copy); Check(feedback.Text.Contains("コピーできません"), "Clipboard failure escaped UI");
            Click(save); Check(saves == 0 && save.IsEnabled, "Cancelled save wrote a file or left button disabled");
            savePath = "fake-diagnostic.md"; Click(save);
            Check(saved == document.Detail && saves == 1 && save.IsEnabled, "Save did not preserve full detail");
            failSave = true; Click(save);
            Check(save.IsEnabled && feedback.Text.Contains("保存できません") && !feedback.Text.Contains("PRIVATE"), "Save error leaked or disabled the UI");
            failSave = false; pending = new(); Click(save);
            Check(!save.IsEnabled && !pending.Task.IsCompleted, "Save blocked or failed to await completion");
            pending.SetResult(); PumpUntil(() => save.IsEnabled);
            Click(open); Check(opened == document.IssueUri && browsers == 1 && DiagnosticReports.IsAllowedIssueUri(opened!), "Browser received another destination");
            failBrowser = true; Click(open);
            Check(feedback.Text.Contains("ブラウザーを開けません") && !feedback.Text.Contains("PRIVATE"), "Browser failure escaped or leaked paths");
            var invalid = new DiagnosticReportWindow(document with { IssueUri = new("https://evil.invalid/") }, actions);
            try { int before = browsers; Click(Find<Button>(invalid, "DiagnosticOpenGitHub")); Check(browsers == before, "Unsafe destination reached browser action"); }
            finally { invalid.Close(); }
            // Use the actual plugin view, rather than a second fake entry point.
            var tool = new FrameCacheToolView();
            Check(Find<Button>(tool, "DiagnosticReportOpen").Content?.ToString() == "問題を報告", "Production tool has no reporting entry");
            Click(Find<Button>(window, "DiagnosticClose")); Check(!window.IsVisible, "Close left the report open");
        }
        finally { window.Close(); }
        Console.WriteLine("DIAGNOSTIC_UI_CHECKS: read-only exact preview, no implicit external action, copy/save/cancel/async/failure, fixed browser destination, production tool entry passed (fake actions; no upload)");
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        T? Search(DependencyObject node)
        {
            if (node is T match && AutomationProperties.GetAutomationId(node) == id) return match;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                if (Search(child) is { } found) return found;
            return null;
        }
        return Search(root) ?? throw new InvalidOperationException("Missing UI control: " + id);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (done() || clock.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); Check(done(), "Async save did not resume the UI"); }
        finally { timer.Stop(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
