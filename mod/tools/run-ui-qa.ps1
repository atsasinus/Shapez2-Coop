param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$qa = Join-Path $root 'local/qa'
$run = Join-Path $qa ('ui-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$game = Join-Path $run 'game'
$profile = Join-Path $run 'profile'
$mods = Join-Path $profile 'mods'
$mp = Join-Path $mods 'Shapez2Multiplayer'
New-Item -ItemType Directory -Path $game,$mp -Force | Out-Null
# Separate executable/profiles; share only the already isolated read-only game data.
foreach ($name in @('shapez 2.exe','UnityPlayer.dll','UnityCrashHandler64.exe')) {
    Copy-Item -LiteralPath (Join-Path $qa "game/$name") -Destination $game
}
foreach ($name in @('shapez 2_Data','MonoBleedingEdge','D3D12')) {
    New-Item -ItemType Junction -Path (Join-Path $game $name) -Target (Join-Path $qa "game/$name") | Out-Null
}
foreach ($dependency in @('HarmonyX','Shapez2UILib')) {
    Copy-Item -LiteralPath (Join-Path $qa "profile/mods/$dependency") -Destination $mods -Recurse
}
foreach ($name in @('Shapez2Multiplayer.dll','Shapez2Coop.Protocol.dll','ENet-CSharp.dll','enet.dll',
    'K4os.Compression.LZ4.dll','K4os.Hash.xxHash.dll','System.Runtime.CompilerServices.Unsafe.dll','translations.json')) {
    Copy-Item -LiteralPath (Join-Path $qa "build/$name") -Destination $mp
}
$manifest = Get-Content (Join-Path $qa 'build/manifest.json') -Raw | ConvertFrom-Json
$manifest.Dependencies = @(@{ModId='local:HarmonyX';ModTitle='HarmonyX';Version='*'},
    @{ModId='local:Shapez2UILib';ModTitle='Shapez2UILib';Version='*'})
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $mp 'manifest.json')
$oldProfile = $env:SHAPEZ2_COOP_QA_PROFILE
$exe = Join-Path $game 'shapez 2.exe'
$logFile = Join-Path $run 'ui.log'
$process = $null
try {
    $env:SHAPEZ2_COOP_QA_PROFILE = $profile
    $process = Start-Process -FilePath $exe -WorkingDirectory $game -WindowStyle Hidden -ArgumentList @(
        '-force-d3d11','-screen-fullscreen','0','-screen-width','1100','-screen-height','740',
        '--disable-store-sdk','--qa','--coop-qa=ui','-logFile',$logFile) -PassThru
    Write-Output "UI QA: $run"
    if (!$process.WaitForExit(180000)) { throw 'UI gate timed out' }
    $log = Get-Content -LiteralPath $logFile -Raw
    $log -split "`n" | Select-String 'QA:|Co-op main menu|Exception:' | Select-Object -Last 30 | ForEach-Object { $_.Line }
    if ($process.ExitCode -ne 0 -or $log -notmatch 'QA:UI_PASS' -or $log -match 'QA:FAIL|Exception:') {
        throw 'UI gate failed; inspect its log'
    }
    Write-Output 'PASS: menu entry, connection view, pause labels, settings controls, layout and screenshots.'
} finally {
    $env:SHAPEZ2_COOP_QA_PROFILE = $oldProfile
    if ($process -and !$process.HasExited -and $process.Path -eq $exe) { $process.Kill(); $process.WaitForExit() }
}
