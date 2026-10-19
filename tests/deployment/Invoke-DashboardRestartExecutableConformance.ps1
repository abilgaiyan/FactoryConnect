[CmdletBinding()]
param([string]$RestartScript)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RestartScript)) {
    $RestartScript = Join-Path $PSScriptRoot '..\..\scripts\deployment\Restart-FactoryConnectDashboard.ps1'
}
$RestartScript = [IO.Path]::GetFullPath($RestartScript)
if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'Executable fixtures require Windows PowerShell 5.1; use powershell.exe.'
}
$releaseId = '1111111111111111111111111111111111111111'
$roots = [Collections.Generic.List[string]]::new()
$results = [ordered]@{}
$build = Join-Path ([IO.Path]::GetTempPath()) ('FactoryConnect-RBS-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $build | Out-Null
$fixture = Join-Path $build 'Fixture.exe'
# Each launched executable owns its health listener. No external responder can
# make a dead API/Dashboard pass. Windows PowerShell/.NET Framework is required.
Add-Type -OutputAssembly $fixture -OutputType WindowsApplication -TypeDefinition @'
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
   if(!api && Environment.GetEnvironmentVariable("RequireProjection") == "true") {
    ok = ok && Environment.GetEnvironmentVariable("Dashboard__Sources__0__ProcessorId") == "operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1";
    ok = ok && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
    ok = ok && Environment.GetEnvironmentVariable("Dashboard__Sources__9__MachineId") == null;
   }
   c.Response.StatusCode = ok ? 200 : 503; c.Response.Close();
  }
 }
}
'@
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-Case([string]$Id,[scriptblock]$Body) {
    Write-Host "Running $Id"
    try { & $Body; $results[$Id] = 'PASS'; Write-Host "$Id PASS" }
    catch {
        $results[$Id] = "FAIL: $($_.Exception.Message)"
        Write-Warning "$Id primary failure: $($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)`n$($_.ScriptStackTrace)"
        throw
    }
}
function Free-Port {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop(); return $port
}
function New-Root([string]$Name,[string]$Failure='') {
    $r = Join-Path ([IO.Path]::GetTempPath()) ("FactoryConnect-RBS-$Name-" + [Guid]::NewGuid().ToString('N'))
    $roots.Add($r)
    foreach ($part in @('config','deployment','releases')) { New-Item -ItemType Directory -Force (Join-Path $r $part) | Out-Null }
    # Model the existing installation lock even when a case constructs runtime
    # evidence directly (R08) rather than invoking restart first.
    [IO.File]::WriteAllBytes((Join-Path $r 'deployment/deployment.lock'),[byte[]]@())
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
    $dash = [ordered]@{Urls="http://127.0.0.1:$DashboardPort";Dashboard=@{ReportingApiBaseAddress="http://127.0.0.1:$ApiPort";RequestTimeout='00:00:10';Sources=@(@{MachineId='00000000-0000-0000-0000-000000000001';ProcessorId='operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1';SiteId='s';ProductionLineId='l';DisplayName='M01';GroupName='LINE-1'})}}
    $edge['RbsFixtureFailure']=$Failure;$api['RbsFixtureFailure']=$Failure;$dash['RbsFixtureFailure']=$Failure
    if(-not $Valid){$edge.Persistence.Provider='__PROVIDER__'}
    $edge|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\edge.production.json');$api|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\api.production.json');$dash|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Root 'config\dashboard.production.json')
}

function Invoke-Restart([string]$Root,[bool]$ExpectFailure=$false,[string]$OperationScript=$RestartScript) {
    $out = Join-Path $Root ('out-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $err = $out + '.err'
    $args = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -MachineId 00000000-0000-0000-0000-000000000001 -ExpectedProcessorId operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1 -DashboardStabilizationSeconds 1 -HealthTimeoutSeconds 2 -ShutdownTimeoutSeconds 2' -f $OperationScript,$Root
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $shell = (Get-Command powershell.exe).Source
    $info.FileName = $env:ComSpec
    $info.Arguments = '/d /s /c ""{0}" {1} >"{2}" 2>"{3}""' -f $shell,$args,$out,$err
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $info
    [void]$p.Start()
    try {
        if (-not $p.WaitForExit(30000)) { throw 'Dashboard restart exceeded harness deadline.' }
        $exitCode = $p.ExitCode
        if ($null -eq $exitCode) { throw 'Harness could not obtain a numeric child exit code.' }
        if (($exitCode -ne 0) -ne $ExpectFailure) {
            $stdout = if (Test-Path $out) { Get-Content -LiteralPath $out -Raw } else { '<missing>' }
            $stderr = if (Test-Path $err) { Get-Content -LiteralPath $err -Raw } else { '<missing>' }
            $runtimeFile = Join-Path $Root 'deployment\\runtime.json'
            $intentFile = Join-Path $Root 'deployment\\runtime-start.intent.json'
            $runtimeEvidence = if (Test-Path $runtimeFile) { Get-Content -LiteralPath $runtimeFile -Raw } else { '<missing>' }
            $intentEvidence = if (Test-Path $intentFile) { Get-Content -LiteralPath $intentFile -Raw } else { '<missing>' }
            throw ("Unexpected Dashboard restart result for {0}: exit={1}; expectedFailure={2}`nSTDOUT:`n{3}`nSTDERR:`n{4}`nRUNTIME:`n{5}`nINTENT:`n{6}" -f $Root,$exitCode,$ExpectFailure,$stdout,$stderr,$runtimeEvidence,$intentEvidence)
        }
    } finally {
        if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
        $p.Dispose()
    }
}
function Read-Runtime([string]$Root) { Get-Content -Raw (Join-Path $Root 'deployment\runtime.json') | ConvertFrom-Json }
function Write-Runtime([string]$Root,$Records) {
    @{selectedRelease=$releaseId;releasePath=(Join-Path $Root "releases\$releaseId");deploymentStatus='Succeeded';edge=$Records.edge;api=$Records.api;dashboard=$Records.dashboard;deploymentAttemptId='fixture'} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Root 'deployment\runtime.json')
}
function Start-Fixture([string]$Root,[string]$Name) {
    $cfg = Get-Content -Raw (Join-Path $Root "config\$Name.production.json") | ConvertFrom-Json
    $savedUrl = $env:Urls; $savedFailure = $env:RbsFixtureFailure
    $p=$null
    try {
        if ($Name -ne 'edge') { $env:Urls = $cfg.Urls }
        $env:RbsFixtureFailure = $cfg.RbsFixtureFailure
        $role = @{edge='Edge';api='Api';dashboard='Dashboard'}[$Name]
        $exe = Join-Path $Root "releases\$releaseId\apps\$Name\FactoryConnect.$role.exe"
        $p = Start-Process $exe -PassThru -WindowStyle Hidden
        return @{name=$Name;pid=$p.Id;executablePath=$exe;startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o')}
    } finally { if ($null -ne $p) { $p.Dispose() }; $env:Urls=$savedUrl; $env:RbsFixtureFailure=$savedFailure }
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

function New-HealthyRoot([string]$Name) {
    $r=New-Root $Name
    $records=@{}
    foreach ($n in @('edge','api','dashboard')) { $records[$n]=Start-Fixture $r $n }
    Write-Runtime $r $records
    Wait-FixtureHealth $r
    return $r
}
function Assert-Preservation($Before,$After) {
    foreach ($n in @('edge','api')) {
        Assert-True (($Before.$n|ConvertTo-Json -Compress) -ceq ($After.$n|ConvertTo-Json -Compress)) "$n identity changed."
        $live=Get-Process -Id $After.$n.pid -ErrorAction Stop
        try { Assert-True ($live.StartTime.ToUniversalTime().ToString('o') -ceq $After.$n.startTimeUtc) "$n process instance changed." }
        finally { $live.Dispose() }
    }
}
function Assert-Rejected([string]$Root) {
    $before=Get-Content -Raw (Join-Path $Root 'deployment/runtime.json')
    $records=Read-Runtime $Root
    $instances=@{}
    foreach ($p in @(Get-Process)) {
        try { if ($p.Path -and $p.Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) { $instances[$p.Id]=$p.StartTime.ToUniversalTime().ToString('o') } } catch { } finally { $p.Dispose() }
    }
    Invoke-Restart $Root $true
    Assert-True ($before -ceq (Get-Content -Raw (Join-Path $Root 'deployment/runtime.json'))) 'Rejection changed durable evidence.'
    $afterInstances=@{}
    foreach ($p in @(Get-Process)) {
        try { if ($p.Path -and $p.Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) { $afterInstances[$p.Id]=$p.StartTime.ToUniversalTime().ToString('o') } } catch { } finally { $p.Dispose() }
    }
    Assert-True ($afterInstances.Count -eq $instances.Count) 'Rejected operation changed the fixture process inventory.'
    foreach ($id in $instances.Keys) { Assert-True ($afterInstances.ContainsKey($id) -and $afterInstances[$id] -ceq $instances[$id]) 'Rejected operation changed a process instance.' }
    foreach ($n in @('edge','api','dashboard')) {
        if ($null -eq $records.$n) { continue }
        $live=Get-Process -Id $records.$n.pid -ErrorAction SilentlyContinue
        try { Assert-True ($null -ne $live) "Rejection terminated $n." }
        finally { if ($null -ne $live) { $live.Dispose() } }
    }
}

Copy-Item (Join-Path (Split-Path $RestartScript -Parent) 'FactoryConnect.ProcessTermination.ps1') (Join-Path $build 'FactoryConnect.ProcessTermination.ps1')
function New-FaultScript([string]$Name,[string]$Anchor,[string]$Injection) {
    $source=Get-Content -Raw -LiteralPath $RestartScript
    Assert-True (($source.Split(@($Anchor),[StringSplitOptions]::None).Count - 1) -eq 1) "Fault anchor is not unique: $Name"
    $source=$source.Replace($Anchor,$Injection+"`r`n"+$Anchor)
    $path=Join-Path $build ("Restart-$Name.ps1")
    Set-Content -LiteralPath $path -Value $source -Encoding UTF8
    return $path
}
function Invoke-InterruptedRestart([string]$Root,[string]$OperationScript) {
    $marker=Join-Path $Root 'interruption-marker.json'
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=(Get-Command powershell.exe).Source
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -MachineId 00000000-0000-0000-0000-000000000001 -ExpectedProcessorId operational-metrics:00000000-0000-0000-0000-000000000001:builtins-v1-availability-containment-v1 -DashboardStabilizationSeconds 1 -HealthTimeoutSeconds 2 -ShutdownTimeoutSeconds 2' -f $OperationScript,$Root
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $hostProcess=[Diagnostics.Process]::Start($info)
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        while (-not (Test-Path -LiteralPath $marker) -and -not $hostProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Assert-True (Test-Path -LiteralPath $marker) 'Restart did not reach the interruption marker.'
        # Terminate only the test PowerShell host, never its fixture child. This
        # bypasses catch/finally and exercises an actual post-launch interruption.
        $hostProcess.Kill(); [void]$hostProcess.WaitForExit(5000)
        return (Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json)
    } finally {
        if (-not $hostProcess.HasExited) { $hostProcess.Kill(); [void]$hostProcess.WaitForExit(5000) }
        $hostProcess.Dispose()
    }
}

function Remove-FixtureDirectory([string]$Path) {
    for ($attempt=0;$attempt -lt 6;$attempt++) {
        try {
            if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
            return
        } catch {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}
function Complete-FixtureRun($PrimaryFailure,$CleanupFailures) {
    if ($null -ne $PrimaryFailure) { throw $PrimaryFailure }
    if ($CleanupFailures.Count -gt 0) { throw ("Fixture cleanup failed: " + ($CleanupFailures -join '; ')) }
}
$primaryFailure=$null
$cleanupFailures=[Collections.Generic.List[string]]::new()

try {
    Invoke-Case 'Executable-D01-ProductionConfiguration' {
        $r=New-HealthyRoot D01; $before=Read-Runtime $r
        $path=Join-Path $r 'config/dashboard.production.json'
        $cfg=Get-Content -Raw $path | ConvertFrom-Json
        $cfg | Add-Member -MemberType NoteProperty -Name RequireProjection -Value $true
        $cfg | ConvertTo-Json -Depth 30 | Set-Content $path
        $savedSource=$env:Dashboard__Sources__9__MachineId
        try {
            $env:Dashboard__Sources__9__MachineId='stale-source'
            Invoke-Restart $r
            Assert-True ($env:Dashboard__Sources__9__MachineId -ceq 'stale-source') 'Restart changed the operator environment.'
        } finally { $env:Dashboard__Sources__9__MachineId=$savedSource }
        $after=Read-Runtime $r
        Assert-True ($after.deploymentStatus -eq 'Succeeded' -and $after.dashboard.pid -ne $before.dashboard.pid) 'Dashboard was not replaced.'
        Assert-True ($null -eq (Get-Process -Id $before.dashboard.pid -ErrorAction SilentlyContinue)) 'Old Dashboard survived.'
        Assert-True ($after.dashboardRestart.terminationConfirmed -and $after.dashboardRestart.terminationObservations[0].outcome -eq 'Exited') 'Exact termination not observed.'
        Assert-True (-not (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Success retained intent.'
        Assert-Preservation $before $after; Wait-FixtureHealth $r
    }
    Invoke-Case 'Executable-D02-Ownership' {
        foreach ($role in @('edge','api','dashboard')) {
            foreach ($field in @('startTimeUtc','executablePath')) {
                $r=New-HealthyRoot D02; $v=Read-Runtime $r
                if ($field -eq 'startTimeUtc') { $v.$role.$field='2000-01-01T00:00:00.0000000Z' }
                else { $v.$role.$field=Join-Path $r 'wrong.exe' }
                $v | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $r 'deployment/runtime.json')
                Assert-Rejected $r
            }
        }
    }
    Invoke-Case 'Executable-D03-LockAndSharedIntent' {
        $r=New-HealthyRoot D03
        $held=[IO.File]::Open((Join-Path $r 'deployment/deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { Assert-Rejected $r } finally { $held.Dispose() }
        $ip=Join-Path $r 'deployment/runtime-start.intent.json'
        '{"attemptId":"foreign","processName":"edge"}' | Set-Content $ip
        $before=Get-Content -Raw $ip; Assert-Rejected $r
        Assert-True ($before -ceq (Get-Content -Raw $ip)) 'Foreign intent changed.'
    }
    Invoke-Case 'Executable-D04-HealthFailures' {
        foreach ($failure in @('/health/live','/health/ready')) {
            $r=New-HealthyRoot D04; $before=Read-Runtime $r
            $path=Join-Path $r 'config/dashboard.production.json'
            $cfg=Get-Content -Raw $path | ConvertFrom-Json
            $cfg.RbsFixtureFailure=$failure
            $cfg | ConvertTo-Json -Depth 30 | Set-Content $path
            Invoke-Restart $r $true; $after=Read-Runtime $r
            Assert-True ($after.deploymentStatus -eq 'Failed' -and $null -eq $after.dashboard) 'Unhealthy replacement claimed ownership.'
            Assert-True (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json')) 'Health failure lost intent.'
            Assert-Preservation $before $after
            Assert-Rejected $r
        }
    }
    Invoke-Case 'Executable-D05-InterruptedLaunch' {
        $r=New-HealthyRoot D05; $before=Read-Runtime $r
        $fault=New-FaultScript 'DashboardInterrupted' "`$phase='Stabilization'" @'
$replacement | ConvertTo-Json | Set-Content (Join-Path $root 'interruption-marker.json')
Start-Sleep -Seconds 120
'@
        $candidate=Invoke-InterruptedRestart $r $fault
        $after=Read-Runtime $r
        Assert-True ($after.deploymentStatus -eq 'DashboardRestarting' -and $null -eq $after.dashboard) 'Interrupted launch claimed ownership.'
        Assert-True (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json')) 'Interrupted host lost launch intent.'
        $live=Get-Process -Id $candidate.pid -ErrorAction Stop
        try { Assert-True ($live.StartTime.ToUniversalTime().ToString('o') -ceq $candidate.startTimeUtc) 'Unpublished replacement not observed.' } finally { $live.Dispose() }
        Assert-Preservation $before $after; Assert-Rejected $r
    }
 } catch {
    $primaryFailure=$_
} finally {
    # Cleanup errors are secondary: never mask the test failure or abandon later
    # roots. Pin, stop and wait on the exact fixture instance, then release every
    # process handle before attempting to delete its executable image.
    foreach ($fixtureRoot in $roots) {
        $rootCleanupFailed=$false
        foreach ($process in @(Get-Process)) {
            $isFixture=$false
            try {
                [void]$process.Handle
                $process.Refresh()
                if ($process.HasExited) { continue }
                $path=$process.Path
                if ([string]::IsNullOrWhiteSpace($path) -or -not $path.StartsWith($fixtureRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { continue }
                $isFixture=$true
                # Path was read from the pinned instance. Stop and observe that
                # same instance, never a PID reacquired after termination.
                Stop-Process -InputObject $process -Force -ErrorAction Stop
                if (-not $process.WaitForExit(5000)) { throw "Fixture process $($process.Id) did not exit within five seconds." }
            } catch {
                if ($isFixture) {
                    $rootCleanupFailed=$true
                    $cleanupFailures.Add("$fixtureRoot process cleanup: $($_.Exception.Message)")
                }
            } finally { $process.Dispose() }
        }
        if ($rootCleanupFailed) { continue }
        try {
            # Earlier assertions and production helpers can have collectible
            # Process wrappers. Release them so Windows can unmap exited images.
            [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
            foreach ($file in @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse)) {
                [IO.File]::SetAttributes($file.FullName,([IO.File]::GetAttributes($file.FullName) -band (-bnot [IO.FileAttributes]::ReadOnly)))
            }
            Remove-FixtureDirectory $fixtureRoot
        } catch { $cleanupFailures.Add("$fixtureRoot file cleanup: $($_.Exception.Message)") }
    }
    try { Remove-FixtureDirectory $build }
    catch { $cleanupFailures.Add("$build file cleanup: $($_.Exception.Message)") }
    foreach ($message in $cleanupFailures) { Write-Warning "Secondary fixture cleanup failure: $message" }
}
Complete-FixtureRun $primaryFailure $cleanupFailures
[pscustomobject]$results
