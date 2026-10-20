param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$env:DOTNET_ROOT = Join-Path $root 'local/tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $root 'local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root 'local/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$managed = Join-Path $GameDirectory 'shapez 2_Data/Managed'
& (Join-Path $env:DOTNET_ROOT 'dotnet.exe') build (Join-Path $root 'mod/src/Shapez2Multiplayer.csproj') -c Release "-p:SPZ2_PATH=$managed" "-p:UiLibPath=$root/local/build/uilib/Shapez2UILib.dll" -p:CoopQa=true "-p:OutputPath=$root/local/qa/build" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'QA build failed' }
