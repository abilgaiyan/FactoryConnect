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
