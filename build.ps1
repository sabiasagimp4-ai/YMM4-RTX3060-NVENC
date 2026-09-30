[CmdletBinding()]
param(
    [string]$Ymm4DirPath = 'D:\YukkuriMovieMaker_v4_Lite',
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$hostDir = (Resolve-Path -LiteralPath $Ymm4DirPath).Path.TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath (Join-Path $hostDir 'YukkuriMovieMaker.Plugin.dll'))) {
    throw "YMM4 DLL not found: $hostDir"
}
$vswhere = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe',
    'C:\Program Files\Microsoft Visual Studio\Installer\vswhere.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $vswhere) { throw 'Visual Studio Build Tools not found.' }
$vsDir = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msbuild = Join-Path $vsDir 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild.exe not found.' }

& $msbuild (Join-Path $root 'NvencNative\NvencNative.vcxproj') /t:Build /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
& dotnet build (Join-Path $root 'NVEncVideoWriterPlugin\NVEncVideoWriterPlugin.csproj') -c Release "-p:YMM4DirPath=$hostDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }

$managed = Join-Path $root 'NVEncVideoWriterPlugin\bin\Release\net10.0-windows10.0.19041.0\YMM4Rtx3060Nvenc.dll'
$native = Join-Path $root 'NvencNative\bin\Release\NvencNative.dll'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$package = Join-Path $dist 'YMM4-RTX3060-NVENC.ymme'
$partial = "$package.partial"
if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [IO.Compression.ZipFile]::Open($partial, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = [ordered]@{
            'YMM4Rtx3060Nvenc.dll' = $managed
            'NvencNative.dll' = $native
            'README.md' = (Join-Path $root 'README.md')
            'LICENSE' = (Join-Path $root 'LICENSE')
            'THIRD_PARTY_NOTICES.txt' = (Join-Path $root 'THIRD_PARTY_NOTICES.txt')
        }
        foreach ($entry in $entries.GetEnumerator()) {
            if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Missing package input: $($entry.Value)" }
            $zipEntry = $archive.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($entry.Value)
            try {
                $output = $zipEntry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            } finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    Move-Item -LiteralPath $partial -Destination $package
} finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
}
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$package.sha256", "$hash  YMM4-RTX3060-NVENC.ymme`n", [Text.Encoding]::ASCII)
Write-Output "Package: $package"
Write-Output "SHA256: $hash"

if ($Smoke) {
    & dotnet run --project (Join-Path $root 'tests\ManagedSmoke\ManagedSmoke.csproj') -c Release "-p:YMM4DirPath=$hostDir" --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Managed smoke failed.' }
    & $msbuild (Join-Path $root 'tests\NativeSmoke.vcxproj') /t:Build /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test build failed.' }
    $testDir = Join-Path $root 'tests\bin\Release'
    Copy-Item -LiteralPath $native -Destination (Join-Path $testDir 'NvencNative.dll') -Force
    $ffprobe = (Get-Command ffprobe -ErrorAction Stop).Source
    $ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
    foreach ($case in @(@('h264', 0), @('hevc', 1))) {
        $output = Join-Path $dist ("smoke-{0}.mp4" -f $case[0])
        & (Join-Path $testDir 'NativeSmoke.exe') $output $case[1]
        if ($LASTEXITCODE -ne 0) { throw "NVENC smoke failed: $($case[0])" }
        $probe = (& $ffprobe -v error -show_entries stream=codec_name,nb_frames -of json $output | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0 -or @($probe.streams | Where-Object codec_name -EQ $case[0]).Count -ne 1 -or
            @($probe.streams | Where-Object codec_name -EQ 'aac').Count -ne 1) {
            throw "Unexpected MP4 streams: $($case[0])"
        }
        & $ffmpeg -v error -i $output -f null NUL
        if ($LASTEXITCODE -ne 0) { throw "MP4 decode failed: $($case[0])" }
        Write-Output "Smoke OK: $($case[0]) + aac"
    }
}
