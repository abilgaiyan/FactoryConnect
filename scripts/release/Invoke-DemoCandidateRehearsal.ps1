[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidatePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^rehearsal-\d{8}-\d{2}$')][string]$AttemptId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedDeploymentContractCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$RehearsalToolSourceCommit,
    [Parameter(Mandatory = $true)][string]$RehearsalConfigurationPath,
    [Parameter(Mandatory = $true)][string]$SqlConnectionString,
    [Parameter(Mandatory = $true)][string]$FixtureExecutablePath,
    [Parameter()][switch]$ProvisionDatabase,
    [Parameter()][int]$StartupTimeoutSeconds = 45,
    [Parameter()][int]$ObservationTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Rehearsal.Common.ps1')

function Wait-RehearsalHttp200 {
    param(
        [Parameter(Mandatory = $true)][Uri]$Uri,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -Method Get -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) { return $response }
        }
        catch {
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for HTTP 200 from '$Uri'."
}

function Add-RehearsalMachineEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Environment,
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$FixturePublicBaseAddress
    )

    for ($index = 0; $index -lt $Configuration.machines.Count; $index++) {
        $machine = $Configuration.machines[$index]
        $prefix = "MTConnect__Machines__${index}"
        $Environment["${prefix}__BaseUri"] = $FixturePublicBaseAddress.TrimEnd('/') + [string]$machine.basePath
        $Environment["${prefix}__MachineId"] = [string]$machine.machineId
        $Environment["${prefix}__DeviceKey"] = [string]$machine.deviceKey
        $Environment["${prefix}__FromSequence"] = '1'
        $Environment["${prefix}__PollingInterval"] = '00:00:00.250'
    }
}

function Add-RehearsalProductionEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Environment,
        [Parameter(Mandatory = $true)]$Configuration
    )

    for ($index = 0; $index -lt $Configuration.machines.Count; $index++) {
        $machine = $Configuration.machines[$index]
        $prefix = "ProductionProcessing__Machines__${index}"
        $Environment["${prefix}__MachineId"] = [string]$machine.machineId
        $Environment["${prefix}__ActivityStreamKey"] = [string]$machine.streamIdentity
        $Environment["${prefix}__QuantityStreamKey"] = 'part_count'
        $Environment["${prefix}__CompanyId"] = 'GAJRA'
        $Environment["${prefix}__SiteId"] = [string]$Configuration.siteId
        $Environment["${prefix}__ProductionLineId"] = [string]$machine.productionLineId
        $Environment["${prefix}__PlannedProduction__AssignmentId"] = "POT-$($index + 1)"
        $Environment["${prefix}__PlannedProduction__TimeZoneId"] = [string]$Configuration.timeZoneId
        $Environment["${prefix}__PlannedProduction__StartsAtLocal"] = '00:00:00'
        $Environment["${prefix}__PlannedProduction__EndsAtLocal"] = '23:59:59'
        $Environment["${prefix}__PlannedProduction__EffectiveFrom"] = '2026-01-01'
        $Environment["${prefix}__Contexts__0__AssignmentId"] = "CTX-$($index + 1)"
        $Environment["${prefix}__Contexts__0__EffectiveFrom"] = '2026-01-01T00:00:00+00:00'
    }

    for ($index = 0; $index -lt $Configuration.shifts.Count; $index++) {
        $shift = $Configuration.shifts[$index]
        $prefix = "ProductionProcessing__ShiftSchedules__${index}"
        $Environment["${prefix}__AssignmentId"] = "SHIFT-SCHEDULE-$($index + 1)"
        $Environment["${prefix}__CompanyId"] = 'GAJRA'
        $Environment["${prefix}__SiteId"] = [string]$Configuration.siteId
        $Environment["${prefix}__ProductionLineId"] = 'LINE-1'
        $Environment["${prefix}__ShiftId"] = [string]$shift.shiftId
        $Environment["${prefix}__Name"] = [string]$shift.shiftId
        $Environment["${prefix}__TimeZoneId"] = [string]$Configuration.timeZoneId
        $Environment["${prefix}__StartsAtLocal"] = [string]$shift.startsAtLocal
        $Environment["${prefix}__EndsAtLocal"] = [string]$shift.endsAtLocal
        $Environment["${prefix}__EffectiveFrom"] = '2026-01-01'
        @('Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday') | ForEach-Object -Begin { $day = 0 } -Process {
            $Environment["${prefix}__ActiveDays__${day}"] = $_
            $day++
        }
    }
}

function Invoke-RehearsalDatabaseProvisioning {
    param(
        [Parameter(Mandatory = $true)]$Admission,
        [Parameter(Mandatory = $true)][string]$DatabaseName
    )

    if (-not $ProvisionDatabase) { return }

    $sqlcmd = Get-Command sqlcmd -ErrorAction Stop
    $arguments = @('-S', [string]$Admission.Server, '-d', 'master', '-b', '-Q', "IF DB_ID(N'$DatabaseName') IS NULL CREATE DATABASE [$DatabaseName]")
    if ([bool]$Admission.IntegratedSecurity) {
        $arguments += '-E'
    }
    else {
        $arguments += @('-U', [string]$Admission.UserId)
        if (-not [string]::IsNullOrEmpty([string]$Admission.Password)) {
            $env:SQLCMDPASSWORD = [string]$Admission.Password
        }
    }

    try {
        & $sqlcmd.Source @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd database provisioning failed with exit code $LASTEXITCODE." }
    }
    finally {
        Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$candidateRoot = (Resolve-Path -LiteralPath $CandidatePath).Path
$candidateId = Split-Path -Leaf $candidateRoot
$configuration = Get-Content -Raw -LiteralPath $RehearsalConfigurationPath | ConvertFrom-Json

if ($configuration.machines.Count -ne 7) { throw 'Rehearsal configuration must contain exactly seven machines.' }
if ((@($configuration.machines.machineId | Sort-Object -Unique)).Count -ne 7) { throw 'Rehearsal MachineIds must be unique.' }
if ((@($configuration.machines.deviceKey | Sort-Object -Unique)).Count -ne 7) { throw 'Rehearsal DeviceKeys must be unique.' }

$attemptMatch = [regex]::Match($AttemptId, '^rehearsal-(?<date>\d{8})-(?<sequence>\d{2})$')
if (-not $attemptMatch.Success -or $attemptMatch.Groups['date'].Value -ne [DateTime]::UtcNow.ToString('yyyyMMdd')) {
    throw 'Rehearsal attempt date must equal the current UTC date.'
}

$apiHost = [string]$configuration.api.listenAddress
$dashboardHost = [string]$configuration.dashboard.listenAddress
$fixtureHost = [string]$configuration.fixture.listenAddress
foreach ($hostName in @($apiHost, $dashboardHost, $fixtureHost)) {
    if ([string]::IsNullOrWhiteSpace($hostName) -or $hostName -in @('localhost','127.0.0.1','::1')) {
        throw 'Production-equivalent rehearsal addresses must be explicit non-loopback hostnames or addresses.'
    }
}

$projection = Get-RehearsalConfigurationProjection -Configuration $configuration -CandidateId $candidateId
$configurationSha256 = Get-RehearsalProjectionSha256 -Projection $projection

$workspaceRoot = Join-Path $repoRoot "artifacts/release/rehearsal/$candidateId/$AttemptId"
$evidenceRoot = Join-Path $repoRoot "artifacts/release/evidence/$candidateId/attempts/$AttemptId"
if (Test-Path -LiteralPath $workspaceRoot) { throw "Rehearsal workspace already exists: '$workspaceRoot'." }
if (Test-Path -LiteralPath $evidenceRoot) { throw "Rehearsal evidence attempt already exists: '$evidenceRoot'." }

[System.IO.Directory]::CreateDirectory((Join-Path $workspaceRoot 'logs')) | Out-Null
[System.IO.Directory]::CreateDirectory((Join-Path $workspaceRoot 'runtime')) | Out-Null
[System.IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null

$projectionText = (($projection | ConvertTo-Json -Depth 20) -replace "`r`n", "`n") + "`n"
Write-DemoCandidateUtf8NoBom -Path (Join-Path $workspaceRoot 'rehearsal-configuration.public.json') -Text $projectionText

$startedAtUtc = [DateTimeOffset]::UtcNow
$outcome = 'Failed'
$failureClassification = $null
$failureMessage = $null
$currentPhase = 'CandidatePreVerification'
$ownedProcesses = [System.Collections.Generic.List[object]]::new()
$processEvidence = [System.Collections.Generic.List[object]]::new()
$checks = [System.Collections.Generic.List[object]]::new()
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$beforeVerification = $null
$afterVerification = $null
$restartPerformed = $false
$restartPassed = $false
$migrationExitCode = $null
$repositoryCurrent = $false
$databaseAdmission = $null
$publishedEvidence = $null

$fixtureBaseAddress = "http://${fixtureHost}:$([int]$configuration.fixture.port)"
$apiBaseAddress = "http://${apiHost}:$([int]$configuration.api.port)"
$dashboardBaseAddress = "http://${dashboardHost}:$([int]$configuration.dashboard.port)"

try {
    $currentPhase = 'CandidatePreVerification'
    $beforeVerification = & (Join-Path $PSScriptRoot 'Test-DemoCandidate.ps1') `
        -CandidatePath $candidateRoot `
        -ExpectedCandidateId $candidateId `
        -ExpectedSourceCommit $ExpectedSourceCommit `
        -ExpectedDeploymentContractCommit $ExpectedDeploymentContractCommit
    $checks.Add([ordered]@{ id = 'candidate-pre-verification'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = $beforeVerification.ManifestSha256 })

    $currentPhase = 'ConfigurationAdmission'
    $databaseAdmission = Resolve-RehearsalDatabaseAdmission `
        -ConnectionString $SqlConnectionString `
        -DatabaseName ([string]$configuration.database.databaseName)

    $currentPhase = 'DatabaseProvisioning'
    Invoke-RehearsalDatabaseProvisioning `
        -Admission $databaseAdmission `
        -DatabaseName ([string]$configuration.database.databaseName)

    $logs = Join-Path $workspaceRoot 'logs'
    $currentPhase = 'FixtureStartup'
    $fixture = Start-RehearsalProcess `
        -Role 'Fixture' `
        -FilePath (Resolve-Path -LiteralPath $FixtureExecutablePath).Path `
        -WorkingDirectory (Split-Path -Parent (Resolve-Path -LiteralPath $FixtureExecutablePath).Path) `
        -StdOutPath (Join-Path $logs 'fixture.stdout.log') `
        -StdErrPath (Join-Path $logs 'fixture.stderr.log') `
        -Environment @{ ASPNETCORE_ENVIRONMENT = 'Production'; ASPNETCORE_URLS = "http://0.0.0.0:$([int]$configuration.fixture.port)" }
    $ownedProcesses.Add($fixture)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$fixtureBaseAddress/health") -TimeoutSeconds $StartupTimeoutSeconds)
    $checks.Add([ordered]@{ id = 'fixture-health'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'seven-machine fixture ready' })

    $currentPhase = 'MigrationExecution'
    $migrationEnvironment = @{
        DOTNET_ENVIRONMENT = 'Production'
        PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
    }
    $migration = Start-RehearsalProcess `
        -Role 'Migrations' `
        -FilePath (Join-Path $candidateRoot 'migrations/FactoryConnect.Migrations.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'migrations') `
        -StdOutPath (Join-Path $logs 'migrations.stdout.log') `
        -StdErrPath (Join-Path $logs 'migrations.stderr.log') `
        -Environment $migrationEnvironment
    $migration.Process.WaitForExit()
    $migrationExitCode = $migration.Process.ExitCode
    $repositoryCurrent = $migrationExitCode -eq 0
    if (-not $repositoryCurrent) { throw "FactoryConnect.Migrations exited with code $migrationExitCode." }
    $checks.Add([ordered]@{ id = 'migration-repository-current'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'exit code 0' })
    Close-RehearsalProcessStreams -OwnedProcess $migration

    $currentPhase = 'RuntimeStartupAcceptance'
    $edgeEnvironment = @{
        DOTNET_ENVIRONMENT = 'Production'
        Persistence__Provider = 'SqlServer'
        PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
    }
    Add-RehearsalMachineEnvironment -Environment $edgeEnvironment -Configuration $configuration -FixturePublicBaseAddress $fixtureBaseAddress
    Add-RehearsalProductionEnvironment -Environment $edgeEnvironment -Configuration $configuration

    $edge = Start-RehearsalProcess `
        -Role 'Edge' `
        -FilePath (Join-Path $candidateRoot 'edge/FactoryConnect.Edge.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'edge') `
        -StdOutPath (Join-Path $logs 'edge.stdout.log') `
        -StdErrPath (Join-Path $logs 'edge.stderr.log') `
        -Environment $edgeEnvironment
    $ownedProcesses.Add($edge)
    Start-Sleep -Seconds 2
    if ($edge.Process.HasExited) { throw "Edge exited during startup with code $($edge.Process.ExitCode)." }
    $checks.Add([ordered]@{ id = 'edge-startup'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'process remained active' })

    $apiEnvironment = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        ASPNETCORE_URLS = "http://0.0.0.0:$([int]$configuration.api.port)"
        Persistence__Provider = 'SqlServer'
        PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
    }
    Add-RehearsalMachineEnvironment -Environment $apiEnvironment -Configuration $configuration -FixturePublicBaseAddress $fixtureBaseAddress

    $api = Start-RehearsalProcess `
        -Role 'Api' `
        -FilePath (Join-Path $candidateRoot 'api/FactoryConnect.Api.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'api') `
        -StdOutPath (Join-Path $logs 'api.stdout.log') `
        -StdErrPath (Join-Path $logs 'api.stderr.log') `
        -Environment $apiEnvironment
    $ownedProcesses.Add($api)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$apiBaseAddress/health") -TimeoutSeconds $StartupTimeoutSeconds)
    $checks.Add([ordered]@{ id = 'api-health'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'HTTP 200' })

    $dashboardEnvironment = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        ASPNETCORE_URLS = "http://0.0.0.0:$([int]$configuration.dashboard.port)"
        Dashboard__ReportingApiBaseAddress = $apiBaseAddress
        Dashboard__RequestTimeout = '00:00:30'
    }
    for ($index = 0; $index -lt $configuration.machines.Count; $index++) {
        $machine = $configuration.machines[$index]
        $prefix = "Dashboard__Sources__${index}"
        $dashboardEnvironment["${prefix}__MachineId"] = [string]$machine.machineId
        $dashboardEnvironment["${prefix}__ProcessorId"] = [string]$machine.processorId
        $dashboardEnvironment["${prefix}__SiteId"] = [string]$configuration.siteId
        $dashboardEnvironment["${prefix}__ProductionLineId"] = [string]$machine.productionLineId
        $dashboardEnvironment["${prefix}__DisplayName"] = [string]$machine.deviceKey
        $dashboardEnvironment["${prefix}__GroupName"] = [string]$machine.productionLineId
        $dashboardEnvironment["${prefix}__DisplayOrder"] = [string]$index
    }

    $dashboard = Start-RehearsalProcess `
        -Role 'Dashboard' `
        -FilePath (Join-Path $candidateRoot 'dashboard/FactoryConnect.Dashboard.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'dashboard') `
        -StdOutPath (Join-Path $logs 'dashboard.stdout.log') `
        -StdErrPath (Join-Path $logs 'dashboard.stderr.log') `
        -Environment $dashboardEnvironment
    $ownedProcesses.Add($dashboard)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$dashboardBaseAddress/health/live") -TimeoutSeconds $StartupTimeoutSeconds)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$dashboardBaseAddress/health/ready") -TimeoutSeconds $StartupTimeoutSeconds)
    $checks.Add([ordered]@{ id = 'dashboard-ready'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'live + ready HTTP 200' })

    $runtimeConfig = Invoke-RestMethod -Uri "$dashboardBaseAddress/dashboard/config" -Method Get -TimeoutSec 10
    if ($runtimeConfig.sources.Count -ne 7) { throw "Dashboard runtime source count was $($runtimeConfig.sources.Count), expected 7." }
    $checks.Add([ordered]@{ id = 'seven-source-configuration'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = '7 sources' })

    $currentStateDeadline = [DateTimeOffset]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
    foreach ($machine in $configuration.machines) {
        $observed = $false
        do {
            try {
                $stateResponse = Invoke-WebRequest -Uri "$dashboardBaseAddress/api/machines/v1/$($machine.machineId)/current-state" -UseBasicParsing -TimeoutSec 5
                if ([int]$stateResponse.StatusCode -eq 200) { $observed = $true; break }
            }
            catch {}
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $currentStateDeadline)
        if (-not $observed) { throw "Current-state acceptance timed out for machine '$($machine.machineId)'." }
    }
    $checks.Add([ordered]@{ id = 'seven-source-current-state'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'HTTP 200 for all seven machines' })

    $today = [DateTime]::UtcNow.Date
    $reportBody = [ordered]@{
        sources = @($configuration.machines | ForEach-Object { [ordered]@{ machineId = [string]$_.machineId; processorId = [string]$_.processorId } })
        fromInclusive = $today.ToString('yyyy-MM-dd')
        toExclusive = $today.AddDays(1).ToString('yyyy-MM-dd')
        metrics = $null
        context = $null
        statuses = $null
        order = 'Ascending'
        pageSize = 100
        continuationToken = $null
    } | ConvertTo-Json -Depth 10
    $report = Invoke-WebRequest `
        -Uri "$dashboardBaseAddress/api/reporting/v1/operational-metrics/production-days/query" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $reportBody `
        -UseBasicParsing `
        -TimeoutSec 30
    if ([int]$report.StatusCode -ne 200) { throw 'Dashboard reporting acceptance did not return HTTP 200.' }
    [void]($report.Content | ConvertFrom-Json)
    $checks.Add([ordered]@{ id = 'reporting-roundtrip'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'Dashboard to API query returned valid JSON' })

    foreach ($service in @($dashboard, $api, $edge)) {
        [void](Stop-RehearsalProcess -OwnedProcess $service)
        Close-RehearsalProcessStreams -OwnedProcess $service
        [void]$ownedProcesses.Remove($service)
    }

    $restartPerformed = $true
    $edge2 = Start-RehearsalProcess -Role 'Edge-Restart' -FilePath (Join-Path $candidateRoot 'edge/FactoryConnect.Edge.exe') -WorkingDirectory (Join-Path $candidateRoot 'edge') -StdOutPath (Join-Path $logs 'edge.restart.stdout.log') -StdErrPath (Join-Path $logs 'edge.restart.stderr.log') -Environment $edgeEnvironment
    $ownedProcesses.Add($edge2)
    $api2 = Start-RehearsalProcess -Role 'Api-Restart' -FilePath (Join-Path $candidateRoot 'api/FactoryConnect.Api.exe') -WorkingDirectory (Join-Path $candidateRoot 'api') -StdOutPath (Join-Path $logs 'api.restart.stdout.log') -StdErrPath (Join-Path $logs 'api.restart.stderr.log') -Environment $apiEnvironment
    $ownedProcesses.Add($api2)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$apiBaseAddress/health") -TimeoutSeconds $StartupTimeoutSeconds)
    $dashboard2 = Start-RehearsalProcess -Role 'Dashboard-Restart' -FilePath (Join-Path $candidateRoot 'dashboard/FactoryConnect.Dashboard.exe') -WorkingDirectory (Join-Path $candidateRoot 'dashboard') -StdOutPath (Join-Path $logs 'dashboard.restart.stdout.log') -StdErrPath (Join-Path $logs 'dashboard.restart.stderr.log') -Environment $dashboardEnvironment
    $ownedProcesses.Add($dashboard2)
    [void](Wait-RehearsalHttp200 -Uri ([Uri]"$dashboardBaseAddress/health/ready") -TimeoutSeconds $StartupTimeoutSeconds)
    $restartPassed = $true
    $checks.Add([ordered]@{ id = 'same-candidate-restart'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'Edge/API/Dashboard regained readiness' })

    $outcome = 'Passed'
}
catch [System.OperationCanceledException] {
    $outcome = 'Canceled'
    $failureClassification = Get-RehearsalFailureClassification -Phase $currentPhase
    $failureMessage = ConvertTo-RehearsalRedactedText $_.Exception.Message
}
catch {
    $outcome = 'Failed'
    $failureClassification = Get-RehearsalFailureClassification -Phase $currentPhase
    $failureMessage = ConvertTo-RehearsalRedactedText $_.Exception.Message
}
finally {
    for ($index = $ownedProcesses.Count - 1; $index -ge 0; $index--) {
        $owned = $ownedProcesses[$index]
        try {
            [void](Stop-RehearsalProcess -OwnedProcess $owned)
        }
        catch {
            $owned.TerminationReason = 'CleanupFailed'
            $owned.StoppedAtUtc = [DateTimeOffset]::UtcNow
            $cleanupFailures.Add("$($owned.Role): $(ConvertTo-RehearsalRedactedText $_.Exception.Message)")
        }

        try {
            Close-RehearsalProcessStreams -OwnedProcess $owned -SkipStop
        }
        catch {
            $cleanupFailures.Add("$($owned.Role) evidence: $(ConvertTo-RehearsalRedactedText $_.Exception.Message)")
        }
    }

    $currentPhase = 'CandidatePostVerification'
    try {
        $afterVerification = & (Join-Path $PSScriptRoot 'Test-DemoCandidate.ps1') `
            -CandidatePath $candidateRoot `
            -ExpectedCandidateId $candidateId `
            -ExpectedSourceCommit $ExpectedSourceCommit `
            -ExpectedDeploymentContractCommit $ExpectedDeploymentContractCommit
        $checks.Add([ordered]@{ id = 'candidate-post-verification'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = $afterVerification.ManifestSha256 })
    }
    catch {
        $outcome = 'Failed'
        $failureClassification = Get-RehearsalFailureClassification -Phase $currentPhase
        $failureMessage = ConvertTo-RehearsalRedactedText $_.Exception.Message
    }

    $candidateUnchanged = $null -ne $beforeVerification -and $null -ne $afterVerification -and $beforeVerification.ManifestSha256 -eq $afterVerification.ManifestSha256
    if (-not $candidateUnchanged) {
        $outcome = 'Failed'
        $failureClassification = 'Verification'
        if ([string]::IsNullOrWhiteSpace($failureMessage)) { $failureMessage = 'Candidate pre/post manifest identity did not match.' }
    }

    if ($cleanupFailures.Count -gt 0) {
        $outcome = 'Failed'
        $failureClassification = 'Verification'
        $cleanupFailureMessage = 'Rehearsal cleanup verification failed: ' + ($cleanupFailures -join ' | ')
        if ([string]::IsNullOrWhiteSpace($failureMessage)) {
            $failureMessage = $cleanupFailureMessage
        }
        else {
            $failureMessage = ConvertTo-RehearsalRedactedText "$failureMessage | $cleanupFailureMessage"
        }
        $checks.Add([ordered]@{ id = 'process-cleanup'; outcome = 'Failed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'one or more owned processes could not be cleanly verified' })
    }
    else {
        $checks.Add([ordered]@{ id = 'process-cleanup'; outcome = 'Passed'; observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); detail = 'all owned processes cleaned and journaled' })
    }

    $completedAtUtc = [DateTimeOffset]::UtcNow
    $evidence = [ordered]@{
        schemaVersion = '1.0'
        candidateId = $candidateId
        attemptId = $AttemptId
        candidateManifestSha256 = if ($null -ne $beforeVerification) { $beforeVerification.ManifestSha256 } else { $null }
        applicationSourceCommit = $ExpectedSourceCommit.ToLowerInvariant()
        deploymentContractCommit = $ExpectedDeploymentContractCommit.ToLowerInvariant()
        rehearsalToolSourceCommit = $RehearsalToolSourceCommit.ToLowerInvariant()
        startedAtUtc = $startedAtUtc.ToString('O')
        completedAtUtc = $completedAtUtc.ToString('O')
        outcome = $outcome
        failureClassification = $failureClassification
        failureMessage = $failureMessage
        rehearsalConfigurationSha256 = $configurationSha256
        database = [ordered]@{
            serverIdentity = [string]$configuration.database.serverIdentity
            databaseName = [string]$configuration.database.databaseName
            migrationExitCode = $migrationExitCode
            repositoryCurrent = $repositoryCurrent
        }
        processes = @($processEvidence)
        checks = @($checks)
        candidateVerification = [ordered]@{
            beforeManifestSha256 = if ($null -ne $beforeVerification) { $beforeVerification.ManifestSha256 } else { $null }
            afterManifestSha256 = if ($null -ne $afterVerification) { $afterVerification.ManifestSha256 } else { $null }
            unchanged = $candidateUnchanged
        }
        sevenSourceProof = [ordered]@{
            configured = $configuration.machines.Count
            observed = if (@($checks.id) -contains 'seven-source-current-state') { 7 } else { 0 }
        }
        restartProof = [ordered]@{
            performed = $restartPerformed
            passed = $restartPassed
        }
    }

    $currentPhase = 'EvidenceFinalization'
    $evidenceText = (($evidence | ConvertTo-Json -Depth 30) -replace "`r`n", "`n") + "`n"
    $publishedEvidence = Publish-RehearsalTerminalEvidence `
        -EvidenceRoot $evidenceRoot `
        -EvidenceText $evidenceText
}

[pscustomobject]@{
    CandidateId = $candidateId
    AttemptId = $AttemptId
    Outcome = $outcome
    FailureClassification = $failureClassification
    EvidencePath = $publishedEvidence.EvidencePath
    EvidenceSha256 = $publishedEvidence.EvidenceSha256
    RehearsalConfigurationSha256 = $configurationSha256
}

if ($outcome -ne 'Passed') { exit 1 }
