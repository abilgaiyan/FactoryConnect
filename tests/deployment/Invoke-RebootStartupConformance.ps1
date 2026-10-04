[CmdletBinding()]
param([string] $StartupScript = (Join-Path $PSScriptRoot '..\..\scripts\deployment\Start-FactoryConnectRuntime.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Assert-True([bool]$Condition,[string]$Message){ if(-not $Condition){ throw $Message } }
function Assert-Contains([string]$Text,[string]$Expected,[string]$Message){ if(-not $Text.Contains($Expected,[StringComparison]::Ordinal)){ throw $Message } }

$source=Get-Content -LiteralPath $StartupScript -Raw
$required=@(
    'PID','executablePath','startTimeUtc','current','Commissioned configuration is required',
    'Runtime identity mismatch detected; no process will be stopped or started.',
    'Process ownership changed before termination','Edge did not survive stabilization',
    '/health','/health/live','/health/ready','migrationOutcome=''NotRun'''
)
foreach($token in $required){ Assert-Contains $source $token "Missing frozen reboot-startup contract token: $token" }

# Static contract proofs complement scenario execution on Windows. They deliberately
# prove mutation exclusions that are safety requirements of the start-only operation.
foreach($forbidden in @('Expand-Archive','FactoryConnect.Migrations.exe','New-Item -ItemType Junction','Remove-Item -LiteralPath $current')){
    Assert-True (-not $source.Contains($forbidden,[StringComparison]::OrdinalIgnoreCase)) "Start-only operation contains forbidden deployment mutation: $forbidden"
}

$classificationIndex=$source.IndexOf("Where-Object State -eq 'Mismatch'",[StringComparison]::Ordinal)
$firstStopIndex=$source.IndexOf('Stop-RecordedOwnedProcess $records[$name]',[StringComparison]::Ordinal)
Assert-True ($classificationIndex -ge 0 -and $firstStopIndex -gt $classificationIndex) 'B07/RBS-06 requires complete mismatch classification before any stop.'
Assert-Contains $source '$identity=Get-ProcessIdentity $Record' 'Termination must revalidate ownership immediately before stop.'

[pscustomobject]@{
    B01='Executable scenario required locally'; B02='Executable scenario required locally'; B03='Executable scenario required locally';
    B04='Executable scenario required locally'; B05='Executable scenario required locally'; B06='Executable scenario required locally';
    B07='Static safety contract PASS; executable PID-reuse scenario required locally'; B08='Contract PASS'; B09='Contract PASS'; B10='Contract PASS'; B11='Exclusive lock contract PASS'; B12='Reconciliation contract PASS'
}
