#requires -Version 7.0
<#
.SYNOPSIS
  Creates an isolated Gate B SQL database from an existing backup.
.DESCRIPTION
  Refuses to overwrite any database. Does not change the incident or
  preserved qualification databases. Run only on the local SQL host.
.EXAMPLE
  ./New-GateBDatabase.ps1 -BackupPath 'F:\backups\verified.bak' -DataDirectory 'F:\SQLData'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$Server = 'DESKTOP-1PJK2OB\FCINVENTORY',
    [string]$Database = 'FactoryConnect_P0_GateB_20261109'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Server -cne 'DESKTOP-1PJK2OB\FCINVENTORY' -or
    $Database -cne 'FactoryConnect_P0_GateB_20261109') {
    throw 'Only the exact local Gate B target is permitted.'
}
if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) { throw 'Backup file not found.' }
if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) { throw 'Data directory not found.' }
Add-Type -AssemblyName 'Microsoft.Data.SqlClient' -ErrorAction SilentlyContinue
if (-not ('Microsoft.Data.SqlClient.SqlConnection' -as [type])) {
    throw 'Microsoft.Data.SqlClient is required. Run from an environment with the SQL client assembly installed.'
}
$builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new()
$builder.DataSource = $Server
$builder.InitialCatalog = 'master'
$builder.IntegratedSecurity = $true
$builder.Encrypt = $true
$builder.TrustServerCertificate = $true
$builder.ApplicationName = 'FactoryConnect-GateB-Restore'
$connection = [Microsoft.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    $cmd = $connection.CreateCommand()
    $cmd.CommandText = "SELECT DB_ID(N'FactoryConnect_P0_GateB_20261109')"
    if ($cmd.ExecuteScalar() -isnot [DBNull]) { throw 'Target database already exists. Refusing overwrite.' }

    $cmd.CommandText = 'RESTORE HEADERONLY FROM DISK = @backup'
    $cmd.Parameters.Clear() | Out-Null
    $null = $cmd.Parameters.Add('@backup', [System.Data.SqlDbType]::NVarChar, 4000)
    $cmd.Parameters['@backup'].Value = (Resolve-Path -LiteralPath $BackupPath).Path
    $cmd.CommandTimeout = 60
    $header = $cmd.ExecuteReader()
    try {
        if (-not $header.Read()) { throw 'Backup header is empty.' }
        $sourceDb = [string]$header['DatabaseName']
        if ($sourceDb -cne 'FactoryConnect') {
            throw "Unapproved source database in backup: $sourceDb"
        }
    } finally { $header.Dispose() }

    $cmd.CommandText = 'RESTORE VERIFYONLY FROM DISK = @backup' 
    $cmd.CommandTimeout = 600
    $null = $cmd.ExecuteNonQuery()

    $cmd.CommandText = 'RESTORE FILELISTONLY FROM DISK = @backup'
    $files = @()
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) {
            $files += [pscustomobject]@{
                LogicalName = [string]$reader['LogicalName']
                Type = [string]$reader['Type']
            }
        }
    } finally { $reader.Dispose() }
    if ($files.Count -lt 2 -or ($files | Where-Object Type -NotIn @('D','L')).Count -gt 0) {
        throw 'Unexpected backup file layout. Manual review required.'
    }

    $moves = @()
    for ($i=0; $i -lt $files.Count; $i++) {
        $extension = if ($files[$i].Type -eq 'L') { '.ldf' } else { '.mdf' }
        $dest = Join-Path $DataDirectory ($Database + '_' + $i + $extension)
        if (Test-Path -LiteralPath $dest) { throw "Destination file exists: $dest" }
        $logical = $files[$i].LogicalName.Replace("'", "''")
        $physical = $dest.Replace("'", "''")
        $moves += "MOVE N'$logical' TO N'$physical'"
    }
    $backupSql = $cmd.Parameters['@backup'].Value.Replace("'", "''")
    $dbSql = $Database.Replace(']', ']]')
    $restore = "RESTORE DATABASE [$dbSql] FROM DISK = N'$backupSql' WITH " +
        ($moves -join ', ') + ', RECOVERY, STATS = 5'
    Write-Host "Source backup database: $sourceDb"
    Write-Host "New isolated database: $Database"
    Write-Host 'No REPLACE or existing-database overwrite will be used.'
    $confirmation = Read-Host "Type RESTORE-GATE-B to create this database"
    if ($confirmation -cne 'RESTORE-GATE-B') { throw 'Cancelled without restore.' }
    # Repeat the existence check immediately before the RESTORE.
    $cmd.Parameters.Clear() | Out-Null
    $cmd.CommandText = "SELECT DB_ID(N'FactoryConnect_P0_GateB_20261109')"
    if ($cmd.ExecuteScalar() -isnot [DBNull]) { throw 'Target appeared during confirmation; refusing.' }
    $cmd.CommandText = $restore
    $cmd.CommandTimeout = 600
    $null = $cmd.ExecuteNonQuery()
    Write-Host "Restored $Database. Verify machine baselines before starting any host."
}
finally { $connection.Dispose() }
