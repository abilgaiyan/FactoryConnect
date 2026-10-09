[CmdletBinding()]
param([string] $ExpectedSha)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Invoke-AuthorityCheck {
    param([scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Conformance command failed with exit code $LASTEXITCODE." }
}

Push-Location $repositoryRoot
try {
    $head = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve implementation SHA.' }
    Write-Output "Implementation SHA: $head"
    if ($ExpectedSha -and $head -ne $ExpectedSha) { throw 'Implementation SHA differs from requested SHA.' }
    $status = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect worktree.' }
    if ($status) { throw 'A clean worktree is required for acceptance evidence.' }
    Invoke-AuthorityCheck { git diff --check 76d4f4a657c7e18c7d0277583cca6bbf4a572c08...HEAD }
    Invoke-AuthorityCheck { dotnet build FactoryConnect.sln -c Release -m:1 }
    Invoke-AuthorityCheck { dotnet test tests/FactoryConnect.Core.Tests/FactoryConnect.Core.Tests.csproj -c Release --no-build }
    Invoke-AuthorityCheck { dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj -c Release --no-build --filter 'Category!=SqlServerIntegration' }
    $finalStatus = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $finalStatus) { throw 'Verification did not finish with a clean worktree.' }
    Write-Output 'Verification finished with a clean worktree.'
}
finally { Pop-Location }
