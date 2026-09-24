[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SqlConnectionString
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoRuntime.Common.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$contract = Get-DemoContract
$resetLease = $null

try {
    # The same exclusive lease used by the supervisor makes the stopped check
    # atomic with the reset: a running supervisor refuses reset, and a new
    # supervisor cannot start while reset owns the lease.
    $resetLease = Open-DemoSupervisorLease -RepoRoot $repoRoot -Purpose Reset

    $databaseTarget = Resolve-DemoDatabaseTarget -ConnectionString $SqlConnectionString
    if ([string]$databaseTarget.DatabaseName -cne $contract.DatabaseName) {
        throw "Interactive demo reset targets '$([string]$databaseTarget.DatabaseName)'; expected exactly '$($contract.DatabaseName)'."
    }

    Write-Host "Resetting only database '$($contract.DatabaseName)' on '$([string]$databaseTarget.Server)'."

    $query = @"
IF DB_ID(N'$($contract.DatabaseName)') IS NOT NULL
BEGIN
    ALTER DATABASE [$($contract.DatabaseName)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$($contract.DatabaseName)];
END;
CREATE DATABASE [$($contract.DatabaseName)];
"@

    Invoke-DemoSqlcmd -DatabaseTarget $databaseTarget -Database 'master' -Query $query

    Write-Host "Reset complete. '$($contract.DatabaseName)' is empty; FactoryConnect.Migrations will repopulate schema on the next demo start."
    Write-Host 'Candidate payloads, rehearsal evidence/workspaces, and prior demo session logs were not modified.'
}
finally {
    if ($null -ne $resetLease) {
        $resetLease.Dispose()
    }
}
