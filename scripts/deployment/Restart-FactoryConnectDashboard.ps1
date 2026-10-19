# Explicit Dashboard-only restart. Never stops acquisition or performs stack reconciliation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][Guid]$MachineId,
    [Parameter(Mandatory = $true)][string]$ExpectedProcessorId,
    [Parameter()][ValidateRange(1,300)][int]$DashboardStabilizationSeconds = 5,
    [Parameter()][ValidateRange(1,300)][int]$HealthTimeoutSeconds = 60,
    [Parameter()][ValidateRange(1,600)][int]$ShutdownTimeoutSeconds
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'FactoryConnect.ProcessTermination.ps1')
if (-not $PSBoundParameters.ContainsKey('ShutdownTimeoutSeconds')) {
    $ShutdownTimeoutSeconds = $script:FactoryConnectShutdownTimeoutDefaultSeconds
}
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
function Start-OwnedProcess {
    param([string]$Name,[string]$Executable,[hashtable]$Environment,[string]$Log)
    $saved=@{}
    try {
        # Removed source-array entries must not survive through an operator shell's inherited environment.
        foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
            $key=[string]$entry.Key
            if ($key -match '^(Dashboard(__|:)|Urls$|ASPNETCORE_URLS$|DOTNET_URLS$)') {
                $saved[$key]=[string]$entry.Value
                [Environment]::SetEnvironmentVariable($key,$null,'Process')
            }
        }
        foreach ($entry in $Environment.GetEnumerator()) {
            if (-not $saved.ContainsKey($entry.Key)) { $saved[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process') }
            [Environment]::SetEnvironmentVariable($entry.Key,[string]$entry.Value,'Process')
        }
        foreach ($nameKey in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) {
            if (-not $saved.ContainsKey($nameKey)) { $saved[$nameKey]=[Environment]::GetEnvironmentVariable($nameKey,'Process') }
            [Environment]::SetEnvironmentVariable($nameKey,'Production','Process')
        }
        $process=Start-Process -FilePath $Executable -WorkingDirectory (Split-Path $Executable -Parent) -PassThru `
            -RedirectStandardOutput (Join-Path $Log "$Name.out.log") -RedirectStandardError (Join-Path $Log "$Name.err.log")
        return [ordered]@{name=$Name;pid=$process.Id;executablePath=[IO.Path]::GetFullPath($Executable);startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o')}
    } finally {
        foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    }
}

function Assert-DashboardConfiguration {
    param($Dashboard)
    Assert-SingleHostUrl $Dashboard.Urls 'Dashboard Urls'
    [void](Assert-AbsoluteHttpUri $Dashboard.Dashboard.ReportingApiBaseAddress 'Dashboard.ReportingApiBaseAddress')
    Assert-PositiveDuration $Dashboard.Dashboard.RequestTimeout 'Dashboard.RequestTimeout'
    $sources=@($Dashboard.Dashboard.Sources)
    if ($sources.Count -lt 1) { throw 'Dashboard must configure at least one source.' }
    $seen=@{}
    foreach ($source in $sources) {
        foreach ($field in @('MachineId','ProcessorId','SiteId','ProductionLineId','DisplayName','GroupName')) {
            Assert-NonEmpty $source.$field "Dashboard source $field"
        }
        $id=[Guid]::Parse([string]$source.MachineId).ToString('D')
        if ($seen.ContainsKey($id)) { throw 'Dashboard source machine identities must be unique.' }
        $seen[$id]=$true
        $prefix="operational-metrics:$id`:"
        if (-not ([string]$source.ProcessorId).StartsWith($prefix,[StringComparison]::Ordinal) -or
            ([string]$source.ProcessorId).Length -eq $prefix.Length) {
            throw 'Dashboard source must use an operational metric processor for its exact machine.'
        }
    }
    $matching=@($sources | Where-Object { [Guid]::Parse([string]$_.MachineId) -eq $MachineId })
    if ($matching.Count -ne 1 -or [string]$matching[0].ProcessorId -cne $ExpectedProcessorId) {
        throw 'Dashboard configuration does not match the explicitly expected machine and processor identity.'
    }
}
function New-DashboardRestartRuntime {
    param([string]$Status,[string]$Phase)
    $value=$oldRuntime | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $states=[ordered]@{edge=(Get-RecordedProcessState $oldRuntime.edge).State;api=(Get-RecordedProcessState $oldRuntime.api).State;dashboard=$dashboardState}
    # Preserve extension properties and all unrelated role records/state.
    $processStates=if ($null -ne $value.PSObject.Properties['processStates']) { $value.processStates } else { [pscustomobject]@{} }
    foreach ($name in $states.Keys) { $processStates | Add-Member -MemberType NoteProperty -Name $name -Value $states[$name] -Force }
    $changes=@{
        dashboard=$dashboardRecord;processStates=$processStates
        runtimeRunning=(@($states.Values | Where-Object { $_ -eq 'Owned' }).Count -gt 0)
        deploymentStatus=$Status;failurePhase=$(if ($Status -eq 'Failed') { $Phase } else { $null })
        updatedAtUtc=[DateTime]::UtcNow.ToString('o')
        dashboardRestart=[ordered]@{
            schemaVersion='1.0';attemptId=$attempt;status=$Status;phase=$Phase
            machineId=$MachineId.ToString('D');expectedProcessorId=$ExpectedProcessorId
            configurationSha256=$configurationHash;oldDashboard=$oldRuntime.dashboard
            terminationConfirmed=$terminationConfirmed;replacementPublished=$replacementPublished
            launchIntentUnresolved=(Test-Path -LiteralPath $intentPath)
            replacementObservation=$(if ($null -ne $replacement) { [ordered]@{record=$replacement;state=(Get-RecordedProcessState $replacement).State} } else { $null })
            terminationObservations=$script:TerminationObservations.ToArray()
        }
    }
    foreach ($name in $changes.Keys) { $value | Add-Member -MemberType NoteProperty -Name $name -Value $changes[$name] -Force }
    return $value
}
function Assert-PreservedRuntime {
    foreach ($name in @('edge','api')) {
        if ((Get-RecordedProcessState $oldRuntime.$name).State -ne 'Owned') { throw "Preserved $name ownership verification failed." }
    }
    Wait-HttpOk $apiHealth $HealthTimeoutSeconds
    foreach ($name in @('edge','api')) {
        if ((Get-RecordedProcessState $oldRuntime.$name).State -ne 'Owned') { throw "Preserved $name ownership changed during health verification." }
    }
}
function Assert-ReplacementDashboard {
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') { throw 'Replacement Dashboard identity verification failed.' }
    Wait-HttpOk $dashboardLive $HealthTimeoutSeconds
    Wait-HttpOk $dashboardReady $HealthTimeoutSeconds
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') { throw 'Replacement Dashboard ownership changed during health verification.' }
}

$root = Get-CanonicalPath $InstallRoot
$deployment=Join-Path $root 'deployment'
$runtimePath=Join-Path $deployment 'runtime.json'
$intentPath=Join-Path $deployment 'runtime-start.intent.json'
$lockPath=Join-Path $deployment 'deployment.lock'
$current=Join-Path $root 'current'
$lock=$null
$attempt='dashboard-restart-'+[Guid]::NewGuid().ToString('N')
$oldRuntime=$null;$dashboardRecord=$null;$dashboardState='Unproven'
$terminationConfirmed=$false;$replacementPublished=$false;$transitionPublished=$false
$replacement=$null;$configurationHash=$null;$phase='Preflight'
try {
    try { $lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch { throw "Another FactoryConnect operation holds the deployment lock, or it is unavailable: $lockPath" }
    if (Test-Path -LiteralPath $intentPath) { throw 'Unresolved runtime-start intent requires explicit reconciliation before Dashboard restart.' }
    $oldRuntime=Read-JsonFile $runtimePath
    if ([string]$oldRuntime.deploymentStatus -cne 'Succeeded') { throw 'Dashboard restart requires a successfully published runtime; reconcile interrupted operations first.' }
    foreach ($operation in @('edgeRestart','dashboardRestart')) {
        if ($null -ne $oldRuntime.PSObject.Properties[$operation] -and [string]$oldRuntime.$operation.status -cne 'Succeeded') {
            throw "Unresolved $operation transition requires explicit reconciliation."
        }
    }
    $item=Get-Item -LiteralPath $current -Force
    $targets=@($item.Target)
    if ([string]$item.LinkType -cne 'Junction' -or $targets.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$targets[0])) {
        throw 'Selected release must be exactly one junction target.'
    }
    $release=Get-CanonicalPath $(if ([IO.Path]::IsPathRooted([string]$targets[0])) { [string]$targets[0] } else { Join-Path $root ([string]$targets[0]) })
    $prefix=(Get-CanonicalPath (Join-Path $root 'releases'))+[IO.Path]::DirectorySeparatorChar
    if (-not $release.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Selected release is outside the releases root.' }
    $releaseId=$release.Substring($prefix.Length)
    if ($releaseId -cnotmatch '^[0-9a-f]{40}$') { throw 'Selected release identity is invalid or nested.' }
    if ([string]$oldRuntime.selectedRelease -cne $releaseId -or (Get-CanonicalPath ([string]$oldRuntime.releasePath)) -ne $release) {
        throw 'Selected release does not match durable runtime ownership.'
    }
    $expected=[ordered]@{}
    foreach ($name in @('edge','api','dashboard')) {
        $role=@{edge='Edge';api='Api';dashboard='Dashboard'}[$name]
        $expected[$name]=Join-Path $release "apps/$name/FactoryConnect.$role.exe"
        if (-not (Test-Path -LiteralPath $expected[$name] -PathType Leaf)) { throw "Selected release executable is missing for $name." }
        $record=$oldRuntime.$name
        if ($null -eq $record -or [string]$record.name -cne $name -or
            (Get-CanonicalPath ([string]$record.executablePath)) -ne (Get-CanonicalPath $expected[$name]) -or
            (Get-RecordedProcessState $record).State -ne 'Owned') {
            throw "Runtime identity mismatch or absence for $name; no process will be terminated."
        }
    }
    $dashboardPath=Join-Path $root 'config/dashboard.production.json'
    $apiPath=Join-Path $root 'config/api.production.json'
    Assert-NoPlaceholders $dashboardPath 'Dashboard'
    $configurationHash=(Get-FileHash -LiteralPath $dashboardPath -Algorithm SHA256).Hash
    $dashboardConfiguration=Read-JsonFile $dashboardPath
    Assert-DashboardConfiguration $dashboardConfiguration
    # Freeze the validated object; do not reread potentially edited configuration for launch.
    $dashboardEnvironment=@{}
    Add-ConfigurationEnvironment $dashboardConfiguration '' $dashboardEnvironment
    $apiHealth=(Get-BaseAddressFromConfiguration (Read-JsonFile $apiPath) 'API')+'/health'
    $dashboardBase=Get-BaseAddressFromConfiguration $dashboardConfiguration 'Dashboard'
    $dashboardLive=$dashboardBase+'/health/live';$dashboardReady=$dashboardBase+'/health/ready'
    Assert-PreservedRuntime
    if ((Get-RecordedProcessState $oldRuntime.dashboard).State -ne 'Owned') { throw 'Dashboard ownership changed during preflight.' }
    if ((Get-FileHash -LiteralPath $dashboardPath -Algorithm SHA256).Hash -cne $configurationHash) { throw 'Dashboard configuration changed during preflight.' }
    $attemptLog=Join-Path $deployment "logs/$attempt"
    New-Item -ItemType Directory -Path $attemptLog -Force | Out-Null
    $dashboardRecord=$oldRuntime.dashboard;$phase='Termination'
    Write-JsonFileAtomic $runtimePath (New-DashboardRestartRuntime 'DashboardRestarting' $phase)
    $transitionPublished=$true
    Stop-RecordedOwnedProcess $oldRuntime.dashboard
    $terminationConfirmed=$true;$dashboardRecord=$null;$dashboardState='Absent';$phase='AbsencePublication'
    Write-JsonFileAtomic $runtimePath (New-DashboardRestartRuntime 'DashboardRestarting' $phase)
    Assert-PreservedRuntime
    $phase='LaunchIntent'
    Write-JsonFileAtomic $intentPath ([ordered]@{
        schemaVersion='1.0';attemptId=$attempt;processName='dashboard';operation='DashboardRestart'
        selectedRelease=$releaseId;executablePath=(Get-CanonicalPath $expected.dashboard)
        configurationSha256=$configurationHash;createdAtUtc=[DateTime]::UtcNow.ToString('o')
    })
    $phase='Launch'
    $replacement=Start-OwnedProcess 'dashboard' $expected.dashboard $dashboardEnvironment $attemptLog
    $phase='Stabilization'
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') { throw 'Replacement Dashboard identity verification failed.' }
    Start-Sleep -Seconds $DashboardStabilizationSeconds
    $phase='HealthVerification'
    Assert-ReplacementDashboard
    Assert-PreservedRuntime
    if ((Get-RecordedProcessState $replacement).State -ne 'Owned') { throw 'Replacement Dashboard ownership changed before publication.' }
    $phase='OwnershipPublication';$dashboardRecord=$replacement;$dashboardState='Owned';$replacementPublished=$true
    try { Write-JsonFileAtomic $runtimePath (New-DashboardRestartRuntime 'DashboardRestarting' $phase) }
    catch { $replacementPublished=$false;$dashboardRecord=$null;$dashboardState='Unproven';throw }
    $phase='IntentClear'
    $liveIntent=Read-JsonFile $intentPath
    if ([string]$liveIntent.attemptId -cne $attempt -or [string]$liveIntent.processName -cne 'dashboard' -or [string]$liveIntent.operation -cne 'DashboardRestart') {
        throw 'Launch intent identity changed; reconciliation is required.'
    }
    Remove-Item -LiteralPath $intentPath -Force
    $phase='FinalVerification'
    Assert-ReplacementDashboard
    Assert-PreservedRuntime
    Write-JsonFileAtomic $runtimePath (New-DashboardRestartRuntime 'Succeeded' 'Complete')
    [pscustomobject]@{Status='Succeeded';Operation='DashboardRestart';AttemptId=$attempt;SourceCommit=$releaseId;InstallRoot=$root;DashboardPid=$dashboardRecord.pid}
} catch {
    # No preserved-role stop, no automatic second launch, no intent clearing on failure.
    $primary=$_
    if ($transitionPublished) {
        if (-not $replacementPublished) {
            $dashboardRecord=$(if ($terminationConfirmed) { $null } else { $oldRuntime.dashboard })
            $dashboardState=$(if ($terminationConfirmed -and ($phase -in @('AbsencePublication','LaunchIntent') -or
                ($null -ne $replacement -and (Get-RecordedProcessState $replacement).State -eq 'Absent'))) { 'Absent' } else { 'Unproven' })
        } else { $dashboardState=(Get-RecordedProcessState $dashboardRecord).State }
        try { Write-JsonFileAtomic $runtimePath (New-DashboardRestartRuntime 'Failed' $phase) }
        catch { Write-Warning "Failure evidence publication failed; retain transition/intent and reconcile explicitly: $($_.Exception.Message)" }
    }
    throw $primary
} finally { if ($null -ne $lock) { $lock.Dispose() } }
