Set-StrictMode -Version Latest

$script:DemoCandidateRepositoryUrl = "https://github.com/abilgaiyan/FactoryConnect"
$script:DemoCandidateSchemaVersion = "1.0"
$script:DemoCandidateConfiguration = "Release"
$script:DemoCandidateTargetFramework = "net10.0"
$script:DemoCandidateRuntimeIdentifier = "win-x64"

function ConvertTo-DemoCandidateJsonString {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return "null"
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')

    foreach ($character in $Value.ToCharArray()) {
        $code = [int][char]$character
        switch ($character) {
            '"' { [void]$builder.Append('\"'); continue }
            '\' { [void]$builder.Append('\\'); continue }
            "`b" { [void]$builder.Append('\b'); continue }
            "`f" { [void]$builder.Append('\f'); continue }
            "`n" { [void]$builder.Append('\n'); continue }
            "`r" { [void]$builder.Append('\r'); continue }
            "`t" { [void]$builder.Append('\t'); continue }
        }

        if ($code -lt 0x20) {
            [void]$builder.AppendFormat(
                [System.Globalization.CultureInfo]::InvariantCulture,
                '\u{0:x4}',
                $code)
        }
        else {
            [void]$builder.Append($character)
        }
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-DemoCandidateSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
}

function ConvertTo-DemoCandidateRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar

    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$pathFull' is outside root '$rootFull'."
    }

    return $pathFull.Substring($prefix.Length).Replace('\', '/')
}

function Get-DemoCandidateCanonicalManifestText {
    param([Parameter(Mandatory = $true)]$Manifest)

    $lines = [System.Collections.Generic.List[string]]::new()

    $lines.Add('{')
    $lines.Add("  `"schemaVersion`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.schemaVersion)),")
    $lines.Add("  `"candidateId`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.candidateId)),")
    $lines.Add("  `"applicationSourceCommit`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.applicationSourceCommit)),")
    $lines.Add("  `"deploymentContractCommit`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.deploymentContractCommit)),")
    $lines.Add("  `"repositoryUrl`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.repositoryUrl)),")
    $lines.Add("  `"createdAtUtc`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.createdAtUtc)),")
    $lines.Add('  "publishProfile": {')
    $lines.Add("    `"configuration`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.publishProfile.configuration)),")
    $lines.Add("    `"targetFramework`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.publishProfile.targetFramework)),")
    $lines.Add("    `"runtimeIdentifier`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.publishProfile.runtimeIdentifier)),")
    $selfContained = if ([bool]$Manifest.publishProfile.selfContained) { 'true' } else { 'false' }
    $lines.Add("    `"selfContained`": $selfContained")
    $lines.Add('  },')
    $lines.Add('  "toolchain": {')
    $lines.Add("    `"dotNetSdkVersion`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.toolchain.dotNetSdkVersion)),")
    $lines.Add("    `"nodeVersion`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.toolchain.nodeVersion)),")
    $lines.Add("    `"npmVersion`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.toolchain.npmVersion)),")
    $lines.Add("    `"packageLockSha256`": $(ConvertTo-DemoCandidateJsonString ([string]$Manifest.toolchain.packageLockSha256))")
    $lines.Add('  },')
    $lines.Add('  "submodules": [')

    $submodules = @($Manifest.submodules)
    for ($index = 0; $index -lt $submodules.Count; $index++) {
        $item = $submodules[$index]
        $suffix = if ($index -lt ($submodules.Count - 1)) { ',' } else { '' }
        $lines.Add('    {')
        $lines.Add("      `"commit`": $(ConvertTo-DemoCandidateJsonString ([string]$item.commit)),")
        $lines.Add("      `"path`": $(ConvertTo-DemoCandidateJsonString ([string]$item.path))")
        $lines.Add("    }$suffix")
    }

    $lines.Add('  ],')
    $lines.Add('  "artifacts": [')

    $artifacts = @($Manifest.artifacts)
    for ($index = 0; $index -lt $artifacts.Count; $index++) {
        $item = $artifacts[$index]
        $suffix = if ($index -lt ($artifacts.Count - 1)) { ',' } else { '' }
        $lines.Add('    {')
        $lines.Add("      `"path`": $(ConvertTo-DemoCandidateJsonString ([string]$item.path)),")
        $lines.Add("      `"length`": $([long]$item.length),")
        $lines.Add("      `"sha256`": $(ConvertTo-DemoCandidateJsonString ([string]$item.sha256))")
        $lines.Add("    }$suffix")
    }

    $lines.Add('  ]')
    $lines.Add('}')

    return (($lines -join "`n") + "`n")
}

function Write-DemoCandidateUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-DemoCandidatePayloadInventory {
    param([Parameter(Mandatory = $true)][string]$CandidateRoot)

    $manifestNames = @('release-manifest.json', 'release-manifest.sha256')
    $records = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($file in (Get-ChildItem -LiteralPath $CandidateRoot -File -Recurse)) {
        $relativePath = ConvertTo-DemoCandidateRelativePath -Root $CandidateRoot -Path $file.FullName
        if ($manifestNames -contains $relativePath) {
            continue
        }

        if (-not $seen.Add($relativePath)) {
            throw "Duplicate normalized payload path '$relativePath'."
        }

        $records.Add([pscustomobject]@{
            path = $relativePath
            length = [long]$file.Length
            sha256 = Get-DemoCandidateSha256 -Path $file.FullName
        })
    }

    $records.Sort([System.Comparison[object]]{
        param($left, $right)
        return [System.StringComparer]::Ordinal.Compare(
            [string]$left.path,
            [string]$right.path)
    })

    return @($records.ToArray())
}
