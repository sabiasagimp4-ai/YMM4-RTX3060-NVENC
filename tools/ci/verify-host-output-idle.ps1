param([Parameter(Mandatory = $true)][string]$HostDirectory)
$ErrorActionPreference = 'Stop'
$project = 'tests/HostCacheProbe/HostCacheProbe.csproj'
$arguments = @('run', '--project', $project, '-c', 'Release', "-p:YMM4DirPath=$HostDirectory", '--', $HostDirectory, '--host-output-idle-regressions')
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Host output / idle regressions failed' }
$cases = @(
    @{ Path = 'NVEncVideoWriterPlugin/TimelineFrameCache.cs'; From = 'if (__state.RunsHost) RestoreHostOutputBeforeUpdate(__instance);'; To = '// mutation: original output not restored'; Flag = '--output-only'; Marker = 'OUTPUT_LIFETIME_REGRESSION' },
    @{ Path = 'NVEncVideoWriterPlugin/IdleFramePreRenderer.cs'; From = '!FrameRenderModelComparison.Matches(liveCapture!.Model, cloneCapture!.Model)'; To = 'liveCapture!.Model != cloneCapture!.Model'; Flag = '--idle-only'; Marker = 'IDLE_IDENTITY_REGRESSION' }
)
foreach ($case in $cases) {
    $original = [System.IO.File]::ReadAllText((Join-Path $pwd $case.Path))
    if (-not $original.Contains($case.From)) { throw "Mutation target missing: $($case.Path)" }
    try {
        [System.IO.File]::WriteAllText((Join-Path $pwd $case.Path), $original.Replace($case.From, $case.To))
        $output = & dotnet @arguments $case.Flag 2>&1
        $code = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($code -eq 0 -or ($output -join "`n") -notmatch $case.Marker) {
            throw "Removing the fix did not fail its regression: $($case.Marker) (exit $code)"
        }
        Write-Host "EXPECTED_MUTATION_FAILURE: $($case.Marker)"
    } finally {
        [System.IO.File]::WriteAllText((Join-Path $pwd $case.Path), $original)
    }
}
