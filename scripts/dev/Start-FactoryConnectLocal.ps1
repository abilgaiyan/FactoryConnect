[CmdletBinding()]
param(
    [string]$SqlInstance = 'DESKTOP-1PJK2OB\FCINVENTORY',
    [string]$Database = 'FactoryConnect_Gajra_Investigation_20261102',
    [switch]$Dashboard,
    [switch]$ApiOnly,
    [switch]$DevUi,
    [ValidateRange(1024,65535)][int]$UiPort = 5173,
    [string]$Gg003DeviceKey = 'DEWAS-SR-GG-003',
    [string]$MoxaDeviceKey = 'MOXA-001',
    [ValidateRange(1024,65535)][int]$ApiPort = 5080,
    [ValidateRange(1024,65535)][int]$DashboardPort = 5090,
    [string]$ConnectionStringEnvironmentVariable
)

$ErrorActionPreference = 'Stop'
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
function Start-Service([string]$project,[string]$url,[hashtable]$envOverrides,[bool]$skipClientBuild) {
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
    if ($updateability -cne 'READ_ONLY') {
        throw 'ReportingOnly requires a physically READ_ONLY database; no changes were made.'
    }
} finally { $connection.Dispose() }

Assert-PortAvailable $ApiPort
if ($startDashboard) { Assert-PortAvailable $DashboardPort }
if ($DevUi) { Assert-PortAvailable $UiPort }
Write-Host "Target: $actualServer / $actualDb ($updateability)"
Write-Host "Current-state inventory: GG-003 ($Gg003DeviceKey), MOXA-001 ($MoxaDeviceKey); no polling enabled."
Write-Host 'Building local reporting API...'
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
    $apiProcess = Start-Service $apiProject $apiUrl @{
        'Persistence__Provider' = 'SqlServer'
        'PersistenceProviders__SqlServer__ConnectionString' = $connectionString
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
    } $false
    Wait-Health "$apiUrl/health" $apiProcess 60
    Write-Host "API ready: $apiUrl"
    if ($startDashboard) {
        $dashboardProcess = Start-Service $dashboardProject $dashUrl @{
            'Dashboard__ReportingApiBaseAddress' = "$apiUrl/"
            'Dashboard__RequestTimeout' = '00:00:30'
        } $true
        Wait-Health "$dashUrl/health/live" $dashboardProcess 30
        Write-Host "Dashboard gateway: $dashUrl (30-second upstream timeout)"
        Write-Host "React UI (built): $dashUrl/"
        if ($DevUi) {
            $clientDirectory = Join-Path $root 'src/FactoryConnect.Dashboard/ClientApp'
            $info = [System.Diagnostics.ProcessStartInfo]::new()
            $info.FileName = 'cmd.exe'
            $info.WorkingDirectory = $clientDirectory
            $info.UseShellExecute = $false
            $info.Arguments = "/d /s /c `"npm.cmd run dev -- --host 127.0.0.1 --port $UiPort --strictPort`""
            $vite = [System.Diagnostics.Process]::Start($info)
            if ($null -eq $vite) { throw 'Could not start Vite.' }
            $owned.Add($vite)
            Wait-Health "$uiUrl/" $vite 45
            Write-Host "Development UI (Supervisor Desk preview): $uiUrl/supervisor-preview"
            Write-Host 'Vite is development-only. The built Dashboard UI remains separate on port 5090.'
        }
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
