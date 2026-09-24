[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidatePath,
    [Parameter(Mandatory = $true)][string]$DemoConfigurationPath,
    [Parameter(Mandatory = $true)][string]$SqlConnectionString,
    [Parameter(Mandatory = $true)][string]$FixtureExecutablePath,
    [Parameter()][ValidateRange(1, 300)][int]$StartupTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoRuntime.Common.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$contract = Get-DemoContract
$supervisorLease = $null
$ownedProcesses = [System.Collections.Generic.List[object]]::new()
$runtimeStatePath = Join-Path $repoRoot 'artifacts/demo/runtime.json'
$dashboardUrl = $null
$cleanupError = $null

try {
    $supervisorLease = Open-DemoSupervisorLease -RepoRoot $repoRoot -Purpose Supervisor

    $candidate = Assert-DemoCandidate -CandidatePath $CandidatePath
    $candidateRoot = $candidate.CandidateRoot

    $configuration = Get-Content -Raw -LiteralPath $DemoConfigurationPath | ConvertFrom-Json
    Assert-DemoSevenMachineConfiguration -Configuration $configuration

    if ([string]$configuration.database.databaseName -cne $contract.DatabaseName) {
        throw "Interactive demo configuration database is '$([string]$configuration.database.databaseName)'; expected exactly '$($contract.DatabaseName)'."
    }

    $databaseTarget = Resolve-DemoDatabaseTarget -ConnectionString $SqlConnectionString

    $fixtureHost = [string]$configuration.fixture.listenAddress
    $apiHost = [string]$configuration.api.listenAddress
    $dashboardHost = [string]$configuration.dashboard.listenAddress
    foreach ($hostName in @($fixtureHost, $apiHost, $dashboardHost)) {
        if ([string]::IsNullOrWhiteSpace($hostName)) {
            throw 'Interactive demo fixture/API/Dashboard listen addresses must be explicit.'
        }
    }

    $fixtureBaseAddress = "http://${fixtureHost}:$([int]$configuration.fixture.port)"
    $apiBaseAddress = "http://${apiHost}:$([int]$configuration.api.port)"
    $dashboardBaseAddress = "http://${dashboardHost}:$([int]$configuration.dashboard.port)"

    $sessionId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $sessionRoot = Join-Path $repoRoot "artifacts/demo/sessions/$sessionId"
    $logs = Join-Path $sessionRoot 'logs'
    [System.IO.Directory]::CreateDirectory($logs) | Out-Null

    Write-Host "FactoryConnect interactive demo"
    Write-Host "Candidate : $($contract.CandidateId)"
    Write-Host "Database  : $($contract.DatabaseName)"
    Write-Host "Session   : $sessionRoot"

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'DatabaseProvisioning'
    Ensure-DemoDatabaseExists -DatabaseTarget $databaseTarget

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'FixtureStartup'
    Assert-RehearsalPortAvailable -Port ([int]$configuration.fixture.port)
    $fixtureInvocationNonce = New-RehearsalInvocationNonce
    $fixtureExecutable = (Resolve-Path -LiteralPath $FixtureExecutablePath).Path
    $fixture = Start-RehearsalProcess `
        -Role 'Fixture' `
        -FilePath $fixtureExecutable `
        -WorkingDirectory (Split-Path -Parent $fixtureExecutable) `
        -StdOutPath (Join-Path $logs 'fixture.stdout.log') `
        -StdErrPath (Join-Path $logs 'fixture.stderr.log') `
        -Environment @{
            ASPNETCORE_ENVIRONMENT = 'Production'
            ASPNETCORE_URLS = "http://0.0.0.0:$([int]$configuration.fixture.port)"
            FACTORYCONNECT_REHEARSAL_INVOCATION_NONCE = $fixtureInvocationNonce
        }
    $ownedProcesses.Add($fixture)
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'FixtureReadiness'
    [void](Wait-RehearsalFixtureReadiness `
        -OwnedProcess $fixture `
        -HealthUri ([Uri]"$fixtureBaseAddress/health") `
        -Port ([int]$configuration.fixture.port) `
        -ExpectedInvocationNonce $fixtureInvocationNonce `
        -TimeoutSeconds $StartupTimeoutSeconds)

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'MigrationExecution'
    $migration = Start-RehearsalProcess `
        -Role 'Migrations' `
        -FilePath (Join-Path $candidateRoot 'migrations/FactoryConnect.Migrations.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'migrations') `
        -StdOutPath (Join-Path $logs 'migrations.stdout.log') `
        -StdErrPath (Join-Path $logs 'migrations.stderr.log') `
        -Environment @{
            DOTNET_ENVIRONMENT = 'Production'
            PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
        }
    $ownedProcesses.Add($migration)
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'MigrationExecution'
    $migration.Process.WaitForExit()
    if ($migration.Process.ExitCode -ne 0) {
        throw "FactoryConnect.Migrations exited with code $($migration.Process.ExitCode)."
    }
    Close-DemoOwnedProcess -OwnedProcess $migration
    [void]$ownedProcesses.Remove($migration)

    $edgeEnvironment = @{
        DOTNET_ENVIRONMENT = 'Production'
        Persistence__Provider = 'SqlServer'
        PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
    }
    Add-DemoMachineEnvironment -Environment $edgeEnvironment -Configuration $configuration -FixturePublicBaseAddress $fixtureBaseAddress
    Add-DemoObservationProcessingEnvironment -Environment $edgeEnvironment -Configuration $configuration
    Add-DemoProductionEnvironment -Environment $edgeEnvironment -Configuration $configuration

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'EdgeStartup'
    $edge = Start-RehearsalProcess `
        -Role 'Edge' `
        -FilePath (Join-Path $candidateRoot 'edge/FactoryConnect.Edge.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'edge') `
        -StdOutPath (Join-Path $logs 'edge.stdout.log') `
        -StdErrPath (Join-Path $logs 'edge.stderr.log') `
        -Environment $edgeEnvironment
    $ownedProcesses.Add($edge)
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'EdgeStability'
    Wait-DemoOwnedProcessStability -OwnedProcess $edge -Seconds 2

    $apiEnvironment = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        ASPNETCORE_URLS = "http://0.0.0.0:$([int]$configuration.api.port)"
        Persistence__Provider = 'SqlServer'
        PersistenceProviders__SqlServer__ConnectionString = $SqlConnectionString
    }
    Add-DemoMachineEnvironment -Environment $apiEnvironment -Configuration $configuration -FixturePublicBaseAddress $fixtureBaseAddress

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'ApiStartup'
    $api = Start-RehearsalProcess `
        -Role 'Api' `
        -FilePath (Join-Path $candidateRoot 'api/FactoryConnect.Api.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'api') `
        -StdOutPath (Join-Path $logs 'api.stdout.log') `
        -StdErrPath (Join-Path $logs 'api.stderr.log') `
        -Environment $apiEnvironment
    $ownedProcesses.Add($api)
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'ApiReadiness'
    [void](Wait-DemoHttp200 -Uri ([Uri]"$apiBaseAddress/health") -OwnedProcess $api -TimeoutSeconds $StartupTimeoutSeconds)
    Assert-DemoOwnedProcessAlive -OwnedProcess $edge

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

    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'DashboardStartup'
    $dashboard = Start-RehearsalProcess `
        -Role 'Dashboard' `
        -FilePath (Join-Path $candidateRoot 'dashboard/FactoryConnect.Dashboard.exe') `
        -WorkingDirectory (Join-Path $candidateRoot 'dashboard') `
        -StdOutPath (Join-Path $logs 'dashboard.stdout.log') `
        -StdErrPath (Join-Path $logs 'dashboard.stderr.log') `
        -Environment $dashboardEnvironment
    $ownedProcesses.Add($dashboard)
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'DashboardReadiness'
    [void](Wait-DemoHttp200 -Uri ([Uri]"$dashboardBaseAddress/health/live") -OwnedProcess $dashboard -TimeoutSeconds $StartupTimeoutSeconds)
    [void](Wait-DemoHttp200 -Uri ([Uri]"$dashboardBaseAddress/health/ready") -OwnedProcess $dashboard -TimeoutSeconds $StartupTimeoutSeconds)

    foreach ($owned in @($fixture, $edge, $api, $dashboard)) {
        Assert-DemoOwnedProcessAlive -OwnedProcess $owned
    }

    $runtimeConfig = Invoke-RestMethod -Uri "$dashboardBaseAddress/dashboard/config" -Method Get -TimeoutSec 10
    if ($runtimeConfig.sources.Count -ne 7) {
        throw "Dashboard runtime source count was $($runtimeConfig.sources.Count), expected 7."
    }

    $dashboardUrl = "$dashboardBaseAddress/"
    Write-DemoRuntimeState -Path $runtimeStatePath -OwnedProcesses $ownedProcesses -Phase 'Running' -DashboardUrl $dashboardUrl

    Write-Host ''
    Write-Host 'FactoryConnect demo is ready.'
    Write-Host "Dashboard: $dashboardUrl"
    Write-Host 'Press Ctrl+C to stop the demo and clean up every owned process.'

    while ($true) {
        foreach ($owned in @($fixture, $edge, $api, $dashboard)) {
            Assert-DemoOwnedProcessAlive -OwnedProcess $owned
        }
        Start-Sleep -Seconds 1
    }
}
finally {
    try {
        if ($ownedProcesses.Count -gt 0) {
            Stop-DemoOwnedProcesses -OwnedProcesses $ownedProcesses
        }
    }
    catch {
        $cleanupError = $_.Exception
    }

    Remove-Item -LiteralPath $runtimeStatePath -Force -ErrorAction SilentlyContinue

    if ($null -ne $supervisorLease) {
        $supervisorLease.Dispose()
    }

    if ($null -ne $cleanupError) {
        throw $cleanupError
    }
}
