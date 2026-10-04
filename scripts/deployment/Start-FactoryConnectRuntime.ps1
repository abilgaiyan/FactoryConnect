[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot,
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
        if ($livePath -eq $expectedPath -and [Math]::Abs(($liveStart - $expectedStart).TotalSeconds) -lt 1.0) {
            return [pscustomobject]@{ State='Owned'; Process=$p }
        }
        [pscustomobject]@{ State='Mismatch'; Process=$p }
    } catch { [pscustomobject]@{ State='Mismatch'; Process=$p } }
}

function Test-HttpOk([string] $Uri) {
    try { ([int](Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) } catch { $false }
}

function Wait-HttpOk([string] $Uri, [int] $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-HttpOk $Uri) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "Health check did not return HTTP 200 within ${TimeoutSeconds}s: $Uri"
}

function Start-RuntimeProcess([string] $Name, [string] $Executable, [string] $WorkingDirectory, [hashtable] $Environment, [string] $LogDirectory) {
    foreach ($key in $Environment.Keys) { [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process') }
    $p = Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory -PassThru -RedirectStandardOutput (Join-Path $LogDirectory "$Name.out.log") -RedirectStandardError (Join-Path $LogDirectory "$Name.err.log")
    Start-Sleep -Milliseconds 100
    $p.Refresh()
    [pscustomobject]@{ name=$Name; pid=$p.Id; executablePath=(Get-CanonicalPath $Executable); startTimeUtc=$p.StartTime.ToUniversalTime().ToString('o') }
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
            $i=0; foreach ($item in $v) {
                $itemKey="$key`__$i"
                if ($item -is [System.Management.Automation.PSCustomObject]) { Add-ConfigurationEnvironment $Target $item $itemKey } else { $Target[$itemKey]=[string]$item }
                $i++
            }
            continue
        }
        $Target[$key]=[string]$v
    }
}

$root = Get-CanonicalPath $InstallRoot
$current = Join-Path $root 'current'
$configRoot = Join-Path $root 'config'
$deploymentRoot = Join-Path $root 'deployment'
$runtimePath = Join-Path $deploymentRoot 'runtime.json'
$lockPath = Join-Path $deploymentRoot 'deployment.lock'
New-Item -ItemType Directory -Path $deploymentRoot -Force | Out-Null

$lock=$null
try {
    $lock=[System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None)
    if (-not (Test-Path -LiteralPath $current)) { throw "Selected release junction is missing: $current" }
    $currentItem=Get-Item -LiteralPath $current -Force
    if ($currentItem.LinkType -ne 'Junction' -or $null -eq $currentItem.Target -or @($currentItem.Target).Count -ne 1) { throw "Selected release must be exactly one junction target: $current" }
    $release=Get-CanonicalPath ([string]@($currentItem.Target)[0])
    $releasesRoot=Get-CanonicalPath (Join-Path $root 'releases')
    if (-not $release.StartsWith($releasesRoot + [System.IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Selected release is outside releases root: $release" }
    $releaseId=Split-Path -Leaf $release
    if ($releaseId -notmatch '^[0-9a-fA-F]{40}$') { throw "Selected release identity is invalid: $releaseId" }

    $configs=@{}
    foreach ($name in @('edge','api','dashboard')) {
        $path=Join-Path $configRoot "$name.production.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Commissioned configuration is required: $path" }
        $configs[$name]=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }

    $runtime=if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json } else { $null }
    $records=@{}; $states=@{}
    foreach ($name in @('edge','api','dashboard')) {
        $records[$name]=if ($null -ne $runtime) { $runtime.$name } else { $null }
        $states[$name]=Get-ProcessIdentity $records[$name]
    }
    if (@($states.Values | Where-Object State -eq 'Mismatch').Count -gt 0) { throw 'Runtime identity mismatch detected; no process will be stopped or started.' }

    $apiHealth='http://127.0.0.1:5080/health'; $dashboardLive='http://127.0.0.1:5080/health/live'; $dashboardReady='http://127.0.0.1:5080/health/ready'
    $allOwned=@($states.Values | Where-Object State -eq 'Owned').Count -eq 3
    if ($allOwned -and (Test-HttpOk $apiHealth) -and (Test-HttpOk $dashboardLive) -and (Test-HttpOk $dashboardReady)) {
        [pscustomobject]@{ Status='AlreadyRunning'; SourceCommit=$releaseId; InstallRoot=$root; Current=$current }; return
    }

    foreach ($name in @('dashboard','api','edge')) { if ($states[$name].State -eq 'Owned') { Stop-RecordedOwnedProcess $records[$name] } }

    $attemptId=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $logDirectory=Join-Path (Join-Path $deploymentRoot 'logs') $attemptId
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $envs=@{}
    foreach ($name in @('edge','api','dashboard')) {
        $e=@{ DOTNET_ENVIRONMENT='Production'; ASPNETCORE_ENVIRONMENT='Production' }
        Add-ConfigurationEnvironment $e $configs[$name]
        $envs[$name]=$e
    }

    $edgeExe=Join-Path $release 'apps\edge\FactoryConnect.Edge.exe'; $apiExe=Join-Path $release 'apps\api\FactoryConnect.Api.exe'; $dashboardExe=Join-Path $release 'apps\dashboard\FactoryConnect.Dashboard.exe'
    foreach ($p in @($edgeExe,$apiExe,$dashboardExe)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Selected release executable is missing: $p" } }

    $edge=Start-RuntimeProcess 'edge' $edgeExe (Split-Path $edgeExe) $envs.edge $logDirectory
    Start-Sleep -Seconds $EdgeStabilizationSeconds
    if ((Get-ProcessIdentity $edge).State -ne 'Owned') { throw 'Edge did not survive stabilization with expected ownership.' }
    $api=Start-RuntimeProcess 'api' $apiExe (Split-Path $apiExe) $envs.api $logDirectory
    Wait-HttpOk $apiHealth $HealthTimeoutSeconds
    $dashboard=Start-RuntimeProcess 'dashboard' $dashboardExe (Split-Path $dashboardExe) $envs.dashboard $logDirectory
    Wait-HttpOk $dashboardLive $HealthTimeoutSeconds; Wait-HttpOk $dashboardReady $HealthTimeoutSeconds
    foreach ($record in @($edge,$api,$dashboard)) { if ((Get-ProcessIdentity $record).State -ne 'Owned') { throw "Final ownership verification failed for $($record.name)." } }

    [ordered]@{ schemaVersion='1.0'; selectedRelease=$releaseId; releasePath=$release; deploymentAttemptId=$attemptId; deploymentStatus='Succeeded'; updatedAtUtc=[DateTime]::UtcNow.ToString('o'); runtimeRunning=$true; failurePhase=''; migrationOutcome='NotRun'; databaseMayHaveChanged=$false; processStates=[ordered]@{edge='Owned';api='Owned';dashboard='Owned'}; edge=$edge; api=$api; dashboard=$dashboard } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runtimePath -Encoding UTF8
    [pscustomobject]@{ Status='Succeeded'; SourceCommit=$releaseId; InstallRoot=$root; Current=$current }
}
finally { if ($null -ne $lock) { $lock.Dispose() } }
