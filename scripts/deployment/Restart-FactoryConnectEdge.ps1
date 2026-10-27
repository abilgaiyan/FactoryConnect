# GG-002-M1.3A: explicit Edge-only restart; never performs whole-stack reconciliation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter()][ValidateRange(1,300)][int]$EdgeStabilizationSeconds = 5,
    [Parameter()][ValidateRange(1,300)][int]$HealthTimeoutSeconds = 60,
    [Parameter()][ValidateRange(1,600)][int]$ShutdownTimeoutSeconds,
    [Parameter()][string]$MoxaRecoveryPlan
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'FactoryConnect.ProcessTermination.ps1')
if (-not $PSBoundParameters.ContainsKey('ShutdownTimeoutSeconds')) {
    $ShutdownTimeoutSeconds = $script:FactoryConnectShutdownTimeoutDefaultSeconds
}
# These read/configuration/launch primitives retain the startup contract. Keep
# Start-FactoryConnectRuntime.ps1 unchanged, including its whole-stack behavior.
function Get-CanonicalPath { param([string]$Path);
 [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar,[System.IO.Path]::AltDirectorySeparatorChar) }
function Read-JsonFile { param([string]$Path);
 Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Write-JsonFileAtomic {
    param([string]$Path,$Value)
    $directory = Split-Path $Path -Parent
    $temp = Join-Path $directory ((Split-Path $Path -Leaf) + '.tmp.' + [Guid]::NewGuid().ToString('N'))
    $backup = $temp + '.backup'
    try {
        ($Value|ConvertTo-Json -Depth 30)|Set-Content -LiteralPath $temp -Encoding utf8
        if(Test-Path -LiteralPath $Path){[System.IO.File]::Replace($temp,$Path,$backup,$true)}else{Move-Item -LiteralPath $temp -Destination $Path}
    }
    finally {
        foreach ($candidate in @($temp, $backup)) {
            if (Test-Path -LiteralPath $candidate) {
                Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
function Assert-NoPlaceholders { param([string]$Path,[string]$Name);
$m=[regex]::Matches((Get-Content -Raw -LiteralPath $Path),'__[A-Z0-9_]+__');
if($m.Count -gt 0){throw "$Name site configuration is not commissioned."} }
function Assert-NonEmpty { param($Value,[string]$Description);
if([string]::IsNullOrWhiteSpace([string]$Value)){throw "$Description is required."} }
function Assert-AbsoluteHttpUri { param($Value,[string]$Description);
Assert-NonEmpty $Value $Description;
$u=$null;
if(-not[Uri]::TryCreate([string]$Value,[UriKind]::Absolute,[ref]$u)-or$u.Scheme-notin@('http','https')){throw "$Description must be an absolute HTTP/HTTPS URI."};
$u }
function Assert-PositiveDuration { param($Value,[string]$Description);
$d=[TimeSpan]::Zero;
if(-not[TimeSpan]::TryParse([string]$Value,[ref]$d)-or$d-le[TimeSpan]::Zero){throw "$Description must be a positive duration."} }
function Assert-SingleHostUrl { param($Value,[string]$Description);
$p=@(([string]$Value).Split(';',[System.StringSplitOptions]::RemoveEmptyEntries));
if($p.Count-ne1){throw "$Description must contain exactly one URL."};
[void](Assert-AbsoluteHttpUri $p[0] $Description) }
function Assert-SiteConfiguration {
 param($Edge,$Api,$Dashboard)
 if([string]$Edge.Persistence.Provider-cne'SqlServer'){throw "Edge Persistence.Provider must be exactly 'SqlServer'."};
Assert-NonEmpty $Edge.PersistenceProviders.SqlServer.ConnectionString 'Edge SQL connection string';
if($null-eq$Edge.MTConnect.Machines-or@($Edge.MTConnect.Machines).Count-lt1){throw 'Edge must configure at least one MTConnect machine.'};
foreach($m in @($Edge.MTConnect.Machines)){[void](Assert-AbsoluteHttpUri $m.BaseUri 'Edge MTConnect machine BaseUri');
Assert-NonEmpty $m.MachineId 'Edge MTConnect machine MachineId';
Assert-NonEmpty $m.DeviceKey 'Edge MTConnect machine DeviceKey';
if($null-ne$m.PSObject.Properties['PollingInterval']){Assert-PositiveDuration $m.PollingInterval 'Edge MTConnect machine PollingInterval'}};
Assert-PositiveDuration $Edge.CurrentState.Freshness.MaximumCurrentAge 'Edge CurrentState.Freshness.MaximumCurrentAge';
if($null-eq$Edge.ProductionProcessing.Machines-or@($Edge.ProductionProcessing.Machines).Count-lt1){throw 'Edge ProductionProcessing.Machines must contain at least one machine.'};
foreach($m in @($Edge.ProductionProcessing.Machines)){foreach($p in @('MachineId','ActivityStreamKey','QuantityStreamKey','CompanyId','SiteId','ProductionLineId')){Assert-NonEmpty $m.$p "Edge ProductionProcessing machine $p"}}
 if([string]$Api.Persistence.Provider-cne'SqlServer'){throw "API Persistence.Provider must be exactly 'SqlServer'."};
Assert-NonEmpty $Api.PersistenceProviders.SqlServer.ConnectionString 'API SQL connection string';
Assert-SingleHostUrl $Api.Urls 'API Urls';
if($null-eq$Api.MTConnect.Machines-or@($Api.MTConnect.Machines).Count-lt1){throw 'API must configure at least one MTConnect machine.'};
foreach($m in @($Api.MTConnect.Machines)){[void](Assert-AbsoluteHttpUri $m.BaseUri 'API MTConnect machine BaseUri');
Assert-NonEmpty $m.MachineId 'API MTConnect machine MachineId';
Assert-NonEmpty $m.DeviceKey 'API MTConnect machine DeviceKey'};
Assert-PositiveDuration $Api.CurrentState.Freshness.MaximumCurrentAge 'API CurrentState.Freshness.MaximumCurrentAge'
 Assert-SingleHostUrl $Dashboard.Urls 'Dashboard Urls';
[void](Assert-AbsoluteHttpUri $Dashboard.Dashboard.ReportingApiBaseAddress 'Dashboard.ReportingApiBaseAddress');
Assert-PositiveDuration $Dashboard.Dashboard.RequestTimeout 'Dashboard.RequestTimeout';
if($null-eq$Dashboard.Dashboard.Sources-or@($Dashboard.Dashboard.Sources).Count-lt1){throw 'Dashboard must configure at least one source.'};
foreach($s in @($Dashboard.Dashboard.Sources)){foreach($p in @('MachineId','ProcessorId','SiteId','ProductionLineId','DisplayName','GroupName')){Assert-NonEmpty $s.$p "Dashboard source $p"}}
}
function Add-ConfigurationEnvironment { param($Value,[string]$Prefix='',[hashtable]$Environment);
if($null-eq$Value){if($Prefix){$Environment[$Prefix]=''};
return};
if($Value-is[System.Management.Automation.PSCustomObject]){foreach($p in $Value.PSObject.Properties){Add-ConfigurationEnvironment $p.Value $(if($Prefix){"$Prefix`__$($p.Name)"}else{$p.Name}) $Environment};
return};
if($Value-is[System.Collections.IEnumerable]-and$Value-isnot[string]){$i=0;
foreach($item in $Value){Add-ConfigurationEnvironment $item $(if($Prefix){"$Prefix`__$i"}else{[string]$i}) $Environment;
$i++};
return};
if(-not$Prefix){throw 'A scalar configuration value cannot be projected without a key.'};
$Environment[$Prefix]=if($Value-is[bool]){$Value.ToString().ToLowerInvariant()}else{[string]$Value} }
function Get-ConfigurationEnvironment { param([string]$Path);
$e=@{};
Add-ConfigurationEnvironment (Read-JsonFile $Path) '' $e;
$e }
function Get-BaseAddressFromConfiguration { param($Configuration,[string]$Name);
$p=@(([string]$Configuration.Urls).Split(';',[System.StringSplitOptions]::RemoveEmptyEntries));
if($p.Count-ne1){throw "$Name configuration must define exactly one Urls address."};
$u=Assert-AbsoluteHttpUri $p[0] "$Name Urls";
if($u.Host-in@('0.0.0.0','+','*')){$b=[UriBuilder]$u;
$b.Host='localhost';
return $b.Uri.AbsoluteUri.TrimEnd('/')};
$u.AbsoluteUri.TrimEnd('/') }
function Get-RecordedProcessState { param($Record);
if($null-eq$Record){return [pscustomobject]@{State='Absent';
Process=$null}};
$p=Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue;
if($null-eq$p){return [pscustomobject]@{State='Absent';
Process=$null}};
try{$p.Refresh();
if($p.HasExited){return [pscustomobject]@{State='Absent';Process=$null}};
$samePath=[System.IO.Path]::GetFullPath($p.Path)-eq[System.IO.Path]::GetFullPath([string]$Record.executablePath);
$sameStart=$p.StartTime.ToUniversalTime().ToString('o')-eq[string]$Record.startTimeUtc;
if($samePath-and$sameStart){return [pscustomobject]@{State='Owned';
Process=$p}};
return [pscustomobject]@{State='Mismatch';
Process=$p}}catch{return [pscustomobject]@{State='Mismatch';
Process=$p}} }
function Test-HttpOk { param([string]$Uri);
try{$r=Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5;
[int]$r.StatusCode-eq200}catch{$false} }
function Wait-HttpOk { param([string]$Uri,[int]$TimeoutSeconds);
$d=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds);
do{if(Test-HttpOk $Uri){return};
Start-Sleep -Milliseconds 500}while([DateTime]::UtcNow-lt$d);
throw "Health check did not return HTTP 200 within $TimeoutSeconds seconds: $Uri" }
function Start-OwnedProcess { param([string]$Name,[string]$Executable,[hashtable]$Environment,[string]$Log);
$saved=@{};
try{foreach($x in $Environment.GetEnumerator()){$saved[$x.Key]=[Environment]::GetEnvironmentVariable($x.Key,'Process');
[Environment]::SetEnvironmentVariable($x.Key,[string]$x.Value,'Process')};
foreach($n in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')){$saved[$n]=[Environment]::GetEnvironmentVariable($n,'Process');
[Environment]::SetEnvironmentVariable($n,'Production','Process')};
$p=Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable -Parent) -PassThru -RedirectStandardOutput (Join-Path $Log "$Name.out.log") -RedirectStandardError (Join-Path $Log "$Name.err.log")}finally{foreach($x in $saved.GetEnumerator()){[Environment]::SetEnvironmentVariable($x.Key,$x.Value,'Process')}};
[ordered]@{name=$Name;
pid=$p.Id;
executablePath=[System.IO.Path]::GetFullPath($Executable);
startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o')} }

function New-EdgeRestartRuntime {
    param([string]$Status,[string]$Phase)
    # Preserve deployment evidence and API/Dashboard records, including extension
    # properties. Only Edge ownership and this explicit operation are changed.
    $value = $oldRuntime | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $observedStates = [ordered]@{edge=$edgeState;api=(Get-RecordedProcessState $oldRuntime.api).State;dashboard=(Get-RecordedProcessState $oldRuntime.dashboard).State}
    $changes = @{
        edge = $edgeRecord
        processStates = $observedStates
        runtimeRunning = (@($observedStates.Values | Where-Object { $_ -eq 'Owned' }).Count -gt 0)
        deploymentStatus = $Status
        failurePhase = $(if ($Status -eq 'Failed') { $Phase } else { $null })
        updatedAtUtc = [DateTime]::UtcNow.ToString('o')
        edgeRestart = [ordered]@{
            schemaVersion='1.0';attemptId=$attempt;status=$Status;phase=$Phase
            oldEdge=$oldRuntime.edge
            terminationConfirmed=$terminationConfirmed
            replacementPublished=$replacementPublished
            launchIntentUnresolved=(Test-Path -LiteralPath $intentPath)
            replacementObservation=$(if ($null -ne $replacement) {
                [ordered]@{record=$replacement;state=(Get-RecordedProcessState $replacement).State}
            } else { $null })
            terminationObservations=$script:TerminationObservations.ToArray()
        }
    }
    foreach ($name in $changes.Keys) { $value | Add-Member -MemberType NoteProperty -Name $name -Value $changes[$name] -Force }
    return $value
}
function Assert-PreservedRuntime {
    foreach ($name in @('api','dashboard')) {
        if ((Get-RecordedProcessState $oldRuntime.$name).State -ne 'Owned') {
            throw "Preserved $name ownership verification failed."
        }
    }
    Wait-HttpOk $apiHealth $HealthTimeoutSeconds
    Wait-HttpOk $dashboardLive $HealthTimeoutSeconds
    Wait-HttpOk $dashboardReady $HealthTimeoutSeconds
    # Health waiting must not conceal a process exit or PID reuse.
    foreach ($name in @('api','dashboard')) {
        if ((Get-RecordedProcessState $oldRuntime.$name).State -ne 'Owned') {
            throw "Preserved $name ownership changed during health verification."
        }
    }
}

$root = Get-CanonicalPath $InstallRoot
$deployment = Join-Path $root 'deployment'
$runtimePath = Join-Path $deployment 'runtime.json'
$intentPath = Join-Path $deployment 'runtime-start.intent.json'
$lockPath = Join-Path $deployment 'deployment.lock'
$current = Join-Path $root 'current'
$lock = $null
$attempt = 'edge-restart-' + [Guid]::NewGuid().ToString('N')
$oldRuntime = $null
$edgeRecord = $null
$edgeState = 'Unproven'
$terminationConfirmed = $false
$replacementPublished = $false
$transitionPublished = $false
$replacement = $null
$phase = 'Preflight'
try {
    # No directory/config/release mutation is needed to open the existing lock.
    try {
        $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    } catch { throw "Another FactoryConnect operation holds the deployment lock, or it is unavailable: $lockPath" }
    if (Test-Path -LiteralPath $intentPath) {
        throw 'Unresolved runtime-start intent requires explicit reconciliation before Edge restart.'
    }
    if (Test-Path -LiteralPath (Join-Path $deployment 'moxa-maintenance.intent.json')) { throw 'Unresolved MOXA maintenance blocks restart.' }
    $moxaPlan = $null
    if ($MoxaRecoveryPlan) {
        . (Join-Path $PSScriptRoot 'FactoryConnect.MoxaMaintenance.ps1')
        $moxaPlan = Read-JsonFile $MoxaRecoveryPlan
    }
    $oldRuntime = Read-JsonFile $runtimePath
    if ([string]$oldRuntime.deploymentStatus -cne 'Succeeded') {
        throw 'Edge restart requires a successfully published runtime; interrupted/failed operations require explicit reconciliation.'
    }
    if ($null -ne $oldRuntime.PSObject.Properties['edgeRestart'] -and
        [string]$oldRuntime.edgeRestart.status -cne 'Succeeded') {
        throw 'Unresolved Edge restart transition requires explicit reconciliation.'
    }
    $item = Get-Item -LiteralPath $current -Force
    $targets = @($item.Target)
    if ([string]$item.LinkType -cne 'Junction' -or $targets.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$targets[0])) {
        throw 'Selected release must be exactly one junction target.'
    }
    $release = Get-CanonicalPath $(if ([IO.Path]::IsPathRooted([string]$targets[0])) {
        [string]$targets[0]
    } else { Join-Path $root ([string]$targets[0]) })
    $releaseRoot = Get-CanonicalPath (Join-Path $root 'releases')
    $prefix = $releaseRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $release.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Selected release is outside the releases root.'
    }
    $releaseId = $release.Substring($prefix.Length)
    if ($releaseId -cnotmatch '^[0-9a-f]{40}$' -or $releaseId.Contains('/') -or $releaseId.Contains('\')) {
        throw 'Selected release identity is invalid or nested.'
    }
    if ([string]$oldRuntime.selectedRelease -cne $releaseId -or
        (Get-CanonicalPath ([string]$oldRuntime.releasePath)) -ne $release) {
        throw 'Selected release does not match durable runtime ownership.'
    }
    $paths = [ordered]@{}
    $cfg = [ordered]@{}
    $expected = [ordered]@{}
    foreach ($name in @('edge','api','dashboard')) {
        $paths[$name] = Join-Path $root "config/$name.production.json"
        Assert-NoPlaceholders $paths[$name] $name
        $cfg[$name] = Read-JsonFile $paths[$name]
        $role = @{edge='Edge';api='Api';dashboard='Dashboard'}[$name]
        $expected[$name] = Join-Path $release "apps/$name/FactoryConnect.$role.exe"
        if (-not (Test-Path -LiteralPath $expected[$name] -PathType Leaf)) {
            throw "Selected release executable is missing for $name."
        }
        $record = $oldRuntime.$name
        if ($null -eq $record -or [string]$record.name -cne $name -or
            (Get-CanonicalPath ([string]$record.executablePath)) -ne (Get-CanonicalPath $expected[$name]) -or
            (Get-RecordedProcessState $record).State -ne 'Owned') {
            throw "Runtime identity mismatch or absence for $name; no process will be terminated."
        }
    }
    Assert-SiteConfiguration $cfg.edge $cfg.api $cfg.dashboard
    $edgeEnvironment = Get-ConfigurationEnvironment $paths.edge
    $apiHealth = (Get-BaseAddressFromConfiguration $cfg.api 'API') + '/health'
    $dashboardBase = Get-BaseAddressFromConfiguration $cfg.dashboard 'Dashboard'
    $dashboardLive = $dashboardBase + '/health/live'
    $dashboardReady = $dashboardBase + '/health/ready'
    Assert-PreservedRuntime
    # Revalidate Edge after the health waits, before publishing any transition.
    if ((Get-RecordedProcessState $oldRuntime.edge).State -ne 'Owned') {
        throw 'Edge ownership changed during preflight.'
    }
    $attemptLog = Join-Path $deployment "logs/$attempt"
    New-Item -ItemType Directory -Path $attemptLog -Force | Out-Null
    if ($null -ne $moxaPlan) { Initialize-MoxaMaintenance $moxaPlan $root $releaseId $oldRuntime $attemptLog }
    $edgeRecord = $oldRuntime.edge
    # A transition is an interruption marker, not a running/absence claim.
    $edgeState = 'Unproven'
    $phase = 'Termination'
    Write-JsonFileAtomic $runtimePath (New-EdgeRestartRuntime 'EdgeRestarting' $phase)
    $transitionPublished = $true
    Stop-RecordedOwnedProcess $oldRuntime.edge
    $terminationConfirmed = $true
    $edgeRecord = $null
    $edgeState = 'Absent'
    $phase = 'AbsencePublication'
    Write-JsonFileAtomic $runtimePath (New-EdgeRestartRuntime 'EdgeRestarting' $phase)

    if ($null -ne $moxaPlan) {
        $phase = 'MoxaRecovery'
        Invoke-MoxaMaintenanceCorrection $moxaPlan
        $edgeEnvironment = Get-ConfigurationEnvironment $paths.edge
    }

    $phase = 'LaunchIntent'
    $intent = [ordered]@{
        schemaVersion='1.0';attemptId=$attempt;processName='edge'
        operation='EdgeRestart';selectedRelease=$releaseId
        executablePath=(Get-CanonicalPath $expected.edge)
        createdAtUtc=[DateTime]::UtcNow.ToString('o')
    }
    Write-JsonFileAtomic $intentPath $intent
    $phase = 'Launch'
    # Until durable ownership is published, this local record cannot become a
    # runtime ownership claim, including in the failure handler.
    $replacement = Start-OwnedProcess 'edge' $expected.edge $edgeEnvironment $attemptLog
    $phase = 'Stabilization'
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') {
        throw 'Replacement Edge identity verification failed.'
    }
    Start-Sleep -Seconds $EdgeStabilizationSeconds
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') {
        throw 'Replacement Edge did not survive stabilization with expected ownership.'
    }
    $phase = 'PreservationVerification'
    Assert-PreservedRuntime
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') {
        throw 'Replacement Edge ownership changed during preservation verification.'
    }
    $phase = 'OwnershipPublication'
    $edgeRecord = $replacement
    $edgeState = 'Owned'
    # Publication includes the truthful claim that this write publishes ownership;
    # failure resets the local claim before writing any failure evidence.
    $replacementPublished = $true
    try { Write-JsonFileAtomic $runtimePath (New-EdgeRestartRuntime 'EdgeRestarting' $phase) }
    catch {
        $replacementPublished = $false
        $edgeRecord = $null
        $edgeState = 'Unproven'
        throw
    }
    $phase = 'IntentClear'
    $liveIntent = Read-JsonFile $intentPath
    if ([string]$liveIntent.attemptId -cne $attempt -or [string]$liveIntent.processName -cne 'edge') {
        throw 'Launch intent identity changed; reconciliation is required.'
    }
    Remove-Item -LiteralPath $intentPath -Force
    $phase = 'FinalVerification'
    Assert-PreservedRuntime
    if ((Get-RecordedProcessState $edgeRecord).State -ne 'Owned') {
        throw 'Replacement Edge ownership changed before completion.'
    }
    Write-JsonFileAtomic $runtimePath (New-EdgeRestartRuntime 'Succeeded' 'Complete')
    if ($null -ne $moxaPlan) {
        $script:MoxaEvidence.phase = 'RestartedAcceptancePending'
        Write-JsonFileAtomic $script:MoxaEvidencePath $script:MoxaEvidence
        Remove-Item -LiteralPath $script:MoxaMarkerPath -Force
    }
    [pscustomobject]@{Status='Succeeded';Operation='EdgeRestart';AttemptId=$attempt;SourceCommit=$releaseId;InstallRoot=$root;EdgePid=$edgeRecord.pid}
} catch {
    # Never stop API/Dashboard, never launch a second replacement, and never
    # clear an intent on failure. Unknown launches need explicit reconciliation.
    $primary = $_
    if ($transitionPublished) {
        if (-not $replacementPublished) {
            $edgeRecord = $(if ($terminationConfirmed) { $null } else { $oldRuntime.edge })
            $edgeState = $(if ($terminationConfirmed -and ($phase -in @('AbsencePublication','LaunchIntent') -or
                ($null -ne $replacement -and (Get-RecordedProcessState $replacement).State -eq 'Absent'))) { 'Absent' } else { 'Unproven' })
        } else {
            $edgeState = (Get-RecordedProcessState $edgeRecord).State
        }
        try { Write-JsonFileAtomic $runtimePath (New-EdgeRestartRuntime 'Failed' $phase) }
        catch { Write-Warning "Failure evidence publication failed; retain the transition/intent and reconcile explicitly: $($_.Exception.Message)" }
    }
    throw $primary
} finally { if ($null -ne $lock) { $lock.Dispose() } }
