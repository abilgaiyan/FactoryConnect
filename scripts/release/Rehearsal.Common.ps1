Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoCandidate.Common.ps1')

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

function Resolve-RehearsalDatabaseAdmission {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][string]$DatabaseName
    )

    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
        $builder.ConnectionString = $ConnectionString
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

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) {
        throw "Failed to start rehearsal process '$Role'."
    }

    $stdout = [System.IO.StreamWriter]::new($StdOutPath, $false, [System.Text.UTF8Encoding]::new($false))
    $stderr = [System.IO.StreamWriter]::new($StdErrPath, $false, [System.Text.UTF8Encoding]::new($false))
    $process.add_OutputDataReceived({ param($sender, $args) if ($null -ne $args.Data) { $stdout.WriteLine($args.Data); $stdout.Flush() } })
    $process.add_ErrorDataReceived({ param($sender, $args) if ($null -ne $args.Data) { $stderr.WriteLine($args.Data); $stderr.Flush() } })
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    [pscustomobject]@{
        Role = $Role
        Process = $process
        StdOutWriter = $stdout
        StdErrWriter = $stderr
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

    $process.Kill($true)
    $process.WaitForExit()
    $OwnedProcess.TerminationReason = 'ForcedStop'
    $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
    $OwnedProcess.ExitCode = $process.ExitCode
    return $OwnedProcess.TerminationReason
}

function Close-RehearsalProcessStreams {
    param([Parameter(Mandatory = $true)]$OwnedProcess)

    if (-not $OwnedProcess.Process.HasExited) {
        [void](Stop-RehearsalProcess -OwnedProcess $OwnedProcess)
    }
    elseif ($null -eq $OwnedProcess.TerminationReason) {
        $OwnedProcess.TerminationReason = 'Exited'
        $OwnedProcess.StoppedAtUtc = [DateTimeOffset]::UtcNow
        $OwnedProcess.ExitCode = $OwnedProcess.Process.ExitCode
    }

    $journalVariable = Get-Variable -Name processEvidence -Scope 1 -ErrorAction SilentlyContinue
    $repoRootVariable = Get-Variable -Name repoRoot -Scope 1 -ErrorAction SilentlyContinue
    if (-not $OwnedProcess.Journaled -and $null -ne $journalVariable) {
        $repoRootValue = if ($null -ne $repoRootVariable) { [string]$repoRootVariable.Value } else { $null }
        $stdoutPath = if ([string]::IsNullOrWhiteSpace($repoRootValue)) { $OwnedProcess.StdOutPath } else { [System.IO.Path]::GetRelativePath($repoRootValue, $OwnedProcess.StdOutPath).Replace('\','/') }
        $stderrPath = if ([string]::IsNullOrWhiteSpace($repoRootValue)) { $OwnedProcess.StdErrPath } else { [System.IO.Path]::GetRelativePath($repoRootValue, $OwnedProcess.StdErrPath).Replace('\','/') }
        $journalVariable.Value.Add([ordered]@{
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

    try { $OwnedProcess.StdOutWriter.Dispose() } catch {}
    try { $OwnedProcess.StdErrWriter.Dispose() } catch {}
    try { $OwnedProcess.Process.Dispose() } catch {}
}
