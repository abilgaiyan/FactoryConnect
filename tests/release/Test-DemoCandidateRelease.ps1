[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidatePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$verifier = Join-Path $repoRoot "scripts/release/Test-DemoCandidate.ps1"
$common = Join-Path $repoRoot "scripts/release/DemoCandidate.Common.ps1"
. $common

function Copy-TestCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in (Get-ChildItem -LiteralPath $Source -Directory -Recurse)) {
        $relative = ConvertTo-DemoCandidateRelativePath -Root $Source -Path $directory.FullName
        [System.IO.Directory]::CreateDirectory(
            (Join-Path $Destination ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))) | Out-Null
    }
    foreach ($file in (Get-ChildItem -LiteralPath $Source -File -Recurse)) {
        $relative = ConvertTo-DemoCandidateRelativePath -Root $Source -Path $file.FullName
        $target = Join-Path $Destination ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [System.IO.File]::Copy($file.FullName, $target, $false)
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        Write-Host "PASS (rejected): $Name"
        return
    }

    throw "Negative Demo Candidate proof unexpectedly passed: $Name"
}

function Rewrite-DetachedChecksum {
    param([Parameter(Mandatory = $true)][string]$Root)

    $manifestPath = Join-Path $Root 'release-manifest.json'
    $checksumPath = Join-Path $Root 'release-manifest.sha256'
    $hash = Get-DemoCandidateSha256 -Path $manifestPath
    Write-DemoCandidateUtf8NoBom -Path $checksumPath -Text "$hash  release-manifest.json`n"
}

$sourceCandidate = (Resolve-Path -LiteralPath $CandidatePath).Path
$positive = & $verifier -CandidatePath $sourceCandidate
Write-Host "PASS: untouched candidate verified ($($positive.PayloadFiles) payload files)."

$testRoot = Join-Path $repoRoot "artifacts/release/test-work/$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

try {
    $candidateId = $positive.CandidateId

    $payloadCase = Join-Path (Join-Path $testRoot 'payload-byte-change') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $payloadCase
    $apiDll = Join-Path $payloadCase 'api/FactoryConnect.Api.dll'
    [System.IO.File]::AppendAllText($apiDll, 'tamper')
    Assert-Rejected 'payload byte changed' { & $verifier -CandidatePath $payloadCase | Out-Null }

    $addedCase = Join-Path (Join-Path $testRoot 'unexpected-payload') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $addedCase
    [System.IO.File]::WriteAllText((Join-Path $addedCase 'api/unexpected.txt'), 'unexpected')
    Assert-Rejected 'unexpected payload added' { & $verifier -CandidatePath $addedCase | Out-Null }

    $pdbCase = Join-Path (Join-Path $testRoot 'pdb') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $pdbCase
    [System.IO.File]::WriteAllText((Join-Path $pdbCase 'api/forbidden.pdb'), 'pdb')
    Assert-Rejected 'PDB injected' { & $verifier -CandidatePath $pdbCase | Out-Null }

    $developmentCase = Join-Path (Join-Path $testRoot 'development-config') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $developmentCase
    [System.IO.File]::WriteAllText((Join-Path $developmentCase 'api/appsettings.Development.json'), '{}')
    Assert-Rejected 'appsettings.Development.json injected' { & $verifier -CandidatePath $developmentCase | Out-Null }

    $templateCase = Join-Path (Join-Path $testRoot 'missing-template') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $templateCase
    Remove-Item -LiteralPath (Join-Path $templateCase 'config/api.production.template.json') -Force
    Assert-Rejected 'approved template missing' { & $verifier -CandidatePath $templateCase | Out-Null }

    $dashboardCase = Join-Path (Join-Path $testRoot 'dashboard-index') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $dashboardCase
    Remove-Item -LiteralPath (Join-Path $dashboardCase 'dashboard/wwwroot/index.html') -Force
    Assert-Rejected 'Dashboard index missing' { & $verifier -CandidatePath $dashboardCase | Out-Null }

    $checksumCase = Join-Path (Join-Path $testRoot 'detached-checksum') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $checksumCase
    [System.IO.File]::WriteAllText((Join-Path $checksumCase 'release-manifest.sha256'), 'invalid')
    Assert-Rejected 'detached manifest checksum changed' { & $verifier -CandidatePath $checksumCase | Out-Null }

    $unknownCase = Join-Path (Join-Path $testRoot 'unknown-property') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $unknownCase
    $unknownManifestPath = Join-Path $unknownCase 'release-manifest.json'
    $unknownText = [System.IO.File]::ReadAllText($unknownManifestPath)
    $unknownText = $unknownText.Replace("{`n", "{`n  `"unexpected`": true,`n")
    Write-DemoCandidateUtf8NoBom -Path $unknownManifestPath -Text $unknownText
    Rewrite-DetachedChecksum -Root $unknownCase
    Assert-Rejected 'unknown manifest property' { & $verifier -CandidatePath $unknownCase | Out-Null }

    $duplicateCase = Join-Path (Join-Path $testRoot 'duplicate-property') $candidateId
    Copy-TestCandidate -Source $sourceCandidate -Destination $duplicateCase
    $duplicateManifestPath = Join-Path $duplicateCase 'release-manifest.json'
    $duplicateText = [System.IO.File]::ReadAllText($duplicateManifestPath)
    $duplicateText = $duplicateText.Replace(
        "{`n",
        "{`n  `"schemaVersion`": `"1.0`",`n")
    Write-DemoCandidateUtf8NoBom -Path $duplicateManifestPath -Text $duplicateText
    Rewrite-DetachedChecksum -Root $duplicateCase
    Assert-Rejected 'duplicate manifest property' { & $verifier -CandidatePath $duplicateCase | Out-Null }

    Write-Host 'Demo Candidate focused integrity matrix: PASS'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Force -Recurse
    }
}
