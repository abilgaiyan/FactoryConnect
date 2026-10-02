[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter()][ValidateRange(1, 300)][int]$EdgeStabilizationSeconds = 1,
    [Parameter()][ValidateRange(1, 300)][int]$HealthTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$productionScript = Join-Path $repoRoot 'scripts/deployment/Deploy-FactoryConnect.ps1'
if (-not (Test-Path -LiteralPath $productionScript -PathType Leaf)) {
    throw "Production deployment script was not found at '$productionScript'."
}

# Scenario 4 exercises the production deployment algorithm and evidence writer.
# The harness creates a disposable instrumented copy and changes only two
# deterministic control points:
#   1. fail immediately before API startup, after the real Edge startup line;
#   2. skip termination of that real Edge process during failure cleanup.
# The production catch/observation path remains responsible for runtime.json,
# deployment-failure.json, processStates, cleanupErrors, and process identity.
$source = Get-Content -Raw -LiteralPath $productionScript

# Anchor to the API-start statement rather than reproducing the entire preceding
# Edge-start line. This remains fail-closed (exactly one match) while avoiding
# sensitivity to formatting elsewhere on that long production statement.
$failureNeedle = @'
    $apiExe=Join-Path $targetReleasePath 'apps/api/FactoryConnect.Api.exe';
'@.TrimEnd("`r", "`n")
$failureReplacement = "    throw 'SCENARIO4_INJECTED_FAILURE_AFTER_EDGE_STARTED'`r`n" + $failureNeedle
if (-not $source.Contains($failureNeedle)) {
    $failureNeedle = $failureNeedle.Replace("`r`n", "`n")
    $failureReplacement = "    throw 'SCENARIO4_INJECTED_FAILURE_AFTER_EDGE_STARTED'`n" + $failureNeedle
}
if (($source.Split($failureNeedle).Count - 1) -ne 1) {
    throw 'Scenario 4 harness could not identify exactly one pre-API startup seam. Production deployer shape changed.'
}
$source = $source.Replace($failureNeedle, $failureReplacement)

$cleanupNeedle = @'
        try { Stop-Process -Id ([int]$record.pid) -ErrorAction Stop; Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue } catch { $cleanup += "$name PID $($record.pid) stop failed: $($_.Exception.Message)" }
'@.TrimEnd("`r", "`n")
$cleanupReplacement = @'
        if ($name -ne 'edge') { try { Stop-Process -Id ([int]$record.pid) -ErrorAction Stop; Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue } catch { $cleanup += "$name PID $($record.pid) stop failed: $($_.Exception.Message)" } }
'@.TrimEnd("`r", "`n")
if (($source.Split($cleanupNeedle).Count - 1) -ne 1) {
    throw 'Scenario 4 harness could not identify exactly one failure-cleanup stop seam. Production deployer shape changed.'
}
$source = $source.Replace($cleanupNeedle, $cleanupReplacement)

$instrumentedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("FactoryConnect-Scenario4-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $instrumentedRoot | Out-Null
$instrumentedScript = Join-Path $instrumentedRoot 'Deploy-FactoryConnect.Scenario4.ps1'
[System.IO.File]::WriteAllText($instrumentedScript, $source, [System.Text.UTF8Encoding]::new($false))

try {
    $caught = $null
    try {
        & $instrumentedScript -PackagePath $PackagePath -InstallRoot $InstallRoot -EdgeStabilizationSeconds $EdgeStabilizationSeconds -HealthTimeoutSeconds $HealthTimeoutSeconds
        throw 'Scenario 4 expected deployment failure, but deployment returned successfully.'
    }
    catch {
        $caught = $_
    }

    if ($caught.Exception.Message -notlike '*SCENARIO4_INJECTED_FAILURE_AFTER_EDGE_STARTED*') {
        throw "Scenario 4 failed at an unexpected point: $($caught.Exception.Message)"
    }

    $runtimePath = Join-Path $InstallRoot 'deployment/runtime.json'
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw 'Scenario 4 did not produce deployment/runtime.json.'
    }
    $runtime = Get-Content -Raw -LiteralPath $runtimePath | ConvertFrom-Json
    if ([string]$runtime.deploymentStatus -cne 'Failed') { throw "Expected deploymentStatus Failed; observed '$($runtime.deploymentStatus)'." }
    if (-not [bool]$runtime.runtimeRunning) { throw 'Expected runtimeRunning true because Edge survives cleanup.' }
    if ([string]$runtime.failurePhase -cne 'Startup') { throw "Expected failurePhase Startup; observed '$($runtime.failurePhase)'." }
    if ([string]$runtime.migrationOutcome -cne 'Succeeded') { throw "Expected migrationOutcome Succeeded; observed '$($runtime.migrationOutcome)'." }
    if ([string]$runtime.processStates.edge -cne 'Owned') { throw "Expected Edge process state Owned; observed '$($runtime.processStates.edge)'." }
    if ([string]$runtime.processStates.api -cne 'Absent') { throw "Expected API process state Absent; observed '$($runtime.processStates.api)'." }
    if ([string]$runtime.processStates.dashboard -cne 'Absent') { throw "Expected Dashboard process state Absent; observed '$($runtime.processStates.dashboard)'." }
    if ($null -eq $runtime.edge) { throw 'Expected exact surviving Edge process record in runtime.json.' }

    $edgeProcess = Get-Process -Id ([int]$runtime.edge.pid) -ErrorAction SilentlyContinue
    if ($null -eq $edgeProcess) { throw "Recorded surviving Edge PID $($runtime.edge.pid) is not alive." }
    $samePath = [System.IO.Path]::GetFullPath($edgeProcess.Path) -eq [System.IO.Path]::GetFullPath([string]$runtime.edge.executablePath)
    $sameStart = $edgeProcess.StartTime.ToUniversalTime().ToString('o') -eq [string]$runtime.edge.startTimeUtc
    if (-not $samePath -or -not $sameStart) { throw 'Surviving Edge PID/path/start-time identity does not match runtime.json.' }

    $attemptId = [string]$runtime.deploymentAttemptId
    $failurePath = Join-Path $InstallRoot "deployment/logs/$attemptId/deployment-failure.json"
    if (-not (Test-Path -LiteralPath $failurePath -PathType Leaf)) { throw 'Scenario 4 did not produce deployment-failure.json for the failed attempt.' }
    $failure = Get-Content -Raw -LiteralPath $failurePath | ConvertFrom-Json
    if ([string]$failure.message -notlike '*SCENARIO4_INJECTED_FAILURE_AFTER_EDGE_STARTED*') { throw 'Failure evidence does not retain the injected primary failure.' }
    if ([string]$failure.processStates.edge -cne 'Owned') { throw 'Failure evidence does not observe the surviving Edge process as Owned.' }
    if (-not (@($failure.cleanupErrors) -contains "edge PID $($runtime.edge.pid) survived cleanup.")) { throw 'Failure evidence does not report the real surviving Edge process.' }

    [pscustomobject]@{
        Outcome = 'PASS'
        AttemptId = $attemptId
        SelectedRelease = [string]$runtime.selectedRelease
        EdgePid = [int]$runtime.edge.pid
        EdgeExecutablePath = [string]$runtime.edge.executablePath
        EdgeStartTimeUtc = [string]$runtime.edge.startTimeUtc
        RuntimePath = $runtimePath
        FailureEvidencePath = $failurePath
    }
}
finally {
    Remove-Item -LiteralPath $instrumentedRoot -Recurse -Force -ErrorAction SilentlyContinue
}
