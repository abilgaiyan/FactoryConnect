[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommit,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$DeploymentContractCommit,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^demo-candidate-\d{8}-\d{2}$')]
    [string]$CandidateId
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "DemoCandidate.Common.ps1")

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter()][string[]]$Arguments = @()
    )

    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $rendered = ($output | Out-String).Trim()
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$rendered"
    }

    return @($output)
}

function Assert-CleanSourceWorkspace {
    $status = Invoke-External git @(
        "status",
        "--porcelain=v1",
        "--untracked-files=all"
    )

    $dirtyLines = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirtyLines.Count -ne 0) {
        throw "Release source workspace is not clean.$([Environment]::NewLine)$($dirtyLines -join [Environment]::NewLine)"
    }
}

function Copy-PublishPayload {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null

    foreach ($entry in (Get-ChildItem -LiteralPath $Source -Force -Recurse)) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Published reparse points are not admitted into Demo Candidates: '$($entry.FullName)'."
        }
    }

    foreach ($file in (Get-ChildItem -LiteralPath $Source -File -Recurse)) {
        if ($file.Extension -ieq '.pdb' -or $file.Name -ieq 'appsettings.Development.json') {
            continue
        }

        $relativePath = ConvertTo-DemoCandidateRelativePath -Root $Source -Path $file.FullName
        $destinationPath = Join-Path $Destination ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = Split-Path -Parent $destinationPath
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        [System.IO.File]::Copy($file.FullName, $destinationPath, $false)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$releaseStartedAtUtc = [DateTime]::UtcNow
$approvedSourceCommit = $SourceCommit.ToLowerInvariant()
$approvedDeploymentContractCommit = $DeploymentContractCommit.ToLowerInvariant()
$releaseRoot = Join-Path $repoRoot "artifacts/release"
$stagingParent = Join-Path $releaseRoot ".staging"
$candidatesParent = Join-Path $releaseRoot "candidates"
$stagingCandidatePath = Join-Path $stagingParent $CandidateId
$finalCandidatePath = Join-Path $candidatesParent $CandidateId
$stagingCreated = $false
$promoted = $false

Push-Location $repoRoot
try {
    if ($CandidateId -notmatch '^demo-candidate-(?<date>\d{8})-(?<sequence>\d{2})$') {
        throw "CandidateId '$CandidateId' is invalid."
    }

    $candidateDateText = $Matches['date']
    $candidateSequence = [int]$Matches['sequence']
    if ($candidateSequence -lt 1 -or $candidateSequence -gt 99) {
        throw "Candidate sequence must be between 01 and 99."
    }

    $candidateDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
        $candidateDateText,
        'yyyyMMdd',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$candidateDate)) {
        throw "CandidateId '$CandidateId' contains an invalid date."
    }

    if ($candidateDate.Date -ne $releaseStartedAtUtc.Date) {
        throw "CandidateId date '$candidateDateText' must equal release creation UTC date '$($releaseStartedAtUtc.ToString('yyyyMMdd'))'."
    }

    $head = ((Invoke-External git @("rev-parse", "HEAD")) -join "").Trim().ToLowerInvariant()
    if ($head -notmatch '^[0-9a-f]{40}$') {
        throw "Repository HEAD did not resolve to a full commit SHA."
    }
    if ($head -ne $approvedSourceCommit) {
        throw "Repository HEAD '$head' does not match approved source commit '$approvedSourceCommit'."
    }

    Assert-CleanSourceWorkspace

    [void](Invoke-External git @("cat-file", "-e", "$approvedDeploymentContractCommit^{commit}"))

    $origin = ((Invoke-External git @("remote", "get-url", "origin")) -join "").Trim()
    $acceptedOrigins = @(
        'https://github.com/abilgaiyan/FactoryConnect',
        'https://github.com/abilgaiyan/FactoryConnect.git',
        'git@github.com:abilgaiyan/FactoryConnect.git'
    )
    if ($acceptedOrigins -notcontains $origin) {
        throw "Repository origin '$origin' is not the authoritative FactoryConnect remote."
    }

    $submoduleRecords = @()
    if (Test-Path (Join-Path $repoRoot ".gitmodules")) {
        $submoduleStatus = Invoke-External git @("submodule", "status", "--recursive")
        foreach ($line in $submoduleStatus) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            if ($line[0] -ne ' ') {
                throw "Submodule state is not clean: $line"
            }

            $parts = $line.Trim().Split([char[]]" ", [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($parts.Count -lt 2 -or $parts[0] -notmatch '^[0-9a-fA-F]{40}$') {
                throw "Unable to parse submodule status line: $line"
            }

            $submoduleRecords += [pscustomobject]@{
                commit = $parts[0].ToLowerInvariant()
                path = $parts[1].Replace('\', '/')
            }
        }

        $submoduleRecords = @($submoduleRecords | Sort-Object -Property path)
    }

    $dotnetVersion = ((Invoke-External dotnet @("--version")) -join "").Trim()
    if ($dotnetVersion -notmatch '^10\.0\.\d+$') {
        throw "Demo Candidate release requires a stable .NET 10.0.x SDK. Resolved '$dotnetVersion'."
    }

    $nodeVersion = (((Invoke-External node @("--version")) -join "").Trim()).TrimStart('v')
    $nodeVersionFile = Join-Path $repoRoot ".node-version"
    if (-not (Test-Path -LiteralPath $nodeVersionFile -PathType Leaf)) {
        throw ".node-version is required for Demo Candidate release production."
    }
    $expectedNodeVersion = (Get-Content -Raw -LiteralPath $nodeVersionFile).Trim().TrimStart('v')
    if ($nodeVersion -ne $expectedNodeVersion) {
        throw "Node version '$nodeVersion' does not match repository authority '$expectedNodeVersion'."
    }

    $npmVersion = ((Invoke-External npm @("--version")) -join "").Trim()
    $packageLockPath = Join-Path $repoRoot "src/FactoryConnect.Dashboard/ClientApp/package-lock.json"
    if (-not (Test-Path -LiteralPath $packageLockPath -PathType Leaf)) {
        throw "Dashboard package-lock.json is required for Demo Candidate release production."
    }
    $packageLockSha256 = Get-DemoCandidateSha256 -Path $packageLockPath

    $deployables = @(
        [pscustomobject]@{ Artifact = 'migrations'; ProjectName = 'FactoryConnect.Migrations'; Project = 'src/FactoryConnect.Migrations/FactoryConnect.Migrations.csproj' },
        [pscustomobject]@{ Artifact = 'edge'; ProjectName = 'FactoryConnect.Edge'; Project = 'src/FactoryConnect.Edge/FactoryConnect.Edge.csproj' },
        [pscustomobject]@{ Artifact = 'api'; ProjectName = 'FactoryConnect.Api'; Project = 'src/FactoryConnect.Api/FactoryConnect.Api.csproj' },
        [pscustomobject]@{ Artifact = 'dashboard'; ProjectName = 'FactoryConnect.Dashboard'; Project = 'src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj' }
    )

    foreach ($deployable in $deployables) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $deployable.Project) -PathType Leaf)) {
            throw "Required deployable project is missing: $($deployable.Project)"
        }
    }

    $templateSources = @(
        'api.production.template.json',
        'dashboard.production.template.json',
        'deployment-manifest.template.json',
        'edge.production.template.json'
    )
    $templateRoot = Join-Path $repoRoot 'config/templates'
    foreach ($template in $templateSources) {
        if (-not (Test-Path -LiteralPath (Join-Path $templateRoot $template) -PathType Leaf)) {
            throw "Required Demo Candidate template is missing: config/templates/$template"
        }
    }

    if (Test-Path -LiteralPath $stagingCandidatePath) {
        throw "Demo Candidate staging path already exists: '$stagingCandidatePath'."
    }
    if (Test-Path -LiteralPath $finalCandidatePath) {
        throw "Demo Candidate final path already exists: '$finalCandidatePath'."
    }

    [System.IO.Directory]::CreateDirectory($stagingParent) | Out-Null
    [System.IO.Directory]::CreateDirectory($candidatesParent) | Out-Null
    [System.IO.Directory]::CreateDirectory($stagingCandidatePath) | Out-Null
    $stagingCreated = $true

    $dashboardClientOutput = Join-Path $repoRoot "artifacts/dashboard-client/release_win-x64"
    if (Test-Path -LiteralPath $dashboardClientOutput) {
        Remove-Item -LiteralPath $dashboardClientOutput -Force -Recurse
    }

    foreach ($deployable in $deployables) {
        $projectPath = Join-Path $repoRoot $deployable.Project
        $publishDirectory = Join-Path $repoRoot "artifacts/publish/$($deployable.ProjectName)/release_win-x64"
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Force -Recurse
        }

        $publishArguments = @(
            'publish',
            $projectPath,
            '--configuration', $script:DemoCandidateConfiguration,
            '--framework', $script:DemoCandidateTargetFramework,
            '--runtime', $script:DemoCandidateRuntimeIdentifier,
            '--self-contained', 'true',
            '--nologo',
            '--output', $publishDirectory
        )

        if ($deployable.Artifact -eq 'dashboard') {
            $publishArguments += '-p:BuildDashboardClient=true'
            $publishArguments += "-p:DashboardClientOutputDirectory=$dashboardClientOutput"
        }

        [void](Invoke-External dotnet $publishArguments)
        if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) {
            throw "Publish output was not produced for '$($deployable.ProjectName)' at '$publishDirectory'."
        }

        Copy-PublishPayload -Source $publishDirectory -Destination (Join-Path $stagingCandidatePath $deployable.Artifact)
    }

    Assert-CleanSourceWorkspace

    $candidateConfig = Join-Path $stagingCandidatePath 'config'
    [System.IO.Directory]::CreateDirectory($candidateConfig) | Out-Null
    foreach ($template in $templateSources) {
        [System.IO.File]::Copy(
            (Join-Path $templateRoot $template),
            (Join-Path $candidateConfig $template),
            $false)
    }

    $payloadInventory = @(Get-DemoCandidatePayloadInventory -CandidateRoot $stagingCandidatePath)
    $createdAtUtc = $releaseStartedAtUtc.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        [System.Globalization.CultureInfo]::InvariantCulture)

    $manifest = [pscustomobject]@{
        schemaVersion = $script:DemoCandidateSchemaVersion
        candidateId = $CandidateId
        applicationSourceCommit = $approvedSourceCommit
        deploymentContractCommit = $approvedDeploymentContractCommit
        repositoryUrl = $script:DemoCandidateRepositoryUrl
        createdAtUtc = $createdAtUtc
        publishProfile = [pscustomobject]@{
            configuration = $script:DemoCandidateConfiguration
            targetFramework = $script:DemoCandidateTargetFramework
            runtimeIdentifier = $script:DemoCandidateRuntimeIdentifier
            selfContained = $true
        }
        toolchain = [pscustomobject]@{
            dotNetSdkVersion = $dotnetVersion
            nodeVersion = $nodeVersion
            npmVersion = $npmVersion
            packageLockSha256 = $packageLockSha256
        }
        submodules = @($submoduleRecords)
        artifacts = @($payloadInventory)
    }

    $manifestPath = Join-Path $stagingCandidatePath 'release-manifest.json'
    $manifestText = Get-DemoCandidateCanonicalManifestText -Manifest $manifest
    Write-DemoCandidateUtf8NoBom -Path $manifestPath -Text $manifestText

    $manifestSha256 = Get-DemoCandidateSha256 -Path $manifestPath
    $checksumPath = Join-Path $stagingCandidatePath 'release-manifest.sha256'
    Write-DemoCandidateUtf8NoBom -Path $checksumPath -Text "$manifestSha256  release-manifest.json`n"

    $verification = & (Join-Path $PSScriptRoot 'Test-DemoCandidate.ps1') `
        -CandidatePath $stagingCandidatePath `
        -ExpectedCandidateId $CandidateId `
        -ExpectedSourceCommit $approvedSourceCommit `
        -ExpectedDeploymentContractCommit $approvedDeploymentContractCommit

    if (Test-Path -LiteralPath $finalCandidatePath) {
        throw "Demo Candidate final path appeared before promotion: '$finalCandidatePath'."
    }

    [System.IO.Directory]::Move($stagingCandidatePath, $finalCandidatePath)
    $promoted = $true

    [pscustomobject]@{
        CandidateId = $CandidateId
        CandidatePath = $finalCandidatePath
        ManifestSha256 = $verification.ManifestSha256
        ApplicationSourceCommit = $approvedSourceCommit
        DeploymentContractCommit = $approvedDeploymentContractCommit
        PayloadFiles = $verification.PayloadFiles
    }
}
catch {
    $primaryFailure = $_.Exception
    if ($stagingCreated -and -not $promoted -and (Test-Path -LiteralPath $stagingCandidatePath)) {
        try {
            Remove-Item -LiteralPath $stagingCandidatePath -Force -Recurse
        }
        catch {
            $primaryFailure.Data['DemoCandidateStagingCleanupFailure'] = $_.Exception
            $primaryFailure.Data['DemoCandidateStagingCleanupPath'] = $stagingCandidatePath
        }
    }

    throw $primaryFailure
}
finally {
    Pop-Location
}
