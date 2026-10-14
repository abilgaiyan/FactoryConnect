[CmdletBinding()]
param([string]$ReleaseBuilder)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ReleaseBuilder)){$ReleaseBuilder=Join-Path $PSScriptRoot '../../scripts/release/Build-FactoryConnectRelease.ps1'}
$t=$null;$e=$null
$ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ReleaseBuilder),[ref]$t,[ref]$e)
if($e.Count){throw ($e|Out-String)}
$f=@($ast.EndBlock.Statements|Where-Object{$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq 'Invoke-External'})[0]
. ([scriptblock]::Create($f.Extent.Text))
$shellName=if($env:OS -eq 'Windows_NT'){if($PSVersionTable.PSVersion.Major -eq 5){'powershell.exe'}else{'pwsh.exe'}}else{'pwsh'}
$shell=Join-Path $PSHOME $shellName
$root=Join-Path ([IO.Path]::GetTempPath()) ('FC-native-proof-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root|Out-Null
$results=[ordered]@{}
try{
    $child=Join-Path $root 'child.ps1'
    '[Console]::Out.WriteLine("stdout-proof");[Console]::Error.WriteLine("stderr-proof");exit 0'|Set-Content $child
    $output=@(Invoke-External $shell @('-NoProfile','-File',$child))
    if(-not($output -contains 'stdout-proof') -or -not($output -contains 'stderr-proof')){throw 'Successful native stderr/stdout was not captured.'}
    if($ErrorActionPreference -ne 'Stop'){throw 'Caller error preference changed.'}
    $results.SuccessfulStderrCaptured='PASS'
    '[Console]::Out.WriteLine("stdout-proof");[Console]::Error.WriteLine("stderr-proof");exit 7'|Set-Content $child
    $message=$null
    try{Invoke-External $shell @('-NoProfile','-File',$child)|Out-Null}catch{$message=$_.Exception.Message}
    if($null -eq $message -or $message -notlike '*exit code 7*' -or $message -notlike '*stdout-proof*' -or $message -notlike '*stderr-proof*'){throw 'Native failure lost exit code or output.'}
    if($ErrorActionPreference -ne 'Stop'){throw 'Failure changed caller error preference.'}
    $results.NativeFailurePreserved='PASS'
    $failed=$false
    try{Invoke-External ('missing-native-'+[Guid]::NewGuid().ToString('N'))|Out-Null}catch{$failed=$true}
    if(-not $failed){throw 'Missing command was accepted.'}
    $results.MissingCommandRejected='PASS'
    # Model Windows npm.cmd/npm discovery without requiring duplicate tools.
    $realCommand=Get-Command -Name $shell -CommandType Application -ErrorAction Stop|Select-Object -First 1
    function Get-Command {
        param([string]$Name,[string]$CommandType,[string]$ErrorAction)
        [pscustomobject]@{Source=$realCommand.Source}
        [pscustomobject]@{Source='unusable-second-discovery-entry'}
    }
    '[Console]::Out.WriteLine("first-path-proof");exit 0'|Set-Content $child
    $output=@(Invoke-External 'duplicate-native-fixture' @('-NoProfile','-File',$child))
    if(-not($output -contains 'first-path-proof')){throw 'Multiple command discovery did not select the first executable.'}
    $results.MultipleDiscoveryUsesFirst='PASS'

}finally{Remove-Item -LiteralPath $root -Recurse -Force}
[pscustomobject]$results
