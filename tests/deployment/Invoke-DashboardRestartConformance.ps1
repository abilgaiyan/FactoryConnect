[CmdletBinding()]
param([string]$RestartScript, [switch]$Executable)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RestartScript)) {
    $RestartScript = Join-Path $PSScriptRoot '../../scripts/deployment/Restart-FactoryConnectDashboard.ps1'
}
$RestartScript = [IO.Path]::GetFullPath($RestartScript)
$tokens=$null; $errors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile($RestartScript,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$source = Get-Content -Raw -LiteralPath $RestartScript
$terminationHelper = Join-Path (Split-Path $RestartScript -Parent) 'FactoryConnect.ProcessTermination.ps1'
# Execute the complete production orchestration, including its real atomic
# publisher, filesystem/lock handling and catch/finally paths. Only process,
# junction and HTTP boundaries are substituted for deterministic fault cases.
$source = $source.Replace(". (Join-Path `$PSScriptRoot 'FactoryConnect.ProcessTermination.ps1')",'. $terminationHelper')
$source = $source.Replace('function Write-JsonFileAtomic {','function Write-JsonFileAtomicCore {')
$hooks = @'
function Get-Item {
    param([string]$LiteralPath,[switch]$Force)
    if ($LiteralPath -eq (Join-Path $proof.root 'current')) {
        return [pscustomobject]@{LinkType='Junction';Target=@($proof.release)}
    }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}
function Get-RecordedProcessState {
    param($Record)
    $state='Absent'
    if ($null -ne $Record -and $proof.live.ContainsKey([int]$Record.pid)) {
        $live=$proof.live[[int]$Record.pid]
        $state=if ($live.executablePath -ceq $Record.executablePath -and
            $live.startTimeUtc -ceq $Record.startTimeUtc) { 'Owned' } else { 'Mismatch' }
    }
    [pscustomobject]@{State=$state;Process=$null}
}
function Wait-HttpOk {
    param([string]$Uri,[int]$TimeoutSeconds)
    $proof.events.Add('Health')
    $proof.healthUris.Add($Uri)
    if ($proof.launched -and $proof.fault -eq 'DashboardReady' -and $Uri.EndsWith('/health/ready')) { throw 'Injected ready failure.' }
    if ($proof.launched -and $proof.fault -eq 'DashboardLive' -and $Uri.EndsWith('/health/live')) { throw 'Injected live failure.' }
    if ($proof.fault -eq 'PreflightHealth' -or ($proof.fault -eq 'PreservationHealth' -and $proof.launched)) {
        throw 'Injected HTTP failure.'
    }
}
function Stop-RecordedOwnedProcess {
    param($Record)
    if ((Get-RecordedProcessState $Record).State -ne 'Owned') { throw 'Injected identity mismatch.' }
    if ($Record.name -cne 'dashboard') { throw 'Attempted termination of a preserved process.' }
    $proof.events.Add('StopDashboard')
    $outcome=if ($proof.fault -eq 'TerminationTimeout') { 'Timeout' } else { 'Exited' }
    $script:TerminationObservations.Add([pscustomobject]@{processName='dashboard';pid=$Record.pid;outcome=$outcome})
    if ($outcome -eq 'Timeout') { throw 'Injected termination timeout.' }
    $proof.live.Remove([int]$Record.pid)
}
function Start-OwnedProcess {
    param([string]$Name,[string]$Executable,[hashtable]$Environment,[string]$Log)
    if ($Name -cne 'dashboard') { throw 'Attempted launch of a preserved process.' }
    $proof.events.Add('LaunchDashboard')
    if ($Environment['Dashboard__Sources__0__ProcessorId'] -cne 'operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1') { throw 'Wrong configuration environment.' }
    if ($proof.fault -eq 'LaunchFailure') { throw 'Injected launch failure with no identity returned.' }
    $proof.nextPid++
    $record=@{name='dashboard';pid=$proof.nextPid;executablePath=$Executable;startTimeUtc=('fixture-start-'+$proof.nextPid)}
    $proof.live[[int]$record.pid]=$record.Clone()
    $proof.launched=$true
    if ($proof.fault -eq 'UnknownLaunch') { throw 'Injected exception after child creation, before identity returned.' }
    if ($proof.fault -eq 'ReplacementIdentity') { $record.startTimeUtc='wrong' }
    return $record
}
function Start-Sleep {
    param([int]$Seconds)
    $proof.events.Add('Stabilize')
    if ($proof.fault -eq 'StabilizationExit') { $proof.live.Remove([int]$proof.nextPid) }
    if ($proof.fault -eq 'PreservedExit') { $proof.live.Remove(102) }
}
function Remove-Item {
    param([string]$LiteralPath,[switch]$Force)
    if ($proof.fault -eq 'Publication:IntentClear' -and $LiteralPath -eq (Join-Path $proof.root 'deployment/runtime-start.intent.json')) {
        throw 'Injected intent-clear failure.'
    }
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Write-JsonFileAtomic {
    param([string]$Path,$Value)
    $event=if ($Path -eq (Join-Path $proof.root 'deployment/runtime-start.intent.json')) { 'Intent' } else { [string]$Value.dashboardRestart.phase }
    $proof.events.Add('Publish:' + $event)
    if ($proof.fault -eq ('Publication:' + $event) -or
        ($proof.fault -eq 'Publication:OwnershipAndFailure' -and $event -in @('OwnershipPublication'))) {
        throw "Injected $event publication failure."
    }
    if ($proof.fault -eq 'Publication:OwnershipAndFailure' -and $event -ne 'Intent' -and $Value.deploymentStatus -eq 'Failed') {
        throw 'Injected failure evidence publication failure.'
    }
    if ($event -eq 'OwnershipPublication' -and -not $proof.events.Contains('Stabilize')) { throw 'Ownership published before stabilization.' }
    Write-JsonFileAtomicCore $Path $Value
    $proof.snapshots.Add([pscustomobject]@{event=$event;json=($Value|ConvertTo-Json -Depth 30)})
}
'@
$source = $source.Replace('$root = Get-CanonicalPath $InstallRoot', $hooks + "`n`$root = Get-CanonicalPath `$InstallRoot")
$operation = [scriptblock]::Create($source)
$results=[ordered]@{}
$roots=[Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function New-Proof([string]$Fault='') {
    $root=Join-Path ([IO.Path]::GetTempPath()) ('FC-dashboard-contract-' + [Guid]::NewGuid().ToString('N'))
    $roots.Add($root)
    $release=Join-Path $root ('releases/' + ('1'*40))
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'deployment'),(Join-Path $root 'config') | Out-Null
    $live=@{}; $records=@{}
    foreach ($name in @('dashboard','api','edge')) {
        $role=@{dashboard='Dashboard';api='Api';edge='Edge'}[$name]
        $exe=Join-Path $release "apps/$name/FactoryConnect.$role.exe"
        New-Item -ItemType Directory -Force -Path (Split-Path $exe -Parent) | Out-Null
        Set-Content -LiteralPath $exe -Value 'non-executable contract fixture'
        $pidValue=@{dashboard=101;api=102;edge=103}[$name]
        $record=@{name=$name;pid=$pidValue;executablePath=$exe;startTimeUtc='fixture-original-start';extension='preserve'}
        $records[$name]=$record; $live[$pidValue]=$record.Clone()
    }
    $runtime=@{schemaVersion='1.0';selectedRelease=('1'*40);releasePath=$release;deploymentStatus='Succeeded';deploymentAttemptId='original-deployment';migrationOutcome='Succeeded';databaseMayHaveChanged=$true;dashboard=$records.dashboard;api=$records.api;edge=$records.edge}
    $runtime | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $root 'deployment/runtime.json')
    $machineId='00000000-0000-0000-0000-000000000001'
    $edge=@{Fixture='not reconfigured'}
    $api=@{Urls='http://127.0.0.1:5080'}
    $dashboard=@{Urls='http://127.0.0.1:5081';Dashboard=@{ReportingApiBaseAddress='http://127.0.0.1:5080';RequestTimeout='00:00:10';Sources=@(@{MachineId=$machineId;ProcessorId="operational-metrics:$machineId`:builtins-v1-availability-containment-v1";SiteId='s';ProductionLineId='l';DisplayName='M01';GroupName='line'})}}
    foreach ($name in @('dashboard','api','edge')) {
        (Get-Variable $name -ValueOnly) | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $root "config/$name.production.json")
    }
    return @{root=$root;release=$release;live=$live;fault=$Fault;events=[Collections.Generic.List[string]]::new();healthUris=[Collections.Generic.List[string]]::new();snapshots=[Collections.Generic.List[object]]::new();nextPid=200;launched=$false;original=$runtime}
}
function Read-ProofRuntime { Get-Content -Raw (Join-Path $proof.root 'deployment/runtime.json') | ConvertFrom-Json }
function Write-ProofRuntime($Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $proof.root 'deployment/runtime.json') }
function Invoke-Proof([bool]$ExpectFailure=$false) {
    $failed=$false
    $proof.lastError=''
    try { & $operation -InstallRoot $proof.root -MachineId 00000000-0000-0000-0000-000000000001 -ExpectedProcessorId operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1 -DashboardStabilizationSeconds 1 -HealthTimeoutSeconds 1 -ShutdownTimeoutSeconds 1 | Out-Null }
    catch { $failed=$true; $proof.lastError=$_.Exception.Message }
    Assert-True ($failed -eq $ExpectFailure) "Unexpected operation outcome; expectedFailure=$ExpectFailure; error=$($proof.lastError)."
}
function Assert-Preserved {
    $value=Read-ProofRuntime
    foreach ($name in @('api','edge')) {
        Assert-True (($value.$name | ConvertTo-Json -Compress) -ceq ($proof.original[$name] | ConvertTo-Json -Compress)) "$name record changed."
        Assert-True ($proof.live.ContainsKey([int]$proof.original[$name].pid)) "$name process was terminated."
    }
    Assert-True ($value.deploymentAttemptId -eq 'original-deployment' -and $value.migrationOutcome -eq 'Succeeded' -and $value.databaseMayHaveChanged) 'Deployment evidence was overwritten.'
}
function Assert-Rejected {
    $before=Get-Content -Raw (Join-Path $proof.root 'deployment/runtime.json')
    Invoke-Proof $true
    Assert-True (-not $proof.events.Contains('StopDashboard') -and -not $proof.events.Contains('LaunchDashboard')) 'Preflight mutated a process.'
    Assert-True ($before -ceq (Get-Content -Raw (Join-Path $proof.root 'deployment/runtime.json'))) 'Rejection changed runtime evidence.'
}
function Invoke-Case([string]$Name,[scriptblock]$Body) {
    & $Body
    $results[$Name]='PASS'
    Write-Host "$Name PASS"
}
try {
    Invoke-Case D01 {
        $proof=New-Proof; Invoke-Proof; Assert-Preserved
        $value=Read-ProofRuntime
        Assert-True ($value.deploymentStatus -eq 'Succeeded' -and $value.dashboard.pid -ne 101) 'Healthy restart failed.'
        $events=@($proof.events | Where-Object { $_ -ne 'Health' }) -join ','
        Assert-True ($events -ceq 'Publish:Termination,StopDashboard,Publish:AbsencePublication,Publish:Intent,LaunchDashboard,Stabilize,Publish:OwnershipPublication,Publish:Complete') "Wrong transition sequence: $events"
        Assert-True (-not (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json'))) 'Completed restart retained intent.'
        foreach ($uri in @('http://127.0.0.1:5081/health/live','http://127.0.0.1:5081/health/ready')) { Assert-True ($proof.healthUris.Contains($uri)) "Required health not verified: $uri" }
    }
    Invoke-Case D02 {
        foreach ($field in @('startTimeUtc','executablePath')) {
            $proof=New-Proof; $v=Read-ProofRuntime; $v.dashboard.$field='wrong'; Write-ProofRuntime $v; Assert-Rejected
        }
    }
    Invoke-Case D03 {
        foreach ($name in @('api','edge')) {
            $proof=New-Proof; $v=Read-ProofRuntime; $v.$name.startTimeUtc='wrong'; Write-ProofRuntime $v; Assert-Rejected
        }
        $proof=New-Proof 'PreflightHealth'; Assert-Rejected
    }
    Invoke-Case D04 {
        $proof=New-Proof
        $held=[IO.File]::Open((Join-Path $proof.root 'deployment/deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { Assert-Rejected } finally { $held.Dispose() }
    }
    Invoke-Case D05 {
        $proof=New-Proof; $ip=Join-Path $proof.root 'deployment/runtime-start.intent.json'
        Set-Content $ip '{"attemptId":"foreign","processName":"dashboard"}'; $before=Get-Content -Raw $ip
        Assert-Rejected
        Assert-True ($before -ceq (Get-Content -Raw $ip)) 'Foreign intent changed.'
    }
    Invoke-Case D06 {
        $proof=New-Proof 'TerminationTimeout'; Invoke-Proof $true; Assert-Preserved
        $v=Read-ProofRuntime
        Assert-True (-not $proof.events.Contains('LaunchDashboard') -and $v.deploymentStatus -eq 'Failed' -and $v.dashboardRestart.terminationObservations[0].outcome -eq 'Timeout') 'Termination timeout allowed replacement or lost evidence.'
    }
    Invoke-Case D07 {
        foreach ($fault in @('LaunchFailure','StabilizationExit','ReplacementIdentity')) {
            $proof=New-Proof $fault; Invoke-Proof $true; Assert-Preserved; $v=Read-ProofRuntime
            Assert-True ($v.deploymentStatus -eq 'Failed' -and $null -eq $v.dashboard -and -not $v.dashboardRestart.replacementPublished) 'Launch failure claimed replacement ownership.'
            Assert-True (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json')) 'Launch failure lost its conservative intent.'
            if ($fault -eq 'StabilizationExit') { Assert-True ($v.processStates.dashboard -eq 'Absent' -and $v.dashboardRestart.replacementObservation.state -eq 'Absent') 'Observed replacement exit was not recorded.' }
        }
    }
    Invoke-Case D08 {
        $proof=New-Proof 'UnknownLaunch'; Invoke-Proof $true; Assert-Preserved
        $v=Read-ProofRuntime
        Assert-True ($proof.live.ContainsKey(201) -and $null -eq $v.dashboard -and $v.processStates.dashboard -eq 'Unproven') 'Unknown launch made unsupported ownership claim.'
        Assert-True (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json')) 'Unknown launch lost durable intent.'
        $proof.events.Clear(); Assert-Rejected
    }
    Invoke-Case D09 {
        foreach ($phase in @('Termination','AbsencePublication','Intent','OwnershipPublication','IntentClear','Complete','OwnershipAndFailure')) {
            $proof=New-Proof ('Publication:'+$phase); Invoke-Proof $true; Assert-Preserved; $v=Read-ProofRuntime
            Assert-True ($v.deploymentStatus -ne 'Succeeded' -or $phase -eq 'Termination') 'Publication failure claimed success.'
            if ($phase -in @('Termination','AbsencePublication','Intent')) { Assert-True (-not $proof.events.Contains('LaunchDashboard')) 'Launch crossed a failed publication barrier.' }
            if ($phase -eq 'IntentClear') {
                Assert-True ($v.dashboardRestart.replacementPublished -and (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json'))) 'Intent-clear failure lost its ownership record or barrier.'
            }
            if ($phase -in @('OwnershipPublication','OwnershipAndFailure')) {
                Assert-True ($null -eq $v.dashboard -and (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json'))) "Failed ownership publication lost intent or claimed ownership: $phase dashboard=$($v.dashboard|ConvertTo-Json -Compress) intent=$(Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json'))."
            }
        }
    }
    Invoke-Case D10 {
        $proof=New-Proof; Invoke-Proof; Assert-Preserved
        foreach ($fault in @('PreservedExit','PreservationHealth','DashboardLive','DashboardReady')) {
            $proof=New-Proof $fault; Invoke-Proof $true; $v=Read-ProofRuntime
            Assert-True ($v.deploymentStatus -eq 'Failed' -and $null -eq $v.dashboard) 'Preservation failure published replacement ownership.'
            Assert-True (Test-Path (Join-Path $proof.root 'deployment/runtime-start.intent.json')) 'Preservation failure cleared intent.'
        }
    }
    Invoke-Case D11 {
        foreach ($kind in @('record','outside','nested')) {
            $proof=New-Proof
            if ($kind -eq 'record') { $v=Read-ProofRuntime; $v.selectedRelease='2'*40; Write-ProofRuntime $v }
            elseif ($kind -eq 'outside') { $proof.release=Join-Path $proof.root 'outside' }
            else { $proof.release=Join-Path $proof.root ('releases/nested/'+('1'*40)) }
            Assert-Rejected
        }
    }
    Invoke-Case D12 {
        $proof=New-Proof; Invoke-Proof; $first=(Read-ProofRuntime).dashboard.pid
        $proof.events.Clear(); Invoke-Proof; Assert-Preserved
        Assert-True ((Read-ProofRuntime).dashboard.pid -ne $first) 'Explicit repeated restart was treated as an implicit no-op.'
        $proof=New-Proof 'TerminationTimeout'; Invoke-Proof $true; $proof.events.Clear(); $proof.fault=''; Assert-Rejected
        # Model both interruption points: before confirmed termination and after
        # confirmed termination but before the Absent publication.
        foreach ($stopOccurred in @($false,$true)) {
            $proof=New-Proof; $v=Read-ProofRuntime; $v.deploymentStatus='DashboardRestarting'
            if ($stopOccurred) { $proof.live.Remove(101) }
            $v|Add-Member -MemberType NoteProperty -Name dashboardRestart -Value @{status='DashboardRestarting';phase='Termination'}
            Write-ProofRuntime $v; Assert-Rejected
        }
    }
    Invoke-Case D14 {
        $proof=New-Proof
        $v=Read-ProofRuntime
        $v | Add-Member -MemberType NoteProperty -Name edgeRestart -Value @{status='Failed';phase='Launch'}
        Write-ProofRuntime $v; Assert-Rejected
        $proof=New-Proof
        $v=Read-ProofRuntime
        $v | Add-Member -MemberType NoteProperty -Name processStates -Value @{extension='preserve';edge='Owned';api='Owned';dashboard='Owned'}
        Write-ProofRuntime $v; Invoke-Proof; Assert-Preserved
        Assert-True ((Read-ProofRuntime).processStates.extension -ceq 'preserve') 'Unrelated process state extension was lost.'
    }
    Invoke-Case D13 {
        foreach ($fault in @('Processor','ExpectedProcessor','Machine','MalformedJson','Placeholders','Timeout')) {
            $proof=New-Proof
            $path=Join-Path $proof.root 'config/dashboard.production.json'
            $cfg=Get-Content -Raw $path | ConvertFrom-Json
            if ($fault -eq 'Processor') { $cfg.Dashboard.Sources[0].ProcessorId='machine-state-activity' }
            if ($fault -eq 'ExpectedProcessor') { $cfg.Dashboard.Sources[0].ProcessorId='operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1' }
            if ($fault -eq 'Machine') { $cfg.Dashboard.Sources[0].MachineId='00000000-0000-0000-0000-000000000002' }
            if ($fault -eq 'Placeholders') { $cfg.Dashboard.Sources[0].SiteId='__SITE__' }
            if ($fault -eq 'Timeout') { $cfg.Dashboard.RequestTimeout='00:00:00' }
            $cfg | ConvertTo-Json -Depth 30 | Set-Content $path
            if ($fault -eq 'MalformedJson') { Set-Content $path '{' }
            Assert-Rejected
        }
    }
} finally {
    foreach ($root in $roots) { Remove-Item -LiteralPath $root -Recurse -Force }
}
[pscustomobject]$results
if ($Executable) {
    & (Join-Path $PSScriptRoot 'Invoke-DashboardRestartExecutableConformance.ps1') -RestartScript $RestartScript
}
