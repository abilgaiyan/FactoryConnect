$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repoRoot 'scripts/release/Rehearsal.Common.ps1')

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action, [Parameter(Mandatory = $true)][string]$Name)
    try { & $Action; throw "Expected rejection did not occur: $Name" }
    catch {
        if ($_.Exception.Message -like "Expected rejection did not occur:*") { throw }
        Write-Host "PASS (rejected): $Name"
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Actual -cne $Expected) {
        throw "$Name expected '$Expected' but observed '$Actual'."
    }
    Write-Host "PASS: $Name"
}

$templateText = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'config/rehearsal/demo.rehearsal.template.json')
$templateText = $templateText.Replace('__FIXTURE_LISTEN_ADDRESS__', 'rehearsal-host.local')
$templateText = $templateText.Replace('__API_LISTEN_ADDRESS__', 'rehearsal-host.local')
$templateText = $templateText.Replace('__DASHBOARD_LISTEN_ADDRESS__', 'rehearsal-host.local')
$templateText = $templateText.Replace('__SQLSERVER_NON_SECRET_IDENTITY__', 'sql-rehearsal.local')
$templateText = $templateText.Replace('__REHEARSAL_DATABASE_NAME__', 'FactoryConnect_Rehearsal_20260923_02')
$configuration = $templateText | ConvertFrom-Json
$projection = Get-RehearsalConfigurationProjection -Configuration $configuration -CandidateId 'demo-candidate-20260923-02'
$projectionText = $projection | ConvertTo-Json -Depth 20
$firstHash = Get-RehearsalProjectionSha256 -Projection $projection
$secondHash = Get-RehearsalProjectionSha256 -Projection $projection
if ($firstHash -cne $secondHash -or $firstHash -notmatch '^[0-9a-f]{64}$') { throw 'Non-secret rehearsal projection hash is not deterministic.' }
if ($projectionText -match '(?i)password|connectionstring|token|secret') { throw 'Non-secret rehearsal projection contains protected field names.' }
Write-Host 'PASS: non-secret configuration projection is deterministic.'

$redacted = ConvertTo-RehearsalRedactedText 'Login failed; Server=db;Database=FactoryConnect;User ID=sa;Password=VerySecret;'
if ($redacted -match 'VerySecret|User ID=sa|Password=') { throw 'Failure-message redaction did not remove protected SQL material.' }
Write-Host 'PASS: protected failure text is redacted.'

$serverAdmission = Resolve-RehearsalDatabaseAdmission `
    -ConnectionString 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect;Integrated Security=True' `
    -DatabaseName 'FactoryConnect_Rehearsal_20260923_02'
Assert-Equal -Actual $serverAdmission.Server -Expected '(localdb)\MSSQLLocalDB' -Name 'Server alias admitted'
Assert-Equal -Actual ([string]$serverAdmission.IntegratedSecurity) -Expected 'True' -Name 'Integrated Security admitted'

$dataSourceAdmission = Resolve-RehearsalDatabaseAdmission `
    -ConnectionString 'Data Source=sql-rehearsal.local;Database=FactoryConnect;Integrated Security=True' `
    -DatabaseName 'FactoryConnect_Rehearsal_20260923_02'
Assert-Equal -Actual $dataSourceAdmission.Server -Expected 'sql-rehearsal.local' -Name 'Data Source alias admitted'

$mixedCaseAdmission = Resolve-RehearsalDatabaseAdmission `
    -ConnectionString 'dAtA sOuRcE=sql-mixed.local;Database=FactoryConnect;Integrated Security=True' `
    -DatabaseName 'FactoryConnect_Rehearsal_20260923_02'
Assert-Equal -Actual $mixedCaseAdmission.Server -Expected 'sql-mixed.local' -Name 'mixed-case alias admitted'

Assert-Throws -Name 'missing SQL server alias' -Action {
    Resolve-RehearsalDatabaseAdmission `
        -ConnectionString 'Database=FactoryConnect;Integrated Security=True' `
        -DatabaseName 'FactoryConnect_Rehearsal_20260923_02' | Out-Null
}
Assert-Throws -Name 'empty SQL server identity' -Action {
    Resolve-RehearsalDatabaseAdmission `
        -ConnectionString 'Server=;Database=FactoryConnect;Integrated Security=True' `
        -DatabaseName 'FactoryConnect_Rehearsal_20260923_02' | Out-Null
}
Assert-Throws -Name 'whitespace SQL server identity' -Action {
    Resolve-RehearsalDatabaseAdmission `
        -ConnectionString 'Server=   ;Database=FactoryConnect;Integrated Security=True' `
        -DatabaseName 'FactoryConnect_Rehearsal_20260923_02' | Out-Null
}

$phaseExpectations = [ordered]@{
    CandidatePreVerification = 'Verification'
    ConfigurationAdmission = 'Configuration'
    DatabaseProvisioning = 'Infrastructure'
    FixtureStartup = 'Dependency'
    MigrationExecution = 'Application'
    RuntimeStartupAcceptance = 'Application'
    CandidatePostVerification = 'Verification'
    EvidenceFinalization = 'Verification'
}
foreach ($entry in $phaseExpectations.GetEnumerator()) {
    Assert-Equal `
        -Actual (Get-RehearsalFailureClassification -Phase ([string]$entry.Key)) `
        -Expected ([string]$entry.Value) `
        -Name "phase $($entry.Key) classification"
}

$runnerText = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts/release/Invoke-DemoCandidateRehearsal.ps1')
$admissionIndex = $runnerText.IndexOf('Resolve-RehearsalDatabaseAdmission')
$provisioningIndex = $runnerText.IndexOf('Invoke-RehearsalDatabaseProvisioning `', $admissionIndex)
$fixtureIndex = $runnerText.IndexOf("-Role 'Fixture'", $provisioningIndex)
$migrationIndex = $runnerText.IndexOf("-Role 'Migrations'", $fixtureIndex)
$edgeIndex = $runnerText.IndexOf("-Role 'Edge'", $migrationIndex)
$apiIndex = $runnerText.IndexOf("-Role 'Api'", $edgeIndex)
$dashboardIndex = $runnerText.IndexOf("-Role 'Dashboard'", $apiIndex)
if ($admissionIndex -lt 0 -or $provisioningIndex -lt 0 -or $fixtureIndex -lt 0 -or
    -not ($admissionIndex -lt $provisioningIndex -and $provisioningIndex -lt $fixtureIndex -and
          $fixtureIndex -lt $migrationIndex -and $migrationIndex -lt $edgeIndex -and
          $edgeIndex -lt $apiIndex -and $apiIndex -lt $dashboardIndex)) {
    throw 'Rehearsal launch suppression ordering is not structurally preserved.'
}
Write-Host 'PASS: configuration/provisioning precede every rehearsal process launch.'

$finallyIndex = $runnerText.IndexOf('finally {')
$postVerificationPhaseIndex = $runnerText.IndexOf("`$currentPhase = 'CandidatePostVerification'", $finallyIndex)
$evidencePhaseIndex = $runnerText.IndexOf("`$currentPhase = 'EvidenceFinalization'", $postVerificationPhaseIndex)
if ($finallyIndex -lt 0 -or $postVerificationPhaseIndex -lt $finallyIndex -or $evidencePhaseIndex -lt $postVerificationPhaseIndex) {
    throw 'Terminal post-verification/evidence-finalization ordering is not preserved.'
}
Write-Host 'PASS: terminal post-verification precedes evidence finalization.'

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("FactoryConnect-RehearsalEvidence-" + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try {
    $evidence = [ordered]@{
        schemaVersion = '1.0'
        candidateId = 'demo-candidate-20260923-02'
        attemptId = 'rehearsal-20260923-01'
        candidateManifestSha256 = '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76'
        applicationSourceCommit = '6bdb89d87d013c177297a7e2d029fed63a2e2b60'
        deploymentContractCommit = '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc'
        rehearsalToolSourceCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        startedAtUtc = '2026-09-23T09:00:00.0000000+00:00'
        completedAtUtc = '2026-09-23T09:05:00.0000000+00:00'
        outcome = 'Passed'
        failureClassification = $null
        failureMessage = $null
        rehearsalConfigurationSha256 = $firstHash
        database = [ordered]@{ serverIdentity = 'sql-rehearsal.local'; databaseName = 'FactoryConnect_Rehearsal_20260923_02'; migrationExitCode = 0; repositoryCurrent = $true }
        processes = @()
        checks = @()
        candidateVerification = [ordered]@{ beforeManifestSha256 = '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76'; afterManifestSha256 = '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76'; unchanged = $true }
        sevenSourceProof = [ordered]@{ configured = 7; observed = 7 }
        restartProof = [ordered]@{ performed = $true; passed = $true }
    }
    $text = (($evidence | ConvertTo-Json -Depth 30) -replace "`r`n", "`n") + "`n"
    $published = Publish-RehearsalTerminalEvidence -EvidenceRoot $tempRoot -EvidenceText $text
    if (-not (Test-Path -LiteralPath $published.EvidencePath)) { throw 'Canonical rehearsal evidence was not promoted.' }
    if (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'rehearsal.json.sha256'))) { throw 'Detached evidence checksum was not promoted.' }
    if (Test-Path -LiteralPath (Join-Path $tempRoot '.rehearsal.json.tmp')) { throw 'Temporary evidence JSON remained after promotion.' }
    if (Test-Path -LiteralPath (Join-Path $tempRoot '.rehearsal.json.sha256.tmp')) { throw 'Temporary evidence checksum remained after promotion.' }
    Write-Host 'PASS: terminal evidence staged and promoted as a complete pair.'

    $originalSha = Get-DemoCandidateSha256 -Path $published.EvidencePath
    Assert-Throws -Name 'terminal evidence overwrite prohibited' -Action {
        Publish-RehearsalTerminalEvidence -EvidenceRoot $tempRoot -EvidenceText $text | Out-Null
    }
    Assert-Equal -Actual (Get-DemoCandidateSha256 -Path $published.EvidencePath) -Expected $originalSha -Name 'existing terminal evidence preserved after reuse rejection'

    & (Join-Path $repoRoot 'scripts/release/Test-DemoCandidateRehearsalEvidence.ps1') `
        -EvidenceAttemptPath $tempRoot `
        -ExpectedCandidateId 'demo-candidate-20260923-02' `
        -ExpectedCandidateManifestSha256 '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76' `
        -ExpectedApplicationSourceCommit '6bdb89d87d013c177297a7e2d029fed63a2e2b60' `
        -ExpectedDeploymentContractCommit '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc' `
        -ExpectedRehearsalToolSourceCommit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' | Out-Null
    Write-Host 'PASS: untouched rehearsal evidence verified.'

    [System.IO.File]::AppendAllText($published.EvidencePath, " ")
    Assert-Throws -Name 'evidence bytes changed' -Action {
        & (Join-Path $repoRoot 'scripts/release/Test-DemoCandidateRehearsalEvidence.ps1') `
            -EvidenceAttemptPath $tempRoot `
            -ExpectedCandidateId 'demo-candidate-20260923-02' `
            -ExpectedCandidateManifestSha256 '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76' `
            -ExpectedApplicationSourceCommit '6bdb89d87d013c177297a7e2d029fed63a2e2b60' `
            -ExpectedDeploymentContractCommit '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc' `
            -ExpectedRehearsalToolSourceCommit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' | Out-Null
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Demo Candidate rehearsal focused matrix: PASS'
