Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$commonPath = Join-Path $repoRoot 'scripts/release/Rehearsal.Common.ps1'
. $commonPath

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Condition) {
        throw "Assertion failed: $Name"
    }

    Write-Host "PASS: $Name"
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Text.Contains($Expected)) {
        throw "Assertion failed: $Name. Expected marker '$Expected'."
    }

    Write-Host "PASS: $Name"
}

$commonText = Get-Content -Raw -LiteralPath $commonPath
foreach ($forbidden in @(
    'add_OutputDataReceived',
    'add_ErrorDataReceived',
    'BeginOutputReadLine',
    'BeginErrorReadLine',
    'ReadToEnd('
)) {
    if ($commonText.Contains($forbidden)) {
        throw "Host-unsafe process capture mechanism remains present: $forbidden"
    }
}
Assert-True -Condition ($commonText.Contains('CopyToAsync')) -Name 'managed capture uses concurrent CopyToAsync pumps'
Assert-True -Condition ($commonText.Contains('Task.WhenAll')) -Name 'managed capture waits for both stream pumps'
Assert-True -Condition ($commonText.Contains('StreamCapture.Complete()')) -Name 'process evidence waits for capture completion'
Write-Host 'PASS: PowerShell process-event callbacks and sequential ReadToEnd capture are absent.'

if ($env:OS -ne 'Windows_NT') {
    Write-Host 'SKIP: R4 runtime process-capture proof requires Windows.'
    return
}

$powershellExe = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $powershellExe)) {
    $powershellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
}

$proofRoot = Join-Path $repoRoot ('artifacts/release/r4-process-capture-proof/' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null

try {
    Write-Host '=== R4 natural-exit proof ==='
    $naturalScript = Join-Path $proofRoot 'natural-child.ps1'
    $naturalStdOut = Join-Path $proofRoot 'natural.stdout.log'
    $naturalStdErr = Join-Path $proofRoot 'natural.stderr.log'
    @'
Set-StrictMode -Version Latest
for ($i = 1; $i -le 400; $i++) {
    [Console]::Out.WriteLine("NATURAL-OUT-$i")
    [Console]::Error.WriteLine("NATURAL-ERR-$i")
}
[Console]::Out.WriteLine('NATURAL-FINAL-STDOUT')
[Console]::Error.WriteLine('NATURAL-FINAL-STDERR')
[Console]::Out.Flush()
[Console]::Error.Flush()
'@ | Set-Content -LiteralPath $naturalScript -Encoding UTF8

    $natural = Start-RehearsalProcess `
        -Role 'R4-NaturalExit' `
        -FilePath $powershellExe `
        -WorkingDirectory $proofRoot `
        -StdOutPath $naturalStdOut `
        -StdErrPath $naturalStdErr `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $naturalScript)

    $natural.Process.WaitForExit()
    $naturalJournal = [System.Collections.Generic.List[object]]::new()
    Close-RehearsalProcessStreams `
        -OwnedProcess $natural `
        -RepoRoot $repoRoot `
        -ProcessEvidence $naturalJournal

    Assert-True -Condition $natural.StreamCapture.StandardOutputPump.IsCompleted -Name 'natural-exit stdout pump completed after child exit'
    Assert-True -Condition $natural.StreamCapture.StandardErrorPump.IsCompleted -Name 'natural-exit stderr pump completed after child exit'
    Assert-True -Condition ($naturalJournal.Count -eq 1) -Name 'natural-exit process journal completed'
    $naturalOutText = Get-Content -Raw -LiteralPath $naturalStdOut
    $naturalErrText = Get-Content -Raw -LiteralPath $naturalStdErr
    Assert-Contains -Text $naturalOutText -Expected 'NATURAL-FINAL-STDOUT' -Name 'natural-exit stdout includes final marker'
    Assert-Contains -Text $naturalErrText -Expected 'NATURAL-FINAL-STDERR' -Name 'natural-exit stderr includes final marker'
    Write-Host 'PASS: natural exit settles both pumps, preserves complete logs, journals, and caller survives.'

    Write-Host '=== R4 forced-stop proof ==='
    $forcedScript = Join-Path $proofRoot 'forced-child.ps1'
    $forcedStdOut = Join-Path $proofRoot 'forced.stdout.log'
    $forcedStdErr = Join-Path $proofRoot 'forced.stderr.log'
    @'
Set-StrictMode -Version Latest
[Console]::Out.WriteLine('FORCED-STDOUT-BEGIN')
[Console]::Error.WriteLine('FORCED-STDERR-BEGIN')
[Console]::Out.Flush()
[Console]::Error.Flush()
$i = 0
while ($true) {
    $i++
    [Console]::Out.WriteLine("FORCED-OUT-$i")
    [Console]::Error.WriteLine("FORCED-ERR-$i")
    [Console]::Out.Flush()
    [Console]::Error.Flush()
    Start-Sleep -Milliseconds 25
}
'@ | Set-Content -LiteralPath $forcedScript -Encoding UTF8

    $forced = Start-RehearsalProcess `
        -Role 'R4-ForcedStop' `
        -FilePath $powershellExe `
        -WorkingDirectory $proofRoot `
        -StdOutPath $forcedStdOut `
        -StdErrPath $forcedStdErr `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $forcedScript)

    Start-Sleep -Milliseconds 750
    $termination = Stop-RehearsalProcess -OwnedProcess $forced -GraceSeconds 0
    Assert-True -Condition ($termination -eq 'ForcedStop') -Name 'forced-stop process tree terminated through existing ownership control'

    $forcedJournal = [System.Collections.Generic.List[object]]::new()
    Close-RehearsalProcessStreams `
        -OwnedProcess $forced `
        -RepoRoot $repoRoot `
        -ProcessEvidence $forcedJournal `
        -SkipStop

    Assert-True -Condition $forced.StreamCapture.StandardOutputPump.IsCompleted -Name 'forced-stop stdout pump settled after termination'
    Assert-True -Condition $forced.StreamCapture.StandardErrorPump.IsCompleted -Name 'forced-stop stderr pump settled after termination'
    Assert-True -Condition ($forcedJournal.Count -eq 1) -Name 'forced-stop process journal completed'
    $forcedOutText = Get-Content -Raw -LiteralPath $forcedStdOut
    $forcedErrText = Get-Content -Raw -LiteralPath $forcedStdErr
    Assert-Contains -Text $forcedOutText -Expected 'FORCED-STDOUT-BEGIN' -Name 'forced-stop stdout retained usable output'
    Assert-Contains -Text $forcedErrText -Expected 'FORCED-STDERR-BEGIN' -Name 'forced-stop stderr retained usable output'
    Write-Host 'PASS: forced stop settles both pumps, retains usable logs, journals, and caller survives.'

    Write-Host 'FC-033.R4 process-capture host-safety matrix: PASS'
}
finally {
    if (Test-Path -LiteralPath $proofRoot) {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
