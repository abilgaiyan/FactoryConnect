[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter()][ValidateRange(1,300)][int]$EdgeStabilizationSeconds = 5,
    [Parameter()][ValidateRange(1,300)][int]$HealthTimeoutSeconds = 60,
    [Parameter()][ValidateRange(1,600)][int]$ShutdownTimeoutSeconds
)
$ErrorActionPreference='Stop'
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
function New-RuntimeState { param([string]$ReleaseId,[string]$ReleasePath,[string]$Attempt,[string]$Status,$Records,$States,[string]$FailurePhase=$null);
$running=@($States.Values|Where-Object{$_-eq'Owned'}).Count-gt0;
[ordered]@{schemaVersion='1.0';
selectedRelease=$ReleaseId;
releasePath=$ReleasePath;
deploymentAttemptId=$Attempt;
deploymentStatus=$Status;
updatedAtUtc=[DateTime]::UtcNow.ToString('o');
runtimeRunning=$running;
failurePhase=$FailurePhase;
migrationOutcome='NotRun';
databaseMayHaveChanged=$false;
processStates=$States;
terminationObservations=$script:TerminationObservations.ToArray();
edge=$Records.edge;
api=$Records.api;
dashboard=$Records.dashboard} }

$root=Get-CanonicalPath $InstallRoot;
$deployment=Join-Path $root 'deployment';
$logs=Join-Path $deployment 'logs';
$config=Join-Path $root 'config';
$releases=Join-Path $root 'releases';
$current=Join-Path $root 'current';
$runtimePath=Join-Path $deployment 'runtime.json';
$intentPath=Join-Path $deployment 'runtime-start.intent.json';
$lockPath=Join-Path $deployment 'deployment.lock';
New-Item -ItemType Directory -Force -Path $deployment,$logs|Out-Null;
$lock=$null;
$attempt=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ');
$attemptLog=Join-Path $logs $attempt;
New-Item -ItemType Directory -Force -Path $attemptLog|Out-Null;
$records=[ordered]@{edge=$null;
api=$null;
dashboard=$null};
$states=[ordered]@{edge='Absent';
api='Absent';
dashboard='Absent'};
$startedThisAttempt=[ordered]@{edge=$false;
api=$false;
dashboard=$false};
$oldRuntime=$null;
$runtimeLoaded=$false;
$mutationStarted=$false;
$release=$null;
$releaseId=$null;
$failurePhase='Preflight';
$intentOwnedByThisAttempt=$false;
$intentProcessRecord=$null
try{
 try{$lock=[System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None)}catch{throw "Another FactoryConnect deployment/runtime-start operation holds '$lockPath'."}
 $oldRuntime=if(Test-Path -LiteralPath $runtimePath){Read-JsonFile $runtimePath}else{$null};
$runtimeLoaded=$true
 if(Test-Path -LiteralPath $intentPath){$intent=Read-JsonFile $intentPath;
throw "Previous runtime-start attempt may have been interrupted before durable process publication (attempt '$($intent.attemptId)', process '$($intent.processName)'). Manual reconciliation is required before startup."}
 if(-not(Test-Path -LiteralPath $current)){throw "Selected release junction is missing: $current"};
$ci=Get-Item -LiteralPath $current -Force;
$target=@($ci.Target);
if([string]$ci.LinkType-cne'Junction'-or$target.Count-ne1-or[string]::IsNullOrWhiteSpace([string]$target[0])){throw "Selected release must be exactly one junction target: $current"};
$release=Get-CanonicalPath $(if([System.IO.Path]::IsPathRooted([string]$target[0])){[string]$target[0]}else{Join-Path (Split-Path $current -Parent) ([string]$target[0])});
$rr=Get-CanonicalPath $releases;
$prefix=$rr+'\';
if(-not$release.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw "Selected release is outside releases root: $release"};
$relative=$release.Substring($prefix.Length);
if([string]::IsNullOrWhiteSpace($relative)-or$relative.Contains('\')-or$relative.Contains('/')-or$relative-cnotmatch'^[0-9a-f]{40}$'){throw "Selected release identity is invalid or nested: $release"};
$releaseId=$relative
 $paths=[ordered]@{edge=Join-Path $config 'edge.production.json';
api=Join-Path $config 'api.production.json';
dashboard=Join-Path $config 'dashboard.production.json'};
foreach($n in $paths.Keys){if(-not(Test-Path -LiteralPath $paths[$n]-PathType Leaf)){throw "Commissioned configuration is required: $($paths[$n])"};
Assert-NoPlaceholders $paths[$n] $n};
$cfg=[ordered]@{edge=Read-JsonFile $paths.edge;
api=Read-JsonFile $paths.api;
dashboard=Read-JsonFile $paths.dashboard};
Assert-SiteConfiguration $cfg.edge $cfg.api $cfg.dashboard;
$envs=[ordered]@{edge=Get-ConfigurationEnvironment $paths.edge;
api=Get-ConfigurationEnvironment $paths.api;
dashboard=Get-ConfigurationEnvironment $paths.dashboard};
$apiBase=Get-BaseAddressFromConfiguration $cfg.api 'API';
$dashBase=Get-BaseAddressFromConfiguration $cfg.dashboard 'Dashboard';
$apiHealth="$apiBase/health";
$live="$dashBase/health/live";
$ready="$dashBase/health/ready"
 $expected=[ordered]@{edge=Join-Path $release 'apps/edge/FactoryConnect.Edge.exe';
api=Join-Path $release 'apps/api/FactoryConnect.Api.exe';
dashboard=Join-Path $release 'apps/dashboard/FactoryConnect.Dashboard.exe'};
foreach($n in $expected.Keys){if(-not(Test-Path -LiteralPath $expected[$n]-PathType Leaf)){throw "Selected release executable is missing: $($expected[$n])"}}
 foreach($n in @('edge','api','dashboard')){$r=if($null-ne$oldRuntime){$oldRuntime.$n}else{$null};
if($null-ne$r-and[System.IO.Path]::GetFullPath([string]$r.executablePath)-ne[System.IO.Path]::GetFullPath([string]$expected[$n])){$records[$n]=$r;
$states[$n]='Mismatch';
continue};
$records[$n]=$r;
$states[$n]=(Get-RecordedProcessState $r).State};
if(@($states.Values|Where-Object{$_-eq'Mismatch'}).Count-gt0){throw 'Runtime identity mismatch detected; no process will be stopped or started.'}
 $allOwned=@($states.Values|Where-Object{$_-eq'Owned'}).Count-eq3;
if($allOwned-and(Test-HttpOk $apiHealth)-and(Test-HttpOk $live)-and(Test-HttpOk $ready)){[pscustomobject]@{Status='AlreadyRunning';
SourceCommit=$releaseId;
InstallRoot=$root;
Current=$current};
return}
 $failurePhase='Reconciliation';
$mutationStarted=$true;
foreach($n in @('dashboard','api','edge')){if($states[$n]-eq'Owned'){Stop-RecordedOwnedProcess $records[$n];
$records[$n]=$null;
$states[$n]='Absent'}}
 $failurePhase='Startup';
Write-JsonFileAtomic $runtimePath (New-RuntimeState $releaseId $release $attempt 'Starting' $records $states)
 foreach($n in @('edge','api','dashboard')){
   $intent=[ordered]@{schemaVersion='1.0';
attemptId=$attempt;
processName=$n;
selectedRelease=$releaseId;
executablePath=[System.IO.Path]::GetFullPath([string]$expected[$n]);
createdAtUtc=[DateTime]::UtcNow.ToString('o')};
Write-JsonFileAtomic $intentPath $intent;
$intentOwnedByThisAttempt=$true
   $intentProcessRecord=$null;
$records[$n]=Start-OwnedProcess $n $expected[$n] $envs[$n] $attemptLog;
$intentProcessRecord=$records[$n];
$states[$n]='Owned';
$startedThisAttempt[$n]=$true;
Write-JsonFileAtomic $runtimePath (New-RuntimeState $releaseId $release $attempt 'Starting' $records $states);
Remove-Item -LiteralPath $intentPath -Force;
$intentOwnedByThisAttempt=$false
   if($n-eq'edge'){Start-Sleep -Seconds $EdgeStabilizationSeconds;
if((Get-RecordedProcessState $records.edge).State-ne'Owned'){throw 'Edge did not survive stabilization with expected ownership.'}}
   elseif($n-eq'api'){Wait-HttpOk $apiHealth $HealthTimeoutSeconds}
   else{Wait-HttpOk $live $HealthTimeoutSeconds;
Wait-HttpOk $ready $HealthTimeoutSeconds}
 }
 $failurePhase='FinalVerification';
foreach($n in @('edge','api','dashboard')){if((Get-RecordedProcessState $records[$n]).State-ne'Owned'){throw "Final ownership verification failed for $n."}};
Wait-HttpOk $apiHealth $HealthTimeoutSeconds;
Wait-HttpOk $live $HealthTimeoutSeconds;
Wait-HttpOk $ready $HealthTimeoutSeconds;
$states=[ordered]@{edge='Owned';
api='Owned';
dashboard='Owned'};
Write-JsonFileAtomic $runtimePath (New-RuntimeState $releaseId $release $attempt 'Succeeded' $records $states);
[pscustomobject]@{Status='Succeeded';
SourceCommit=$releaseId;
InstallRoot=$root;
Current=$current}
}catch{$primary=$_.Exception.Message;
$cleanup=@();
foreach($n in @('dashboard','api','edge')){if(-not$startedThisAttempt[$n]-or$states[$n]-ne'Owned'-or$null-eq$records[$n]){continue};
try{Stop-RecordedOwnedProcess $records[$n];
$records[$n]=$null;
$states[$n]='Absent'}catch{$cleanup+="$n cleanup failed: $($_.Exception.Message)"}};
# An unknown post-launch outcome must retain its barrier. A record is required
# even for absence proof; failure to obtain StartTime is not proof of no child.
if ($intentOwnedByThisAttempt -and $null -ne $lock -and $null -ne $intentProcessRecord -and (Test-Path -LiteralPath $intentPath)) {
    try {
        $liveIntent = Read-JsonFile $intentPath
        if ([string]$liveIntent.attemptId -eq $attempt -and
            (Get-RecordedProcessState $intentProcessRecord).State -eq 'Absent') {
            Remove-Item -LiteralPath $intentPath -Force
            $intentOwnedByThisAttempt = $false
        }
    } catch { # Preserve the intent when absence cannot be established.
    }
};
foreach($n in @('edge','api','dashboard')){if($null-ne$records[$n]){$states[$n]=(Get-RecordedProcessState $records[$n]).State;if($states[$n]-eq'Absent'){$records[$n]=$null}}};
if($mutationStarted-and$runtimeLoaded-and$null-ne$releaseId){try{Write-JsonFileAtomic $runtimePath (New-RuntimeState $releaseId $release $attempt 'Failed' $records $states $failurePhase)}catch{}};
if($cleanup.Count-gt0){throw "$primary Cleanup: $($cleanup -join '; ')"};
throw
}finally{if($null-ne$lock){$lock.Dispose()}}
