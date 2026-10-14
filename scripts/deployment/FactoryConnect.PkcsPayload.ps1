# Release-side verification; never loads or executes the candidate assembly.
function Get-FactoryConnectPkcsMetadata([string]$Path) {
    $assembly=[Reflection.AssemblyName]::GetAssemblyName($Path)
    $file=[Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    [pscustomobject]@{Name=$assembly.Name;AssemblyVersion=$assembly.Version.ToString();ProductVersion=$file.ProductVersion;FileVersion=$file.FileVersion}
}
function Assert-FactoryConnectPatchedPkcsVersion([string]$Version) {
    if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw "Unrecognized/prerelease Pkcs version: $Version" }
    $value=[version]$Version
    if ($value.Minor -ne 0 -or -not (($value.Major -eq 9 -and $value.Build -ge 21) -or ($value.Major -eq 10 -and $value.Build -ge 13))) {
        throw "Affected or unsupported Pkcs version: $Version. Require 9.0.21+ or 10.0.13+ in the supported series."
    }
}
function Assert-FactoryConnectPkcsPayload {
    param([string]$AppRoot,[string]$DepsPath,[string]$ManifestPath,[string]$Application)
    $graph=Get-Content -Raw -LiteralPath $DepsPath|ConvertFrom-Json
    $targetName=[string]$graph.runtimeTarget.name
    if ($targetName -cne '.NETCoreApp,Version=v10.0/win-x64') { throw 'Unexpected release runtime target.' }
    $requiredPacks=@('Microsoft.NETCore.App')
    if ($Application -in @('api','dashboard')) { $requiredPacks+='Microsoft.AspNetCore.App' }
    foreach ($pack in $requiredPacks) {
        $prefix="runtimepack.$pack.Runtime.win-x64/"
        $entries=@($graph.libraries.PSObject.Properties|Where-Object{$_.Name.StartsWith($prefix,[StringComparison]::Ordinal)})
        if ($entries.Count -ne 1) { throw "Missing/ambiguous runtime-pack identity for $pack." }
        $versionText=$entries[0].Name.Substring($prefix.Length)
        if ($versionText -cnotmatch '^10\.0\.\d+$' -or [version]$versionText -lt [version]'10.0.13') { throw "Unpatched runtime pack: $($entries[0].Name)" }
    }
    $target=$graph.targets.PSObject.Properties[$targetName]
    if ($null -eq $target) { throw 'Selected dependency target is missing.' }
    $owners=@(
        foreach ($library in $target.Value.PSObject.Properties) {
            $property=$library.Value.PSObject.Properties['runtime']
            if ($null -eq $property -or $null -eq $property.Value) { continue }
            foreach ($asset in $property.Value.PSObject.Properties) {
                if (($asset.Name -split '/')[-1] -ceq 'System.Security.Cryptography.Pkcs.dll') {
                    [pscustomobject]@{Library=$library.Name;Asset=$asset}
                }
            }
        }
    )
    if ($owners.Count -ne 1) { throw 'Pkcs published asset ownership is absent or ambiguous.' }
    $owner=$owners[0]
    if ($owner.Asset.Name -cne 'System.Security.Cryptography.Pkcs.dll' -or $null -eq $graph.libraries.PSObject.Properties[$owner.Library]) { throw 'Pkcs owner or canonical asset path is invalid.' }
    if ($owner.Library -cmatch '^System\.Security\.Cryptography\.Pkcs/(\d+\.\d+\.\d+)$') { $version=$Matches[1];$kind='NuGet' }
    elseif ($owner.Library -cmatch '^runtimepack\.Microsoft\.AspNetCore\.App\.Runtime\.win-x64/(\d+\.\d+\.\d+)$') { $version=$Matches[1];$kind='RuntimePack' }
    else { throw "Unrecognized Pkcs asset owner: $($owner.Library)" }
    Assert-FactoryConnectPatchedPkcsVersion $version
    $dll=Join-Path $AppRoot 'System.Security.Cryptography.Pkcs.dll'
    $identity=Get-FactoryConnectPkcsMetadata $dll
    if ($identity.Name -cne 'System.Security.Cryptography.Pkcs' -or
        $identity.AssemblyVersion -cne "$(([version]$version).Major).0.0.0" -or
        $identity.ProductVersion -cnotmatch '^([0-9]+\.[0-9]+\.[0-9]+)(?:\+[^\s]+)?$') { throw 'Pkcs assembly identity is invalid.' }
    if ($Matches[1] -cne $version) { throw 'Pkcs assembly product version does not match its published owner.' }
    foreach ($pair in @(@{Name='assemblyVersion';Value=$identity.AssemblyVersion},@{Name='fileVersion';Value=$identity.FileVersion})) {
        $property=$owner.Asset.Value.PSObject.Properties[$pair.Name]
        if ($null -ne $property -and [string]$property.Value -cne [string]$pair.Value) { throw "Pkcs $($pair.Name) disagrees with dependency asset metadata." }
    }
    $hash=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $line="$hash  apps/$Application/System.Security.Cryptography.Pkcs.dll"
    if (@(Get-Content -LiteralPath $ManifestPath|Where-Object{$_ -ceq $line}).Count -ne 1) { throw 'Pkcs assembly lacks exact manifest coverage.' }
    [pscustomobject]@{PkcsPayload='PASS';Application=$Application;Owner=$owner.Library;OwnerKind=$kind;Version=$version;Sha256=$hash}
}
