[CmdletBinding()]
param([string]$StartupScript)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($StartupScript)) {
    $StartupScript = Join-Path $PSScriptRoot '../../scripts/deployment/Start-FactoryConnectRuntime.ps1'
}
# Execute the production failure handler with controlled launch/cleanup outcomes.
# No fixture process or factory installation is accessed by these fault cases.
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($StartupScript), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Startup script does not parse.' }
$mainTry = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1]
$handler = [scriptblock]::Create($mainTry.CatchClauses[0].Body.Extent.Text.Trim().Substring(1).TrimEnd().TrimEnd('}'))
function Read-JsonFile([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Get-RecordedProcessState($Record) { [pscustomobject]@{ State = $script:proofState } }
function Stop-RecordedOwnedProcess($Record) { if ($script:proofState -ne 'Absent') { throw 'Injected cleanup failure.' } }
function Write-JsonFileAtomic { throw 'Injected publication failure.' }
function New-RuntimeState { return @{} }
$root = Join-Path ([IO.Path]::GetTempPath()) ('RBS-intent-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$results = [ordered]@{}
try {
    foreach ($case in @('UnknownLaunch','CleanupFailure','VerifiedAbsent','ForeignIntent','LockLoser','PublicationFailure')) {
        $attempt = 'current'
        $intentPath = Join-Path $root 'runtime-start.intent.json'
        $runtimePath = Join-Path $root 'runtime.json'
        $persistedAttempt = if ($case -eq 'ForeignIntent') { 'foreign' } else { $attempt }
        @{attemptId=$persistedAttempt} | ConvertTo-Json | Set-Content $intentPath
        $lock = if ($case -eq 'LockLoser') { $null } else { [pscustomobject]@{} }
        $intentOwnedByThisAttempt = $case -notin @('ForeignIntent','LockLoser')
        $intentProcessRecord = if ($case -eq 'UnknownLaunch') { $null } else { @{pid=42;name='edge'} }
        $script:proofState = if ($case -eq 'VerifiedAbsent') { 'Absent' } else { 'Owned' }
        $records = [ordered]@{edge=$intentProcessRecord;api=$null;dashboard=$null}
        $states = [ordered]@{edge='Owned';api='Absent';dashboard='Absent'}
        $startedThisAttempt = [ordered]@{edge=$case -eq 'CleanupFailure';api=$false;dashboard=$false}
        $mutationStarted = $true
        $runtimeLoaded = $true
        $releaseId = 'fixture'
        $release = $root
        $failurePhase = 'Injected'
        try { throw 'Injected launch/publication failure.' } catch {
            try { & $handler } catch { }
        }
        $exists = Test-Path -LiteralPath $intentPath
        if ($exists -ne ($case -ne 'VerifiedAbsent')) { throw "$case violated intent preservation." }
        $results[$case] = 'PASS'
    }
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
[pscustomobject]$results
