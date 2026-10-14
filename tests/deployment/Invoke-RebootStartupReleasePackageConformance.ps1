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
    "processTerminationScript = 'FactoryConnect.ProcessTermination.ps1'",
    "scripts/deployment/Restart-FactoryConnectEdge.ps1",
    "scripts/deployment/Get-FactoryConnectEdgeRestartAssessment.ps1",
    "edgeRestartScript = 'Restart-FactoryConnectEdge.ps1'",
    "edgeRestartAssessmentScript = 'Get-FactoryConnectEdgeRestartAssessment.ps1'",
    "Copy(`$edgeRestartScriptPath, (Join-Path `$stagingRoot 'Restart-FactoryConnectEdge.ps1'), `$false)",
    "Copy(`$edgeRestartAssessmentScriptPath, (Join-Path `$stagingRoot 'Get-FactoryConnectEdgeRestartAssessment.ps1'), `$false)"
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

if (-not [string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $providerRoot = Join-Path $ReleaseDirectory 'apps/sql-readiness'
    foreach ($name in @('FactoryConnect.SqlReadiness.exe','FactoryConnect.SqlReadiness.deps.json','FactoryConnect.SqlReadiness.runtimeconfig.json','Microsoft.Data.SqlClient.dll','Microsoft.Data.SqlClient.SNI.dll')) {
        if (-not (Test-Path (Join-Path $providerRoot $name))) { throw "Required SQL provider payload missing: $name" }
    }
    $versions = [xml](Get-Content (Join-Path (Split-Path (Split-Path (Split-Path $ReleaseBuilder -Parent) -Parent) -Parent) 'Directory.Packages.props') -Raw)
    $version = @($versions.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.Data.SqlClient' })[0].Version
    $dependencies = Get-Content (Join-Path $providerRoot 'FactoryConnect.SqlReadiness.deps.json') -Raw
    if (-not $dependencies.Contains("Microsoft.Data.SqlClient/$version")) { throw 'Readiness SQL provider differs from central runtime version.' }
    foreach ($file in Get-ChildItem $providerRoot -File -Recurse) {
        $relative = $file.FullName.Substring($ReleaseDirectory.TrimEnd('\').Length + 1).Replace('\','/')
        $line = (Get-FileHash $file.FullName).Hash.ToLowerInvariant() + '  ' + $relative
        if (@(Get-Content (Join-Path $ReleaseDirectory 'MANIFEST.sha256') | Where-Object { $_ -ceq $line }).Count -ne 1) { throw 'Readiness dependency lacks exact manifest coverage.' }
    }
    [pscustomobject]@{SqlReadinessProviderPayload='PASS';Provider='Microsoft.Data.SqlClient';Version=$version}
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $metadata = Get-Content -Raw (Join-Path $ReleaseDirectory 'release.json') | ConvertFrom-Json
    $deploymentRoot = Join-Path (Split-Path (Split-Path $ReleaseBuilder -Parent) -Parent) 'deployment'
    foreach ($entry in @(
        @{property='edgeRestartScript';name='Restart-FactoryConnectEdge.ps1'},
        @{property='edgeRestartAssessmentScript';name='Get-FactoryConnectEdgeRestartAssessment.ps1'}
    )) {
        $name=$entry.name
        if ($metadata.($entry.property) -cne $name) { throw "Packaged $name metadata differs." }
        $packaged=Join-Path $ReleaseDirectory $name
        $hash=(Get-FileHash -LiteralPath $packaged -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne (Get-FileHash -LiteralPath (Join-Path $deploymentRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Packaged $name bytes differ from source." }
        if (@(Get-Content (Join-Path $ReleaseDirectory 'MANIFEST.sha256') | Where-Object { $_ -ceq "$hash  $name" }).Count -ne 1) { throw "$name is not exactly manifest-covered." }
        $tokens=$null; $errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile($packaged,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { throw "Packaged $name failed parsing." }
        [pscustomobject]@{EdgeRestartPayload='PASS';Name=$name;Sha256=$hash;SourceCommit=$metadata.sourceCommit}
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $repoRoot = Split-Path (Split-Path (Split-Path $ReleaseBuilder -Parent) -Parent) -Parent
    $versions = [xml](Get-Content (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
    $pkcsVersion = @($versions.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'System.Security.Cryptography.Pkcs' })[0].Version
    $sqlApplications = [ordered]@{'sql-readiness'='SqlReadiness';migrations='Migrations';edge='Edge';api='Api'}
    foreach ($application in $sqlApplications.Keys) {
        $appRoot = Join-Path $ReleaseDirectory "apps/$application"
        $deps = Join-Path $appRoot "FactoryConnect.$($sqlApplications[$application]).deps.json"
        $graph = Get-Content -Raw -LiteralPath $deps | ConvertFrom-Json
        $pkcs = @($graph.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith('System.Security.Cryptography.Pkcs/', [StringComparison]::Ordinal) })
        if ($application -ne 'api') {
            if ($pkcs.Count -ne 1 -or $pkcs[0].Name -cne "System.Security.Cryptography.Pkcs/$pkcsVersion") {
                throw "$application does not resolve the central patched Pkcs version."
            }
        }
        else {
            # Self-contained Web SDK publish can select the ASP.NET Core runtime-pack
            # assembly over the NuGet asset. Prove exact ownership, not DLL presence.
            if ($pkcs.Count -ne 0) { throw 'API has ambiguous NuGet and runtime-pack Pkcs ownership.' }
            $owners = @(
                foreach ($target in $graph.targets.PSObject.Properties) {
                    foreach ($library in $target.Value.PSObject.Properties) {
                        $runtime = $library.Value.runtime
                        if ($null -ne $runtime -and
                            $null -ne $runtime.PSObject.Properties['System.Security.Cryptography.Pkcs.dll']) {
                            [pscustomobject]@{ Target=$target.Name; Library=$library.Name }
                        }
                    }
                }
            )
            if ($owners.Count -ne 1 -or
                $owners[0].Target -cne '.NETCoreApp,Version=v10.0/win-x64' -or
                $owners[0].Library -cne 'runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.7' -or
                $null -eq $graph.libraries.PSObject.Properties[$owners[0].Library]) {
                throw 'API Pkcs runtime-pack ownership is absent, ambiguous or unrecognized.'
            }
            $runtimeVersion = $owners[0].Library.Split('/')[-1]
            $dllPath = Join-Path $appRoot 'System.Security.Cryptography.Pkcs.dll'
            if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw 'API runtime-pack Pkcs DLL is missing.' }
            $productVersion = (Get-Item -LiteralPath $dllPath).VersionInfo.ProductVersion
            $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
            if (-not $productVersion.StartsWith("$runtimeVersion+", [StringComparison]::Ordinal) -or
                $assemblyVersion -cne '10.0.0.0') {
                throw 'API Pkcs assembly identity does not match its runtime-pack authority.'
            }
        }
        $dll = Join-Path $appRoot 'System.Security.Cryptography.Pkcs.dll'
        $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        $line = "$hash  apps/$application/System.Security.Cryptography.Pkcs.dll"
        if (@(Get-Content (Join-Path $ReleaseDirectory 'MANIFEST.sha256') | Where-Object { $_ -ceq $line }).Count -ne 1) { throw "$application Pkcs assembly lacks exact manifest coverage." }
    }
    [pscustomobject]@{PkcsPayload='PASS';Version=$pkcsVersion;Applications=$sqlApplications.Count}
}
