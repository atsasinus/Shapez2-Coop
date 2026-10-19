param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$env:DOTNET_ROOT = Join-Path $root 'local/tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $root 'local/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $root 'local/nuget'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$managed = Join-Path $GameDirectory 'shapez 2_Data/Managed'
if (!(Test-Path (Join-Path $managed 'SPZGameAssembly.dll'))) { throw 'Game managed assemblies not found' }
$uiOutput = Join-Path $root 'local/build/uilib'
& $dotnet build (Join-Path $root 'local/Shapez2UILib/Shapez2UILib.csproj') -c Release "-p:SPZ2_PATH=$managed" "-p:OutputPath=$uiOutput" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'UI dependency build failed' }
& $dotnet build (Join-Path $root 'mod/src/Shapez2Multiplayer.csproj') -c Release "-p:SPZ2_PATH=$managed" "-p:UiLibPath=$uiOutput/Shapez2UILib.dll" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
