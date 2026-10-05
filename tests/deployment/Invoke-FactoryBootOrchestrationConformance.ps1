[CmdletBinding()]
param([switch]$ContractOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$systemScript = Join-Path $repo 'scripts/deployment/Start-FactoryConnectSystem.ps1'
$installer = Join-Path $repo 'scripts/deployment/Install-FactoryConnectSystemStartup.ps1'
$builder = Join-Path $repo 'scripts/release/Build-FactoryConnectRelease.ps1'
foreach ($file in @($systemScript,$installer,$builder)) {
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw "$file failed parsing: $errors" }
}
$text = Get-Content $systemScript -Raw
foreach ($forbidden in @('Stop-Process','FactoryConnect.Migrations.exe','Expand-Archive','Register-ScheduledTask','New-Item -ItemType Junction','Start-FactoryConnectRuntime.ps1'' -SourceCommit')) {
    if ($text.Contains($forbidden)) { throw "Orchestration has forbidden responsibility: $forbidden" }
}
$build = Get-Content $builder -Raw
foreach ($name in @('Start-FactoryConnectSystem.ps1','Install-FactoryConnectSystemStartup.ps1','systemStartupScript','systemStartupInstaller')) {
    if (-not $build.Contains($name)) { throw "Packaging contract missing $name" }
}
if ($ContractOnly) { [pscustomobject]@{Contract='PASS';Executable='NOT EXECUTED'}; return }
if ($env:OS -ne 'Windows_NT') { throw 'Executable boot orchestration proofs require Windows PowerShell 5.1.' }
$roots = New-Object 'Collections.Generic.List[string]'
$results = [ordered]@{}
$id = '1111111111111111111111111111111111111111'
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function New-Fixture([int]$AcquisitionExit=0,[int]$RuntimeExit=0) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('FactoryConnect-Boot-' + [Guid]::NewGuid().ToString('N'))
    $roots.Add($root)
    foreach ($part in @('deployment',"releases/$id",'config')) { New-Item -ItemType Directory -Force (Join-Path $root $part) | Out-Null }
    $release = Join-Path $root "releases/$id"
    Copy-Item $systemScript (Join-Path $release 'Start-FactoryConnectSystem.ps1')
    New-Item -ItemType Junction -Path (Join-Path $root 'current') -Target $release | Out-Null
    @"
@echo off
>>"%~dp0trace.txt" echo Acquisition
exit /b $AcquisitionExit
"@ | Set-Content (Join-Path $root 'Start-Acquisition.bat') -Encoding ASCII
    @"
param([string]`$InstallRoot)
Add-Content (Join-Path `$InstallRoot 'trace.txt') 'Runtime'
[pscustomobject]@{Status='AlreadyRunning'}
exit $RuntimeExit
"@ | Set-Content (Join-Path $release 'Start-FactoryConnectRuntime.ps1')
    '{"ownership":"preserve"}' | Set-Content (Join-Path $root 'deployment/runtime.json')
    '{"commissioned":"preserve"}' | Set-Content (Join-Path $root 'config/sentinel.json')
    @{sourceCommit=$id;systemStartupScript='Start-FactoryConnectSystem.ps1'} | ConvertTo-Json | Set-Content (Join-Path $release 'release.json')
    $hash=(Get-FileHash (Join-Path $release 'Start-FactoryConnectSystem.ps1')).Hash.ToLowerInvariant()
    "$hash  Start-FactoryConnectSystem.ps1" | Set-Content (Join-Path $release 'MANIFEST.sha256')
    return $root
}
function Invoke-Orchestrator([string]$Root) {
    $shell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $p = New-Object Diagnostics.Process
    $p.StartInfo = New-Object Diagnostics.ProcessStartInfo
    $p.StartInfo.FileName = $shell
    $p.StartInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}"' -f $systemScript,$Root
    $p.StartInfo.UseShellExecute=$false; $p.StartInfo.CreateNoWindow=$true
    $p.StartInfo.RedirectStandardError=$true; $p.StartInfo.RedirectStandardOutput=$true
    [void]$p.Start()
    $out=$p.StandardOutput.ReadToEndAsync(); $err=$p.StandardError.ReadToEndAsync()
    try {
        if (-not $p.WaitForExit(20000)) { $p.Kill(); throw 'Orchestration exceeded fixture deadline.' }
        [void]$out.GetAwaiter().GetResult(); [void]$err.GetAwaiter().GetResult()
        return [int]$p.ExitCode
    } finally { $p.Dispose() }
}
function Case([string]$Name,[scriptblock]$Body) { & $Body; $results[$Name]='PASS'; Write-Host "$Name PASS" }
try {
    Case MissingAcquisition {
        $r=New-Fixture
        Remove-Item (Join-Path $r 'Start-Acquisition.bat')
        Assert-True ((Invoke-Orchestrator $r) -ne 0) 'Missing acquisition accepted.'
        Assert-True (-not (Test-Path (Join-Path $r 'trace.txt'))) 'Runtime ran without acquisition.'
    }
    Case OrchestrationLockContention {
        $r=New-Fixture
        $lock=[IO.File]::Open((Join-Path $r 'deployment/system-start.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try {
            Assert-True ((Invoke-Orchestrator $r) -ne 0) 'Orchestrator bypassed its lock.'
            Assert-True (-not (Test-Path (Join-Path $r 'trace.txt'))) 'Lock loser invoked acquisition/runtime.'
        } finally { $lock.Dispose() }
    }
    Case AcquisitionFailure {
        $r=New-Fixture 7
        Assert-True ((Invoke-Orchestrator $r) -eq 7) 'Acquisition failure code not propagated.'
        Assert-True ((Get-Content (Join-Path $r 'trace.txt') -Raw).Trim() -eq 'Acquisition') 'Runtime ran after acquisition failure.'
    }
    Case MissingSelection {
        $r=New-Fixture
        [IO.Directory]::Delete((Join-Path $r 'current'))
        Assert-True ((Invoke-Orchestrator $r) -ne 0) 'Missing current accepted.'
        Assert-True ((Get-Content (Join-Path $r 'trace.txt') -Raw).Trim() -eq 'Acquisition') 'Runtime ran without selection.'
    }
    Case InvalidSelection {
        $r=New-Fixture
        [IO.Directory]::Delete((Join-Path $r 'current'))
        New-Item -ItemType Junction -Path (Join-Path $r 'current') -Target (Join-Path $r 'config') | Out-Null
        Assert-True ((Invoke-Orchestrator $r) -ne 0) 'Outside current accepted.'
    }
    Case MissingRuntime {
        $r=New-Fixture
        Remove-Item (Join-Path $r "releases/$id/Start-FactoryConnectRuntime.ps1")
        Assert-True ((Invoke-Orchestrator $r) -ne 0) 'Missing runtime authority accepted.'
    }
    Case RuntimeFailure {
        $r=New-Fixture 0 9
        Assert-True ((Invoke-Orchestrator $r) -eq 9) 'Runtime failure code not propagated.'
        Assert-True ((@(Get-Content (Join-Path $r 'trace.txt')) -join ',') -eq 'Acquisition,Runtime') 'Startup ordering incorrect.'
    }
    Case VerifiedAlreadyRunning {
        $r=New-Fixture
        $runtime=(Get-FileHash (Join-Path $r 'deployment/runtime.json')).Hash
        $config=(Get-FileHash (Join-Path $r 'config/sentinel.json')).Hash
        $batch=(Get-FileHash (Join-Path $r 'Start-Acquisition.bat')).Hash
        Assert-True ((Invoke-Orchestrator $r) -eq 0) 'Verified chain failed.'
        Assert-True ((@(Get-Content (Join-Path $r 'trace.txt')) -join ',') -eq 'Acquisition,Runtime') 'Startup ordering incorrect.'
        Assert-True ((Get-FileHash (Join-Path $r 'deployment/runtime.json')).Hash -eq $runtime) 'Orchestration changed runtime evidence.'
        Assert-True ((Get-FileHash (Join-Path $r 'config/sentinel.json')).Hash -eq $config) 'Configuration changed.'
        Assert-True ((Get-FileHash (Join-Path $r 'Start-Acquisition.bat')).Hash -eq $batch) 'Acquisition authority changed.'
        $evidence=@(Get-ChildItem (Join-Path $r 'deployment/logs') -Recurse -Filter system-start.json)
        Assert-True ($evidence.Count -eq 1) 'Completion evidence missing.'
        Assert-True ((Get-Content $evidence[0].FullName -Raw | ConvertFrom-Json).phase -eq 'Completed') 'Completion not recorded.'
    }
    Case StableInstallation {
        $r=New-Fixture
        $first=& $installer -InstallRoot $r
        Assert-True ($first.Status -eq 'Installed') 'Stable installation failed.'
        Assert-True ((Get-FileHash (Join-Path $r 'Start-FactoryConnectSystem.ps1')).Hash -eq (Get-FileHash $systemScript).Hash) 'Root authority differs from packaged source.'
        Assert-True ((& $installer -InstallRoot $r).Status -eq 'AlreadyInstalled') 'Installation replay not idempotent.'
        Assert-True (-not (Test-Path (Join-Path $r 'trace.txt'))) 'Installer launched acquisition/runtime.'
    }
    Case StableReplacement {
        $r=New-Fixture
        Set-Content (Join-Path $r 'Start-FactoryConnectSystem.ps1') '# previous reviewed entry'
        Assert-True ((& $installer -InstallRoot $r).Status -eq 'Installed') 'Stable replacement failed.'
        Assert-True ((Get-FileHash (Join-Path $r 'Start-FactoryConnectSystem.ps1')).Hash -eq (Get-FileHash $systemScript).Hash) 'Replacement byte identity failed.'
        Assert-True (@(Get-ChildItem $r -Filter '*.tmp.*').Count -eq 0) 'Installer left temporary files.'
    }
    Case ManifestMismatch {
        $r=New-Fixture
        Add-Content (Join-Path $r "releases/$id/Start-FactoryConnectSystem.ps1") '# corruption'
        $caught=$false; try { & $installer -InstallRoot $r | Out-Null } catch { $caught=$true }
        Assert-True $caught 'Corrupt source installed.'
        Assert-True (-not (Test-Path (Join-Path $r 'Start-FactoryConnectSystem.ps1'))) 'Failed verification installed root authority.'
    }
    Case IntentExclusion {
        $r=New-Fixture; $intent=Join-Path $r 'deployment/runtime-start.intent.json'
        Set-Content $intent 'unresolved'; $before=(Get-FileHash $intent).Hash
        $caught=$false; try { & $installer -InstallRoot $r | Out-Null } catch { $caught=$true }
        Assert-True $caught 'Installer crossed unresolved intent.'
        Assert-True ((Get-FileHash $intent).Hash -eq $before) 'Installer altered intent.'
    }
    Case InstallerLockContention {
        $r=New-Fixture
        $lock=[IO.File]::Open((Join-Path $r 'deployment/deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try {
            $caught=$false; try { & $installer -InstallRoot $r | Out-Null } catch { $caught=$true }
            Assert-True $caught 'Installer bypassed deployment lock.'
            Assert-True (-not (Test-Path (Join-Path $r 'Start-FactoryConnectSystem.ps1'))) 'Lock loser installed authority.'
        } finally { $lock.Dispose() }
    }
} finally {
    foreach ($r in $roots) {
        $current=Join-Path $r 'current'
        if (Test-Path -LiteralPath $current) { [IO.Directory]::Delete($current) }
        Remove-Item -LiteralPath $r -Recurse -Force
    }
}
[pscustomobject]$results
