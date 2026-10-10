[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ExpectedSha)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $repositoryRoot
try {
    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedSha) { throw "Expected exact HEAD $ExpectedSha; found $head" }
    if (git status --porcelain) { throw 'Verification requires a clean worktree.' }
    git diff --check 9d35a9c923c0cab8d57978fb9429a891da21cdfd...HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Diff check failed.' }
    dotnet build FactoryConnect.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test tests/FactoryConnect.Core.Tests/FactoryConnect.Core.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'Category!=SqlServerIntegration'
    if ($LASTEXITCODE -ne 0) { throw 'Non-SQL integration tests failed.' }
    Push-Location src/FactoryConnect.Dashboard/ClientApp
    try {
        $npmCommand = if ($env:OS -eq 'Windows_NT') { 'npm.cmd' } else { 'npm' }
        & $npmCommand ci
        if ($LASTEXITCODE -ne 0) { throw 'Frontend installation failed.' }
        foreach ($check in @('typecheck', 'contracts:check', 'test', 'build')) {
            & $npmCommand run $check
            if ($LASTEXITCODE -ne 0) { throw "Frontend $check failed." }
        }
    } finally { Pop-Location }
    if ((git rev-parse HEAD).Trim() -ne $ExpectedSha -or (git status --porcelain)) { throw 'Verification finished with a changed source state.' }
    Write-Host "Fleet/range verification PASS at $ExpectedSha; clean worktree."
} finally { Pop-Location }
