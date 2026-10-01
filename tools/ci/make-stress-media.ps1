param([Parameter(Mandatory)] [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    choco install ffmpeg -y --no-progress | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg installation failed' }
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + $env:Path
}
for ($track = 0; $track -lt 12; $track++) {
    $path = Join-Path $OutputDirectory ('video-{0:00}.mp4' -f $track)
    ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=1920x1080:rate=30' -t 3 -vf "hue=h=$($track * 30)" -an -c:v libx264 -preset ultrafast -crf 20 -pix_fmt yuv420p -threads 2 -y $path
    if ($LASTEXITCODE -ne 0) { throw "Video generation failed: $track" }
}
ffmpeg -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=30' -af 'volume=0.02' -ac 2 -c:a pcm_s16le -y (Join-Path $OutputDirectory 'clock.wav')
if ($LASTEXITCODE -ne 0) { throw 'Audio clock generation failed' }
Get-ChildItem $OutputDirectory | Select-Object Name, Length | Format-Table
