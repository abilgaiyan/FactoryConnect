# Dot-sourced only by the Edge restart owner while holding deployment.lock.
function Initialize-MoxaMaintenance {
    param($Plan, [string]$Root, [string]$ReleaseId, $Runtime, [string]$LogDirectory)
    foreach ($name in @('OperationId','ExpectedServer','ExpectedDatabase','ApprovalReference','ExternalLaunchersSuspendedReference','RecoveryExecutable','RecoveryExecutableSha256')) {
        if ([string]::IsNullOrWhiteSpace([string]$Plan.$name)) { throw "MOXA plan requires $name." }
    }
    [void][Guid]::Parse([string]$Plan.OperationId)
    if ([Guid]$Plan.OperationId -eq [Guid]::Empty) { throw 'Empty operation ID.' }
    if ((Get-FileHash -LiteralPath $Plan.RecoveryExecutable -Algorithm SHA256).Hash -cne [string]$Plan.RecoveryExecutableSha256) {
        throw 'Recovery executable checksum mismatch.'
    }
    # Independent service supervision is outside the deployment lock protocol.
    # Do not stop/reconfigure it speculatively. Commission its exclusion first.
    $services = @(Get-CimInstance Win32_Service -ErrorAction Stop | Where-Object {
        $_.StartMode -ne 'Disabled' -and ($_.PathName -match 'FactoryConnect\.Edge' -or $_.PathName -like "*$Root*")
    })
    if ($services.Count -ne 0) { throw 'Independent FactoryConnect service launcher must be disabled and reviewed before maintenance.' }
    $script:MoxaMarkerPath = Join-Path $Root 'deployment/moxa-maintenance.intent.json'
    $script:MoxaEvidencePath = Join-Path $LogDirectory 'moxa-maintenance.json'
    $script:MoxaConfigPath = Join-Path $Root 'config/edge.production.json'
    $script:MoxaOriginalConfig = [IO.File]::ReadAllText($script:MoxaConfigPath)
    $script:MoxaOriginalHash = (Get-FileHash -LiteralPath $script:MoxaConfigPath -Algorithm SHA256).Hash
    $script:MoxaCandidate = $script:MoxaOriginalConfig | ConvertFrom-Json
    $machine = @($script:MoxaCandidate.ProductionProcessing.Machines | Where-Object {
        [Guid]$_.MachineId -eq [Guid]'bfa0dc97-9a68-45ed-b189-1dacb7d863f0'
    })
    if ($machine.Count -ne 1 -or [string]$machine[0].ActivityStreamKey -cne 'mtconnect:DEWAS-SR-MOXA-001') { throw 'Exact MOXA configuration required.' }
    if ($null -ne $machine[0].PSObject.Properties['ProductionStartsAt'] -and $null -ne $machine[0].ProductionStartsAt) {
        throw 'MOXA configuration already has a boundary; reconcile explicitly.'
    }
    $machine[0] | Add-Member -MemberType NoteProperty -Name ProductionStartsAt -Value '2026-10-25T02:00:00Z' -Force
    $script:MoxaEvidence = [ordered]@{
        schemaVersion=1; operationId=[string]$Plan.OperationId; approvalReference=[string]$Plan.ApprovalReference
        externalLaunchersSuspendedReference=[string]$Plan.ExternalLaunchersSuspendedReference
        selectedRelease=$ReleaseId; oldEdge=$Runtime.edge; originalConfigSha256=$script:MoxaOriginalHash
        recoveryExecutableSha256=[string]$Plan.RecoveryExecutableSha256; phase='Prepared'
    }
    # Persistent barrier survives loss of the wrapper and its OS file lock.
    Write-JsonFileAtomic $script:MoxaMarkerPath $script:MoxaEvidence
    Write-JsonFileAtomic $script:MoxaEvidencePath $script:MoxaEvidence
}

function Invoke-MoxaMaintenanceCorrection {
    param($Plan)
    # Includes unrecorded Edge instances at any release/path, plus dotnet-hosted Edge.
    $candidates = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -ieq 'FactoryConnect.Edge.exe' -or
        ($_.Name -ieq 'dotnet.exe' -and ($null -eq $_.CommandLine -or $_.CommandLine -match 'FactoryConnect\.Edge'))
    })
    if ($candidates.Count -ne 0) { throw 'Edge absence cannot be established.' }
    if ((Get-FileHash -LiteralPath $script:MoxaConfigPath -Algorithm SHA256).Hash -cne $script:MoxaOriginalHash) {
        throw 'Edge configuration changed during maintenance.'
    }
    $script:MoxaOriginalConfig | Set-Content -LiteralPath ($script:MoxaEvidencePath + '.edge-config.backup') -Encoding utf8
    $script:MoxaEvidence.phase = 'EdgeAbsent'
    Write-JsonFileAtomic $script:MoxaMarkerPath $script:MoxaEvidence
    Write-JsonFileAtomic $script:MoxaEvidencePath $script:MoxaEvidence
    $saved = [Environment]::GetEnvironmentVariable('FACTORYCONNECT_MOXA_RECOVERY_CONNECTION_STRING','Process')
    try {
        [Environment]::SetEnvironmentVariable('FACTORYCONNECT_MOXA_RECOVERY_CONNECTION_STRING',
            [string]$script:MoxaCandidate.PersistenceProviders.SqlServer.ConnectionString,'Process')
        & $Plan.RecoveryExecutable ([string]$Plan.OperationId) ([string]$Plan.ExpectedServer) ([string]$Plan.ExpectedDatabase) ([string]$Plan.ApprovalReference) $script:MoxaEvidencePath
        if ($LASTEXITCODE -ne 0) { throw 'MOXA SQL correction failed or commit outcome is uncertain; retain maintenance barrier.' }
    } finally {
        [Environment]::SetEnvironmentVariable('FACTORYCONNECT_MOXA_RECOVERY_CONNECTION_STRING',$saved,'Process')
    }
    $script:MoxaEvidence.phase = 'SqlCommitted'
    Write-JsonFileAtomic $script:MoxaMarkerPath $script:MoxaEvidence
    Write-JsonFileAtomic $script:MoxaEvidencePath $script:MoxaEvidence
    # SQL has committed. Never restore unbounded configuration automatically.
    Write-JsonFileAtomic $script:MoxaConfigPath $script:MoxaCandidate
    $script:MoxaEvidence.phase = 'ConfigurationActivated'
    Write-JsonFileAtomic $script:MoxaMarkerPath $script:MoxaEvidence
    Write-JsonFileAtomic $script:MoxaEvidencePath $script:MoxaEvidence
}
