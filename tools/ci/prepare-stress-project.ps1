# Relink the portable fixture after extracting its project and assets together.
param([string] $Project = (Join-Path $PSScriptRoot 'stress-30s-portable.ymmp'))
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path $Project).Path
$root = Split-Path $Project
$text = [IO.File]::ReadAllText($Project)
$count = 0
$relinked = [regex]::Replace($text, '"FilePath"\s*:\s*"assets[/\\]([^"\\/]+)"', {
    param($match)
    $path = Join-Path (Join-Path $root 'assets') $match.Groups[1].Value
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing stress media: $path" }
    $script:count++
    '"FilePath":' + (ConvertTo-Json -InputObject $path -Compress)
})
if ($count -ne 121) { throw "Expected 121 video/audio paths; found $count" }
$output = Join-Path $root 'stress-30s-local.ymmp'
[IO.File]::WriteAllText($output, $relinked, (New-Object Text.UTF8Encoding($false)))
Write-Output "Open in YMM4: $output"
