# GUI smoke test on a CI runner (Windows PowerShell 5.1): starts a YMM4 copy with this plugin installed, opens a
# project, opens the plugin's frame cache tool, lets the idle pre-renderer work, and prints screenshots (JPEG,
# base64 between "=====SHOT" and "=====END" lines) plus the window and UI texts it finds. Never run it against a
# YMM4 someone uses: it writes settings and plugins into the given YMM4 folder and kills the process at the end.
param(
    [Parameter(Mandatory)] [string] $HostDir,
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $PluginDir,
    [int] $SettleSeconds = 60
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, WindowsBase, UIAutomationClient, UIAutomationTypes
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
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
    }
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

# The cache tool's counters (reuse by path, renders, stored preview frames, disk reads/writes).
function Counts([string] $label) {
    foreach ($window in Windows-Of $process) {
        $condition = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        $ae::FromHandle($window.Handle).FindAll($scope::Descendants, $condition) | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -match (U '\u518D\u5229\u7528|\u4FDD\u5B58|\u5148\u8AAD\u307F') } | ForEach-Object { Write-Output "counts ($label): $_" }
    }
}

function List-Windows($process) {
    Windows-Of $process | ForEach-Object { Write-Output ("window: [{0}] '{1}' {2}" -f $_.Class, $_.Title, $_.Rect) }
}

# Message boxes (e.g. "associate the YMM4 file extensions?"): answer No, or the only button there is.
function Answer-Dialogs($process) {
    foreach ($window in @(Windows-Of $process) | Where-Object { $_.Class -eq '#32770' }) {
        $dialog = $ae::FromHandle($window.Handle)
        Texts $dialog 'dialog'
        $buttons = @($dialog.FindAll($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))))
        $button = $buttons | Where-Object { $_.Current.Name -match '^&?No' } | Select-Object -First 1
        if (-not $button -and $buttons.Count -eq 1) { $button = $buttons[0] }
        if ($button) {
            Write-Output "answering '$($window.Title)' with '$($button.Current.Name)'"
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } else {
            Write-Output "answering '$($window.Title)' with IDNO"
            [Win]::PostMessage($window.Handle, 0x0111, [IntPtr]7, [IntPtr]::Zero) | Out-Null   # WM_COMMAND, IDNO
        }
    }
}

# The first element with this name that is on screen, in any window of the process (menus are windows of their own).
function Find-Visible($process, [string] $name) {
    $named = New-Object System.Windows.Automation.PropertyCondition ($ae::NameProperty, $name)
    foreach ($window in @(Windows-Of $process) | Sort-Object Area) {
        foreach ($element in $ae::FromHandle($window.Handle).FindAll($scope::Descendants, $named)) {
            if (-not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty) { return $element }
        }
    }
    return $null
}

function Click-At([int] $x, [int] $y, [string] $what) {
    $element = $null
    try { $element = $ae::FromPoint((New-Object System.Windows.Point $x, $y)) } catch { }
    $under = if ($element) { "$($element.Current.ControlType.ProgrammaticName) '$($element.Current.Name)' $($element.Current.ClassName)" } else { '?' }
    Write-Output "click $what at $x,$y (under the cursor: $under)"
    [Win]::Click($x, $y)
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
# YMM4's own settings for a first start: this version was already seen (no "about" window) and the file extension
# question was answered (no message box). Everything else keeps YMM4's defaults.
[IO.File]::WriteAllText((Join-Path $settings 'YukkuriMovieMaker.Settings.YMMSettings.json'),
    '{"Version":"' + $version + '","IsYMMPAssociationChecked":true,"IsYMMTAssociationChecked":true,"IsYMMEAssociationChecked":true}')
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
        List-Windows $process
        # The main window is titled "YukkuriMovieMaker v<version> ..."; wait until the project has loaded.
        $loading = $windows | Where-Object { $_.Title -match '^Loading' }
        $candidate = $windows | Where-Object { -not $loading -and $_.Class -like 'HwndWrapper*' -and $_.Title -match '^YukkuriMovieMaker v' } |
            Sort-Object Area -Descending | Select-Object -First 1
        # The first start shows the "about" window (ShowDialog) before the main window.
        foreach ($window in $windows | Where-Object { -not $candidate -and $_.Class -like 'HwndWrapper*' -and $_.Title -match '^About' }) {
            Write-Output "closing '$($window.Title)'"
            [Win]::PostMessage($window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        Answer-Dialogs $process
        if ($candidate -and @($windows | Where-Object { $_.Class -eq '#32770' }).Count -ne 0) { continue }
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
    # A message box can still come after the main window.
    for ($i = 0; $i -lt 4; $i++) { Start-Sleep -Seconds 2; Answer-Dialogs $process }
    [Win]::SetForegroundWindow($main.Handle) | Out-Null
    Start-Sleep -Seconds 1
    List-Windows $process
    Shot 'opened'

    # Open the plugin's tool from the tools menu. The menu entry's name is on a text inside the menu item.
    $before = @(Windows-Of $process | ForEach-Object { $_.Handle })
    $root = $ae::FromHandle($main.Handle)
    $menuItems = $root.FindAll($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
    $menuItems | ForEach-Object { Write-Output "menu: '$($_.Current.Name)'" }
    $tools = $menuItems | Where-Object { $_.Current.Name -like "*$toolMenu*" -or $_.Current.Name -match '^_?Tools?\b' } | Select-Object -First 1
    if ($tools) {
        $tools.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Seconds 2
        $entry = Find-Visible $process $cacheTool
        if ($entry) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $item = $entry
            while ($item -and $item.Current.ControlType -ne [System.Windows.Automation.ControlType]::MenuItem) { $item = $walker.GetParent($item) }
            $pattern = $null
            if ($item -and $item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                Write-Output "invoking the tool's menu item ($($item.Current.ClassName))"
                $pattern.Invoke()
            } else {
                $r = $entry.Current.BoundingRectangle
                Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) "the tool's menu entry"
            }
        } else { Write-Output 'tool menu entry not found' }
    } else { Write-Output 'tools menu not found' }
    Start-Sleep -Seconds 3
    # Close the menu if it is still open (a small window without a title).
    if (@(Windows-Of $process | Where-Object { $_.Title -eq '' -and $_.Area -lt 200000 -and $_.Class -like 'HwndWrapper*' }).Count -ne 0) {
        Write-Output 'menu still open: Escape'
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Seconds 1
    }
    # The tool opens as a window of its own: put it over the item property pane (right), off the preview and timeline.
    $tool = Windows-Of $process | Where-Object { $before -notcontains $_.Handle -and $_.Class -like 'HwndWrapper*' -and $_.Area -gt 10000 } |
        Sort-Object Area -Descending | Select-Object -First 1
    if ($tool) {
        Write-Output "tool window: '$($tool.Title)' $($tool.Rect)"
        [Win]::MoveWindow($tool.Handle, 1196, 40, 404, 790, $true) | Out-Null
    } else { Write-Output 'no new tool window' }
    Start-Sleep -Seconds 3
    List-Windows $process
    Shot 'tool-opened'

    # Select the Layer 00 item (on screen at the playhead, so the preview draws its selection rectangle), then leave
    # YMM4 alone: the idle pre-renderer fills the cache ahead of the playhead and the bars turn green/blue.
    Click-At 180 546 'the Layer 00 item'
    Start-Sleep -Seconds 20
    Shot 'prerendering'
    Start-Sleep -Seconds ([Math]::Max(5, $SettleSeconds - 20))
    Shot 'settled'
    # Seek to 00:00:05 on the ruler: that frame (both rectangles and the text) comes from the cache.
    Click-At 250 518 'the ruler at 5 s'
    Start-Sleep -Seconds 4
    Shot 'seek'
    Counts 'after the seek'

    # Normal playback beyond the pre-rendered 10 s: seek to 16 s and play at once (before the pre-renderer's idle
    # delay), so the host renders these frames and the cache stores them; then the same range again.
    foreach ($pass in 1, 2) {
        Click-At 580 518 'the ruler at 16 s'
        Start-Sleep -Milliseconds 300
        [Win]::SetForegroundWindow($main.Handle) | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        Start-Sleep -Seconds 3
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        Start-Sleep -Seconds 2
        Shot "playback-$pass"
        Counts "after playback pass $pass"
    }
    List-Windows $process
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
