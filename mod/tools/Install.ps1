param([Parameter(Mandatory=$true)][string]$ModsDirectory)
$ErrorActionPreference = 'Stop'
$mods = [IO.Path]::GetFullPath($ModsDirectory)
if ((Split-Path $mods -Leaf) -ne 'mods') { throw 'Specify the game data mods directory, not the Steam installation.' }
$source = Join-Path $PSScriptRoot 'Shapez2Coop'
if (!(Test-Path (Join-Path $source 'manifest.json'))) { throw 'Run Install.ps1 from the extracted distribution archive.' }
$target = Join-Path $mods 'Shapez2Coop'
if (Test-Path -LiteralPath $target) { throw 'Existing mod folder found. Back it up or remove it manually before installing.' }
New-Item -ItemType Directory -Path $mods -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Recurse
Write-Output "Installed alpha in $target. Enable it and its Workshop dependencies in the game."
