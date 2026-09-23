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
    $sha = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([Convert]::ToHexString($sha)).ToLowerInvariant()
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
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $process.add_OutputDataReceived({ param($sender, $args) if ($null -ne $args.Data) { $stdout.WriteLine($args.Data); $stdout.Flush() } })
    $process.add_ErrorDataReceived({ param($sender, $args) if ($null -ne $args.Data) { $stderr.WriteLine($args.Data); $stderr.Flush() } })

    [pscustomobject]@{
        Role = $Role
        Process = $process
        StdOutWriter = $stdout
        StdErrWriter = $stderr
        StartedAtUtc = [DateTimeOffset]::UtcNow
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
        return 'Exited'
    }

    try {
        $process.CloseMainWindow() | Out-Null
        if ($process.WaitForExit($GraceSeconds * 1000)) {
            return 'GracefulStop'
        }
    }
    catch {
    }

    $process.Kill($true)
    $process.WaitForExit()
    return 'ForcedStop'
}

function Close-RehearsalProcessStreams {
    param([Parameter(Mandatory = $true)]$OwnedProcess)

    try { $OwnedProcess.StdOutWriter.Dispose() } catch {}
    try { $OwnedProcess.StdErrWriter.Dispose() } catch {}
    try { $OwnedProcess.Process.Dispose() } catch {}
}
