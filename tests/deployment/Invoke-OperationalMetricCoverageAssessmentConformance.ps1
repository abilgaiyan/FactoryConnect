[CmdletBinding()]
param(
    [ValidateSet('Portable', 'FocusedSql', 'Full')]
    [string] $Mode = 'Portable'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Invoke-CoverageCheck {
    param([scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Conformance command failed with exit code $LASTEXITCODE." }
}

Push-Location $repositoryRoot
try {
    Invoke-CoverageCheck { git rev-parse HEAD }
    Invoke-CoverageCheck { git diff --check }
    Invoke-CoverageCheck { git diff --check dc811d119de847889b4668a902ccefcdaa0f95ab...HEAD }
    if ($Mode -ne 'Portable' -and [string]::IsNullOrWhiteSpace($env:FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING)) {
        throw 'Set FACTORYCONNECT_SQLSERVER_TEST_CONNECTION_STRING for a disposable SQL test server first.'
    }

    if ($Mode -eq 'Portable') {
        Invoke-CoverageCheck { dotnet build FactoryConnect.sln -c Release }
        Invoke-CoverageCheck { dotnet test tests/FactoryConnect.Core.Tests/FactoryConnect.Core.Tests.csproj -c Release --no-build }
        Invoke-CoverageCheck { dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'Category!=SqlServerIntegration' }
    }
    elseif ($Mode -eq 'FocusedSql') {
        Invoke-CoverageCheck { dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~SqlServerOperationalMetricCoverageAssessmentIntegrationTests|FullyQualifiedName~SqlServerMigration015UpgradeIntegrationTests' }
    }
    else {
        Invoke-CoverageCheck { dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release }
    }
}
finally { Pop-Location }
