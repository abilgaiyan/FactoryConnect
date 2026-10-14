[CmdletBinding()]
param([string]$ExecutableHarness)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($ExecutableHarness)) { $ExecutableHarness=Join-Path $PSScriptRoot 'Invoke-EdgeRestartExecutableConformance.ps1' }
$t=$null;$e=$null
$ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ExecutableHarness),[ref]$t,[ref]$e)
if($e.Count){throw ($e|Out-String)}
foreach($name in @('Complete-FixtureRun','Remove-FixtureDirectory')){
    $function=@($ast.EndBlock.Statements|Where-Object{$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name})[0]
    . ([scriptblock]::Create($function.Extent.Text))
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$results=[ordered]@{}
$failures=[Collections.Generic.List[string]]::new()
$primary=$null
try{throw 'Original R08 failure.'}catch{$primary=$_}
$failures.Add('Injected secondary deletion failure.')
$caught=$null
try{Complete-FixtureRun $primary $failures}catch{$caught=$_}
Assert-True ($null -ne $caught -and $caught.Exception.Message -ceq 'Original R08 failure.') 'Cleanup failure replaced the primary test error.'
$results.PrimaryFailurePreserved='PASS'
$caught=$null
try{Complete-FixtureRun $null $failures}catch{$caught=$_}
Assert-True ($null -ne $caught -and $caught.Exception.Message -like '*Injected secondary deletion failure.*') 'A cleanup-only failure was incorrectly reported as success.'
$results.CleanupOnlyFailureReported='PASS'
$failures.Clear();Complete-FixtureRun $null $failures
$results.SuccessWithoutFailures='PASS'
# Model transient sharing failures around the actual recursive directory removal.
$script:removeAttempts=0;$script:failUntil=2
function Remove-Item {
    param([string]$LiteralPath,[switch]$Recurse,[switch]$Force,[string]$ErrorAction)
    $script:removeAttempts++
    if($script:removeAttempts -le $script:failUntil){throw 'Injected executable sharing failure.'}
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Start-Sleep {param([int]$Milliseconds)}
$root=Join-Path ([IO.Path]::GetTempPath()) ('FC-cleanup-proof-'+[Guid]::NewGuid().ToString('N'))
try{
    New-Item -ItemType Directory $root|Out-Null
    Set-Content (Join-Path $root 'Fixture.exe') 'disposable non-executable fixture'
    Remove-FixtureDirectory $root
    Assert-True ($script:removeAttempts -eq 3 -and -not(Test-Path $root)) 'Transient sharing failures did not retry removal.'
    $results.TransientSharingRetry='PASS'
    New-Item -ItemType Directory $root|Out-Null
    $script:removeAttempts=0;$script:failUntil=99;$caught=$null
    try{Remove-FixtureDirectory $root}catch{$caught=$_}
    Assert-True ($null -ne $caught -and $script:removeAttempts -eq 6 -and (Test-Path $root)) 'Persistent deletion failure exceeded its retry bound or disappeared.'
    $results.PersistentSharingBounded='PASS'
}finally{if(Test-Path $root){Microsoft.PowerShell.Management\Remove-Item -LiteralPath $root -Recurse -Force}}
[pscustomobject]$results
