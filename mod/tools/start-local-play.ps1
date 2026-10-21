$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$qaRoot = [IO.Path]::GetFullPath((Join-Path $root 'local/qa'))
$exe = Join-Path $qaRoot 'game/shapez 2.exe'
if (Get-Process -Name 'shapez 2' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'An isolated game is already running. Close it before starting another pair.'
}
$release = Join-Path $root 'mod/src/bin/Release'
$built = Get-Content (Join-Path $release 'build-inputs.json') -Raw | ConvertFrom-Json
if ((Get-FileHash (Join-Path $release 'Shapez2Multiplayer.dll')).Hash -ne $built.moduleSha256) { throw 'Release hash mismatch' }
$session = Join-Path $qaRoot ('manual-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $session | Out-Null
$oldProfile = $env:SHAPEZ2_COOP_QA_PROFILE
$instances = @()
try {
    foreach ($role in @('host','client')) {
        $profile = [IO.Path]::GetFullPath((Join-Path $session $role))
        if (!$profile.StartsWith($qaRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Profile escaped local/qa'
        }
        $mods = Join-Path $profile 'mods'
        $mp = Join-Path $mods 'Shapez2Multiplayer'
        New-Item -ItemType Directory -Path $mp -Force | Out-Null
        foreach ($dependency in @('HarmonyX','Shapez2UILib')) {
            Copy-Item -LiteralPath (Join-Path $qaRoot "profile/mods/$dependency") -Destination $mods -Recurse
        }
        foreach ($name in @('Shapez2Multiplayer.dll','Shapez2Coop.Protocol.dll','ENet-CSharp.dll','enet.dll',
            'K4os.Compression.LZ4.dll','K4os.Hash.xxHash.dll','System.Runtime.CompilerServices.Unsafe.dll','translations.json')) {
            Copy-Item -LiteralPath (Join-Path $release $name) -Destination $mp
        }
        $manifest = Get-Content (Join-Path $release 'manifest.json') -Raw | ConvertFrom-Json
        $manifest.Dependencies = @(@{ ModId='local:HarmonyX'; ModTitle='HarmonyX'; Version='*' },
            @{ ModId='local:Shapez2UILib'; ModTitle='Shapez2UILib'; Version='*' })
        $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $mp 'manifest.json')
        @{ SimulationHz=20; VisualPrediction=$true } | ConvertTo-Json | Set-Content (Join-Path $profile 'coop-settings.json')
        $env:SHAPEZ2_COOP_QA_PROFILE = $profile
        $log = Join-Path $session "$role.log"
        # The user requested visible, interactive windows. No batchmode or QA-bot role.
        $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Normal -ArgumentList @(
            '-force-d3d11','-screen-fullscreen','0','-screen-width','1100','-screen-height','740',
            '--disable-store-sdk','--qa','-logFile',$log
        ) -PassThru
        $instances += [pscustomobject]@{ role=$role; pid=$process.Id; profile=$profile; log=$log }
        Write-Output ("Started $role window: PID " + $process.Id)
    }
} finally { $env:SHAPEZ2_COOP_QA_PROFILE = $oldProfile }
$instances | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $session 'instances.json')
Write-Output ("Session: " + $session)
