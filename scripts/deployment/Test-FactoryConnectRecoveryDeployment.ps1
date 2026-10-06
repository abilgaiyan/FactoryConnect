[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\FactoryConnect',
    [Parameter(Mandatory=$true)][string]$ApprovedRelease,
    [Parameter(Mandatory=$true)][string]$ApprovedManifestSha256,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'

function Assert-RecoveryDeploymentCapture {
    param($Capture, [string]$ApprovedRelease, [string]$ApprovedManifestSha256)
    if ($ApprovedRelease -cnotmatch '^[0-9a-f]{40}$' -or $ApprovedManifestSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Approved release/hash format invalid.' }
    if ($Capture.LinkType -cne 'Junction' -or @($Capture.Targets).Count -ne 1) { throw 'Current must be exactly one junction.' }
    $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $Capture.Root 'releases') $ApprovedRelease)).TrimEnd('\','/')
    if ([IO.Path]::GetFullPath([string]$Capture.Targets[0]).TrimEnd('\','/') -ine $expected) { throw 'Junction does not select approved release.' }
    if ($Capture.Release.sourceCommit -cne $ApprovedRelease -or $Capture.Release.schemaVersion -cne '1.0' -or
        $Capture.ManifestHash -ine $ApprovedManifestSha256 -or -not $Capture.PayloadVerified) { throw 'Release provenance/payload does not match reviewed artifact.' }
    $r = $Capture.Runtime
    if ($r.selectedRelease -cne $ApprovedRelease -or [IO.Path]::GetFullPath([string]$r.releasePath).TrimEnd('\','/') -ine $expected -or
        $r.deploymentStatus -cne 'Succeeded' -or $r.runtimeRunning -ne $true) { throw 'Runtime selection is not successful/current.' }
    foreach ($name in @('edge','api')) {
        $record = $r.$name; $live = $Capture.Processes.$name
        $exeName = if ($name -eq 'edge') { 'FactoryConnect.Edge.exe' } else { 'FactoryConnect.Api.exe' }
        $exe = [IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $expected 'apps') $name) $exeName))
        if ($r.processStates.$name -cne 'Owned' -or $null -eq $record -or $null -eq $live -or
            [int]$record.pid -le 0 -or $record.pid -ne $live.pid -or
            [IO.Path]::GetFullPath([string]$record.executablePath) -ine $exe -or
            [IO.Path]::GetFullPath([string]$live.executablePath) -ine $exe -or
            ([DateTimeOffset]$record.startTimeUtc).UtcTicks -ne ([DateTimeOffset]$live.startTimeUtc).UtcTicks) { throw "Exact $name process ownership not established." }
    }
    return [pscustomobject]@{ Status='Verified'; GateBaseline='348e6167f9a8b7e8784732ca8cc8a14559837d08';
        ApprovedRelease=$ApprovedRelease; ApprovedManifestSha256=$ApprovedManifestSha256.ToUpperInvariant();
        ReviewQualification='Approved release and manifest hash must come from independent package/source review retaining P0-C. No ancestry inference.';
        ObservedAtUtc=[DateTime]::UtcNow.ToString('o'); Capture=$Capture }
}
if ($FunctionsOnly) { return }
$root = [IO.Path]::GetFullPath($InstallRoot)
$item = Get-Item -LiteralPath (Join-Path $root 'current') -Force
$targets = @($item.Target)
if ($item.LinkType -cne 'Junction' -or $targets.Count -ne 1) { throw 'Invalid current junction.' }
$target = if ([IO.Path]::IsPathRooted([string]$targets[0])) { [string]$targets[0] } else { Join-Path $root ([string]$targets[0]) }
$releasePath = [IO.Path]::GetFullPath($target)
$release = Get-Content -LiteralPath (Join-Path $releasePath 'release.json') -Raw | ConvertFrom-Json
$manifestPath = Join-Path $releasePath 'MANIFEST.sha256'
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
if ($manifestHash -ine $ApprovedManifestSha256) { throw 'Manifest differs from approved artifact.' }
$entries = @{}
foreach ($line in Get-Content -LiteralPath $manifestPath) {
    if (-not $line) { continue }
    if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<path>.+)$') { throw 'Invalid payload manifest.' }
    $relative = $Matches.path.Replace('\','/'); $hash = $Matches.hash
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..' -or $entries.ContainsKey($relative)) { throw 'Unsafe/duplicate payload path.' }
    $file = Join-Path $releasePath $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $hash) { throw "Payload mismatch: $relative" }
    $entries[$relative]=$hash
}
foreach ($required in @('release.json','apps/edge/FactoryConnect.Edge.exe','apps/api/FactoryConnect.Api.exe')) {
    if (-not $entries.ContainsKey($required)) { throw "Required payload missing from manifest: $required" }
}
$actual = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse | Where-Object { $_.FullName -ine $manifestPath })
if ($actual.Count -ne $entries.Count) { throw 'Payload membership differs from approved manifest.' }
foreach ($file in $actual) {
    $relative = $file.FullName.Substring($releasePath.TrimEnd('\','/').Length + 1).Replace('\','/')
    if (-not $entries.ContainsKey($relative)) { throw "Unmanifested payload file: $relative" }
}
$runtime = Get-Content -LiteralPath (Join-Path $root 'deployment/runtime.json') -Raw | ConvertFrom-Json
$processes = @{}
foreach ($name in @('edge','api')) {
    $record = $runtime.$name
    if ($null -eq $record) { throw "No $name process record." }
    $process = Get-Process -Id ([int]$record.pid) -ErrorAction Stop
    $processes[$name] = [pscustomobject]@{ pid=$process.Id; executablePath=$process.Path; startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o') }
}
$capture = [pscustomobject]@{ Root=$root; LinkType=$item.LinkType; Targets=@($releasePath); Release=$release;
    ManifestHash=$manifestHash; PayloadVerified=$true; Runtime=$runtime; Processes=$processes }
Assert-RecoveryDeploymentCapture $capture $ApprovedRelease $ApprovedManifestSha256 | ConvertTo-Json -Depth 20
