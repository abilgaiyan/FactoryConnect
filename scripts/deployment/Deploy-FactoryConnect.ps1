[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter()]
    [string]$InstallRoot = 'D:\FactoryConnect',

    [Parameter()]
    [ValidateRange(1, 300)]
    [int]$EdgeStabilizationSeconds = 5,

    [Parameter()]
    [ValidateRange(1, 300)]
    [int]$HealthTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    return ([System.IO.Path]::GetRelativePath($Root, $Path)).Replace('\', '/')
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 100
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )
    ($Value | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $Path -Encoding utf8
}

function Assert-NoPlaceholders {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $text = Get-Content -Raw -LiteralPath $Path
    $matches = [regex]::Matches($text, '__[A-Z0-9_]+__')
    if ($matches.Count -ne 0) {
        $values = @($matches | ForEach-Object Value | Sort-Object -Unique)
        throw "$Name site configuration is not commissioned. Replace placeholders: $($values -join ', ')"
    }
}

function Add-ConfigurationEnvironment {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter()][string]$Prefix = '',
        [Parameter(Mandatory = $true)][hashtable]$Environment
    )

    if ($null -eq $Value) {
        if (-not [string]::IsNullOrWhiteSpace($Prefix)) { $Environment[$Prefix] = '' }
        return
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            $childPrefix = if ([string]::IsNullOrWhiteSpace($Prefix)) { $property.Name } else { "$Prefix`__$($property.Name)" }
            Add-ConfigurationEnvironment -Value $property.Value -Prefix $childPrefix -Environment $Environment
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            $childPrefix = if ([string]::IsNullOrWhiteSpace($Prefix)) { [string]$index } else { "$Prefix`__$index" }
            Add-ConfigurationEnvironment -Value $item -Prefix $childPrefix -Environment $Environment
            $index++
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($Prefix)) { throw 'A scalar configuration value cannot be projected without a key.' }
    $Environment[$Prefix] = if ($Value -is [bool]) { $Value.ToString().ToLowerInvariant() } else { [string]$Value }
}

function Get-ConfigurationEnvironment {
    param([Parameter(Mandatory = $true)][string]$Path)
    $environment = @{}
    Add-ConfigurationEnvironment -Value (Read-JsonFile -Path $Path) -Environment $environment
    return $environment
}

function Assert-SiteConfiguration {
    param(
        [Parameter(Mandatory = $true)]$Edge,
        [Parameter(Mandatory = $true)]$Api,
        [Parameter(Mandatory = $true)]$Dashboard
    )

    if ([string]$Edge.Persistence.Provider -cne 'SqlServer') { throw "Edge Persistence.Provider must be exactly 'SqlServer'." }
    if ([string]::IsNullOrWhiteSpace([string]$Edge.PersistenceProviders.SqlServer.ConnectionString)) { throw 'Edge SQL connection string is required.' }
    if ($null -eq $Edge.MTConnect.Machines -or @($Edge.MTConnect.Machines).Count -lt 1) { throw 'Edge must configure at least one MTConnect machine.' }
    foreach ($machine in @($Edge.MTConnect.Machines)) {
        if ([string]::IsNullOrWhiteSpace([string]$machine.BaseUri)) { throw 'Every Edge MTConnect machine requires BaseUri.' }
        if ([string]::IsNullOrWhiteSpace([string]$machine.MachineId)) { throw 'Every Edge MTConnect machine requires MachineId.' }
        if ([string]::IsNullOrWhiteSpace([string]$machine.DeviceKey)) { throw 'Every Edge MTConnect machine requires DeviceKey.' }
    }
    if ($null -eq $Edge.ProductionProcessing.Machines -or @($Edge.ProductionProcessing.Machines).Count -lt 1) { throw 'Edge ProductionProcessing.Machines must contain at least one machine.' }

    if ([string]$Api.Persistence.Provider -cne 'SqlServer') { throw "API Persistence.Provider must be exactly 'SqlServer'." }
    if ([string]::IsNullOrWhiteSpace([string]$Api.PersistenceProviders.SqlServer.ConnectionString)) { throw 'API SQL connection string is required.' }
    if ([string]::IsNullOrWhiteSpace([string]$Api.Urls)) { throw 'API Urls is required.' }

    if ([string]::IsNullOrWhiteSpace([string]$Dashboard.Urls)) { throw 'Dashboard Urls is required.' }
    if ([string]::IsNullOrWhiteSpace([string]$Dashboard.Dashboard.ReportingApiBaseAddress)) { throw 'Dashboard.ReportingApiBaseAddress is required.' }
    if ($null -eq $Dashboard.Dashboard.Sources -or @($Dashboard.Dashboard.Sources).Count -lt 1) { throw 'Dashboard must configure at least one source.' }
}

function Get-BaseAddressFromConfiguration {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $urls = [string]$Configuration.Urls
    $parts = @($urls.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 1) { throw "$Name configuration must define exactly one 'Urls' address in v1. Resolved '$urls'." }
    $uri = [Uri]$parts[0]
    if ($uri.Host -in @('0.0.0.0', '+', '*')) {
        $builder = [UriBuilder]$uri
        $builder.Host = 'localhost'
        return $builder.Uri.AbsoluteUri.TrimEnd('/')
    }
    return $uri.AbsoluteUri.TrimEnd('/')
}

function Start-OwnedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][hashtable]$ConfigurationEnvironment,
        [Parameter(Mandatory = $true)][string]$StdOutPath,
        [Parameter(Mandatory = $true)][string]$StdErrPath
    )

    $saved = @{}
    try {
        foreach ($entry in $ConfigurationEnvironment.GetEnumerator()) {
            $saved[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }
        foreach ($name in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, 'Production', 'Process')
        }
        return Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory -PassThru `
            -RedirectStandardOutput $StdOutPath -RedirectStandardError $StdErrPath
    }
    finally {
        foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
}

function Test-OwnedProcess {
    param([Parameter(Mandatory = $true)]$Record)
    try {
        $process = Get-Process -Id ([int]$Record.pid) -ErrorAction Stop
        $actualPath = [System.IO.Path]::GetFullPath($process.Path)
        $actualStart = $process.StartTime.ToUniversalTime().ToString('o')
        return ($actualPath -eq [System.IO.Path]::GetFullPath([string]$Record.executablePath)) -and ($actualStart -eq [string]$Record.startTimeUtc)
    }
    catch { return $false }
}

function Stop-RecordedProcesses {
    param([Parameter(Mandatory = $true)][string]$RuntimePath)
    if (-not (Test-Path -LiteralPath $RuntimePath -PathType Leaf)) { return }
    $runtime = Read-JsonFile -Path $RuntimePath
    foreach ($name in @('dashboard','api','edge')) {
        $record = $runtime.$name
        if ($null -eq $record) { continue }
        if (-not (Test-OwnedProcess -Record $record)) { throw "Refusing to stop $name PID $($record.pid): PID/path/start-time ownership verification failed." }
        Stop-Process -Id ([int]$record.pid) -ErrorAction Stop
        Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue
    }
}

function Wait-HttpOk {
    param([Parameter(Mandatory = $true)][string]$Uri,[Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Health check did not return HTTP 200 within $TimeoutSeconds seconds: $Uri"
}

function Get-ManifestMap {
    param([Parameter(Mandatory = $true)][string]$Root)
    $manifestPath = Join-Path $Root 'MANIFEST.sha256'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package is missing MANIFEST.sha256.' }
    $map = @{}
    foreach ($line in (Get-Content -LiteralPath $manifestPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<path>.+)$') { throw "Invalid manifest line: '$line'." }
        $path = $Matches['path'].Replace('\','/')
        if ($map.ContainsKey($path)) { throw "Duplicate manifest path '$path'." }
        $map[$path] = $Matches['hash']
    }
    return $map
}

function Assert-ContainedRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path,[Parameter(Mandatory = $true)][string]$Description)
    if ([System.IO.Path]::IsPathRooted($Path) -or $Path.Contains('..') -or $Path.StartsWith('/') -or $Path.StartsWith('\')) {
        throw "$Description must be a contained relative path. Resolved '$Path'."
    }
}

function Verify-Package {
    param([Parameter(Mandatory = $true)][string]$Root)

    $releasePath = Join-Path $Root 'release.json'
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'Package is missing release.json.' }
    $expected = Get-ManifestMap -Root $Root
    $actual = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object { (Get-RelativePath -Root $Root -Path $_.FullName) -ne 'MANIFEST.sha256' })
    if ($actual.Count -ne $expected.Count) { throw "Package membership mismatch. Manifest has $($expected.Count) payload files; package has $($actual.Count)." }
    foreach ($file in $actual) {
        $relative = Get-RelativePath -Root $Root -Path $file.FullName
        if (-not $expected.ContainsKey($relative)) { throw "Package contains unmanifested file '$relative'." }
        if ((Get-Sha256 -Path $file.FullName) -ne $expected[$relative]) { throw "Package hash mismatch for '$relative'." }
    }

    $release = Read-JsonFile -Path $releasePath
    if ([string]$release.schemaVersion -ne '1.0') { throw "Unsupported release schema '$($release.schemaVersion)'." }
    if ([string]$release.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'release.json contains an invalid sourceCommit.' }
    if ([string]$release.publish.runtimeIdentifier -ne 'win-x64' -or -not [bool]$release.publish.selfContained) { throw 'Release is not the frozen win-x64 self-contained profile.' }

    $requiredApps = @{ migrations='apps/migrations/FactoryConnect.Migrations.exe'; edge='apps/edge/FactoryConnect.Edge.exe'; api='apps/api/FactoryConnect.Api.exe'; dashboard='apps/dashboard/FactoryConnect.Dashboard.exe' }
    foreach ($name in $requiredApps.Keys) {
        $app = @($release.applications | Where-Object { [string]$_.name -eq $name })
        if ($app.Count -ne 1) { throw "release.json must contain exactly one '$name' application." }
        $path = [string]$app[0].executable
        Assert-ContainedRelativePath -Path $path -Description "$name executable"
        if ($path.Replace('\','/') -cne $requiredApps[$name]) { throw "$name executable path does not match the frozen package layout." }
        if (-not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Package is missing required executable '$path'." }
    }

    $requiredTemplates = @{ edge='config-templates/edge.production.template.json'; api='config-templates/api.production.template.json'; dashboard='config-templates/dashboard.production.template.json' }
    foreach ($name in $requiredTemplates.Keys) {
        $template = @($release.configurationTemplates | Where-Object { [string]$_.name -eq $name })
        if ($template.Count -ne 1) { throw "release.json must contain exactly one '$name' configuration template." }
        $path = [string]$template[0].path
        Assert-ContainedRelativePath -Path $path -Description "$name template"
        if ($path.Replace('\','/') -cne $requiredTemplates[$name]) { throw "$name template path does not match the frozen package layout." }
        if (-not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Package is missing required template '$path'." }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Root 'Deploy-FactoryConnect.ps1') -PathType Leaf)) { throw 'Package is missing Deploy-FactoryConnect.ps1.' }
    return [pscustomobject]@{ Release=$release; Manifest=$expected }
}

function Assert-ExactPackageMatch {
    param([Parameter(Mandatory = $true)][string]$IncomingRoot,[Parameter(Mandatory = $true)][string]$InstalledRoot)
    $incoming = Verify-Package -Root $IncomingRoot
    $installed = Verify-Package -Root $InstalledRoot
    if ([string]$incoming.Release.sourceCommit -ne [string]$installed.Release.sourceCommit) { throw 'Installed release identity conflict.' }
    if ($incoming.Manifest.Count -ne $installed.Manifest.Count) { throw 'Installed release payload differs from incoming package.' }
    foreach ($path in $incoming.Manifest.Keys) {
        if (-not $installed.Manifest.ContainsKey($path) -or $installed.Manifest[$path] -ne $incoming.Manifest[$path]) {
            throw "Installed immutable release differs from incoming package at '$path'."
        }
    }
}

function Get-CurrentTarget {
    param([Parameter(Mandatory = $true)][string]$CurrentPath)
    if (-not (Test-Path -LiteralPath $CurrentPath)) { return $null }
    $item = Get-Item -LiteralPath $CurrentPath -Force
    return [System.IO.Path]::GetFullPath([string]$item.Target)
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
$attemptId = [Guid]::NewGuid().ToString('N')
$deploymentRoot = Join-Path $InstallRoot 'deployment'
$logsRoot = Join-Path $deploymentRoot "logs/$attemptId"
$runtimePath = Join-Path $deploymentRoot 'runtime.json'
$lockPath = Join-Path $deploymentRoot 'deployment.lock'
$configRoot = Join-Path $InstallRoot 'config'
$releasesRoot = Join-Path $InstallRoot 'releases'
$currentPath = Join-Path $InstallRoot 'current'
$tempPackageRoot = $null
$newProcesses = [System.Collections.Generic.List[object]]::new()
$lockStream = $null
$sourceCommit = $null
$stagedRelease = $null
$selected = $false
$records = [ordered]@{ edge=$null; api=$null; dashboard=$null }

try {
    [System.IO.Directory]::CreateDirectory($InstallRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($deploymentRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($logsRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($configRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($releasesRoot) | Out-Null

    try { $lockStream = [System.IO.File]::Open($lockPath,[System.IO.FileMode]::OpenOrCreate,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None) }
    catch { throw "Another FactoryConnect deployment is already active for '$InstallRoot'." }

    if (Test-Path -LiteralPath $PackagePath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($PackagePath) -ine '.zip') { throw 'PackagePath file must be a .zip archive.' }
        $tempPackageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FactoryConnectDeploy-$attemptId"
        [System.IO.Directory]::CreateDirectory($tempPackageRoot) | Out-Null
        Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempPackageRoot
        $roots = @(Get-ChildItem -LiteralPath $tempPackageRoot -Directory)
        if ($roots.Count -ne 1) { throw 'Release ZIP must contain exactly one top-level release directory.' }
        $packageRoot = $roots[0].FullName
    }
    elseif (Test-Path -LiteralPath $PackagePath -PathType Container) { $packageRoot = $PackagePath }
    else { throw "PackagePath does not exist: '$PackagePath'." }

    $packageVerification = Verify-Package -Root $packageRoot
    $release = $packageVerification.Release
    $sourceCommit = [string]$release.sourceCommit
    $stagedRelease = Join-Path $releasesRoot $sourceCommit

    if (Test-Path -LiteralPath $stagedRelease) { Assert-ExactPackageMatch -IncomingRoot $packageRoot -InstalledRoot $stagedRelease }
    else {
        $stagingPath = Join-Path $releasesRoot ".$sourceCommit.staging-$attemptId"
        Copy-Item -LiteralPath $packageRoot -Destination $stagingPath -Recurse
        [System.IO.Directory]::Move($stagingPath,$stagedRelease)
    }

    $configMap = @{ edge='edge.production.json'; api='api.production.json'; dashboard='dashboard.production.json' }
    $createdConfigs = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @('edge','api','dashboard')) {
        $template = $release.configurationTemplates | Where-Object { [string]$_.name -eq $name } | Select-Object -First 1
        $sitePath = Join-Path $configRoot $configMap[$name]
        if (-not (Test-Path -LiteralPath $sitePath -PathType Leaf)) {
            Copy-Item -LiteralPath (Join-Path $stagedRelease ([string]$template.path)) -Destination $sitePath
            $createdConfigs.Add($sitePath)
        }
    }
    if ($createdConfigs.Count -ne 0) { throw "Created first-install site configuration files: $($createdConfigs -join ', '). Supply commissioning values, then rerun deployment. No runtime was stopped or activated." }

    $edgeConfigPath = Join-Path $configRoot $configMap.edge
    $apiConfigPath = Join-Path $configRoot $configMap.api
    $dashboardConfigPath = Join-Path $configRoot $configMap.dashboard
    Assert-NoPlaceholders -Path $edgeConfigPath -Name 'Edge'; Assert-NoPlaceholders -Path $apiConfigPath -Name 'API'; Assert-NoPlaceholders -Path $dashboardConfigPath -Name 'Dashboard'
    $edgeConfig = Read-JsonFile -Path $edgeConfigPath; $apiConfig = Read-JsonFile -Path $apiConfigPath; $dashboardConfig = Read-JsonFile -Path $dashboardConfigPath
    Assert-SiteConfiguration -Edge $edgeConfig -Api $apiConfig -Dashboard $dashboardConfig
    $apiBase = Get-BaseAddressFromConfiguration -Configuration $apiConfig -Name 'API'
    $dashboardBase = Get-BaseAddressFromConfiguration -Configuration $dashboardConfig -Name 'Dashboard'

    $currentTarget = Get-CurrentTarget -CurrentPath $currentPath
    if ($currentTarget -eq [System.IO.Path]::GetFullPath($stagedRelease) -and (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        $runtime = Read-JsonFile -Path $runtimePath
        if ([string]$runtime.releaseCommit -eq $sourceCommit -and [string]$runtime.deploymentStatus -eq 'Succeeded' -and (Test-OwnedProcess $runtime.edge) -and (Test-OwnedProcess $runtime.api) -and (Test-OwnedProcess $runtime.dashboard)) {
            [pscustomobject]@{ Status='AlreadyDeployed'; SourceCommit=$sourceCommit; ReleasePath=$stagedRelease; CurrentPath=$currentPath; RuntimePath=$runtimePath }
            return
        }
    }

    Stop-RecordedProcesses -RuntimePath $runtimePath

    $migration = $release.applications | Where-Object { $_.name -eq 'migrations' } | Select-Object -First 1
    $migrationExecutable = Join-Path $stagedRelease ([string]$migration.executable)
    $migrationDirectory = Split-Path -Parent $migrationExecutable
    $migrationEnvironment = Get-ConfigurationEnvironment -Path $edgeConfigPath
    $savedMigrationEnvironment = @{}
    Push-Location $migrationDirectory
    try {
        foreach ($entry in $migrationEnvironment.GetEnumerator()) { $savedMigrationEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key,'Process'); [Environment]::SetEnvironmentVariable($entry.Key,[string]$entry.Value,'Process') }
        foreach ($name in @('DOTNET_ENVIRONMENT','ASPNETCORE_ENVIRONMENT')) { $savedMigrationEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,'Production','Process') }
        $migrationOutput = & $migrationExecutable 2>&1
        $migrationExitCode = $LASTEXITCODE
        $migrationOutput | Set-Content -LiteralPath (Join-Path $logsRoot 'migrations.log')
        if ($migrationExitCode -ne 0) { throw "FactoryConnect.Migrations exited with code $migrationExitCode. Activation is unchanged; database state may have changed." }
    }
    finally { Pop-Location; foreach ($entry in $savedMigrationEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') } }

    $junctionTemp = Join-Path $InstallRoot "current.new-$attemptId"
    if (Test-Path -LiteralPath $junctionTemp) { Remove-Item -LiteralPath $junctionTemp -Force }
    New-Item -ItemType Junction -Path $junctionTemp -Target $stagedRelease | Out-Null
    if (Test-Path -LiteralPath $currentPath) { Remove-Item -LiteralPath $currentPath -Force }
    Rename-Item -LiteralPath $junctionTemp -NewName 'current'
    $selected = $true
    Write-JsonFile -Path $runtimePath -Value ([ordered]@{ schemaVersion='1.0'; deploymentAttemptId=$attemptId; releaseCommit=$sourceCommit; releasePath=$stagedRelease; selectedAtUtc=[DateTime]::UtcNow.ToString('o'); deploymentStatus='Starting'; edge=$null; api=$null; dashboard=$null })

    foreach ($name in @('edge','api','dashboard')) {
        $application = $release.applications | Where-Object { $_.name -eq $name } | Select-Object -First 1
        $executable = Join-Path $stagedRelease ([string]$application.executable)
        $configPath = switch ($name) { 'edge'{$edgeConfigPath}; 'api'{$apiConfigPath}; 'dashboard'{$dashboardConfigPath} }
        $process = Start-OwnedProcess -Executable $executable -WorkingDirectory (Split-Path -Parent $executable) -ConfigurationEnvironment (Get-ConfigurationEnvironment -Path $configPath) -StdOutPath (Join-Path $logsRoot "$name.stdout.log") -StdErrPath (Join-Path $logsRoot "$name.stderr.log")
        $newProcesses.Add($process); $process.Refresh()
        $records[$name] = [ordered]@{ pid=$process.Id; executablePath=[System.IO.Path]::GetFullPath($executable); startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o') }
        Write-JsonFile -Path $runtimePath -Value ([ordered]@{ schemaVersion='1.0'; deploymentAttemptId=$attemptId; releaseCommit=$sourceCommit; releasePath=$stagedRelease; selectedAtUtc=[DateTime]::UtcNow.ToString('o'); deploymentStatus='Starting'; edge=$records.edge; api=$records.api; dashboard=$records.dashboard })
        if ($name -eq 'edge') { Start-Sleep -Seconds $EdgeStabilizationSeconds; if ($process.HasExited) { throw "Edge exited during the $EdgeStabilizationSeconds-second stabilization period." } }
        elseif ($name -eq 'api') { Wait-HttpOk -Uri "$apiBase/health" -TimeoutSeconds $HealthTimeoutSeconds }
        else { Wait-HttpOk -Uri "$dashboardBase/health/live" -TimeoutSeconds $HealthTimeoutSeconds; Wait-HttpOk -Uri "$dashboardBase/health/ready" -TimeoutSeconds $HealthTimeoutSeconds }
    }

    Write-JsonFile -Path $runtimePath -Value ([ordered]@{ schemaVersion='1.0'; deploymentAttemptId=$attemptId; releaseCommit=$sourceCommit; releasePath=$stagedRelease; selectedAtUtc=[DateTime]::UtcNow.ToString('o'); deploymentStatus='Succeeded'; edge=$records.edge; api=$records.api; dashboard=$records.dashboard })
    [pscustomobject]@{ Status='Succeeded'; SourceCommit=$sourceCommit; ReleasePath=$stagedRelease; CurrentPath=$currentPath; RuntimePath=$runtimePath; LogsPath=$logsRoot }
}
catch {
    for ($index = $newProcesses.Count - 1; $index -ge 0; $index--) { try { $process = $newProcesses[$index]; if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop } } catch { } }
    $failure = [ordered]@{ schemaVersion='1.0'; deploymentAttemptId=$attemptId; releaseCommit=$sourceCommit; releasePath=$stagedRelease; deploymentStatus='Failed'; selected=$selected; failedAtUtc=[DateTime]::UtcNow.ToString('o'); edge=$records.edge; api=$records.api; dashboard=$records.dashboard; error=$_.Exception.Message }
    Write-JsonFile -Path (Join-Path $logsRoot 'deployment-failure.json') -Value $failure
    if ($selected) { Write-JsonFile -Path $runtimePath -Value $failure }
    throw
}
finally {
    if ($null -ne $lockStream) { $lockStream.Dispose() }
    if ($null -ne $tempPackageRoot -and (Test-Path -LiteralPath $tempPackageRoot)) { Remove-Item -LiteralPath $tempPackageRoot -Force -Recurse -ErrorAction SilentlyContinue }
}
