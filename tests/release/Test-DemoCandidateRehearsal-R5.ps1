Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runnerPath = Join-Path $repoRoot 'scripts/release/Invoke-DemoCandidateRehearsal.ps1'
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

function Import-RunnerFunction {
    param([Parameter(Mandatory = $true)][string]$Name)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $runnerPath,
        [ref]$tokens,
        [ref]$parseErrors)

    if ($parseErrors.Count -ne 0) {
        throw "Runner parse failed: $($parseErrors[0].Message)"
    }

    $functionAst = $ast.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $Name
        },
        $true)

    if ($null -eq $functionAst) {
        throw "Runner function '$Name' was not found."
    }

    $bodyText = $functionAst.Body.Extent.Text
    if ($bodyText.Length -lt 2 -or $bodyText[0] -ne '{' -or $bodyText[$bodyText.Length - 1] -ne '}') {
        throw "Runner function '$Name' body shape was unexpected."
    }

    $body = [scriptblock]::Create($bodyText.Substring(1, $bodyText.Length - 2))
    Set-Item -Path ("Function:\script:{0}" -f $Name) -Value $body
}

$runnerText = Get-Content -Raw -LiteralPath $runnerPath

Assert-Contains -Text $runnerText -Expected "order = 'period-ascending'" -Name 'reporting request uses canonical period-ascending vocabulary'
Assert-True -Condition (-not $runnerText.Contains("order = 'Ascending'")) -Name 'obsolete Ascending reporting vocabulary is absent'
Assert-Contains -Text $runnerText -Expected 'ObservationProcessing__Streams__${index}' -Name 'runner emits observation-processing stream entries'
Assert-Contains -Text $runnerText -Expected '[string]$machine.streamIdentity' -Name 'observation StreamKey projects authoritative StreamIdentity'
Assert-Contains -Text $runnerText -Expected 'Wait-RehearsalOwnedProcessStability -OwnedProcess $edge -Seconds 2' -Name 'initial Edge uses bounded stabilization survival'
Assert-Contains -Text $runnerText -Expected 'Wait-RehearsalOwnedProcessStability -OwnedProcess $edge2 -Seconds 2' -Name 'restart Edge uses the same stabilization authority'
Assert-Contains -Text $runnerText -Expected 'throw "Rehearsal process ''$($OwnedProcess.Role)'' exited$detail."' -Name 'owned-process failure reports role and exit detail'

$restartAssertIndex = $runnerText.IndexOf('Assert-RehearsalOwnedProcessAlive -OwnedProcess $edge2', [System.StringComparison]::Ordinal)
$restartPassedIndex = $runnerText.IndexOf('$restartPassed = $true', [System.StringComparison]::Ordinal)
Assert-True -Condition ($restartAssertIndex -ge 0 -and $restartPassedIndex -gt $restartAssertIndex) -Name 'restart success is recorded only after restarted Edge liveness validation'

Import-RunnerFunction -Name 'Add-RehearsalObservationProcessingEnvironment'
Import-RunnerFunction -Name 'Assert-RehearsalOwnedProcessAlive'
Import-RunnerFunction -Name 'Wait-RehearsalOwnedProcessStability'

$machines = @()
for ($index = 1; $index -le 7; $index++) {
    $deviceKey = 'CNC-{0:D2}' -f $index
    $machines += [pscustomobject]@{
        machineId = ('11111111-1111-1111-1111-{0:D12}' -f (100 + $index))
        deviceKey = $deviceKey
        streamIdentity = "mtconnect:$deviceKey"
    }
}
$configuration = [pscustomobject]@{ machines = $machines }
$environment = @{}
Add-RehearsalObservationProcessingEnvironment -Environment $environment -Configuration $configuration

$streamKeyEntries = @($environment.Keys | Where-Object { $_ -match '^ObservationProcessing__Streams__\d+__StreamKey$' })
$machineIdEntries = @($environment.Keys | Where-Object { $_ -match '^ObservationProcessing__Streams__\d+__MachineId$' })
Assert-True -Condition ($streamKeyEntries.Count -eq 7) -Name 'exactly seven observation StreamKey entries are emitted'
Assert-True -Condition ($machineIdEntries.Count -eq 7) -Name 'exactly seven observation MachineId entries are emitted'
Assert-True -Condition (-not $environment.ContainsKey('ObservationProcessing__Streams__7__StreamKey')) -Name 'no eighth observation stream is emitted'

for ($index = 0; $index -lt 7; $index++) {
    $machine = $machines[$index]
    Assert-True `
        -Condition ($environment["ObservationProcessing__Streams__${index}__MachineId"] -ceq [string]$machine.machineId) `
        -Name "stream $index MachineId matches topology"
    Assert-True `
        -Condition ($environment["ObservationProcessing__Streams__${index}__StreamKey"] -ceq [string]$machine.streamIdentity) `
        -Name "stream $index StreamKey projects authoritative StreamIdentity"
    Assert-True `
        -Condition ([string]$machine.streamIdentity -ceq "mtconnect:$([string]$machine.deviceKey)") `
        -Name "stream $index topology StreamIdentity matches mtconnect DeviceKey identity"
}

if ($env:OS -ne 'Windows_NT') {
    Write-Host 'SKIP: R5 owned-process runtime proof requires Windows.'
    Write-Host 'FC-033.R5 rehearsal acceptance matrix: PASS (structural/configuration proof; Windows lifecycle proof skipped)'
    return
}

$proofRoot = Join-Path $repoRoot ('artifacts/release/r5-edge-liveness-proof/' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null

try {
    $childExe = Join-Path $proofRoot 'FactoryConnect.R5.EdgeLivenessChild.exe'
    $childSource = @'
using System;
using System.Threading;

public static class Program
{
    public static int Main()
    {
        string mode = Environment.GetEnvironmentVariable("FACTORYCONNECT_R5_CHILD_MODE") ?? string.Empty;
        if (string.Equals(mode, "stable", StringComparison.Ordinal))
        {
            Thread.Sleep(5000);
            return 0;
        }
        if (string.Equals(mode, "fail", StringComparison.Ordinal))
        {
            Thread.Sleep(100);
            return 7;
        }
        return 3;
    }
}
'@

    Add-Type `
        -TypeDefinition $childSource `
        -Language CSharp `
        -OutputAssembly $childExe `
        -OutputType ConsoleApplication

    Assert-True -Condition (Test-Path -LiteralPath $childExe) -Name 'R5 liveness proof child compiled under Windows PowerShell 5.1'

    $stable = Start-RehearsalProcess `
        -Role 'Edge-R5-Stable' `
        -FilePath $childExe `
        -WorkingDirectory $proofRoot `
        -StdOutPath (Join-Path $proofRoot 'stable.stdout.log') `
        -StdErrPath (Join-Path $proofRoot 'stable.stderr.log') `
        -Environment @{ FACTORYCONNECT_R5_CHILD_MODE = 'stable' }

    Wait-RehearsalOwnedProcessStability -OwnedProcess $stable -Seconds 1
    Assert-RehearsalOwnedProcessAlive -OwnedProcess $stable
    Assert-True -Condition (-not $stable.Process.HasExited) -Name 'healthy owned Edge process survives stabilization and boundary validation'
    [void](Stop-RehearsalProcess -OwnedProcess $stable -GraceSeconds 0)
    $stableJournal = [System.Collections.Generic.List[object]]::new()
    Close-RehearsalProcessStreams -OwnedProcess $stable -RepoRoot $repoRoot -ProcessEvidence $stableJournal -SkipStop

    $failed = Start-RehearsalProcess `
        -Role 'Edge-R5-Failed' `
        -FilePath $childExe `
        -WorkingDirectory $proofRoot `
        -StdOutPath (Join-Path $proofRoot 'failed.stdout.log') `
        -StdErrPath (Join-Path $proofRoot 'failed.stderr.log') `
        -Environment @{ FACTORYCONNECT_R5_CHILD_MODE = 'fail' }

    Assert-True -Condition $failed.Process.WaitForExit(5000) -Name 'failed Edge proof child exits within bounded wait'
    Assert-True -Condition ($failed.Process.ExitCode -eq 7) -Name 'failed Edge proof child exposes expected exit code'
    $failure = $null
    try {
        Assert-RehearsalOwnedProcessAlive -OwnedProcess $failed
    }
    catch {
        $failure = $_.Exception.Message
    }
    Assert-True -Condition ($null -ne $failure) -Name 'dead Edge fails dependent acceptance directly'
    Assert-Contains -Text $failure -Expected 'Edge-R5-Failed' -Name 'dead Edge failure identifies owned role'
    Assert-Contains -Text $failure -Expected 'exit code 7' -Name 'dead Edge failure includes exit code when available'
    $failedJournal = [System.Collections.Generic.List[object]]::new()
    Close-RehearsalProcessStreams -OwnedProcess $failed -RepoRoot $repoRoot -ProcessEvidence $failedJournal

    Write-Host 'FC-033.R5 rehearsal acceptance matrix: PASS'
}
finally {
    if (Test-Path -LiteralPath $proofRoot) {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
