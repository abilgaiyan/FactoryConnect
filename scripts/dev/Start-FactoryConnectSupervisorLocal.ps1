# Developer-only supervisor entry launcher. Never use against production or restored incident databases.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConnectionStringEnvironmentVariable,
    [Parameter(Mandatory)][string]$OperatorId,
    [ValidateRange(1024,65535)][int]$ApiPort = 5080,
    [ValidateRange(1024,65535)][int]$DashboardPort = 5090,
    [switch]$EnableWrites
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $EnableWrites) { throw 'Explicit -EnableWrites is required for this developer-only launcher.' }
if ($ApiPort -eq $DashboardPort) { throw 'API and dashboard ports must differ.' }
if ([string]::IsNullOrWhiteSpace($OperatorId) -or $OperatorId.Length -gt 200) {
    throw 'A non-empty OperatorId (maximum 200 characters) is required.'
}
$connectionString = [Environment]::GetEnvironmentVariable($ConnectionStringEnvironmentVariable, 'Process')
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Connection-string environment variable is missing. Do not put credentials in command arguments.'
}
Add-Type -AssemblyName System.Data
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
$database = [string]$builder.InitialCatalog
# Require an unmistakable disposable developer database, not the V1 incident/demo/production database.
if ($database -cnotmatch '^FactoryConnect_V1_Dev_[A-Za-z0-9_]+$') {
    throw 'Database must be explicitly named FactoryConnect_V1_Dev_<suffix>.'
}
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT DB_NAME(), CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Updateability')), OBJECT_ID('dbo.SupervisorProductionEntry', 'U')"
    $reader = $command.ExecuteReader()
    try {
        if (-not $reader.Read()) { throw 'Database preflight returned no result.' }
        if ([string]$reader.GetString(0) -cne $database) { throw 'Database identity mismatch.' }
        if ([string]$reader.GetString(1) -cne 'READ_WRITE') { throw 'Development database is not writable.' }
        if ($reader.IsDBNull(2)) { throw 'Migration 024 is missing: dbo.SupervisorProductionEntry not found.' }
    } finally { $reader.Close() }
} finally { $connection.Dispose() }

function Assert-Port([int]$port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    try { $listener.Start() } finally { $listener.Stop() }
}
Assert-Port $ApiPort
Assert-Port $DashboardPort

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$api = Join-Path $root 'src/FactoryConnect.Api/FactoryConnect.Api.csproj'
$dashboard = Join-Path $root 'src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj'
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
function Start-LocalProject([string]$project, [string]$url, [hashtable]$settings, [bool]$skipClientBuild) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = 'dotnet'
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $quoted = '"' + $project.Replace('"', '\"') + '"'
    $info.Arguments = "run --no-launch-profile --project $quoted"
    if ($skipClientBuild) { $info.Arguments += ' -p:BuildDashboardClient=false' }
    $info.Arguments += " -- --urls $url"
    $info.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $info.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Development'
    foreach ($key in $settings.Keys) { $info.EnvironmentVariables[$key] = [string]$settings[$key] }
    $p = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $p) { throw "Could not start $project" }
    $processes.Add($p)
    return $p
}
function Wait-LocalHealth([string]$url, [System.Diagnostics.Process]$p) {
    for ($i = 0; $i -lt 120; $i++) {
        if ($p.HasExited) { throw "Service exited before healthy: $url" }
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "Health check timed out: $url"
}
$apiUrl = "http://127.0.0.1:$ApiPort"
$dashboardUrl = "http://127.0.0.1:$DashboardPort"
Write-Host "Developer-only supervisor commissioning: $database (loopback only)"
Write-Host 'No migration, MTConnect Edge host, polling or production deployment will be started.'
try {
    $apiProcess = Start-LocalProject $api $apiUrl @{
        'Persistence__Provider' = 'SqlServer'
        'PersistenceProviders__SqlServer__ConnectionString' = $connectionString
        'Commissioning__Enabled' = 'true'
        'Commissioning__ReadOnlyConnectionString' = $connectionString
        'SupervisorLocalEntry__EnableWrites' = 'true'
        'SupervisorLocalEntry__OperatorId' = $OperatorId
        'SupervisorLocalEntry__WriteConnectionString' = $connectionString
        'MTConnect__Machines__0__BaseUri' = 'http://127.0.0.1:1'
    } $false
    Wait-LocalHealth "$apiUrl/health" $apiProcess
    $dashboardProcess = Start-LocalProject $dashboard $dashboardUrl @{
        'Dashboard__ReportingApiBaseAddress' = "$apiUrl/"
        'SupervisorLocalEntry__EnableGateway' = 'true'
    } $false
    Wait-LocalHealth "$dashboardUrl/health/live" $dashboardProcess
    Write-Host "API: $apiUrl"
    Write-Host "Dashboard: $dashboardUrl"
    Write-Host 'Confirm a development machine/shift roster exists before submitting an entry.'
    Write-Host 'Press Ctrl+C to stop these locally owned processes.'
    while ($true) {
        foreach ($p in $processes) { if ($p.HasExited) { throw "Local service exited (PID $($p.Id))." } }
        Start-Sleep -Seconds 1
    }
} finally {
    foreach ($p in $processes) {
        try {
            if (-not $p.HasExited) {
                & taskkill.exe /PID $p.Id /T /F | Out-Null
                [void]$p.WaitForExit(5000)
            }
        } catch { Write-Warning "Could not stop owned process PID $($p.Id)." }
        $p.Dispose()
    }
}
