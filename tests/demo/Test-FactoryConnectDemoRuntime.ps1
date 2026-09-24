[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$commonPath = Join-Path $repoRoot 'scripts/demo/DemoRuntime.Common.ps1'
$startPath = Join-Path $repoRoot 'scripts/demo/Start-FactoryConnectDemo.ps1'
$resetPath = Join-Path $repoRoot 'scripts/demo/Reset-FactoryConnectDemo.ps1'
$baseline = '37d342e84d3ea79b6282c898d5425d9ee2e8867f'

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

Invoke-Proof 'D02' 'Frozen candidate identity is exact' {
    $contract = Get-DemoContract
    Assert-True ($contract.CandidateId -ceq 'demo-candidate-20260923-02') 'Candidate ID drifted.'
    Assert-True ($contract.CandidateManifestSha256 -ceq '88d5fdb73c04f916692c1819f9adffe38d838c071f5b1f34a8f94ead018dcb76') 'Candidate manifest drifted.'
    Assert-True ($contract.ApplicationSourceCommit -ceq '6bdb89d87d013c177297a7e2d029fed63a2e2b60') 'Application source commit drifted.'
    Assert-True ($contract.DeploymentContractCommit -ceq '9ff9c4d6abaa696b8daf25eff6a724bacd0ba5fc') 'Deployment contract commit drifted.'
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
    $script:cleanupRoles = [System.Collections.Generic.List[string]]::new()
    $original = ${function:Close-DemoOwnedProcess}
    try {
        ${function:Close-DemoOwnedProcess} = {
            param($OwnedProcess)
            $script:cleanupRoles.Add([string]$OwnedProcess.Role)
        }
        $owned = [System.Collections.Generic.List[object]]::new()
        foreach ($role in @('Fixture','Edge','Api','Dashboard')) { $owned.Add([pscustomobject]@{ Role = $role }) }
        try {
            throw [System.Management.Automation.PipelineStoppedException]::new()
        }
        finally {
            Stop-DemoOwnedProcesses -OwnedProcesses $owned
        }
    }
    catch [System.Management.Automation.PipelineStoppedException] {
    }
    finally {
        ${function:Close-DemoOwnedProcess} = $original
    }

    Assert-True (($script:cleanupRoles -join ',') -ceq 'Dashboard,Api,Edge,Fixture') 'Pipeline-stop cleanup did not cover every owned process.'
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

Invoke-Proof 'D15' 'FC-034.1 changed-path boundary is exact' {
    $git = Get-Command git -ErrorAction Stop
    $paths = @(& $git.Source -C $repoRoot diff --name-only "$baseline...HEAD")
    if ($LASTEXITCODE -ne 0) { throw 'git diff --name-only failed.' }
    $expected = @(
        'scripts/demo/DemoRuntime.Common.ps1',
        'scripts/demo/Reset-FactoryConnectDemo.ps1',
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
