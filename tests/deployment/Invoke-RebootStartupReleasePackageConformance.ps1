[CmdletBinding()]
param(
    [Parameter()]
    [string]$ReleaseBuilder,
    [Parameter()][string]$ReleaseDirectory
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
    "runtimeStartupScript = 'Start-FactoryConnectRuntime.ps1'",
    "FactoryConnect.ProcessTermination.ps1",
    "FactoryConnect.SqlReadiness.ps1",
    "sqlReadinessScript = 'FactoryConnect.SqlReadiness.ps1'",
    "Copy(`$sqlReadinessScriptPath, (Join-Path `$stagingRoot 'FactoryConnect.SqlReadiness.ps1'), `$false)",
    "processTerminationScript = 'FactoryConnect.ProcessTermination.ps1'"
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

if (-not [string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $metadata = Get-Content -Raw (Join-Path $ReleaseDirectory 'release.json') | ConvertFrom-Json
    if ($metadata.sqlReadinessScript -cne 'FactoryConnect.SqlReadiness.ps1') { throw 'Packaged SQL readiness metadata differs.' }
    $name = 'FactoryConnect.SqlReadiness.ps1'
    $source = Join-Path (Split-Path (Split-Path $ReleaseBuilder -Parent) -Parent) "deployment/$name"
    $packaged = Join-Path $ReleaseDirectory $name
    $hash = (Get-FileHash -LiteralPath $packaged -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Packaged SQL readiness bytes differ from source.' }
    $line = "$hash  $name"
    if (@(Get-Content (Join-Path $ReleaseDirectory 'MANIFEST.sha256') | Where-Object { $_ -ceq $line }).Count -ne 1) { throw 'SQL readiness is not exactly manifest-covered.' }
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($packaged,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'Packaged SQL readiness failed parsing.' }
    [pscustomobject]@{SqlReadinessPayload='PASS';Sha256=$hash;SourceCommit=$metadata.sourceCommit}
}
