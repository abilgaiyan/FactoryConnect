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
function Get-RelativePath {
    param([string]$Root,[string]$Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Path '$Path' is outside root '$Root'." }
    $pathFull.Substring($prefix.Length).Replace('\','/')
}
function Read-JsonFile { param([string]$Path); Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
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
    foreach ($machine in @($Edge.ProductionProcessing.Machines)) { foreach ($property in @('MachineId','ActivityStreamKey','QuantityStreamKey','CompanyId','SiteId','ProductionLineId')) { Assert-NonEmpty $machine.$property "Edge ProductionProcessing machine $property" } }

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
    foreach ($source in @($Dashboard.Dashboard.Sources)) { foreach ($property in @('MachineId','ProcessorId','SiteId','ProductionLineId','DisplayName','GroupName')) { Assert-NonEmpty $source.$property "Dashboard source $property" } }
}

function Add-ConfigurationEnvironment {
    param($Value,[string]$Prefix = '',[hashtable]$Environment)
    if ($null -eq $Value) { if ($Prefix) { $Environment[$Prefix] = '' }; return }
    if ($Value -is [System.Management.Automation.PSCustomObject]) { foreach ($property in $Value.PSObject.Properties) { Add-ConfigurationEnvironment $property.Value $(if ($Prefix) { "$Prefix`__$($property.Name)" } else { $property.Name }) $Environment }; return }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) { $index = 0; foreach ($item in $Value) { Add-ConfigurationEnvironment $item $(if ($Prefix) { "$Prefix`__$index" } else { [string]$index }) $Environment; $index++ }; return }
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
    [ordered]@{ edge = if ($null -ne $Runtime) { $Runtime.edge } else { $null }; api = if ($null -ne $Runtime) { $Runtime.api } else { $null }; dashboard = if ($null -ne $Runtime) { $Runtime.dashboard } else { $null } }
}
function Get-ObservedProcessRecords {
    param($NewRecords,$OldRecords)
    $records=[ordered]@{edge=$null;api=$null;dashboard=$null}; $states=[ordered]@{edge='Absent';api='Absent';dashboard='Absent'}
    foreach ($name in @('edge','api','dashboard')) {
        $candidates=@($NewRecords[$name],$OldRecords[$name]) | Where-Object { $null -ne $_ }
        foreach ($record in $candidates) {
            $state=Get-RecordedProcessState $record
            if ($state.State -eq 'Owned') { $records[$name]=$record; $states[$name]='Owned'; break }
            if ($state.State -eq 'Mismatch' -and $states[$name] -ne 'Owned') { $records[$name]=$record; $states[$name]='IdentityMismatch' }
        }
    }
    [pscustomobject]@{Records=$records;States=$states}
}
function Stop-RecordedProcesses {
    param($Records)
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
    $deployment=[string]$release.deploymentScript; Assert-ContainedRelativePath $deployment 'deployment script'; if ($deployment.Replace('\','/') -cne 'Deploy-FactoryConnect.ps1' -or -not (Test-Path -LiteralPath (Join-Path $Root $deployment) -PathType Leaf)) { throw 'release.json deploymentScript is invalid.' }
    [pscustomobject]@{Release=$release;Manifest=$expected}
}
function Verify-InstalledReleaseAgainstIncoming {
    param([string]$InstalledRoot,$IncomingManifest)
    $installedManifestPath=Join-Path $InstalledRoot 'MANIFEST.sha256'
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) { throw 'Existing immutable release directory is missing MANIFEST.sha256.' }
    $installedManifest=Get-ManifestMap $InstalledRoot
    if ($installedManifest.Count -ne $IncomingManifest.Count) { throw 'Existing immutable release manifest conflicts with incoming package membership.' }
    foreach ($path in $IncomingManifest.Keys) {
        if (-not $installedManifest.ContainsKey($path) -or $installedManifest[$path] -cne $IncomingManifest[$path]) { throw "Existing immutable release manifest conflicts with incoming package at '$path'." }
    }
    $actual=@(Get-ChildItem -LiteralPath $InstalledRoot -File -Recurse | Where-Object { (Get-RelativePath $InstalledRoot $_.FullName) -ne 'MANIFEST.sha256' })
    if ($actual.Count -ne $IncomingManifest.Count) { throw 'Existing immutable release directory conflicts with incoming package membership.' }
    foreach ($file in $actual) { $relative=Get-RelativePath $InstalledRoot $file.FullName; if (-not $IncomingManifest.ContainsKey($relative) -or (Get-Sha256 $file.FullName) -ne $IncomingManifest[$relative]) { throw "Existing immutable release directory conflicts with incoming package at '$relative'." } }
}

function Get-ProcessRecord { param([string]$Name,$Process,[string]$Executable); [ordered]@{name=$Name;pid=$Process.Id;executablePath=[System.IO.Path]::GetFullPath($Executable);startTimeUtc=$Process.StartTime.ToUniversalTime().ToString('o')} }
function Get-SelectedReleaseState {
    param([string]$CurrentPath,[string]$ReleasesRoot)
    if (-not (Test-Path -LiteralPath $CurrentPath)) { return [pscustomobject]@{Kind='Absent';Release=$null;Target=$null} }
    try {
        $item=Get-Item -LiteralPath $CurrentPath -Force
        $target=@($item.Target)[0]
        if ([string]::IsNullOrWhiteSpace([string]$target)) { return [pscustomobject]@{Kind='Unexpected';Release=$null;Target=$null} }
        if (-not [System.IO.Path]::IsPathRooted([string]$target)) { $target=Join-Path (Split-Path $CurrentPath -Parent) ([string]$target) }
        $targetFull=[System.IO.Path]::GetFullPath([string]$target).TrimEnd('\')
        $releasesFull=[System.IO.Path]::GetFullPath($ReleasesRoot).TrimEnd('\')
        $prefix=$releasesFull + '\'
        if (-not $targetFull.StartsWith($prefix,[System.StringComparison]::OrdinalIgnoreCase)) { return [pscustomobject]@{Kind='Unexpected';Release=$null;Target=$targetFull} }
        $relative=$targetFull.Substring($prefix.Length)
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or $relative.Contains('/') -or $relative -cnotmatch '^[0-9a-f]{40}$') { return [pscustomobject]@{Kind='Unexpected';Release=$null;Target=$targetFull} }
        [pscustomobject]@{Kind='Release';Release=$relative;Target=$targetFull}
    }
    catch { [pscustomobject]@{Kind='Unreadable';Release=$null;Target=$null} }
}
function Assert-CurrentTargetsRelease {
    param([string]$CurrentPath,[string]$ReleasesRoot,[string]$Expected)
    $state=Get-SelectedReleaseState $CurrentPath $ReleasesRoot
    $expectedTarget=[System.IO.Path]::GetFullPath((Join-Path $ReleasesRoot $Expected)).TrimEnd('\')
    if ($state.Kind -ne 'Release' -or $state.Release -cne $Expected -or -not [string]::Equals([string]$state.Target,$expectedTarget,[System.StringComparison]::OrdinalIgnoreCase)) { throw "Current selection is not '$Expected'. Observed kind '$($state.Kind)', release '$($state.Release)', target '$($state.Target)'." }
}

function Remove-ActivationJunction {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item=Get-Item -LiteralPath $Path -Force
    if ([string]$item.LinkType -cne 'Junction') { throw "Refusing to remove activation path '$Path' because it is not a junction." }
    & cmd.exe /d /c rmdir ('"' + $Path + '"')
    if ($LASTEXITCODE -ne 0) { throw "Failed to remove activation junction '$Path'. rmdir exited with code $LASTEXITCODE." }
    if (Test-Path -LiteralPath $Path) { throw "Activation junction '$Path' still exists after removal." }
}

function New-RuntimeState {
    param([string]$SelectedRelease,[string]$ReleasePath,[string]$AttemptId,[string]$Status,$Records,[string]$FailurePhase=$null,[string]$MigrationOutcome=$null,[bool]$DatabaseMayHaveChanged=$false,$ProcessStates=$null)
    $verifiedRunning=$false
    if ($null -ne $ProcessStates) {
        foreach ($name in @('edge','api','dashboard')) {
            if ([string]$ProcessStates[$name] -eq 'Owned') { $verifiedRunning=$true; break }
        }
    }
    elseif ($Status -eq 'Succeeded') { $verifiedRunning=$true }
    [ordered]@{schemaVersion='1.0';selectedRelease=$SelectedRelease;releasePath=$ReleasePath;deploymentAttemptId=$AttemptId;deploymentStatus=$Status;updatedAtUtc=[DateTime]::UtcNow.ToString('o');runtimeRunning=$verifiedRunning;failurePhase=$FailurePhase;migrationOutcome=$MigrationOutcome;databaseMayHaveChanged=$DatabaseMayHaveChanged;processStates=$ProcessStates;edge=$Records.edge;api=$Records.api;dashboard=$Records.dashboard}
}

$install=[System.IO.Path]::GetFullPath($InstallRoot)
$deployment=Join-Path $install 'deployment'; $logs=Join-Path $deployment 'logs'; $config=Join-Path $install 'config'; $releases=Join-Path $install 'releases'; $current=Join-Path $install 'current'; $runtimePath=Join-Path $deployment 'runtime.json'
New-Item -ItemType Directory -Force -Path $deployment,$logs | Out-Null
$attemptId=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'); $attemptLog=Join-Path $logs $attemptId; New-Item -ItemType Directory -Force -Path $attemptLog | Out-Null; $failurePath=Join-Path $attemptLog 'deployment-failure.json'
$lockPath=Join-Path $deployment 'deployment.lock'; $lockStream=$null; $lockAcquired=$false; $runtimeDisruptionStarted=$false; $migrationStarted=$false; $migrationCompleted=$false; $failurePhase='Preparation'; $newRecords=[ordered]@{edge=$null;api=$null;dashboard=$null}; $oldRecords=[ordered]@{edge=$null;api=$null;dashboard=$null}; $oldSelected=$null; $targetRelease=$null; $targetReleasePath=$null; $packageRoot=$null; $packageExtractRoot=$null
try {
    try { $lockStream=[System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None); $lockAcquired=$true } catch { throw "Another FactoryConnect deployment holds '$lockPath'." }

    $resolvedPackage=[System.IO.Path]::GetFullPath($PackagePath)
    if (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($resolvedPackage) -ine '.zip') { throw "PackagePath file must be a .zip archive. Resolved '$resolvedPackage'." }
        $packageExtractRoot=Join-Path $attemptLog 'package'
        New-Item -ItemType Directory -Force -Path $packageExtractRoot | Out-Null
        Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $packageExtractRoot
        $candidates=@(Get-ChildItem -LiteralPath $packageExtractRoot -Directory)
        if ($candidates.Count -ne 1 -or @(Get-ChildItem -LiteralPath $packageExtractRoot -File).Count -ne 0) { throw 'Release archive must contain exactly one top-level package directory.' }
        $packageRoot=$candidates[0].FullName
    }
    elseif (Test-Path -LiteralPath $resolvedPackage -PathType Container) { $packageRoot=$resolvedPackage }
    else { throw "PackagePath '$resolvedPackage' does not exist." }

    $verified=Verify-Package $packageRoot; $release=$verified.Release; $targetRelease=[string]$release.sourceCommit; $targetReleasePath=Join-Path $releases $targetRelease
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    if (Test-Path -LiteralPath $targetReleasePath) { Verify-InstalledReleaseAgainstIncoming $targetReleasePath $verified.Manifest }
    else {
        $stagingRoot=Join-Path $releases ".staging-$targetRelease-$attemptId"
        if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
        Copy-Item -LiteralPath $packageRoot -Destination $stagingRoot -Recurse
        Verify-InstalledReleaseAgainstIncoming $stagingRoot $verified.Manifest
        Move-Item -LiteralPath $stagingRoot -Destination $targetReleasePath
    }

    New-Item -ItemType Directory -Force -Path $config | Out-Null
    $created=@()
    foreach ($name in @('edge','api','dashboard')) { $destination=Join-Path $config "$name.production.json"; if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath (Join-Path $packageRoot "config-templates/$name.production.template.json") -Destination $destination; $created += $destination } }
    if ($created.Count -gt 0) { throw "Commissioning required. Created site configuration files: $($created -join ', '). Complete them and rerun deployment." }

    $edgePath=Join-Path $config 'edge.production.json'; $apiPath=Join-Path $config 'api.production.json'; $dashboardPath=Join-Path $config 'dashboard.production.json'
    Assert-NoPlaceholders $edgePath 'Edge'; Assert-NoPlaceholders $apiPath 'API'; Assert-NoPlaceholders $dashboardPath 'Dashboard'
    $edgeConfig=Read-JsonFile $edgePath; $apiConfig=Read-JsonFile $apiPath; $dashboardConfig=Read-JsonFile $dashboardPath; Assert-SiteConfiguration $edgeConfig $apiConfig $dashboardConfig
    $edgeEnvironment=Get-ConfigurationEnvironment $edgePath; $apiEnvironment=Get-ConfigurationEnvironment $apiPath; $dashboardEnvironment=Get-ConfigurationEnvironment $dashboardPath
    $apiBase=Get-BaseAddressFromConfiguration $apiConfig 'API'; $dashboardBase=Get-BaseAddressFromConfiguration $dashboardConfig 'Dashboard'

    $oldRuntime=if (Test-Path -LiteralPath $runtimePath) { Read-JsonFile $runtimePath } else { $null }; $oldRecords=Get-RuntimeRecords $oldRuntime
    $selection=Get-SelectedReleaseState $current $releases; if ($selection.Kind -eq 'Release') { $oldSelected=$selection.Release }

    if ($selection.Kind -eq 'Release' -and $selection.Release -ceq $targetRelease -and (Test-OwnedProcess $oldRecords.edge) -and (Test-OwnedProcess $oldRecords.api) -and (Test-OwnedProcess $oldRecords.dashboard) -and (Test-HttpOk "$apiBase/health") -and (Test-HttpOk "$dashboardBase/health/live") -and (Test-HttpOk "$dashboardBase/health/ready")) { [pscustomobject]@{Status='AlreadyDeployed';SourceCommit=$targetRelease;InstallRoot=$install}; return }

    $failurePhase='Shutdown'; $runtimeDisruptionStarted=$true
    Stop-RecordedProcesses $oldRecords

    $failurePhase='Migration'
    $migrationExe=Join-Path $targetReleasePath 'apps/migrations/FactoryConnect.Migrations.exe'; $migrationDirectory=Split-Path $migrationExe -Parent; $migrationEnvironment=@{}; foreach ($entry in $edgeEnvironment.GetEnumerator()) { $migrationEnvironment[$entry.Key]=$entry.Value }; $migrationEnvironment['DemoStandardProvisioning__Enabled']='false'
    $saved=@{}; try { foreach ($entry in $migrationEnvironment.GetEnumerator()) { $saved[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process'); [Environment]::SetEnvironmentVariable($entry.Key,[string]$entry.Value,'Process') }; foreach ($name in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) { $saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,'Production','Process') }; $migrationStarted=$true; $migration=Start-Process -FilePath $migrationExe -WorkingDirectory $migrationDirectory -Wait -PassThru -RedirectStandardOutput (Join-Path $attemptLog 'migrations.out.log') -RedirectStandardError (Join-Path $attemptLog 'migrations.err.log') } finally { foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') } }
    if ($migration.ExitCode -ne 0) { throw "Database migration failed with exit code $($migration.ExitCode)." }
    $migrationCompleted=$true

    $failurePhase='Selection'
    $replacement="$current.new-$attemptId"; if (Test-Path -LiteralPath $replacement) { Remove-Item -LiteralPath $replacement -Force }
    New-Item -ItemType Junction -Path $replacement -Target $targetReleasePath | Out-Null
    if (Test-Path -LiteralPath $current) { Remove-ActivationJunction $current }
    Rename-Item -LiteralPath $replacement -NewName (Split-Path $current -Leaf)
    Assert-CurrentTargetsRelease $current $releases $targetRelease
    Write-JsonFile $runtimePath (New-RuntimeState $targetRelease $targetReleasePath $attemptId 'Starting' $newRecords $null 'Succeeded' $false)

    $failurePhase='Startup'
    $edgeExe=Join-Path $targetReleasePath 'apps/edge/FactoryConnect.Edge.exe'; $edge=Start-OwnedProcess $edgeExe (Split-Path $edgeExe -Parent) $edgeEnvironment (Join-Path $attemptLog 'edge.out.log') (Join-Path $attemptLog 'edge.err.log'); $newRecords.edge=Get-ProcessRecord 'edge' $edge $edgeExe; Write-JsonFile $runtimePath (New-RuntimeState $targetRelease $targetReleasePath $attemptId 'Starting' $newRecords $null 'Succeeded' $false); Start-Sleep -Seconds $EdgeStabilizationSeconds; if (-not (Test-OwnedProcess $newRecords.edge)) { throw 'Edge exited during startup stabilization.' }
    $apiExe=Join-Path $targetReleasePath 'apps/api/FactoryConnect.Api.exe'; $api=Start-OwnedProcess $apiExe (Split-Path $apiExe -Parent) $apiEnvironment (Join-Path $attemptLog 'api.out.log') (Join-Path $attemptLog 'api.err.log'); $newRecords.api=Get-ProcessRecord 'api' $api $apiExe; Write-JsonFile $runtimePath (New-RuntimeState $targetRelease $targetReleasePath $attemptId 'Starting' $newRecords $null 'Succeeded' $false); Wait-HttpOk "$apiBase/health" $HealthTimeoutSeconds | Out-Null
    $dashboardExe=Join-Path $targetReleasePath 'apps/dashboard/FactoryConnect.Dashboard.exe'; $dashboard=Start-OwnedProcess $dashboardExe (Split-Path $dashboardExe -Parent) $dashboardEnvironment (Join-Path $attemptLog 'dashboard.out.log') (Join-Path $attemptLog 'dashboard.err.log'); $newRecords.dashboard=Get-ProcessRecord 'dashboard' $dashboard $dashboardExe; Write-JsonFile $runtimePath (New-RuntimeState $targetRelease $targetReleasePath $attemptId 'Starting' $newRecords $null 'Succeeded' $false); Wait-HttpOk "$dashboardBase/health/live" $HealthTimeoutSeconds | Out-Null; Wait-HttpOk "$dashboardBase/health/ready" $HealthTimeoutSeconds | Out-Null

    $failurePhase='FinalVerification'
    Assert-CurrentTargetsRelease $current $releases $targetRelease
    foreach ($name in @('edge','api','dashboard')) { if (-not (Test-OwnedProcess $newRecords[$name])) { throw "Final ownership/liveness verification failed for $name." } }
    Wait-HttpOk "$apiBase/health" $HealthTimeoutSeconds | Out-Null; Wait-HttpOk "$dashboardBase/health/live" $HealthTimeoutSeconds | Out-Null; Wait-HttpOk "$dashboardBase/health/ready" $HealthTimeoutSeconds | Out-Null
    $successStates=[ordered]@{edge='Owned';api='Owned';dashboard='Owned'}
    Write-JsonFile $runtimePath (New-RuntimeState $targetRelease $targetReleasePath $attemptId 'Succeeded' $newRecords $null 'Succeeded' $false $successStates)
    [pscustomobject]@{Status='Succeeded';SourceCommit=$targetRelease;InstallRoot=$install;Current=$current}
}
catch {
    $primary=$_.Exception.Message; $cleanup=@()
    foreach ($name in @('dashboard','api','edge')) {
        $record=$newRecords[$name]; if ($null -eq $record) { continue }
        $state=Get-RecordedProcessState $record
        if ($state.State -eq 'Absent') { $newRecords[$name]=$null; continue }
        if ($state.State -eq 'Mismatch') { $cleanup += "$name PID $($record.pid) identity mismatch during cleanup."; continue }
        try { Stop-Process -Id ([int]$record.pid) -ErrorAction Stop; Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue } catch { $cleanup += "$name PID $($record.pid) stop failed: $($_.Exception.Message)" }
        if (Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue) { $cleanup += "$name PID $($record.pid) survived cleanup." } else { $newRecords[$name]=$null }
    }
    $observed=Get-ObservedProcessRecords $newRecords $oldRecords
    $selection=Get-SelectedReleaseState $current $releases
    $selectedRelease=if ($selection.Kind -eq 'Release') { $selection.Release } else { $null }
    $selectedPath=if ($selection.Kind -eq 'Release') { $selection.Target } else { $null }
    $migrationOutcome=if ($migrationCompleted) { 'Succeeded' } elseif ($migrationStarted) { 'Failed' } else { 'NotStarted' }
    if ($lockAcquired -and $runtimeDisruptionStarted) { Write-JsonFile $runtimePath (New-RuntimeState $selectedRelease $selectedPath $attemptId 'Failed' $observed.Records $failurePhase $migrationOutcome $migrationStarted $observed.States) }
    Write-JsonFile $failurePath ([ordered]@{schemaVersion='1.0';attemptId=$attemptId;failedAtUtc=[DateTime]::UtcNow.ToString('o');message=$primary;failurePhase=$failurePhase;migrationOutcome=$migrationOutcome;databaseMayHaveChanged=$migrationStarted;cleanupErrors=$cleanup;runtimeStateUpdated=($lockAcquired -and $runtimeDisruptionStarted);observedSelection=[ordered]@{kind=$selection.Kind;release=$selection.Release;target=$selection.Target};processStates=$observed.States})
    throw
}
finally { if ($null -ne $lockStream) { $lockStream.Dispose() } }
