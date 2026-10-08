[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommit,

    [Parameter()]
    [string]$OutputRoot,

    [Parameter()]
    [string]$DeploymentScriptPath,

    [Parameter()]
    [string]$RuntimeStartupScriptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    # Windows PowerShell 5.1 runs on .NET Framework, where Path.GetRelativePath
    # is unavailable. Keep release construction compatible with both Windows
    # PowerShell and modern PowerShell by deriving a contained relative path.
    $rootFullPath = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathFullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $rootFullPath + [System.IO.Path]::DirectorySeparatorChar

    if (-not $pathFullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$pathFullPath' is not contained by root '$rootFullPath'."
    }

    return $pathFullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Assert-CleanWorkspace {
    $status = Invoke-External git @('status', '--porcelain=v1', '--untracked-files=all')
    $dirty = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirty.Count -ne 0) {
        throw "FactoryConnect release production requires a clean workspace.$([Environment]::NewLine)$($dirty -join [Environment]::NewLine)"
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
            throw "Published reparse points are not admitted into FactoryConnect releases: '$($entry.FullName)'."
        }
    }

    foreach ($file in (Get-ChildItem -LiteralPath $Source -File -Recurse)) {
        if ($file.Extension -ieq '.pdb' -or $file.Name -ieq 'appsettings.Development.json') {
            continue
        }

        $relativePath = Get-RelativePath -Root $Source -Path $file.FullName
        $destinationPath = Join-Path $Destination ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
        [System.IO.File]::Copy($file.FullName, $destinationPath, $false)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$buildStartedAtUtc = [DateTime]::UtcNow
$runtimeIdentifier = 'win-x64'
$configuration = 'Release'
$targetFramework = 'net10.0'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts/releases'
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

if ([string]::IsNullOrWhiteSpace($DeploymentScriptPath)) {
    $DeploymentScriptPath = Join-Path $repoRoot 'scripts/deployment/Deploy-FactoryConnect.ps1'
}
else {
    $DeploymentScriptPath = [System.IO.Path]::GetFullPath($DeploymentScriptPath)
}

if ([string]::IsNullOrWhiteSpace($RuntimeStartupScriptPath)) {
    $RuntimeStartupScriptPath = Join-Path $repoRoot 'scripts/deployment/Start-FactoryConnectRuntime.ps1'
}
else {
    $RuntimeStartupScriptPath = [System.IO.Path]::GetFullPath($RuntimeStartupScriptPath)
}

$systemStartupScriptPath = Join-Path $repoRoot 'scripts/deployment/Start-FactoryConnectSystem.ps1'
$systemStartupInstallerPath = Join-Path $repoRoot 'scripts/deployment/Install-FactoryConnectSystemStartup.ps1'
$sqlReadinessScriptPath = Join-Path $repoRoot 'scripts/deployment/FactoryConnect.SqlReadiness.ps1'
$processTerminationScriptPath = Join-Path $repoRoot 'scripts/deployment/FactoryConnect.ProcessTermination.ps1'

Push-Location $repoRoot
try {
    $head = ((Invoke-External git @('rev-parse', 'HEAD')) -join '').Trim().ToLowerInvariant()
    if ($head -notmatch '^[0-9a-f]{40}$') {
        throw 'Repository HEAD did not resolve to a full commit SHA.'
    }

    if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
        $SourceCommit = $head
    }
    $SourceCommit = $SourceCommit.ToLowerInvariant()

    if ($head -ne $SourceCommit) {
        throw "Repository HEAD '$head' does not match requested source commit '$SourceCommit'."
    }

    Assert-CleanWorkspace

    $origin = ((Invoke-External git @('remote', 'get-url', 'origin')) -join '').Trim()
    $acceptedOrigins = @(
        'https://github.com/abilgaiyan/FactoryConnect',
        'https://github.com/abilgaiyan/FactoryConnect.git',
        'git@github.com:abilgaiyan/FactoryConnect.git'
    )
    if ($acceptedOrigins -notcontains $origin) {
        throw "Repository origin '$origin' is not the authoritative FactoryConnect remote."
    }

    $dotnetVersion = ((Invoke-External dotnet @('--version')) -join '').Trim()
    if ($dotnetVersion -notmatch '^10\.0\.\d+$') {
        throw "FactoryConnect release production requires a stable .NET 10.0.x SDK. Resolved '$dotnetVersion'."
    }

    $nodeVersionFile = Join-Path $repoRoot '.node-version'
    if (-not (Test-Path -LiteralPath $nodeVersionFile -PathType Leaf)) {
        throw '.node-version is required for FactoryConnect release production.'
    }
    $expectedNodeVersion = (Get-Content -Raw -LiteralPath $nodeVersionFile).Trim().TrimStart('v')
    $nodeVersion = (((Invoke-External node @('--version')) -join '').Trim()).TrimStart('v')
    if ($nodeVersion -ne $expectedNodeVersion) {
        throw "Node version '$nodeVersion' does not match repository authority '$expectedNodeVersion'."
    }
    $npmVersion = ((Invoke-External npm @('--version')) -join '').Trim()

    if (-not (Test-Path -LiteralPath $DeploymentScriptPath -PathType Leaf)) {
        throw "The frozen package contract requires the deployment script, but it was not found at '$DeploymentScriptPath'. Implement scripts/deployment/Deploy-FactoryConnect.ps1 before producing a transferable release."
    }

    if (-not (Test-Path -LiteralPath $RuntimeStartupScriptPath -PathType Leaf)) {
        throw "The reboot-safe package contract requires the runtime startup script, but it was not found at '$RuntimeStartupScriptPath'."
    }

    foreach ($requiredPath in @($systemStartupScriptPath, $systemStartupInstallerPath, $processTerminationScriptPath, $sqlReadinessScriptPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required boot orchestration input missing: '$requiredPath'."
        }
    }

    $deployables = @(
        [pscustomobject]@{ Name = 'sql-readiness'; Project = 'tools/FactoryConnect.SqlReadiness/FactoryConnect.SqlReadiness.csproj'; Executable = 'FactoryConnect.SqlReadiness.exe' },
        [pscustomobject]@{ Name = 'migrations'; Project = 'src/FactoryConnect.Migrations/FactoryConnect.Migrations.csproj'; Executable = 'FactoryConnect.Migrations.exe' },
        [pscustomobject]@{ Name = 'edge'; Project = 'src/FactoryConnect.Edge/FactoryConnect.Edge.csproj'; Executable = 'FactoryConnect.Edge.exe' },
        [pscustomobject]@{ Name = 'api'; Project = 'src/FactoryConnect.Api/FactoryConnect.Api.csproj'; Executable = 'FactoryConnect.Api.exe' },
        [pscustomobject]@{ Name = 'dashboard'; Project = 'src/FactoryConnect.Dashboard/FactoryConnect.Dashboard.csproj'; Executable = 'FactoryConnect.Dashboard.exe' }
    )

    $templates = @(
        [pscustomobject]@{ Name = 'edge'; Source = 'config/templates/edge.production.template.json'; PackagePath = 'config-templates/edge.production.template.json' },
        [pscustomobject]@{ Name = 'api'; Source = 'config/templates/api.production.template.json'; PackagePath = 'config-templates/api.production.template.json' },
        [pscustomobject]@{ Name = 'dashboard'; Source = 'config/templates/dashboard.production.template.json'; PackagePath = 'config-templates/dashboard.production.template.json' }
    )

    foreach ($deployable in $deployables) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $deployable.Project) -PathType Leaf)) {
            throw "Required deployable project is missing: $($deployable.Project)"
        }
    }
    foreach ($template in $templates) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $template.Source) -PathType Leaf)) {
            throw "Required production template is missing: $($template.Source)"
        }
    }

    $releaseName = "FactoryConnect-$SourceCommit"
    $stagingParent = Join-Path $OutputRoot '.staging'
    $stagingRoot = Join-Path $stagingParent $releaseName
    $finalRoot = Join-Path $OutputRoot $releaseName
    $zipPath = Join-Path $OutputRoot "$releaseName.zip"

    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Force -Recurse
    }
    if (Test-Path -LiteralPath $finalRoot) {
        throw "Release directory already exists and is immutable: '$finalRoot'."
    }
    if (Test-Path -LiteralPath $zipPath) {
        throw "Release archive already exists: '$zipPath'."
    }

    [System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null

    $dashboardClientOutput = Join-Path $repoRoot 'artifacts/dashboard-client/factory_release_win-x64'
    if (Test-Path -LiteralPath $dashboardClientOutput) {
        Remove-Item -LiteralPath $dashboardClientOutput -Force -Recurse
    }

    foreach ($deployable in $deployables) {
        $publishDirectory = Join-Path $repoRoot "artifacts/publish/$($deployable.Name)/factory_release_win-x64"
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Force -Recurse
        }

        $publishArguments = @(
            'publish', (Join-Path $repoRoot $deployable.Project),
            '--configuration', $configuration,
            '--framework', $targetFramework,
            '--runtime', $runtimeIdentifier,
            '--self-contained', 'true',
            '--nologo',
            '--output', $publishDirectory
        )
        if ($deployable.Name -eq 'dashboard') {
            $publishArguments += '-p:BuildDashboardClient=true'
            $publishArguments += "-p:DashboardClientOutputDirectory=$dashboardClientOutput"
        }

        [void](Invoke-External dotnet $publishArguments)

        $publishedExecutable = Join-Path $publishDirectory $deployable.Executable
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "Publish did not produce required executable '$publishedExecutable'."
        }

        Copy-PublishPayload -Source $publishDirectory -Destination (Join-Path $stagingRoot "apps/$($deployable.Name)")
    }

    $templateRoot = Join-Path $stagingRoot 'config-templates'
    [System.IO.Directory]::CreateDirectory($templateRoot) | Out-Null
    foreach ($template in $templates) {
        $destination = Join-Path $stagingRoot ($template.PackagePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        [System.IO.File]::Copy((Join-Path $repoRoot $template.Source), $destination, $false)
    }

    [System.IO.File]::Copy($DeploymentScriptPath, (Join-Path $stagingRoot 'Deploy-FactoryConnect.ps1'), $false)
    [System.IO.File]::Copy($RuntimeStartupScriptPath, (Join-Path $stagingRoot 'Start-FactoryConnectRuntime.ps1'), $false)
    [System.IO.File]::Copy($systemStartupScriptPath, (Join-Path $stagingRoot 'Start-FactoryConnectSystem.ps1'), $false)
    [System.IO.File]::Copy($systemStartupInstallerPath, (Join-Path $stagingRoot 'Install-FactoryConnectSystemStartup.ps1'), $false)
    [System.IO.File]::Copy($sqlReadinessScriptPath, (Join-Path $stagingRoot 'FactoryConnect.SqlReadiness.ps1'), $false)
    [System.IO.File]::Copy($processTerminationScriptPath, (Join-Path $stagingRoot 'FactoryConnect.ProcessTermination.ps1'), $false)

    $release = [ordered]@{
        schemaVersion = '1.0'
        sourceCommit = $SourceCommit
        buildTimestampUtc = $buildStartedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        repositoryUrl = 'https://github.com/abilgaiyan/FactoryConnect'
        publish = [ordered]@{
            configuration = $configuration
            targetFramework = $targetFramework
            runtimeIdentifier = $runtimeIdentifier
            selfContained = $true
        }
        toolchain = [ordered]@{
            dotNetSdkVersion = $dotnetVersion
            nodeVersion = $nodeVersion
            npmVersion = $npmVersion
        }
        applications = @(
            foreach ($deployable in $deployables) {
                [ordered]@{
                    name = $deployable.Name
                    path = "apps/$($deployable.Name)"
                    executable = "apps/$($deployable.Name)/$($deployable.Executable)"
                }
            }
        )
        configurationTemplates = @(
            foreach ($template in $templates) {
                [ordered]@{ name = $template.Name; path = $template.PackagePath }
            }
        )
        deploymentScript = 'Deploy-FactoryConnect.ps1'
        runtimeStartupScript = 'Start-FactoryConnectRuntime.ps1'
        systemStartupScript = 'Start-FactoryConnectSystem.ps1'
        systemStartupInstaller = 'Install-FactoryConnectSystemStartup.ps1'
        sqlReadinessScript = 'FactoryConnect.SqlReadiness.ps1'
        processTerminationScript = 'FactoryConnect.ProcessTermination.ps1'
        migrationLedgerTarget = 'FactoryConnect SQL migration ledger managed by FactoryConnect.Migrations'
    }

    $releaseJson = ($release | ConvertTo-Json -Depth 8) + "`n"
    Write-Utf8NoBom -Path (Join-Path $stagingRoot 'release.json') -Text $releaseJson

    Assert-CleanWorkspace

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $stagingRoot -File -Recurse |
            Where-Object { $_.Name -ne 'MANIFEST.sha256' } |
            Sort-Object { Get-RelativePath -Root $stagingRoot -Path $_.FullName }
    )
    $manifestLines = foreach ($file in $payloadFiles) {
        $relative = Get-RelativePath -Root $stagingRoot -Path $file.FullName
        "$(Get-Sha256 -Path $file.FullName)  $relative"
    }
    Write-Utf8NoBom -Path (Join-Path $stagingRoot 'MANIFEST.sha256') -Text (($manifestLines -join "`n") + "`n")

    [System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    [System.IO.Directory]::Move($stagingRoot, $finalRoot)

    Compress-Archive -LiteralPath $finalRoot -DestinationPath $zipPath -CompressionLevel Optimal

    [pscustomobject]@{
        SourceCommit = $SourceCommit
        ReleaseDirectory = $finalRoot
        ArchivePath = $zipPath
        PayloadFiles = $payloadFiles.Count
        ManifestSha256 = Get-Sha256 -Path (Join-Path $finalRoot 'MANIFEST.sha256')
    }
}
finally {
    Pop-Location
}
