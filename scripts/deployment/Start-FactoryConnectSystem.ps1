[CmdletBinding()]
param([string]$InstallRoot = 'D:\FactoryConnect')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Factory boot orchestration requires Windows.' }
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$attempt = [Guid]::NewGuid().ToString('N')
$deployment = Join-Path $root 'deployment'
$logRoot = Join-Path $deployment "logs/system-$attempt"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$phase = 'Acquisition'
$exitCode = 1
$lock = $null
$releaseId = $null
function Invoke-SystemChild {
    param([string]$Executable,[string]$Arguments)
    # File redirection avoids waiting for pipe EOF from long-lived descendants.
    # WaitForExit waits for cmd/powershell itself, not the acquisition process tree.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    try { $process.WaitForExit(); return [int]$process.ExitCode }
    finally { $process.Dispose() }
}
try {
    # Separate orchestration exclusion; never hold deployment.lock while invoking
    # RBS, which obtains that lock itself. Acquisition authority is not modified.
    $lock = [IO.File]::Open((Join-Path $deployment 'system-start.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $batch = Join-Path $root 'Start-Acquisition.bat'
    if (-not (Test-Path -LiteralPath $batch -PathType Leaf)) { throw "Acquisition operation missing: $batch" }
    # cmd expands percent signs even in quotes. Fail closed rather than invoke a
    # different path; task commissioning must use a plain installation path.
    if ($root.Contains('%') -or $root.Contains('"') -or $root.Contains("`r") -or $root.Contains("`n")) { throw 'InstallRoot cannot contain cmd expansion or quoting characters.' }
    $cmd = Join-Path $env:SystemRoot 'System32/cmd.exe'
    $arguments = '/d /v:off /s /c ""{0}" >"{1}" 2>"{2}""' -f $batch,(Join-Path $logRoot 'acquisition.out.log'),(Join-Path $logRoot 'acquisition.err.log')
    $exitCode = Invoke-SystemChild $cmd $arguments
    if ($exitCode -ne 0) { throw "Acquisition verification failed with exit code $exitCode; runtime not invoked." }
    $exitCode = 1
    $phase = 'Selection'
    $current = Join-Path $root 'current'
    $item = Get-Item -LiteralPath $current -Force
    $targets = @($item.Target)
    if ($item.LinkType -cne 'Junction' -or $targets.Count -ne 1) { throw 'current must be one selected-release junction.' }
    $target = [IO.Path]::GetFullPath([string]$targets[0]).TrimEnd('\')
    $releases = [IO.Path]::GetFullPath((Join-Path $root 'releases')).TrimEnd('\')
    if (-not $target.StartsWith($releases + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'current is outside the releases root.' }
    $releaseId = $target.Substring($releases.Length + 1)
    if ($releaseId -cnotmatch '^[0-9a-f]{40}$') { throw 'current must select one non-nested release identity.' }
    $runtime = Join-Path $current 'Start-FactoryConnectRuntime.ps1'
    if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) { throw 'Selected release does not contain the runtime startup authority.' }
    $phase = 'Runtime'
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    # Redirect at cmd level so children inheriting handles do not stall logging.
    $arguments = '/d /v:off /s /c ""{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -InstallRoot "{2}" >"{3}" 2>"{4}""' -f $powershell,$runtime,$root,(Join-Path $logRoot 'runtime.out.log'),(Join-Path $logRoot 'runtime.err.log')
    $exitCode = Invoke-SystemChild $cmd $arguments
    if ($exitCode -ne 0) { throw "Runtime startup failed with exit code $exitCode." }
    $phase = 'Completed'
} catch {
    $message = $_.Exception.Message
    if ($exitCode -eq 0) { $exitCode = 1 }
    Write-Error -Message $message -ErrorAction Continue
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    $evidence = [ordered]@{schemaVersion='1.0';attemptId=$attempt;completedAtUtc=[DateTime]::UtcNow.ToString('o');phase=$phase;exitCode=$exitCode;selectedRelease=$releaseId;installRoot=$root}
    if (Get-Variable message -ErrorAction SilentlyContinue) { $evidence['message'] = $message }
    $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logRoot 'system-start.json') -Encoding UTF8
}
exit $exitCode
