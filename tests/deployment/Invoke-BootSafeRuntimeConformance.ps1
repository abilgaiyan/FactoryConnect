[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../../scripts/deployment/Start-FactoryConnectRuntime.ps1'
$text = Get-Content -Raw $path
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($path),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw "Runtime parser errors: $errors" }
# Execute the unchanged production classification/health/admission/reconciliation
# statements with controlled process and health outcomes. No machine is accessed.
$start=$text.IndexOf(" foreach(" + '$n in @(''edge'',''api'',''dashboard'')){$r=if')
$end=$text.IndexOf(" " + '$failurePhase=''Startup'';',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Production admission boundary not found.' }
$admission=[scriptblock]::Create($text.Substring($start,$end-$start))
function Get-RecordedProcessState($Record) { [pscustomobject]@{State=$script:fixtureStates[$Record.name]} }
function Test-HttpOk($Uri) { $script:healthy }
function Get-Process {
    param($ErrorAction)
    if ($script:inventoryFailure) { throw 'Injected inventory failure.' }
    foreach ($n in @('edge','api','dashboard')) {
        if ($script:fixtureStates[$n] -eq 'Owned') {
            [pscustomobject]@{Id=$oldRuntime[$n].pid;ProcessName="FactoryConnect.$n"}
        }
    }
    if ($script:unrecorded) { [pscustomobject]@{Id=123;ProcessName='FactoryConnect.Edge'} }
}
function Stop-RecordedOwnedProcess($Record) { $script:stops++ }
$results=[ordered]@{}
foreach ($mode in @('BootSafe','Reconcile','Restart')) {
    foreach ($case in @('Healthy','Unhealthy','Cold','Partial','Mismatch','Unrecorded','HealthyDuplicate','InventoryFailure')) {
        $StartupMode=$mode
        $script:healthy=$case -in @('Healthy','HealthyDuplicate')
        $script:unrecorded=$case -in @('Unrecorded','HealthyDuplicate')
        $script:inventoryFailure=$case -eq 'InventoryFailure'
        $script:fixtureStates=@{edge='Absent';api='Absent';dashboard='Absent'}
        if ($case -in @('Healthy','Unhealthy','HealthyDuplicate')) { foreach ($n in @('edge','api','dashboard')) { $script:fixtureStates[$n]='Owned' } }
        if ($case -eq 'Partial') { $script:fixtureStates.edge='Owned' }
        if ($case -eq 'Mismatch') { $script:fixtureStates.edge='Mismatch' }
        $expected=@{}; $oldRuntime=@{}; $fixturePid=1000
        foreach ($n in @('edge','api','dashboard')) {
            $expected[$n]=Join-Path ([IO.Path]::GetTempPath()) "$n.exe"
            $oldRuntime[$n]=@{name=$n;executablePath=$expected[$n];pid=$fixturePid++}
        }
        $records=@{}; $states=@{}
        $script:stops=0; $mutationStarted=$false
        $apiHealth='api'; $live='live'; $ready='ready'
        $releaseId='fixture'; $root='fixture'; $current='fixture'
        $rejected=$false; $output=$null
        try { $output=. $admission } catch { $rejected=$true }
        $mustReject=($case -eq 'Mismatch') -or ($mode -eq 'BootSafe' -and $case -in @('Unhealthy','Partial','Unrecorded','HealthyDuplicate','InventoryFailure')) -or ($mode -eq 'Restart' -and $case -in @('Unrecorded','HealthyDuplicate','InventoryFailure'))
        if ($rejected -ne $mustReject) { throw "$mode/$case rejection incorrect." }
        if ($mustReject -and ($script:stops -ne 0 -or $mutationStarted)) { throw "$mode/$case mutated before rejection." }
        if ($mode -ne 'Restart' -and $case -eq 'Healthy' -and ($output.Status -ne 'AlreadyRunning' -or $script:stops -ne 0 -or $mutationStarted)) { throw "$mode healthy reuse failed." }
        if ($mode -eq 'Restart' -and $case -eq 'Healthy' -and ($null -ne $output -or $script:stops -ne 3 -or -not $mutationStarted)) { throw 'Explicit restart did not reconcile healthy owned processes.' }
        if ($case -eq 'Cold' -and (-not $mutationStarted -or $script:stops -ne 0)) { throw "$mode cold admission failed." }
        if ($mode -in @('Reconcile','Restart') -and $case -in @('Partial','Unhealthy')) {
            $count=if($case -eq 'Partial'){1}else{3}
            if ($script:stops -ne $count) { throw 'Deployment reconciliation regressed.' }
        }
        $results["$mode/$case"]='PASS'
    }
}
[pscustomobject]$results
