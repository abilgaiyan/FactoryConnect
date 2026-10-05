[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'System startup installation requires Windows.' }
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$deployment = Join-Path $root 'deployment'
$lock = $null
$temp = $null
$backup = $null
try {
    # This operation only publishes the stable orchestration entry point.
    # It does not create a task, start acquisition/runtime, or change current.
    $lock = [IO.File]::Open((Join-Path $deployment 'deployment.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if (Test-Path -LiteralPath (Join-Path $deployment 'runtime-start.intent.json')) { throw 'Unresolved runtime startup intent blocks system-entry installation.' }
    $item = Get-Item -LiteralPath (Join-Path $root 'current') -Force
    $targets = @($item.Target)
    if ($item.LinkType -cne 'Junction' -or $targets.Count -ne 1) { throw 'current must be one selected-release junction.' }
    $release = [IO.Path]::GetFullPath([string]$targets[0]).TrimEnd('\')
    $prefix = [IO.Path]::GetFullPath((Join-Path $root 'releases')).TrimEnd('\') + '\'
    if (-not $release.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $release.Substring($prefix.Length) -cnotmatch '^[0-9a-f]{40}$') { throw 'current release identity is invalid.' }
    $name = 'Start-FactoryConnectSystem.ps1'
    $source = Join-Path $release $name
    $metadata = Get-Content (Join-Path $release 'release.json') -Raw | ConvertFrom-Json
    if ($metadata.systemStartupScript -cne $name -or $metadata.sourceCommit -cne $release.Substring($prefix.Length)) { throw 'Selected release does not declare the system startup authority.' }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $entries = @(Get-Content (Join-Path $release 'MANIFEST.sha256') | Where-Object { $_ -cmatch '^[0-9a-f]{64}  Start-FactoryConnectSystem\.ps1$' })
    if ($entries.Count -ne 1 -or $entries[0].Substring(0,64) -cne $hash) { throw 'Packaged system startup authority failed manifest verification.' }
    $destination = Join-Path $root $name
    if (Test-Path -LiteralPath $destination) {
        $existing = Get-Item -LiteralPath $destination -Force
        if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Stable system entry must not be a reparse point.' }
    }
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ceq $hash) {
        [pscustomobject]@{Status='AlreadyInstalled';SourceCommit=$metadata.sourceCommit;Sha256=$hash;Path=$destination}
        return
    }
    $temp = $destination + '.tmp.' + [Guid]::NewGuid().ToString('N')
    $backup = $temp + '.backup'
    [IO.File]::Copy($source,$temp,$false)
    if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temp,$destination,$backup,$true) }
    else { [IO.File]::Move($temp,$destination) }
    [pscustomobject]@{Status='Installed';SourceCommit=$metadata.sourceCommit;Sha256=$hash;Path=$destination}
} finally {
    foreach ($path in @($temp,$backup)) { if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force } }
    if ($null -ne $lock) { $lock.Dispose() }
}
