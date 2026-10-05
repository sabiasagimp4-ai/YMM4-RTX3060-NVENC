# Version compatibility check on a CI runner (Windows PowerShell 5.1): starts a YMM4 copy with this plugin installed
# and records what the plugin enabled there (HostIntegration.ReportStatus writes it when YMM4_RTX3060_NVENC_STATUS_FILE
# is set), plus the windows and message boxes YMM4 showed. Writes one JSON object to -Out. Never run it against a YMM4
# someone uses: it writes settings and plugins into the given YMM4 folder and kills the process at the end.
param(
    [Parameter(Mandatory)] [string] $HostDir,
    [Parameter(Mandatory)] [string] $PluginDir,
    [Parameter(Mandatory)] [string] $Out,
    [int] $TimeoutSeconds = 150
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class StartWin
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr p);
    // The texts of a message box: its title, then each child (the message and the buttons), as "Class: text".
    public static string Describe(IntPtr h)
    {
        var parts = new List<string> { Text(h) };
        EnumChildWindows(h, (c, p) => { var t = Text(c); if (t.Length != 0) parts.Add(Class(c) + ": " + t); return true; }, IntPtr.Zero);
        return string.Join(" | ", parts);
    }
    public static List<IntPtr> Windows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q); if (q == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
}
'@
# Windows PowerShell reads this file in the system code page: non-ASCII text is written as escapes.
$aboutTitle = [regex]::Unescape('^About|\u30D0\u30FC\u30B8\u30E7\u30F3\u60C5\u5831')

# Install the plugin (every file of the package) and answer YMM4's first-start questions in its settings.
$target = Join-Path $HostDir 'user\plugin\YMM4Rtx3060Nvenc'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $PluginDir '*') $target -Recurse -Force
$version = (Get-Item (Join-Path $HostDir 'YukkuriMovieMaker.dll')).VersionInfo.FileVersion
$settings = Join-Path $HostDir "user\setting\$version"
New-Item -ItemType Directory -Path $settings -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $settings 'YukkuriMovieMaker.Settings.YMMSettings.json'),
    '{"Version":"' + $version + '","IsYMMPAssociationChecked":true,"IsYMMTAssociationChecked":true,"IsYMMEAssociationChecked":true}')

$statusFile = Join-Path $env:RUNNER_TEMP "ymm4-status-$version.json"
Remove-Item $statusFile -ErrorAction SilentlyContinue
$env:YMM4_RTX3060_NVENC_STATUS_FILE = $statusFile
$result = [ordered]@{ fileVersion = $version; started = $false; mainWindow = $false; status = $null; exitCode = $null
    windows = New-Object System.Collections.Generic.List[string]; dialogs = New-Object System.Collections.Generic.List[string] }
$seen = @{}
$process = Start-Process (Join-Path $HostDir 'YukkuriMovieMaker.exe') -PassThru
$result.started = $true
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $settleUntil = $null
    $mainSince = $null
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Seconds 2
        foreach ($handle in [StartWin]::Windows([uint32]$process.Id)) {
            $title = [StartWin]::Text($handle); $class = [StartWin]::Class($handle)
            $key = "[$class] $title"
            if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $result.windows.Add($key); Write-Output "window: $key" }
            if ($class -like 'HwndWrapper*' -and $title -match '^YukkuriMovieMaker v') { $result.mainWindow = $true }
            if ($class -eq '#32770') {
                # A message box (plugin load errors are shown this way): keep its texts. It is left open: which answer
                # a box needs is not known here, and answering one can end YMM4 or start its updater.
                $text = [StartWin]::Describe($handle)
                if ($text -and -not $result.dialogs.Contains($text)) { $result.dialogs.Add($text); Write-Output "dialog: $text" }
            } elseif ($class -like 'HwndWrapper*' -and $title -match $aboutTitle) {
                [StartWin]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
        }
        if ((Test-Path $statusFile) -and -not $settleUntil) { $settleUntil = (Get-Date).AddSeconds(5) }
        # After the status, wait a little for the main window and for message boxes that follow the plugin load.
        if ($settleUntil -and ((Get-Date) -gt $settleUntil -or $result.mainWindow)) { break }
        if ($result.mainWindow -and -not $settleUntil -and -not $mainSince) { $mainSince = Get-Date }
        if ($mainSince -and (Get-Date) -gt $mainSince.AddSeconds(30)) { break }   # main window but no status: the plugin did not load
    }
    if ($process.HasExited) { $result.exitCode = $process.ExitCode }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}
if (Test-Path $statusFile) { $result.status = Get-Content $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json }
$json = $result | ConvertTo-Json -Depth 6 -Compress
[IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Output $json
