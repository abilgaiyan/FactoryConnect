# Shared RBS-14 authority. Loaded by deployment and start-only operations.
$script:FactoryConnectShutdownTimeoutDefaultSeconds = 120
$script:TerminationObservations = New-Object 'Collections.Generic.List[object]'

function Request-FactoryConnectTermination {
    param($Process)
    Stop-Process -InputObject $Process -ErrorAction Stop
}
function Wait-FactoryConnectTermination {
    param($Process,[int]$TimeoutSeconds)
    return $Process.WaitForExit($TimeoutSeconds * 1000)
}
function Invoke-FactoryConnectTermination {
    param($Record,[ValidateRange(1,600)][int]$TimeoutSeconds)
    $process = $null
    $outcome = 'ObservationFailure'
    $detail = $null
    $phase = 'Ownership'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        try { $process = Get-Process -Id ([int]$Record.pid) -ErrorAction Stop }
        catch {
            if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId*') {
                $outcome = 'Exited'; $detail = 'Recorded process is already absent.'
            } else { throw }
        }
        if ($null -ne $process) {
            # Pin this process instance before ownership validation. Subsequent
            # request/wait use this instance, never a PID reacquired after kill.
            [void]$process.Handle
            $process.Refresh()
            if ($process.HasExited) {
                $outcome = 'Exited'; $detail = 'Recorded process has already exited.'
            } elseif ($process.Id -ne [int]$Record.pid -or [IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath([string]$Record.executablePath) -or
                      $process.StartTime.ToUniversalTime().ToString('o') -cne [string]$Record.startTimeUtc) {
                $outcome = 'IdentityMismatch'; $detail = 'PID/path/UTC start time does not match; termination not requested.'
            } else {
                $phase = 'Request'
                try { Request-FactoryConnectTermination $process }
                catch {
                    # Exit may race the request. Only this exact instance can
                    # establish that race; access/observation errors stay failures.
                    $process.Refresh()
                    if (-not $process.HasExited) { throw }
                }
                $phase = 'Observation'
                $waited = Wait-FactoryConnectTermination $process $TimeoutSeconds
                $process.Refresh()
                if ($waited -or $process.HasExited) {
                    $outcome = 'Exited'; $detail = 'Exact recorded process termination confirmed.'
                } else {
                    $outcome = 'Timeout'; $detail = 'Exact recorded process remained unexited at the observation deadline.'
                }
            }
        }
    } catch { $outcome = 'ObservationFailure'; $detail = $_.Exception.Message }
    finally {
        $watch.Stop()
        if ($null -ne $process) {
            try { $process.Dispose() }
            catch { $outcome='ObservationFailure'; $phase='Disposal'; $detail=$_.Exception.Message }
        }
    }
    [pscustomobject]@{
        processName=[string]$Record.name;pid=[int]$Record.pid
        executablePath=[string]$Record.executablePath;startTimeUtc=[string]$Record.startTimeUtc
        outcome=$outcome;phase=$phase;timeoutSeconds=$TimeoutSeconds
        elapsedMilliseconds=$watch.ElapsedMilliseconds;detail=$detail
    }
}
function Stop-RecordedOwnedProcess {
    param($Record)
    $result = Invoke-FactoryConnectTermination $Record $ShutdownTimeoutSeconds
    $script:TerminationObservations.Add($result)
    if ($result.outcome -ne 'Exited') {
        throw "Termination $($result.outcome) for $($Record.name) PID $($Record.pid) during $($result.phase): $($result.detail)"
    }
}
