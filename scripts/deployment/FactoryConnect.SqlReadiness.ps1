Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Data -ErrorAction Stop

function Read-FactoryConnectSqlReadinessConfiguration {
    param([Parameter(Mandatory=$true)][string]$InstallRoot)
    # Validate both configurations before any contact. Never include connection
    # strings, parser errors or provider exception messages in startup evidence.
    $targets = @()
    foreach ($name in @('edge','api')) {
        $stage='ReadFile'
        try {
            $path = Join-Path $InstallRoot "config/$name.production.json"
            $text = Get-Content -LiteralPath $path -Raw -ErrorAction Stop
            if ($text -match '__[A-Z0-9_]+__') { throw 'Placeholder' }
            $stage='Json'
            $configuration = $text | ConvertFrom-Json -ErrorAction Stop
            $stage='Provider'
            if ($configuration.Persistence.Provider -cne 'SqlServer') { throw 'Provider' }
            $value = [string]$configuration.PersistenceProviders.SqlServer.ConnectionString
            if ([string]::IsNullOrWhiteSpace($value)) { throw 'Missing' }
            $stage='ConnectionString'
            $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
            $builder.ConnectionString = $value
            $stage='Target'
            if ([string]::IsNullOrWhiteSpace($builder.DataSource) -or
                [string]::IsNullOrWhiteSpace($builder.InitialCatalog)) { throw 'Target' }
            $targets += [pscustomobject]@{Name=$name;ConnectionString=$value}
        } catch {
            throw "SQL readiness requires valid commissioned $name SQL configuration (stage: $stage)."
        }
    }
    return $targets
}

function Invoke-FactoryConnectSqlReadinessProbe {
    param([string]$ConnectionString,[int]$BudgetMilliseconds,
          [System.Threading.CancellationToken]$CancellationToken)
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder.ConnectionString = $ConnectionString
    # Preserve authentication, target and TLS policy. Only bound connection time
    # and disable pooling so readiness requires a new authenticated connection.
    $builder.ConnectTimeout = [Math]::Max(1,[int][Math]::Ceiling($BudgetMilliseconds / 1000.0))
    $builder.Pooling = $false
    $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
    $command = $null
    $deadline = [System.Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken)
    $deadline.CancelAfter($BudgetMilliseconds)
    try {
        $connection.OpenAsync($deadline.Token).GetAwaiter().GetResult()
        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT 1'
        $command.CommandTimeout = $builder.ConnectTimeout
        $value = $command.ExecuteScalarAsync($deadline.Token).GetAwaiter().GetResult()
        return ([int]$value -eq 1)
    } finally {
        if ($null -ne $command) { $command.Dispose() }
        $connection.Dispose()
        $deadline.Dispose()
    }
}

function Wait-FactoryConnectSqlReadiness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$InstallRoot,
        [ValidateRange(1,600)][int]$TimeoutSeconds = 180,
        [ValidateRange(1,30000)][int]$RetryMilliseconds = 2000,
        [ValidateRange(1,30000)][int]$AttemptMilliseconds = 5000,
        [System.Threading.CancellationToken]$CancellationToken = [System.Threading.CancellationToken]::None,
        [scriptblock]$OnEvidence = {},
        # Function-level conformance seam; never exposed by the boot CLI/config.
        [scriptblock]$Probe = ${function:Invoke-FactoryConnectSqlReadinessProbe}
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $attempts = 0
    $status = 'ConfigurationInvalid'
    $targetStatus = [ordered]@{edge='NotChecked';api='NotChecked'}
    try {
        $targets = @(Read-FactoryConnectSqlReadinessConfiguration $InstallRoot)
        $status = 'Waiting'
        while ($true) {
            $CancellationToken.ThrowIfCancellationRequested()
            if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $status = 'TimedOut'
                throw 'SQL readiness timed out; runtime not invoked.'
            }
            $attempts++
            # Both connections must succeed in the same pass. Earlier successes
            # are not retained across retries and distinct credentials are tested.
            $ready = $true
            foreach ($target in $targets) {
                $CancellationToken.ThrowIfCancellationRequested()
                $remaining = [int][Math]::Floor($TimeoutSeconds * 1000 - $clock.Elapsed.TotalMilliseconds)
                if ($remaining -le 0) { $ready=$false; break }
                $budget = [Math]::Min($AttemptMilliseconds,$remaining)
                $targetStatus[$target.Name] = 'Unavailable'
                try {
                    $success = & $Probe $target.ConnectionString $budget $CancellationToken
                    if ($success -eq $true) { $targetStatus[$target.Name]='Ready' }
                    else { $ready=$false }
                } catch {
                    $CancellationToken.ThrowIfCancellationRequested()
                    $ready=$false
                    # Do not persist SQL/provider diagnostics containing secrets.
                }
            }
            if ($ready -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
                $status='Ready'
                return
            }
            $remaining = [int][Math]::Floor($TimeoutSeconds * 1000 - $clock.Elapsed.TotalMilliseconds)
            if ($remaining -gt 0) {
                [void]$CancellationToken.WaitHandle.WaitOne([Math]::Min($RetryMilliseconds,$remaining))
            }
        }
    } catch {
        if ($CancellationToken.IsCancellationRequested) {
            $status='Cancelled'
            throw 'SQL readiness cancelled; runtime not invoked.'
        }
        throw
    } finally {
        $clock.Stop()
        $evidence = [ordered]@{
            schemaVersion='1.0';status=$status;attempts=$attempts
            elapsedMilliseconds=$clock.ElapsedMilliseconds;timeoutSeconds=$TimeoutSeconds
            completedAtUtc=[DateTime]::UtcNow.ToString('o');targets=$targetStatus
        }
        & $OnEvidence $evidence | Out-Null
    }
}
