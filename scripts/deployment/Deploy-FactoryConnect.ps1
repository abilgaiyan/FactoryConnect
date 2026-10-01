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
        if (-not [string]::IsNullOrWhiteSpace($Prefix)) {
            $Environment[$Prefix] = ''
        }
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

    if ([string]::IsNullOrWhiteSpace($Prefix)) {
        throw 'A scalar configuration value cannot be projected without a key.'
    }

    if ($Value -is [bool]) {
        $Environment[$Prefix] = $Value.ToString().ToLowerInvariant()
    }
    else {
        $Environment[$Prefix] = [string]$Value
    }
}

function Get-ConfigurationEnvironment {
    param([Parameter(Mandatory = $true)][string]$Path)

    $environment = @{}
    Add-ConfigurationEnvironment -Value (Read-JsonFile -Path $Path) -Environment $environment
    return $environment
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
        $saved['DOTNET_ENVIRONMENT'] = [Environment]::GetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_ENVIRONMENT', 'Production', 'Process')

        return Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory -PassThru `
            -RedirectStandardOutput $StdOutPath -RedirectStandardError $StdErrPath
    }
    finally {
        foreach ($entry in $saved.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

function Test-OwnedProcess {
    param([Parameter(Mandatory = $true)]$Record)

    try {
        $process = Get-Process -Id ([int]$Record.pid) -ErrorAction Stop
        $actualPath = $process.Path
        $actualStart = $process.StartTime.ToUniversalTime().ToString('o')
        return ([System.IO.Path]::GetFullPath($actualPath) -eq [System.IO.Path]::GetFullPath([string]$Record.executablePath)) -and
            ($actualStart -eq [string]$Record.startTimeUtc)
    }
    catch {
        return $false
    }
}

function Stop-RecordedProcesses {
    param([Parameter(Mandatory = $true)][string]$RuntimePath)

    if (-not (Test-Path -LiteralPath $RuntimePath -PathType Leaf)) {
        return
    }

    $runtime = Read-JsonFile -Path $RuntimePath
    foreach ($name in @('dashboard', 'api', 'edge')) {
        $record = $runtime.$name
        if ($null -eq $record) {
            continue
        }
        if (-not (Test-OwnedProcess -Record $record)) {
            throw "Refusing to stop $name PID $($record.pid): PID/path/start-time ownership verification failed."
        }
        Stop-Process -Id ([int]$record.pid) -ErrorAction Stop
        Wait-Process -Id ([int]$record.pid) -Timeout 30 -ErrorAction SilentlyContinue
    }
}

function Wait-HttpOk {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Health check did not return HTTP 200 within $TimeoutSeconds seconds: $Uri"
}

function Get-BaseAddressFromConfiguration {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $urls = [string]$Configuration.Urls
    if ([string]::IsNullOrWhiteSpace($urls)) {
        throw "$Name configuration must define a single 'Urls' address for deployment health checks."
    }
    $parts = @($urls.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 1) {
        throw "$Name configuration must define exactly one 'Urls' address in v1. Resolved '$urls'."
    }
    $uri = [Uri]$parts[0]
    if ($uri.Host -in @('0.0.0.0', '+', '*')) {
        $builder = [UriBuilder]$uri
        $builder.Host = 'localhost'
        return $builder.Uri.AbsoluteUri.TrimEnd('/')
    }
    return $uri.AbsoluteUri.TrimEnd('/')
}

function Verify-Package {
    param([Parameter(Mandatory = $true)][string]$Root)

    $releasePath = Join-Path $Root 'release.json'
    $manifestPath = Join-Path $Root 'MANIFEST.sha256'
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'Package is missing release.json.' }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package is missing MANIFEST.sha256.' }

    $expected = @{}
    foreach ($line in (Get-Content -LiteralPath $manifestPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<path>.+)$') {
            throw "Invalid manifest line: '$line'."
        }
        $path = $Matches['path'].Replace('\', '/')
        if ($expected.ContainsKey($path)) { throw "Duplicate manifest path '$path'." }
        $expected[$path] = $Matches['hash']
    }

    $actual = @(
        Get-ChildItem -LiteralPath $Root -File -Recurse |
            Where-Object { (Get-RelativePath -Root $Root -Path $_.FullName) -ne 'MANIFEST.sha256' }
    )
    if ($actual.Count -ne $expected.Count) {
        throw "Package membership mismatch. Manifest has $($expected.Count) payload files; package has $($actual.Count)."
    }
    foreach ($file in $actual) {
        $relative = Get-RelativePath -Root $Root -Path $file.FullName
        if (-not $expected.ContainsKey($relative)) { throw "Package contains unmanifested file '$relative'." }
        if ((Get-Sha256 -Path $file.FullName) -ne $expected[$relative]) { throw "Package hash mismatch for '$relative'." }
    }

    $release = Read-JsonFile -Path $releasePath
    if ([string]$release.schemaVersion -ne '1.0') { throw "Unsupported release schema '$($release.schemaVersion)'." }
    if ([string]$release.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'release.json contains an invalid sourceCommit.' }
    if ([string]$release.publish.runtimeIdentifier -ne 'win-x64' -or -not [bool]$release.publish.selfContained) {
        throw 'Release is not the frozen win-x64 self-contained profile.'
    }
    return $release
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
$attemptId = [Guid]::NewGuid().ToString('N')
$deploymentRoot = Join-Path $InstallRoot 'deployment'
$logsRoot = Join-Path $deploymentRoot "logs/$attemptId"
$runtimePath = Join-Path $deploymentRoot 'runtime.json'
$configRoot = Join-Path $InstallRoot 'config'
$releasesRoot = Join-Path $InstallRoot 'releases'
$currentPath = Join-Path $InstallRoot 'current'
$tempPackageRoot = $null
$newProcesses = @()

try {
    [System.IO.Directory]::CreateDirectory($InstallRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($deploymentRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($logsRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($configRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($releasesRoot) | Out-Null

    if (Test-Path -LiteralPath $PackagePath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($PackagePath) -ine '.zip') { throw 'PackagePath file must be a .zip archive.' }
        $tempPackageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FactoryConnectDeploy-$attemptId"
        [System.IO.Directory]::CreateDirectory($tempPackageRoot) | Out-Null
        Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempPackageRoot
        $roots = @(Get-ChildItem -LiteralPath $tempPackageRoot -Directory)
        if ($roots.Count -ne 1) { throw 'Release ZIP must contain exactly one top-level release directory.' }
        $packageRoot = $roots[0].FullName
    }
    elseif (Test-Path -LiteralPath $PackagePath -PathType Container) {
        $packageRoot = $PackagePath
    }
    else {
        throw "PackagePath does not exist: '$PackagePath'."
    }

    $release = Verify-Package -Root $packageRoot
    $sourceCommit = [string]$release.sourceCommit
    $stagedRelease = Join-Path $releasesRoot $sourceCommit

    if (Test-Path -LiteralPath $stagedRelease) {
        $installedRelease = Verify-Package -Root $stagedRelease
        if ([string]$installedRelease.sourceCommit -ne $sourceCommit) { throw 'Installed release identity conflict.' }
    }
    else {
        $stagingPath = Join-Path $releasesRoot ".$sourceCommit.staging-$attemptId"
        Copy-Item -LiteralPath $packageRoot -Destination $stagingPath -Recurse
        [System.IO.Directory]::Move($stagingPath, $stagedRelease)
    }

    $configMap = @{
        edge = 'edge.production.json'
        api = 'api.production.json'
        dashboard = 'dashboard.production.json'
    }
    foreach ($template in $release.configurationTemplates) {
        $name = [string]$template.name
        if (-not $configMap.ContainsKey($name)) { continue }
        $sitePath = Join-Path $configRoot $configMap[$name]
        if (-not (Test-Path -LiteralPath $sitePath -PathType Leaf)) {
            Copy-Item -LiteralPath (Join-Path $stagedRelease ([string]$template.path)) -Destination $sitePath
            throw "Created first-install site configuration '$sitePath'. Supply and validate commissioning values, then rerun deployment. No runtime was stopped or activated."
        }
    }

    $edgeConfigPath = Join-Path $configRoot $configMap.edge
    $apiConfigPath = Join-Path $configRoot $configMap.api
    $dashboardConfigPath = Join-Path $configRoot $configMap.dashboard
    Assert-NoPlaceholders -Path $edgeConfigPath -Name 'Edge'
    Assert-NoPlaceholders -Path $apiConfigPath -Name 'API'
    Assert-NoPlaceholders -Path $dashboardConfigPath -Name 'Dashboard'

    $edgeConfig = Read-JsonFile -Path $edgeConfigPath
    $apiConfig = Read-JsonFile -Path $apiConfigPath
    $dashboardConfig = Read-JsonFile -Path $dashboardConfigPath
    $apiBase = Get-BaseAddressFromConfiguration -Configuration $apiConfig -Name 'API'
    $dashboardBase = Get-BaseAddressFromConfiguration -Configuration $dashboardConfig -Name 'Dashboard'

    Stop-RecordedProcesses -RuntimePath $runtimePath

    $migration = $release.applications | Where-Object { $_.name -eq 'migrations' } | Select-Object -First 1
    $migrationExecutable = Join-Path $stagedRelease ([string]$migration.executable)
    $migrationEnvironment = Get-ConfigurationEnvironment -Path $edgeConfigPath
    $savedMigrationEnvironment = @{}
    try {
        foreach ($entry in $migrationEnvironment.GetEnumerator()) {
            $savedMigrationEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }
        $migrationOutput = & $migrationExecutable 2>&1
        $migrationExitCode = $LASTEXITCODE
        $migrationOutput | Set-Content -LiteralPath (Join-Path $logsRoot 'migrations.log')
        if ($migrationExitCode -ne 0) {
            throw "FactoryConnect.Migrations exited with code $migrationExitCode. Activation is unchanged; database state may have changed."
        }
    }
    finally {
        foreach ($entry in $savedMigrationEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    $junctionTemp = Join-Path $InstallRoot "current.new-$attemptId"
    if (Test-Path -LiteralPath $junctionTemp) { Remove-Item -LiteralPath $junctionTemp -Force }
    New-Item -ItemType Junction -Path $junctionTemp -Target $stagedRelease | Out-Null
    if (Test-Path -LiteralPath $currentPath) { Remove-Item -LiteralPath $currentPath -Force }
    Rename-Item -LiteralPath $junctionTemp -NewName 'current'

    $records = @{}
    foreach ($name in @('edge', 'api', 'dashboard')) {
        $application = $release.applications | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($null -eq $application) { throw "release.json is missing application '$name'." }
        $executable = Join-Path $stagedRelease ([string]$application.executable)
        $workingDirectory = Split-Path -Parent $executable
        $configPath = switch ($name) {
            'edge' { $edgeConfigPath }
            'api' { $apiConfigPath }
            'dashboard' { $dashboardConfigPath }
        }
        $process = Start-OwnedProcess -Executable $executable -WorkingDirectory $workingDirectory `
            -ConfigurationEnvironment (Get-ConfigurationEnvironment -Path $configPath) `
            -StdOutPath (Join-Path $logsRoot "$name.stdout.log") -StdErrPath (Join-Path $logsRoot "$name.stderr.log")
        $newProcesses += $process
        $process.Refresh()
        $records[$name] = [ordered]@{
            pid = $process.Id
            executablePath = [System.IO.Path]::GetFullPath($executable)
            startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o')
        }

        if ($name -eq 'edge') {
            Start-Sleep -Seconds $EdgeStabilizationSeconds
            if ($process.HasExited) { throw "Edge exited during the $EdgeStabilizationSeconds-second stabilization period." }
        }
        elseif ($name -eq 'api') {
            Wait-HttpOk -Uri "$apiBase/health" -TimeoutSeconds $HealthTimeoutSeconds
        }
        elseif ($name -eq 'dashboard') {
            Wait-HttpOk -Uri "$dashboardBase/health/live" -TimeoutSeconds $HealthTimeoutSeconds
            Wait-HttpOk -Uri "$dashboardBase/health/ready" -TimeoutSeconds $HealthTimeoutSeconds
        }
    }

    $runtime = [ordered]@{
        schemaVersion = '1.0'
        deploymentAttemptId = $attemptId
        releaseCommit = $sourceCommit
        releasePath = $stagedRelease
        selectedAtUtc = [DateTime]::UtcNow.ToString('o')
        deploymentStatus = 'Succeeded'
        edge = $records.edge
        api = $records.api
        dashboard = $records.dashboard
    }
    ($runtime | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $runtimePath -Encoding utf8

    [pscustomobject]@{
        Status = 'Succeeded'
        SourceCommit = $sourceCommit
        ReleasePath = $stagedRelease
        CurrentPath = $currentPath
        RuntimePath = $runtimePath
        LogsPath = $logsRoot
    }
}
catch {
    foreach ($process in @($newProcesses | Select-Object -Reverse)) {
        try {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop }
        }
        catch { }
    }

    $failure = [ordered]@{
        schemaVersion = '1.0'
        deploymentAttemptId = $attemptId
        deploymentStatus = 'Failed'
        failedAtUtc = [DateTime]::UtcNow.ToString('o')
        error = $_.Exception.Message
    }
    ($failure | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath (Join-Path $logsRoot 'deployment-failure.json') -Encoding utf8
    throw
}
finally {
    if ($null -ne $tempPackageRoot -and (Test-Path -LiteralPath $tempPackageRoot)) {
        Remove-Item -LiteralPath $tempPackageRoot -Force -Recurse -ErrorAction SilentlyContinue
    }
}
