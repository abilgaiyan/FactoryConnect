[CmdletBinding()]
param(
    [UInt64]$ThroughPosition = 0,
    [ValidateRange(1, 10000)][int]$MaximumRevisions = 50,
    [ValidateRange(1, 120)][int]$TimeBudgetMinutes = 30
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires Windows LocalDB.' }
if (-not $env:FACTORYCONNECT_GG003_CATCHUP_CONNECTION_STRING) {
    throw 'Set FACTORYCONNECT_GG003_CATCHUP_CONNECTION_STRING to the already rehearsed disposable FactoryConnect_GG003_STAGEB_REPLAY_<suffix> LocalDB clone.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$results = Join-Path $repo ('artifacts\TestResults\Gg003Catchup\' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($results)
$report = Join-Path $results 'catchup.json'
$variables = @{
    FACTORYCONNECT_GG003_CATCHUP_TARGET = "$ThroughPosition"
    FACTORYCONNECT_GG003_CATCHUP_MAX_CYCLES = "$MaximumRevisions"
    FACTORYCONNECT_GG003_CATCHUP_MINUTES = "$TimeBudgetMinutes"
    FACTORYCONNECT_GG003_CATCHUP_REPORT = $report
}
$saved = @{}
try {
    foreach ($name in $variables.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    & dotnet test (Join-Path $repo 'tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj') `
        --configuration Release -m:1 --filter 'FullyQualifiedName~Gg003BoundedContainmentCatchupAcceptanceTests' `
        --logger 'trx;LogFileName=gg003-catchup.trx' --results-directory $results
    $exitCode = $LASTEXITCODE
    if (Test-Path $report) {
        $summary = Get-Content $report -Raw | ConvertFrom-Json
        Write-Output ($summary | ConvertTo-Json -Depth 8)
    }
    if ($exitCode -ne 0) { throw "Catch-up failed. Preserve clone and evidence: $results" }
    if (-not (Test-Path $report)) { throw "No catch-up report: test may have been skipped. Inspect $results" }
    if ($summary.FullCapturedCatchupComplete) { Write-Output "Captured historical head reached; evidence: $results" }
    else { Write-Output "PARTIAL bounded rehearsal only. Resume after reviewing evidence: $results" }
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
