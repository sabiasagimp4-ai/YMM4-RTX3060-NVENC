# GUI smoke test on a CI runner (Windows PowerShell 5.1): starts a YMM4 copy with this plugin installed, opens a
# project, opens the plugin's frame cache tool, lets the idle pre-renderer work, and prints screenshots (JPEG,
# base64 between "=====SHOT" and "=====END" lines) plus the window and UI texts it finds. Never run it against a
# YMM4 someone uses: it writes settings and plugins into the given YMM4 folder and kills the process at the end.
param(
    [Parameter(Mandatory)] [string] $HostDir,
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $PluginDir,
    [int] $SettleSeconds = 30
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Win
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> Windows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q); if (q == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static int Area(IntPtr h) { RECT r; GetWindowRect(h, out r); return (r.Right - r.Left) * (r.Bottom - r.Top); }
    public static string Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top); }
}
'@
$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
function U([string] $escaped) { [regex]::Unescape($escaped) }
$cacheTool = U '\u63CF\u753B\u30AD\u30E3\u30C3\u30B7\u30E5'   # the tool's name
$toolMenu = U '\u30C4\u30FC\u30EB'   # the tools menu

function Shot([string] $name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $graphics.Dispose()
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $parameters = New-Object System.Drawing.Imaging.EncoderParameters 1
    $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 85L
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, $codec, $parameters)
    $bitmap.Dispose()
    $base64 = [Convert]::ToBase64String($stream.ToArray())
    Write-Output "=====SHOT $name $($bounds.Width)x$($bounds.Height) $($stream.Length)"
    for ($i = 0; $i -lt $base64.Length; $i += 4000) { Write-Output ('B64 ' + $base64.Substring($i, [Math]::Min(4000, $base64.Length - $i))) }
    Write-Output "=====END $name"
}

function Windows-Of($process) {
    foreach ($handle in [Win]::Windows([uint32]$process.Id)) {
        [pscustomobject]@{ Handle = $handle; Title = [Win]::Text($handle); Class = [Win]::Class($handle); Rect = [Win]::Rect($handle); Area = [Win]::Area($handle) }
    }
}

function Texts($element, [string] $label) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = $element.FindAll($scope::Descendants, $condition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
    Write-Output "--- texts ($label): $(@($texts).Count)"
    $texts | Select-Object -First 200 | ForEach-Object { Write-Output "  $_" }
}

# Install the plugin and turn the cache on (FrameCacheToolSettings).
$pluginTarget = Join-Path $HostDir 'user\plugin\YMM4Rtx3060Nvenc'
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
foreach ($file in 'YMM4Rtx3060Nvenc.dll', '0Harmony.dll', 'NvencNative.dll') {
    $source = Join-Path $PluginDir $file
    if (Test-Path $source) { Copy-Item $source $pluginTarget -Force }
}
$version = (Get-Item (Join-Path $HostDir 'YukkuriMovieMaker.dll')).VersionInfo.FileVersion
$settings = Join-Path $HostDir "user\setting\$version"
New-Item -ItemType Directory -Path $settings -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $settings 'NVEncVideoWriterPlugin.FrameCacheToolSettings.json'), '{"Enabled":true}')
try { Set-DisplayResolution -Width 1600 -Height 900 -Force -ErrorAction Stop } catch { Write-Output "resolution unchanged: $($_.Exception.Message)" }
Write-Output "screen: $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds)"

$process = Start-Process (Join-Path $HostDir 'YukkuriMovieMaker.exe') -ArgumentList "`"$Project`"" -PassThru
try {
    # Wait for the main window; close dialogs (message boxes, the first-run "about" window) on the way.
    $main = $null
    $deadline = (Get-Date).AddSeconds(150)
    $shotAt = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Seconds 2
        $windows = @(Windows-Of $process)
        $windows | ForEach-Object { Write-Output ("window: [{0}] '{1}' {2}" -f $_.Class, $_.Title, $_.Rect) }
        $candidate = $windows | Where-Object { $_.Title -match 'gui-smoke' } | Sort-Object Area -Descending | Select-Object -First 1
        foreach ($window in $windows | Where-Object { $_.Class -eq '#32770' }) {
            Texts ($ae::FromHandle($window.Handle)) 'dialog'
            [Win]::PostMessage($window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        if ($candidate) {
            foreach ($window in $windows | Where-Object { $_.Handle -ne $candidate.Handle -and $_.Area -gt 10000 -and $_.Class -like 'HwndWrapper*' }) {
                Write-Output "closing extra window '$($window.Title)'"
                [Win]::PostMessage($window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
            $main = $candidate
            break
        }
        if ((Get-Date) -gt $shotAt) { Shot 'waiting'; $shotAt = (Get-Date).AddSeconds(40) }
    }
    if ($process.HasExited) { throw "YMM4 exited with $($process.ExitCode)" }
    if (-not $main) { Shot 'no-main-window'; throw 'The main window with the project did not appear' }
    [Win]::ShowWindow($main.Handle, 3) | Out-Null   # maximize
    [Win]::SetForegroundWindow($main.Handle) | Out-Null
    Start-Sleep -Seconds 5
    Shot 'opened'

    # Open the plugin's tool from the tools menu.
    $root = $ae::FromHandle($main.Handle)
    $menuItems = $root.FindAll($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
    $menuItems | ForEach-Object { Write-Output "menu: '$($_.Current.Name)'" }
    $tools = $menuItems | Where-Object { $_.Current.Name -like "*$toolMenu*" } | Select-Object -First 1
    if ($tools) {
        $tools.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Seconds 2
        $entry = $ae::RootElement.FindFirst($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($ae::NameProperty, $cacheTool)))
        if ($entry) {
            Write-Output "tool menu entry: $($entry.Current.ControlType.ProgrammaticName)"
            $pattern = $null
            if ($entry.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
            elseif ($entry.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
        } else { Write-Output 'tool menu entry not found' }
    } else { Write-Output 'tools menu not found' }
    Start-Sleep -Seconds 3
    Shot 'tool-opened'
    Get-Process -Id $process.Id | Out-Null
    (Windows-Of $process) | ForEach-Object { Write-Output ("window: [{0}] '{1}' {2}" -f $_.Class, $_.Title, $_.Rect) }

    # Idle: the pre-renderer fills the cache ahead of the playhead; the bars should turn green.
    Start-Sleep -Seconds $SettleSeconds
    Shot 'settled'
    foreach ($window in Windows-Of $process) { Texts ($ae::FromHandle($window.Handle)) $window.Title }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $logs = Join-Path $HostDir 'user\log'
    if (Test-Path $logs) {
        Get-ChildItem $logs -File | Sort-Object LastWriteTime | Select-Object -Last 2 | ForEach-Object {
            Write-Output "--- log $($_.Name)"
            Get-Content $_.FullName -Tail 80 -Encoding UTF8
        }
    }
}
