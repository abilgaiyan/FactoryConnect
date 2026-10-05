[CmdletBinding()]
param(
    [Parameter()]
    [string]$ReleaseBuilder = (Join-Path $PSScriptRoot '..\..\scripts\release\Build-FactoryConnectRelease.ps1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
