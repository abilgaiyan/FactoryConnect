Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoCandidate.Common.ps1')

if (-not ('FactoryConnect.Rehearsal.ManagedProcessStreamCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FactoryConnect.Rehearsal
{
    public sealed class ManagedProcessStreamCapture : IDisposable
    {
        private readonly FileStream _standardOutput;
        private readonly FileStream _standardError;
        private Task _standardOutputPump;
        private Task _standardErrorPump;
        private bool _completed;
        private bool _disposed;

        public ManagedProcessStreamCapture(string standardOutputPath, string standardErrorPath)
        {
            if (standardOutputPath == null) throw new ArgumentNullException("standardOutputPath");
            if (standardErrorPath == null) throw new ArgumentNullException("standardErrorPath");

            _standardOutput = new FileStream(
                standardOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                81920,
                true);

            try
            {
                _standardError = new FileStream(
                    standardErrorPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    true);
            }
            catch
            {
                _standardOutput.Dispose();
                throw;
            }
        }

        public Task StandardOutputPump { get { return _standardOutputPump; } }
        public Task StandardErrorPump { get { return _standardErrorPump; } }

        public void Begin(Process process)
        {
            if (process == null) throw new ArgumentNullException("process");
            if (_standardOutputPump != null || _standardErrorPump != null)
            {
                throw new InvalidOperationException("Rehearsal process stream capture was already started.");
            }

            _standardOutputPump = process.StandardOutput.BaseStream.CopyToAsync(_standardOutput);
            _standardErrorPump = process.StandardError.BaseStream.CopyToAsync(_standardError);
        }

        public void Complete()
        {
            if (_completed) return;
            if (_standardOutputPump == null || _standardErrorPump == null)
            {
                throw new InvalidOperationException("Rehearsal process stream capture was not started.");
            }

            try
            {
                Task.WhenAll(_standardOutputPump, _standardErrorPump).GetAwaiter().GetResult();
                _standardOutput.Flush();
                _standardError.Flush();
                _completed = true;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Rehearsal process output capture failed.", exception);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _standardOutput.Dispose();
            _standardError.Dispose();
        }
    }
}
'@
}

function ConvertTo-RehearsalRedactedText {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $redacted = $Text
    $patterns = @(
        '(?i)(Password|Pwd|User ID|UserID|Uid|Token|ApiKey|Secret)\s*=\s*[^;\s]+',
        '(?i)(Data Source|Server)\s*=\s*[^;]+;.*?(Initial Catalog|Database)\s*=\s*[^;]+;.*'
    )

    foreach ($pattern in $patterns) {
        $redacted = [regex]::Replace($redacted, $pattern, '<redacted>')
    }

    return $redacted
}

function Get-RehearsalConfigurationProjection {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$CandidateId
    )

    [ordered]@{
        schemaVersion = '1.0'
        candidateId = $CandidateId
        siteId = [string]$Configuration.siteId
        timeZoneId = [string]$Configuration.timeZoneId
        fixture = [ordered]@{
            listenAddress = [string]$Configuration.fixture.listenAddress
            port = [int]$Configuration.fixture.port
        }
        api = [ordered]@{
            listenAddress = [string]$Configuration.api.listenAddress
            port = [int]$Configuration.api.port
        }
        dashboard = [ordered]@{
            listenAddress = [string]$Configuration.dashboard.listenAddress
            port = [int]$Configuration.dashboard.port
        }
        database = [ordered]@{
            serverIdentity = [string]$Configuration.database.serverIdentity
            databaseName = [string]$Configuration.database.databaseName
        }
        shifts = @($Configuration.shifts | ForEach-Object {
            [ordered]@{
                shiftId = [string]$_.shiftId
                startsAtLocal = [string]$_.startsAtLocal
                endsAtLocal = [string]$_.endsAtLocal
            }
        })
        machines = @($Configuration.machines | Sort-Object machineId | ForEach-Object {
            [ordered]@{
                machineId = [string]$_.machineId
                deviceKey = [string]$_.deviceKey
                basePath = [string]$_.basePath
                streamIdentity = [string]$_.streamIdentity
                processorId = [string]$_.processorId
                productionLineId = [string]$_.productionLineId
            }
        })
    }
}

function Get-RehearsalProjectionSha256 {
    param([Parameter(Mandatory = $true)]$Projection)

    $json = ($Projection | ConvertTo-Json -Depth 20)
    $normalized = ($json -replace "`r`n", "`n") + "`n"
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($normalized)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sha = $sha256.ComputeHash($bytes)
    }
    finally {
        $sha256.Dispose()
    }
    return ([System.BitConverter]::ToString($sha)).Replace('-', '').ToLowerInvariant()
}

function New-RehearsalInvocationNonce {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-RehearsalListeningProcessIds {
    param([Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port)

    if ($env:OS -ne 'Windows_NT') {
        throw 'Rehearsal listener ownership verification currently requires Windows.'
    }

    $command = Get-Command Get-NetTCPConnection -ErrorAction Stop
    return @(& $command -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { [int]$_ })
}

function Assert-RehearsalPortAvailable {
    param([Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port)

    $owners = @(Get-RehearsalListeningProcessIds -Port $Port)
    if ($owners.Count -gt 0) {
        throw "Rehearsal fixture port $Port already has a listener owned by PID(s): $($owners -join ', ')."
    }
}

function Wait-RehearsalFixtureReadiness {
    param(
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter(Mandatory = $true)][Uri]$HealthUri,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedInvocationNonce,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $process = $OwnedProcess.Process
        $process.Refresh()
        if ($process.HasExited) {
            throw "Rehearsal fixture process exited before attributable readiness with code $($process.ExitCode)."
        }

        $owners = @(Get-RehearsalListeningProcessIds -Port $Port)
        if ($owners.Count -gt 0 -and -not ($owners -contains [int]$process.Id)) {
            throw "Rehearsal fixture port $Port is owned by unrelated PID(s): $($owners -join ', '); expected PID $($process.Id)."
        }

        if ($owners -contains [int]$process.Id) {
            try {
                $health = Invoke-RestMethod -Uri $HealthUri -Method Get -TimeoutSec 5
                if ([string]$health.invocationNonce -cne $ExpectedInvocationNonce) {
                    throw 'Rehearsal fixture health identity did not match the current invocation.'
                }

                return [pscustomobject]@{
                    ProcessId = [int]$process.Id
                    InvocationIdentityMatched = $true
                }
            }
            catch {
                if ($_.Exception.Message -eq 'Rehearsal fixture health identity did not match the current invocation.') {
                    throw
                }
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for attributable rehearsal fixture readiness from '$HealthUri'."
}

function Resolve-RehearsalDatabaseAdmission {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][string]$DatabaseName
    )

    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$ConnectionString)
    }
    catch {
        throw "Rehearsal SQL connection string is invalid: $($_.Exception.Message)"
    }

    $server = [string]$builder.DataSource
    if ([string]::IsNullOrWhiteSpace($server)) {
        throw 'Rehearsal SQL configuration must contain Server or Data Source with a non-empty value.'
    }
    if ($DatabaseName -notmatch '^[A-Za-z0-9_-]+$') {
        throw 'Rehearsal database name contains unsupported characters.'
    }

    $integrated = [bool]$builder.IntegratedSecurity
    $userId = [string]$builder.UserID
    $password = [string]$builder.Password

    if (-not $integrated -and [string]::IsNullOrWhiteSpace($userId)) {
        throw 'Rehearsal database provisioning requires Integrated Security or explicit User ID.'
    }

    [pscustomobject]@{
        Server = $server
        IntegratedSecurity = $integrated
        UserId = if ([string]::IsNullOrWhiteSpace($userId)) { $null } else { $userId }
        Password = if ([string]::IsNullOrEmpty($password)) { $null } else { $password }
    }
}

function Get-RehearsalFailureClassification {
    param([Parameter(Mandatory = $true)][string]$Phase)

    switch ($Phase) {
        'CandidatePreVerification' { return 'Verification' }
        'ConfigurationAdmission' { return 'Configuration' }
        'DatabaseProvisioning' { return 'Infrastructure' }
        'FixtureStartup' { return 'Dependency' }
        'MigrationExecution' { return 'Application' }
        'RuntimeStartupAcceptance' { return 'Application' }
        'CandidatePostVerification' { return 'Verification' }
        'EvidenceFinalization' { return 'Verification' }
        default { throw "Unknown rehearsal phase '$Phase'." }
    }
}

function Publish-RehearsalTerminalEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceRoot,
        [Parameter(Mandatory = $true)][string]$EvidenceText
    )

    $jsonPath = Join-Path $EvidenceRoot 'rehearsal.json'
    $checksumPath = Join-Path $EvidenceRoot 'rehearsal.json.sha256'
    $jsonTempPath = Join-Path $EvidenceRoot '.rehearsal.json.tmp'
    $checksumTempPath = Join-Path $EvidenceRoot '.rehearsal.json.sha256.tmp'

    foreach ($path in @($jsonPath, $checksumPath, $jsonTempPath, $checksumTempPath)) {
        if (Test-Path -LiteralPath $path) {
            throw "Rehearsal evidence finalization refuses to overwrite existing content: '$path'."
        }
    }

    Write-DemoCandidateUtf8NoBom -Path $jsonTempPath -Text $EvidenceText
    $evidenceSha256 = Get-DemoCandidateSha256 -Path $jsonTempPath
    $checksumText = "$evidenceSha256  rehearsal.json`n"
    Write-DemoCandidateUtf8NoBom -Path $checksumTempPath -Text $checksumText

    $verifiedSha256 = Get-DemoCandidateSha256 -Path $jsonTempPath
    $verifiedChecksum = Get-Content -Raw -LiteralPath $checksumTempPath
    if ($verifiedSha256 -cne $evidenceSha256 -or $verifiedChecksum -cne $checksumText) {
        throw 'Rehearsal evidence temporary files failed internal consistency verification.'
    }

    [System.IO.File]::Move($checksumTempPath, $checksumPath)
    [System.IO.File]::Move($jsonTempPath, $jsonPath)

    [pscustomobject]@{
        EvidencePath = $jsonPath
        EvidenceSha256 = $evidenceSha256
    }
}

function Get-RehearsalContainedRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Target
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $targetFull = [System.IO.Path]::GetFullPath($Target)
    $rootPrefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar

    if (-not $targetFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Rehearsal evidence path '$targetFull' is outside repository root '$rootFull'."
    }

    return $targetFull.Substring($rootPrefix.Length).Replace('\', '/')
}

function Start-RehearsalProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$StdOutPath,
        [Parameter(Mandatory = $true)][string]$StdErrPath,
        [Parameter()][string[]]$Arguments = @(),
        [Parameter()][hashtable]$Environment = @{}
    )

    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true

    foreach ($argument in $Arguments) {
        [void]$start.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $start.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $capture = [FactoryConnect.Rehearsal.ManagedProcessStreamCapture]::new($StdOutPath, $StdErrPath)
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw "Failed to start rehearsal process '$Role'."
        }

        $processStartTimeUtc = $process.StartTime.ToUniversalTime()
        try {
            $capture.Begin($process)
        }
        catch {
            $failedOwned = [pscustomobject]@{
                Role = $Role
                Process = $process
                ProcessStartTimeUtc = $processStartTimeUtc
                StartedAtUtc = [DateTimeOffset]::UtcNow
                StoppedAtUtc = $null
                TerminationReason = $null
                ExitCode = $null
            }
            try { [void](Stop-RehearsalProcess -OwnedProcess $failedOwned -GraceSeconds 0) } catch {}
            throw
        }
    }
    catch {
        $capture.Dispose()
        $process.Dispose()
        throw
    }

    [pscustomobject]@{
        Role = $Role
        Process = $process
        ProcessStartTimeUtc = $processStartTimeUtc
        StreamCapture = $capture
        StdOutPath = $StdOutPath
        StdErrPath = $StdErrPath
        StartedAtUtc = [DateTimeOffset]::UtcNow
        StoppedAtUtc = $null
        TerminationReason = $null
        ExitCode = $null
        Journaled = $false
        FilePath = $FilePath
        WorkingDirectory = $WorkingDirectory
    }
}

function Stop-RehearsalProcess {
    param(
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter()][int]$GraceSeconds = 5
    )

    $process = $OwnedProcess.Process
    if ($process.HasExited) {
        $OwnedProcess.TerminationReason = 'Exited'
        $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
        $OwnedProcess.ExitCode = $process.ExitCode
        return $OwnedProcess.TerminationReason
    }

    try {
        $process.CloseMainWindow() | Out-Null
        if ($process.WaitForExit($GraceSeconds * 1000)) {
            $OwnedProcess.TerminationReason = 'GracefulStop'
            $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
            $OwnedProcess.ExitCode = $process.ExitCode
            return $OwnedProcess.TerminationReason
        }
    }
    catch {
    }

    $process.Refresh()
    if ($process.HasExited) {
        $OwnedProcess.TerminationReason = 'GracefulStop'
        $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
        $OwnedProcess.ExitCode = $process.ExitCode
        return $OwnedProcess.TerminationReason
    }

    $capturedPid = [int]$process.Id
    $capturedStartTimeUtc = if ($null -ne $OwnedProcess.PSObject.Properties['ProcessStartTimeUtc']) {
        [DateTime]$OwnedProcess.ProcessStartTimeUtc
    }
    else {
        $process.StartTime.ToUniversalTime()
    }
    $observedStartTimeUtc = $process.StartTime.ToUniversalTime()
    if ($observedStartTimeUtc.Ticks -ne $capturedStartTimeUtc.Ticks) {
        throw "Rehearsal process identity changed before forced cleanup for role '$($OwnedProcess.Role)' (PID $capturedPid)."
    }

    $taskkill = Get-Command taskkill.exe -ErrorAction Stop
    & $taskkill.Source /PID ([string]$capturedPid) /T /F | Out-Null
    $taskkillExitCode = $LASTEXITCODE

    try { [void]$process.WaitForExit(5000) } catch {}
    $process.Refresh()
    if (-not $process.HasExited) {
        throw "Forced rehearsal cleanup did not terminate role '$($OwnedProcess.Role)' (PID $capturedPid); taskkill exit code $taskkillExitCode."
    }

    $OwnedProcess.TerminationReason = 'ForcedStop'
    $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
    $OwnedProcess.ExitCode = $process.ExitCode
    return $OwnedProcess.TerminationReason
}

function Close-RehearsalProcessStreams {
    param(
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$ProcessEvidence,
        [Parameter()][switch]$SkipStop
    )

    try {
        if (-not $SkipStop -and -not $OwnedProcess.Process.HasExited) {
            [void](Stop-RehearsalProcess -OwnedProcess $OwnedProcess)
        }
        elseif ($OwnedProcess.Process.HasExited -and $null -eq $OwnedProcess.TerminationReason) {
            $OwnedProcess.TerminationReason = 'Exited'
            $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
            $OwnedProcess.ExitCode = $OwnedProcess.Process.ExitCode
        }

        if ($null -eq $OwnedProcess.StoppedAtUtc) {
            $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
        }
        if ($OwnedProcess.Process.HasExited -and $null -eq $OwnedProcess.ExitCode) {
            $OwnedProcess.ExitCode = $OwnedProcess.Process.ExitCode
        }

        if ($null -ne $OwnedProcess.PSObject.Properties['StreamCapture'] -and $null -ne $OwnedProcess.StreamCapture) {
            $OwnedProcess.StreamCapture.Complete()
        }

        if (-not $OwnedProcess.Journaled) {
            $stdoutPath = Get-RehearsalContainedRelativePath -Root $RepoRoot -Target $OwnedProcess.StdOutPath
            $stderrPath = Get-RehearsalContainedRelativePath -Root $RepoRoot -Target $OwnedProcess.StdErrPath
            $ProcessEvidence.Add([ordered]@{
                role = $OwnedProcess.Role
                pid = $OwnedProcess.Process.Id
                executable = $OwnedProcess.FilePath
                startedAtUtc = $OwnedProcess.StartedAtUtc.ToString('O')
                stoppedAtUtc = $OwnedProcess.StoppedAtUtc.ToString('O')
                exitCode = $OwnedProcess.ExitCode
                terminationReason = $OwnedProcess.TerminationReason
                stdoutLog = $stdoutPath
                stderrLog = $stderrPath
            })
            $OwnedProcess.Journaled = $true
        }
    }
    finally {
        try {
            if ($null -ne $OwnedProcess.PSObject.Properties['StreamCapture'] -and $null -ne $OwnedProcess.StreamCapture) {
                $OwnedProcess.StreamCapture.Dispose()
            }
        }
        catch {}
        try { $OwnedProcess.Process.Dispose() } catch {}
    }
}
