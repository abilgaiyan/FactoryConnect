[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This acceptance wrapper requires Windows LocalDB.' }
# Supply credentials through the process environment, never command-line arguments or logs.
if (-not $env:FACTORYCONNECT_GG003_DISPOSABLE_CONNECTION_STRING) {
    throw 'Set FACTORYCONNECT_GG003_DISPOSABLE_CONNECTION_STRING to a fresh developer LocalDB clone named FactoryConnect_GG003_STAGEB_REPLAY_<suffix>. The original RCA database and factory servers are rejected.'
}
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj'
$results = Join-Path $repo 'artifacts\TestResults\Gg003ContainmentReplay'
# The test validates server/catalog before migration or writes. It never creates, restores or drops databases.
& dotnet test $project --configuration Release -m:1 `
    --filter 'FullyQualifiedName~Gg003RestoredContainmentRuntimeAcceptanceTests' `
    --logger 'console;verbosity=normal' --logger 'trx;LogFileName=gg003-containment-replay.trx' `
    --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Disposable GG-003 containment acceptance failed. Preserve the clone and TRX for review; no factory change is authorized.' }
Write-Output "Acceptance command completed. Review the executed (not skipped) test and retained evidence at $results. This does not authorize factory activation."
