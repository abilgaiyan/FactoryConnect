[CmdletBinding()]
param(
    [string]$SqlInstance = 'DESKTOP-1PJK2OB\FCINVENTORY',
    [string]$Database = 'FactoryConnect_Gajra_Investigation_20261102',
    [switch]$Dashboard,
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
    foreach ($argument in @('run','--no-build','--no-launch-profile','--project',$project)) {
        [void]$info.ArgumentList.Add($argument)
    }
    if ($skipClientBuild) { [void]$info.ArgumentList.Add('-p:BuildDashboardClient=false') }
    foreach ($argument in @('--','--urls',$url)) { [void]$info.ArgumentList.Add($argument) }
    $info.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $info.Environment['DOTNET_ENVIRONMENT'] = 'Development'
    foreach ($name in $envOverrides.Keys) { $info.Environment[$name] = [string]$envOverrides[$name] }
    $process = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Could not start $project" }
    $owned.Add($process)
    return $process
}
if ([string]::IsNullOrWhiteSpace($SqlInstance) -or [string]::IsNullOrWhiteSpace($Database)) {
    throw 'SqlInstance and Database are required.'
}
if ($Dashboard -and $ApiPort -eq $DashboardPort) { throw 'API and Dashboard ports must differ.' }

Add-Type -AssemblyName System.Data
if ($ConnectionStringEnvironmentVariable) {
    $connectionString = [Environment]::GetEnvironmentVariable($ConnectionStringEnvironmentVariable,'Process')
    if ([string]::IsNullOrWhiteSpace($connectionString)) { throw 'Named connection-string environment variable is missing.' }
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
    if ($builder.DataSource -cne $SqlInstance -or $builder.InitialCatalog -cne $Database) {
        throw 'Connection string target differs from supplied SqlInstance/Database.'
    }
} else {
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder.DataSource = $SqlInstance
    $builder.InitialCatalog = $Database
    $builder.IntegratedSecurity = $true
    $builder.Encrypt = $true
    $builder.TrustServerCertificate = $true
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
if ($Dashboard) { Assert-PortAvailable $DashboardPort }
Write-Host "Target: $actualServer / $actualDb ($updateability)"
Write-Host 'Building local reporting API...'
& dotnet build $apiProject --nologo
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
if ($Dashboard) {
    & dotnet build $dashboardProject --nologo '-p:BuildDashboardClient=false'
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
}
$apiUrl = "http://127.0.0.1:$ApiPort"
$dashUrl = "http://127.0.0.1:$DashboardPort"
try {
    $apiProcess = Start-Service $apiProject $apiUrl @{
        'Persistence__Provider' = 'SqlServer'
        'PersistenceProviders__SqlServer__ConnectionString' = $connectionString
    } $false
    Wait-Health "$apiUrl/health" $apiProcess 60
    Write-Host "API ready: $apiUrl"
    if ($Dashboard) {
        $dashboardProcess = Start-Service $dashboardProject $dashUrl @{
            'Dashboard__ReportingApiBaseAddress' = "$apiUrl/"
            'Dashboard__RequestTimeout' = '00:00:30'
        } $true
        Wait-Health "$dashUrl/health/live" $dashboardProcess 30
        Write-Host "Dashboard gateway: $dashUrl (30-second upstream timeout)"
        Write-Host 'Dashboard UI requires prebuilt static assets; Vite is not launched.'
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
            if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        } catch { Write-Warning "Cleanup failed for owned process PID $($process.Id)." }
        $process.Dispose()
    }
}
