<#
.SYNOPSIS
One-command check on a PC with an NVIDIA GPU (RTX 3060): builds the plugin and runs, on real NVENC and the real GPU,
the smoke tests, the host cache tests, preview timing, NVENC throughput, the color matrix check (finding #5) and a
VUI experiment. Writes dist\rtx-check\<time>\summary.md and a zip of every log next to it.

.DESCRIPTION
Runs each step as its own process and keeps going after a failure, so one report shows every result. The steps
follow build.ps1 -Smoke but are split up, because -Smoke stops at the first failure and the later steps (NativeSmoke,
the benchmark) would then not run. -NoNvenc is for a Windows machine without NVENC (a CI runner): it skips the steps
that encode and checks the rest. The VUI experiment patches NvencNative.cpp, measures, and always restores the file
byte for byte; it is skipped when that file has local changes. Nothing is pushed or installed into YMM4.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rtx-check.ps1 -Ymm4DirPath 'D:\YukkuriMovieMaker_v4_Lite'

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rtx-check.ps1 -Ymm4DirPath C:\ymm4-host -NoNvenc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Ymm4DirPath,
    [switch] $NoNvenc,
    [ValidateRange(1, 10)] [int] $PreviewRuns = 3,
    [ValidateRange(1, 10)] [int] $BenchmarkRuns = 3,
    [switch] $SkipVuiExperiment
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$started = Get-Date
if (-not (Test-Path -LiteralPath (Join-Path $Ymm4DirPath 'YukkuriMovieMaker.Plugin.dll'))) {
    throw "YMM4 DLL not found in $Ymm4DirPath"
}
$hostDir = (Resolve-Path -LiteralPath $Ymm4DirPath).Path.TrimEnd('\') + '\'
$running = @(Get-Process -Name 'YukkuriMovieMaker*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($hostDir, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) { throw "YMM4 is running from $hostDir. Close it, and preferably pass a copy of the YMM4 folder." }
$out = Join-Path $root ('dist\rtx-check\' + $started.ToString('yyyyMMdd-HHmmss'))
$media = Join-Path $out 'media'
New-Item -ItemType Directory -Force -Path $media | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false
$results = New-Object System.Collections.Generic.List[object]
$shell = (Get-Process -Id $PID).Path
$savedEncoding = $null
try { $savedEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = $utf8 } catch { }
$script:checkLog = $null

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

function Invoke-Logged([string] $Log, [string] $FilePath, [string[]] $Arguments) {
    $stdout = "$Log.stdout"
    $stderr = "$Log.stderr"
    $start = @{ FilePath = $FilePath; WorkingDirectory = $root; NoNewWindow = $true; PassThru = $true
        RedirectStandardOutput = $stdout; RedirectStandardError = $stderr }
    $commandLine = ConvertTo-CommandLine $Arguments
    if ($commandLine) { $start.ArgumentList = $commandLine }  # Start-Process rejects an empty argument list
    $process = Start-Process @start
    $null = $process.Handle
    $process.WaitForExit()
    $text = ''
    if (Test-Path -LiteralPath $stdout) { $text = [IO.File]::ReadAllText($stdout, $utf8) }
    if ((Test-Path -LiteralPath $stderr) -and (Get-Item -LiteralPath $stderr).Length -gt 0) {
        $text += "`r`n----- stderr -----`r`n" + [IO.File]::ReadAllText($stderr, $utf8)
    }
    Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue
    $header = "> $FilePath $commandLine`r`n"
    [IO.File]::AppendAllText($Log, $header + $text + "`r`n> exit code $($process.ExitCode)`r`n", $utf8)
    @{ ExitCode = $process.ExitCode; Text = $text }
}

# Inside a check: runs a program into the check's log and throws on a nonzero exit code.
function Invoke-Native([string] $FilePath, [string[]] $Arguments) {
    $run = Invoke-Logged $script:checkLog $FilePath $Arguments
    if ($run.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($FilePath)) exited with code $($run.ExitCode)" }
    $run.Text
}

function Write-CheckLog([string] $Text) { [IO.File]::AppendAllText($script:checkLog, $Text + "`r`n", $utf8) }

function Add-Result([string] $Id, [string] $Title, [string] $Status, [string] $Note, [double] $Seconds, [string[]] $Found, [string] $Text) {
    $tail = ''
    if ($Status -eq 'FAIL' -and $Text) { $tail = (($Text -split "`r?`n") | Select-Object -Last 60) -join "`n" }
    $results.Add([pscustomobject]@{ Id = $Id; Title = $Title; Status = $Status; Note = $Note; Seconds = [Math]::Round($Seconds); Found = @($Found); Tail = $tail })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow'; INFO = 'Cyan' }[$Status]
    $suffix = ''
    if ($Note) { $suffix = " - $Note" }
    Write-Host ('[{0}] {1}: {2} ({3} s){4}' -f $Id, $Title, $Status, [Math]::Round($Seconds), $suffix) -ForegroundColor $color
}

function Skip-Check([string] $Id, [string] $Title, [string] $Reason) { Add-Result $Id $Title 'SKIP' $Reason 0 @() '' }

# Runs a block as one result. The block throws to fail; every expected line must appear in the check's log.
function Invoke-Check([string] $Id, [string] $Title, [scriptblock] $Body, [string[]] $Expect = @(), [string] $Status = 'PASS') {
    Write-Host "[$Id] $Title ..."
    $script:checkLog = Join-Path $out "$Id.log"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $note = ''
    try { $null = & $Body }
    catch { $Status = 'FAIL'; $note = $_.Exception.Message; Write-CheckLog "FAILED: $note" }
    $text = ''
    if (Test-Path -LiteralPath $script:checkLog) { $text = [IO.File]::ReadAllText($script:checkLog, $utf8) }
    $found = @($Expect | Where-Object { $text.Contains($_) })
    $missing = @($Expect | Where-Object { -not $text.Contains($_) })
    if ($Status -ne 'FAIL' -and $missing.Count -gt 0) { $Status = 'FAIL'; $note = 'missing output: ' + ($missing -join ' | ') }
    Add-Result $Id $Title $Status $note $watch.Elapsed.TotalSeconds $found $text
    $Status -ne 'FAIL'
}

function Get-Lines([string] $Path, [string] $Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    @([IO.File]::ReadAllLines($Path, $utf8) | Where-Object { $_ -match $Pattern })
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

function Test-Tool([string] $Name) { [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

$msbuild = Find-Msbuild
$hasDotnet = Test-Tool 'dotnet'
$hasFfmpeg = (Test-Tool 'ffmpeg') -and (Test-Tool 'ffprobe')
$hasGit = Test-Tool 'git'
$hasNvidiaSmi = Test-Tool 'nvidia-smi'
$python = $null
$pythonArgs = @()
foreach ($candidate in @('python', 'py')) {
    if (-not (Test-Tool $candidate)) { continue }
    $prefix = @()
    if ($candidate -eq 'py') { $prefix = @('-3') }
    $version = & $candidate @prefix --version 2>&1
    if ($LASTEXITCODE -eq 0 -and "$version" -match 'Python 3') { $python = $candidate; $pythonArgs = $prefix; break }
}
if (-not $hasDotnet) { throw 'dotnet (.NET SDK 10) not found.' }
if (-not $msbuild) { throw 'Visual Studio Build Tools with the C++ workload (MSBuild) not found.' }
if (-not $NoNvenc -and -not $hasNvidiaSmi) {
    throw 'No NVIDIA driver found (nvidia-smi). On a machine without NVENC, add -NoNvenc.'
}
if (-not $NoNvenc -and -not $hasFfmpeg) { throw 'ffmpeg and ffprobe must be on PATH.' }

$testBin = Join-Path $root 'tests\bin\Release'
$nativeDll = Join-Path $root 'NvencNative\bin\Release\NvencNative.dll'
$hostArg = '-p:YMM4DirPath=' + $hostDir

# ---------- environment ----------

$envLines = New-Object System.Collections.Generic.List[string]
$commit = 'unknown'
if ($hasGit) { $commit = (& git rev-parse HEAD 2>$null) }
$envLines.Add("commit: $commit")
$envLines.Add("mode: $(if ($NoNvenc) { 'NoNvenc (no encoding steps)' } else { 'NVENC' })")
$envLines.Add("YMM4: $hostDir $((Get-Item -LiteralPath (Join-Path $hostDir 'YukkuriMovieMaker.dll') -ErrorAction SilentlyContinue).VersionInfo.FileVersion)")
foreach ($gpu in @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)) { $envLines.Add("video controller: $($gpu.Name), driver $($gpu.DriverVersion)") }
if ($hasNvidiaSmi) {
    $smi = & nvidia-smi --query-gpu=name,driver_version,memory.total,pcie.link.gen.current,pcie.link.width.current --format=csv,noheader 2>$null
    $envLines.Add("nvidia-smi: $smi")
}
$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cpu) { $envLines.Add("CPU: $($cpu.Name), $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads") }
$system = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
if ($system) { $envLines.Add("RAM: $([Math]::Round($system.TotalPhysicalMemory / 1GB, 1)) GB") }
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { $envLines.Add("OS: $($os.Caption) $($os.Version) (build $($os.BuildNumber))") }
$envLines.Add("PowerShell: $($PSVersionTable.PSVersion)")
$envLines.Add("dotnet: $(& dotnet --version 2>$null)")
$envLines.Add("MSBuild: $msbuild")
if ($hasFfmpeg) { $envLines.Add("ffmpeg: $((& ffmpeg -version 2>$null) | Select-Object -First 1)") } else { $envLines.Add('ffmpeg: not found') }
if ($python) { $envLines.Add("python: $(& $python @pythonArgs --version 2>&1)") } else { $envLines.Add('python: not found') }
foreach ($volume in @(Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter)) {
    $envLines.Add("volume $($volume.DriveLetter): $($volume.FileSystemType), $($volume.DriveType), $([Math]::Round($volume.SizeRemaining / 1GB)) GB free")
}
[IO.File]::WriteAllLines((Join-Path $out 'environment.txt'), $envLines, $utf8)
$envLines | ForEach-Object { Write-Host $_ }

# ---------- build and checks ----------

$built = Invoke-Check 'build' 'build.ps1 (native, plugin, host load check, package)' {
    Invoke-Native $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'build.ps1'), '-Ymm4DirPath', $hostDir.TrimEnd('\'))
} -Expect @('Package:')

[void](Invoke-Check 'native-checks' 'NativeChecks (native invariants)' {
    Invoke-Native $msbuild @((Join-Path $root 'tests\NativeChecks.vcxproj'), '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/nologo', '/v:minimal')
    Invoke-Native (Join-Path $testBin 'NativeChecks.exe') @()
})

$managedExpect = @('Dedicated MTA thread, exception propagation and failure cleanup OK', 'Managed failure paths OK',
    'Cancellation stops pending writer calls before encoding')
if ($NoNvenc) {
    [void](Invoke-Check 'managed-smoke' 'ManagedSmoke without a GPU (dispatcher, failure paths)' {
        Invoke-Native 'dotnet' @('run', '--project', 'tests\ManagedSmoke\ManagedSmoke.csproj', '-c', 'Release', $hostArg, '--no-launch-profile')
    } -Expect $managedExpect)
} else {
    [void](Invoke-Check 'managed-smoke' 'ManagedSmoke on NVENC (#6: no partial file is left)' {
        Invoke-Native 'dotnet' @('run', '--project', 'tests\ManagedSmoke\ManagedSmoke.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $media)
    } -Expect ($managedExpect + @('Managed audio-first GPU output OK', 'Concurrent host audio/video and Dispose OK',
        'Cancelled and incomplete GPU exports preserved existing output; complete export published OK', 'Invalid GPU input preserved existing output OK')))
}

$audioFirst = Join-Path $media 'managed-audio-first.mp4'
if ($NoNvenc) { Skip-Check 'audio-first' 'Audio-first MP4: streams, decode, audio samples' 'no NVENC' }
elseif (-not (Test-Path -LiteralPath $audioFirst)) { Skip-Check 'audio-first' 'Audio-first MP4: streams, decode, audio samples' 'ManagedSmoke wrote no output' }
else {
    [void](Invoke-Check 'audio-first' 'Audio-first MP4: streams, decode, audio samples' {
        $probe = (Invoke-Native 'ffprobe' @('-v', 'error', '-show_entries', 'stream=codec_name,nb_frames', '-of', 'json', $audioFirst)) | ConvertFrom-Json
        if (@($probe.streams | Where-Object { $_.codec_name -eq 'h264' -and $_.nb_frames -eq '30' }).Count -ne 1 -or
            @($probe.streams | Where-Object { $_.codec_name -eq 'aac' }).Count -ne 1) { throw 'expected one 30-frame h264 stream and one aac stream' }
        Invoke-Native 'ffmpeg' @('-v', 'error', '-i', $audioFirst, '-f', 'null', '-')
        $pcmPath = Join-Path $media 'managed-audio-first.s16le'
        Invoke-Native 'ffmpeg' @('-v', 'error', '-i', $audioFirst, '-vn', '-ac', '1', '-ar', '48000', '-f', 's16le', '-y', $pcmPath)
        $pcm = [IO.File]::ReadAllBytes($pcmPath)
        $peak = 0
        for ($i = 0; $i + 1 -lt $pcm.Length; $i += 2) { $peak = [Math]::Max($peak, [Math]::Abs([int][BitConverter]::ToInt16($pcm, $i))) }
        Write-CheckLog "PCM bytes $($pcm.Length), peak $peak"
        if ($pcm.Length -lt 96000 -or $pcm.Length -gt 100000 -or $peak -lt 2000 -or $peak -gt 5000) { throw "audio samples lost or changed: $($pcm.Length) bytes, peak $peak" }
        Remove-Item -LiteralPath $pcmPath
    })
}

foreach ($check in @(@('store-checks', 'tests\StoreChecksHarness\StoreChecks.csproj', 'StoreChecks (store, memory policy, idle traversal)'),
        @('readiness-checks', 'tests\ReadinessChecks\ReadinessChecks.csproj', 'ReadinessChecks (render readiness)'),
        @('file-lease-checks', 'tests\FileLeaseChecks\FileLeaseChecks.csproj', 'FileLeaseChecks (external file leases)'))) {
    $project = $check[1]
    [void](Invoke-Check $check[0] $check[2] { Invoke-Native 'dotnet' @('run', '--project', $project, '-c', 'Release', '--no-launch-profile') })
}

[void](Invoke-Check 'cache-checks' 'CacheChecks against the real host (#1 #2 #4 #9)' {
    Invoke-Native 'dotnet' @('run', '--project', 'tests\CacheChecks\CacheChecks.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $hostDir)
} -Expect @('Layer settings as YMM4 saves them:', 'Per-frame keys: unrelated frames survive edits, boundaries, settings, per-frame files, scene items OK',
    'Unverifiable file: only its frames render normally, idle passes them, no repeated project-wide verification OK'))

# --video wants a real H.264 MP4: the NVENC output, or one made with ffmpeg without NVENC.
$video = $null
if (Test-Path -LiteralPath $audioFirst) { $video = $audioFirst }
elseif ($hasFfmpeg) {
    $generated = Join-Path $media 'probe-video.mp4'
    & ffmpeg -v error -y -f lavfi -i 'testsrc2=size=320x180:rate=30' -t 1 -c:v libx264 -pix_fmt yuv420p $generated 2>$null
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $generated)) { $video = $generated }
}
$gpuArgs = @('run', '--project', 'tests\HostCacheProbe\HostCacheProbe.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $hostDir, '--gpu')
if ($video) { $gpuArgs += @('--video', $video) }
[void](Invoke-Check 'host-gpu' "HostCacheProbe --gpu$(if ($video) { ' --video' }) on the default adapter (#6 #7 #4 #9)" {
    Invoke-Native 'dotnet' $gpuArgs
} -Expect @('NVENC export: missing frames, cancellation and failure delete the partial file OK',
    "NVENC options: saved and loaded through the plugin settings and YMM4's JSON OK",
    'Idle pre-render: independent scene clone, cancelled commit guard and unverifiable frames passed over OK'))

[void](Invoke-Check 'host-integration' 'HostCacheProbe --integration' {
    Invoke-Native 'dotnet' @('run', '--project', 'tests\HostCacheProbe\HostCacheProbe.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $hostDir, '--integration')
})

function Invoke-NativeSmoke([string] $Folder) {
    Invoke-Native $msbuild @((Join-Path $root 'tests\NativeSmoke.vcxproj'), '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/nologo', '/v:minimal')
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $testBin 'NvencNative.dll') -Force
    foreach ($case in @(@('h264', '0'), @('hevc', '1'))) {
        $output = Join-Path $Folder "smoke-$($case[0]).mp4"
        Invoke-Native (Join-Path $testBin 'NativeSmoke.exe') @($output, $case[1])
        $probe = (Invoke-Native 'ffprobe' @('-v', 'error', '-show_entries', 'stream=codec_name,nb_frames', '-of', 'json', $output)) | ConvertFrom-Json
        if (@($probe.streams | Where-Object { $_.codec_name -eq $case[0] }).Count -ne 1 -or @($probe.streams | Where-Object { $_.codec_name -eq 'aac' }).Count -ne 1) {
            throw "unexpected streams in $output"
        }
        Invoke-Native 'ffmpeg' @('-v', 'error', '-i', $output, '-f', 'null', '-')
        Write-CheckLog "Smoke OK: $($case[0]) + aac"
    }
}

if ($NoNvenc) { Skip-Check 'native-smoke' 'NativeSmoke H.264/HEVC + AAC, cancel and failure' 'no NVENC' }
else {
    [void](Invoke-Check 'native-smoke' 'NativeSmoke H.264/HEVC + AAC, cancel and failure' {
        Invoke-NativeSmoke $media
        foreach ($case in @(@('h264', '0'), @('hevc', '1'))) {
            foreach ($mode in @('cancel', 'failure')) {
                Invoke-Native (Join-Path $testBin 'NativeSmoke.exe') @((Join-Path $media "smoke-$($case[0])-$mode.partial"), $case[1], $mode)
                Write-CheckLog "Abort OK: $($case[0]) $mode"
            }
        }
    } -Expect @('Smoke OK: h264 + aac', 'Smoke OK: hevc + aac', 'Abort OK: h264 cancel', 'Abort OK: h264 failure', 'Abort OK: hevc cancel', 'Abort OK: hevc failure'))
}

foreach ($run in 1..$PreviewRuns) {
    [void](Invoke-Check "preview-$run" "HostCacheProbe --preview-performance, run $run of $PreviewRuns" {
        Invoke-Native 'dotnet' @('run', '--project', 'tests\HostCacheProbe\HostCacheProbe.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $hostDir, '--preview-performance')
        Copy-Item -LiteralPath (Join-Path $root 'dist\preview-performance.json') -Destination (Join-Path $out "preview-performance-$run.json") -Force
    } -Expect @('frames pixel-exact'))
}

if ($NoNvenc) { Skip-Check 'benchmark' 'NVENC throughput, 1080p, 300 frames, 7 cases' 'no NVENC' }
elseif (-not (Test-Path -LiteralPath (Join-Path $testBin 'NativeSmoke.exe'))) { Skip-Check 'benchmark' 'NVENC throughput, 1080p, 300 frames, 7 cases' 'NativeSmoke was not built' }
else {
    [void](Invoke-Check 'benchmark' "NVENC throughput, 1080p, 300 frames, 7 cases x $BenchmarkRuns" {
        Invoke-Native $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tests\benchmark.ps1'), '-Runs', "$BenchmarkRuns")
        Copy-Item -LiteralPath (Join-Path $root 'dist\benchmark\results.csv') -Destination (Join-Path $out 'benchmark.csv') -Force
    })
}

function Invoke-ColorCheck([string] $Folder, [string] $Json) {
    $arguments = @($pythonArgs) + @((Join-Path $root 'tools\color-matrix-check.py'), '--json', $Json)
    foreach ($name in @('managed-audio-first.mp4', 'managed-threaded.mp4')) {
        if (Test-Path -LiteralPath (Join-Path $Folder $name)) { $arguments += @('--managed', (Join-Path $Folder $name)) }
    }
    foreach ($name in @('smoke-h264.mp4', 'smoke-hevc.mp4')) {
        if (Test-Path -LiteralPath (Join-Path $Folder $name)) { $arguments += @('--native', (Join-Path $Folder $name)) }
    }
    if ($arguments -notcontains '--managed' -and $arguments -notcontains '--native') { throw "no smoke output in $Folder" }
    Invoke-Native $python $arguments
    foreach ($name in @('smoke-h264.mp4', 'smoke-hevc.mp4')) {
        $file = Join-Path $Folder $name
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $headers = Invoke-Logged ($script:checkLog + '.headers') 'ffmpeg' @('-v', 'info', '-i', $file, '-c', 'copy', '-bsf:v', 'trace_headers', '-frames:v', '1', '-f', 'null', '-')
        $vui = @(($headers.Text -split "`r?`n") | Where-Object { $_ -match 'vui_parameters_present_flag|video_signal_type_present_flag|video_full_range_flag|colour_description_present_flag|colour_primaries|transfer_characteristics|matrix_coefficients' })
        Write-CheckLog "VUI $name ($($vui.Count) lines):"
        foreach ($line in $vui) { Write-CheckLog ('  vui: ' + ($line -replace '^\[[^\]]*\]\s*', '')) }
        Remove-Item -LiteralPath ($script:checkLog + '.headers') -ErrorAction SilentlyContinue
    }
}

$colorExpect = @('best:', 'VUI smoke-h264.mp4')
if ($NoNvenc) { Skip-Check 'color' 'Color matrix and VUI of the NVENC output (#5)' 'no NVENC' }
elseif (-not $python) { Skip-Check 'color' 'Color matrix and VUI of the NVENC output (#5)' 'Python 3 not found' }
else {
    [void](Invoke-Check 'color' 'Color matrix and VUI of the NVENC output (#5)' { Invoke-ColorCheck $media (Join-Path $out 'color.json') } -Expect $colorExpect -Status 'INFO')
}

# OneDrive: attributes only (no file names), to see whether synced files carry reparse points (#3).
$oneDriveRows = @()
$oneDriveRoots = @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
if (-not $oneDriveRoots) { Skip-Check 'onedrive' 'OneDrive file attributes (#3)' 'no OneDrive folder' }
else {
    [void](Invoke-Check 'onedrive' 'OneDrive file attributes (#3)' {
        foreach ($folder in $oneDriveRoots) {
            $drive = (Get-Item -LiteralPath $folder).PSDrive.Name
            $fileSystem = (Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue).FileSystemType
            $files = @(Get-ChildItem -LiteralPath $folder -File -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 20)
            foreach ($file in $files) {
                $script:oneDriveRows += [pscustomobject]@{ Drive = "${drive}: $fileSystem"; Extension = $file.Extension; SizeKiB = [Math]::Round($file.Length / 1KB)
                    Attributes = $file.Attributes.ToString(); Hex = '0x{0:X}' -f [int]$file.Attributes }
            }
        }
        foreach ($row in $script:oneDriveRows) { Write-CheckLog "$($row.Drive) $($row.Extension) $($row.SizeKiB) KiB $($row.Hex) $($row.Attributes)" }
    } -Status 'INFO')
    $oneDriveRows = $script:oneDriveRows
}

# VUI experiment: does signaling BT.709 in the VUI also change the conversion NVENC applies?
$source = Join-Path $root 'NvencNative\NvencNative.cpp'
$vuiMedia = Join-Path $out 'media-vui-bt709'
$vuiSnippet = @(
    '        // EXPERIMENT (tools/rtx-check.ps1, restored afterwards): signal BT.709 limited range in the VUI.',
    '        auto signalBt709 = [](NV_ENC_CONFIG_H264_VUI_PARAMETERS& vui)',
    '        {',
    '            vui.videoSignalTypePresentFlag = 1;',
    '            vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;',
    '            vui.videoFullRangeFlag = 0;',
    '            vui.colourDescriptionPresentFlag = 1;',
    '            vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;',
    '            vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;',
    '            vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT709;',
    '        };',
    '        if (codec == kCodecHevc)',
    '            signalBt709(state->config.encodeCodecConfig.hevcConfig.hevcVUIParameters);',
    '        else if (codec == kCodecH264)',
    '            signalBt709(state->config.encodeCodecConfig.h264Config.h264VUIParameters);')
$sourceChanged = $false
if ($hasGit) { $sourceChanged = [bool](& git status --porcelain -- 'NvencNative/NvencNative.cpp' 2>$null) }
$patched = $false
if ($NoNvenc) { Skip-Check 'vui-experiment' 'VUI BT.709 experiment' 'no NVENC' }
elseif ($SkipVuiExperiment) { Skip-Check 'vui-experiment' 'VUI BT.709 experiment' '-SkipVuiExperiment' }
elseif (-not $python) { Skip-Check 'vui-experiment' 'VUI BT.709 experiment' 'Python 3 not found' }
elseif ($sourceChanged) { Skip-Check 'vui-experiment' 'VUI BT.709 experiment' 'NvencNative.cpp has local changes' }
else {
    $original = [IO.File]::ReadAllBytes($source)
    try {
        [void](Invoke-Check 'vui-experiment' 'VUI BT.709 experiment (patched build, restored afterwards)' {
            $text = [IO.File]::ReadAllText($source)
            $newline = "`n"
            if ($text.Contains("`r`n")) { $newline = "`r`n" }
            $anchor = @('        if (codec == kCodecHevc)', '        {', '            state->config.encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;') -join $newline
            $at = $text.IndexOf($anchor)
            if ($at -lt 0 -or $text.IndexOf($anchor, $at + 1) -ge 0) { throw 'the patch anchor was not found exactly once in NvencNative.cpp' }
            [IO.File]::WriteAllText($source, $text.Substring(0, $at) + ($vuiSnippet -join $newline) + $newline + $text.Substring($at), $utf8)
            $script:patched = $true
            New-Item -ItemType Directory -Force -Path $vuiMedia | Out-Null
            Invoke-Native $msbuild @((Join-Path $root 'NvencNative\NvencNative.vcxproj'), '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/m', '/nologo', '/v:minimal')
            Invoke-Native 'dotnet' @('run', '--project', 'tests\ManagedSmoke\ManagedSmoke.csproj', '-c', 'Release', $hostArg, '--no-launch-profile', '--', $vuiMedia)
            Invoke-NativeSmoke $vuiMedia
            Invoke-ColorCheck $vuiMedia (Join-Path $out 'color-vui-bt709.json')
        } -Expect $colorExpect -Status 'INFO')
    }
    finally { [IO.File]::WriteAllBytes($source, $original) }
}

if ($patched) {
    # Rebuild from the restored source, so dist\ and the test binaries match the commit again.
    [void](Invoke-Check 'restore-build' 'Rebuild after the experiment (restored source)' {
        Invoke-Native $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'build.ps1'), '-Ymm4DirPath', $hostDir.TrimEnd('\'))
        Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $testBin 'NvencNative.dll') -Force
    } -Expect @('Package:'))
}

[void](Invoke-Check 'leftovers' 'No partial files or staging folders left in TEMP; working tree unchanged (#6)' {
    $temp = [IO.Path]::GetTempPath()
    $partials = @(Get-ChildItem -LiteralPath $temp -Filter '.*.partial' -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
    $staging = @(Get-ChildItem -LiteralPath $temp -Filter 'ymm-nvenc-staging-*' -Force -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
    foreach ($item in $partials + $staging) { Write-CheckLog "left: $($item.Name)" }
    foreach ($item in @(Get-ChildItem -LiteralPath $media -Filter '*.partial' -File -ErrorAction SilentlyContinue)) { Write-CheckLog "NativeSmoke abort output (expected, the native layer keeps it): $($item.Name) $($item.Length) bytes" }
    if ($hasGit) {
        $changes = @(& git status --porcelain --untracked-files=no 2>$null)
        foreach ($line in $changes) { Write-CheckLog "working tree: $line" }
        if ($changes.Count -gt 0) { throw 'tracked files changed' }
    }
    if ($partials.Count + $staging.Count -gt 0) { throw "$($partials.Count) partial file(s) and $($staging.Count) staging folder(s) left in TEMP" }
})

# ---------- summary ----------

$md = New-Object System.Text.StringBuilder
function Add-Md([string] $Line = '') { [void]$md.AppendLine($Line) }
function Add-Code([string[]] $Lines) {
    Add-Md '```text'
    foreach ($line in $Lines) { Add-Md $line }
    Add-Md '```'
}

Add-Md "# rtx-check $($started.ToString('yyyy-MM-dd HH:mm'))"
Add-Md
Add-Md "Arguments: -NoNvenc:$([bool]$NoNvenc) -PreviewRuns $PreviewRuns -BenchmarkRuns $BenchmarkRuns -SkipVuiExperiment:$([bool]$SkipVuiExperiment). Total $([Math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min."
Add-Md
Add-Md '## Environment'
Add-Code $envLines
Add-Md
Add-Md '## Results'
Add-Md
Add-Md '| Id | Check | Result | Seconds | Note |'
Add-Md '| --- | --- | --- | ---: | --- |'
foreach ($result in $results) { Add-Md "| $($result.Id) | $($result.Title) | $($result.Status) | $($result.Seconds) | $(($result.Note -replace '\|', '/') -replace "`r?`n", ' ') |" }
Add-Md
Add-Md '## Expected lines found'
foreach ($result in @($results | Where-Object { $_.Found.Count -gt 0 })) {
    Add-Md
    Add-Md "- $($result.Id): $($result.Found.Count) line(s)"
    foreach ($line in $result.Found) { Add-Md "  - ``$line``" }
}
$failed = @($results | Where-Object { $_.Status -eq 'FAIL' })
if ($failed.Count -gt 0) {
    Add-Md
    Add-Md '## Failures (last 60 log lines)'
    foreach ($result in $failed) {
        Add-Md
        Add-Md "### $($result.Id): $($result.Note)"
        Add-Code @($result.Tail -split "`n")
    }
}

Add-Md
Add-Md '## Preview performance'
Add-Md
Add-Md 'Lines: `PERF|fixture|mode|frame=p50/p95/p99/max/mean` (ms) and counters. Full JSON: preview-performance-*.json.'
foreach ($run in 1..$PreviewRuns) {
    $log = Join-Path $out "preview-$run.log"
    $adapter = Get-Lines $log 'frames pixel-exact'
    $perf = Get-Lines $log '^PERF\|'
    Add-Md
    Add-Md "### Run $run"
    Add-Code (@($adapter) + @($perf))
    if (-not $NoNvenc -and $adapter.Count -gt 0 -and ($adapter -join ' ') -notmatch 'NVIDIA') { Add-Md '**The preview ran on an adapter other than the NVIDIA GPU.**' }
}

$csv = Join-Path $out 'benchmark.csv'
if (Test-Path -LiteralPath $csv) {
    Add-Md
    Add-Md '## NVENC throughput (1080p, 300 frames; median of the runs)'
    Add-Md
    Add-Md '| Case | Median s | Median fps | Bytes |'
    Add-Md '| --- | ---: | ---: | ---: |'
    foreach ($group in @(Import-Csv -LiteralPath $csv | Group-Object Case)) {
        $seconds = @($group.Group | ForEach-Object { [double]::Parse($_.Seconds, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
        $fps = @($group.Group | ForEach-Object { [double]::Parse($_.FPS, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
        $middle = [int][Math]::Floor($seconds.Count / 2)
        $medianSeconds = $seconds[$middle]
        $medianFps = $fps[$middle]
        if ($seconds.Count % 2 -eq 0) { $medianSeconds = ($seconds[$middle - 1] + $seconds[$middle]) / 2; $medianFps = ($fps[$middle - 1] + $fps[$middle]) / 2 }
        Add-Md ('| {0} | {1:F3} | {2:F1} | {3} |' -f $group.Name, $medianSeconds, $medianFps, $group.Group[-1].Bytes)
    }
}

foreach ($pair in @(@('color', 'Color matrix of the NVENC output (#5)'), @('vui-experiment', 'VUI experiment: BT.709 signaled'))) {
    $log = Join-Path $out "$($pair[0]).log"
    if (-not (Test-Path -LiteralPath $log)) { continue }
    Add-Md
    Add-Md "## $($pair[1])"
    Add-Code (Get-Lines $log '^(\S.*\(.*frames|  signaled:|  bt[67]0[19]-|  best:|    shown by|VUI |  vui: )')
}
if ($patched) {
    Add-Md
    Add-Md 'Inserted before `if (codec == kCodecHevc)` in `InitializeEncoder` for the experiment, then restored:'
    Add-Md
    Add-Md '```cpp'
    foreach ($line in $vuiSnippet) { Add-Md $line }
    Add-Md '```'
}

if ($oneDriveRows.Count -gt 0) {
    Add-Md
    Add-Md '## OneDrive attributes (first 20 files, names omitted)'
    Add-Md
    Add-Md '| Drive | Extension | KiB | Attributes | Hex |'
    Add-Md '| --- | --- | ---: | --- | --- |'
    foreach ($row in $oneDriveRows) { Add-Md "| $($row.Drive) | $($row.Extension) | $($row.SizeKiB) | $($row.Attributes) | $($row.Hex) |" }
}

$leftovers = Join-Path $out 'leftovers.log'
if (Test-Path -LiteralPath $leftovers) {
    Add-Md
    Add-Md '## Leftovers'
    Add-Code (Get-Lines $leftovers '^(left|NativeSmoke|working tree|FAILED)')
}

$summary = $md.ToString()
$summary = $summary.Replace($root, '<repo>').Replace($hostDir.TrimEnd('\'), '<ymm4>')
foreach ($personal in @($env:USERPROFILE, $env:USERNAME) | Where-Object { $_ }) { $summary = $summary.Replace($personal, '<user>') }
$summaryPath = Join-Path $out 'summary.md'
[IO.File]::WriteAllText($summaryPath, $summary, $utf8)
$zip = "$out.zip"
try { Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force } catch { Write-Host "zip failed: $($_.Exception.Message)" -ForegroundColor Yellow }
if ($savedEncoding) { try { [Console]::OutputEncoding = $savedEncoding } catch { } }

$counts = $results | Group-Object Status | ForEach-Object { "$($_.Name) $($_.Count)" }
Write-Host ''
Write-Host "Results: $($counts -join ', ')"
Write-Host "Summary: $summaryPath"
Write-Host "Logs:    $zip"
Write-Host 'The logs contain local paths; summary.md shows them as <repo>, <ymm4> and <user>.'
if ($failed.Count -gt 0) { exit 1 }
exit 0
