param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dirty = & git -c "safe.directory=$root" -C $root status --porcelain -- mod AGENTS.md
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the mod sources before packaging, then run a normal Release build.' }
$commit = (& git -c "safe.directory=$root" -C $root rev-parse HEAD).Trim()
$build = Join-Path $root 'mod/src/bin/Release'
$builtInputs = Get-Content (Join-Path $build 'build-inputs.json') -Raw | ConvertFrom-Json
$inputs = @(& (Join-Path $PSScriptRoot 'build-inputs.ps1') -Root $root)
if ((Compare-Object @($builtInputs.sources) $inputs -Property file,sha256) -or
    $builtInputs.moduleSha256 -ne (Get-FileHash (Join-Path $build 'Shapez2Multiplayer.dll')).Hash -or
    $builtInputs.protocolSha256 -ne (Get-FileHash (Join-Path $build 'Shapez2Coop.Protocol.dll')).Hash) {
    throw 'Release output does not match current source inputs. Run build.ps1 again.'
}
$manifest = Get-Content (Join-Path $build 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Version -notmatch '^[0-9A-Za-z.-]+$') { throw 'Invalid package version' }
# Explicit allowlist. Game assemblies, Workshop dependencies and build tools cannot enter the package.
$runtime = @('Shapez2Multiplayer.dll','Shapez2Coop.Protocol.dll','ENet-CSharp.dll','enet.dll',
    'K4os.Compression.LZ4.dll','K4os.Hash.xxHash.dll','System.Runtime.CompilerServices.Unsafe.dll',
    'manifest.json','translations.json')
$notices = @('ENet-CSharp-MIT.txt','ENet-native-MIT.txt','K4os-LZ4-MIT.txt','LZ4-BSD.txt',
    'K4os-xxHash-MIT.txt','xxHash-BSD.txt','dotnet-runtime-MIT.txt','dotnet-runtime-THIRD-PARTY.txt','SOURCES.md')
foreach ($name in $runtime) {
    if (!(Test-Path -LiteralPath (Join-Path $build $name) -PathType Leaf)) { throw "Missing Release file: $name" }
}
$deps = Get-Content (Join-Path $root 'mod/DEPENDENCIES.json') -Raw | ConvertFrom-Json
$enet = $deps.redistributedRuntime | Where-Object name -eq 'ENet-CSharp'
if ((Get-FileHash (Join-Path $build 'ENet-CSharp.dll')).Hash -ne $enet.managedSha256 -or
    (Get-FileHash (Join-Path $build 'enet.dll')).Hash -ne $enet.nativeSha256) { throw 'ENet provenance mismatch' }
Add-Type -Path (Join-Path $root 'local/nuget/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $build 'Shapez2Multiplayer.dll'))
try {
    if ($assembly.MainModule.Types | Where-Object { $_.Name -like 'CoopQa*' -or
        ($_.Name -eq 'CoopSession' -and ($_.Methods | Where-Object Name -like 'Qa*')) }) { throw 'QA code found in normal package' }
    $mvid = $assembly.MainModule.Mvid.ToString()
} finally { $assembly.Dispose() }
$stage = Join-Path $root ('local/package-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $stage 'bundle'
$modFolder = Join-Path $bundle 'Shapez2Coop'
$source = Join-Path $stage 'source'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $modFolder,$source,$dist -Force | Out-Null
foreach ($name in $runtime) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination $modFolder }
foreach ($name in @('README.md','TESTING.md','LICENSE','THIRD-PARTY.md','DEPENDENCIES.json')) {
    Copy-Item -LiteralPath (Join-Path $root "mod/$name") -Destination $modFolder
}
$licenseFolder = Join-Path $modFolder 'licenses'
New-Item -ItemType Directory -Path $licenseFolder | Out-Null
foreach ($name in $notices) { Copy-Item -LiteralPath (Join-Path $root "mod/licenses/$name") -Destination $licenseFolder }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $build 'build-inputs.json') -Destination $modFolder
# Source inventory comes from Git, restricted to development files; ignored game/QA data is never scanned.
$files = & git -c "safe.directory=$root" -C $root ls-files -- mod AGENTS.md
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source' }
foreach ($relative in $files) {
    if ($relative -notmatch '^(mod/|AGENTS\.md$)' -or $relative -match '(^|/)(bin|obj|local|references)/' -or
        ([IO.Path]::GetExtension($relative) -notin @('.cs','.csproj','.json','.ps1','.md','.txt',''))) {
        throw "Unexpected source archive entry: $relative"
    }
    $target = Join-Path $source $relative
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target
}
# These two MIT inputs are required by the project and have separately verified hashes.
foreach ($name in @('ENet-CSharp.dll','enet.dll')) {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $source "mod/src/$name")
}
$inventory = foreach ($name in $runtime) { @{ file=$name; sha256=(Get-FileHash (Join-Path $modFolder $name)).Hash } }
@{ version=$manifest.Version; sourceCommit=$commit; moduleId=$mvid; files=@($inventory) } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $modFolder 'BUILD.json')
$sourceName = "Shapez2Coop-$($manifest.Version)-source.zip"
$sourceZip = Join-Path $bundle $sourceName
[IO.Compression.ZipFile]::CreateFromDirectory($source, $sourceZip)
$archive = Join-Path $dist "Shapez2Coop-$($manifest.Version)-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw 'Package already exists; archive it explicitly or increase the version.' }
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $archive)
$hash = (Get-FileHash $archive).Hash
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256"
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
