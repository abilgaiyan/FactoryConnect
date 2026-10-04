[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $InstallRoot,
    [int] $EdgeStabilizationSeconds = 5,
    [int] $HealthTimeoutSeconds = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CanonicalPath([string] $Path) {
    [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}
function Get-ProcessIdentity([object] $Record) {
    if ($null -eq $Record -or $null -eq $Record.pid) { return [pscustomobject]@{ State='Absent'; Process=$null } }
    $p = Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
    if ($null -eq $p) { return [pscustomobject]@{ State='Absent'; Process=$null } }
    try {
        $livePath = Get-CanonicalPath $p.Path
        $expectedPath = Get-CanonicalPath ([string]$Record.executablePath)
        $liveStart = $p.StartTime.ToUniversalTime()
        $expectedStart = [DateTimeOffset]::Parse([string]$Record.startTimeUtc).UtcDateTime
        if ($livePath -eq $expectedPath -and [Math]::Abs(($liveStart - $expectedStart).TotalSeconds) -lt 1.0) { return [pscustomobject]@{ State='Owned'; Process=$p } }
        return [pscustomobject]@{ State='Mismatch'; Process=$p }
    } catch { return [pscustomobject]@{ State='Mismatch'; Process=$p } }
}
function Test-HttpOk([string] $Uri) { try { return ([int](Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) } catch { return $false } }
function Wait-HttpOk([string] $Uri, [int] $Timeout) {
    $deadline = (Get-Date).AddSeconds($Timeout)
    do { if (Test-HttpOk $Uri) { return }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
    throw "Health check did not return HTTP 200 within ${Timeout}s: $Uri"
}
function Stop-RecordedOwnedProcess([object] $Record) {
    $identity = Get-ProcessIdentity $Record
    if ($identity.State -eq 'Absent') { return }
    if ($identity.State -ne 'Owned') { throw "Process ownership changed before termination for $($Record.name); refusing PID $($Record.pid)." }
    Stop-Process -Id ([int]$Record.pid) -ErrorAction Stop
    Wait-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
}
function Add-ConfigurationEnvironment([hashtable] $Target, [object] $Value, [string] $Prefix='') {
    if ($null -eq $Value) { return }
    foreach ($property in $Value.PSObject.Properties) {
        $key = if ([string]::IsNullOrEmpty($Prefix)) { $property.Name } else { "$Prefix`__$($property.Name)" }
        $v = $property.Value
        if ($null -eq $v) { continue }
        if ($v -is [System.Management.Automation.PSCustomObject]) { Add-ConfigurationEnvironment $Target $v $key; continue }
        if ($v -is [System.Collections.IEnumerable] -and $v -isnot [string]) {
            $i = 0
            foreach ($item in $v) {
                $itemKey = "$key`__$i"
                if ($item -is [System.Management.Automation.PSCustomObject]) { Add-ConfigurationEnvironment $Target $item $itemKey } else { $Target[$itemKey] = [string]$item }
                $i++
            }
            continue
        }
        $Target[$key] = [string]$v
    }
}
function Start-RuntimeProcess([string] $Name, [string] $Exe, [hashtable] $Env, [string] $Log) {
    foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, [string]$Env[$k], 'Process') }
    $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru -RedirectStandardOutput (Join-Path $Log "$Name.out.log") -RedirectStandardError (Join-Path $Log "$Name.err.log")
    Start-Sleep -Milliseconds 100; $p.Refresh()
    [pscustomobject]@{ name=$Name; pid=$p.Id; executablePath=(Get-CanonicalPath $Exe); startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o') }
}

$root = Get-CanonicalPath $InstallRoot
$current = Join-Path $root 'current'
$configRoot = Join-Path $root 'config'
$deploymentRoot = Join-Path $root 'deployment'
$runtimePath = Join-Path $deploymentRoot 'runtime.json'
$lockPath = Join-Path $deploymentRoot 'deployment.lock'
New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null
$lock = $null; $started = @()
try {
    $lock = [System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None)
    if (-not (Test-Path -LiteralPath $current)) { throw "Selected release junction is missing: $current" }
    $currentItem = Get-Item -LiteralPath $current -Force
    if ($currentItem.LinkType -ne 'Junction' -or $null -eq $currentItem.Target -or @($currentItem.Target).Count -ne 1) { throw "Selected release must be exactly one junction target: $current" }
    $release = Get-CanonicalPath ([string]@($currentItem.Target)[0])
    $releasesRoot = Get-CanonicalPath (Join-Path $root 'releases')
    if (-not ($release.StartsWith($releasesRoot + [System.IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))) { throw "Selected release is outside releases root: $release" }
    $releaseId = Split-Path -Leaf $release
    if ($releaseId -notmatch '^[0-9a-fA-F]{40}$') { throw "Selected release identity is invalid: $releaseId" }

    $configs = @{}
    foreach ($name in @('edge','api','dashboard')) {
        $path = Join-Path $configRoot "$name.production.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Commissioned configuration is required: $path" }
        $configs[$name] = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }

    $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json } else { $null }
    $records = @{}; $states = @{}
    foreach ($name in @('edge','api','dashboard')) {
        $records[$name] = if ($null -ne $runtime) { $runtime.$name } else { $null }
        $states[$name] = Get-ProcessIdentity $records[$name]
    }
    if (@($states.Values | Where-Object State -eq 'Mismatch').Count -gt 0) { throw 'Runtime identity mismatch detected; no process will be stopped or started.' }

    $apiHealth='http://127.0.0.1:5080/health'; $live='http://127.0.0.1:5080/health/live'; $ready='http://127.0.0.1:5080/health/ready'
    $allOwned = @($states.Values | Where-Object State -eq 'Owned').Count -eq 3
    if ($allOwned -and (Test-HttpOk $apiHealth) -and (Test-HttpOk $live) -and (Test-HttpOk $ready)) { [pscustomobject]@{ Status='AlreadyRunning'; SourceCommit=$releaseId; InstallRoot=$root; Current=$current }; return }

    foreach ($name in @('dashboard','api','edge')) { if ($states[$name].State -eq 'Owned') { Stop-RecordedOwnedProcess $records[$name] } }

    $attempt = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $log = Join-Path (Join-Path $deploymentRoot 'logs') $attempt
    New-Item -ItemType Directory -Path $log -Force | Out-Null
    $envs = @{}
    foreach ($name in @('edge','api','dashboard')) {
        $e = @{ DOTNET_ENVIRONMENT='Production'; ASPNETCORE_ENVIRONMENT='Production' }
        Add-ConfigurationEnvironment $e $configs[$name]
        $envs[$name] = $e
    }
    $edgeExe=Join-Path $release 'apps\edge\FactoryConnect.Edge.exe'; $apiExe=Join-Path $release 'apps\api\FactoryConnect.Api.exe'; $dashExe=Join-Path $release 'apps\dashboard\FactoryConnect.Dashboard.exe'
    foreach ($p in @($edgeExe,$apiExe,$dashExe)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Selected release executable is missing: $p" } }

    $edge=Start-RuntimeProcess 'edge' $edgeExe $envs.edge $log; $started += ,$edge
    Start-Sleep -Seconds $EdgeStabilizationSeconds
    if ((Get-ProcessIdentity $edge).State -ne 'Owned') { throw 'Edge did not survive stabilization with expected ownership.' }
    $api=Start-RuntimeProcess 'api' $apiExe $envs.api $log; $started += ,$api
    Wait-HttpOk $apiHealth $HealthTimeoutSeconds
    $dash=Start-RuntimeProcess 'dashboard' $dashExe $envs.dashboard $log; $started += ,$dash
    Wait-HttpOk $live $HealthTimeoutSeconds; Wait-HttpOk $ready $HealthTimeoutSeconds
    foreach ($record in @($edge,$api,$dash)) { if ((Get-ProcessIdentity $record).State -ne 'Owned') { throw "Final ownership verification failed for $($record.name)." } }

    [ordered]@{ schemaVersion='1.0'; selectedRelease=$releaseId; releasePath=$release; deploymentAttemptId=$attempt; deploymentStatus='Succeeded'; updatedAtUtc=[DateTime]::UtcNow.ToString('o'); runtimeRunning=$true; failurePhase=''; migrationOutcome='NotRun'; databaseMayHaveChanged=$false; processStates=[ordered]@{edge='Owned';api='Owned';dashboard='Owned'}; edge=$edge; api=$api; dashboard=$dash } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runtimePath -Encoding UTF8
    [pscustomobject]@{ Status='Succeeded'; SourceCommit=$releaseId; InstallRoot=$root; Current=$current }
} catch {
    foreach ($record in @($started | Select-Object -Reverse)) { try { Stop-RecordedOwnedProcess $record } catch { } }
    throw
} finally { if ($null -ne $lock) { $lock.Dispose() } }
