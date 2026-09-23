[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidatePath,

    [Parameter()]
    [string]$ExpectedCandidateId,

    [Parameter()]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceCommit,

    [Parameter()]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedDeploymentContractCommit
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "DemoCandidate.Common.ps1")

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', resolved '$Actual'."
    }
}

function Assert-CanonicalHexSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Value -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must be a lowercase 64-character SHA-256 value."
    }
}

$candidateRoot = (Resolve-Path -LiteralPath $CandidatePath).Path
if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
    throw "Candidate path '$CandidatePath' is not a directory."
}

$allowedTopLevelDirectories = @('api', 'config', 'dashboard', 'edge', 'migrations')
$allowedTopLevelFiles = @('release-manifest.json', 'release-manifest.sha256')
$topLevel = @(Get-ChildItem -LiteralPath $candidateRoot -Force)

foreach ($entry in $topLevel) {
    if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Candidate top-level reparse points are forbidden: '$($entry.Name)'."
    }

    if ($entry.PSIsContainer) {
        if ($allowedTopLevelDirectories -notcontains $entry.Name) {
            throw "Unexpected candidate top-level directory '$($entry.Name)'."
        }
    }
    elseif ($allowedTopLevelFiles -notcontains $entry.Name) {
        throw "Unexpected candidate top-level file '$($entry.Name)'."
    }
}

foreach ($requiredDirectory in $allowedTopLevelDirectories) {
    $path = Join-Path $candidateRoot $requiredDirectory
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required candidate directory is missing: '$requiredDirectory'."
    }
}

foreach ($requiredFile in $allowedTopLevelFiles) {
    $path = Join-Path $candidateRoot $requiredFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required candidate file is missing: '$requiredFile'."
    }
}

foreach ($entry in (Get-ChildItem -LiteralPath $candidateRoot -Force -Recurse)) {
    if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        $relative = ConvertTo-DemoCandidateRelativePath -Root $candidateRoot -Path $entry.FullName
        throw "Candidate reparse points are forbidden: '$relative'."
    }
}

$manifestPath = Join-Path $candidateRoot 'release-manifest.json'
$checksumPath = Join-Path $candidateRoot 'release-manifest.sha256'
$manifestHash = Get-DemoCandidateSha256 -Path $manifestPath
$checksumBytes = [System.IO.File]::ReadAllBytes($checksumPath)
$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$checksumText = $utf8Strict.GetString($checksumBytes)
$expectedChecksumText = "$manifestHash  release-manifest.json`n"
if ($checksumText -cne $expectedChecksumText) {
    throw "release-manifest.sha256 does not exactly match the canonical detached-checksum format or manifest bytes."
}

$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
if ($manifestBytes.Length -ge 3 -and
    $manifestBytes[0] -eq 0xEF -and
    $manifestBytes[1] -eq 0xBB -and
    $manifestBytes[2] -eq 0xBF) {
    throw "release-manifest.json must be UTF-8 without BOM."
}

$manifestText = $utf8Strict.GetString($manifestBytes)
try {
    $manifest = $manifestText | ConvertFrom-Json
}
catch {
    throw "release-manifest.json is not valid JSON: $($_.Exception.Message)"
}

$canonicalText = Get-DemoCandidateCanonicalManifestText -Manifest $manifest
if ($manifestText -cne $canonicalText) {
    throw "release-manifest.json is not in the canonical FactoryConnect manifest byte form."
}

Assert-Equal ([string]$manifest.schemaVersion) $script:DemoCandidateSchemaVersion 'Unexpected manifest schemaVersion.'
Assert-Equal ([string]$manifest.repositoryUrl) $script:DemoCandidateRepositoryUrl 'Unexpected repositoryUrl.'
Assert-Equal ([string]$manifest.publishProfile.configuration) $script:DemoCandidateConfiguration 'Unexpected publish configuration.'
Assert-Equal ([string]$manifest.publishProfile.targetFramework) $script:DemoCandidateTargetFramework 'Unexpected target framework.'
Assert-Equal ([string]$manifest.publishProfile.runtimeIdentifier) $script:DemoCandidateRuntimeIdentifier 'Unexpected runtime identifier.'
if (-not [bool]$manifest.publishProfile.selfContained) {
    throw "Demo Candidate publish profile must be self-contained."
}

$candidateId = [string]$manifest.candidateId
if ($candidateId -notmatch '^demo-candidate-(?<date>\d{8})-(?<sequence>\d{2})$') {
    throw "Manifest candidateId '$candidateId' is invalid."
}

$sequence = [int]$Matches['sequence']
if ($sequence -lt 1 -or $sequence -gt 99) {
    throw "Manifest candidate sequence must be between 01 and 99."
}

$parsedDate = [DateTime]::MinValue
if (-not [DateTime]::TryParseExact(
    $Matches['date'],
    'yyyyMMdd',
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::None,
    [ref]$parsedDate)) {
    throw "Manifest candidateId contains an invalid date."
}

$createdAt = [DateTime]::MinValue
if (-not [DateTime]::TryParseExact(
    [string]$manifest.createdAtUtc,
    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
    [System.Globalization.CultureInfo]::InvariantCulture,
    [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal,
    [ref]$createdAt)) {
    throw "Manifest createdAtUtc is not canonical UTC."
}

Assert-Equal $parsedDate.Date $createdAt.Date 'CandidateId date must equal manifest creation UTC date.'
Assert-Equal (Split-Path -Leaf $candidateRoot) $candidateId 'Candidate directory name must equal manifest candidateId.'

if (-not [string]::IsNullOrWhiteSpace($ExpectedCandidateId)) {
    Assert-Equal $candidateId $ExpectedCandidateId 'Candidate identity mismatch.'
}

$sourceCommit = ([string]$manifest.applicationSourceCommit).ToLowerInvariant()
$deploymentCommit = ([string]$manifest.deploymentContractCommit).ToLowerInvariant()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Manifest applicationSourceCommit must be a lowercase full commit SHA."
}
if ($deploymentCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Manifest deploymentContractCommit must be a lowercase full commit SHA."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
    Assert-Equal $sourceCommit $ExpectedSourceCommit.ToLowerInvariant() 'Application source commit mismatch.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedDeploymentContractCommit)) {
    Assert-Equal $deploymentCommit $ExpectedDeploymentContractCommit.ToLowerInvariant() 'Deployment contract commit mismatch.'
}

if ([string]$manifest.toolchain.dotNetSdkVersion -notmatch '^10\.0\.\d+$') {
    throw "Manifest .NET SDK version must be stable 10.0.x."
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.toolchain.nodeVersion)) {
    throw "Manifest Node version is required."
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.toolchain.npmVersion)) {
    throw "Manifest npm version is required."
}
Assert-CanonicalHexSha256 ([string]$manifest.toolchain.packageLockSha256) 'packageLockSha256'

$previousSubmodulePath = $null
foreach ($submodule in @($manifest.submodules)) {
    if ([string]$submodule.commit -notmatch '^[0-9a-f]{40}$') {
        throw "Manifest submodule commit must be a lowercase full commit SHA."
    }
    $submodulePath = [string]$submodule.path
    if ([string]::IsNullOrWhiteSpace($submodulePath) -or $submodulePath.Contains('\')) {
        throw "Manifest submodule path '$submodulePath' is not normalized."
    }
    if ($null -ne $previousSubmodulePath -and
        [System.StringComparer]::Ordinal.Compare($previousSubmodulePath, $submodulePath) -ge 0) {
        throw "Manifest submodules must be strictly ordinal-sorted with unique paths."
    }
    $previousSubmodulePath = $submodulePath
}

$forbidden = @(Get-ChildItem -LiteralPath $candidateRoot -File -Recurse | Where-Object {
    $_.Extension -ieq '.pdb' -or $_.Name -ieq 'appsettings.Development.json'
})
if ($forbidden.Count -ne 0) {
    $names = $forbidden | ForEach-Object { ConvertTo-DemoCandidateRelativePath -Root $candidateRoot -Path $_.FullName }
    throw "Forbidden candidate payload file(s): $($names -join ', ')."
}

$requiredTemplates = @(
    'api.production.template.json',
    'dashboard.production.template.json',
    'deployment-manifest.template.json',
    'edge.production.template.json'
)
$configFiles = @(Get-ChildItem -LiteralPath (Join-Path $candidateRoot 'config') -File -Recurse)
if ($configFiles.Count -ne $requiredTemplates.Count) {
    throw "Candidate config directory must contain exactly the four approved templates."
}
foreach ($template in $requiredTemplates) {
    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $candidateRoot 'config') $template) -PathType Leaf)) {
        throw "Approved candidate template is missing: '$template'."
    }
}

$entrypoints = @(
    @{ Directory = 'migrations'; Name = 'FactoryConnect.Migrations' },
    @{ Directory = 'edge'; Name = 'FactoryConnect.Edge' },
    @{ Directory = 'api'; Name = 'FactoryConnect.Api' },
    @{ Directory = 'dashboard'; Name = 'FactoryConnect.Dashboard' }
)
foreach ($entrypoint in $entrypoints) {
    $root = Join-Path $candidateRoot $entrypoint.Directory
    foreach ($extension in @('.exe', '.dll', '.deps.json', '.runtimeconfig.json')) {
        $path = Join-Path $root ($entrypoint.Name + $extension)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required published entrypoint file is missing: '$($entrypoint.Directory)/$($entrypoint.Name)$extension'."
        }
    }
}

$dashboardWebRoot = Join-Path (Join-Path $candidateRoot 'dashboard') 'wwwroot'
$dashboardIndex = Join-Path $dashboardWebRoot 'index.html'
if (-not (Test-Path -LiteralPath $dashboardIndex -PathType Leaf)) {
    throw "Dashboard published frontend entry 'dashboard/wwwroot/index.html' is missing."
}
$indexText = [System.IO.File]::ReadAllText($dashboardIndex)
$scriptMatch = [regex]::Match($indexText, '<script[^>]+src="(?<path>/assets/index-[^"]+\.js)"')
if (-not $scriptMatch.Success) {
    throw "Dashboard frontend entry does not reference the required hashed Vite JavaScript asset."
}
$assetRelative = $scriptMatch.Groups['path'].Value.TrimStart('/').Replace('/', [System.IO.Path]::DirectorySeparatorChar)
$assetPath = Join-Path $dashboardWebRoot $assetRelative
if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
    throw "Dashboard referenced Vite asset is missing: '$($scriptMatch.Groups['path'].Value)'."
}

$actualInventory = @(Get-DemoCandidatePayloadInventory -CandidateRoot $candidateRoot)
$manifestInventory = @($manifest.artifacts)
if ($actualInventory.Count -ne $manifestInventory.Count) {
    throw "Manifest payload count does not match candidate payload count."
}

for ($index = 0; $index -lt $actualInventory.Count; $index++) {
    $actual = $actualInventory[$index]
    $recorded = $manifestInventory[$index]
    Assert-Equal ([string]$recorded.path) ([string]$actual.path) "Payload path mismatch at manifest index $index."
    Assert-Equal ([long]$recorded.length) ([long]$actual.length) "Payload length mismatch for '$($actual.path)'."
    Assert-CanonicalHexSha256 ([string]$recorded.sha256) "Artifact SHA-256 for '$($actual.path)'"
    Assert-Equal ([string]$recorded.sha256) ([string]$actual.sha256) "Payload SHA-256 mismatch for '$($actual.path)'."
}

[pscustomobject]@{
    CandidateId = $candidateId
    CandidatePath = $candidateRoot
    ManifestSha256 = $manifestHash
    ApplicationSourceCommit = $sourceCommit
    DeploymentContractCommit = $deploymentCommit
    PayloadFiles = $actualInventory.Count
}
