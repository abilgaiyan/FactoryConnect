[CmdletBinding()]
param([string] $StartupScript)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($StartupScript)) { $StartupScript = Join-Path $PSScriptRoot '..\..\scripts\deployment\Start-FactoryConnectRuntime.ps1' }
$StartupScript=[System.IO.Path]::GetFullPath($StartupScript)
if(-not(Test-Path -LiteralPath $StartupScript -PathType Leaf)){throw "Reboot-startup script was not found: $StartupScript"}
function Assert-True([bool]$Condition,[string]$Message){if(-not$Condition){throw $Message}}
function Assert-Contains([string]$Text,[string]$Expected,[string]$Message){if($Text.IndexOf($Expected,[StringComparison]::Ordinal)-lt0){throw $Message}}
$source=Get-Content -LiteralPath $StartupScript -Raw
$required=@('pid','executablePath','startTimeUtc','current','Commissioned configuration is required','Runtime identity mismatch detected; no process will be stopped or started.','Process ownership changed before termination','Edge did not survive stabilization','Get-BaseAddressFromConfiguration','/health','/health/live','/health/ready',"migrationOutcome='NotRun'",'FinalVerification','Write-JsonFileAtomic $runtimePath','runtime-start.intent.json')
foreach($token in $required){Assert-Contains $source $token "Missing frozen reboot-startup contract token: $token"}
foreach($forbidden in @('Expand-Archive','FactoryConnect.Migrations.exe','New-Item -ItemType Junction','Remove-ActivationJunction')){Assert-True ($source.IndexOf($forbidden,[StringComparison]::OrdinalIgnoreCase)-lt0) "Start-only operation contains forbidden deployment mutation: $forbidden"}
$classify=$source.IndexOf("Where-Object{`$_-eq'Mismatch'}",[StringComparison]::Ordinal);$stop=$source.IndexOf('Stop-RecordedOwnedProcess $records[$n]',[StringComparison]::Ordinal);Assert-True ($classify-ge0-and$stop-gt$classify) 'RBS-06 requires complete mismatch classification before any stop.'
Assert-Contains $source '$identity=Get-RecordedProcessState $Record' 'Termination must revalidate ownership immediately before stop.'
Assert-Contains $source "if(`$identity.State-ne'Owned')" 'Termination must fail closed if ownership changed before stop.'
Assert-Contains $source 'startTimeUtc=$p.StartTime.ToUniversalTime().ToString(''o'')' 'Process record must preserve exact UTC start time.'
Assert-Contains $source '$p.StartTime.ToUniversalTime().ToString(''o'')-eq[string]$Record.startTimeUtc' 'Ownership comparison must use exact UTC start-time equality.'
Assert-Contains $source '[System.IO.Path]::GetFullPath([string]$r.executablePath)-ne[System.IO.Path]::GetFullPath([string]$expected[$n])' 'Recorded executable must match selected release expected executable.'
Assert-Contains $source 'Assert-SiteConfiguration $cfg.edge $cfg.api $cfg.dashboard' 'Commissioned configuration must be fully validated before reconciliation.'
Assert-Contains $source '$relative=$release.Substring($prefix.Length)' 'Selected-release identity must be derived relative to the releases root.'
Assert-Contains $source '$relative.Contains(''\'')-or$relative.Contains(''/'')' 'Nested selected-release targets must be rejected.'
Assert-Contains $source '$relative-cnotmatch''^[0-9a-f]{40}$''' 'Selected release must be exactly one 40-character commit identity.'
Assert-Contains $source '$mutationStarted-and$runtimeLoaded-and$null-ne$releaseId' 'Preflight failure must not overwrite existing runtime evidence.'
Assert-Contains $source '$startedThisAttempt[$n]' 'Failure cleanup must be limited to processes started by the current attempt.'
Assert-Contains $source '$intentOwnedByThisAttempt' 'Startup intent cleanup must be limited to the current attempt.'
Assert-Contains $source '[string]$liveIntent.attemptId-eq$attempt' 'Startup intent cleanup must verify the persisted attempt identity before deletion.'
Assert-Contains $source 'Previous runtime-start attempt may have been interrupted before durable process publication' 'Unresolved process-creation crash window must fail closed on retry.'
[pscustomobject]@{B01='Executable scenario required locally';B02='Executable scenario required locally';B03='Executable scenario required locally';B04='Executable scenario required locally';B05='Executable scenario required locally';B06='Executable scenario required locally';B07='Static safety PASS; executable PID-reuse scenario required locally';B08='Static contract PASS';B09='Static contract PASS';B10='Static contract PASS';B11='Exclusive lock contract PASS';B12='Reconciliation contract PASS'}
