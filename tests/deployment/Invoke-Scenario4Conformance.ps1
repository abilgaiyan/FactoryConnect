[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter()][string]$SqlServerConnectionString = $env:FACTORYCONNECT_TEST_SQL_CONNECTION_STRING,
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
if ([string]::IsNullOrWhiteSpace($SqlServerConnectionString)) {
    throw 'Scenario 4 requires -SqlServerConnectionString or FACTORYCONNECT_TEST_SQL_CONNECTION_STRING. Use only a disposable test database; the production deployer executes real migrations.'
}

function Write-ScenarioConfiguration {
    param([string]$Root,[string]$ConnectionString)
    $configRoot = Join-Path $Root 'config'
    New-Item -ItemType Directory -Force -Path $configRoot | Out-Null

    $machineId = '11111111-1111-1111-1111-111111111111'
    $siteId = '22222222-2222-2222-2222-222222222222'
    $companyId = '33333333-3333-3333-3333-333333333333'
    $lineId = '44444444-4444-4444-4444-444444444444'
    $processorId = '55555555-5555-5555-5555-555555555555'
    $deviceKey = 'scenario4'
    $apiUrl = 'http://127.0.0.1:51981'
    $dashboardUrl = 'http://127.0.0.1:51982'

    $edge = [ordered]@{
        Persistence = [ordered]@{ Provider = 'SqlServer' }
        PersistenceProviders = [ordered]@{ SqlServer = [ordered]@{ ConnectionString = $ConnectionString } }
        MTConnect = [ordered]@{ Machines = @([ordered]@{ BaseUri='http://127.0.0.1:51983'; MachineId=$machineId; DeviceKey=$deviceKey; FromSequence='1'; PollingInterval='00:00:01' }) }
        CurrentState = [ordered]@{ Freshness = [ordered]@{ MaximumCurrentAge='00:00:10' } }
        ProductionProcessing = [ordered]@{
            Machines = @([ordered]@{ MachineId=$machineId; ActivityStreamKey="mtconnect:$deviceKey"; QuantityStreamKey="mtconnect:$deviceKey:part-count"; CompanyId=$companyId; SiteId=$siteId; ProductionLineId=$lineId })
            ShiftSchedules = @()
        }
    }
    $api = [ordered]@{
        Urls = $apiUrl
        Persistence = [ordered]@{ Provider = 'SqlServer' }
        PersistenceProviders = [ordered]@{ SqlServer = [ordered]@{ ConnectionString = $ConnectionString } }
        MTConnect = [ordered]@{ Machines = @([ordered]@{ BaseUri='http://127.0.0.1:51983'; MachineId=$machineId; DeviceKey=$deviceKey }) }
        CurrentState = [ordered]@{ Freshness = [ordered]@{ MaximumCurrentAge='00:00:30' } }
    }
    $dashboard = [ordered]@{
        Urls = $dashboardUrl
        Dashboard = [ordered]@{
            ReportingApiBaseAddress = $apiUrl
            RequestTimeout = '00:00:30'
            Sources = @([ordered]@{ MachineId=$machineId; ProcessorId=$processorId; SiteId=$siteId; ProductionLineId=$lineId; DisplayName='Scenario 4'; GroupName='Scenario 4'; DisplayOrder=0 })
        }
    }
    foreach ($entry in @(
        @{Name='edge';Value=$edge},
        @{Name='api';Value=$api},
        @{Name='dashboard';Value=$dashboard}
    )) {
        $path = Join-Path $configRoot "$($entry.Name).production.json"
        ($entry.Value | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $path -Encoding UTF8
    }
}

# Scenario 4 exercises the production deployment algorithm and evidence writer.
# The harness creates a disposable instrumented copy and changes only two
# deterministic control points:
#   1. fail immediately before API startup, after the real Edge startup line;
#   2. skip termination of that real Edge process during failure cleanup.
# The production catch/observation path remains responsible for runtime.json,
# deployment-failure.json, processStates, cleanupErrors, and process identity.
$source = Get-Content -Raw -LiteralPath $productionScript

$apiPattern = '(?m)^(?<indent>\s*)\$apiExe=Join-Path \$targetReleasePath ''apps/api/FactoryConnect\.Api\.exe'';'
$apiMatches = [regex]::Matches($source, $apiPattern)
if ($apiMatches.Count -ne 1) {
    throw "Scenario 4 harness expected exactly one pre-API startup seam; observed $($apiMatches.Count). Production deployer shape changed."
}
$source = [regex]::Replace(
    $source,
    $apiPattern,
    { param($match) $match.Groups['indent'].Value + "throw 'SCENARIO4_INJECTED_FAILURE_AFTER_EDGE_STARTED'`r`n" + $match.Value },
    1)

$cleanupPattern = '(?m)^(?<indent>\s*)try \{ Stop-RecordedOwnedProcess \$record; \$newRecords\[\$name\]=\$null \}\r?\n\s*catch \{ \$cleanup \+= "\$name cleanup failed: \$\(\$_\.Exception\.Message\)" \}'
$cleanupMatches = [regex]::Matches($source, $cleanupPattern)
if ($cleanupMatches.Count -ne 1) {
    throw "Scenario 4 harness expected exactly one failure-cleanup stop seam; observed $($cleanupMatches.Count). Production deployer shape changed."
}
$source = [regex]::Replace(
    $source,
    $cleanupPattern,
    {
        param($match)
        $indent = $match.Groups['indent'].Value
        $statement = $match.Value.TrimStart()
        $indent + 'if ($name -eq ''edge'') { $cleanup += "edge PID $($record.pid) survived cleanup." } else { ' + $statement + ' }'
    },
    1)

$instrumentedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("FactoryConnect-Scenario4-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $instrumentedRoot | Out-Null
$instrumentedScript = Join-Path $instrumentedRoot 'Deploy-FactoryConnect.Scenario4.ps1'
[System.IO.File]::WriteAllText($instrumentedScript, $source, [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path (Split-Path $productionScript -Parent) 'FactoryConnect.ProcessTermination.ps1') -Destination $instrumentedRoot

try {
    # Scenario 4 owns its disposable commissioning fixture. This does not bypass
    # production validation: the real deployer still parses and validates all
    # three files and executes the real migration against the caller-supplied
    # disposable SQL database.
    Write-ScenarioConfiguration $InstallRoot $SqlServerConnectionString

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
