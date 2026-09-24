[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$commonPath = Join-Path $repoRoot 'scripts/demo/DemoRuntime.Common.ps1'
$startPath = Join-Path $repoRoot 'scripts/demo/Start-FactoryConnectDemo.ps1'
$resetPath = Join-Path $repoRoot 'scripts/demo/Reset-FactoryConnectDemo.ps1'
$baseline = 'd341d082bd50e2ad516da15b6159001774a0ac42'

. $commonPath

$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Proof {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Body
    )

    try {
        & $Body
        $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Outcome = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Outcome = 'FAIL' })
        throw "$Id $Name failed: $($_.Exception.Message)"
    }
}

function Assert-True {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Body,
        [Parameter(Mandatory = $true)][string]$MessagePattern
    )

    $thrown = $null
    try { & $Body }
    catch { $thrown = $_.Exception }

    if ($null -eq $thrown) { throw 'Expected operation to throw.' }
    if ($thrown.Message -notlike $MessagePattern) {
        throw "Unexpected exception '$($thrown.Message)'; expected '$MessagePattern'."
    }
}

function Get-ScriptAst {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "PowerShell parse failed for '$Path': $($errors[0].Message)"
    }
    return $ast
}

Invoke-Proof 'D01' 'PowerShell sources parse cleanly' {
    [void](Get-ScriptAst -Path $commonPath)
    [void](Get-ScriptAst -Path $startPath)
    [void](Get-ScriptAst -Path $resetPath)
}

Invoke-Proof 'D02' 'Candidate approval is explicit and deployment contract stays frozen' {
    $contract = Get-DemoContract
    Assert-True ($contract.DeploymentContractCommit -ceq '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc') 'Deployment contract drifted.'
    Assert-True ($contract.DatabaseName -ceq 'FactoryConnect_Demo') 'Demo database drifted.'
    Assert-True (-not ($contract.PSObject.Properties.Name -contains 'CandidateId')) 'Candidate ID is still pinned in the contract.'
    $startText = Get-Content -Raw -LiteralPath $startPath
    foreach ($parameter in @('ExpectedCandidateId','ExpectedManifestSha256','ExpectedSourceCommit')) {
        Assert-True ($startText.Contains(('[string]) "Launcher does not require $parameter."
    }
    $commonText = Get-Content -Raw -LiteralPath $commonPath
    Assert-True ($commonText.Contains('-ExpectedSourceCommit $ExpectedSourceCommit')) 'Source approval is not passed to the release verifier.'
    Assert-True ($commonText.Contains('-ExpectedDeploymentContractCommit $script:DemoDeploymentContractCommit')) 'Frozen deployment contract is not passed to the verifier.'
    Assert-True ($commonText.Contains('candidateId = $CandidateId')) 'Runtime state does not record the selected candidate.'
}

Invoke-Proof 'D02A' 'Candidate approval rejects malformed or missing identities' {
    $valid = @{
        ExpectedCandidateId = 'demo-candidate-20260924-01'
        ExpectedManifestSha256 = ('a' * 64)
        ExpectedSourceCommit = ('b' * 40)
    }
    Assert-DemoCandidateApproval @valid
    foreach ($badId in @('', 'demo-candidate-20260924-02/other')) {
        $invalid = $valid.Clone()
        $invalid.ExpectedCandidateId = $badId
        Assert-Throws -MessagePattern '*ExpectedCandidateId*' -Body { Assert-DemoCandidateApproval @invalid }
    }
    $invalid = $valid.Clone()
    $invalid.ExpectedManifestSha256 = 'A' * 64
    Assert-Throws -MessagePattern '*ExpectedManifestSha256*' -Body { Assert-DemoCandidateApproval @invalid }
    $invalid = $valid.Clone()
    $invalid.ExpectedSourceCommit = 'not-a-commit'
    Assert-Throws -MessagePattern '*ExpectedSourceCommit*' -Body { Assert-DemoCandidateApproval @invalid }
}

Invoke-Proof 'D02B' 'Candidate ID and manifest mismatches fail closed' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoCandidate-' + [Guid]::NewGuid().ToString('N'))
    $candidateRoot = Join-Path $proofRoot 'demo-candidate-20260924-01'
    [System.IO.Directory]::CreateDirectory($candidateRoot) | Out-Null
    try {
        Assert-Throws -MessagePattern '*requires approved candidate*' -Body {
            Assert-DemoCandidate -CandidatePath $candidateRoot `
                -ExpectedCandidateId 'demo-candidate-20260924-02' `
                -ExpectedManifestSha256 ('a' * 64) `
                -ExpectedSourceCommit ('b' * 40)
        }
        Assert-Throws -MessagePattern '*does not match approved SHA-256*' -Body {
            Assert-DemoCandidateManifestApproval `
                -ActualManifestSha256 ('c' * 64) `
                -ExpectedManifestSha256 ('a' * 64)
        }
    }
    finally {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D03' 'Exact explicit FactoryConnect_Demo database is admitted' {
    $target = Resolve-DemoDatabaseTarget -ConnectionString 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_Demo;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    Assert-True ($target.DatabaseName -ceq 'FactoryConnect_Demo') 'Exact demo database was not preserved.'
}

Invoke-Proof 'D04' 'Missing/default database is rejected' {
    Assert-Throws -MessagePattern '*explicitly name database*' -Body {
        Resolve-DemoDatabaseTarget -ConnectionString 'Server=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    }
}

Invoke-Proof 'D05' 'master and alternate databases are rejected' {
    foreach ($name in @('master','FactoryConnect','factoryconnect_demo')) {
        Assert-Throws -MessagePattern '*expected exactly*' -Body {
            Resolve-DemoDatabaseTarget -ConnectionString "Server=(localdb)\MSSQLLocalDB;Database=$name;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
        }
    }
}

Invoke-Proof 'D06' 'Reset refuses while supervisor lease is held' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoLease-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null
    $supervisor = $null
    try {
        $supervisor = Open-DemoSupervisorLease -RepoRoot $proofRoot -Purpose Supervisor
        Assert-Throws -MessagePattern '*reset refused because the demo supervisor is running*' -Body {
            Open-DemoSupervisorLease -RepoRoot $proofRoot -Purpose Reset
        }
    }
    finally {
        if ($null -ne $supervisor) { $supervisor.Dispose() }
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D07' 'Reset script has no database-name escape hatch' {
    $resetText = Get-Content -Raw -LiteralPath $resetPath
    Assert-True (-not ($resetText -match '\[string\]\$DatabaseName')) 'Reset exposes a DatabaseName parameter.'
    Assert-True ($resetText -match 'Resolve-DemoDatabaseTarget') 'Reset does not use exact database admission.'
    Assert-True ($resetText -match 'Open-DemoSupervisorLease.+Purpose Reset') 'Reset does not acquire the shared exclusive lease.'
}

Invoke-Proof 'D08' 'Startup order is candidate DB fixture migrations Edge API Dashboard readiness' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $markers = @(
        'Assert-DemoCandidate',
        'Ensure-DemoDatabaseExists',
        "-Role 'Fixture'",
        "-Role 'Migrations'",
        "-Role 'Edge'",
        "-Role 'Api'",
        "-Role 'Dashboard'",
        '/health/ready',
        'FactoryConnect demo is ready.'
    )
    $cursor = -1
    foreach ($marker in $markers) {
        $next = $text.IndexOf($marker, $cursor + 1, [System.StringComparison]::Ordinal)
        if ($next -lt 0) { throw "Missing startup marker '$marker'." }
        if ($next -le $cursor) { throw "Startup marker '$marker' is out of order." }
        $cursor = $next
    }
}

Invoke-Proof 'D09' 'Seven-machine R5 stream projection is reused without mappings' {
    $commonText = Get-Content -Raw -LiteralPath $commonPath
    Assert-True ($commonText -match 'ObservationProcessing__Streams__') 'Observation processing stream projection is missing.'
    Assert-True ($commonText -match 'StreamKey') 'Canonical stream key projection is missing.'
    Assert-True (-not ($commonText -match 'Mappings__')) 'FC-034.1 introduced mapping configuration.'
}

Invoke-Proof 'D10' 'Supervisor remains foreground and continuously checks owned services' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $whileMarker = 'while ($true)'
    $whileIndex = $text.IndexOf($whileMarker, [System.StringComparison]::Ordinal)
    Assert-True ($whileIndex -ge 0) 'Foreground supervisor loop is missing.'
    $loopText = $text.Substring($whileIndex)
    Assert-True ($loopText.IndexOf('foreach ($owned in @($fixture, $edge, $api, $dashboard))', [System.StringComparison]::Ordinal) -ge 0) 'Foreground supervisor loop does not enumerate fixture, edge, api, and dashboard.'
    Assert-True ($loopText.IndexOf('Assert-DemoOwnedProcessAlive -OwnedProcess $owned', [System.StringComparison]::Ordinal) -ge 0) 'Foreground supervisor loop does not liveness-check each owned service.'
}

Invoke-Proof 'D11' 'Startup failure is wired to cleanup every owned process' {
    $ast = Get-ScriptAst -Path $startPath
    $finallyBlocks = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally }, $true))
    Assert-True ($finallyBlocks.Count -ge 1) 'Supervisor has no top-level finally cleanup.'
    $startText = Get-Content -Raw -LiteralPath $startPath
    Assert-True ($startText -match 'finally\s*\{[\s\S]*Stop-DemoOwnedProcesses') 'Startup failure cannot be proven to route through shared cleanup.'

    $script:cleanupRoles = [System.Collections.Generic.List[string]]::new()
    $original = ${function:Close-DemoOwnedProcess}
    try {
        ${function:Close-DemoOwnedProcess} = {
            param($OwnedProcess)
            $script:cleanupRoles.Add([string]$OwnedProcess.Role)
        }
        $owned = [System.Collections.Generic.List[object]]::new()
        foreach ($role in @('Fixture','Edge','Api','Dashboard')) { $owned.Add([pscustomobject]@{ Role = $role }) }
        try { throw 'synthetic startup failure' }
        finally { Stop-DemoOwnedProcesses -OwnedProcesses $owned }
    }
    catch {
        if ($_.Exception.Message -ne 'synthetic startup failure') { throw }
    }
    finally {
        ${function:Close-DemoOwnedProcess} = $original
    }

    Assert-True (($script:cleanupRoles -join ',') -ceq 'Dashboard,Api,Edge,Fixture') 'Startup-failure cleanup did not cover every owned process in reverse ownership order.'
}

Invoke-Proof 'D12' 'Ctrl+C pipeline-stop semantics route through cleanup every owned process' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoPipelineStop-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null
    $proofScript = Join-Path $proofRoot 'pipeline-stop-proof.ps1'
    $evidencePath = Join-Path $proofRoot 'cleanup-roles.txt'
    $childPowerShell = (Get-Process -Id $PID).Path
    $proofText = @'
param(
    [Parameter(Mandatory = $true)][string]$CommonPath,
    [Parameter(Mandatory = $true)][string]$EvidencePath
)
$ErrorActionPreference = 'Stop'
. $CommonPath
function Close-DemoOwnedProcess {
    param($OwnedProcess)
    Add-Content -LiteralPath $EvidencePath -Value ([string]$OwnedProcess.Role) -Encoding UTF8
}
$owned = [System.Collections.Generic.List[object]]::new()
foreach ($role in @('Fixture','Edge','Api','Dashboard')) {
    $owned.Add([pscustomobject]@{ Role = $role })
}
try {
    throw [System.Management.Automation.PipelineStoppedException]::new()
}
finally {
    Stop-DemoOwnedProcesses -OwnedProcesses $owned
}
'@

    try {
        Set-Content -LiteralPath $proofScript -Value $proofText -Encoding UTF8
        & $childPowerShell -NoProfile -ExecutionPolicy Bypass -File $proofScript -CommonPath $commonPath -EvidencePath $evidencePath *> $null
        Assert-True (Test-Path -LiteralPath $evidencePath) 'Pipeline-stop child did not execute cleanup evidence.'
        $cleanupRoles = @((Get-Content -LiteralPath $evidencePath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert-True (($cleanupRoles -join ',') -ceq 'Dashboard,Api,Edge,Fixture') 'Pipeline-stop cleanup did not cover every owned process in reverse ownership order.'
    }
    finally {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D12A' 'Edge projects CurrentState freshness' {
    $text = Get-Content -Raw -LiteralPath $startPath
    Assert-True ($text -match "CurrentState__Freshness__MaximumCurrentAge\s*=\s*'00:00:30'") 'Edge freshness setting is absent or incorrect.'
}

Invoke-Proof 'D12B' 'Schedules cover every configured production line and shift' {
    $configuration = [pscustomobject]@{
        siteId = 'SITE-1'
        timeZoneId = 'India Standard Time'
        machines = @(
            [pscustomobject]@{ machineId = 'M1'; streamIdentity = 'mtconnect:CNC-01'; productionLineId = 'LINE-1' },
            [pscustomobject]@{ machineId = 'M2'; streamIdentity = 'mtconnect:CNC-06'; productionLineId = 'LINE-2' },
            [pscustomobject]@{ machineId = 'M3'; streamIdentity = 'mtconnect:CNC-07'; productionLineId = 'LINE-2' }
        )
        shifts = @(
            [pscustomobject]@{ shiftId = 'SHIFT-1'; startsAtLocal = '06:00:00'; endsAtLocal = '14:00:00' },
            [pscustomobject]@{ shiftId = 'SHIFT-2'; startsAtLocal = '14:00:00'; endsAtLocal = '22:00:00' },
            [pscustomobject]@{ shiftId = 'SHIFT-3'; startsAtLocal = '22:00:00'; endsAtLocal = '06:00:00' }
        )
    }
    $environment = @{}
    Add-DemoProductionEnvironment -Environment $environment -Configuration $configuration

    $actual = @{}
    for ($index = 0; $index -lt 6; $index++) {
        $prefix = "ProductionProcessing__ShiftSchedules__${index}"
        $line = [string]$environment["${prefix}__ProductionLineId"]
        $shift = [string]$environment["${prefix}__ShiftId"]
        $assignment = [string]$environment["${prefix}__AssignmentId"]
        Assert-True (-not [string]::IsNullOrWhiteSpace($line)) "Schedule $index has no production line."
        Assert-True ($assignment -ceq "SHIFT-SCHEDULE-$line-$shift") "Schedule $index assignment is incorrect."
        Assert-True ($environment["${prefix}__ActiveDays__6"] -ceq 'Saturday') "Schedule $index omits an active day."
        $actual["$line/$shift"] = $true
    }
    Assert-True (-not $environment.ContainsKey('ProductionProcessing__ShiftSchedules__6__ShiftId')) 'Unexpected seventh schedule.'
    Assert-True ($actual.Count -eq 6) 'Schedule line/shift pairs are duplicated.'
    foreach ($line in @('LINE-1','LINE-2')) {
        foreach ($shift in @('SHIFT-1','SHIFT-2','SHIFT-3')) {
            Assert-True ($actual.ContainsKey("$line/$shift")) "Missing schedule $line/$shift."
        }
    }
}

Invoke-Proof 'D12C' 'Local Dashboard uses Development with explicit demo API and sources' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $dashboardMarker = '$dashboardEnvironment = @{'
    $dashboardStart = $text.IndexOf($dashboardMarker, [System.StringComparison]::Ordinal)
    Assert-True ($dashboardStart -ge 0) 'Dashboard child environment is missing.'
    $dashboardEnd = $text.IndexOf("    }", $dashboardStart, [System.StringComparison]::Ordinal)
    Assert-True ($dashboardEnd -gt $dashboardStart) 'Dashboard child environment does not close.'
    $dashboardBlock = $text.Substring($dashboardStart, $dashboardEnd - $dashboardStart)
    Assert-True ($dashboardBlock.Contains("ASPNETCORE_ENVIRONMENT = 'Development'")) 'Local Dashboard is not in Development.'
    Assert-True ($dashboardBlock.Contains('Dashboard__ReportingApiBaseAddress = $apiBaseAddress')) 'Demo API base address is not projected.'
    Assert-True ($text.Contains('$dashboardEnvironment["${prefix}__MachineId"]')) 'Demo machine sources are not projected.'
    Assert-True ($text.Contains('$dashboardEnvironment["${prefix}__ProductionLineId"]')) 'Demo line sources are not projected.'
}

Invoke-Proof 'D13' 'Reset drops/recreates only demo DB and leaves migration authority to next start' {
    $text = Get-Content -Raw -LiteralPath $resetPath
    Assert-True ($text -match 'DROP DATABASE') 'Reset does not drop the demo database.'
    Assert-True ($text -match 'CREATE DATABASE') 'Reset does not recreate the demo database.'
    Assert-True (-not ($text -match 'FactoryConnect\.Migrations\.exe')) 'Reset incorrectly runs migrations.'
}

Invoke-Proof 'D14' 'Candidate rehearsal evidence and workspaces are not deletion targets' {
    $text = (Get-Content -Raw -LiteralPath $startPath) + "`n" + (Get-Content -Raw -LiteralPath $resetPath)
    Assert-True (-not ($text -match 'Remove-Item[^\r\n]*(candidate|rehearsal|evidence)')) 'Demo tooling deletes protected candidate/rehearsal/evidence content.'
}

Invoke-Proof 'D15' 'Candidate authority correction changed-path boundary is exact' {
    $git = Get-Command git -ErrorAction Stop
    $paths = @(& $git.Source -C $repoRoot diff --name-only "$baseline...HEAD")
    if ($LASTEXITCODE -ne 0) { throw 'git diff --name-only failed.' }
    $expected = @(
        'scripts/demo/DemoRuntime.Common.ps1',
        'scripts/demo/Start-FactoryConnectDemo.ps1',
        'tests/demo/Test-FactoryConnectDemoRuntime.ps1'
    )
    $actual = @($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object)
    $wanted = @($expected | Sort-Object)
    Assert-True (($actual -join "`n") -ceq ($wanted -join "`n")) ("Unexpected FC-034.1 changed paths: " + ($actual -join ', '))
}

Invoke-Proof 'D16' 'Whitespace/error diff check is clean' {
    $git = Get-Command git -ErrorAction Stop
    & $git.Source -C $repoRoot diff --check "$baseline...HEAD"
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
}

$results | Format-Table -AutoSize
Write-Host ''
Write-Host "FC-034.1 conformance: $($results.Count)/$($results.Count) PASS"
 + $parameter))) "Launcher does not require $parameter."
    }
    $commonText = Get-Content -Raw -LiteralPath $commonPath
    Assert-True ($commonText.Contains('-ExpectedSourceCommit $ExpectedSourceCommit')) 'Source approval is not passed to the release verifier.'
    Assert-True ($commonText.Contains('-ExpectedDeploymentContractCommit $script:DemoDeploymentContractCommit')) 'Frozen deployment contract is not passed to the verifier.'
    Assert-True ($commonText.Contains('candidateId = $CandidateId')) 'Runtime state does not record the selected candidate.'
}

Invoke-Proof 'D02A' 'Candidate approval rejects malformed or missing identities' {
    $valid = @{
        ExpectedCandidateId = 'demo-candidate-20260924-01'
        ExpectedManifestSha256 = ('a' * 64)
        ExpectedSourceCommit = ('b' * 40)
    }
    Assert-DemoCandidateApproval @valid
    foreach ($badId in @('', 'demo-candidate-20260924-02/other')) {
        $invalid = $valid.Clone()
        $invalid.ExpectedCandidateId = $badId
        Assert-Throws -MessagePattern '*ExpectedCandidateId*' -Body { Assert-DemoCandidateApproval @invalid }
    }
    $invalid = $valid.Clone()
    $invalid.ExpectedManifestSha256 = 'A' * 64
    Assert-Throws -MessagePattern '*ExpectedManifestSha256*' -Body { Assert-DemoCandidateApproval @invalid }
    $invalid = $valid.Clone()
    $invalid.ExpectedSourceCommit = 'not-a-commit'
    Assert-Throws -MessagePattern '*ExpectedSourceCommit*' -Body { Assert-DemoCandidateApproval @invalid }
}

Invoke-Proof 'D02B' 'Candidate ID and manifest mismatches fail closed' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoCandidate-' + [Guid]::NewGuid().ToString('N'))
    $candidateRoot = Join-Path $proofRoot 'demo-candidate-20260924-01'
    [System.IO.Directory]::CreateDirectory($candidateRoot) | Out-Null
    try {
        Assert-Throws -MessagePattern '*requires approved candidate*' -Body {
            Assert-DemoCandidate -CandidatePath $candidateRoot `
                -ExpectedCandidateId 'demo-candidate-20260924-02' `
                -ExpectedManifestSha256 ('a' * 64) `
                -ExpectedSourceCommit ('b' * 40)
        }
        Assert-Throws -MessagePattern '*does not match approved SHA-256*' -Body {
            Assert-DemoCandidateManifestApproval `
                -ActualManifestSha256 ('c' * 64) `
                -ExpectedManifestSha256 ('a' * 64)
        }
    }
    finally {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D03' 'Exact explicit FactoryConnect_Demo database is admitted' {
    $target = Resolve-DemoDatabaseTarget -ConnectionString 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_Demo;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    Assert-True ($target.DatabaseName -ceq 'FactoryConnect_Demo') 'Exact demo database was not preserved.'
}

Invoke-Proof 'D04' 'Missing/default database is rejected' {
    Assert-Throws -MessagePattern '*explicitly name database*' -Body {
        Resolve-DemoDatabaseTarget -ConnectionString 'Server=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    }
}

Invoke-Proof 'D05' 'master and alternate databases are rejected' {
    foreach ($name in @('master','FactoryConnect','factoryconnect_demo')) {
        Assert-Throws -MessagePattern '*expected exactly*' -Body {
            Resolve-DemoDatabaseTarget -ConnectionString "Server=(localdb)\MSSQLLocalDB;Database=$name;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
        }
    }
}

Invoke-Proof 'D06' 'Reset refuses while supervisor lease is held' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoLease-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null
    $supervisor = $null
    try {
        $supervisor = Open-DemoSupervisorLease -RepoRoot $proofRoot -Purpose Supervisor
        Assert-Throws -MessagePattern '*reset refused because the demo supervisor is running*' -Body {
            Open-DemoSupervisorLease -RepoRoot $proofRoot -Purpose Reset
        }
    }
    finally {
        if ($null -ne $supervisor) { $supervisor.Dispose() }
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D07' 'Reset script has no database-name escape hatch' {
    $resetText = Get-Content -Raw -LiteralPath $resetPath
    Assert-True (-not ($resetText -match '\[string\]\$DatabaseName')) 'Reset exposes a DatabaseName parameter.'
    Assert-True ($resetText -match 'Resolve-DemoDatabaseTarget') 'Reset does not use exact database admission.'
    Assert-True ($resetText -match 'Open-DemoSupervisorLease.+Purpose Reset') 'Reset does not acquire the shared exclusive lease.'
}

Invoke-Proof 'D08' 'Startup order is candidate DB fixture migrations Edge API Dashboard readiness' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $markers = @(
        'Assert-DemoCandidate',
        'Ensure-DemoDatabaseExists',
        "-Role 'Fixture'",
        "-Role 'Migrations'",
        "-Role 'Edge'",
        "-Role 'Api'",
        "-Role 'Dashboard'",
        '/health/ready',
        'FactoryConnect demo is ready.'
    )
    $cursor = -1
    foreach ($marker in $markers) {
        $next = $text.IndexOf($marker, $cursor + 1, [System.StringComparison]::Ordinal)
        if ($next -lt 0) { throw "Missing startup marker '$marker'." }
        if ($next -le $cursor) { throw "Startup marker '$marker' is out of order." }
        $cursor = $next
    }
}

Invoke-Proof 'D09' 'Seven-machine R5 stream projection is reused without mappings' {
    $commonText = Get-Content -Raw -LiteralPath $commonPath
    Assert-True ($commonText -match 'ObservationProcessing__Streams__') 'Observation processing stream projection is missing.'
    Assert-True ($commonText -match 'StreamKey') 'Canonical stream key projection is missing.'
    Assert-True (-not ($commonText -match 'Mappings__')) 'FC-034.1 introduced mapping configuration.'
}

Invoke-Proof 'D10' 'Supervisor remains foreground and continuously checks owned services' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $whileMarker = 'while ($true)'
    $whileIndex = $text.IndexOf($whileMarker, [System.StringComparison]::Ordinal)
    Assert-True ($whileIndex -ge 0) 'Foreground supervisor loop is missing.'
    $loopText = $text.Substring($whileIndex)
    Assert-True ($loopText.IndexOf('foreach ($owned in @($fixture, $edge, $api, $dashboard))', [System.StringComparison]::Ordinal) -ge 0) 'Foreground supervisor loop does not enumerate fixture, edge, api, and dashboard.'
    Assert-True ($loopText.IndexOf('Assert-DemoOwnedProcessAlive -OwnedProcess $owned', [System.StringComparison]::Ordinal) -ge 0) 'Foreground supervisor loop does not liveness-check each owned service.'
}

Invoke-Proof 'D11' 'Startup failure is wired to cleanup every owned process' {
    $ast = Get-ScriptAst -Path $startPath
    $finallyBlocks = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally }, $true))
    Assert-True ($finallyBlocks.Count -ge 1) 'Supervisor has no top-level finally cleanup.'
    $startText = Get-Content -Raw -LiteralPath $startPath
    Assert-True ($startText -match 'finally\s*\{[\s\S]*Stop-DemoOwnedProcesses') 'Startup failure cannot be proven to route through shared cleanup.'

    $script:cleanupRoles = [System.Collections.Generic.List[string]]::new()
    $original = ${function:Close-DemoOwnedProcess}
    try {
        ${function:Close-DemoOwnedProcess} = {
            param($OwnedProcess)
            $script:cleanupRoles.Add([string]$OwnedProcess.Role)
        }
        $owned = [System.Collections.Generic.List[object]]::new()
        foreach ($role in @('Fixture','Edge','Api','Dashboard')) { $owned.Add([pscustomobject]@{ Role = $role }) }
        try { throw 'synthetic startup failure' }
        finally { Stop-DemoOwnedProcesses -OwnedProcesses $owned }
    }
    catch {
        if ($_.Exception.Message -ne 'synthetic startup failure') { throw }
    }
    finally {
        ${function:Close-DemoOwnedProcess} = $original
    }

    Assert-True (($script:cleanupRoles -join ',') -ceq 'Dashboard,Api,Edge,Fixture') 'Startup-failure cleanup did not cover every owned process in reverse ownership order.'
}

Invoke-Proof 'D12' 'Ctrl+C pipeline-stop semantics route through cleanup every owned process' {
    $proofRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FactoryConnect-DemoPipelineStop-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($proofRoot) | Out-Null
    $proofScript = Join-Path $proofRoot 'pipeline-stop-proof.ps1'
    $evidencePath = Join-Path $proofRoot 'cleanup-roles.txt'
    $childPowerShell = (Get-Process -Id $PID).Path
    $proofText = @'
param(
    [Parameter(Mandatory = $true)][string]$CommonPath,
    [Parameter(Mandatory = $true)][string]$EvidencePath
)
$ErrorActionPreference = 'Stop'
. $CommonPath
function Close-DemoOwnedProcess {
    param($OwnedProcess)
    Add-Content -LiteralPath $EvidencePath -Value ([string]$OwnedProcess.Role) -Encoding UTF8
}
$owned = [System.Collections.Generic.List[object]]::new()
foreach ($role in @('Fixture','Edge','Api','Dashboard')) {
    $owned.Add([pscustomobject]@{ Role = $role })
}
try {
    throw [System.Management.Automation.PipelineStoppedException]::new()
}
finally {
    Stop-DemoOwnedProcesses -OwnedProcesses $owned
}
'@

    try {
        Set-Content -LiteralPath $proofScript -Value $proofText -Encoding UTF8
        & $childPowerShell -NoProfile -ExecutionPolicy Bypass -File $proofScript -CommonPath $commonPath -EvidencePath $evidencePath *> $null
        Assert-True (Test-Path -LiteralPath $evidencePath) 'Pipeline-stop child did not execute cleanup evidence.'
        $cleanupRoles = @((Get-Content -LiteralPath $evidencePath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert-True (($cleanupRoles -join ',') -ceq 'Dashboard,Api,Edge,Fixture') 'Pipeline-stop cleanup did not cover every owned process in reverse ownership order.'
    }
    finally {
        Remove-Item -LiteralPath $proofRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Proof 'D12A' 'Edge projects CurrentState freshness' {
    $text = Get-Content -Raw -LiteralPath $startPath
    Assert-True ($text -match "CurrentState__Freshness__MaximumCurrentAge\s*=\s*'00:00:30'") 'Edge freshness setting is absent or incorrect.'
}

Invoke-Proof 'D12B' 'Schedules cover every configured production line and shift' {
    $configuration = [pscustomobject]@{
        siteId = 'SITE-1'
        timeZoneId = 'India Standard Time'
        machines = @(
            [pscustomobject]@{ machineId = 'M1'; streamIdentity = 'mtconnect:CNC-01'; productionLineId = 'LINE-1' },
            [pscustomobject]@{ machineId = 'M2'; streamIdentity = 'mtconnect:CNC-06'; productionLineId = 'LINE-2' },
            [pscustomobject]@{ machineId = 'M3'; streamIdentity = 'mtconnect:CNC-07'; productionLineId = 'LINE-2' }
        )
        shifts = @(
            [pscustomobject]@{ shiftId = 'SHIFT-1'; startsAtLocal = '06:00:00'; endsAtLocal = '14:00:00' },
            [pscustomobject]@{ shiftId = 'SHIFT-2'; startsAtLocal = '14:00:00'; endsAtLocal = '22:00:00' },
            [pscustomobject]@{ shiftId = 'SHIFT-3'; startsAtLocal = '22:00:00'; endsAtLocal = '06:00:00' }
        )
    }
    $environment = @{}
    Add-DemoProductionEnvironment -Environment $environment -Configuration $configuration

    $actual = @{}
    for ($index = 0; $index -lt 6; $index++) {
        $prefix = "ProductionProcessing__ShiftSchedules__${index}"
        $line = [string]$environment["${prefix}__ProductionLineId"]
        $shift = [string]$environment["${prefix}__ShiftId"]
        $assignment = [string]$environment["${prefix}__AssignmentId"]
        Assert-True (-not [string]::IsNullOrWhiteSpace($line)) "Schedule $index has no production line."
        Assert-True ($assignment -ceq "SHIFT-SCHEDULE-$line-$shift") "Schedule $index assignment is incorrect."
        Assert-True ($environment["${prefix}__ActiveDays__6"] -ceq 'Saturday') "Schedule $index omits an active day."
        $actual["$line/$shift"] = $true
    }
    Assert-True (-not $environment.ContainsKey('ProductionProcessing__ShiftSchedules__6__ShiftId')) 'Unexpected seventh schedule.'
    Assert-True ($actual.Count -eq 6) 'Schedule line/shift pairs are duplicated.'
    foreach ($line in @('LINE-1','LINE-2')) {
        foreach ($shift in @('SHIFT-1','SHIFT-2','SHIFT-3')) {
            Assert-True ($actual.ContainsKey("$line/$shift")) "Missing schedule $line/$shift."
        }
    }
}

Invoke-Proof 'D12C' 'Local Dashboard uses Development with explicit demo API and sources' {
    $text = Get-Content -Raw -LiteralPath $startPath
    $dashboardMarker = '$dashboardEnvironment = @{'
    $dashboardStart = $text.IndexOf($dashboardMarker, [System.StringComparison]::Ordinal)
    Assert-True ($dashboardStart -ge 0) 'Dashboard child environment is missing.'
    $dashboardEnd = $text.IndexOf("    }", $dashboardStart, [System.StringComparison]::Ordinal)
    Assert-True ($dashboardEnd -gt $dashboardStart) 'Dashboard child environment does not close.'
    $dashboardBlock = $text.Substring($dashboardStart, $dashboardEnd - $dashboardStart)
    Assert-True ($dashboardBlock.Contains("ASPNETCORE_ENVIRONMENT = 'Development'")) 'Local Dashboard is not in Development.'
    Assert-True ($dashboardBlock.Contains('Dashboard__ReportingApiBaseAddress = $apiBaseAddress')) 'Demo API base address is not projected.'
    Assert-True ($text.Contains('$dashboardEnvironment["${prefix}__MachineId"]')) 'Demo machine sources are not projected.'
    Assert-True ($text.Contains('$dashboardEnvironment["${prefix}__ProductionLineId"]')) 'Demo line sources are not projected.'
}

Invoke-Proof 'D13' 'Reset drops/recreates only demo DB and leaves migration authority to next start' {
    $text = Get-Content -Raw -LiteralPath $resetPath
    Assert-True ($text -match 'DROP DATABASE') 'Reset does not drop the demo database.'
    Assert-True ($text -match 'CREATE DATABASE') 'Reset does not recreate the demo database.'
    Assert-True (-not ($text -match 'FactoryConnect\.Migrations\.exe')) 'Reset incorrectly runs migrations.'
}

Invoke-Proof 'D14' 'Candidate rehearsal evidence and workspaces are not deletion targets' {
    $text = (Get-Content -Raw -LiteralPath $startPath) + "`n" + (Get-Content -Raw -LiteralPath $resetPath)
    Assert-True (-not ($text -match 'Remove-Item[^\r\n]*(candidate|rehearsal|evidence)')) 'Demo tooling deletes protected candidate/rehearsal/evidence content.'
}

Invoke-Proof 'D15' 'Candidate authority correction changed-path boundary is exact' {
    $git = Get-Command git -ErrorAction Stop
    $paths = @(& $git.Source -C $repoRoot diff --name-only "$baseline...HEAD")
    if ($LASTEXITCODE -ne 0) { throw 'git diff --name-only failed.' }
    $expected = @(
        'scripts/demo/DemoRuntime.Common.ps1',
        'scripts/demo/Start-FactoryConnectDemo.ps1',
        'tests/demo/Test-FactoryConnectDemoRuntime.ps1'
    )
    $actual = @($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object)
    $wanted = @($expected | Sort-Object)
    Assert-True (($actual -join "`n") -ceq ($wanted -join "`n")) ("Unexpected FC-034.1 changed paths: " + ($actual -join ', '))
}

Invoke-Proof 'D16' 'Whitespace/error diff check is clean' {
    $git = Get-Command git -ErrorAction Stop
    & $git.Source -C $repoRoot diff --check "$baseline...HEAD"
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
}

$results | Format-Table -AutoSize
Write-Host ''
Write-Host "FC-034.1 conformance: $($results.Count)/$($results.Count) PASS"
