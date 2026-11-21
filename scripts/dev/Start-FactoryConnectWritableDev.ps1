[CmdletBinding()]
param(
    [string]$SqlInstance = 'DESKTOP-1PJK2OB\FCINVENTORY',
    [string]$Database = 'FactoryConnect_V1_Dev_Local',
    [switch]$Dashboard,
    [switch]$ApiOnly,
    [switch]$DevUi,
    [switch]$ApplyPendingMigrations,
    [bool]$SeparateServiceWindows = $true,
    [switch]$EnableSupervisorEntries,
    [switch]$EnableSupervisorShiftTargets,
    [switch]$EnableDowntimeReasonAdministration,
    [string]$SupervisorOperatorId,
    [ValidateRange(1024,65535)][int]$UiPort = 5173,
    [string]$Gg003DeviceKey = 'DEWAS-SR-GG-003',
    [string]$MoxaDeviceKey = 'MOXA-001',
    [ValidateRange(1024,65535)][int]$ApiPort = 5080,
    [ValidateRange(1024,65535)][int]$DashboardPort = 5090,
    [string]$ConnectionStringEnvironmentVariable
)

$ErrorActionPreference = 'Stop'
# Local configuration is optional; explicit command-line parameters take precedence.
# Keep credentials out of this file and out of source control.
$settingsPath = Join-Path $PSScriptRoot 'development.settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settingsMap = @{
        SqlInstance = 'SqlInstance'; Database = 'Database'; SupervisorOperatorId = 'SupervisorOperatorId';
        UiPort = 'UiPort'; ApiPort = 'ApiPort'; DashboardPort = 'DashboardPort';
        Gg003DeviceKey = 'Gg003DeviceKey'; MoxaDeviceKey = 'MoxaDeviceKey'
    }
    foreach ($name in $settingsMap.Keys) {
        if (-not $PSBoundParameters.ContainsKey($name)) {
            $property = $settings.PSObject.Properties[$settingsMap[$name]]
            if ($null -ne $property -and $null -ne $property.Value) {
                Set-Variable -Name $name -Value $property.Value
            }
        }
    }
    foreach ($name in @('Dashboard','ApiOnly','DevUi','EnableSupervisorEntries','EnableSupervisorShiftTargets','EnableDowntimeReasonAdministration')) {
        if (-not $PSBoundParameters.ContainsKey($name)) {
            $property = $settings.PSObject.Properties[$name]
            if ($null -ne $property -and $null -ne $property.Value) {
                Set-Variable -Name $name -Value ([bool]$property.Value)
            }
        }
    }
}

Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$apiProject = Join-Path $root 'src/FactoryConnect.Api/FactoryConnect.Api.csproj'
$dashboardProject = Join-Path $root 'src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj'
$owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Assert-PortAvailable([int]$port) {
    $tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    try { $tcp.Start() } finally { $tcp.Stop() }
}
function Wait-Health([string]$url,[System.Diagnostics.Process]$process,[int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Service exited before healthy: $url (exit $($process.ExitCode))." }
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "Health check timed out: $url"
}
# Start-Process opens an independent console on Windows. Temporarily setting process
# environment variables allows children to inherit secrets without placing them in
# command-line arguments or writing them to temporary files.
function Start-DebugWindow([string]$workingDirectory,[string]$command,[hashtable]$envOverrides) {
    $previous = @{}
    try {
        foreach ($name in $envOverrides.Keys) {
            $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, [string]$envOverrides[$name], 'Process')
        }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList "-NoProfile -NoExit -EncodedCommand $encoded" -WorkingDirectory $workingDirectory -PassThru
        if ($null -eq $process) { throw 'Could not open debug console.' }
        $owned.Add($process)
        return $process
    } finally {
        foreach ($name in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}
function Start-Service([string]$project,[string]$url,[hashtable]$envOverrides,[bool]$skipClientBuild) {
    if ($SeparateServiceWindows) {
        $arguments = 'run --no-build --no-launch-profile --project "' + $project + '"'
        if ($skipClientBuild) { $arguments += ' -p:BuildDashboardClient=false' }
        $arguments += ' -- --urls ' + $url
        $command = '& dotnet ' + $arguments + '; Write-Host "Service stopped. Exit code: $LASTEXITCODE"'
        $windowEnvironment = @{ 'ASPNETCORE_ENVIRONMENT' = 'Development'; 'DOTNET_ENVIRONMENT' = 'Development' }
        foreach ($name in $envOverrides.Keys) { $windowEnvironment[$name] = $envOverrides[$name] }
        return Start-DebugWindow $root $command $windowEnvironment
    }
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = 'dotnet'
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    # ProcessStartInfo.ArgumentList and .Environment are unavailable in Windows PowerShell 5.1.
    # Only trusted repository paths and locally constructed URLs are placed in arguments.
    $quotedProject = '"' + $project.Replace('"', '\\"') + '"'
    $info.Arguments = "run --no-build --no-launch-profile --project $quotedProject"
    if ($skipClientBuild) { $info.Arguments += ' -p:BuildDashboardClient=false' }
    $info.Arguments += " -- --urls $url"
    $info.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $info.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Development'
    foreach ($name in $envOverrides.Keys) { $info.EnvironmentVariables[$name] = [string]$envOverrides[$name] }
    $process = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Could not start $project" }
    $owned.Add($process)
    return $process
}
if ([string]::IsNullOrWhiteSpace($SqlInstance) -or [string]::IsNullOrWhiteSpace($Database)) {
    throw 'SqlInstance and Database are required.'
}
if ($DevUi -and $ApiOnly) { throw '-DevUi requires the Dashboard gateway; remove -ApiOnly.' }
if ($Dashboard -and $ApiOnly) { throw 'Use either -Dashboard or -ApiOnly, not both.' }
if (($EnableSupervisorEntries -or $EnableSupervisorShiftTargets) -and [string]::IsNullOrWhiteSpace($SupervisorOperatorId)) {
    throw '-EnableSupervisorEntries requires -SupervisorOperatorId for audit attribution.'
}
if (($EnableSupervisorEntries -or $EnableSupervisorShiftTargets) -and $SupervisorOperatorId.Length -gt 200) {
    throw 'SupervisorOperatorId must not exceed 200 characters.'
}
$startDashboard = -not $ApiOnly
if ($DevUi -and ($UiPort -eq $ApiPort -or $UiPort -eq $DashboardPort)) { throw 'Vite, API and Dashboard ports must differ.' }
if ($startDashboard -and $ApiPort -eq $DashboardPort) { throw 'API and Dashboard ports must differ.' }

Add-Type -AssemblyName System.Data
if ($ConnectionStringEnvironmentVariable) {
    $connectionString = [Environment]::GetEnvironmentVariable($ConnectionStringEnvironmentVariable,'Process')
    if ([string]::IsNullOrWhiteSpace($connectionString)) { throw 'Named connection-string environment variable is missing.' }
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
    if ($builder['Data Source'] -cne $SqlInstance -or $builder['Initial Catalog'] -cne $Database) {
        throw 'Connection string target differs from supplied SqlInstance/Database.'
    }
} else {
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = $SqlInstance
    $builder['Initial Catalog'] = $Database
    $builder['Integrated Security'] = $true
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $true
}
$connectionString = $builder.ConnectionString
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT DB_NAME(), CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Updateability'))"
    $reader = $command.ExecuteReader()
    if (-not $reader.Read()) { throw 'Database identity query returned no result.' }
    $actualDb = [string]$reader.GetString(0)
    $actualServer = [string]$reader.GetString(1)
    $updateability = [string]$reader.GetString(2)
    $reader.Close()
    if ($actualDb -cne $Database) { throw 'SQL database identity mismatch.' }
    $explicitWorkingDatabase = [Environment]::GetEnvironmentVariable('FACTORYCONNECT_DEV_WORKING_DATABASE', 'Process')
    $isDefaultDev = $Database -ceq 'FactoryConnect_V1_Dev_Local'
    $isExplicitWorkingCopy = (-not [string]::IsNullOrWhiteSpace($explicitWorkingDatabase)) -and
        ($Database -ceq $explicitWorkingDatabase) -and
        ($Database -cmatch '^FactoryConnect_V1_[A-Za-z0-9_]+_Work$') -and
        $SqlInstance.StartsWith(($env:COMPUTERNAME + '\'), [StringComparison]::OrdinalIgnoreCase)
    if ((-not $isDefaultDev -and -not $isExplicitWorkingCopy) -or $updateability -cne 'READ_WRITE') {
        throw 'Writable local development requires the default dev database or an explicitly opted-in local working copy in READ_WRITE mode; no changes were made.'
    }
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT COUNT(*) FROM dbo.FactoryConnectMigrationHistory WHERE MigrationId IN (32,33)"
    if ([int]$command.ExecuteScalar() -ne 2) { throw 'Development database must have migrations 032 and 033 applied.' }
    $command.CommandText = "SELECT CASE WHEN OBJECT_ID('dbo.SupervisorShiftTarget','U') IS NOT NULL AND OBJECT_ID('dbo.SupervisorAcceptedShiftProduction','U') IS NOT NULL THEN 1 ELSE 0 END"
    if ([int]$command.ExecuteScalar() -ne 1) { throw 'Development database is missing supervisor tables.' }
} finally { $connection.Dispose() }

if ($ApplyPendingMigrations) {
    $migrationProject = Join-Path $root 'src/FactoryConnect.Migrations/FactoryConnect.Migrations.csproj'
    if (-not (Test-Path -LiteralPath $migrationProject)) { throw 'Repository migration project not found.' }
    Write-Host "Applying repository SQL migrations to $actualServer / $actualDb..."
    $variableName = 'PersistenceProviders__SqlServer__ConnectionString'
    $oldValue = [Environment]::GetEnvironmentVariable($variableName, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($variableName, $connectionString, 'Process')
        & dotnet run --project $migrationProject
        if ($LASTEXITCODE -ne 0) { throw 'SQL migration failed; services were not started.' }
    } finally {
        [Environment]::SetEnvironmentVariable($variableName, $oldValue, 'Process')
    }
}
Assert-PortAvailable $ApiPort
if ($startDashboard) { Assert-PortAvailable $DashboardPort }
if ($DevUi) { Assert-PortAvailable $UiPort }
Write-Host "Target: $actualServer / $actualDb ($updateability)"
Write-Host "Current-state inventory: GG-003 ($Gg003DeviceKey), MOXA-001 ($MoxaDeviceKey); no polling enabled."
Write-Host 'Building writable local development API (no Edge acquisition host)...'
& dotnet build $apiProject --nologo
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
if ($startDashboard) {
    Write-Host 'Building Dashboard and React UI (npm ci + npm run build)...'
    & dotnet build $dashboardProject --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
}
$apiUrl = "http://127.0.0.1:$ApiPort"
$dashUrl = "http://127.0.0.1:$DashboardPort"
$uiUrl = "http://127.0.0.1:$UiPort"
try {
    $apiEnvironment = @{
        'Persistence__Provider' = 'SqlServer'
        'PersistenceProviders__SqlServer__ConnectionString' = $connectionString
        'Commissioning__Enabled' = 'true' # Local loopback-only API; CommissioningAccess still rejects remote/forwarded requests.
        'Commissioning__ReadOnlyConnectionString' = $connectionString
        'MTConnect__Machines__0__MachineId' = '677b030b-1ede-4a67-82ae-6a494791357f'
        'MTConnect__Machines__0__DeviceKey' = $Gg003DeviceKey
        'MTConnect__Machines__0__BaseUri' = 'http://127.0.0.1:5000'
        'MTConnect__Machines__0__FromSequence' = '1'
        'MTConnect__Machines__0__PollingInterval' = '00:00:01'
        'MTConnect__Machines__1__MachineId' = 'bfa0dc97-9a68-45ed-b189-1dacb7d863f0'
        'MTConnect__Machines__1__DeviceKey' = $MoxaDeviceKey
        'MTConnect__Machines__1__BaseUri' = 'http://127.0.0.1:5000'
        'MTConnect__Machines__1__FromSequence' = '1'
        'MTConnect__Machines__1__PollingInterval' = '00:00:01'
    }
    if ($EnableSupervisorEntries) {
        $apiEnvironment['SupervisorLocalEntry__EnableWrites'] = 'true'
        $apiEnvironment['SupervisorLocalEntry__OperatorId'] = $SupervisorOperatorId
        $apiEnvironment['SupervisorLocalEntry__WriteConnectionString'] = $connectionString
    }
    if ($EnableSupervisorShiftTargets) {
        $apiEnvironment['SupervisorLocalShiftTarget__EnableWrites'] = 'true'
        $apiEnvironment['SupervisorLocalShiftTarget__OperatorId'] = $SupervisorOperatorId
        $apiEnvironment['SupervisorLocalShiftTarget__WriteConnectionString'] = $connectionString
    }
    if ($EnableDowntimeReasonAdministration) {
        $apiEnvironment['SupervisorLocalDowntimeReasons__EnableWrites'] = 'true'
        $apiEnvironment['SupervisorLocalDowntimeReasons__WriteConnectionString'] = $connectionString
    }
    $apiProcess = Start-Service $apiProject $apiUrl $apiEnvironment $false
    Wait-Health "$apiUrl/health" $apiProcess 60
    Write-Host "API ready: $apiUrl"
    if ($startDashboard) {
        $dashboardEnvironment = @{
            'Dashboard__ReportingApiBaseAddress' = "$apiUrl/"
            'Dashboard__RequestTimeout' = '00:02:00'
        }
        if ($EnableSupervisorEntries) { $dashboardEnvironment['SupervisorLocalEntry__EnableGateway'] = 'true' }
        if ($DevUi -or $EnableSupervisorShiftTargets) {
            $dashboardEnvironment['SupervisorLocalShiftTarget__EnableGateway'] = 'true'
        }
        if ($EnableDowntimeReasonAdministration) {
            $dashboardEnvironment['SupervisorLocalDowntimeReasons__EnableGateway'] = 'true'
        }
        # Only advertise reporting sources explicitly selected for this development database.
        # Do not fabricate a roster or silently select the placeholder development machine.
        if ($null -ne (Get-Variable -Name settings -ErrorAction SilentlyContinue)) {
            $sourcesProperty = $settings.PSObject.Properties['ReportingSources']
            if ($null -ne $sourcesProperty -and $null -ne $sourcesProperty.Value) {
                $sourceIndex = 0
                foreach ($source in @($sourcesProperty.Value)) {
                    $machineGuid = [guid]::Empty
                    if (-not [guid]::TryParse([string]$source.MachineId, [ref]$machineGuid) -or
                        $machineGuid -eq [guid]::Empty -or
                        $machineGuid -eq [guid]'00000000-0000-0000-0000-000000000001' -or
                        [string]::IsNullOrWhiteSpace([string]$source.ProcessorId) -or
                        [string]::IsNullOrWhiteSpace([string]$source.SiteId)) {
                        throw 'ReportingSources requires a real machine GUID, processorId and siteId (no development placeholder).'
                    }
                    $prefix = "Dashboard__Sources__${sourceIndex}__"
                    $dashboardEnvironment[$prefix + 'MachineId'] = $machineGuid.ToString('D')
                    foreach ($field in @('ProcessorId','SiteId','ProductionLineId','DisplayName','GroupName')) {
                        $fieldProperty = $source.PSObject.Properties[$field]
                        $dashboardEnvironment[$prefix + $field] = if ($null -eq $fieldProperty) { '' } else { [string]$fieldProperty.Value }
                    }
                    $dashboardEnvironment[$prefix + 'DisplayOrder'] = [string]$sourceIndex
                    $sourceIndex++
                }
            }
        }
        $dashboardProcess = Start-Service $dashboardProject $dashUrl $dashboardEnvironment $true
        Wait-Health "$dashUrl/health/live" $dashboardProcess 30
        Write-Host "Dashboard gateway: $dashUrl (30-second upstream timeout)"
        Write-Host "React UI (built): $dashUrl/"
        if ($DevUi) {
            $clientDirectory = Join-Path $root 'src/FactoryConnect.Dashboard/ClientApp'
            $info = [System.Diagnostics.ProcessStartInfo]::new()
            $info.FileName = 'cmd.exe'
            $info.WorkingDirectory = $clientDirectory
            $info.UseShellExecute = $false
            $info.EnvironmentVariables['FACTORYCONNECT_DASHBOARD_DEV_PROXY'] = $dashUrl
            $info.Arguments = "/d /s /c `"npm.cmd run dev -- --host 127.0.0.1 --port $UiPort --strictPort`""
            if ($SeparateServiceWindows) {
                $vite = Start-DebugWindow $clientDirectory 'npm.cmd run dev -- --host 127.0.0.1 --port $env:FACTORYCONNECT_UI_PORT --strictPort; Write-Host "Vite stopped. Exit code: $LASTEXITCODE"' @{
                    'FACTORYCONNECT_DASHBOARD_DEV_PROXY' = $dashUrl
                    'FACTORYCONNECT_UI_PORT' = [string]$UiPort
                }
            } else {
                $vite = [System.Diagnostics.Process]::Start($info)
                if ($null -eq $vite) { throw 'Could not start Vite.' }
                $owned.Add($vite)
            }
            Wait-Health "$uiUrl/" $vite 45
            Write-Host "Development UI (Supervisor Desk preview): $uiUrl/supervisor-preview"
            Write-Host 'Vite is development-only. The built Dashboard UI remains separate on port 5090.'
        }
    }
    if ($EnableSupervisorEntries) {
        Write-Host 'Local supervisor entry writes requested for explicit operator identity; API commissioning access rules still apply.'
    } else {
        Write-Host 'Supervisor writes are disabled (default). Use -EnableSupervisorEntries and -SupervisorOperatorId to opt in.'
    }
    Write-Host 'Ctrl+C stops the processes started by this launcher.'
    while ($true) {
        foreach ($process in $owned) {
            if ($process.HasExited) { throw "Started service exited (PID $($process.Id))." }
        }
        Start-Sleep -Seconds 1
    }
} finally {
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) {
                # taskkill /T terminates only the process tree rooted at the PID we started.
                & taskkill.exe /PID $process.Id /T /F | Out-Null
                [void]$process.WaitForExit(5000)
            }
        } catch { Write-Warning "Cleanup failed for owned process PID $($process.Id)." }
        $process.Dispose()
    }
}
