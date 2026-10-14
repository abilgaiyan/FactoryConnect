[CmdletBinding()]
param([string]$RestartScript)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RestartScript)) {
    $RestartScript = Join-Path $PSScriptRoot '..\..\scripts\deployment\Restart-FactoryConnectEdge.ps1'
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

function Invoke-Restart([string]$Root,[bool]$ExpectFailure=$false,[string]$OperationScript=$RestartScript) {
    $out = Join-Path $Root ('out-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $err = $out + '.err'
    $args = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -EdgeStabilizationSeconds 1 -HealthTimeoutSeconds 2 -ShutdownTimeoutSeconds 2' -f $OperationScript,$Root
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
        if (-not $p.WaitForExit(30000)) { throw 'Edge restart exceeded harness deadline.' }
        $exitCode = $p.ExitCode
        if ($null -eq $exitCode) { throw 'Harness could not obtain a numeric child exit code.' }
        if (($exitCode -ne 0) -ne $ExpectFailure) {
            $stdout = if (Test-Path $out) { Get-Content -LiteralPath $out -Raw } else { '<missing>' }
            $stderr = if (Test-Path $err) { Get-Content -LiteralPath $err -Raw } else { '<missing>' }
            $runtimeFile = Join-Path $Root 'deployment\\runtime.json'
            $intentFile = Join-Path $Root 'deployment\\runtime-start.intent.json'
            $runtimeEvidence = if (Test-Path $runtimeFile) { Get-Content -LiteralPath $runtimeFile -Raw } else { '<missing>' }
            $intentEvidence = if (Test-Path $intentFile) { Get-Content -LiteralPath $intentFile -Raw } else { '<missing>' }
            throw ("Unexpected Edge restart result for {0}: exit={1}; expectedFailure={2}`nSTDOUT:`n{3}`nSTDERR:`n{4}`nRUNTIME:`n{5}`nINTENT:`n{6}" -f $Root,$exitCode,$ExpectFailure,$stdout,$stderr,$runtimeEvidence,$intentEvidence)
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
    try {
        if ($Name -ne 'edge') { $env:Urls = $cfg.Urls }
        $env:RbsFixtureFailure = $cfg.RbsFixtureFailure
        $role = @{edge='Edge';api='Api';dashboard='Dashboard'}[$Name]
        $exe = Join-Path $Root "releases\$releaseId\apps\$Name\FactoryConnect.$role.exe"
        $p = Start-Process $exe -PassThru -WindowStyle Hidden
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

function New-HealthyRoot([string]$Name) {
    $r=New-Root $Name
    $records=@{}
    foreach ($n in @('edge','api','dashboard')) { $records[$n]=Start-Fixture $r $n }
    Write-Runtime $r $records
    Wait-FixtureHealth $r
    return $r
}
function Assert-Preservation($Before,$After) {
    foreach ($n in @('api','dashboard')) {
        Assert-True (($Before.$n|ConvertTo-Json -Compress) -ceq ($After.$n|ConvertTo-Json -Compress)) "$n identity changed."
        $live=Get-Process -Id $After.$n.pid -ErrorAction Stop
        Assert-True ($live.StartTime.ToUniversalTime().ToString('o') -ceq $After.$n.startTimeUtc) "$n process instance changed."
    }
}
function Assert-Rejected([string]$Root) {
    $before=Get-Content -Raw (Join-Path $Root 'deployment/runtime.json')
    $records=Read-Runtime $Root
    Invoke-Restart $Root $true
    Assert-True ($before -ceq (Get-Content -Raw (Join-Path $Root 'deployment/runtime.json'))) 'Rejection changed durable evidence.'
    foreach ($n in @('edge','api','dashboard')) {
        Assert-True ($null -ne (Get-Process -Id $records.$n.pid -ErrorAction SilentlyContinue)) "Rejection terminated $n."
    }
}

$AssessmentScript=Join-Path (Split-Path $RestartScript -Parent) 'Get-FactoryConnectEdgeRestartAssessment.ps1'
Copy-Item (Join-Path (Split-Path $RestartScript -Parent) 'FactoryConnect.ProcessTermination.ps1') (Join-Path $build 'FactoryConnect.ProcessTermination.ps1')
function New-FaultScript([string]$Name,[string]$Anchor,[string]$Injection) {
    $source=Get-Content -Raw -LiteralPath $RestartScript
    Assert-True (($source.Split(@($Anchor),[StringSplitOptions]::None).Count - 1) -eq 1) "Fault anchor is not unique: $Name"
    $source=$source.Replace($Anchor,$Injection+"`r`n"+$Anchor)
    $path=Join-Path $build ("Restart-$Name.ps1")
    Set-Content -LiteralPath $path -Value $source -Encoding UTF8
    return $path
}
function Assert-ReadOnlyAssessment([string]$Root,[string]$Classification) {
    $before=@{}
    foreach ($file in @(Get-ChildItem (Join-Path $Root 'deployment') -File)) { $before[$file.FullName]=(Get-FileHash $file.FullName).Hash }
    $processes=@{}
    foreach ($p in @(Get-Process)) {
        try { if ($p.Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) { $processes[$p.Id]=$p.StartTime.ToUniversalTime().ToString('o') } } catch { }
    }
    $report=& $AssessmentScript -InstallRoot $Root
    Assert-True ($report.classification -ceq $Classification) "Unexpected assessment: $($report|ConvertTo-Json -Depth 10 -Compress)"
    Assert-True (-not $report.mutationAuthorized -and $report.recoveryRequired) 'Assessment authorized mutation or omitted recovery requirement.'
    $after=@(Get-ChildItem (Join-Path $Root 'deployment') -File)
    Assert-True ($after.Count -eq $before.Count) 'Assessment changed the evidence file inventory.'
    foreach ($file in $after) { Assert-True ($before[$file.FullName] -ceq (Get-FileHash $file.FullName).Hash) 'Assessment changed evidence bytes.' }
    foreach ($id in $processes.Keys) {
        $live=Get-Process -Id $id -ErrorAction Stop
        Assert-True ($live.StartTime.ToUniversalTime().ToString('o') -ceq $processes[$id]) 'Assessment changed a process instance.'
    }
    return $report
}
function Invoke-InterruptedRestart([string]$Root,[string]$OperationScript) {
    $marker=Join-Path $Root 'interruption-marker.json'
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=(Get-Command powershell.exe).Source
    $info.Arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -EdgeStabilizationSeconds 1 -HealthTimeoutSeconds 2 -ShutdownTimeoutSeconds 2' -f $OperationScript,$Root
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

try {
    Invoke-Case 'Executable-E01-E10' {
        $r=New-HealthyRoot E01; $before=Read-Runtime $r
        Invoke-Restart $r; $after=Read-Runtime $r
        Assert-True ($after.deploymentStatus -eq 'Succeeded' -and $after.edge.pid -ne $before.edge.pid) 'Edge was not replaced.'
        Assert-True ($null -eq (Get-Process -Id $before.edge.pid -ErrorAction SilentlyContinue)) 'Old Edge survived.'
        Assert-True ($after.edgeRestart.terminationConfirmed -and $after.edgeRestart.terminationObservations[0].outcome -eq 'Exited') 'Termination was not observed.'
        Assert-True (-not (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Success retained launch intent.'
        Assert-Preservation $before $after; Wait-FixtureHealth $r
    }
    Invoke-Case 'Executable-E02' {
        foreach ($field in @('startTimeUtc','executablePath')) {
            $r=New-HealthyRoot E02; $v=Read-Runtime $r
            if ($field -eq 'startTimeUtc') { $v.edge.startTimeUtc='2000-01-01T00:00:00.0000000Z' }
            else { $v.edge.executablePath=$v.api.executablePath }
            $v|ConvertTo-Json -Depth 30|Set-Content (Join-Path $r 'deployment/runtime.json')
            Assert-Rejected $r
        }
    }
    Invoke-Case 'Executable-E03' {
        foreach ($name in @('api','dashboard')) {
            $r=New-HealthyRoot E03; $v=Read-Runtime $r; $v.$name.startTimeUtc='2000-01-01T00:00:00.0000000Z'
            $v|ConvertTo-Json -Depth 30|Set-Content (Join-Path $r 'deployment/runtime.json'); Assert-Rejected $r
        }
    }
    Invoke-Case 'Executable-E04' {
        $r=New-HealthyRoot E04
        $held=[IO.File]::Open((Join-Path $r 'deployment/deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { Assert-Rejected $r } finally { $held.Dispose() }
    }
    Invoke-Case 'Executable-E05' {
        $r=New-HealthyRoot E05; $ip=Join-Path $r 'deployment/runtime-start.intent.json'
        '{"attemptId":"foreign","processName":"edge"}'|Set-Content $ip; $before=Get-Content -Raw $ip
        Assert-Rejected $r
        Assert-True ($before -ceq (Get-Content -Raw $ip)) 'Foreign intent changed.'
    }
    Invoke-Case 'Executable-E07' {
        $r=New-HealthyRoot E07; $before=Read-Runtime $r
        $path=Join-Path $r 'config/edge.production.json'; $cfg=Get-Content -Raw $path|ConvertFrom-Json
        $cfg.RbsFixtureFailure='edge'; $cfg|ConvertTo-Json -Depth 30|Set-Content $path
        Invoke-Restart $r $true; $after=Read-Runtime $r
        Assert-True ($after.deploymentStatus -eq 'Failed' -and $null -eq $after.edge -and $after.processStates.edge -in @('Absent','Unproven')) 'Exited replacement published ownership.'
        Assert-True (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json')) 'Failed launch lost intent.'
        Assert-Preservation $before $after; Wait-FixtureHealth $r
    }

    Invoke-Case 'Executable-R01-TerminationInterruption' {
        $r=New-HealthyRoot R01; $before=Read-Runtime $r
        $injection='[IO.File]::WriteAllText((Join-Path $root ''interruption-marker.json''), ''{"terminated":true}''); [Threading.Thread]::Sleep([Threading.Timeout]::Infinite)'
        $fault=New-FaultScript R01 '$terminationConfirmed = $true' $injection
        [void](Invoke-InterruptedRestart $r $fault)
        $after=Read-Runtime $r
        Assert-True ($after.deploymentStatus -eq 'EdgeRestarting' -and -not $after.edgeRestart.terminationConfirmed) 'Interruption invented termination publication.'
        Assert-True ($null -eq (Get-Process -Id $before.edge.pid -ErrorAction SilentlyContinue)) 'Old Edge survived termination.'
        Assert-Preservation $before $after
        $report=Assert-ReadOnlyAssessment $r 'AbsentReplacement'
        Assert-True ($report.identityStates.oldEdge -eq 'Absent' -and -not $report.launchIntentPresent) 'Termination interruption was not distinguished.'
    }
    Invoke-Case 'Executable-R02-PostLaunchInterruption' {
        $r=New-HealthyRoot R02; $before=Read-Runtime $r
        $injection='[IO.File]::WriteAllText((Join-Path $root ''interruption-marker.json''), ($replacement|ConvertTo-Json -Depth 10)); [Threading.Thread]::Sleep([Threading.Timeout]::Infinite)'
        $fault=New-FaultScript R02 "`$phase = 'Stabilization'" $injection
        $child=Invoke-InterruptedRestart $r $fault
        $after=Read-Runtime $r
        Assert-True ($null -eq $after.edge -and (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Post-launch interruption lost intent or invented ownership.'
        Assert-Preservation $before $after
        $report=Assert-ReadOnlyAssessment $r 'AmbiguousOwnership'
        Assert-True ($report.candidates.Count -eq 1 -and $report.candidates[0].pid -eq $child.pid -and $report.candidates[0].startTimeUtc -ceq $child.startTimeUtc) 'Exact live candidate was not discovered.'
        $evidence=Get-Content -Raw (Join-Path $r 'deployment/runtime.json')
        Invoke-Restart $r $true
        Assert-True ($evidence -ceq (Get-Content -Raw (Join-Path $r 'deployment/runtime.json'))) 'Retry changed interrupted evidence.'
        Assert-True ((Get-Process -Id $child.pid).StartTime.ToUniversalTime().ToString('o') -ceq $child.startTimeUtc) 'Retry changed the unrecorded child.'
    }
    Invoke-Case 'Executable-R03-OwnershipPublicationFailure' {
        $r=New-HealthyRoot R03; $before=Read-Runtime $r
        $injection='[IO.File]::SetAttributes($runtimePath, ([IO.File]::GetAttributes($runtimePath) -bor [IO.FileAttributes]::ReadOnly)); $script:fixturePublicationLock = [IO.File]::Open($runtimePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)'
        $fault=New-FaultScript R03 "    `$replacementPublished = `$true" $injection
        Invoke-Restart $r $true $fault
        $path=Join-Path $r 'deployment/runtime.json'
        [IO.File]::SetAttributes($path,([IO.File]::GetAttributes($path) -band (-bnot [IO.FileAttributes]::ReadOnly)))
        $after=Read-Runtime $r; Assert-Preservation $before $after
        Assert-True ($null -eq $after.edge -and (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Failed ownership publication lost intent or claimed ownership.'
        $report=Assert-ReadOnlyAssessment $r 'AmbiguousOwnership'
        Assert-True ($report.candidates.Count -eq 1) 'Unrecorded live replacement was not discovered.'
    }
    Invoke-Case 'Executable-R04-FinalPublicationFailure' {
        $r=New-HealthyRoot R04; $before=Read-Runtime $r
        $injection='[IO.File]::SetAttributes($runtimePath, ([IO.File]::GetAttributes($runtimePath) -bor [IO.FileAttributes]::ReadOnly)); $script:fixturePublicationLock = [IO.File]::Open($runtimePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)'
        $fault=New-FaultScript R04 "    Write-JsonFileAtomic `$runtimePath (New-EdgeRestartRuntime 'Succeeded' 'Complete')" $injection
        Invoke-Restart $r $true $fault
        $path=Join-Path $r 'deployment/runtime.json'
        [IO.File]::SetAttributes($path,([IO.File]::GetAttributes($path) -band (-bnot [IO.FileAttributes]::ReadOnly)))
        $after=Read-Runtime $r; Assert-Preservation $before $after
        Assert-True ($after.deploymentStatus -ne 'Succeeded' -and $after.edgeRestart.replacementPublished -and -not (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Final publication failure did not retain its blocked owned state.'
        $report=Assert-ReadOnlyAssessment $r 'VerifiablyOwnedReplacement'
        Assert-True ($report.identityStates.recordedEdge -eq 'Verified') 'Durable replacement could not be verified.'
        Assert-Rejected $r
    }
    Invoke-Case 'Executable-R05-FinalVerificationFailure' {
        $r=New-HealthyRoot R05; $before=Read-Runtime $r
        $fault=New-FaultScript R05 "    `$phase = 'FinalVerification'" "    `$phase = 'FinalVerification'; throw 'Injected final-verification interruption after intent clear.'"
        Invoke-Restart $r $true $fault
        $after=Read-Runtime $r; Assert-Preservation $before $after
        Assert-True ($after.deploymentStatus -eq 'Failed' -and $after.failurePhase -eq 'FinalVerification' -and -not (Test-Path (Join-Path $r 'deployment/runtime-start.intent.json'))) 'Final verification failure was not durably recorded.'
        [void](Assert-ReadOnlyAssessment $r 'VerifiablyOwnedReplacement'); Assert-Rejected $r
    }


    Invoke-Case 'Executable-R06-OwnedWithIntentInterruption' {
        $r=New-HealthyRoot R06; $before=Read-Runtime $r
        $injection='[IO.File]::WriteAllText((Join-Path $root ''interruption-marker.json''), ($replacement|ConvertTo-Json -Depth 10)); [Threading.Thread]::Sleep([Threading.Timeout]::Infinite)'
        $fault=New-FaultScript R06 "    `$phase = 'IntentClear'" $injection
        $child=Invoke-InterruptedRestart $r $fault
        $after=Read-Runtime $r; Assert-Preservation $before $after
        $report=Assert-ReadOnlyAssessment $r 'VerifiablyOwnedReplacement'
        Assert-True ($report.launchIntentPresent -and $report.launchIntentConsistent -and $after.edge.pid -eq $child.pid) 'Owned replacement/intent interruption was not distinguished.'
        Assert-Rejected $r
    }
    Invoke-Case 'Executable-R07-AbsentWithIntent' {
        $r=New-HealthyRoot R07; $before=Read-Runtime $r
        $injection='[IO.File]::WriteAllText((Join-Path $root ''interruption-marker.json''), ($replacement|ConvertTo-Json -Depth 10)); [Threading.Thread]::Sleep([Threading.Timeout]::Infinite)'
        $fault=New-FaultScript R07 "`$phase = 'Stabilization'" $injection
        $child=Invoke-InterruptedRestart $r $fault
        # Fixture-only exit simulation. Never a production recovery action.
        $live=Get-Process -Id $child.pid -ErrorAction Stop
        [void]$live.Handle
        Assert-True ($live.Path -eq $child.executablePath -and $live.StartTime.ToUniversalTime().ToString('o') -ceq $child.startTimeUtc) 'Fixture identity changed before exit simulation.'
        Stop-Process -InputObject $live -ErrorAction Stop
        Assert-True ($live.WaitForExit(5000)) 'Fixture replacement did not exit.'
        $live.Dispose()
        $after=Read-Runtime $r; Assert-Preservation $before $after
        $report=Assert-ReadOnlyAssessment $r 'AbsentReplacement'
        Assert-True ($report.launchIntentPresent -and $report.candidates.Count -eq 0) 'Absent replacement with retained intent was not distinguished.'
        $beforeBytes=Get-Content -Raw (Join-Path $r 'deployment/runtime.json')
        Invoke-Restart $r $true
        Assert-True ($beforeBytes -ceq (Get-Content -Raw (Join-Path $r 'deployment/runtime.json'))) 'Retry bypassed the absent replacement intent barrier.'
    }

    Invoke-Case 'Executable-R08-OldAndReplacementLive' {
        $r=New-HealthyRoot R08; $before=Read-Runtime $r
        # Deliberately contradictory evidence over two real isolated Edge
        # instances: verify the assessor does not hide the surviving old Edge.
        $replacement=Start-Fixture $r edge
        $value=Read-Runtime $r
        $value.edge=$replacement;$value.deploymentStatus='Failed'
        $value|Add-Member -MemberType NoteProperty -Name edgeRestart -Value @{
            schemaVersion='1.0';attemptId='fixture-dual-edge';status='Failed';phase='FinalVerification'
            oldEdge=$before.edge;replacementPublished=$true;terminationConfirmed=$true
            replacementObservation=@{record=$replacement;state='Owned'}
        }
        $value|ConvertTo-Json -Depth 30|Set-Content (Join-Path $r 'deployment/runtime.json')
        Assert-Preservation $before (Read-Runtime $r)
        $report=Assert-ReadOnlyAssessment $r 'AmbiguousOwnership'
        Assert-True ($report.oldAndReplacementLive -and $report.identityStates.oldEdge -eq 'Verified' -and
            $report.identityStates.recordedEdge -eq 'Verified' -and $report.candidates.Count -eq 1 -and
            @($report.reasons|Where-Object{$_ -like 'OldAndReplacementLive:*'}).Count -eq 1) 'Exact dual-Edge conflict was not explicitly classified.'
        Assert-Rejected $r
        $old=Get-Process -Id $before.edge.pid -ErrorAction Stop
        Assert-True ($old.StartTime.ToUniversalTime().ToString('o') -ceq $before.edge.startTimeUtc) 'Assessment/retry changed the old Edge instance.'
    }
    Invoke-Case 'Executable-E11' {
        $r=New-HealthyRoot E11; $v=Read-Runtime $r; $v.selectedRelease='2'*40
        $v|ConvertTo-Json -Depth 30|Set-Content (Join-Path $r 'deployment/runtime.json'); Assert-Rejected $r
    }
    Invoke-Case 'Executable-E12' {
        $r=New-HealthyRoot E12; Invoke-Restart $r; $first=Read-Runtime $r
        Invoke-Restart $r; $second=Read-Runtime $r
        Assert-True ($first.edge.pid -ne $second.edge.pid) 'Explicit new restart did not replace Edge.'
        Assert-True ($first.edgeRestart.attemptId -cne $second.edgeRestart.attemptId) 'Restart reused attempt identity.'
        Assert-Preservation $first $second
        $second.deploymentStatus='EdgeRestarting'
        $second|ConvertTo-Json -Depth 30|Set-Content (Join-Path $r 'deployment/runtime.json'); Assert-Rejected $r
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
        foreach ($file in @(Get-ChildItem -LiteralPath $r -File -Recurse)) {
            [IO.File]::SetAttributes($file.FullName,([IO.File]::GetAttributes($file.FullName) -band (-bnot [IO.FileAttributes]::ReadOnly)))
        }
        Remove-Item -LiteralPath $r -Recurse -Force
    }
    Remove-Item -LiteralPath $build -Recurse -Force
}
[pscustomobject]$results
