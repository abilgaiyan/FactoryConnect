[CmdletBinding()]
param(
    [switch]$InstallClientDependencies
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$apiProject = Join-Path $repoRoot "src/FactoryConnect.Api/FactoryConnect.Api.csproj"
$dashboardProject = Join-Path $repoRoot "src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj"
$clientDirectory = Join-Path $repoRoot "src/FactoryConnect.Dashboard/ClientApp"

if ($InstallClientDependencies) {
    Write-Host "Installing dashboard client dependencies..."
    Push-Location $clientDirectory
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Starting FactoryConnect local dashboard development topology:"
Write-Host "  API       http://localhost:5080"
Write-Host "  Dashboard http://localhost:5090"
Write-Host "  React     http://localhost:5173"
Write-Host ""
Write-Host "Local mode gives Vite exclusive ownership of ClientApp/node_modules."
Write-Host "This launcher does not start SQL Server, FactoryConnect.Edge, or an MTConnect Agent."

$apiCommand = "`$env:ASPNETCORE_URLS='http://localhost:5080'; `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project `"$apiProject`""
$dashboardCommand = "`$env:ASPNETCORE_URLS='http://localhost:5090'; `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project `"$dashboardProject`" -p:BuildDashboardClient=false"
$clientCommand = "Set-Location -LiteralPath `"$clientDirectory`"; npm run dev"

$apiProcess = Start-Process powershell -PassThru -ArgumentList "-NoExit -Command `"$apiCommand`""
$dashboardProcess = Start-Process powershell -PassThru -ArgumentList "-NoExit -Command `"$dashboardCommand`""
$clientProcess = Start-Process powershell -PassThru -ArgumentList "-NoExit -Command `"$clientCommand`""

$processes = @($apiProcess, $dashboardProcess, $clientProcess)

Write-Host "Started $($processes.Count) development processes."
Write-Host "Open http://localhost:5173 after the services are ready."
