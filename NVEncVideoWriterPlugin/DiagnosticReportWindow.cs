using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace NVEncVideoWriterPlugin;

// Actions are supplied by the UI, never called by the collector or render/encoder threads.
internal sealed record DiagnosticReportActions(Action<string> Copy, Func<string?> ChooseSavePath,
    Func<string, string, Task> Save, Action<Uri> OpenBrowser)
{
    internal static DiagnosticReportActions Default => new(Clipboard.SetText,
        () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "ymm4-diagnostic.md", Filter = "Markdown (*.md)|*.md", DefaultExt = ".md", AddExtension = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        },
        (path, text) => File.WriteAllTextAsync(path, text, new UTF8Encoding(false)),
        uri =>
        {
            if (!DiagnosticReports.IsAllowedIssueUri(uri)) throw new InvalidOperationException("Invalid diagnostic issue URL");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        });
}

internal sealed class DiagnosticReportWindow : Window
{
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    internal DiagnosticReportWindow(DiagnosticDocument document, DiagnosticReportActions? actions = null)
    {
        actions ??= DiagnosticReportActions.Default;
        Title = "問題を報告"; Width = 720; Height = 720; MinWidth = 460; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "内容を確認してから報告してください。GitHub を開くと下の送信内容が GitHub に渡ります。投稿にはログインと、GitHub 上で最後の投稿操作が必要です。コピー・保存にはアカウントは不要です。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = "GitHub に渡す内容", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var github = Text(document.GitHubBody, "DiagnosticGitHubBody", 150);
        panel.Children.Add(github);
        if (document.SummaryOnly) panel.Children.Add(new TextBlock
        {
            Text = "長いため GitHub には要約を入れます。必要なら詳細レポートをコピーし、GitHub の本文へ貼り付けてください。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        });
        panel.Children.Add(new TextBlock { Text = "詳細レポート（コピー・保存用）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(Text(document.Detail, "DiagnosticDetail", 240));
        panel.Children.Add(new TextBlock
        {
            Text = "素材・プロジェクト本文・ファイルパス・例外メッセージ・生ログは含めません。症状と再現手順は GitHub で記入してください。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
        });
        var row = new WrapPanel();
        var copy = Button("詳細をコピー", "DiagnosticCopy");
        var save = Button("詳細を保存", "DiagnosticSave");
        var open = Button("GitHub の報告画面を開く", "DiagnosticOpenGitHub");
        var close = Button("閉じる", "DiagnosticClose");
        foreach (var button in new[] { copy, save, open, close }) row.Children.Add(button);
        panel.Children.Add(row); panel.Children.Add(feedback);
        AutomationProperties.SetAutomationId(feedback, "DiagnosticFeedback");
        copy.Click += (_, _) =>
        {
            try { actions.Copy(document.Detail); feedback.Text = "詳細をコピーしました。"; }
            catch { feedback.Text = "コピーできませんでした。本文を選択してコピーするか、ファイルへ保存してください。"; }
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                if (actions.ChooseSavePath() is { } path)
                { await actions.Save(path, document.Detail); feedback.Text = "詳細レポートを保存しました。"; }
            }
            catch { feedback.Text = "保存できませんでした。保存先のアクセス権と空き容量を確認してください。"; }
            finally { save.IsEnabled = true; }
        };
        open.Click += (_, _) =>
        {
            try
            {
                if (!DiagnosticReports.IsAllowedIssueUri(document.IssueUri)) throw new InvalidOperationException("Invalid issue URL");
                actions.OpenBrowser(document.IssueUri);
                feedback.Text = "GitHub で内容を確認し、投稿してください。同じ問題の Issue があれば、そちらへ追記できます。";
            }
            catch { feedback.Text = "ブラウザーを開けませんでした。詳細を保存し、リポジトリの Issues から報告してください。"; }
        };
        close.Click += (_, _) => Close();
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static TextBox Text(string value, string id, double height)
    {
        var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(box, id); return box;
    }
    private static Button Button(string label, string id)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetAutomationId(button, id); return button;
    }
}
