[CmdletBinding()]
param([ValidateRange(1, 10)][int]$Runs = 3)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $PSScriptRoot 'bin\Release\NativeSmoke.exe'
$native = Join-Path $PSScriptRoot 'bin\Release\NvencNative.dll'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $native)) {
    throw 'Run .\build.ps1 -Smoke first.'
}
$outputDir = Join-Path $root 'dist\benchmark'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$cases = @(
    @{ Name = 'h264-balanced'; Codec = 0; Quality = 1; Fast = 0; Async = 0 },
    @{ Name = 'h264-speed'; Codec = 0; Quality = 0; Fast = 0; Async = 0 },
    @{ Name = 'h264-fast-nv12'; Codec = 0; Quality = 1; Fast = 1; Async = 0 },
    @{ Name = 'hevc-balanced-async'; Codec = 1; Quality = 1; Fast = 0; Async = 1 },
    @{ Name = 'hevc-speed-async'; Codec = 1; Quality = 0; Fast = 0; Async = 1 },
    @{ Name = 'hevc-balanced-sync'; Codec = 1; Quality = 1; Fast = 0; Async = 0 },
    @{ Name = 'hevc-fast-nv12'; Codec = 1; Quality = 1; Fast = 1; Async = 1 }
)
$warmup = Join-Path $outputDir 'warmup.mp4'
& $exe $warmup 0 30 1920 1080 1 0 0 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GPU warmup failed.' }
$rows = foreach ($run in 1..$Runs) {
    foreach ($case in $cases) {
        $output = Join-Path $outputDir ($case.Name + '.mp4')
        $result = & $exe $output $case.Codec 300 1920 1080 $case.Quality $case.Fast $case.Async
        if ($LASTEXITCODE -ne 0 -or $result -notmatch 'elapsed_seconds=([0-9.]+) frames_per_second=([0-9.]+)') {
            throw "Benchmark failed: $($case.Name), run $run, $result"
        }
        [pscustomobject]@{
            Case = $case.Name
            Run = $run
            Seconds = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
            FPS = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
            Bytes = (Get-Item -LiteralPath $output).Length
        }
    }
}
foreach ($case in $cases) {
    $output = Join-Path $outputDir ($case.Name + '.mp4')
    $frames = & ffprobe -v error -select_streams v:0 -show_entries stream=nb_frames -of csv=p=0 $output
    if ($LASTEXITCODE -ne 0 -or "$frames".Trim() -ne '300') {
        throw "Unexpected frame count: $($case.Name), $frames"
    }
    & ffmpeg -v error -i $output -f null NUL
    if ($LASTEXITCODE -ne 0) { throw "Decode failed: $($case.Name)" }
}
$csv = Join-Path $outputDir 'results.csv'
$rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding utf8
$rows | Group-Object Case | ForEach-Object {
    $times = @($_.Group.Seconds | Sort-Object)
    $middle = [int][Math]::Floor($times.Count / 2)
    [pscustomobject]@{
        Case = $_.Name
        MedianSeconds = if ($times.Count % 2) { $times[$middle] } else { ($times[$middle - 1] + $times[$middle]) / 2 }
        Bytes = $_.Group[-1].Bytes
    }
} | Sort-Object Case | Format-Table -AutoSize
Write-Output "CSV: $csv"
