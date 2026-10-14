[CmdletBinding()]
param([switch]$Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../scripts/deployment/FactoryConnect.PkcsPayload.ps1')
$metadataReader=${function:Get-FactoryConnectPkcsMetadata}
if($Executable -and ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -ne 5)){throw 'Executable assembly fixtures require Windows PowerShell 5.1.'}
function Get-FactoryConnectPkcsMetadata([string]$Path){
    if($Executable){return (& $metadataReader $Path)}
    return $script:fixtureIdentity
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('FC-pkcs-conformance-'+[Guid]::NewGuid().ToString('N'))
$results=[ordered]@{}
function New-Case([string]$Owner,[string]$ProductVersion) {
    $dir=Join-Path $root ([Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $dir -Force|Out-Null
    $dll=Join-Path $dir 'System.Security.Cryptography.Pkcs.dll'
    $major=([version]$ProductVersion).Major
    if($Executable){
        $provider=New-Object Microsoft.CSharp.CSharpCodeProvider
        $parameters=New-Object CodeDom.Compiler.CompilerParameters
        $parameters.GenerateExecutable=$false;$parameters.GenerateInMemory=$false;$parameters.OutputAssembly=$dll
        $code="using System.Reflection; [assembly: AssemblyVersion(`"$major.0.0.0`")] [assembly: AssemblyFileVersion(`"$ProductVersion`")] [assembly: AssemblyInformationalVersion(`"$ProductVersion+fixture`")] public class PkcsFixture {}"
        # PowerShell literal quote escaping, compatible with 5.1.
        try{$compiled=$provider.CompileAssemblyFromSource($parameters,$code);if($compiled.Errors.HasErrors){throw ($compiled.Errors|Out-String)}}finally{$provider.Dispose()}
    }else{Set-Content $dll 'non-executable fixture';$script:fixtureIdentity=[pscustomobject]@{Name='System.Security.Cryptography.Pkcs';AssemblyVersion="$major.0.0.0";ProductVersion="$ProductVersion+fixture";FileVersion="$ProductVersion.0"}}
    $libraries=[ordered]@{'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13'=@{};'runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.13'=@{}}
    $libraries[$Owner]=@{}
    $target=[ordered]@{'native-only/1.0.0'=@{native=@{'native.dll'=@{}}}}
    $target[$Owner]=@{runtime=@{'System.Security.Cryptography.Pkcs.dll'=@{}}}
    $graph=[ordered]@{runtimeTarget=@{name='.NETCoreApp,Version=v10.0/win-x64'};libraries=$libraries;targets=@{'.NETCoreApp,Version=v10.0/win-x64'=$target}}
    $manifest=Join-Path $dir 'MANIFEST.sha256'
    ((Get-FileHash $dll).Hash.ToLowerInvariant()+'  apps/api/System.Security.Cryptography.Pkcs.dll')|Set-Content $manifest
    return @{dir=$dir;deps=(Join-Path $dir 'app.deps.json');manifest=$manifest;graph=$graph}
}
function Invoke-Case([string]$Name,$Case,[bool]$Reject=$false){
    $Case.graph|ConvertTo-Json -Depth 30|Set-Content $Case.deps
    $failed=$false
    try{Assert-FactoryConnectPkcsPayload -AppRoot $Case.dir -DepsPath $Case.deps -ManifestPath $Case.manifest -Application 'api'|Out-Null}catch{$failed=$true}
    if($failed -ne $Reject){throw "Unexpected result for $Name; rejectionExpected=$Reject"}
    $results[$Name]='PASS';Write-Host "$Name PASS"
}
try{
    Invoke-Case PatchedNuGet (New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21')
    Invoke-Case PatchedRuntime (New-Case 'runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.13' '10.0.13')
    Invoke-Case AffectedNuGet (New-Case 'System.Security.Cryptography.Pkcs/9.0.13' '9.0.13') $true
    $c=New-Case 'runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.7' '10.0.7';$c.graph.libraries.Remove('runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.13');Invoke-Case AffectedRuntime $c $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';$c.graph.libraries['System.Security.Cryptography.Pkcs/9.0.13']=@{};Invoke-Case UnusedLibraryIsNotOwner $c
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';$c.graph.targets['.NETCoreApp,Version=v10.0/win-x64']['runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.13']=@{runtime=@{'System.Security.Cryptography.Pkcs.dll'=@{}}};Invoke-Case DuplicateOwner $c $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';$c.graph.targets['.NETCoreApp,Version=v10.0/win-x64'].Remove('System.Security.Cryptography.Pkcs/9.0.21');Invoke-Case MissingOwner $c $true
    Invoke-Case ProductOwnerMismatch (New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.13') $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';Set-Content $c.manifest 'wrong';Invoke-Case ManifestMismatch $c $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';Remove-Item (Join-Path $c.dir 'System.Security.Cryptography.Pkcs.dll');Invoke-Case MissingDll $c $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';$c.graph.libraries.Remove('runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13');$c.graph.libraries['runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.7']=@{};Invoke-Case UnsafeCoreRuntimeWithPatchedPkcs $c $true
    $c=New-Case 'System.Security.Cryptography.Pkcs/9.0.21' '9.0.21';$c.graph.runtimeTarget.name='.NETCoreApp,Version=v10.0/linux-x64';Invoke-Case WrongTarget $c $true
}finally{if(Test-Path $root){Remove-Item $root -Recurse -Force}}
[pscustomobject]@{Mode=$(if($Executable){'Compiled Windows assembly fixtures'}else{'Controlled identity fixtures'});Cases=[pscustomobject]$results}
