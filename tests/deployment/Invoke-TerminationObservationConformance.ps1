[CmdletBinding()]
param([switch]$Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$helper=Join-Path $repo 'scripts/deployment/FactoryConnect.ProcessTermination.ps1'
$deploy=Join-Path $repo 'scripts/deployment/Deploy-FactoryConnect.ps1'
$runtime=Join-Path $repo 'scripts/deployment/Start-FactoryConnectRuntime.ps1'
function Parse([string]$Path) {
    $t=$null;$e=$null;$a=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$t,[ref]$e)
    if($e.Count){throw "$Path parse failure: $e"}; return $a
}
$deployAst=Parse $deploy; $runtimeAst=Parse $runtime; [void](Parse $helper)
. $helper
$ShutdownTimeoutSeconds=2
$results=[ordered]@{}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Case([string]$Name,[scriptblock]$Body){& $Body;$results[$Name]='PASS';Write-Host "$Name PASS"}
function New-Proof {
    $script:proof=[pscustomobject]@{Handle=1;HasExited=$false;Path=[IO.Path]::GetFullPath((Join-Path $repo 'fake.exe'));StartTime=[DateTime]::UtcNow;Id=42}
    $script:proof | Add-Member ScriptMethod Refresh {}
    $script:proof | Add-Member ScriptMethod Dispose {}
    $script:record=@{name='edge';pid=42;executablePath=$script:proof.Path;startTimeUtc=$script:proof.StartTime.ToUniversalTime().ToString('o')}
    $script:mode='Normal';$script:requestCount=0;$script:queryCount=0;$script:observedTimeout=0
    $script:TerminationObservations.Clear()
}
function Get-Process {
    param($Id,$ErrorAction)
    $script:queryCount++
    if($script:mode -eq 'QueryFailure'){throw 'Injected process observation failure.'}
    if($script:mode -eq 'AlreadyAbsent'){Write-Error 'No such process' -ErrorId NoProcessFoundForGivenId -ErrorAction Stop}
    return $script:proof
}
function Request-FactoryConnectTermination {
    param($Process)
    Assert-True ([object]::ReferenceEquals($Process,$script:proof)) 'Request used a different process.'
    $script:requestCount++
    if($script:mode -eq 'RequestFailure'){throw 'Injected termination request failure.'}
}
function Wait-FactoryConnectTermination {
    param($Process,[int]$TimeoutSeconds)
    Assert-True ([object]::ReferenceEquals($Process,$script:proof)) 'Wait used a different process.'
    $script:observedTimeout=$TimeoutSeconds
    if($script:mode -eq 'WaitFailure'){throw 'Injected exact-process wait failure.'}
    if($script:mode -eq 'Timeout'){return $false}
    if($script:mode -eq 'Delayed'){Start-Sleep -Milliseconds 30}
    $script:proof.HasExited=$true
    return $true
}
Case NormalExit {
    New-Proof; $r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'Exited' -and $requestCount -eq 1 -and $queryCount -eq 1) 'Normal exact-instance exit failed.'
}
Case DelayedWithinDeadline {
    New-Proof;$script:mode='Delayed';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'Exited' -and $observedTimeout -eq 2 -and $r.elapsedMilliseconds -ge 25) 'Delayed exit or configured deadline failed.'
}
Case ExitedButObservable {
    New-Proof;$proof.HasExited=$true;$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'Exited' -and $requestCount -eq 0) 'An observable exited object was classified as surviving.'
}
Case AlreadyAbsent {
    New-Proof;$script:mode='AlreadyAbsent';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'Exited' -and $requestCount -eq 0) 'Already absent process was not handled safely.'
}
Case DeadlineExpiry {
    New-Proof;$script:mode='Timeout';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'Timeout' -and $requestCount -eq 1) 'Deadline expiry failed.'
}
Case IdentityMismatch {
    New-Proof;$record.startTimeUtc='2000-01-01T00:00:00.0000000Z';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'IdentityMismatch' -and $requestCount -eq 0) 'Mismatched identity was terminated.'
}
Case PidMismatch {
    New-Proof;$proof.Id=43;$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'IdentityMismatch' -and $requestCount -eq 0) 'Mismatched PID object was terminated.'
}
Case PathMismatch {
    New-Proof;$record.executablePath=[IO.Path]::GetFullPath((Join-Path $repo 'other.exe'));$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'IdentityMismatch' -and $requestCount -eq 0) 'Mismatched executable was terminated.'
}
Case ObservationFailure {
    New-Proof;$script:mode='QueryFailure';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'ObservationFailure' -and $requestCount -eq 0) 'Query failure became exit proof.'
}
Case WaitFailure {
    New-Proof;$script:mode='WaitFailure';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'ObservationFailure') 'Wait failure became exit proof.'
}
Case RequestFailure {
    New-Proof;$script:mode='RequestFailure';$r=Invoke-FactoryConnectTermination $record 2
    Assert-True ($r.outcome -eq 'ObservationFailure' -and $r.phase -eq 'Request') 'Request failure was masked.'
}
# Load real production wrapper/classification functions; do not mirror them.
foreach($name in @('Stop-RecordedProcesses','Get-RecordedProcessState','Get-ObservedProcessRecords','Get-SelectedReleaseState')) {
    $f=$deployAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Invoke-Expression $f.Extent.Text
}
Case DeploymentShutdownBarrier {
    New-Proof;$script:mode='Timeout';$oldRecords=[ordered]@{edge=$record;api=$null;dashboard=$null}
    $source=Get-Content $deploy -Raw
    $start=$source.IndexOf("    `$failurePhase='Shutdown';")
    $end=$source.IndexOf('    $migrationExe=',$start)
    $block=[scriptblock]::Create($source.Substring($start,$end-$start))
    $caught=$false;$failurePhase='Preparation'
    try { & $block } catch { $caught=$true }
    Assert-True ($caught -and $TerminationObservations[0].outcome -eq 'Timeout') 'Shutdown timeout failed to block migration transition.'
    Assert-True ($oldRecords.edge -eq $record) 'Failed shutdown erased predecessor ownership.'
}
Case DeploymentCleanup {
    New-Proof;$script:mode='Timeout'
    $newRecords=[ordered]@{edge=$record;api=$null;dashboard=$null}
    $oldRecords=[ordered]@{edge=$null;api=$null;dashboard=$null}
    $current=Join-Path $repo 'not-a-selection';$releases=Join-Path $repo 'not-releases'
    $attemptId='proof';$migrationCompleted=$false;$migrationStarted=$false;$lockAcquired=$true;$runtimeDisruptionStarted=$false
    $failurePhase='Startup';$failurePath='failure';$runtimePath='runtime';$script:failureEvidence=$null
    function Write-JsonFile($Path,$Value){$script:failureEvidence=$Value}
    $try=@($deployAst.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[-1]
    $body=$try.CatchClauses[0].Body.Extent.Text
    $handler=[scriptblock]::Create($body.Substring(1,$body.Length-2))
    try{throw 'primary'}catch{try{& $handler}catch{}}
    Assert-True ($failureEvidence.terminationObservations[0].outcome -eq 'Timeout') 'Cleanup failed to preserve termination outcome.'
    Assert-True (-not $failureEvidence.databaseMayHaveChanged -and $failureEvidence.migrationOutcome -eq 'NotStarted') 'Cleanup falsified migration evidence.'
    Assert-True ($newRecords.edge -eq $record) 'Cleanup timeout erased unexited ownership.'
}
Case RuntimeReconciliation {
    New-Proof;$script:mode='Timeout';$records=[ordered]@{edge=$record;api=$null;dashboard=$null};$states=[ordered]@{edge='Owned';api='Absent';dashboard='Absent'}
    $source=Get-Content $runtime -Raw
    $start=$source.IndexOf(" `$failurePhase='Reconciliation';")
    if($start -lt 0){$start=$source.IndexOf(" `$failurePhase='Reconciliation'")}
    $end=$source.IndexOf(" `$failurePhase='Startup';",$start)
    $block=[scriptblock]::Create($source.Substring($start,$end-$start))
    $caught=$false;try{& $block}catch{$caught=$true}
    Assert-True ($caught -and $TerminationObservations[0].outcome -eq 'Timeout' -and $records.edge -eq $record) 'Runtime reconciliation crossed unresolved termination.'
}
Case Scenario4InjectionSeam {
    $scenario=Parse (Join-Path $PSScriptRoot 'Invoke-Scenario4Conformance.ps1')
    $assignment=$scenario.Find({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$cleanupPattern'},$true)
    Invoke-Expression $assignment.Extent.Text
    Assert-True ([regex]::Matches((Get-Content $deploy -Raw),$cleanupPattern).Count -eq 1) 'Scenario 4 cleanup injection seam no longer matches exactly once.'
}
if($Executable){
    if($env:OS -ne 'Windows_NT'){throw 'Real-process termination proof requires Windows.'}
    Remove-Item Function:Get-Process
    . $helper
    $child=Start-Process (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile -Command "Start-Sleep -Seconds 120"' -PassThru -WindowStyle Hidden
    try{
        $r=@{name='disposable';pid=$child.Id;executablePath=$child.Path;startTimeUtc=$child.StartTime.ToUniversalTime().ToString('o')}
        $result=Invoke-FactoryConnectTermination $r 5
        Assert-True ($result.outcome -eq 'Exited' -and $child.WaitForExit(5000)) 'Real owned process exit not confirmed.'
        $results['WindowsExactProcess']='PASS'
    }finally{if(-not $child.HasExited){$child.Kill();[void]$child.WaitForExit(5000)};$child.Dispose()}
}
[pscustomobject]$results
