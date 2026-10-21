param([ValidateSet('profile','client-profile','observer-2-profile','observer-3-profile')][string]$ProfileName = 'profile')
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$profile = Join-Path $root "local/qa/$ProfileName"
$mods = Join-Path $profile 'mods'
$mp = Join-Path $mods 'Shapez2Multiplayer'
$ui = Join-Path $mods 'Shapez2UILib'
$harmony = Join-Path $mods 'HarmonyX'
New-Item -ItemType Directory -Force $mp,$ui,$harmony | Out-Null
foreach ($name in @('Shapez2Multiplayer.dll','Shapez2Coop.Protocol.dll','ENet-CSharp.dll','enet.dll','K4os.Compression.LZ4.dll','K4os.Hash.xxHash.dll','System.Runtime.CompilerServices.Unsafe.dll','translations.json')) {
    Copy-Item -LiteralPath (Join-Path $root "local/qa/build/$name") -Destination $mp -Force
}
Copy-Item (Join-Path $root 'local/build/uilib/Shapez2UILib.dll') $ui -Force
$uiManifest = Get-Content (Join-Path $root 'local/Shapez2UILib/manifest.json') -Raw | ConvertFrom-Json
$uiManifest.Dependencies[0].ModId = 'local:HarmonyX'
$uiManifest.GameVersionSupportRange = '*'
$uiManifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ui 'manifest.json')
$manifest = Get-Content (Join-Path $root 'local/qa/build/manifest.json') -Raw | ConvertFrom-Json
$manifest.Dependencies = @(@{ ModId='local:HarmonyX'; ModTitle='HarmonyX'; Version='*' }, @{ ModId='local:Shapez2UILib'; ModTitle='Shapez2UILib'; Version='*' })
$manifest.GameVersionSupportRange = '*'
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $mp 'manifest.json')
$packages = @('harmonyx/2.16.1','monomod.runtimedetour/25.3.4','monomod.utils/25.0.12','monomod.core/1.3.4','monomod.backports/1.1.2','monomod.ilhelpers/1.1.0','mono.cecil/0.11.6','system.text.json/5.0.2','system.text.encodings.web/5.0.1','microsoft.bcl.asyncinterfaces/5.0.0','system.runtime.compilerservices.unsafe/6.0.0')
foreach ($package in $packages) {
    $framework = if (Test-Path (Join-Path $root "local/nuget/$package/lib/net452")) { 'net452' } else { 'netstandard2.0' }
    Copy-Item (Join-Path $root "local/nuget/$package/lib/$framework/*.dll") $harmony -Force
}
Copy-Item (Join-Path $root 'local/nuget/system.valuetuple/4.5.0/lib/net461/System.ValueTuple.dll') $harmony -Force
@{ Version='2.16.1'; Description='Local QA dependency'; Author='HarmonyX authors'; GameVersionSupportRange='*'; Assemblies=@('0Harmony.dll'); Dependencies=@(); Conflicts=@() } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $harmony 'manifest.json')
# The normal package depends on the Workshop resolver. QA without Steam only needs this local alias.
$managed = Join-Path $root 'local/qa/game/shapez 2_Data/Managed'
Copy-Item (Join-Path $managed 'Facepunch.Steamworks.Win64.dll') (Join-Path $managed 'Facepunch.Steamworks.dll') -Force
Write-Output "QA dependencies prepared in $profile"
