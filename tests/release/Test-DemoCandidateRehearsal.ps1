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
    $jsonPath = Join-Path $tempRoot 'rehearsal.json'
    Write-DemoCandidateUtf8NoBom -Path $jsonPath -Text $text
    $sha = Get-DemoCandidateSha256 -Path $jsonPath
    Write-DemoCandidateUtf8NoBom -Path (Join-Path $tempRoot 'rehearsal.json.sha256') -Text "$sha  rehearsal.json`n"

    & (Join-Path $repoRoot 'scripts/release/Test-DemoCandidateRehearsalEvidence.ps1') `
        -EvidenceAttemptPath $tempRoot `
        -ExpectedCandidateId 'demo-candidate-20260923-02' `
        -ExpectedCandidateManifestSha256 '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76' `
        -ExpectedApplicationSourceCommit '6bdb89d87d013c177297a7e2d029fed63a2e2b60' `
        -ExpectedDeploymentContractCommit '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc' `
        -ExpectedRehearsalToolSourceCommit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' | Out-Null
    Write-Host 'PASS: untouched rehearsal evidence verified.'

    [System.IO.File]::AppendAllText($jsonPath, " ")
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
