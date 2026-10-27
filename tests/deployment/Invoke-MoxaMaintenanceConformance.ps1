[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/deployment/FactoryConnect.MoxaMaintenance.ps1')
function Write-JsonFileAtomic { param($Path,$Value); $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Get-CimInstance { param($ClassName,$ErrorAction); if ($script:CompetingEdge -and $ClassName -eq 'Win32_Process') { [pscustomobject]@{Name='FactoryConnect.Edge.exe';CommandLine='edge'} } }
function Assert-True { param($Condition,$Message); if (-not $Condition) { throw $Message } }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('fc-moxa-' + [Guid]::NewGuid().ToString('N'))
$script:CompetingEdge = $false
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'config'),(Join-Path $fixture 'deployment'),(Join-Path $fixture 'logs') -Force | Out-Null
    $tool = Join-Path $fixture 'fake-recovery.ps1'
    'param($Operation,$Server,$Database,$Approval,$Evidence); $global:LASTEXITCODE=0' | Set-Content $tool
    $cfg = [ordered]@{PersistenceProviders=@{SqlServer=@{ConnectionString='fixture-only'}};ProductionProcessing=@{Machines=@(
        @{MachineId='bfa0dc97-9a68-45ed-b189-1dacb7d863f0';ActivityStreamKey='mtconnect:DEWAS-SR-MOXA-001'},
        @{MachineId='11111111-1111-1111-1111-111111111111';ActivityStreamKey='gg003';Other='preserved'})}}
    $configPath = Join-Path $fixture 'config/edge.production.json'
    Write-JsonFileAtomic $configPath $cfg
    $plan = [pscustomobject]@{OperationId=[Guid]::NewGuid().ToString();ExpectedServer='fixture';ExpectedDatabase='fixture';ApprovalReference='test';ExternalLaunchersSuspendedReference='test';RecoveryExecutable=$tool;RecoveryExecutableSha256=(Get-FileHash $tool).Hash}
    Initialize-MoxaMaintenance $plan $fixture ('a'*40) ([pscustomobject]@{edge=@{pid=1}}) (Join-Path $fixture 'logs')
    Assert-True (Test-Path $script:MoxaMarkerPath) 'Persistent barrier missing before shutdown.'
    $script:CompetingEdge = $true
    $failed=$false
    try { Invoke-MoxaMaintenanceCorrection $plan } catch { $failed=$true }
    Assert-True $failed 'Competing Edge was accepted.'
    Assert-True (Test-Path $script:MoxaMarkerPath) 'Failure removed barrier.'
    $script:CompetingEdge = $false
    Invoke-MoxaMaintenanceCorrection $plan
    $actual = Get-Content -Raw $configPath | ConvertFrom-Json
    Assert-True ($actual.ProductionProcessing.Machines[0].ProductionStartsAt -eq '2026-10-25T02:00:00Z') 'Boundary not activated.'
    Assert-True ($actual.ProductionProcessing.Machines[1].Other -eq 'preserved') 'Other machine configuration changed.'
    Assert-True (Test-Path $script:MoxaMarkerPath) 'Barrier cleared before restart acceptance.'
    # Simulate a native correction failure: config and persistent barrier survive.
    Write-JsonFileAtomic $configPath $cfg
    'param($Operation,$Server,$Database,$Approval,$Evidence); $global:LASTEXITCODE=7' | Set-Content $tool
    $plan.RecoveryExecutableSha256=(Get-FileHash $tool).Hash
    Initialize-MoxaMaintenance $plan $fixture ('a'*40) ([pscustomobject]@{edge=@{pid=1}}) (Join-Path $fixture 'logs')
    $failed=$false
    try { Invoke-MoxaMaintenanceCorrection $plan } catch { $failed=$true }
    Assert-True $failed 'Failed SQL correction was accepted.'
    $actual = Get-Content -Raw $configPath | ConvertFrom-Json
    Assert-True ($null -eq $actual.ProductionProcessing.Machines[0].PSObject.Properties['ProductionStartsAt']) 'Failed SQL correction activated configuration.'
    Assert-True (Test-Path $script:MoxaMarkerPath) 'SQL failure removed barrier.'
    foreach ($name in @('Deploy-FactoryConnect.ps1','Start-FactoryConnectRuntime.ps1','Restart-FactoryConnectEdge.ps1','Restart-FactoryConnectDashboard.ps1','Install-FactoryConnectSystemStartup.ps1')) {
        $path=Join-Path $repo "scripts/deployment/$name"
        $tokens=$null; $errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
        Assert-True ($errors.Count -eq 0) "Parser errors in $name"
        Assert-True ((Get-Content -Raw $path).Contains('moxa-maintenance.intent.json')) "Missing persistent barrier in $name"
    }
    Write-Output 'MOXA maintenance conformance passed (mocked process/SQL boundaries; not production rehearsal).'
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
