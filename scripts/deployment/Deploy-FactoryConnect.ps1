[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter()][string]$InstallRoot = 'D:\FactoryConnect',
    [Parameter()][ValidateRange(1, 300)][int]$EdgeStabilizationSeconds = 5,
    [Parameter()][ValidateRange(1, 300)][int]$HealthTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256 { param([Parameter(Mandatory = $true)][string]$Path); (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-RelativePath { param([string]$Root,[string]$Path); ([System.IO.Path]::GetRelativePath($Root,$Path)).Replace('\','/') }
function Read-JsonFile { param([string]$Path); Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 100 }
function Write-JsonFile { param([string]$Path,$Value); ($Value | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $Path -Encoding utf8 }

function Assert-NoPlaceholders {
    param([string]$Path,[string]$Name)
    $matches = [regex]::Matches((Get-Content -Raw -LiteralPath $Path),'__[A-Z0-9_]+__')
    if ($matches.Count -gt 0) { throw "$Name site configuration is not commissioned. Replace placeholders: $((@($matches | ForEach-Object Value | Sort-Object -Unique)) -join ', ')" }
}

function Assert-NonEmpty { param($Value,[string]$Description); if ([string]::IsNullOrWhiteSpace([string]$Value)) { throw "$Description is required." } }
function Assert-AbsoluteHttpUri {
    param($Value,[string]$Description)
    Assert-NonEmpty $Value $Description
    $uri = $null
    if (-not [Uri]::TryCreate([string]$Value,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -notin @('http','https')) { throw "$Description must be an absolute HTTP/HTTPS URI. Resolved '$Value'." }
    return $uri
}
function Assert-PositiveDuration {
    param($Value,[string]$Description)
    $duration = [TimeSpan]::Zero
    if (-not [TimeSpan]::TryParse([string]$Value,[ref]$duration) -or $duration -le [TimeSpan]::Zero) { throw "$Description must be a positive duration. Resolved '$Value'." }
}
function Assert-SingleHostUrl {
    param($Value,[string]$Description)
    $parts = @(([string]$Value).Split(';',[System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 1) { throw "$Description must contain exactly one URL." }
    [void](Assert-AbsoluteHttpUri $parts[0] $Description)
}

function Assert-SiteConfiguration {
    param($Edge,$Api,$Dashboard)

    if ([string]$Edge.Persistence.Provider -cne 'SqlServer') { throw "Edge Persistence.Provider must be exactly 'SqlServer'." }
    Assert-NonEmpty $Edge.PersistenceProviders.SqlServer.ConnectionString 'Edge SQL connection string'
    if ($null -eq $Edge.MTConnect.Machines -or @($Edge.MTConnect.Machines).Count -lt 1) { throw 'Edge must configure at least one MTConnect machine.' }
    foreach ($machine in @($Edge.MTConnect.Machines)) {
        [void](Assert-AbsoluteHttpUri $machine.BaseUri 'Edge MTConnect machine BaseUri')
        Assert-NonEmpty $machine.MachineId 'Edge MTConnect machine MachineId'
        Assert-NonEmpty $machine.DeviceKey 'Edge MTConnect machine DeviceKey'
        if ($null -ne $machine.PSObject.Properties['PollingInterval']) { Assert-PositiveDuration $machine.PollingInterval 'Edge MTConnect machine PollingInterval' }
    }
    Assert-PositiveDuration $Edge.CurrentState.Freshness.MaximumCurrentAge 'Edge CurrentState.Freshness.MaximumCurrentAge'
    if ($null -eq $Edge.ProductionProcessing.Machines -or @($Edge.ProductionProcessing.Machines).Count -lt 1) { throw 'Edge ProductionProcessing.Machines must contain at least one machine.' }
    foreach ($machine in @($Edge.ProductionProcessing.Machines)) {
        foreach ($property in @('MachineId','ActivityStreamKey','QuantityStreamKey','CompanyId','SiteId','ProductionLineId')) { Assert-NonEmpty $machine.$property "Edge ProductionProcessing machine $property" }
    }

    if ([string]$Api.Persistence.Provider -cne 'SqlServer') { throw "API Persistence.Provider must be exactly 'SqlServer'." }
    Assert-NonEmpty $Api.PersistenceProviders.SqlServer.ConnectionString 'API SQL connection string'
    Assert-SingleHostUrl $Api.Urls 'API Urls'
    if ($null -eq $Api.MTConnect.Machines -or @($Api.MTConnect.Machines).Count -lt 1) { throw 'API must configure at least one MTConnect machine.' }
    foreach ($machine in @($Api.MTConnect.Machines)) {
        [void](Assert-AbsoluteHttpUri $machine.BaseUri 'API MTConnect machine BaseUri')
        Assert-NonEmpty $machine.MachineId 'API MTConnect machine MachineId'
        Assert-NonEmpty $machine.DeviceKey 'API MTConnect machine DeviceKey'
    }
    Assert-PositiveDuration $Api.CurrentState.Freshness.MaximumCurrentAge 'API CurrentState.Freshness.MaximumCurrentAge'

    Assert-SingleHostUrl $Dashboard.Urls 'Dashboard Urls'
    [void](Assert-AbsoluteHttpUri $Dashboard.Dashboard.ReportingApiBaseAddress 'Dashboard.ReportingApiBaseAddress')
    Assert-PositiveDuration $Dashboard.Dashboard.RequestTimeout 'Dashboard.RequestTimeout'
    if ($null -eq $Dashboard.Dashboard.Sources -or @($Dashboard.Dashboard.Sources).Count -lt 1) { throw 'Dashboard must configure at least one source.' }
    foreach ($source in @($Dashboard.Dashboard.Sources)) {
        foreach ($property in @('MachineId','ProcessorId','SiteId','ProductionLineId','DisplayName','GroupName')) { Assert-NonEmpty $source.$property "Dashboard source $property" }
    }
}

function Add-ConfigurationEnvironment {
    param($Value,[string]$Prefix = '',[hashtable]$Environment)
    if ($null -eq $Value) { if ($Prefix) { $Environment[$Prefix] = '' }; return }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) { Add-ConfigurationEnvironment $property.Value $(if ($Prefix) { "$Prefix`__$($property.Name)" } else { $property.Name }) $Environment }
        return
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0; foreach ($item in $Value) { Add-ConfigurationEnvironment $item $(if ($Prefix) { "$Prefix`__$index" } else { [string]$index }) $Environment; $index++ }; return
    }
    if (-not $Prefix) { throw 'A scalar configuration value cannot be projected without a key.' }
    $Environment[$Prefix] = if ($Value -is [bool]) { $Value.ToString().ToLowerInvariant() } else { [string]$Value }
}
function Get-ConfigurationEnvironment { param([string]$Path); $environment=@{}; Add-ConfigurationEnvironment (Read-JsonFile $Path) '' $environment; $environment }

function Get-BaseAddressFromConfiguration {
    param($Configuration,[string]$Name)
    $parts = @(([string]$Configuration.Urls).Split(';',[System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 1) { throw "$Name configuration must define exactly one Urls address." }
    $uri = Assert-AbsoluteHttpUri $parts[0] "$Name Urls"
    if ($uri.Host -in @('0.0.0.0','+','*')) { $builder=[UriBuilder]$uri; $builder.Host='localhost'; return $builder.Uri.AbsoluteUri.TrimEnd('/') }
    $uri.AbsoluteUri.TrimEnd('/')
}

function Start-OwnedProcess {
    param([string]$Executable,[string]$WorkingDirectory,[hashtable]$ConfigurationEnvironment,[string]$StdOutPath,[string]$StdErrPath)
    $saved=@{}
    try {
        foreach ($entry in $ConfigurationEnvironment.GetEnumerator()) { $saved[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process'); [Environment]::SetEnvironmentVariable($entry.Key,[string]$entry.Value,'Process') }
        foreach ($name in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) { $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,'Production','Process') }
        Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory -PassThru -RedirectStandardOutput $StdOutPath -RedirectStandardError $StdErrPath
    }
    finally { foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') } }
}

function Get-RecordedProcessState {
    param($Record)
    if ($null -eq $Record) { return [pscustomobject]@{ State='Absent'; Process=$null } }
    $process = Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return [pscustomobject]@{ State='Absent'; Process=$null } }
    try {
        $samePath = [System.IO.Path]::GetFullPath($process.Path) -eq [System.IO.Path]::GetFullPath([string]$Record.executablePath)
        $sameStart = $process.StartTime.ToUniversalTime().ToString('o') -eq [string]$Record.startTimeUtc
        if ($samePath -and $sameStart) { return [pscustomobject]@{ State='Owned'; Process=$process } }
        return [pscustomobject]@{ State='Mismatch'; Process=$process }
    }
    catch { return [pscustomobject]@{ State='Mismatch'; Process=$process } }
}
function Test-OwnedProcess { param($Record); (Get-RecordedProcessState $Record).State -eq 'Owned' }

function Get-RuntimeRecords {
    param($Runtime)
    $records=[ordered]@{edge=$null;api=$null;dashboard=$null}
    if ($null -eq $Runtime) { return $records }
    foreach ($name in @('edge','api','dashboard')) { if ($null -ne $Runtime.PSObject.Properties[$name]) { $records[$name]=$Runtime.$name } }
    $records
}

function Stop-RecordedProcesses {
    param([System.Collections.IDictionary]$Records)
    foreach ($name in @('dashboard','api','edge')) {
        $record=$Records[$name]; if ($null -eq $record) { continue }
        $state=Get-RecordedProcessState $record
        if ($state.State -eq 'Absent') { $Records[$name]=$null; continue }
        if ($state.State -eq 'Mismatch') { throw "Refusing to stop $name PID $($record.pid): live PID/path/start-time identity does not match the recorded FactoryConnect process." }
        Stop-Process -Id ([int]$record.pid) -ErrorAction Stop
        Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue
        if (Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue) { throw "FactoryConnect $name PID $($record.pid) did not stop within 30 seconds." }
        $Records[$name]=$null
    }
}

function Get-ObservedProcessRecords {
    param([System.Collections.IDictionary]$Primary,[System.Collections.IDictionary]$Secondary)
    $records=[ordered]@{edge=$null;api=$null;dashboard=$null}; $states=[ordered]@{edge='Absent';api='Absent';dashboard='Absent'}
    foreach ($name in @('edge','api','dashboard')) {
        $candidates=@($Primary[$name],$Secondary[$name]) | Where-Object { $null -ne $_ }
        foreach ($record in $candidates) {
            $state=Get-RecordedProcessState $record
            if ($state.State -eq 'Owned') { $records[$name]=$record; $states[$name]='Owned'; break }
            if ($state.State -eq 'Mismatch' -and $states[$name] -ne 'Owned') { $records[$name]=$record; $states[$name]='IdentityMismatch' }
        }
    }
    [pscustomobject]@{Records=$records;States=$states}
}

function Wait-HttpOk {
    param([string]$Uri,[int]$TimeoutSeconds)
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do { try { $response=Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5; if ([int]$response.StatusCode -eq 200) { return $true } } catch {}; Start-Sleep -Milliseconds 500 } while ([DateTime]::UtcNow -lt $deadline)
    throw "Health check did not return HTTP 200 within $TimeoutSeconds seconds: $Uri"
}
function Test-HttpOk { param([string]$Uri); try { $response=Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5; return [int]$response.StatusCode -eq 200 } catch { return $false } }

function Get-ManifestMap {
    param([string]$Root)
    $manifestPath=Join-Path $Root 'MANIFEST.sha256'; if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package is missing MANIFEST.sha256.' }
    $map=@{}
    foreach ($line in Get-Content -LiteralPath $manifestPath) { if (-not $line) { continue }; if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<path>.+)$') { throw "Invalid manifest line: '$line'." }; $path=$Matches.path.Replace('\','/'); if ($map.ContainsKey($path)) { throw "Duplicate manifest path '$path'." }; $map[$path]=$Matches.hash }
    $map
}
function Assert-ContainedRelativePath { param([string]$Path,[string]$Description); if ([System.IO.Path]::IsPathRooted($Path) -or $Path.Contains('..') -or $Path.StartsWith('/') -or $Path.StartsWith('\')) { throw "$Description must be a contained relative path. Resolved '$Path'." } }

function Verify-Package {
    param([string]$Root)
    $releasePath=Join-Path $Root 'release.json'; if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'Package is missing release.json.' }
    $expected=Get-ManifestMap $Root
    $actual=@(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object { (Get-RelativePath $Root $_.FullName) -ne 'MANIFEST.sha256' })
    if ($actual.Count -ne $expected.Count) { throw "Package membership mismatch. Manifest has $($expected.Count) payload files; package has $($actual.Count)." }
    foreach ($file in $actual) { $relative=Get-RelativePath $Root $file.FullName; if (-not $expected.ContainsKey($relative)) { throw "Package contains unmanifested file '$relative'." }; if ((Get-Sha256 $file.FullName) -ne $expected[$relative]) { throw "Package hash mismatch for '$relative'." } }
    $release=Read-JsonFile $releasePath
    if ([string]$release.schemaVersion -ne '1.0' -or [string]$release.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'release.json identity/schema is invalid.' }
    if ([string]$release.publish.runtimeIdentifier -ne 'win-x64' -or -not [bool]$release.publish.selfContained) { throw 'Release is not the frozen win-x64 self-contained profile.' }
    $requiredApps=@{migrations='apps/migrations/FactoryConnect.Migrations.exe';edge='apps/edge/FactoryConnect.Edge.exe';api='apps/api/FactoryConnect.Api.exe';dashboard='apps/dashboard/FactoryConnect.Dashboard.exe'}
    foreach ($name in $requiredApps.Keys) { $app=@($release.applications | Where-Object { [string]$_.name -eq $name }); if ($app.Count -ne 1) { throw "release.json must contain exactly one '$name' application." }; $path=[string]$app[0].executable; Assert-ContainedRelativePath $path "$name executable"; if ($path.Replace('\','/') -cne $requiredApps[$name] -or -not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Invalid or missing $name executable '$path'." } }
    $requiredTemplates=@{edge='config-templates/edge.production.template.json';api='config-templates/api.production.template.json';dashboard='config-templates/dashboard.production.template.json'}
    foreach ($name in $requiredTemplates.Keys) { $template=@($release.configurationTemplates | Where-Object { [string]$_.name -eq $name }); if ($template.Count -ne 1) { throw "release.json must contain exactly one '$name' configuration template." }; $path=[string]$template[0].path; Assert-ContainedRelativePath $path "$name template"; if ($path.Replace('\','/') -cne $requiredTemplates[$name] -or -not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Invalid or missing $name template '$path'." } }
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'Deploy-FactoryConnect.ps1') -PathType Leaf)) { throw 'Package is missing Deploy-FactoryConnect.ps1.' }
    [pscustomobject]@{Release=$release;Manifest=$expected}
}

function Assert-ExactPackageMatch {
    param([string]$IncomingRoot,[string]$InstalledRoot)
    $incoming=Verify-Package $IncomingRoot; $installed=Verify-Package $InstalledRoot
    if ([string]$incoming.Release.sourceCommit -ne [string]$installed.Release.sourceCommit -or $incoming.Manifest.Count -ne $installed.Manifest.Count) { throw 'Installed immutable release differs from incoming package.' }
    foreach ($path in $incoming.Manifest.Keys) { if (-not $installed.Manifest.ContainsKey($path) -or $installed.Manifest[$path] -ne $incoming.Manifest[$path]) { throw "Installed immutable release differs from incoming package at '$path'." } }
}
function Get-CurrentTarget { param([string]$CurrentPath); if (-not (Test-Path -LiteralPath $CurrentPath)) { return $null }; [System.IO.Path]::GetFullPath([string](Get-Item -LiteralPath $CurrentPath -Force).Target) }

function Get-ObservedSelection {
    param([string]$CurrentPath,[string]$OldSelection,[string]$NewSelection)
    try { $target=Get-CurrentTarget $CurrentPath } catch { return [pscustomobject]@{State='Unreadable';Target=$null;Commit=$null;Error=$_.Exception.Message} }
    if ($null -eq $target) { return [pscustomobject]@{State='Absent';Target=$null;Commit=$null;Error=$null} }
    if ($NewSelection -and $target -eq [System.IO.Path]::GetFullPath($NewSelection)) { return [pscustomobject]@{State='New';Target=$target;Commit=(Split-Path -Leaf $target);Error=$null} }
    if ($OldSelection -and $target -eq [System.IO.Path]::GetFullPath($OldSelection)) { return [pscustomobject]@{State='Old';Target=$target;Commit=(Split-Path -Leaf $target);Error=$null} }
    [pscustomobject]@{State='UnexpectedTarget';Target=$target;Commit=$null;Error=$null}
}

function Assert-FinalRuntimeState {
    param([System.Collections.IDictionary]$Records,[string]$CurrentPath,[string]$ExpectedRelease,[string]$ApiBase,[string]$DashboardBase)
    $target=Get-CurrentTarget $CurrentPath
    if ($null -eq $target -or $target -ne [System.IO.Path]::GetFullPath($ExpectedRelease)) { throw "Final current selection does not target the new release. Observed '$target'." }
    foreach ($name in @('edge','api','dashboard')) { if (-not (Test-OwnedProcess $Records[$name])) { throw "Final $name process ownership/liveness verification failed." } }
    if (-not (Test-HttpOk "$ApiBase/health")) { throw 'Final API /health verification failed.' }
    if (-not (Test-HttpOk "$DashboardBase/health/live")) { throw 'Final Dashboard /health/live verification failed.' }
    if (-not (Test-HttpOk "$DashboardBase/health/ready")) { throw 'Final Dashboard /health/ready verification failed.' }
}

$InstallRoot=[System.IO.Path]::GetFullPath($InstallRoot); $PackagePath=[System.IO.Path]::GetFullPath($PackagePath)
$attemptId=[Guid]::NewGuid().ToString('N'); $deploymentRoot=Join-Path $InstallRoot 'deployment'; $logsRoot=Join-Path $deploymentRoot "logs/$attemptId"; $runtimePath=Join-Path $deploymentRoot 'runtime.json'; $lockPath=Join-Path $deploymentRoot 'deployment.lock'; $configRoot=Join-Path $InstallRoot 'config'; $releasesRoot=Join-Path $InstallRoot 'releases'; $currentPath=Join-Path $InstallRoot 'current'
$tempPackageRoot=$null; $lockStream=$null; $sourceCommit=$null; $stagedRelease=$null; $migrationStarted=$false; $migrationSucceeded=$false; $failurePhase='Preparation'; $records=[ordered]@{edge=$null;api=$null;dashboard=$null}; $oldRecords=[ordered]@{edge=$null;api=$null;dashboard=$null}; $newProcesses=[System.Collections.Generic.List[object]]::new(); $oldSelection=$null

try {
    foreach ($directory in @($InstallRoot,$deploymentRoot,$logsRoot,$configRoot,$releasesRoot)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    try { $lockStream=[System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None) } catch { throw "Another FactoryConnect deployment is already active for '$InstallRoot'." }

    if (Test-Path -LiteralPath $PackagePath -PathType Leaf) { if ([System.IO.Path]::GetExtension($PackagePath) -ine '.zip') { throw 'PackagePath file must be a .zip archive.' }; $tempPackageRoot=Join-Path ([System.IO.Path]::GetTempPath()) "FactoryConnectDeploy-$attemptId"; [System.IO.Directory]::CreateDirectory($tempPackageRoot)|Out-Null; Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempPackageRoot; $roots=@(Get-ChildItem -LiteralPath $tempPackageRoot -Directory); if ($roots.Count -ne 1) { throw 'Release ZIP must contain exactly one top-level release directory.' }; $packageRoot=$roots[0].FullName }
    elseif (Test-Path -LiteralPath $PackagePath -PathType Container) { $packageRoot=$PackagePath } else { throw "PackagePath does not exist: '$PackagePath'." }

    $verification=Verify-Package $packageRoot; $release=$verification.Release; $sourceCommit=[string]$release.sourceCommit; $stagedRelease=Join-Path $releasesRoot $sourceCommit
    if (Test-Path -LiteralPath $stagedRelease) { Assert-ExactPackageMatch $packageRoot $stagedRelease } else { $stagingPath=Join-Path $releasesRoot ".$sourceCommit.staging-$attemptId"; Copy-Item -LiteralPath $packageRoot -Destination $stagingPath -Recurse; [System.IO.Directory]::Move($stagingPath,$stagedRelease) }

    $configMap=@{edge='edge.production.json';api='api.production.json';dashboard='dashboard.production.json'}; $createdConfigs=[System.Collections.Generic.List[string]]::new()
    foreach ($name in @('edge','api','dashboard')) { $template=$release.configurationTemplates | Where-Object { [string]$_.name -eq $name } | Select-Object -First 1; $sitePath=Join-Path $configRoot $configMap[$name]; if (-not (Test-Path -LiteralPath $sitePath -PathType Leaf)) { Copy-Item -LiteralPath (Join-Path $stagedRelease ([string]$template.path)) -Destination $sitePath; $createdConfigs.Add($sitePath) } }
    if ($createdConfigs.Count -gt 0) { throw "Created first-install site configuration files: $($createdConfigs -join ', '). Supply commissioning values, then rerun deployment. No runtime was stopped or activated." }

    $edgeConfigPath=Join-Path $configRoot $configMap.edge; $apiConfigPath=Join-Path $configRoot $configMap.api; $dashboardConfigPath=Join-Path $configRoot $configMap.dashboard
    Assert-NoPlaceholders $edgeConfigPath 'Edge'; Assert-NoPlaceholders $apiConfigPath 'API'; Assert-NoPlaceholders $dashboardConfigPath 'Dashboard'
    $edgeConfig=Read-JsonFile $edgeConfigPath; $apiConfig=Read-JsonFile $apiConfigPath; $dashboardConfig=Read-JsonFile $dashboardConfigPath; Assert-SiteConfiguration $edgeConfig $apiConfig $dashboardConfig
    $apiBase=Get-BaseAddressFromConfiguration $apiConfig 'API'; $dashboardBase=Get-BaseAddressFromConfiguration $dashboardConfig 'Dashboard'

    $oldSelection=Get-CurrentTarget $currentPath
    $oldRuntime=if(Test-Path -LiteralPath $runtimePath -PathType Leaf){Read-JsonFile $runtimePath}else{$null}
    $oldRecords=Get-RuntimeRecords $oldRuntime
    if ($oldSelection -eq [System.IO.Path]::GetFullPath($stagedRelease) -and $null -ne $oldRuntime) {
        $owned=(Test-OwnedProcess $oldRuntime.edge) -and (Test-OwnedProcess $oldRuntime.api) -and (Test-OwnedProcess $oldRuntime.dashboard)
        $healthy=$owned -and (Test-HttpOk "$apiBase/health") -and (Test-HttpOk "$dashboardBase/health/live") -and (Test-HttpOk "$dashboardBase/health/ready")
        if ([string]$oldRuntime.releaseCommit -eq $sourceCommit -and [string]$oldRuntime.deploymentStatus -eq 'Succeeded' -and $healthy) { [pscustomobject]@{Status='AlreadyDeployed';SourceCommit=$sourceCommit;ReleasePath=$stagedRelease;CurrentPath=$currentPath;RuntimePath=$runtimePath}; return }
    }

    $failurePhase='StopOldRuntime'; Stop-RecordedProcesses $oldRecords
    if ($oldSelection) { Write-JsonFile $runtimePath ([ordered]@{schemaVersion='1.0';deploymentAttemptId=$attemptId;releaseCommit=(Split-Path -Leaf $oldSelection);releasePath=$oldSelection;deploymentStatus='StoppedForDeployment';runtimeRunning=$false;processStates=[ordered]@{edge='Absent';api='Absent';dashboard='Absent'};edge=$null;api=$null;dashboard=$null}) }

    $failurePhase='Migration'; $migrationStarted=$true
    $migration=$release.applications | Where-Object { $_.name -eq 'migrations' } | Select-Object -First 1; $migrationExecutable=Join-Path $stagedRelease ([string]$migration.executable); $migrationDirectory=Split-Path -Parent $migrationExecutable; $migrationEnvironment=Get-ConfigurationEnvironment $edgeConfigPath; $saved=@{}
    Push-Location $migrationDirectory
    try { foreach ($entry in $migrationEnvironment.GetEnumerator()) { $saved[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process'); [Environment]::SetEnvironmentVariable($entry.Key,[string]$entry.Value,'Process') }; foreach ($name in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) { $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,'Production','Process') }; $migrationOutput=& $migrationExecutable 2>&1; $migrationExitCode=$LASTEXITCODE; $migrationOutput | Set-Content -LiteralPath (Join-Path $logsRoot 'migrations.log'); if ($migrationExitCode -ne 0) { throw "FactoryConnect.Migrations exited with code $migrationExitCode." }; $migrationSucceeded=$true }
    finally { Pop-Location; foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') } }

    $failurePhase='Selection'; $junctionTemp=Join-Path $InstallRoot "current.new-$attemptId"; if (Test-Path -LiteralPath $junctionTemp) { Remove-Item -LiteralPath $junctionTemp -Force }; New-Item -ItemType Junction -Path $junctionTemp -Target $stagedRelease | Out-Null; if (Test-Path -LiteralPath $currentPath) { Remove-Item -LiteralPath $currentPath -Force }; Rename-Item -LiteralPath $junctionTemp -NewName 'current'
    $observedSelection=Get-ObservedSelection $currentPath $oldSelection $stagedRelease
    if ($observedSelection.State -ne 'New') { throw "Selection did not result in current targeting the staged release. Observed state '$($observedSelection.State)', target '$($observedSelection.Target)'." }
    Write-JsonFile $runtimePath ([ordered]@{schemaVersion='1.0';deploymentAttemptId=$attemptId;releaseCommit=$sourceCommit;releasePath=$stagedRelease;selectedAtUtc=[DateTime]::UtcNow.ToString('o');deploymentStatus='Starting';runtimeRunning=$false;processStates=[ordered]@{edge='Absent';api='Absent';dashboard='Absent'};edge=$null;api=$null;dashboard=$null})

    $failurePhase='Startup'
    foreach ($name in @('edge','api','dashboard')) {
        $application=$release.applications | Where-Object { $_.name -eq $name } | Select-Object -First 1; $executable=Join-Path $stagedRelease ([string]$application.executable); $configPath=switch($name){'edge'{$edgeConfigPath};'api'{$apiConfigPath};'dashboard'{$dashboardConfigPath}}
        $process=Start-OwnedProcess $executable (Split-Path -Parent $executable) (Get-ConfigurationEnvironment $configPath) (Join-Path $logsRoot "$name.stdout.log") (Join-Path $logsRoot "$name.stderr.log"); $newProcesses.Add($process); $process.Refresh(); $records[$name]=[ordered]@{pid=$process.Id;executablePath=[System.IO.Path]::GetFullPath($executable);startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o')}
        Write-JsonFile $runtimePath ([ordered]@{schemaVersion='1.0';deploymentAttemptId=$attemptId;releaseCommit=$sourceCommit;releasePath=$stagedRelease;selectedAtUtc=[DateTime]::UtcNow.ToString('o');deploymentStatus='Starting';runtimeRunning=$true;processStates=[ordered]@{edge=$(if($null -ne $records.edge){'Owned'}else{'Absent'});api=$(if($null -ne $records.api){'Owned'}else{'Absent'});dashboard=$(if($null -ne $records.dashboard){'Owned'}else{'Absent'})};edge=$records.edge;api=$records.api;dashboard=$records.dashboard})
        if ($name -eq 'edge') { Start-Sleep -Seconds $EdgeStabilizationSeconds; if ($process.HasExited) { throw "Edge exited during the $EdgeStabilizationSeconds-second stabilization period." } } elseif ($name -eq 'api') { [void](Wait-HttpOk "$apiBase/health" $HealthTimeoutSeconds) } else { [void](Wait-HttpOk "$dashboardBase/health/live" $HealthTimeoutSeconds); [void](Wait-HttpOk "$dashboardBase/health/ready" $HealthTimeoutSeconds) }
    }
    $failurePhase='FinalVerification'; Assert-FinalRuntimeState $records $currentPath $stagedRelease $apiBase $dashboardBase
    Write-JsonFile $runtimePath ([ordered]@{schemaVersion='1.0';deploymentAttemptId=$attemptId;releaseCommit=$sourceCommit;releasePath=$stagedRelease;selectedAtUtc=[DateTime]::UtcNow.ToString('o');deploymentStatus='Succeeded';runtimeRunning=$true;processStates=[ordered]@{edge='Owned';api='Owned';dashboard='Owned'};edge=$records.edge;api=$records.api;dashboard=$records.dashboard})
    [pscustomobject]@{Status='Succeeded';SourceCommit=$sourceCommit;ReleasePath=$stagedRelease;CurrentPath=$currentPath;RuntimePath=$runtimePath;LogsPath=$logsRoot}
}
catch {
    $primaryError=$_.Exception.Message; $cleanupFailures=[System.Collections.Generic.List[string]]::new()
    for ($index=$newProcesses.Count-1;$index -ge 0;$index--) { $process=$newProcesses[$index]; try { if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop; Wait-Process -Id $process.Id -Timeout 30 -ErrorAction SilentlyContinue }; if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { $cleanupFailures.Add("PID $($process.Id) survived cleanup.") } } catch { $cleanupFailures.Add("PID $($process.Id) cleanup failed: $($_.Exception.Message)") } }

    $observedProcesses=Get-ObservedProcessRecords $records $oldRecords
    $observedSelection=Get-ObservedSelection $currentPath $oldSelection $stagedRelease
    $selectedRelease=$observedSelection.Target
    $selectedCommit=$observedSelection.Commit
    $runtimeRunning=@($observedProcesses.States.Values | Where-Object { $_ -eq 'Owned' }).Count -gt 0
    $unresolvedIdentity=@($observedProcesses.States.Values | Where-Object { $_ -eq 'IdentityMismatch' }).Count -gt 0
    $failure=[ordered]@{schemaVersion='1.0';deploymentAttemptId=$attemptId;releaseCommit=$selectedCommit;releasePath=$selectedRelease;selectionState=$observedSelection.State;selectionObservationError=$observedSelection.Error;deploymentStatus='Failed';runtimeRunning=$runtimeRunning;runtimeIdentityUnresolved=$unresolvedIdentity;failurePhase=$failurePhase;migrationOutcome=$(if(-not $migrationStarted){'NotStarted'}elseif($migrationSucceeded){'Succeeded'}else{'Failed'});databaseMayHaveChanged=($migrationStarted -and -not $migrationSucceeded);failedAtUtc=[DateTime]::UtcNow.ToString('o');processStates=$observedProcesses.States;edge=$observedProcesses.Records.edge;api=$observedProcesses.Records.api;dashboard=$observedProcesses.Records.dashboard;cleanupFailures=@($cleanupFailures);error=$primaryError}
    Write-JsonFile (Join-Path $logsRoot 'deployment-failure.json') $failure
    if ($migrationStarted -or $oldSelection -or $null -ne $selectedRelease -or $runtimeRunning -or $unresolvedIdentity) { Write-JsonFile $runtimePath $failure }
    throw
}
finally {
    if ($null -ne $lockStream) { $lockStream.Dispose() }
    if ($null -ne $tempPackageRoot -and (Test-Path -LiteralPath $tempPackageRoot)) { Remove-Item -LiteralPath $tempPackageRoot -Force -Recurse -ErrorAction SilentlyContinue }
}
