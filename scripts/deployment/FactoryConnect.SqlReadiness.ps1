Set-StrictMode -Version Latest
$script:SqlReadinessExecutable = Join-Path $PSScriptRoot 'apps/sql-readiness/FactoryConnect.SqlReadiness.exe'

function Invoke-FactoryConnectSqlProvider {
    param([string]$Operation,[string]$ConnectionString,[int]$BudgetMilliseconds,
          [System.Threading.CancellationToken]$CancellationToken)
    if (-not (Test-Path -LiteralPath $script:SqlReadinessExecutable -PathType Leaf)) {
        throw 'Packaged SQL readiness executable missing; runtime not invoked.'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $script:SqlReadinessExecutable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $started=$false
    try {
        $started=$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $request=@{Operation=$Operation;ConnectionString=$ConnectionString;BudgetMilliseconds=$BudgetMilliseconds} | ConvertTo-Json -Compress
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes($request)
        $process.StandardInput.BaseStream.Write($bytes,0,$bytes.Length)
        $process.StandardInput.BaseStream.Close()
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(20)) {
            $CancellationToken.ThrowIfCancellationRequested()
            if ($clock.ElapsedMilliseconds -ge $BudgetMilliseconds) {
                throw 'SQL readiness provider deadline exceeded.'
            }
        }
        $CancellationToken.ThrowIfCancellationRequested()
        $reply = $output.GetAwaiter().GetResult() | ConvertFrom-Json
        [void]$errors.GetAwaiter().GetResult()
        if ($reply.Status -eq 'AuthenticationRejected' -and $process.ExitCode -eq 12) {
            throw 'SQL readiness authentication rejected; runtime not invoked.'
        }
        if ($process.ExitCode -eq 0 -and $reply.Status -in @('Ready','Valid')) { return $true }
        return $false
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Read-FactoryConnectSqlReadinessConfiguration {
    param([Parameter(Mandatory=$true)][string]$InstallRoot,
          [Diagnostics.Stopwatch]$Clock,[int]$TimeoutSeconds,
          [Threading.CancellationToken]$CancellationToken)
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
            $CancellationToken.ThrowIfCancellationRequested()
            $remaining=[int][Math]::Floor($TimeoutSeconds * 1000 - $Clock.Elapsed.TotalMilliseconds)
            if ($remaining -le 0) { throw 'Deadline' }
            if (-not (Invoke-FactoryConnectSqlProvider 'Validate' $value ([Math]::Min(5000,$remaining)) $CancellationToken)) { throw 'Target' }
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
    return (Invoke-FactoryConnectSqlProvider 'Probe' $ConnectionString $BudgetMilliseconds $CancellationToken)
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
        $targets = @(Read-FactoryConnectSqlReadinessConfiguration $InstallRoot $clock $TimeoutSeconds $CancellationToken)
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
                    # Authentication rejection is permanent for the commissioned identity.
                    # Never log the provider message or connection string.
                    if ($_.Exception.Message -eq 'SQL readiness authentication rejected; runtime not invoked.') {
                        $status='AuthenticationRejected'
                        throw 'SQL readiness authentication rejected; runtime not invoked.'
                    }
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
