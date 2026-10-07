<#
.SYNOPSIS
Every check of this repository on one Windows PC with an NVIDIA GPU (RTX 3060), in one command. Writes
<WorkDir>\runs\<commit>\summary.md.

.DESCRIPTION
Four parts, after a common setup (YMM4 4.56.1.0 downloaded, the plugin package built like a release):
  latest    the checks of the cache-development workflow against YMM4 4.56.1.0, on this PC's GPU
  rtx       tools\rtx-check.ps1: NVENC smoke tests, throughput, color matrix, VUI experiment (-NoNvenc skips encoding)
  gui       YMM4 started with the plugin, a project opened, the frame cache tool used (tools\ci\gui-smoke.ps1), and the
            stress run with its trace checked. They click fixed screen positions: both need a 1600x900 primary
            screen at 100% scale, and are skipped on other screens. Do not use the PC while they run.
  versions  every YMM4 version the plugin supports (from YMM4's update server, oldest first, in one folder that
            downloads only the files that changed, as YMM4's updater does), like the ymm4-compat workflow: the files
            scanned, YMM4 started with the plugin (what it enabled), and the cache checks run as an unread build. The
            version table is rendered into the run folder, not into the repository.

Each step runs as its own process and the script keeps going after a failure, so one report shows every result. A
second run of the same commit resumes: steps that passed are taken from the earlier run (-Fresh starts over). YMM4 is
downloaded into <WorkDir>\ymm4 and never into the repository; nothing is committed, pushed or installed into a YMM4
someone uses. The working tree must have no local changes (the results are those of the commit). Two steps change
a tracked source file for a moment and restore it byte for byte, as the workflows do: the negative control (latest)
breaks a guard in NVEncVideoWriterPlugin\TimelineFrameCache.cs to see the checks catch it, and rtx-check's VUI
experiment patches NvencNative\NvencNative.cpp.

Needs: .NET SDK 10, Visual Studio Build Tools with the C++ workload, Git for Windows (its bash), jq, Python 3,
ffmpeg and ffprobe on PATH, about 20 GB free, and the NVIDIA driver (unless -NoNvenc).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\local-full-check.ps1

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\local-full-check.ps1 -Parts versions -Versions 4.55.1.1,4.54.0.1
#>
[CmdletBinding()]
param(
    [string] $WorkDir = 'C:\ymm4-full-check',
    [string[]] $Parts = @('latest', 'rtx', 'gui', 'versions'),
    [string[]] $Versions = @('all'),
    [switch] $NoNvenc,
    [ValidateRange(1, 10)] [int] $PreviewRuns = 3,
    [switch] $Fresh
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$started = Get-Date
$utf8 = New-Object System.Text.UTF8Encoding $false
$shell = (Get-Process -Id $PID).Path
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$referenceVersion = '4.56.1.0'   # the read build the workflows build and check against
$env:PYTHONUTF8 = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$savedEncoding = $null
try { $savedEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = $utf8 } catch { }

# powershell -File passes "a,b" as one string.
$Parts = @($Parts | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
$Versions = @($Versions | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
foreach ($name in $Parts) {
    if (@('latest', 'rtx', 'gui', 'versions') -notcontains $name) { throw "Unknown part '$name' (latest, rtx, gui, versions)." }
}

# ---------- the checkout ----------

$commit = "$(& git rev-parse HEAD 2>$null)".Trim()
if ($LASTEXITCODE -ne 0 -or -not $commit) { throw 'Run this from a git checkout of the repository (git not found, or not a checkout).' }
$branch = "$(& git rev-parse --abbrev-ref HEAD 2>$null)".Trim()
$changes = @(& git status --porcelain --untracked-files=no 2>$null)
if ($changes.Count -gt 0) {
    throw ("Tracked files have local changes; the results would not be those of the commit. Commit or stash them " +
        "(an interrupted run can leave NVEncVideoWriterPlugin\TimelineFrameCache.cs or NvencNative\NvencNative.cpp " +
        "changed: git checkout -- <file>).`n" + ($changes -join "`n"))
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$WorkDir = (Resolve-Path -LiteralPath $WorkDir).Path.TrimEnd('\')
if (($WorkDir + '\').StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '-WorkDir must be outside the repository.' }
$run = Join-Path $WorkDir ('runs\' + $commit.Substring(0, 12))
if ($Fresh -and (Test-Path -LiteralPath $run)) { Rename-Item -LiteralPath $run -NewName ((Split-Path -Leaf $run) + '-' + $started.ToString('yyyyMMdd-HHmmss')) }
$logs = Join-Path $run 'logs'
$results = Join-Path $run 'results'
$ymm4 = Join-Path $WorkDir 'ymm4'
$media = Join-Path $WorkDir 'media'
foreach ($folder in @($logs, $results, $ymm4, $media)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
$stepsFile = Join-Path $run 'steps.jsonl'
$progressFile = Join-Path $run 'progress.txt'
$refDir = Join-Path $ymm4 $referenceVersion
$refArg = '-p:YMM4DirPath=' + $refDir + '\'
$pluginDll = Join-Path $root 'NVEncVideoWriterPlugin\bin\Release\net10.0-windows10.0.19041.0\YMM4Rtx3060Nvenc.dll'
$pluginDir = Join-Path $run 'plugin'
$fingerprint = Join-Path $root 'tools\HostFingerprint\bin\Release\net10.0\HostFingerprintTool.dll'
$video = Join-Path $media 'probe-video.mp4'

# Steps of an earlier run of this commit (the last record of each wins).
$previous = @{}
if (Test-Path -LiteralPath $stepsFile) {
    foreach ($line in [IO.File]::ReadAllLines($stepsFile, $utf8)) {
        if (-not $line.Trim()) { continue }
        try { $record = $line | ConvertFrom-Json; $previous[$record.Id] = $record } catch { }
    }
}

# ---------- process and result helpers ----------

# Windows command-line quoting (CommandLineToArgvW rules), so paths with spaces survive PowerShell 5.1 and 7.
function ConvertTo-CommandLine([string[]] $Arguments) {
    $quoted = foreach ($argument in $Arguments) {
        if ($argument -ne '' -and $argument -notmatch '[\s"]') { $argument; continue }
        $escaped = [regex]::Replace($argument, '(\\*)"', { param($m) ('\' * (2 * $m.Groups[1].Length)) + '\"' })
        '"' + [regex]::Replace($escaped, '(\\+)$', { param($m) $m.Value + $m.Value }) + '"'
    }
    $quoted -join ' '
}

# Runs a program into $Log (its output is in "$Log.stdout" while it runs). A program still running after
# $TimeoutMinutes is ended with its child processes.
function Invoke-Logged([string] $Log, [string] $FilePath, [string[]] $Arguments, [int] $TimeoutMinutes = 60) {
    $stdout = "$Log.stdout"
    $stderr = "$Log.stderr"
    $start = @{ FilePath = $FilePath; WorkingDirectory = $root; NoNewWindow = $true; PassThru = $true
        RedirectStandardOutput = $stdout; RedirectStandardError = $stderr }
    $commandLine = ConvertTo-CommandLine $Arguments
    if ($commandLine) { $start.ArgumentList = $commandLine }  # Start-Process rejects an empty argument list
    [IO.File]::AppendAllText($Log, "> $FilePath $commandLine`r`n", $utf8)
    $process = Start-Process @start
    $null = $process.Handle
    $timedOut = -not $process.WaitForExit($TimeoutMinutes * 60000)
    if ($timedOut) {
        Start-Process -FilePath 'taskkill.exe' -ArgumentList "/PID $($process.Id) /T /F" -NoNewWindow -Wait
        $process.WaitForExit()
    }
    $out = ''
    $err = ''
    if (Test-Path -LiteralPath $stdout) { $out = [IO.File]::ReadAllText($stdout, $utf8) }
    if (Test-Path -LiteralPath $stderr) { $err = [IO.File]::ReadAllText($stderr, $utf8) }
    Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue
    $text = $out
    if ($err.Trim()) { $text += "`r`n----- stderr -----`r`n" + $err }
    $code = $process.ExitCode
    if ($timedOut) { $code = -1; $text += "`r`n> timed out after $TimeoutMinutes min" }
    [IO.File]::AppendAllText($Log, $text + "`r`n> exit code $code`r`n", $utf8)
    @{ ExitCode = $code; Stdout = $out; Text = $text }
}

# Inside a step: runs a program into the step's log and throws on a nonzero exit code.
function Invoke-Native([string] $FilePath, [string[]] $Arguments, [int] $TimeoutMinutes = 60) {
    $result = Invoke-Logged $script:stepLog $FilePath $Arguments $TimeoutMinutes
    if ($result.ExitCode -eq -1) { throw "$([IO.Path]::GetFileName($FilePath)) timed out after $TimeoutMinutes min" }
    if ($result.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($FilePath)) exited with code $($result.ExitCode)" }
    $result
}

# The test projects are built once (setup-build) and run without building again: a build that finds nothing to do
# still takes several seconds per run (the reference lowering of the plugin runs every time).
$hostProjects = @('tests\HostCacheProbe\HostCacheProbe.csproj', 'tests\CacheChecks\CacheChecks.csproj', 'tests\GuiSmoke\GuiSmoke.csproj')
$plainProjects = @('tests\HostLoadChecks\HostLoadChecks.csproj', 'tests\StoreChecksHarness\StoreChecks.csproj', 'tests\ReadinessChecks\ReadinessChecks.csproj',
    'tests\CacheAdversarialChecks\CacheAdversarialChecks.csproj', 'tests\FileLeaseChecks\FileLeaseChecks.csproj', 'tests\DescriptionJsonChecks\DescriptionJsonChecks.csproj')

function Build-Project([string] $Project) {
    $arguments = @('build', $Project, '-c', 'Release', '--nologo', '-v', 'q')
    if ($hostProjects -contains $Project) { $arguments += $refArg }
    Invoke-Native 'dotnet' $arguments
}

# dotnet run of a project that references YMM4 (built against the reference YMM4, as the workflows do).
function Invoke-HostProject([string] $Project, [string[]] $Arguments, [int] $TimeoutMinutes = 60) {
    Invoke-Native 'dotnet' (@('run', '--no-build', '--project', $Project, '-c', 'Release', $refArg, '--no-launch-profile', '--') + $Arguments) $TimeoutMinutes
}

# Several programs at once, each into its own part of the step's log; throws naming those that failed.
function Invoke-Parallel([hashtable[]] $Runs, [int] $TimeoutMinutes = 60) {
    $launched = foreach ($entry in $Runs) {
        $log = Join-Path $logs ("$($script:stepId)-$($entry.Name).part")
        $commandLine = ConvertTo-CommandLine $entry.Arguments
        $process = Start-Process -FilePath $entry.FilePath -ArgumentList $commandLine -WorkingDirectory $root -NoNewWindow -PassThru `
            -RedirectStandardOutput "$log.stdout" -RedirectStandardError "$log.stderr"
        $null = $process.Handle
        @{ Name = $entry.Name; Process = $process; Log = $log; CommandLine = "$($entry.FilePath) $commandLine" }
    }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $failed = @()
    foreach ($item in $launched) {
        $left = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
        $code = $null
        if ($item.Process.WaitForExit($left)) { $code = $item.Process.ExitCode }
        else { Start-Process -FilePath 'taskkill.exe' -ArgumentList "/PID $($item.Process.Id) /T /F" -NoNewWindow -Wait; $item.Process.WaitForExit(); $code = -1 }
        $text = ''
        foreach ($stream in @('stdout', 'stderr')) {
            $file = "$($item.Log).$stream"
            if (Test-Path -LiteralPath $file) { $text += [IO.File]::ReadAllText($file, $utf8); Remove-Item -LiteralPath $file }
        }
        Write-StepLog "> $($item.CommandLine)`r`n$text`r`n> exit code $code"
        if ($code -ne 0) { $failed += $item.Name }
    }
    if ($failed.Count -gt 0) { throw "failed: $($failed -join ', ')" }
}

function Invoke-Python([string[]] $Arguments) { Invoke-Native $python (@($pythonArgs) + $Arguments) }

function Write-StepLog([string] $Text) { [IO.File]::AppendAllText($script:stepLog, $Text + "`r`n", $utf8) }

$records = New-Object System.Collections.Generic.List[object]

function Add-Record($Record, [bool] $Persist) {
    $records.Add($Record)
    if ($Persist) { [IO.File]::AppendAllText($stepsFile, ($Record | ConvertTo-Json -Compress -Depth 3) + "`n", $utf8) }
    $color = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow' }[$Record.Status]
    $suffix = ''
    if ($Record.Note) { $suffix = " - $($Record.Note)" }
    if ($Record.Replayed) { $suffix += ' (earlier run)' }
    Write-Host ('[{0}] {1}: {2} ({3} min){4}' -f $Record.Id, $Record.Title, $Record.Status, $Record.Minutes, $suffix) -ForegroundColor $color
}

function Test-Passed([string] $Id) { $previous.ContainsKey($Id) -and $previous[$Id].Status -eq 'PASS' }

# Runs a block as one step. The block throws to fail and may set $script:stepNote. A step that passed in an earlier
# run of this commit is taken from it, unless -Always (downloads and builds the later steps need).
function Invoke-Step([string] $Part, [string] $Id, [string] $Title, [scriptblock] $Body, [switch] $Always) {
    if (-not $Always -and (Test-Passed $Id)) {
        $earlier = $previous[$Id]
        Add-Record ([pscustomobject]@{ Part = $Part; Id = $Id; Title = $Title; Status = 'PASS'; Minutes = $earlier.Minutes
            Note = $earlier.Note; Tail = ''; Finished = $earlier.Finished; Replayed = $true }) $false
        return $true
    }
    Write-Host "[$Id] $Title ..."
    [IO.File]::WriteAllText($progressFile, "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) running $Id - $Title`r`n", $utf8)
    $script:stepId = $Id
    $script:stepLog = Join-Path $logs "$Id.log"
    Remove-Item -LiteralPath $script:stepLog -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText($script:stepLog, "# $Title`r`n", $utf8)
    $script:stepNote = ''
    $stepWatch = [Diagnostics.Stopwatch]::StartNew()
    $stepStatus = 'PASS'
    $stepMessage = ''
    # In the step (and the functions it calls) every error fails it; the script itself keeps going.
    $ErrorActionPreference = 'Stop'
    try { $null = & $Body }
    catch { $stepStatus = 'FAIL'; $stepMessage = $_.Exception.Message; Write-StepLog "FAILED: $stepMessage" }
    if (-not $stepMessage) { $stepMessage = $script:stepNote }
    $stepTail = ''
    if ($stepStatus -eq 'FAIL') {
        $stepTail = (([IO.File]::ReadAllLines($script:stepLog, $utf8) | Where-Object { $_ -notmatch '^B64 ' }) | Select-Object -Last 60) -join "`n"
    }
    Add-Record ([pscustomobject]@{ Part = $Part; Id = $Id; Title = $Title; Status = $stepStatus
        Minutes = [Math]::Round($stepWatch.Elapsed.TotalMinutes, 1); Note = $stepMessage; Tail = $stepTail
        Finished = (Get-Date).ToString('yyyy-MM-dd HH:mm'); Replayed = $false }) $true
    $stepStatus -ne 'FAIL'
}

function Skip-Step([string] $Part, [string] $Id, [string] $Title, [string] $Reason) {
    Add-Record ([pscustomobject]@{ Part = $Part; Id = $Id; Title = $Title; Status = 'SKIP'; Minutes = 0; Note = $Reason; Tail = ''
        Finished = (Get-Date).ToString('yyyy-MM-dd HH:mm'); Replayed = $false }) $true
}

function Get-StepStatus([string] $Id) {
    $found = @($records | Where-Object { $_.Id -eq $Id })
    if ($found.Count -eq 0) { return $null }
    $found[-1].Status
}

# Downloads one YMM4 version with tools/ci/fetch-ymm4.sh (Git Bash), as the workflows do.
function Invoke-Fetch([string] $Version, [string] $Destination, [string] $Mode) {
    $env:YMM4_FETCH_PRUNE = '1'   # delete the files (outside user\) the version does not have
    try { Invoke-Native $bash @('tools/ci/fetch-ymm4.sh', $Version, ($Destination -replace '\\', '/'), $Mode) 120 }
    finally { Remove-Item Env:YMM4_FETCH_PRUNE -ErrorAction SilentlyContinue }
}

# The same download in the background, while the steps of another version run; Complete-Fetch waits for it and
# records it as that version's download step.
function Start-Fetch([string] $Version, [string] $Destination) {
    $log = Join-Path $logs "v$Version-fetch.log"
    [IO.File]::WriteAllText($log, "# YMM4 ${Version}: download (the files that changed), during the steps of the version before`r`n", $utf8)
    $commandLine = ConvertTo-CommandLine @('tools/ci/fetch-ymm4.sh', $Version, ($Destination -replace '\\', '/'), '--app-ffmpeg')
    $env:YMM4_FETCH_PRUNE = '1'
    try {
        $process = Start-Process -FilePath $bash -ArgumentList $commandLine -WorkingDirectory $root -NoNewWindow -PassThru `
            -RedirectStandardOutput "$log.stdout" -RedirectStandardError "$log.stderr"
        $null = $process.Handle
    }
    finally { Remove-Item Env:YMM4_FETCH_PRUNE -ErrorAction SilentlyContinue }
    @{ Version = $Version; Process = $process; Log = $log; CommandLine = "$bash $commandLine"; Started = Get-Date }
}

function Complete-Fetch([hashtable] $Fetch) {
    $id = "v$($Fetch.Version)-fetch"
    $title = "YMM4 $($Fetch.Version): download (the files that changed)"
    $code = $null
    if ($Fetch.Process.WaitForExit(120 * 60000)) { $code = $Fetch.Process.ExitCode }
    else { Start-Process -FilePath 'taskkill.exe' -ArgumentList "/PID $($Fetch.Process.Id) /T /F" -NoNewWindow -Wait; $Fetch.Process.WaitForExit(); $code = -1 }
    $text = ''
    foreach ($stream in @('stdout', 'stderr')) {
        $file = "$($Fetch.Log).$stream"
        if (Test-Path -LiteralPath $file) { $text += [IO.File]::ReadAllText($file, $utf8); Remove-Item -LiteralPath $file }
    }
    [IO.File]::AppendAllText($Fetch.Log, "> $($Fetch.CommandLine)`r`n$text`r`n> exit code $code`r`n", $utf8)
    $status = 'PASS'
    $note = 'downloaded while the version before was checked'
    $tail = ''
    if ($code -ne 0) {
        $status = 'FAIL'
        $note = "fetch-ymm4.sh exited with code $code"
        $tail = (([IO.File]::ReadAllLines($Fetch.Log, $utf8)) | Select-Object -Last 60) -join "`n"
    }
    Add-Record ([pscustomobject]@{ Part = 'versions'; Id = $id; Title = $title; Status = $status
        Minutes = [Math]::Round(((Get-Date) - $Fetch.Started).TotalMinutes, 1); Note = $note; Tail = $tail
        Finished = (Get-Date).ToString('yyyy-MM-dd HH:mm'); Replayed = $false }) $true
    $status -ne 'FAIL'
}

# The user folder of a YMM4 copy under <WorkDir>\ymm4 (the plugin and settings a start installed): YMM4 then starts as
# the first time.
function Reset-User([string] $HostDir) {
    if (-not $HostDir.StartsWith($ymm4 + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "not a YMM4 copy of this script: $HostDir" }
    $user = Join-Path $HostDir 'user'
    if (Test-Path -LiteralPath $user) { Remove-Item -LiteralPath $user -Recurse -Force }
}

function Get-OutcomeOf([string[]] $Ids) {
    $statuses = @($Ids | ForEach-Object { Get-StepStatus $_ })
    if ($statuses -contains 'FAIL') { return 'failure' }
    if (@($statuses | Where-Object { $_ -ne 'PASS' }).Count -gt 0) { return 'skipped' }
    'success'
}

# ---------- tools ----------

function Find-Msbuild {
    $vswhere = @('C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe',
        'C:\Program Files\Microsoft Visual Studio\Installer\vswhere.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $vswhere) { return $null }
    $vsDir = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsDir) { return $null }
    $path = Join-Path $vsDir 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $path) { return $path }
    $null
}

function Find-GitBash {
    $candidates = @((Join-Path $env:ProgramFiles 'Git\bin\bash.exe'))
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) { $candidates += Join-Path (Split-Path -Parent (Split-Path -Parent $git.Source)) 'bin\bash.exe' }
    $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Test-Tool([string] $Name) { [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

$missing = New-Object System.Collections.Generic.List[string]
$msbuild = Find-Msbuild
$bash = Find-GitBash
$python = $null
$pythonArgs = @()
foreach ($candidate in @('python', 'py')) {
    if (-not (Test-Tool $candidate)) { continue }
    $prefix = @()
    if ($candidate -eq 'py') { $prefix = @('-3') }
    $pythonVersion = & $candidate @prefix --version 2>&1
    if ($LASTEXITCODE -eq 0 -and "$pythonVersion" -match 'Python 3') { $python = $candidate; $pythonArgs = $prefix; break }
}
if (-not (Test-Tool 'dotnet') -or -not (@(& dotnet --list-sdks 2>$null) -match '^10\.').Count) { $missing.Add('.NET SDK 10 (winget install Microsoft.DotNet.SDK.10)') }
if (-not $msbuild) { $missing.Add('Visual Studio Build Tools with the C++ workload (MSBuild)') }
if (-not $bash) { $missing.Add('Git for Windows (winget install Git.Git)') }
else {
    # No double quotes in the command: Windows PowerShell 5.1 drops them from native arguments.
    $absent = (@(& $bash -c 'for t in curl jq openssl base64 xargs; do command -v $t > /dev/null || echo $t; done' 2>$null) -join ' ').Trim()
    if ($absent) { $missing.Add("in Git Bash: $absent (jq: winget install jqlang.jq, then a new terminal)") }
}
if (-not $python) { $missing.Add('Python 3 (winget install Python.Python.3.12)') }
if (-not (Test-Tool 'ffmpeg') -or -not (Test-Tool 'ffprobe')) { $missing.Add('ffmpeg and ffprobe on PATH (winget install Gyan.FFmpeg)') }
if (-not (Test-Path -LiteralPath $windowsPowerShell)) { $missing.Add('Windows PowerShell 5.1') }
if (-not $NoNvenc -and -not (Test-Tool 'nvidia-smi')) { $missing.Add('the NVIDIA driver (nvidia-smi); on a PC without NVENC add -NoNvenc') }
if ($missing.Count -gt 0) { throw ("Missing:`n  " + ($missing -join "`n  ")) }
# YMM4 may hand a second start over to a running one: the checks start their own copies.
if (@(Get-Process -Name 'YukkuriMovieMaker*' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'YMM4 is running. Close it first.' }

$minimum = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'NVEncVideoWriterPlugin\HostReferenceVersion.targets')), '<Ymm4MinimumVersion[^>]*>([^<]+)<').Groups[1].Value
$pluginVersion = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'NVEncVideoWriterPlugin\NVEncVideoWriterPlugin.csproj')), '<InformationalVersion>([^<]+)<').Groups[1].Value
$pluginLabel = "$pluginVersion+$($commit.Substring(0, 7))"

# ---------- environment ----------

$envLines = New-Object System.Collections.Generic.List[string]
$envLines.Add("commit: $commit ($branch), plugin $pluginLabel")
$envLines.Add("parts: $($Parts -join ', '); versions: $($Versions -join ' '); mode: $(if ($NoNvenc) { 'NoNvenc' } else { 'NVENC' })")
foreach ($gpu in @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)) { $envLines.Add("video controller: $($gpu.Name), driver $($gpu.DriverVersion), $($gpu.CurrentHorizontalResolution)x$($gpu.CurrentVerticalResolution)") }
if (Test-Tool 'nvidia-smi') { $envLines.Add("nvidia-smi: $(& nvidia-smi '--query-gpu=name,driver_version,memory.total' '--format=csv,noheader' 2>$null)") }
$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cpu) { $envLines.Add("CPU: $("$($cpu.Name)".Trim()), $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads") }
$system = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
if ($system) { $envLines.Add("RAM: $([Math]::Round($system.TotalPhysicalMemory / 1GB, 1)) GB") }
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { $envLines.Add("OS: $($os.Caption) $($os.Version), UI culture $((Get-UICulture).Name), culture $((Get-Culture).Name)") }
$envLines.Add("PowerShell: $($PSVersionTable.PSVersion); dotnet: $(& dotnet --version 2>$null); $(& $python @pythonArgs --version 2>&1)")
$envLines.Add("ffmpeg: $((& ffmpeg -version 2>$null) | Select-Object -First 1)")
$drive = Get-PSDrive -Name $WorkDir.Substring(0, 1) -ErrorAction SilentlyContinue
if ($drive) {
    $envLines.Add("free on ${drive}: $([Math]::Round($drive.Free / 1GB)) GB")
    if ($drive.Free -lt 20GB) { Write-Host "Less than 20 GB free on ${drive}:; the downloads and builds may not fit." -ForegroundColor Yellow }
}
[IO.File]::WriteAllLines((Join-Path $run 'environment.txt'), $envLines, $utf8)
$envLines | ForEach-Object { Write-Host $_ }

# ---------- setup (every part) ----------

$ready = Invoke-Step 'setup' 'setup-ymm4' "YMM4 $referenceVersion from its update server (the reference build)" {
    Invoke-Fetch $referenceVersion $refDir '--app-ffmpeg'
} -Always
$ready = (Invoke-Step 'setup' 'setup-package' 'build.ps1: native, plugin, load check, package (like a release)' {
    Invoke-Native $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'build.ps1'), '-Ymm4DirPath', $refDir)
    if (Test-Path -LiteralPath $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force }
    $zip = Join-Path $run 'plugin.zip'
    Copy-Item -LiteralPath (Join-Path $root 'dist\YMM4-RTX3060-NVENC.ymme') -Destination $zip -Force
    Expand-Archive -LiteralPath $zip -DestinationPath $pluginDir
    Remove-Item -LiteralPath $zip
    Invoke-Native 'dotnet' @('build', 'tools\HostFingerprint', '-c', 'Release', '--nologo', '-v', 'q')
} -Always) -and $ready
$ready = (Invoke-Step 'setup' 'setup-build' 'The test projects, built once (the steps run them without building again)' {
    foreach ($project in $hostProjects + $plainProjects) { Build-Project $project }
} -Always) -and $ready
[void](Invoke-Step 'setup' 'setup-media' 'Test clips for --video (ffmpeg)' {
    $clip = Join-Path $media 'probe-video-ffmpeg'
    if (-not (Test-Path -LiteralPath "$clip.m2ts")) {
        Invoke-Native 'ffmpeg' @('-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=320x180:rate=30', '-t', '2', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-y', $video)
        # For the FFmpeg reader: key frames every half second, and the same clip in MPEG-TS (seeks there can land late).
        Invoke-Native 'ffmpeg' @('-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=320x180:rate=30', '-t', '4', '-c:v', 'libx264', '-g', '15', '-bf', '2', '-pix_fmt', 'yuv420p', '-y', "$clip.mp4")
        Invoke-Native 'ffmpeg' @('-v', 'error', '-i', "$clip.mp4", '-c', 'copy', '-f', 'mpegts', '-y', "$clip.m2ts")
    }
} -Always)
if (-not $ready) { Write-Host 'The setup failed: the parts need YMM4 and the plugin package.' -ForegroundColor Red }

# ---------- latest: the cache-development workflow against YMM4 4.56.1.0 ----------

if ($ready -and $Parts -contains 'latest') {
    $minDir = Join-Path $ymm4 "$minimum-top"
    $haveMinimum = Invoke-Step 'latest' 'latest-ymm4-minimum' "YMM4 $minimum (the oldest supported), its application folder" {
        Invoke-Fetch $minimum $minDir '--top'
    } -Always
    if ($haveMinimum) {
        [void](Invoke-Step 'latest' 'latest-load-minimum' "The plugin loads on YMM4 $minimum (types load, missing members are guarded)" {
            Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'tests\HostLoadChecks\HostLoadChecks.csproj', '-c', 'Release', '--no-launch-profile', '--', $minDir, $pluginDll)
            $api = Invoke-Native 'dotnet' @($fingerprint, 'api', $pluginDll, $minDir)
            $list = Join-Path $run 'missing-on-minimum.txt'
            [IO.File]::WriteAllText($list, $api.Stdout, $utf8)
            Invoke-Python @('tools\compat\ymm4_compat.py', 'guarded', $list)
        })
    } else { Skip-Step 'latest' 'latest-load-minimum' "The plugin loads on YMM4 $minimum" 'download failed' }
    [void](Invoke-Step 'latest' 'latest-load-reference' "The plugin loads on YMM4 $referenceVersion" {
        Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'tests\HostLoadChecks\HostLoadChecks.csproj', '-c', 'Release', '--no-launch-profile', '--', ($refDir + '\'), $pluginDll)
    })
    [void](Invoke-Step 'latest' 'latest-native' 'NativeChecks (native invariants)' {
        Invoke-Native $msbuild @('NvencNative\NvencNative.vcxproj', '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/nologo', '/v:minimal')
        Invoke-Native $msbuild @('tests\NativeChecks.vcxproj', '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/nologo', '/v:minimal')
        Invoke-Native (Join-Path $root 'tests\bin\Release\NativeChecks.exe') @()
    })
    foreach ($check in @(
            @('psd-duplicate-parser', 'Duplicate PsdParser modules reject only affected frames'),
            @('psd-duplicate-source', 'Duplicate PSD file-source modules reject only affected frames'),
            @('psd-tachie-check', 'PSD tachie pixels, shared settings and envelope completion'),
            @('psd-tachie-large', 'Large PSD tachie second playback and exact pixels (109 MiB per PSD)'),
            @('animation-tachie-large', 'Large animation tachie second playback and exact pixels (309 PNG parts)'))) {
        $mode = $check[0]
        [void](Invoke-Step 'latest' "latest-$mode" "HostCacheProbe --${mode}: $($check[1])" { Invoke-HostProject 'tests\HostCacheProbe\HostCacheProbe.csproj' @(($refDir + '\'), "--$mode") })
    }
    # They use the CPU only, and none depends on another: all at once.
    [void](Invoke-Step 'latest' 'latest-portable' 'StoreChecks, ReadinessChecks, CacheAdversarialChecks, FileLeaseChecks, DescriptionJsonChecks (at once)' {
        Invoke-Parallel @(foreach ($project in $plainProjects | Where-Object { $_ -notlike '*HostLoadChecks*' }) {
            @{ Name = [IO.Path]::GetFileNameWithoutExtension($project); FilePath = 'dotnet'
                Arguments = @('run', '--no-build', '--project', $project, '-c', 'Release', '--no-launch-profile') }
        })
    })
    [void](Invoke-Step 'latest' 'latest-negative-control' 'Live-reuse regression rejects a deliberately removed validation guard (source restored)' {
        $path = Join-Path $root 'NVEncVideoWriterPlugin\TimelineFrameCache.cs'
        $original = [IO.File]::ReadAllBytes($path)
        $text = [IO.File]::ReadAllText($path)
        $broken = $text.Replace('ReferenceEquals(state.LastOutput, previousOutput) && StillCurrent(pending))', 'ReferenceEquals(state.LastOutput, previousOutput))')
        if ($broken -eq $text) { throw 'the mutation did not apply' }
        $failure = $null
        try {
            [IO.File]::WriteAllText($path, $broken, $utf8)
            Build-Project 'tests\HostCacheProbe\HostCacheProbe.csproj'
            $probe = Invoke-Logged $script:stepLog 'dotnet' @('run', '--no-build', '--project', 'tests\HostCacheProbe\HostCacheProbe.csproj', '-c', 'Release', $refArg, '--no-launch-profile', '--', ($refDir + '\'), '--gpu')
            if ($probe.ExitCode -eq 0 -or -not $probe.Text.Contains('Edit between capture and live lookup reused stale output')) {
                $failure = 'the probe did not fail at the stale-live-output regression'
            } else { Write-StepLog 'The mutated build failed at the stale-live-output regression, as expected.' }
        }
        catch { $failure = $_.Exception.Message }
        finally { [IO.File]::WriteAllBytes($path, $original) }
        # The later steps run the probe without building it: build it again from the restored source.
        Build-Project 'tests\HostCacheProbe\HostCacheProbe.csproj'
        if ($failure) { throw $failure }
    })
    [void](Invoke-Step 'latest' 'latest-cache-checks' 'CacheChecks: dependency keys against the real host' { Invoke-HostProject 'tests\CacheChecks\CacheChecks.csproj' @(($refDir + '\')) })
    foreach ($check in @(
            @('integration', 'Host integration'),
            @('gpu', 'Pixel parity on this GPU, export hook, idle pre-render'),
            @('gpu-retention-check', 'GPU cold copies, read-ahead and device-loss notification'),
            @('idle-parallel-check', 'Idle parallel workers, pixel parity and cancellation (2 and 4 workers)'),
            @('edit-description-check', 'Incremental descriptions match 1000 actual host edits'),
            @('preview-performance', 'GPU retention, invalidation and trace measurement'),
            @('animation-tachie-check', 'Animation tachie pixels, delayed envelopes, groups, scenes, INI, blink'))) {
        $mode = $check[0]
        [void](Invoke-Step 'latest' "latest-$mode" "HostCacheProbe --${mode}: $($check[1])" {
            Invoke-HostProject 'tests\HostCacheProbe\HostCacheProbe.csproj' @(($refDir + '\'), "--$mode")
            if ($mode -eq 'preview-performance') { Copy-Item -LiteralPath (Join-Path $root 'dist\preview-performance.json') -Destination (Join-Path $run 'preview-performance.json') -Force }
        })
    }
}

# ---------- rtx: tools\rtx-check.ps1 ----------

if ($ready -and $Parts -contains 'rtx') {
    $rtxTitle = 'tools\rtx-check.ps1: NVENC smoke, throughput, color matrix, VUI, preview timing'
    if ($NoNvenc) { $rtxTitle = 'tools\rtx-check.ps1 -NoNvenc (no encoding steps)' }
    [void](Invoke-Step 'rtx' 'rtx-check' $rtxTitle {
        $since = Get-Date
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\rtx-check.ps1'), '-Ymm4DirPath', $refDir, '-PreviewRuns', "$PreviewRuns")
        if ($NoNvenc) { $arguments += '-NoNvenc' }
        $check = Invoke-Logged $script:stepLog $shell $arguments 240
        $folder = Get-ChildItem -LiteralPath (Join-Path $root 'dist\rtx-check') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.CreationTime -ge $since.AddSeconds(-5) } | Sort-Object Name | Select-Object -Last 1
        if ($folder -and (Test-Path -LiteralPath (Join-Path $folder.FullName 'summary.md'))) {
            Copy-Item -LiteralPath (Join-Path $folder.FullName 'summary.md') -Destination (Join-Path $run 'rtx-check-summary.md') -Force
            if (Test-Path -LiteralPath "$($folder.FullName).zip") { Copy-Item -LiteralPath "$($folder.FullName).zip" -Destination (Join-Path $run 'rtx-check-logs.zip') -Force }
        } else { throw 'rtx-check wrote no summary.md' }
        $counts = [regex]::Match($check.Stdout, '(?m)^Results: (.+)$')
        if ($counts.Success) { $script:stepNote = $counts.Groups[1].Value.Trim() }
        if ($check.ExitCode -ne 0) { throw "rtx-check reported failures ($($script:stepNote)); see rtx-check-summary.md" }
    })
}

# ---------- gui: tools\ci\gui-smoke.ps1 ----------

function Remove-Screenshots([string] $Log) {
    if (Test-Path -LiteralPath $Log) { [IO.File]::WriteAllLines($Log, @([IO.File]::ReadAllLines($Log, $utf8) | Where-Object { $_ -notmatch '^B64 ' }), $utf8) }
}

if ($ready -and $Parts -contains 'gui') {
    $guiHost = Join-Path $ymm4 "gui-$referenceVersion"
    $guiOut = Join-Path $run 'gui'
    # The scripts click fixed positions of the CI layout: a 1600x900 primary screen at 100% scale (Windows Server sets
    # it itself). Read as Windows PowerShell sees it, the scaled size.
    $code = 'Add-Type -AssemblyName System.Windows.Forms; $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; [string]$b.Width + ''x'' + [string]$b.Height'
    $screen = "$(& $windowsPowerShell -NoProfile -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))) 2>$null)".Trim()
    $server = [bool](Get-Command Set-DisplayResolution -ErrorAction SilentlyContinue)
    if (-not $server -and $screen -ne '1600x900') {
        $reason = "primary screen $screen at this scale, not 1600x900 at 100% (the GUI scripts click fixed positions)"
        Skip-Step 'gui' 'gui-smoke' 'GUI smoke: YMM4 with the plugin, a project, the frame cache tool' $reason
        Skip-Step 'gui' 'gui-stress' 'GUI stress: 30 s Full-HD project played off/cold/warm, seeks, edits, purge (trace checked)' $reason
    } else {
        $copied = Invoke-Step 'gui' 'gui-host' "A copy of YMM4 $referenceVersion for the GUI runs" {
            & robocopy.exe $refDir $guiHost /MIR /XD (Join-Path $refDir 'user') (Join-Path $guiHost 'user') /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
            $global:LASTEXITCODE = 0
        } -Always
        if ($copied) {
            [void](Invoke-Step 'gui' 'gui-smoke' 'GUI smoke: YMM4 with the plugin, a project, the frame cache tool (screenshots)' {
                $smoke = Join-Path $guiOut 'smoke'
                New-Item -ItemType Directory -Force -Path $smoke | Out-Null
                Reset-User $guiHost
                Invoke-HostProject 'tests\GuiSmoke\GuiSmoke.csproj' @(($guiHost + '\'), (Join-Path $smoke 'gui-smoke.ymmp'))
                $trace = Join-Path $smoke 'gui-trace.jsonl'
                try {
                    Invoke-Native $windowsPowerShell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\ci\gui-smoke.ps1'),
                        '-HostDir', $guiHost, '-Project', (Join-Path $smoke 'gui-smoke.ymmp'), '-PluginDir', $pluginDir, '-ArtifactDirectory', $smoke, '-TracePath', $trace) 30
                }
                finally { Remove-Screenshots $script:stepLog }
                # What the pre-renderer did, paused and while the smoke test played (its frames in the trace).
                $frames = @([IO.File]::ReadAllLines($trace, $utf8) | Where-Object { $_.Contains('"Stage":"idle-frame"') })
                $playing = @($frames | Where-Object { $_.Contains('playing=True') })
                $rendered = @($frames | Where-Object { $_.Contains('"Outcome":"Rendered"') })
                $renderedPlaying = @($playing | Where-Object { $_.Contains('"Outcome":"Rendered"') })
                $script:stepNote = "pre-render: $($rendered.Count) frames drawn ($($renderedPlaying.Count) while playing), $($frames.Count) visited"
                Write-StepLog $script:stepNote
            })
            [void](Invoke-Step 'gui' 'gui-stress' 'GUI stress: 30 s Full-HD project played off/cold/warm, seeks, edits, purge (trace checked)' {
                $stress = Join-Path $guiOut 'stress'
                if (Test-Path -LiteralPath $stress) { Remove-Item -LiteralPath $stress -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $stress | Out-Null
                Invoke-Native $windowsPowerShell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\ci\make-stress-media.ps1'), '-OutputDirectory', (Join-Path $stress 'assets'))
                Invoke-HostProject 'tests\GuiSmoke\GuiSmoke.csproj' @(($guiHost + '\'), (Join-Path $stress 'stress-30s.ymmp'), '--stress', (Join-Path $stress 'assets'))
                Invoke-Native $windowsPowerShell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\ci\prepare-stress-project.ps1'), '-Project', (Join-Path $stress 'stress-30s-portable.ymmp'))
                Reset-User $guiHost
                $trace = Join-Path $stress 'gui-trace.jsonl'
                $gui = Invoke-Logged $script:stepLog $windowsPowerShell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\ci\gui-smoke.ps1'),
                    '-HostDir', $guiHost, '-Project', (Join-Path $stress 'stress-30s.ymmp'), '-PluginDir', $pluginDir, '-TracePath', $trace, '-Stress', '-ArtifactDirectory', $stress) 40
                $problems = @()
                if ($gui.ExitCode -ne 0) { $problems += "gui-smoke.ps1 exited with code $($gui.ExitCode)" }
                if (Test-Path -LiteralPath $trace) {
                    $analysis = Invoke-Logged $script:stepLog $python (@($pythonArgs) + @('tools\analyze-stress-trace.py', $trace, '--projects', $stress, '--output', (Join-Path $stress 'stress-summary.json')))
                    if ($analysis.ExitCode -ne 0) { $problems += 'the playback evidence checks failed (tools\analyze-stress-trace.py)' }
                } else { $problems += 'no GUI trace was written' }
                if ($problems.Count -gt 0) { throw ($problems -join '; ') }
            })
        }
    }
}

# ---------- versions: the ymm4-compat workflow on every supported version ----------

if ($ready -and $Parts -contains 'versions') {
    $targets = @()
    $listed = Invoke-Step 'versions' 'versions-list' "The versions to check (the update server's and the read builds, $minimum and newer)" {
        if ($Versions -contains 'all') {
            $server = Invoke-Native $bash @('tools/ci/fetch-ymm4.sh', 'list')
            $read = [regex]::Matches([IO.File]::ReadAllText((Join-Path $root 'NVEncVideoWriterPlugin\HostKnownBuilds.cs')), 'new\("(\d+(?:\.\d+){3})",') | ForEach-Object { $_.Groups[1].Value }
            $all = @(($server.Stdout -split "`r?`n") + @($read)) | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+(\.\d+){3}$' -and [version]$_ -ge [version]$minimum }
        } else {
            $all = @($Versions | Where-Object { $_ -match '^\d+(\.\d+){3}$' })
            if ($all.Count -ne $Versions.Count) { throw "not a version: $($Versions -join ' ')" }
        }
        # Oldest first: each version then downloads only what changed since the one before.
        $script:targets = @($all | Sort-Object -Unique | Sort-Object { [version]$_ })
        if ($script:targets.Count -eq 0) { throw 'no version to check' }
        [IO.File]::WriteAllLines((Join-Path $run 'versions.txt'), $script:targets, $utf8)
        $script:stepNote = "$($script:targets.Count) versions, $($script:targets[0]) to $($script:targets[-1])"
    } -Always
    # Two folders: the next version downloads into one while the other is checked (each downloads only the files that
    # changed since the version before it there).
    $apps = @((Join-Path $ymm4 'app-a'), (Join-Path $ymm4 'app-b'))
    $kinds = @('scan', 'start', 'keys', 'probe', 'animation', 'psd')
    $queue = @($targets | Where-Object { $version = $_; @($kinds | Where-Object { -not (Test-Passed "v$version-$_") }).Count -gt 0 })
    $prefetch = $null
    for ($index = 0; $index -lt $targets.Count; $index++) {
        $version = $targets[$index]
        $prefix = "v$version"
        $stepIds = @($kinds | ForEach-Object { "$prefix-$_" })
        $position = [Array]::IndexOf($queue, $version)
        $app = $apps[[Math]::Max(0, $position) % 2]
        $fetched = $true
        if ($position -ge 0) {
            if ($prefetch -and $prefetch.Version -eq $version) { $fetched = Complete-Fetch $prefetch }
            else { $fetched = Invoke-Step 'versions' "$prefix-fetch" "YMM4 ${version}: download (the files that changed)" { Invoke-Fetch $version $app '--app-ffmpeg' } -Always }
            $prefetch = $null
            if ($position + 1 -lt $queue.Count) { $prefetch = Start-Fetch $queue[$position + 1] $apps[($position + 1) % 2] }
        }
        if (-not $fetched) {
            [IO.File]::WriteAllText((Join-Path $results "$version.scan.json"), '{"error":"download failed"}', $utf8)
            foreach ($id in $stepIds) { Skip-Step 'versions' $id "YMM4 ${version}" 'download failed' }
            continue
        }
        if ($position -ge 0) { Reset-User $app }
        [void](Invoke-Step 'versions' "$prefix-scan" "YMM4 ${version}: files (runtime, references, APIs, contracts)" {
            $scan = Invoke-Logged $script:stepLog 'dotnet' @($fingerprint, 'scan', $app, $pluginDir)
            $text = $scan.Stdout
            if ($scan.ExitCode -ne 0) { $text = '{"error":"scan failed"}' }
            [IO.File]::WriteAllText((Join-Path $results "$version.scan.json"), $text, $utf8)
            if ($scan.ExitCode -ne 0) { throw "the scan exited with code $($scan.ExitCode)" }
        })
        [void](Invoke-Step 'versions' "$prefix-start" "YMM4 ${version}: started with the plugin (what it enabled)" {
            Reset-User $app
            $out = Join-Path $results "$version.start.json"
            Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
            try {
                Invoke-Native $windowsPowerShell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\compat\start-check.ps1'),
                    '-HostDir', $app, '-PluginDir', $pluginDir, '-Out', $out) 10
            }
            finally { Reset-User $app }
            if (-not (Test-Path -LiteralPath $out)) { throw 'start-check wrote no result' }
            $start = [IO.File]::ReadAllText($out, $utf8) | ConvertFrom-Json
            if (-not $start.status) { throw "the plugin reported no status (not loaded); dialogs: $(@($start.dialogs) -join ' / ')" }
            $features = $start.status.features
            $off = @()
            if ($features) { $off = @($features.PSObject.Properties | Where-Object { $_.Value -is [bool] -and -not $_.Value } | ForEach-Object { $_.Name }) }
            $script:stepNote = "export hook $($start.status.exportHooked), cache $($start.status.cacheAvailable)"
            if ($off.Count -gt 0) { $script:stepNote += "; off: $($off -join ', ')" }
            if (-not $start.status.exportHooked) { throw "the NVENC export hook is not connected: $($start.status.exportProblem)" }
        })
        [void](Invoke-Step 'versions' "$prefix-keys" "YMM4 ${version}: CacheChecks (real host)" { Invoke-HostProject 'tests\CacheChecks\CacheChecks.csproj' @(($app + '\')) })
        [void](Invoke-Step 'versions' "$prefix-probe" "YMM4 ${version}: HostCacheProbe --unread --gpu --video (export hook, pixel parity)" {
            Invoke-HostProject 'tests\HostCacheProbe\HostCacheProbe.csproj' @(($app + '\'), '--unread', '--gpu', '--video', $video)
        })
        [void](Invoke-Step 'versions' "$prefix-animation" "YMM4 ${version}: HostCacheProbe --unread --animation-tachie-check" {
            Invoke-HostProject 'tests\HostCacheProbe\HostCacheProbe.csproj' @(($app + '\'), '--unread', '--animation-tachie-check')
        })
        [void](Invoke-Step 'versions' "$prefix-psd" "YMM4 ${version}: HostCacheProbe --unread --psd-tachie-check" {
            Invoke-HostProject 'tests\HostCacheProbe\HostCacheProbe.csproj' @(($app + '\'), '--unread', '--psd-tachie-check')
        })
        $tests = [ordered]@{ build = Get-OutcomeOf @('setup-package'); keys = Get-OutcomeOf @("$prefix-keys")
            probe = Get-OutcomeOf @("$prefix-probe", "$prefix-animation", "$prefix-psd") }
        [IO.File]::WriteAllText((Join-Path $results "$version.tests.json"), ($tests | ConvertTo-Json -Compress), $utf8)
    }
    if ($prefetch) { [void](Complete-Fetch $prefetch) }
    if ($listed) {
        [void](Invoke-Step 'versions' 'versions-report' 'The version table (tools\compat\ymm4_compat.py merge and render, into the run folder)' {
            $report = Join-Path $run 'report'
            if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $report | Out-Null
            $data = Join-Path $report 'ymm4-versions.json'
            Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $report 'README.md')
            Invoke-Python @('tools\compat\ymm4_compat.py', 'merge', $results, $data, '--plugin', $pluginLabel, '--commit', $commit, '--run', 'local-full-check')
            Invoke-Python @('tools\compat\ymm4_compat.py', 'render', $data, (Join-Path $report 'README.md'), (Join-Path $report 'YMM4_VERSIONS.md'))
        } -Always)
    }
}

# ---------- summary ----------

$md = New-Object System.Text.StringBuilder
function Add-Md([string] $Line = '') { [void]$md.AppendLine($Line) }
function Add-Code([string[]] $Lines) {
    Add-Md '```text'
    foreach ($line in $Lines) { Add-Md $line }
    Add-Md '```'
}
function Format-Cell([string] $Text) { ($Text -replace '\|', '/') -replace "`r?`n", ' ' }

$counts = @($records | Group-Object Status | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Count)" })
$replayed = @($records | Where-Object { $_.Replayed }).Count
Add-Md "# local-full-check $($started.ToString('yyyy-MM-dd HH:mm'))"
Add-Md
Add-Md "Commit ``$commit`` ($branch), plugin $pluginLabel. Parts: $($Parts -join ', '). -NoNvenc:$([bool]$NoNvenc). This run took $([Math]::Round(((Get-Date) - $started).TotalMinutes)) min."
Add-Md
Add-Md "Results: $($counts -join ', ')$(if ($replayed) { " ($replayed passed in an earlier run of this commit)" })."
Add-Md
Add-Md '## Environment'
Add-Code $envLines
Add-Md
Add-Md '## Results'
foreach ($part in @('setup', 'latest', 'rtx', 'gui', 'versions')) {
    # Per version only the failures here; every version is in the table below.
    $rows = @($records | Where-Object { $_.Part -eq $part -and ($part -ne 'versions' -or $_.Id -notmatch '^v\d' -or $_.Status -eq 'FAIL') })
    if ($rows.Count -eq 0) { continue }
    Add-Md
    Add-Md "### $part"
    Add-Md
    Add-Md '| Id | Check | Result | Min | Note |'
    Add-Md '| --- | --- | --- | ---: | --- |'
    foreach ($row in $rows) {
        $status = $row.Status
        if ($row.Replayed) { $status += ' (earlier)' }
        Add-Md "| $($row.Id) | $(Format-Cell $row.Title) | $status | $($row.Minutes) | $(Format-Cell $row.Note) |"
    }
}

$versionRows = @($records | Where-Object { $_.Id -match '^v(\d+(\.\d+){3})-(scan|start|keys|probe|animation|psd)$' })
if ($versionRows.Count -gt 0) {
    Add-Md
    Add-Md '### Each version'
    Add-Md
    Add-Md 'scan: files, start: YMM4 started with the plugin, keys: CacheChecks, probe: --unread --gpu --video, animation / psd: the tachie checks. Note: what the plugin reported at the start.'
    Add-Md
    Add-Md '| YMM4 | scan | start | keys | probe | animation | psd | Min | Note |'
    Add-Md '| --- | --- | --- | --- | --- | --- | --- | ---: | --- |'
    foreach ($group in @($versionRows | Group-Object { $_.Id.Substring(1, $_.Id.LastIndexOf('-') - 1) } | Sort-Object { [version]$_.Name } -Descending)) {
        $cells = foreach ($kind in @('scan', 'start', 'keys', 'probe', 'animation', 'psd')) {
            $found = @($group.Group | Where-Object { $_.Id -eq "v$($group.Name)-$kind" })
            if ($found.Count -eq 0) { '-' } else { $found[-1].Status }
        }
        $minutes = [Math]::Round((@($group.Group | Measure-Object Minutes -Sum).Sum), 1)
        $startRow = @($group.Group | Where-Object { $_.Id -eq "v$($group.Name)-start" }) | Select-Object -Last 1
        $note = ''
        if ($startRow) { $note = $startRow.Note }
        Add-Md "| $($group.Name) | $($cells -join ' | ') | $minutes | $(Format-Cell $note) |"
    }
}

$failed = @($records | Where-Object { $_.Status -eq 'FAIL' })
if ($failed.Count -gt 0) {
    Add-Md
    Add-Md '## Failures (last 60 log lines)'
    foreach ($row in $failed) {
        Add-Md
        Add-Md "### $($row.Id): $(Format-Cell $row.Note)"
        Add-Code @($row.Tail -split "`n")
    }
}

# What the checks themselves passed over (a feature the host does not allow, a missing tool): read from every log.
$skippedLines = New-Object System.Collections.Generic.List[string]
foreach ($row in @($records | Where-Object { $_.Status -ne 'SKIP' })) {
    $log = Join-Path $logs "$($row.Id).log"
    if (-not (Test-Path -LiteralPath $log)) { continue }
    foreach ($line in @([IO.File]::ReadAllLines($log, $utf8) | Where-Object { $_ -match '(?i)\bskip(ped|ping|s)?\b' -and $_ -notmatch '^B64 ' } | Select-Object -Unique)) {
        if ($skippedLines.Count -lt 300) { $skippedLines.Add("$($row.Id): $($line.Trim())") }
    }
}
if ($skippedLines.Count -gt 0) {
    Add-Md
    Add-Md '## Lines that say a check was skipped'
    Add-Code $skippedLines
}

$rtxSummary = Join-Path $run 'rtx-check-summary.md'
if (Test-Path -LiteralPath $rtxSummary) {
    Add-Md
    Add-Md '## rtx-check'
    foreach ($line in [IO.File]::ReadAllLines($rtxSummary, $utf8)) {
        if ($line -match '^#') { Add-Md ('##' + $line) } else { Add-Md $line }
    }
}

$readme = Join-Path $run 'report\README.md'
if (Test-Path -LiteralPath $readme) {
    $text = [IO.File]::ReadAllText($readme, $utf8)
    $block = [regex]::Match($text, '(?s)<!-- ymm4-versions:begin -->(.*?)<!-- ymm4-versions:end -->')
    if ($block.Success) {
        Add-Md
        Add-Md '## The version table of this run (as the README would show it)'
        Add-Md
        Add-Md 'Per version: report\YMM4_VERSIONS.md and report\ymm4-versions.json in the run folder.'
        Add-Md
        Add-Md $block.Groups[1].Value.Trim()
    }
}

$summary = $md.ToString().Replace($root.TrimEnd('\'), '<repo>').Replace($WorkDir, '<work>')
foreach ($personal in @($env:USERPROFILE, $env:USERNAME) | Where-Object { $_ -and $_.Length -ge 3 }) { $summary = $summary.Replace($personal, '<user>') }
$summaryPath = Join-Path $run 'summary.md'
[IO.File]::WriteAllText($summaryPath, $summary, $utf8)
try { Compress-Archive -Path (Join-Path $logs '*') -DestinationPath (Join-Path $run 'logs.zip') -Force } catch { Write-Host "zip failed: $($_.Exception.Message)" -ForegroundColor Yellow }
[IO.File]::WriteAllText($progressFile, "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) finished: $($counts -join ', ')`r`n", $utf8)
if ($savedEncoding) { try { [Console]::OutputEncoding = $savedEncoding } catch { } }

Write-Host ''
Write-Host "Results: $($counts -join ', ')"
Write-Host "Summary: $summaryPath"
Write-Host "Logs:    $(Join-Path $run 'logs.zip')"
Write-Host 'summary.md shows local paths as <repo>, <work> and <user>.'
if ($failed.Count -gt 0) { exit 1 }
exit 0
