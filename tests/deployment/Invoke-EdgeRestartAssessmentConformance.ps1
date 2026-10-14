[CmdletBinding()]
param([string]$AssessmentScript)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($AssessmentScript)) { $AssessmentScript=Join-Path $PSScriptRoot '../../scripts/deployment/Get-FactoryConnectEdgeRestartAssessment.ps1' }
$AssessmentScript=[IO.Path]::GetFullPath($AssessmentScript)
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($AssessmentScript,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors|Out-String) }
$source=Get-Content -Raw -LiteralPath $AssessmentScript
$hooks=@'
function Get-Item {
    param([string]$LiteralPath,[switch]$Force)
    if ($LiteralPath -eq (Join-Path $proof.root 'current')) { return [pscustomobject]@{LinkType='Junction';Target=@($proof.release)} }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}
function Get-AssessmentInventory {
    if ($proof.inventoryError) { throw 'Injected inventory access denial.' }
    $proof.inventoryReads++
    $rows=@($proof.live.Values | ForEach-Object { [pscustomobject]@{name='edge';pid=$_.pid;executablePath=$_.executablePath;startTimeUtc=$_.startTimeUtc;observation='VerifiedIdentity'} })
    if ($proof.hidden) { $rows+= [pscustomobject]@{name='edge';pid=900;executablePath=$null;startTimeUtc=$null;observation='Unproven'} }
    if ($proof.unstable -and $proof.inventoryReads -eq 2) { $rows=@() }
    return $rows
}
function Get-AssessmentRecordState($Record) {
    if ($null -eq $Record) { return 'NoRecord' }
    if ($Record.name -in @('api','dashboard')) { return 'Verified' }
    if (-not $proof.live.ContainsKey([int]$Record.pid)) { return 'Absent' }
    if (Test-AssessmentSameRecord $Record $proof.live[[int]$Record.pid]) { return 'Verified' }
    return 'Mismatch'
}
'@
$operation=[scriptblock]::Create($source.Replace('$root=Get-AssessmentPath $InstallRoot',$hooks+"`n`$root=Get-AssessmentPath `$InstallRoot"))
$roots=[Collections.Generic.List[string]]::new();$results=[ordered]@{}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function New-Proof {
    $root=Join-Path ([IO.Path]::GetTempPath()) ('FC-edge-assessment-'+[Guid]::NewGuid().ToString('N'));$roots.Add($root)
    $release=Join-Path $root ('releases/'+('1'*40));$exe=Join-Path $release 'apps/edge/FactoryConnect.Edge.exe'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'deployment'),(Split-Path $exe -Parent)|Out-Null
    Set-Content (Join-Path $root 'deployment/deployment.lock') '';Set-Content $exe 'non-executable fixture'
    $old=@{name='edge';pid=100;executablePath=$exe;startTimeUtc='fixture-old'}
    $new=@{name='edge';pid=200;executablePath=$exe;startTimeUtc='fixture-new'}
    $runtime=@{selectedRelease=('1'*40);releasePath=$release;deploymentStatus='Failed';edge=$new;api=@{name='api';pid=101};dashboard=@{name='dashboard';pid=102};edgeRestart=@{attemptId='restart-proof';phase='FinalVerification';oldEdge=$old;replacementPublished=$true;replacementObservation=@{record=$new;state='Owned'}}}
    return @{root=$root;release=$release;exe=$exe;old=$old;new=$new;runtime=$runtime;intent=$null;live=@{200=$new.Clone()};inventoryError=$false;hidden=$false;unstable=$false;inventoryReads=0}
}
function Publish-Proof {
    $proof.runtime|ConvertTo-Json -Depth 30|Set-Content (Join-Path $proof.root 'deployment/runtime.json')
    if ($null -ne $proof.intent) { $proof.intent|ConvertTo-Json -Depth 30|Set-Content (Join-Path $proof.root 'deployment/runtime-start.intent.json') }
}
function Assert-Assessment([string]$Expected) {
    Publish-Proof
    $paths=@(Get-ChildItem (Join-Path $proof.root 'deployment') -File)
    $hashes=@{};foreach($file in $paths){$hashes[$file.FullName]=(Get-FileHash $file.FullName).Hash}
    $report=& $operation -InstallRoot $proof.root
    Assert-True ($report.classification -ceq $Expected) "Expected $Expected; got $($report|ConvertTo-Json -Depth 10 -Compress)"
    Assert-True (-not $report.mutationAuthorized -and $report.recoveryRequired) 'Read-only assessment authorized mutation or hid recovery.'
    $after=@(Get-ChildItem (Join-Path $proof.root 'deployment') -File)
    Assert-True ($paths.Count -eq $after.Count) 'Assessment changed file inventory.'
    foreach($file in $after){Assert-True ($hashes[$file.FullName] -ceq (Get-FileHash $file.FullName).Hash) 'Assessment changed evidence bytes.'}
    return $report
}
function Invoke-Case([string]$Name,[scriptblock]$Body){& $Body;$results[$Name]='PASS';Write-Host "$Name PASS"}
try {
    Invoke-Case A01-Owned { $proof=New-Proof;[void](Assert-Assessment 'VerifiablyOwnedReplacement') }
    Invoke-Case A02-Absent {
        $proof=New-Proof;$proof.live.Clear();[void](Assert-Assessment 'AbsentReplacement')
        $proof=New-Proof;$proof.live.Clear();$proof.runtime.edge=$proof.old;$proof.runtime.edgeRestart.replacementPublished=$false;$proof.runtime.edgeRestart.Remove('replacementObservation')
        $proof.live[100]=$proof.old;[void](Assert-Assessment 'AbsentReplacement')
    }
    Invoke-Case A03-UnpublishedCandidate {
        $proof=New-Proof;$proof.runtime.edge=$null;$proof.runtime.edgeRestart.replacementPublished=$false
        $report=Assert-Assessment 'AmbiguousOwnership'
        Assert-True ($report.identityStates.diagnosticReplacement -eq 'Verified' -and $report.candidates.Count -eq 1) 'Diagnostic identity was lost or improperly adopted.'
    }
    Invoke-Case A04-UnknownLaunch {
        $proof=New-Proof;$proof.runtime.edge=$null;$proof.runtime.edgeRestart.replacementPublished=$false;$proof.runtime.edgeRestart.Remove('replacementObservation')
        $proof.intent=@{operation='EdgeRestart';processName='edge';attemptId='restart-proof';selectedRelease=('1'*40);executablePath=$proof.exe}
        $report=Assert-Assessment 'AmbiguousOwnership'
        Assert-True ($report.launchIntentConsistent -and $report.candidates[0].pid -eq 200) 'Unrecorded candidate was not discovered.'
    }
    Invoke-Case A05-PidReuse {
        $proof=New-Proof;$proof.live[200]=$proof.new.Clone();$proof.live[200].startTimeUtc='reused';[void](Assert-Assessment 'AmbiguousOwnership')
    }
    Invoke-Case A06-MultipleCandidates {
        $proof=New-Proof;$extra=$proof.new.Clone();$extra.pid=201;$proof.live[201]=$extra;[void](Assert-Assessment 'AmbiguousOwnership')
    }
    Invoke-Case A07-Inaccessible {
        $proof=New-Proof;$proof.live.Clear();$proof.hidden=$true;[void](Assert-Assessment 'AmbiguousOwnership')
        $proof=New-Proof;$proof.inventoryError=$true;[void](Assert-Assessment 'AmbiguousOwnership')
    }
    Invoke-Case A08-Unstable { $proof=New-Proof;$proof.unstable=$true;[void](Assert-Assessment 'AmbiguousOwnership') }
    Invoke-Case A09-ForeignIntent {
        $proof=New-Proof;$proof.intent=@{attemptId='foreign';processName='edge'};[void](Assert-Assessment 'AmbiguousOwnership')
    }
    Invoke-Case A10-ReleaseMismatch { $proof=New-Proof;$proof.runtime.selectedRelease='2'*40;[void](Assert-Assessment 'AmbiguousOwnership') }
    Invoke-Case A11-LockContention {
        $proof=New-Proof;Publish-Proof
        $path=Join-Path $proof.root 'deployment/deployment.lock'
        $held=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $failed=$false
        try { try { & $operation -InstallRoot $proof.root|Out-Null } catch { $failed=$true } }
        finally {$held.Dispose()}
        Assert-True $failed 'Read-only operation ignored lock contention.'
    }
    Invoke-Case A13-OldAndReplacementLive {
        foreach ($published in @($true,$false)) {
            $proof=New-Proof;$proof.live[100]=$proof.old.Clone()
            $proof.runtime.edgeRestart.replacementPublished=$published
            # Even an erroneous Succeeded marker must not conceal this conflict.
            $proof.runtime.deploymentStatus='Succeeded';$proof.runtime.edgeRestart.status='Succeeded'
            $report=Assert-Assessment 'AmbiguousOwnership'
            Assert-True ($report.oldAndReplacementLive -and $report.identityStates.oldEdge -eq 'Verified' -and
                $report.candidates.Count -eq 1 -and @($report.reasons | Where-Object { $_ -like 'OldAndReplacementLive:*' }).Count -eq 1) 'Simultaneously live old/replacement conflict was not explicit.'
        }
    }
    Invoke-Case A12-MissingLock {
        $proof=New-Proof;Publish-Proof;$path=Join-Path $proof.root 'deployment/deployment.lock';Remove-Item $path
        $failed=$false;try{& $operation -InstallRoot $proof.root|Out-Null}catch{$failed=$true}
        Assert-True ($failed -and -not(Test-Path $path)) 'Read-only operation created a lock or accepted missing exclusion.'
    }
} finally {foreach($root in $roots){Remove-Item -LiteralPath $root -Recurse -Force}}
[pscustomobject]$results
