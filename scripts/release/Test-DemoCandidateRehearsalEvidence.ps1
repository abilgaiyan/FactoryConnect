[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceAttemptPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^demo-candidate-\d{8}-\d{2}$')][string]$ExpectedCandidateId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedCandidateManifestSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedApplicationSourceCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedDeploymentContractCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedRehearsalToolSourceCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoCandidate.Common.ps1')

$root = (Resolve-Path -LiteralPath $EvidenceAttemptPath).Path
$evidencePath = Join-Path $root 'rehearsal.json'
$checksumPath = Join-Path $root 'rehearsal.json.sha256'
foreach ($path in @($evidencePath, $checksumPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required rehearsal evidence file is missing: '$path'."
    }
}

$entries = @(Get-ChildItem -LiteralPath $root -Force)
if ($entries.Count -ne 2 -or @($entries.Name | Sort-Object) -join '|' -ne 'rehearsal.json|rehearsal.json.sha256') {
    throw 'Rehearsal evidence attempt must contain exactly rehearsal.json and rehearsal.json.sha256.'
}

$evidenceSha256 = Get-DemoCandidateSha256 -Path $evidencePath
$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$checksumBytes = [System.IO.File]::ReadAllBytes($checksumPath)
$checksumText = $utf8Strict.GetString($checksumBytes)
if ($checksumText -cne "$evidenceSha256  rehearsal.json`n") {
    throw 'rehearsal.json.sha256 does not exactly match the evidence bytes.'
}

$bytes = [System.IO.File]::ReadAllBytes($evidencePath)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    throw 'rehearsal.json must be UTF-8 without BOM.'
}
$text = $utf8Strict.GetString($bytes)
if (-not $text.EndsWith("`n", [StringComparison]::Ordinal) -or $text.EndsWith("`n`n", [StringComparison]::Ordinal)) {
    throw 'rehearsal.json must have exactly one terminal LF.'
}

try { $evidence = $text | ConvertFrom-Json } catch { throw "rehearsal.json is invalid JSON: $($_.Exception.Message)" }

$expectedProperties = @(
    'schemaVersion','candidateId','attemptId','candidateManifestSha256',
    'applicationSourceCommit','deploymentContractCommit','rehearsalToolSourceCommit',
    'startedAtUtc','completedAtUtc','outcome','failureClassification','failureMessage',
    'rehearsalConfigurationSha256','database','processes','checks',
    'candidateVerification','sevenSourceProof','restartProof')
$actualProperties = @($evidence.PSObject.Properties.Name)
if ($actualProperties.Count -ne $expectedProperties.Count) { throw 'Unexpected rehearsal evidence property count.' }
for ($index = 0; $index -lt $expectedProperties.Count; $index++) {
    if ($actualProperties[$index] -cne $expectedProperties[$index]) {
        throw "Unexpected rehearsal evidence property/order '$($actualProperties[$index])'."
    }
}

if ([string]$evidence.schemaVersion -cne '1.0') { throw 'Unsupported rehearsal evidence schemaVersion.' }
if ([string]$evidence.candidateId -cne $ExpectedCandidateId) { throw 'Rehearsal candidateId mismatch.' }
if ([string]$evidence.candidateManifestSha256 -cne $ExpectedCandidateManifestSha256) { throw 'Candidate manifest SHA-256 mismatch.' }
if ([string]$evidence.applicationSourceCommit -cne $ExpectedApplicationSourceCommit.ToLowerInvariant()) { throw 'Application source commit mismatch.' }
if ([string]$evidence.deploymentContractCommit -cne $ExpectedDeploymentContractCommit.ToLowerInvariant()) { throw 'Deployment contract commit mismatch.' }
if ([string]$evidence.rehearsalToolSourceCommit -cne $ExpectedRehearsalToolSourceCommit.ToLowerInvariant()) { throw 'Rehearsal tool source commit mismatch.' }
if ([string]$evidence.rehearsalConfigurationSha256 -notmatch '^[0-9a-f]{64}$') { throw 'Invalid rehearsal configuration SHA-256.' }
if ([string]$evidence.attemptId -notmatch '^rehearsal-\d{8}-\d{2}$') { throw 'Invalid rehearsal attemptId.' }
if ([string]$evidence.outcome -notin @('Passed','Failed','Canceled')) { throw 'Invalid rehearsal outcome.' }
if ($null -ne $evidence.failureClassification -and [string]$evidence.failureClassification -notin @('Configuration','Infrastructure','Candidate','Application','Dependency','Verification')) {
    throw 'Invalid rehearsal failureClassification.'
}
if ([string]$evidence.failureMessage -match '(?i)(Password|Pwd|User ID|UserID|Uid|Token|ApiKey|Secret)\s*=') {
    throw 'Potential protected configuration leaked into failureMessage.'
}

if ($evidence.outcome -eq 'Passed') {
    if ($evidence.failureClassification -ne $null -or $evidence.failureMessage -ne $null) { throw 'Passed evidence cannot carry failure fields.' }
    if (-not [bool]$evidence.database.repositoryCurrent) { throw 'Passed rehearsal must prove repository-current migration.' }
    if (-not [bool]$evidence.candidateVerification.unchanged) { throw 'Passed rehearsal must prove unchanged candidate.' }
    if ([int]$evidence.sevenSourceProof.configured -ne 7 -or [int]$evidence.sevenSourceProof.observed -ne 7) { throw 'Passed rehearsal must prove seven configured and observed sources.' }
    if (-not [bool]$evidence.restartProof.performed -or -not [bool]$evidence.restartProof.passed) { throw 'Passed rehearsal must prove same-candidate restart.' }
}

[pscustomobject]@{
    CandidateId = [string]$evidence.candidateId
    AttemptId = [string]$evidence.attemptId
    Outcome = [string]$evidence.outcome
    EvidenceSha256 = $evidenceSha256
    RehearsalConfigurationSha256 = [string]$evidence.rehearsalConfigurationSha256
    RehearsalToolSourceCommit = [string]$evidence.rehearsalToolSourceCommit
}
