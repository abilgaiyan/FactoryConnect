[CmdletBinding()]
param([string]$StartupScript)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($StartupScript)) {
    $StartupScript = Join-Path $PSScriptRoot '..\..\scripts\deployment\Start-FactoryConnectRuntime.ps1'
}
$StartupScript = [IO.Path]::GetFullPath($StartupScript)
$releaseId = '1111111111111111111111111111111111111111'
$roots = [Collections.Generic.List[string]]::new()
$results = [ordered]@{}
$build = Join-Path ([IO.Path]::GetTempPath()) ('FactoryConnect-RBS-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $build | Out-Null
$fixture = Join-Path $build 'Fixture.exe'
# Each launched executable owns its health listener. No external responder can
# make a dead API/Dashboard pass. Windows PowerShell/.NET Framework is required.
Add-Type -OutputAssembly $fixture -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.Net;
using System.Diagnostics;
using System.Threading;
public static class Fixture {
 public static void Main() {
  string name = Process.GetCurrentProcess().MainModule.FileName;
  string failure = Environment.GetEnvironmentVariable("RbsFixtureFailure") ?? "";
  if(name.Contains("Edge")) { if(failure == "edge") return; Thread.Sleep(Timeout.Infinite); return; }
  bool api = name.Contains("Api");
  var listener = new HttpListener();
  listener.Prefixes.Add(Environment.GetEnvironmentVariable("Urls").TrimEnd('/') + "/");
  listener.Start();
  while(true) {
   var c = listener.GetContext(); string path = c.Request.Url.AbsolutePath;
   bool ok = api ? path == "/health" : path == "/health/live" || path == "/health/ready";
   if((api && failure == "api") || (!api && failure == path)) ok = false;
   c.Response.StatusCode = ok ? 200 : 503; c.Response.Close();
  }
 }
}
'@
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-Case([string]$Id,[scriptblock]$Body) {
    Write-Host "Running $Id"
    try { & $Body; $results[$Id] = 'PASS'; Write-Host "$Id PASS" }
    catch { $results[$Id] = "FAIL: $($_.Exception.Message)"; throw }
}
function Free-Port {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop(); return $port
}
function New-Root([string]$Name,[string]$Failure='') {
    $r = Join-Path ([IO.Path]::GetTempPath()) ("FactoryConnect-RBS-$Name-" + [Guid]::NewGuid().ToString('N'))
    $roots.Add($r)
    foreach ($part in @('config','deployment','releases')) { New-Item -ItemType Directory -Force (Join-Path $r $part) | Out-Null }
    foreach ($n in @('edge','api','dashboard')) {
        $dir = Join-Path $r "releases\$releaseId\apps\$n"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $role = @{edge='Edge';api='Api';dashboard='Dashboard'}[$n]
        Copy-Item $fixture (Join-Path $dir "FactoryConnect.$role.exe")
    }
    New-Item -ItemType Junction -Path (Join-Path $r 'current') -Target (Join-Path $r "releases\$releaseId") | Out-Null
    Write-Config $r (Free-Port) (Free-Port) $true $Failure
    return $r
}
function Write-Config([string]$Root,[int]$ApiPort,[int]$DashboardPort,[bool]$Valid=$true,[string]$Failure='') {
    $edge = [ordered]@{Persistence=@{Provider='SqlServer'};PersistenceProviders=@{SqlServer=@{ConnectionString='Server=(local);Database=Disposable;Integrated Security=True'}};MTConnect=@{Machines=@(@{BaseUri='http://127.0.0.1:5001';MachineId='00000000-0000-0000-0000-000000000001';DeviceKey='M01';PollingInterval='00:00:01'})};CurrentState=@{Freshness=@{MaximumCurrentAge='00:00:10'}};ProductionProcessing=@{Machines=@(@{MachineId='00000000-0000-0000-0000-000000000001';ActivityStreamKey='a';QuantityStreamKey='q';CompanyId='c';SiteId='s';ProductionLineId='l'})}}
    $api = [ordered]@{Persistence=@{Provider='SqlServer'};PersistenceProviders=@{SqlServer=@{ConnectionString='Server=(local);Database=Disposable;Integrated Security=True'}};Urls="http://127.0.0.1:$ApiPort";MTConnect=@{Machines=@(@{BaseUri='http://127.0.0.1:5001';MachineId='00000000-0000-0000-0000-000000000001';DeviceKey='M01'})};CurrentState=@{Freshness=@{MaximumCurrentAge='00:00:10'}}}
    $dash = [ordered]@{Urls="http://127.0.0.1:$DashboardPort";Dashboard=@{ReportingApiBaseAddress="http://127.0.0.1:$ApiPort";RequestTimeout='00:00:10';Sources=@(@{MachineId='00000000-0000-0000-0000-000000000001';ProcessorId='p';SiteId='s';ProductionLineId='l';DisplayName='M01';GroupName='LINE-1'})}}
    $edge['RbsFixtureFailure']=$Failure;$api['RbsFixtureFailure']=$Failure;$dash['RbsFixtureFailure']=$Failure
    if(-not $Valid){$edge.Persistence.Provider='__PROVIDER__'}
    $edge|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\edge.production.json');$api|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\api.production.json');$dash|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\dashboard.production.json')
}

function Invoke-Startup([string]$Root,[bool]$ExpectFailure=$false) {
    $out = Join-Path $Root ('out-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $err = $out + '.err'
    $args = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -EdgeStabilizationSeconds 1 -HealthTimeoutSeconds 2' -f $StartupScript,$Root
    $p = Start-Process powershell.exe -ArgumentList $args -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    try {
        if (-not $p.WaitForExit(30000)) { throw 'Startup exceeded harness deadline.' }
        $p.Refresh()
        $exitCode = $p.ExitCode
        if (($exitCode -ne 0) -ne $ExpectFailure) {
            $stdout = if (Test-Path $out) { Get-Content -LiteralPath $out -Raw } else { '<missing>' }
            $stderr = if (Test-Path $err) { Get-Content -LiteralPath $err -Raw } else { '<missing>' }
            $runtimeFile = Join-Path $Root 'deployment\\runtime.json'
            $intentFile = Join-Path $Root 'deployment\\runtime-start.intent.json'
            $runtimeEvidence = if (Test-Path $runtimeFile) { Get-Content -LiteralPath $runtimeFile -Raw } else { '<missing>' }
            $intentEvidence = if (Test-Path $intentFile) { Get-Content -LiteralPath $intentFile -Raw } else { '<missing>' }
            throw ("Unexpected startup result for {0}: exit={1}; expectedFailure={2}\nSTDOUT:\n{3}\nSTDERR:\n{4}\nRUNTIME:\n{5}\nINTENT:\n{6}" -f $Root,$exitCode,$ExpectFailure,$stdout,$stderr,$runtimeEvidence,$intentEvidence)
        }
    } finally {
        if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }
        $p.Dispose()
    }
}
function Read-Runtime([string]$Root) { Get-Content -Raw (Join-Path $Root 'deployment\runtime.json') | ConvertFrom-Json }
function Write-Runtime([string]$Root,$Records) {
    @{edge=$Records.edge;api=$Records.api;dashboard=$Records.dashboard;deploymentAttemptId='fixture'} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Root 'deployment\runtime.json')
}
function Start-Fixture([string]$Root,[string]$Name) {
    $cfg = Get-Content -Raw (Join-Path $Root "config\$Name.production.json") | ConvertFrom-Json
    $savedUrl = $env:Urls; $savedFailure = $env:RbsFixtureFailure
    try {
        if ($Name -ne 'edge') { $env:Urls = $cfg.Urls }
        $env:RbsFixtureFailure = $cfg.RbsFixtureFailure
        $role = @{edge='Edge';api='Api';dashboard='Dashboard'}[$Name]
        $exe = Join-Path $Root "releases\$releaseId\apps\$Name\FactoryConnect.$role.exe"
        $p = Start-Process $exe -PassThru
        return @{name=$Name;pid=$p.Id;executablePath=$exe;startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o')}
    } finally { $env:Urls=$savedUrl; $env:RbsFixtureFailure=$savedFailure }
}
function Wait-FixtureHealth([string]$Root) {
    $api = Get-Content -Raw (Join-Path $Root 'config\api.production.json') | ConvertFrom-Json
    $dash = Get-Content -Raw (Join-Path $Root 'config\dashboard.production.json') | ConvertFrom-Json
    foreach ($uri in @("$($api.Urls)/health","$($dash.Urls)/health/live","$($dash.Urls)/health/ready")) {
        $ok = $false
        for ($i=0;$i -lt 30;$i++) {
            try { $ok = (Invoke-WebRequest $uri -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200 } catch { }
            if ($ok) { break }; Start-Sleep -Milliseconds 100
        }
        Assert-True $ok "Fixture not healthy: $uri"
    }
}
function Assert-NoChildren([string]$Root) {
    $found = @(Get-Process | Where-Object { try { $_.Path.StartsWith($Root + '\',[StringComparison]::OrdinalIgnoreCase) } catch { $false } })
    Assert-True ($found.Count -eq 0) 'Failed startup leaked a fixture child.'
}
try {
    Invoke-Case B01 { $r=New-Root B01; Invoke-Startup $r; Assert-True ((Read-Runtime $r).deploymentStatus -eq 'Succeeded') 'Clean startup failed.' }
    Invoke-Case B02 {
        $r=New-Root B02; Invoke-Startup $r
        $before=Get-Content -Raw (Join-Path $r 'deployment\runtime.json')
        Invoke-Startup $r
        Assert-True ($before -ceq (Get-Content -Raw (Join-Path $r 'deployment\runtime.json'))) 'AlreadyRunning changed ownership evidence.'
    }
    Invoke-Case B03 {
        $r=New-Root B03; $e=Start-Fixture $r edge
        Stop-Process -Id $e.pid; Wait-Process -Id $e.pid -ErrorAction SilentlyContinue
        Write-Runtime $r @{edge=$e;api=$null;dashboard=$null}
        Invoke-Startup $r; Assert-True ((Read-Runtime $r).deploymentStatus -eq 'Succeeded') 'Stale reboot evidence did not recover.'
    }
    Invoke-Case B04 { $r=New-Root B04 edge; Invoke-Startup $r $true; Assert-NoChildren $r }
    Invoke-Case B05 { $r=New-Root B05 api; Invoke-Startup $r $true; Assert-NoChildren $r }
    Invoke-Case B06 {
        foreach ($path in @('/health/live','/health/ready')) {
            $r=New-Root B06 $path; Invoke-Startup $r $true; Assert-NoChildren $r
        }
    }
    Invoke-Case B07 {
        $r=New-Root B07; $e=Start-Fixture $r edge; $bad=Start-Fixture $r api
        $bad.startTimeUtc='2000-01-01T00:00:00.0000000Z'
        Write-Runtime $r @{edge=$e;api=$bad;dashboard=$null}
        $before=Get-Content -Raw (Join-Path $r 'deployment\runtime.json')
        Invoke-Startup $r $true
        Assert-True ($null -ne (Get-Process -Id $e.pid -ErrorAction SilentlyContinue)) 'Mismatch stopped Owned Edge.'
        Assert-True ($null -ne (Get-Process -Id $bad.pid -ErrorAction SilentlyContinue)) 'Mismatch stopped unrelated identity.'
        Assert-True ($before -ceq (Get-Content -Raw (Join-Path $r 'deployment\runtime.json'))) 'Mismatch changed evidence.'
    }
    Invoke-Case B08 { $r=New-Root B08; cmd /c "rmdir `"$r\current`"" | Out-Null; Invoke-Startup $r $true; Assert-NoChildren $r }
    Invoke-Case B09 {
        foreach ($kind in @('nested','outside')) {
            $r=New-Root B09; cmd /c "rmdir `"$r\current`"" | Out-Null
            $target=if($kind -eq 'nested'){Join-Path $r "releases\nested\$releaseId"}else{Join-Path $r 'outside'}
            New-Item -ItemType Directory -Force $target | Out-Null
            New-Item -ItemType Junction -Path (Join-Path $r 'current') -Target $target | Out-Null
            Invoke-Startup $r $true; Assert-NoChildren $r
        }
    }
    Invoke-Case B10 {
        foreach ($kind in @('missing','invalid')) {
            $r=New-Root B10; $rp=Join-Path $r 'deployment\runtime.json'
            '{"deploymentAttemptId":"preserve"}' | Set-Content $rp
            $before=Get-Content -Raw $rp
            if($kind -eq 'missing'){Remove-Item (Join-Path $r 'config\edge.production.json')}else{Write-Config $r (Free-Port) (Free-Port) $false}
            Invoke-Startup $r $true
            Assert-True ($before -ceq (Get-Content -Raw $rp)) 'Preflight overwrote runtime evidence.'
            Assert-NoChildren $r
        }
    }
    Invoke-Case B11 {
        $r=New-Root B11; $ip=Join-Path $r 'deployment\runtime-start.intent.json'
        '{"attemptId":"foreign","processName":"edge"}' | Set-Content $ip; $before=Get-Content -Raw $ip
        $lock=[IO.File]::Open((Join-Path $r 'deployment\deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { Invoke-Startup $r $true; Assert-True ($before -ceq (Get-Content -Raw $ip)) 'Lock loser changed intent.' } finally { $lock.Dispose() }
        for($i=0;$i -lt 2;$i++){Invoke-Startup $r $true; Assert-True ($before -ceq (Get-Content -Raw $ip)) 'Retry deleted unresolved intent.'}
        Assert-NoChildren $r
    }
    Invoke-Case B12 {
        foreach($kind in @('partial','unhealthy')) {
            $r=New-Root B12 $(if($kind -eq 'unhealthy'){'api'}else{''})
            $e=Start-Fixture $r edge; $records=@{edge=$e;api=$null;dashboard=$null}
            if($kind -eq 'unhealthy'){$records.api=Start-Fixture $r api;$records.dashboard=Start-Fixture $r dashboard}
            Write-Runtime $r $records
            # Existing children retain their unhealthy environment; the next
            # canonical attempt receives the repaired commissioned fixture config.
            $cfg=Get-Content -Raw (Join-Path $r 'config\api.production.json') | ConvertFrom-Json
            $dash=Get-Content -Raw (Join-Path $r 'config\dashboard.production.json') | ConvertFrom-Json
            Write-Config $r ([Uri]$cfg.Urls).Port ([Uri]$dash.Urls).Port
            Invoke-Startup $r
            Assert-True ((Read-Runtime $r).edge.pid -ne $e.pid) 'Reconciliation retained old Edge.'
            Assert-True ((Read-Runtime $r).deploymentStatus -eq 'Succeeded') 'Reconciliation did not succeed.'
        }
    }
} finally {
    # Enumerate every child launched from a disposable root, including processes
    # that never reached runtime.json. Revalidate PID/path/start before stopping.
    foreach($r in $roots) {
        foreach($p in @(Get-Process)) {
            try { $path=$p.Path; $start=$p.StartTime.ToUniversalTime().ToString('o') } catch { continue }
            if ([string]::IsNullOrEmpty($path) -or -not $path.StartsWith($r + '\',[StringComparison]::OrdinalIgnoreCase)) { continue }
            try {
                $live=Get-Process -Id $p.Id -ErrorAction SilentlyContinue
                if($null -ne $live -and $live.Path -eq $path -and $live.StartTime.ToUniversalTime().ToString('o') -eq $start){Stop-Process -InputObject $live -Force; $live.WaitForExit()}
            } catch { throw "Fixture cleanup failed: $($_.Exception.Message)" }
        }
        Remove-Item -LiteralPath $r -Recurse -Force
    }
    Remove-Item -LiteralPath $build -Recurse -Force
}
[pscustomobject]$results
