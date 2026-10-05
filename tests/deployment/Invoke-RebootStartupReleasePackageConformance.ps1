[CmdletBinding()]
param(
    [Parameter()]
    [string]$ReleaseBuilder
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell can evaluate parameter default expressions before
# $PSScriptRoot is available. Resolve the repository-relative default only
# after parameter binding so the conformance harness works under the same
# powershell.exe host used by deployment proofs.
if ([string]::IsNullOrWhiteSpace($ReleaseBuilder)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
        throw 'Unable to resolve release-package conformance script root.'
    }

    $ReleaseBuilder = Join-Path $scriptRoot '..\..\scripts\release\Build-FactoryConnectRelease.ps1'
}

$ReleaseBuilder = [System.IO.Path]::GetFullPath($ReleaseBuilder)

if (-not (Test-Path -LiteralPath $ReleaseBuilder -PathType Leaf)) {
    throw "Release builder not found: $ReleaseBuilder"
}

$text = Get-Content -Raw -LiteralPath $ReleaseBuilder

$required = @(
    '[string]$RuntimeStartupScriptPath',
    "scripts/deployment/Start-FactoryConnectRuntime.ps1",
    "Test-Path -LiteralPath `$RuntimeStartupScriptPath -PathType Leaf",
    "Copy(`$RuntimeStartupScriptPath, (Join-Path `$stagingRoot 'Start-FactoryConnectRuntime.ps1'), `$false)",
    "runtimeStartupScript = 'Start-FactoryConnectRuntime.ps1'"
)

foreach ($token in $required) {
    if ($text.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing reboot-startup release-package contract token: $token"
    }
}

[pscustomobject]@{
    RBSReleasePackageContract = 'PASS'
    StartupAuthority = 'Start-FactoryConnectRuntime.ps1'
    ManifestCoverage = 'Inherited from complete staged payload enumeration'
}
