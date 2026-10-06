$ErrorActionPreference='Stop'
$release='348e6167f9a8b7e8784732ca8cc8a14559837d08'
$hash='A'*64
. (Join-Path $PSScriptRoot '../../scripts/deployment/Test-FactoryConnectRecoveryDeployment.ps1') -ApprovedRelease $release -ApprovedManifestSha256 $hash -FunctionsOnly
function New-Capture {
    param([string]$Sha=$release)
    $root=if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'C:\FactoryConnect-Test' } else { '/tmp/FactoryConnect-Test' }
    $path=Join-Path (Join-Path $root 'releases') $Sha
    $records=@{}; $processes=@{}
    foreach ($name in @('edge','api')) {
        $exe=if($name-eq'edge'){'FactoryConnect.Edge.exe'}else{'FactoryConnect.Api.exe'}
        $record=[pscustomobject]@{pid=100;executablePath=(Join-Path (Join-Path (Join-Path $path 'apps') $name) $exe);startTimeUtc='2026-10-06T10:00:00.0000000Z'}
        $records[$name]=$record
        $processes[$name]=[pscustomobject]@{pid=$record.pid;executablePath=$record.executablePath;startTimeUtc=$record.startTimeUtc}
    }
    [pscustomobject]@{Root=$root;LinkType='Junction';Targets=@($path);Release=[pscustomobject]@{schemaVersion='1.0';sourceCommit=$Sha};ManifestHash=$hash;PayloadVerified=$true;
        Runtime=[pscustomobject]@{selectedRelease=$Sha;releasePath=$path;deploymentStatus='Succeeded';runtimeRunning=$true;processStates=@{edge='Owned';api='Owned'};edge=$records.edge;api=$records.api};Processes=$processes}
}
$cases=@(
    @{Name='Exact approved release';Reject=$false;Change={param($c)}}
    @{Name='Stale runtime selection';Reject=$true;Change={param($c)$c.Runtime.selectedRelease='0'*40}}
    @{Name='Wrong junction';Reject=$true;Change={param($c)$c.Targets=@((Join-Path $c.Root 'old'))}}
    @{Name='Multiple junction targets';Reject=$true;Change={param($c)$c.Targets+=@($c.Root)}}
    @{Name='Edge path mismatch';Reject=$true;Change={param($c)$c.Processes.edge.executablePath=Join-Path $c.Root 'old.exe'}}
    @{Name='API path mismatch';Reject=$true;Change={param($c)$c.Processes.api.executablePath=Join-Path $c.Root 'old.exe'}}
    @{Name='PID reuse';Reject=$true;Change={param($c)$c.Processes.edge.startTimeUtc='2026-10-06T11:00:00Z'}}
    @{Name='PID mismatch';Reject=$true;Change={param($c)$c.Processes.api.pid=101}}
    @{Name='Dead process';Reject=$true;Change={param($c)$c.Processes.edge=$null}}
    @{Name='Old release';Reject=$true;Change={param($c)$c.Release.sourceCommit='0'*40}}
    @{Name='Unowned process';Reject=$true;Change={param($c)$c.Runtime.processStates.api='Mismatch'}}
    @{Name='Payload provenance mismatch';Reject=$true;Change={param($c)$c.ManifestHash='B'*64}}
)
foreach($case in $cases){
    $capture=New-Capture
    & $case.Change $capture
    $rejected=$false
    try{$null=Assert-RecoveryDeploymentCapture $capture $release $hash}catch{$rejected=$true}
    if($rejected-ne$case.Reject){throw "FAIL: $($case.Name)"}
    Write-Host "PASS: $($case.Name)"
}
$descendant='1'*40
$null=Assert-RecoveryDeploymentCapture (New-Capture $descendant) $descendant $hash
Write-Host 'PASS: Explicitly reviewed descendant with pinned payload'
$rejected=$false
try{$null=Assert-RecoveryDeploymentCapture (New-Capture $descendant) $release $hash}catch{$rejected=$true}
if(-not$rejected){throw 'Unreviewed descendant accepted'}
Write-Host 'PASS: Unreviewed descendant rejected'
