[CmdletBinding()]
param([string]$DeploymentScript)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $DeploymentScript) { $DeploymentScript = Join-Path $PSScriptRoot '../../scripts/deployment/Deploy-FactoryConnect.ps1' }
$DeploymentScript = [IO.Path]::GetFullPath($DeploymentScript)
if ($env:OS -ne 'Windows_NT') { throw 'RBS-07 process/junction proof requires Windows PowerShell 5.1.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('FactoryConnect-RBS07-' + [Guid]::NewGuid().ToString('N'))
$child = $null
$childPath = $null; $childStart = $null
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Read-SelectionTarget([string]$Path) {
    # Windows PowerShell exposes junction Target as string[], even for one target.
    $targets = @((Get-Item -LiteralPath $Path).Target)
    if ($targets.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$targets[0])) {
        throw 'Expected exactly one current junction target.'
    }
    return [string]$targets[0]
}
function Snapshot([string]$Path) {
    $items = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Path.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    return ($items -join "`n")
}
try {
    foreach ($part in @('deployment','config','releases/1111111111111111111111111111111111111111','package')) {
        New-Item -ItemType Directory -Force (Join-Path $root $part) | Out-Null
    }
    $release = Join-Path $root 'releases/1111111111111111111111111111111111111111'
    Set-Content (Join-Path $release 'release-sentinel.txt') 'commissioned release'
    foreach ($role in @('edge','api','dashboard')) { Set-Content (Join-Path $root "config/$role.production.json") '{"commissioned":"preserve"}' }
    Set-Content (Join-Path $root 'package/package-sentinel.txt') 'incoming package must not be inspected or installed'
    $current = Join-Path $root 'current'
    $windowsHost = $env:OS -eq 'Windows_NT'
    if ($windowsHost) { New-Item -ItemType Junction -Path $current -Target $release | Out-Null }
    else { New-Item -ItemType SymbolicLink -Path $current -Target $release | Out-Null }
    $selection = Read-SelectionTarget $current
    # Reproduce launch-before-ownership-publication: a live child is deliberately
    # absent from runtime.json. Intent is the only durable launch warning.
    $shell = if ($windowsHost) { Join-Path $PSHOME 'powershell.exe' } else { Join-Path $PSHOME 'pwsh' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $fixture = Join-Path $release 'UnrecordedLaunch.exe'
    Add-Type -OutputAssembly $fixture -OutputType WindowsApplication -TypeDefinition @'
public static class UnrecordedLaunch {
    public static void Main() { System.Threading.Thread.Sleep(120000); }
}
'@
    $info.FileName = $fixture
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($info)
    $identity = Get-Process -Id $child.Id
    $childPath = $identity.Path; $childStart = $identity.StartTime.ToUniversalTime()
    $runtime = Join-Path $root 'deployment/runtime.json'
    Set-Content $runtime '{"edge":null,"api":null,"dashboard":null,"deploymentAttemptId":"prior"}'
    $intent = Join-Path $root 'deployment/runtime-start.intent.json'
    # Opaque content additionally proves deployment does not interpret intent.
    [IO.File]::WriteAllBytes($intent,[Text.Encoding]::UTF8.GetBytes('{"attemptId":"crashed","launchPending":"edge"}'))
    $beforeIntent = (Get-FileHash $intent).Hash; $beforeRuntime = (Get-FileHash $runtime).Hash
    $beforeConfig = Snapshot (Join-Path $root 'config')
    $beforeRelease = Snapshot (Join-Path $root 'releases')
    $beforePackage = Snapshot (Join-Path $root 'package')
    $out = Join-Path $root 'deployment.stdout'; $err = Join-Path $root 'deployment.stderr'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $shell
    $info.Arguments = '-NoProfile -File "{0}" -PackagePath "{1}" -InstallRoot "{2}"' -f $DeploymentScript,(Join-Path $root 'package'),$root
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $deploy = [Diagnostics.Process]::Start($info)
    $stdoutTask = $deploy.StandardOutput.ReadToEndAsync(); $stderrTask = $deploy.StandardError.ReadToEndAsync()
    try {
        if (-not $deploy.WaitForExit(30000)) { $deploy.Kill(); throw 'Deployment exceeded RBS-07 deadline.' }
        $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult()
        Set-Content $out $stdout; Set-Content $err $stderr
        Assert-True ($deploy.ExitCode -ne 0) 'Deployment did not reject unresolved intent.'
        Assert-True ($stderr -match 'Unresolved runtime startup intent exists') "Wrong rejection: $stderr"
    } finally { $deploy.Dispose() }
    Assert-True ((Get-FileHash $intent).Hash -eq $beforeIntent) 'Intent changed.'
    Assert-True ((Get-FileHash $runtime).Hash -eq $beforeRuntime) 'Runtime evidence changed.'
    Assert-True ((Snapshot (Join-Path $root 'config')) -ceq $beforeConfig) 'Configuration changed.'
    Assert-True ((Snapshot (Join-Path $root 'releases')) -ceq $beforeRelease) 'Release contents changed.'
    Assert-True ((Snapshot (Join-Path $root 'package')) -ceq $beforePackage) 'Package changed.'
    Assert-True ((Read-SelectionTarget $current) -ceq $selection) 'Current selection changed.'
    Assert-True (@(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($release,[StringComparison]::OrdinalIgnoreCase) }).Count -eq 1) 'Unexpected runtime process launched.'
    $after = Get-Process -Id $child.Id
    Assert-True ($after.Path -eq $childPath -and $after.StartTime.ToUniversalTime() -eq $childStart) 'Unrecorded launch was stopped/replaced.'
    $failures = @(Get-ChildItem (Join-Path $root 'deployment/logs') -Recurse -Filter deployment-failure.json)
    Assert-True ($failures.Count -eq 1) 'Missing unique rejection evidence.'
    $failure = Get-Content $failures[0].FullName -Raw | ConvertFrom-Json
    Assert-True ($failure.failurePhase -eq 'UnresolvedStartupIntent') 'Guard was not first preflight decision.'
    Assert-True ($failure.migrationOutcome -eq 'NotStarted' -and -not $failure.databaseMayHaveChanged -and -not $failure.runtimeStateUpdated) 'Database/runtime mutation boundary crossed.'
    Assert-True (@(Get-ChildItem (Join-Path $root 'deployment/logs') -Recurse -Directory | Where-Object Name -eq 'package').Count -eq 0) 'Package extraction occurred.'
    # Ensure lock was released even on rejection.
    $lock = [IO.File]::Open((Join-Path $root 'deployment/deployment.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $lock.Dispose()
    [pscustomobject]@{RBS07='PASS';DatabaseProof='Migration not entered; no SQL database used'}
} finally {
    if ($null -ne $child) {
        $live = Get-Process -Id $child.Id -ErrorAction SilentlyContinue
        if ($live -and $null -ne $childStart -and $live.Path -eq $childPath -and $live.StartTime.ToUniversalTime() -eq $childStart) { $child.Kill(); [void]$child.WaitForExit(5000) }
        $child.Dispose()
    }
    $link = Join-Path $root 'current'
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'Cleanup refused: current is no longer a junction/link.'
        }
        # Delete only the junction itself. Remove-Item in Windows PowerShell 5.1
        # can prompt about children; recursive removal risks following the target.
        [IO.Directory]::Delete($link)
    }
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
