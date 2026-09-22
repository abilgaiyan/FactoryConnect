[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommit
)

$ErrorActionPreference = "Stop"

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @()
    )

    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $rendered = ($output | Out-String).Trim()
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$rendered"
    }

    return @($output)
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Push-Location $repoRoot

try {
    $approvedSourceCommit = $SourceCommit.ToLowerInvariant()

    $head = ((Invoke-External git @("rev-parse", "HEAD")) -join "").Trim().ToLowerInvariant()
    if ($head -notmatch '^[0-9a-f]{40}$') {
        throw "Repository HEAD did not resolve to a full commit SHA."
    }

    if ($head -ne $approvedSourceCommit) {
        throw "Repository HEAD '$head' does not match approved source commit '$approvedSourceCommit'."
    }

    $status = Invoke-External git @(
        "status",
        "--porcelain=v1",
        "--untracked-files=all"
    )

    $dirtyLines = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirtyLines.Count -ne 0) {
        throw "Release source workspace is not clean.$([Environment]::NewLine)$($dirtyLines -join [Environment]::NewLine)"
    }

    $submoduleRecords = @()
    if (Test-Path (Join-Path $repoRoot ".gitmodules")) {
        $submoduleStatus = Invoke-External git @(
            "submodule",
            "status",
            "--recursive"
        )

        foreach ($line in $submoduleStatus) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            if ($line[0] -ne ' ') {
                throw "Submodule state is not clean: $line"
            }

            $parts = $line.Trim().Split(
                [char[]]" ",
                [System.StringSplitOptions]::RemoveEmptyEntries)

            if ($parts.Count -lt 2) {
                throw "Unable to parse submodule status line: $line"
            }

            $submoduleRecords += [pscustomobject]@{
                Commit = $parts[0]
                Path = $parts[1]
            }
        }
    }

    $dotnetVersion = ((Invoke-External dotnet @("--version")) -join "").Trim()
    if ($dotnetVersion -notmatch '^10\.0\.\d+$') {
        throw "Demo Candidate release requires a stable .NET 10.0.x SDK. Resolved '$dotnetVersion'."
    }

    $nodeVersionRaw = ((Invoke-External node @("--version")) -join "").Trim()
    $nodeVersion = $nodeVersionRaw.TrimStart('v')

    $nodeVersionFile = Join-Path $repoRoot ".node-version"
    if (-not (Test-Path $nodeVersionFile)) {
        throw ".node-version is required for Demo Candidate release production."
    }

    $expectedNodeVersion = (Get-Content -Raw $nodeVersionFile).Trim().TrimStart('v')
    if ($nodeVersion -ne $expectedNodeVersion) {
        throw "Node version '$nodeVersion' does not match repository authority '$expectedNodeVersion'."
    }

    $npmVersion = ((Invoke-External npm @("--version")) -join "").Trim()

    $packageLockPath = Join-Path $repoRoot "src/FactoryConnect.Dashboard/ClientApp/package-lock.json"
    if (-not (Test-Path $packageLockPath)) {
        throw "Dashboard package-lock.json is required for Demo Candidate release production."
    }

    $packageLockSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageLockPath).Hash.ToLowerInvariant()

    $deployables = @(
        [pscustomobject]@{
            Artifact = "migrations"
            Project = "src/FactoryConnect.Migrations/FactoryConnect.Migrations.csproj"
        },
        [pscustomobject]@{
            Artifact = "edge"
            Project = "src/FactoryConnect.Edge/FactoryConnect.Edge.csproj"
        },
        [pscustomobject]@{
            Artifact = "api"
            Project = "src/FactoryConnect.Api/FactoryConnect.Api.csproj"
        },
        [pscustomobject]@{
            Artifact = "dashboard"
            Project = "src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj"
        }
    )

    foreach ($deployable in $deployables) {
        $projectPath = Join-Path $repoRoot $deployable.Project
        if (-not (Test-Path $projectPath)) {
            throw "Required deployable project is missing: $($deployable.Project)"
        }
    }

    [pscustomobject]@{
        SourceCommit = $head
        DotNetSdkVersion = $dotnetVersion
        NodeVersion = $nodeVersion
        NpmVersion = $npmVersion
        PackageLockSha256 = $packageLockSha256
        Deployables = $deployables
        Submodules = $submoduleRecords
    }
}
finally {
    Pop-Location
}
