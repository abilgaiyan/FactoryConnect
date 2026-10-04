[CmdletBinding()]
param(
    [string]$StartupScript = (Join-Path $PSScriptRoot '..\..\scripts\deployment\Start-FactoryConnectRuntime.ps1')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Executable RBS acceptance harness. It never targets a caller-supplied FactoryConnect
# installation. Every scenario owns a fresh disposable root below $env:TEMP.
$StartupScript = [IO.Path]::GetFullPath($StartupScript)
if (-not (Test-Path -LiteralPath $StartupScript -PathType Leaf)) { throw "Startup script not found: $StartupScript" }
$releaseId = '1111111111111111111111111111111111111111'
$results = [ordered]@{}
$roots = [Collections.Generic.List[string]]::new()
$processes = [Collections.Generic.List[int]]::new()

function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-Case([string]$Id,[scriptblock]$Body) {
    try { & $Body; $script:results[$Id] = 'PASS' }
    catch { $script:results[$Id] = "FAIL: $($_.Exception.Message)"; throw }
}
function New-Root([string]$Name) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("FactoryConnect-RBS-$Name-" + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Force -Path $root,(Join-Path $root 'config'),(Join-Path $root 'deployment'),(Join-Path $root 'releases')
    $release = Join-Path $root "releases\$releaseId"
    foreach($p in @('apps\edge','apps\api','apps\dashboard')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $release $p) }
    $current = Join-Path $root 'current'
    cmd /c "mklink /J `"$current`" `"$release`"" | Out-Null
    $roots.Add($root)
    return $root
}
function Copy-DummyExecutables([string]$Root) {
    # powershell.exe is copied/renamed so Path identity is deterministic while the
    # dummy processes remain harmless and local to the disposable root.
    $source = (Get-Command powershell.exe).Source
    foreach($n in @('edge','api','dashboard')) {
        $dest = Join-Path $Root "releases\$releaseId\apps\$n\FactoryConnect.$(if($n-eq'edge'){'Edge'}elseif($n-eq'api'){'Api'}else{'Dashboard'}).exe"
        Copy-Item -LiteralPath $source -Destination $dest
    }
}
function Write-Config([string]$Root,[int]$ApiPort,[int]$DashboardPort,[bool]$Valid=$true) {
    $edge = [ordered]@{
        Persistence=@{Provider='SqlServer'};PersistenceProviders=@{SqlServer=@{ConnectionString='Server=(local);Database=Disposable;Integrated Security=True'}}
        MTConnect=@{Machines=@(@{BaseUri='http://127.0.0.1:5001';MachineId='00000000-0000-0000-0000-000000000001';DeviceKey='M01';PollingInterval='00:00:01'})}
        CurrentState=@{Freshness=@{MaximumCurrentAge='00:00:10'}}
        ProductionProcessing=@{Machines=@(@{MachineId='00000000-0000-0000-0000-000000000001';ActivityStreamKey='a';QuantityStreamKey='q';CompanyId='c';SiteId='s';ProductionLineId='l'})}
    }
    $api = [ordered]@{Persistence=@{Provider='SqlServer'};PersistenceProviders=@{SqlServer=@{ConnectionString='Server=(local);Database=Disposable;Integrated Security=True'}};Urls="http://127.0.0.1:$ApiPort";MTConnect=@{Machines=@(@{BaseUri='http://127.0.0.1:5001';MachineId='00000000-0000-0000-0000-000000000001';DeviceKey='M01'})};CurrentState=@{Freshness=@{MaximumCurrentAge='00:00:10'}}}
    $dash = [ordered]@{Urls="http://127.0.0.1:$DashboardPort";Dashboard=@{ReportingApiBaseAddress="http://127.0.0.1:$ApiPort";RequestTimeout='00:00:10';Sources=@(@{MachineId='00000000-0000-0000-0000-000000000001';ProcessorId='p';SiteId='s';ProductionLineId='l';DisplayName='M01';GroupName='LINE-1'})}}
    if(-not $Valid){$edge.Persistence.Provider='__PROVIDER__'}
    $edge|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\edge.production.json')
    $api|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\api.production.json')
    $dash|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\dashboard.production.json')
}
function Start-HealthServer([int]$Port,[string[]]$Paths) {
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(@"
`$l=[Net.HttpListener]::new();`$l.Prefixes.Add('http://127.0.0.1:$Port/');`$l.Start();try{while(`$true){`$c=`$l.GetContext();if(@('$($Paths -join "','")') -contains `$c.Request.Url.AbsolutePath){`$c.Response.StatusCode=200}else{`$c.Response.StatusCode=404};`$c.Response.Close()}}finally{`$l.Close()}
"@))
    $p=Start-Process powershell.exe -ArgumentList '-NoProfile','-EncodedCommand',$encoded -PassThru -WindowStyle Hidden
    $processes.Add($p.Id); Start-Sleep -Milliseconds 500; return $p
}
function Invoke-Startup([string]$Root,[int]$HealthTimeout=2) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $StartupScript -InstallRoot $Root -EdgeStabilizationSeconds 1 -HealthTimeoutSeconds $HealthTimeout 2>&1
    if($LASTEXITCODE-ne0){throw "Startup exited $LASTEXITCODE"}
}
function Read-Runtime([string]$Root){Get-Content -Raw (Join-Path $Root 'deployment\runtime.json')|ConvertFrom-Json}
function New-RecordedProcess([string]$Name,[string]$Exe) {
    $p=Start-Process -FilePath $Exe -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -WindowStyle Hidden;$processes.Add($p.Id)
    Start-Sleep -Milliseconds 150
    [ordered]@{name=$Name;pid=$p.Id;executablePath=[IO.Path]::GetFullPath($Exe);startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o')}
}
function Write-Runtime([string]$Root,$Edge,$Api,$Dashboard) {
    $state=[ordered]@{schemaVersion='1.0';selectedRelease=$releaseId;releasePath=(Join-Path $Root "releases\$releaseId");deploymentAttemptId='fixture';deploymentStatus='Succeeded';updatedAtUtc=[DateTime]::UtcNow.ToString('o');runtimeRunning=$true;failurePhase='';migrationOutcome='NotRun';databaseMayHaveChanged=$false;processStates=@{edge='Owned';api='Owned';dashboard='Owned'};edge=$Edge;api=$Api;dashboard=$Dashboard}
    $state|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'deployment\runtime.json')
}

try {
    Invoke-Case B01 {
        $r=New-Root B01;Copy-DummyExecutables $r;Write-Config $r 55101 55102
        $a=Start-HealthServer 55101 @('/health');$d=Start-HealthServer 55102 @('/health/live','/health/ready')
        Invoke-Startup $r; $s=Read-Runtime $r; Assert-True ($s.deploymentStatus-eq'Succeeded') 'clean startup did not succeed';Assert-True ($s.processStates.edge-eq'Owned'-and$s.processStates.api-eq'Owned'-and$s.processStates.dashboard-eq'Owned') 'clean startup ownership incomplete'
    }
    Invoke-Case B02 {
        $r=New-Root B02;Copy-DummyExecutables $r;Write-Config $r 55111 55112;$a=Start-HealthServer 55111 @('/health');$d=Start-HealthServer 55112 @('/health/live','/health/ready')
        $rel=Join-Path $r "releases\$releaseId";$e=New-RecordedProcess edge (Join-Path $rel 'apps\edge\FactoryConnect.Edge.exe');$ap=New-RecordedProcess api (Join-Path $rel 'apps\api\FactoryConnect.Api.exe');$da=New-RecordedProcess dashboard (Join-Path $rel 'apps\dashboard\FactoryConnect.Dashboard.exe');Write-Runtime $r $e $ap $da
        $before=@($e.pid,$ap.pid,$da.pid);Invoke-Startup $r;$after=Read-Runtime $r;Assert-True ((@($after.edge.pid,$after.api.pid,$after.dashboard.pid)-join ',')-eq($before-join ',')) 'AlreadyRunning changed process identities'
    }
    Invoke-Case B03 {
        $r=New-Root B03;Copy-DummyExecutables $r;Write-Config $r 55121 55122;$a=Start-HealthServer 55121 @('/health');$d=Start-HealthServer 55122 @('/health/live','/health/ready');$rel=Join-Path $r "releases\$releaseId";$e=New-RecordedProcess edge (Join-Path $rel 'apps\edge\FactoryConnect.Edge.exe');Write-Runtime $r $e $null $null;Invoke-Startup $r;$s=Read-Runtime $r;Assert-True ($s.edge.pid-ne$e.pid) 'partial runtime was not reconciled'
    }
    Invoke-Case B04 {
        $r=New-Root B04;Copy-DummyExecutables $r;Write-Config $r 55131 55132;$rel=Join-Path $r "releases\$releaseId";$e=New-RecordedProcess edge (Join-Path $rel 'apps\edge\FactoryConnect.Edge.exe');$ap=New-RecordedProcess api (Join-Path $rel 'apps\api\FactoryConnect.Api.exe');$da=New-RecordedProcess dashboard (Join-Path $rel 'apps\dashboard\FactoryConnect.Dashboard.exe');Write-Runtime $r $e $ap $da
        $failed=$false;try{Invoke-Startup $r 1}catch{$failed=$true};Assert-True $failed 'unhealthy runtime unexpectedly succeeded';Assert-True ($null-eq(Get-Process -Id $e.pid -ErrorAction SilentlyContinue)) 'old owned Edge survived unhealthy reconciliation'
    }
    Invoke-Case B05 {
        $r=New-Root B05;Copy-DummyExecutables $r;Write-Config $r 55141 55142;$failed=$false;try{Invoke-Startup $r 1}catch{$failed=$true};Assert-True $failed 'health failure unexpectedly succeeded';$s=Read-Runtime $r;Assert-True ($s.deploymentStatus-eq'Failed') 'failure state not published';foreach($n in @('edge','api','dashboard')){if($null-ne$s.$n){Assert-True ($null-eq(Get-Process -Id ([int]$s.$n.pid)-ErrorAction SilentlyContinue)) "$n started by failed attempt survived cleanup"}}
    }
    Invoke-Case B06 {
        $r=New-Root B06;Copy-DummyExecutables $r;Write-Config $r 55151 55152;$rel=Join-Path $r "releases\$releaseId";$e=New-RecordedProcess edge (Join-Path $rel 'apps\edge\FactoryConnect.Edge.exe');$foreign=Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -WindowStyle Hidden;$processes.Add($foreign.Id);$bad=[ordered]@{name='api';pid=$foreign.Id;executablePath=(Join-Path $rel 'apps\api\FactoryConnect.Api.exe');startTimeUtc='2000-01-01T00:00:00.0000000Z'};Write-Runtime $r $e $bad $null;$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'mismatch unexpectedly succeeded';Assert-True ($null-ne(Get-Process -Id $e.pid -ErrorAction SilentlyContinue)) 'mismatch path stopped existing Owned process'
    }
    Invoke-Case B07 {
        $r=New-Root B07;Copy-DummyExecutables $r;Write-Config $r 55161 55162;$rel=Join-Path $r "releases\$releaseId";$p=New-RecordedProcess edge (Join-Path $rel 'apps\edge\FactoryConnect.Edge.exe');$p.startTimeUtc='1999-01-01T00:00:00.0000000Z';Write-Runtime $r $p $null $null;$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'PID/start-time mismatch unexpectedly succeeded';Assert-True ($null-ne(Get-Process -Id $p.pid -ErrorAction SilentlyContinue)) 'PID-reuse guard terminated mismatched process'
    }
    Invoke-Case B08 {
        $r=New-Root B08;Copy-DummyExecutables $r;Write-Config $r 55171 55172;$sentinel=[ordered]@{schemaVersion='1.0';deploymentAttemptId='preserve-me'};$sentinel|ConvertTo-Json|Set-Content (Join-Path $r 'deployment\runtime.json');Write-Config $r 55171 55172 $false;$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'invalid commissioning unexpectedly succeeded';$after=Get-Content -Raw (Join-Path $r 'deployment\runtime.json')|ConvertFrom-Json;Assert-True ($after.deploymentAttemptId-eq'preserve-me') 'preflight failure overwrote runtime evidence'
    }
    Invoke-Case B09 {
        $r=New-Root B09;Copy-DummyExecutables $r;Write-Config $r 55181 55182;$intent=@{schemaVersion='1.0';attemptId='foreign';processName='edge';selectedRelease=$releaseId;executablePath='x';createdAtUtc=[DateTime]::UtcNow.ToString('o')};$intent|ConvertTo-Json|Set-Content (Join-Path $r 'deployment\runtime-start.intent.json');$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'unresolved intent unexpectedly succeeded';Assert-True (Test-Path (Join-Path $r 'deployment\runtime-start.intent.json')) 'foreign unresolved intent was deleted'
    }
    Invoke-Case B10 {
        $r=New-Root B10;Copy-DummyExecutables $r;Write-Config $r 55191 55192;$lockPath=Join-Path $r 'deployment\deployment.lock';$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);try{$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'concurrent lock unexpectedly succeeded'}finally{$lock.Dispose()}
    }
    Invoke-Case B11 {
        $r=New-Root B11;Copy-DummyExecutables $r;Write-Config $r 55201 55202;$intent=@{schemaVersion='1.0';attemptId='owner';processName='edge';selectedRelease=$releaseId;executablePath='x';createdAtUtc=[DateTime]::UtcNow.ToString('o')};$intent|ConvertTo-Json|Set-Content (Join-Path $r 'deployment\runtime-start.intent.json');$lockPath=Join-Path $r 'deployment\deployment.lock';$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);try{$failed=$false;try{Invoke-Startup $r}catch{$failed=$true};Assert-True $failed 'lock contention unexpectedly succeeded';Assert-True (Test-Path (Join-Path $r 'deployment\runtime-start.intent.json')) 'lock loser deleted active owner intent'}finally{$lock.Dispose()}
    }
    Invoke-Case B12 {
        $r=New-Root B12;Copy-DummyExecutables $r;Write-Config $r 55211 55212;$a=Start-HealthServer 55211 @('/health');$d=Start-HealthServer 55212 @('/health/live','/health/ready');Invoke-Startup $r;$s=Read-Runtime $r;Assert-True ($s.deploymentStatus-eq'Succeeded'-and$s.failurePhase-eq'') 'final verification did not publish success';Assert-True (-not(Test-Path (Join-Path $r 'deployment\runtime-start.intent.json'))) 'successful startup left unresolved intent'
    }
}
finally {
    foreach($pidValue in @($processes)){Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue}
    foreach($root in @($roots)){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
}

[pscustomobject]$results
if(@($results.Values|Where-Object{$_-ne'PASS'}).Count-gt0){exit 1}
