param([string]$Version = '8.0.419')
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolsDir = Join-Path $root 'local/tools'
New-Item -ItemType Directory -Force $toolsDir | Out-Null
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
$sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $Version)[0]
if (!$sdk) { throw "SDK $Version absent from official release metadata" }
$file = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' })[0]
if (!$file -or !([uri]$file.url).Host.EndsWith('.microsoft.com')) { throw 'Invalid official download metadata' }
$archive = Join-Path $toolsDir "dotnet-sdk-$Version-win-x64.zip"
Invoke-WebRequest $file.url -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 mismatch' }
$destination = Join-Path $toolsDir 'dotnet'
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
@{ version=$Version; url=$file.url; sha512=$file.hash } | ConvertTo-Json | Set-Content (Join-Path $toolsDir 'sdk-provenance.json')
& (Join-Path $destination 'dotnet.exe') --info
if ($LASTEXITCODE -ne 0) { throw 'SDK verification failed' }
