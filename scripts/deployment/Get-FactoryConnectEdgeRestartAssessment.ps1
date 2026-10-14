# Read-only evidence collection. This operation never adopts/stops/launches a
# process, edits runtime evidence, clears an intent, or authorizes recovery.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Get-AssessmentProperty($Value,[string]$Name) {
    if ($null -eq $Value) { return $null }
    $property=$Value.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Get-AssessmentPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
}
function Get-AssessmentTimestamp($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime().ToString('o') }
    return [string]$Value
}
function Get-AssessmentRecordState($Record) {
    if ($null -eq $Record) { return 'NoRecord' }
    $process=$null
    try {
        try { $process=Get-Process -Id ([int]$Record.pid) -ErrorAction Stop }
        catch {
            if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId*') { return 'Absent' }
            return 'Unproven'
        }
        [void]$process.Handle
        $process.Refresh()
        if ($process.HasExited) { return 'Absent' }
        if ((Get-AssessmentPath $process.Path) -ne (Get-AssessmentPath ([string]$Record.executablePath)) -or
            $process.StartTime.ToUniversalTime().ToString('o') -cne (Get-AssessmentTimestamp $Record.startTimeUtc)) {
            return 'Mismatch'
        }
        return 'Verified'
    } catch { return 'Unproven' }
    finally { if ($null -ne $process) { $process.Dispose() } }
}
function Get-AssessmentInventory {
    # CIM enumerates matching executable names even when path access is denied.
    # Such rows are uncertainty, never an absence proof. Path/start time are then
    # observed together on a pinned live process instance; PID/name alone cannot
    # establish ownership. No use of parent PID to infer launch provenance.
    $rows=@(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'FactoryConnect.Edge.exe'" -ErrorAction Stop)
    $result=[Collections.Generic.List[object]]::new()
    foreach ($row in $rows) {
        $process=$null
        try {
            if ([string]::IsNullOrWhiteSpace([string]$row.ExecutablePath)) {
                throw 'Executable path is not accessible.'
            }
            try { $process=Get-Process -Id ([int]$row.ProcessId) -ErrorAction Stop }
            catch {
                if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId*') { continue }
                throw
            }
            [void]$process.Handle; $process.Refresh()
            if ($process.HasExited) { continue }
            if ((Get-AssessmentPath $process.Path) -ne (Get-AssessmentPath ([string]$row.ExecutablePath))) {
                throw 'Process identity changed during enumeration.'
            }
            $result.Add([pscustomobject]@{name='edge';pid=$process.Id;executablePath=(Get-AssessmentPath $process.Path);startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o');observation='VerifiedIdentity'})
        } catch {
            $result.Add([pscustomobject]@{name='edge';pid=[int]$row.ProcessId;executablePath=$null;startTimeUtc=$null;observation='Unproven'})
        } finally { if ($null -ne $process) { $process.Dispose() } }
    }
    return $result.ToArray()
}
function Test-AssessmentSameRecord($Left,$Right) {
    if ($null -eq $Left -or $null -eq $Right) { return $false }
    try {
        return ([int]$Left.pid -eq [int]$Right.pid -and
            (Get-AssessmentPath ([string]$Left.executablePath)) -eq (Get-AssessmentPath ([string]$Right.executablePath)) -and
            (Get-AssessmentTimestamp $Left.startTimeUtc) -ceq (Get-AssessmentTimestamp $Right.startTimeUtc))
    } catch { return $false }
}
$root=Get-AssessmentPath $InstallRoot
$deployment=Join-Path $root 'deployment'
$lock=$null
try {
    # Open only an existing lock file for read, with exclusive sharing. Missing
    # lock or contention is a rejection; no filesystem objects are created.
    $lock=[IO.File]::Open((Join-Path $deployment 'deployment.lock'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    $runtime=Get-Content -Raw -LiteralPath (Join-Path $deployment 'runtime.json') | ConvertFrom-Json
    $intentPath=Join-Path $deployment 'runtime-start.intent.json'
    $intent=if (Test-Path -LiteralPath $intentPath) { Get-Content -Raw -LiteralPath $intentPath | ConvertFrom-Json } else { $null }
    $restart=Get-AssessmentProperty $runtime 'edgeRestart'
    $oldEdge=Get-AssessmentProperty $restart 'oldEdge'
    $record=Get-AssessmentProperty $runtime 'edge'
    $diagnostic=Get-AssessmentProperty (Get-AssessmentProperty $restart 'replacementObservation') 'record'
    $reasons=[Collections.Generic.List[string]]::new()
    $selectionVerified=$false
    $expected=$null
    try {
        $current=Get-Item -LiteralPath (Join-Path $root 'current') -Force
        $targets=@($current.Target)
        if ([string]$current.LinkType -cne 'Junction' -or $targets.Count -ne 1) { throw 'Invalid current junction.' }
        $release=Get-AssessmentPath $(if ([IO.Path]::IsPathRooted([string]$targets[0])) { [string]$targets[0] } else { Join-Path $root ([string]$targets[0]) })
        $prefix=(Get-AssessmentPath (Join-Path $root 'releases'))+[IO.Path]::DirectorySeparatorChar
        if (-not $release.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release is outside releases root.' }
        $releaseId=$release.Substring($prefix.Length)
        if ($releaseId -cnotmatch '^[0-9a-f]{40}$' -or $releaseId -cne [string]$runtime.selectedRelease -or
            $release -ne (Get-AssessmentPath ([string]$runtime.releasePath))) { throw 'Release selection does not match runtime evidence.' }
        $expected=Get-AssessmentPath (Join-Path $release 'apps/edge/FactoryConnect.Edge.exe')
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { throw 'Selected Edge executable is missing.' }
        $selectionVerified=$true
    } catch { $reasons.Add($_.Exception.Message) }
    $inventory=@(); $inventoryComplete=$false; $inventoryStable=$false
    try {
        $first=@(Get-AssessmentInventory)
        $inventory=@(Get-AssessmentInventory)
        $inventoryComplete=(@($first+$inventory | Where-Object { $_.observation -ne 'VerifiedIdentity' }).Count -eq 0)
        $inventoryStable=(($first | Sort-Object pid | ConvertTo-Json -Depth 5 -Compress) -ceq ($inventory | Sort-Object pid | ConvertTo-Json -Depth 5 -Compress))
        if (-not $inventoryComplete) { $reasons.Add('Process inventory includes inaccessible identities.') }
        if (-not $inventoryStable) { $reasons.Add('Process inventory changed between observations.') }
    } catch { $reasons.Add('Process inventory is unavailable: '+$_.Exception.Message) }
    $states=[ordered]@{oldEdge=(Get-AssessmentRecordState $oldEdge);recordedEdge=(Get-AssessmentRecordState $record);diagnosticReplacement=(Get-AssessmentRecordState $diagnostic);api=(Get-AssessmentRecordState (Get-AssessmentProperty $runtime 'api'));dashboard=(Get-AssessmentRecordState (Get-AssessmentProperty $runtime 'dashboard'))}
    $intentConsistent=$true
    if ($null -ne $intent) {
        $intentConsistent=($null -ne $restart -and [string](Get-AssessmentProperty $intent 'operation') -ceq 'EdgeRestart' -and
            [string](Get-AssessmentProperty $intent 'processName') -ceq 'edge' -and
            [string](Get-AssessmentProperty $intent 'attemptId') -ceq [string](Get-AssessmentProperty $restart 'attemptId') -and
            [string](Get-AssessmentProperty $intent 'selectedRelease') -ceq [string]$runtime.selectedRelease)
        try { $intentConsistent=$intentConsistent -and (Get-AssessmentPath ([string]$intent.executablePath)) -eq $expected }
        catch { $intentConsistent=$false }
        if (-not $intentConsistent) { $reasons.Add('Startup intent does not match this Edge restart.') }
    }
    $candidates=@($inventory | Where-Object { $_.observation -eq 'VerifiedIdentity' -and $_.executablePath -eq $expected -and -not (Test-AssessmentSameRecord $_ $oldEdge) })
    $classification='AmbiguousOwnership'
    $published=($null -ne $restart -and (Get-AssessmentProperty $restart 'replacementPublished') -eq $true)
    $recordIsReplacement=($null -ne $record -and -not (Test-AssessmentSameRecord $record $oldEdge))
    $recordAtExpectedPath=$false
    if ($null -ne $record -and $selectionVerified) {
        try { $recordAtExpectedPath=(Get-AssessmentPath ([string]$record.executablePath)) -eq $expected -and [string]$record.name -ceq 'edge' }
        catch { }
    }
    if ($selectionVerified -and $inventoryComplete -and $inventoryStable -and $intentConsistent -and $null -ne $restart) {
        if ($published -and $recordIsReplacement -and $recordAtExpectedPath -and $states.recordedEdge -eq 'Verified' -and
            $candidates.Count -eq 1 -and (Test-AssessmentSameRecord $candidates[0] $record)) {
            $classification='VerifiablyOwnedReplacement'
        } elseif ($candidates.Count -eq 0 -and $states.recordedEdge -in @('NoRecord','Absent','Verified') -and
            (-not $recordIsReplacement -or $states.recordedEdge -eq 'Absent') -and
            $states.diagnosticReplacement -in @('NoRecord','Absent') -and $states.oldEdge -in @('Verified','Absent')) {
            $classification='AbsentReplacement'
        } else { $reasons.Add('Candidate presence, identity mismatch, or unpublished ownership requires explicit investigation.') }
    }
    # A point-in-time observation is never authority to alter durable evidence.
    [pscustomobject]@{
        schemaVersion='1.0';operation='EdgeRestartReadOnlyAssessment';observedAtUtc=[DateTime]::UtcNow.ToString('o')
        classification=$classification;recoveryRequired=([string]$runtime.deploymentStatus -cne 'Succeeded' -or $null -ne $intent -or
            ($null -ne $restart -and [string](Get-AssessmentProperty $restart 'status') -cne 'Succeeded'))
        mutationAuthorized=$false;installRoot=$root;selectedRelease=[string]$runtime.selectedRelease
        runtimeStatus=[string]$runtime.deploymentStatus;restartPhase=(Get-AssessmentProperty $restart 'phase')
        launchIntentPresent=($null -ne $intent);launchIntentConsistent=$intentConsistent
        selectedReleaseVerified=$selectionVerified;inventoryComplete=$inventoryComplete;inventoryStable=$inventoryStable
        identityStates=[pscustomobject]$states;candidates=$candidates;inventory=$inventory
        diagnosticReplacementRecord=$diagnostic;reasons=$reasons.ToArray()
    }
} finally { if ($null -ne $lock) { $lock.Dispose() } }
