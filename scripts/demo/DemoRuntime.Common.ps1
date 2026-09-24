Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '../release/Rehearsal.Common.ps1')

$script:DemoDeploymentContractCommit = '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc'
$script:DemoDatabaseName = 'FactoryConnect_Demo'

function Get-DemoContract {
    [pscustomobject]@{
        DeploymentContractCommit = $script:DemoDeploymentContractCommit
        DatabaseName = $script:DemoDatabaseName
    }
}

function Resolve-DemoDatabaseTarget {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'Interactive demo SQL connection string is required.'
    }

    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    catch {
        throw "Interactive demo SQL connection string is invalid: $($_.Exception.Message)"
    }

    $server = [string]$builder.DataSource
    if ([string]::IsNullOrWhiteSpace($server)) {
        throw 'Interactive demo SQL configuration must contain an explicit Server or Data Source.'
    }

    $databaseName = [string]$builder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "Interactive demo SQL configuration must explicitly name database '$script:DemoDatabaseName'; inferred or default database names are forbidden."
    }
    if ($databaseName -cne $script:DemoDatabaseName) {
        throw "Interactive demo SQL connection targets '$databaseName'; expected exactly '$script:DemoDatabaseName'."
    }

    $integrated = [bool]$builder.IntegratedSecurity
    $userId = [string]$builder.UserID
    $password = [string]$builder.Password
    if (-not $integrated -and [string]::IsNullOrWhiteSpace($userId)) {
        throw 'Interactive demo SQL access requires Integrated Security or an explicit User ID.'
    }

    [pscustomobject]@{
        Server = $server
        DatabaseName = $databaseName
        IntegratedSecurity = $integrated
        UserId = if ([string]::IsNullOrWhiteSpace($userId)) { $null } else { $userId }
        Password = if ([string]::IsNullOrEmpty($password)) { $null } else { $password }
        ConnectionString = $ConnectionString
    }
}

function Invoke-DemoSqlcmd {
    param(
        [Parameter(Mandatory = $true)]$DatabaseTarget,
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $sqlcmd = Get-Command sqlcmd -ErrorAction Stop
    $arguments = @('-S', [string]$DatabaseTarget.Server, '-d', $Database, '-b', '-Q', $Query)
    if ([bool]$DatabaseTarget.IntegratedSecurity) {
        $arguments += '-E'
    }
    else {
        $arguments += @('-U', [string]$DatabaseTarget.UserId)
        if (-not [string]::IsNullOrEmpty([string]$DatabaseTarget.Password)) {
            $env:SQLCMDPASSWORD = [string]$DatabaseTarget.Password
        }
    }

    try {
        & $sqlcmd.Source @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "sqlcmd failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    }
}

function Ensure-DemoDatabaseExists {
    param([Parameter(Mandatory = $true)]$DatabaseTarget)

    if ([string]$DatabaseTarget.DatabaseName -cne $script:DemoDatabaseName) {
        throw "Interactive demo database admission no longer targets exactly '$script:DemoDatabaseName'."
    }

    Invoke-DemoSqlcmd -DatabaseTarget $DatabaseTarget -Database 'master' -Query (
        "IF DB_ID(N'$script:DemoDatabaseName') IS NULL CREATE DATABASE [$script:DemoDatabaseName]"
    )
}

function Get-DemoSupervisorLockPath {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    Join-Path $RepoRoot 'artifacts/demo/supervisor.lock'
}

function Open-DemoSupervisorLease {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][ValidateSet('Supervisor','Reset')][string]$Purpose
    )

    $lockPath = Get-DemoSupervisorLockPath -RepoRoot $RepoRoot
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $lockPath)) | Out-Null

    try {
        $stream = [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        if ($Purpose -eq 'Reset') {
            throw 'Interactive demo reset refused because the demo supervisor is running.'
        }
        throw 'Interactive demo supervisor is already running.'
    }

    try {
        $stream.SetLength(0)
        $text = "purpose=$Purpose`npid=$PID`nstartedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))`n"
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    catch {
        $stream.Dispose()
        throw
    }

    return $stream
}

function Assert-DemoCandidateApproval {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedCandidateId,
        [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceCommit
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedCandidateId) -or
        $ExpectedCandidateId -notmatch '^demo-candidate-[0-9]{8}-[0-9]{2}$') {
        throw 'ExpectedCandidateId must be an explicit demo candidate ID.'
    }
    if ($ExpectedManifestSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'ExpectedManifestSha256 must be a lowercase 64-character SHA-256 value.'
    }
    if ($ExpectedSourceCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'ExpectedSourceCommit must be a lowercase 40-character commit SHA.'
    }
}

function Assert-DemoCandidateManifestApproval {
    param(
        [Parameter(Mandatory = $true)][string]$ActualManifestSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256
    )

    if ($ActualManifestSha256 -cne $ExpectedManifestSha256) {
        throw "Interactive demo candidate manifest SHA-256 '$ActualManifestSha256' does not match approved SHA-256 '$ExpectedManifestSha256'."
    }
}

function Assert-DemoCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$ExpectedCandidateId,
        [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceCommit
    )

    Assert-DemoCandidateApproval -ExpectedCandidateId $ExpectedCandidateId `
        -ExpectedManifestSha256 $ExpectedManifestSha256 `
        -ExpectedSourceCommit $ExpectedSourceCommit

    $candidateRoot = (Resolve-Path -LiteralPath $CandidatePath).Path
    $candidateId = Split-Path -Leaf $candidateRoot
    if ($candidateId -cne $ExpectedCandidateId) {
        throw "Interactive demo requires approved candidate '$ExpectedCandidateId'; received '$candidateId'."
    }

    $verification = & (Join-Path $PSScriptRoot '../release/Test-DemoCandidate.ps1') `
        -CandidatePath $candidateRoot `
        -ExpectedCandidateId $ExpectedCandidateId `
        -ExpectedSourceCommit $ExpectedSourceCommit `
        -ExpectedDeploymentContractCommit $script:DemoDeploymentContractCommit

    Assert-DemoCandidateManifestApproval `
        -ActualManifestSha256 ([string]$verification.ManifestSha256) `
        -ExpectedManifestSha256 $ExpectedManifestSha256

    return [pscustomobject]@{
        CandidateRoot = $candidateRoot
        Verification = $verification
    }
}

function Assert-DemoSevenMachineConfiguration {
    param([Parameter(Mandatory = $true)]$Configuration)

    if ($Configuration.machines.Count -ne 7) {
        throw 'Interactive demo configuration must contain exactly seven machines.'
    }
    if ((@($Configuration.machines.machineId | Sort-Object -Unique)).Count -ne 7) {
        throw 'Interactive demo MachineIds must be unique.'
    }
    if ((@($Configuration.machines.deviceKey | Sort-Object -Unique)).Count -ne 7) {
        throw 'Interactive demo DeviceKeys must be unique.'
    }
    if ((@($Configuration.machines.streamIdentity | Sort-Object -Unique)).Count -ne 7) {
        throw 'Interactive demo StreamIdentities must be unique.'
    }

    foreach ($machine in $Configuration.machines) {
        $expectedStreamIdentity = "mtconnect:$([string]$machine.deviceKey)"
        if ([string]$machine.streamIdentity -cne $expectedStreamIdentity) {
            throw "Interactive demo stream identity '$([string]$machine.streamIdentity)' does not match '$expectedStreamIdentity'."
        }
    }
}

function Add-DemoMachineEnvironment {
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

function Add-DemoObservationProcessingEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Environment,
        [Parameter(Mandatory = $true)]$Configuration
    )

    for ($index = 0; $index -lt $Configuration.machines.Count; $index++) {
        $machine = $Configuration.machines[$index]
        $prefix = "ObservationProcessing__Streams__${index}"
        $Environment["${prefix}__MachineId"] = [string]$machine.machineId
        $Environment["${prefix}__StreamKey"] = [string]$machine.streamIdentity
    }
}

function Add-DemoProductionEnvironment {
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

    $productionLines = @($Configuration.machines.productionLineId | Sort-Object -Unique)
    $scheduleIndex = 0
    foreach ($productionLineId in $productionLines) {
        foreach ($shift in $Configuration.shifts) {
            $prefix = "ProductionProcessing__ShiftSchedules__${scheduleIndex}"
            $Environment["${prefix}__AssignmentId"] = "SHIFT-SCHEDULE-$productionLineId-$($shift.shiftId)"
            $Environment["${prefix}__CompanyId"] = 'GAJRA'
            $Environment["${prefix}__SiteId"] = [string]$Configuration.siteId
            $Environment["${prefix}__ProductionLineId"] = [string]$productionLineId
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
            $scheduleIndex++
        }
    }
}

function Assert-DemoOwnedProcessAlive {
    param([Parameter(Mandatory = $true)]$OwnedProcess)

    $process = $OwnedProcess.Process
    $process.Refresh()
    if (-not $process.HasExited) { return }

    $exitCode = $null
    try { $exitCode = $process.ExitCode } catch {}
    $detail = if ($null -ne $exitCode) { " with exit code $exitCode" } else { '' }
    throw "Interactive demo process '$($OwnedProcess.Role)' exited$detail."
}

function Wait-DemoOwnedProcessStability {
    param(
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter()][ValidateRange(1, 300)][int]$Seconds = 2
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        Assert-DemoOwnedProcessAlive -OwnedProcess $OwnedProcess
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    Assert-DemoOwnedProcessAlive -OwnedProcess $OwnedProcess
}

function Wait-DemoHttp200 {
    param(
        [Parameter(Mandatory = $true)][Uri]$Uri,
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Assert-DemoOwnedProcessAlive -OwnedProcess $OwnedProcess
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

function Write-DemoRuntimeState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$CandidateId,
        [Parameter(Mandatory = $true)][System.Collections.IEnumerable]$OwnedProcesses,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter()][string]$DashboardUrl
    )

    $document = [ordered]@{
        schemaVersion = '1.0'
        candidateId = $CandidateId
        databaseName = $script:DemoDatabaseName
        supervisorPid = $PID
        phase = $Phase
        dashboardUrl = $DashboardUrl
        processes = @($OwnedProcesses | ForEach-Object {
            [ordered]@{
                role = [string]$_.Role
                pid = [int]$_.Process.Id
                startedAtUtc = $_.StartedAtUtc.ToString('O')
            }
        })
    }

    $json = (($document | ConvertTo-Json -Depth 10) -replace "`r`n", "`n") + "`n"
    Write-DemoCandidateUtf8NoBom -Path $Path -Text $json
}

function Close-DemoOwnedProcess {
    param([Parameter(Mandatory = $true)]$OwnedProcess)

    try {
        if (-not $OwnedProcess.Process.HasExited) {
            [void](Stop-RehearsalProcess -OwnedProcess $OwnedProcess)
        }
    }
    finally {
        if ($null -ne $OwnedProcess.PSObject.Properties['StreamCapture']) {
            try { $OwnedProcess.StreamCapture.Complete() } catch {}
            try { $OwnedProcess.StreamCapture.Dispose() } catch {}
        }
        try { $OwnedProcess.Process.Dispose() } catch {}
    }
}

function Stop-DemoOwnedProcesses {
    param([Parameter(Mandatory = $true)][System.Collections.IList]$OwnedProcesses)

    $failures = [System.Collections.Generic.List[string]]::new()
    for ($index = $OwnedProcesses.Count - 1; $index -ge 0; $index--) {
        $owned = $OwnedProcesses[$index]
        try {
            Close-DemoOwnedProcess -OwnedProcess $owned
        }
        catch {
            $failures.Add("$($owned.Role): $($_.Exception.Message)")
        }
    }

    $OwnedProcesses.Clear()
    if ($failures.Count -gt 0) {
        throw ('Interactive demo cleanup failed: ' + ($failures -join ' | '))
    }
}
