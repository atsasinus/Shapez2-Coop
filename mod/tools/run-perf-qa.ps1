param([Parameter(Mandatory=$true)][string]$SaveFile,
      [ValidateRange(10,60)][int]$Seconds = 15,
      [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label = 'baseline',
      [ValidateRange(120,600)][int]$TimeoutSeconds = 360)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$qa = [IO.Path]::GetFullPath((Join-Path $root 'local/qa'))
$inputFile = [IO.Path]::GetFullPath($SaveFile)
if (!$inputFile.StartsWith($qa + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an isolated save copy under local/qa'
}
if (Get-Process -Name 'shapez 2' -ErrorAction SilentlyContinue) { throw 'Close games before a controlled performance comparison' }
$run = Join-Path $qa ("perf-$Label-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$game = Join-Path $run 'game'
New-Item -ItemType Directory -Path $game | Out-Null
Copy-Item -LiteralPath $inputFile -Destination (Join-Path $run 'input.spz2')
$inputHash=(Get-FileHash -LiteralPath $inputFile).Hash
if ((Get-FileHash (Join-Path $run 'input.spz2')).Hash -ne $inputHash) { throw 'Fixture hash mismatch' }
foreach ($name in @('shapez 2.exe','UnityPlayer.dll','UnityCrashHandler64.exe')) {
    Copy-Item -LiteralPath (Join-Path $qa "game/$name") -Destination $game
}
foreach ($name in @('shapez 2_Data','MonoBleedingEdge','D3D12')) {
    New-Item -ItemType Junction -Path (Join-Path $game $name) -Target (Join-Path $qa "game/$name") | Out-Null
}
$runtime = @('Shapez2Multiplayer.dll','Shapez2Coop.Protocol.dll','ENet-CSharp.dll','enet.dll',
    'K4os.Compression.LZ4.dll','K4os.Hash.xxHash.dll','System.Runtime.CompilerServices.Unsafe.dll','translations.json')
foreach ($role in @('host','client')) {
    $mods=Join-Path $run "$role/mods"; $mp=Join-Path $mods 'Shapez2Multiplayer'
    New-Item -ItemType Directory -Path $mp -Force | Out-Null
    foreach ($dep in @('HarmonyX','Shapez2UILib')) { Copy-Item -LiteralPath (Join-Path $qa "profile/mods/$dep") -Destination $mods -Recurse }
    foreach ($name in $runtime) { Copy-Item -LiteralPath (Join-Path $qa "build/$name") -Destination $mp }
    $manifest=Get-Content (Join-Path $qa 'build/manifest.json') -Raw | ConvertFrom-Json
    $manifest.Dependencies=@(@{ModId='local:HarmonyX';ModTitle='HarmonyX';Version='*'},@{ModId='local:Shapez2UILib';ModTitle='Shapez2UILib';Version='*'})
    $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $mp 'manifest.json')
}
$oldProfile=$env:SHAPEZ2_COOP_QA_PROFILE; $oldStop=$env:SHAPEZ2_COOP_QA_STOP_FILE
$oldSave=$env:SHAPEZ2_COOP_PERF_SAVE; $oldSeconds=$env:SHAPEZ2_COOP_PERF_SECONDS
$env:SHAPEZ2_COOP_PERF_SAVE=Join-Path $run 'input.spz2'; $env:SHAPEZ2_COOP_PERF_SECONDS="$Seconds"
$exe=Join-Path $game 'shapez 2.exe'; $processes=@{}; $problem=''; $passed=$false; $exitCodes=@{}
function Read-Log([string]$role) {
    $file=Join-Path $run "$role.log"
    if (!(Test-Path -LiteralPath $file)) {return ''}
    if ((Get-Item -LiteralPath $file).Length -gt 32MB) {throw 'Perf log limit exceeded'}
    $s=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $r=[IO.StreamReader]::new($s); try {return $r.ReadToEnd()} finally {$r.Dispose()}
}
function Start-Role([string]$role) {
    $env:SHAPEZ2_COOP_QA_PROFILE=Join-Path $run $role
    $env:SHAPEZ2_COOP_QA_STOP_FILE=Join-Path $run "$role.stop"
    $processes[$role]=Start-Process -FilePath $exe -WorkingDirectory $game -WindowStyle Hidden -ArgumentList @(
        '-batchmode','-force-d3d11','-screen-fullscreen','0','-screen-width','1100','-screen-height','740',
        '--disable-store-sdk','--qa',"--coop-qa=perf-$role",'-logFile',(Join-Path $run "$role.log")) -PassThru
}
try {
    Start-Role 'host'; Write-Output "Performance QA: $run"
    $until=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $hostLog=Read-Log 'host'
        if (!$processes.ContainsKey('client') -and $hostLog -match 'PERF:JOIN_READY') { Start-Role 'client'; Write-Output 'Join client started.' }
        if ($hostLog -match 'PERF:RESULT' -or $processes.host.HasExited) {break}
        if ($processes.ContainsKey('client') -and $processes.client.HasExited) {throw 'Client exited before benchmark completion'}
        if ([DateTime]::UtcNow -gt $until) {throw 'Perf scenario timed out'}
        Start-Sleep -Seconds 1
    } while ($true)
    $hostResult=Get-Content (Join-Path $run 'host/perf-result.json') -Raw | ConvertFrom-Json
    $clientResult=Get-Content (Join-Path $run 'client/perf-result.json') -Raw | ConvertFrom-Json
    if ($hostResult.status -ne 'pass' -or $clientResult.status -ne 'pass') {throw "Scenario results: host=$($hostResult.status) client=$($clientResult.status)"}
    $hostChecks=[regex]::Matches($hostLog,'QA:CHECKSUM_HOST (step=\d+ digest=[A-F0-9-]+)') | ForEach-Object {$_.Groups[1].Value}
    $clientLog=Read-Log 'client'
    $clientChecks=[regex]::Matches($clientLog,'QA:CHECKSUM_CLIENT (step=\d+ digest=[A-F0-9-]+)') | ForEach-Object {$_.Groups[1].Value}
    $shared=@($clientChecks | Where-Object {$hostChecks -contains $_})
    if ($shared.Count -lt 2 -or $hostLog -notmatch 'QA:SNAPSHOT_MATCH') {throw 'Insufficient agreed simulation boundaries'}
    $passed=$true
} catch { $problem=$_.Exception.Message }
finally {
    foreach ($role in $processes.Keys) {New-Item -ItemType File -Path (Join-Path $run "$role.stop") -Force | Out-Null}
    foreach ($role in $processes.Keys) {
        $p=$processes[$role]
        if (!$p.WaitForExit(30000)) { $problem+=" $role shutdown timeout."; $passed=$false; if ($p.Path -eq $exe) {$p.Kill();$p.WaitForExit()} }
        $exitCodes[$role]=$p.ExitCode
        if ($p.ExitCode -ne 0 -or (Read-Log $role) -match 'QA:FAIL|Exception:') {$passed=$false;$problem+=" $role runtime/shutdown error."}
    }
    $env:SHAPEZ2_COOP_QA_PROFILE=$oldProfile; $env:SHAPEZ2_COOP_QA_STOP_FILE=$oldStop
    $env:SHAPEZ2_COOP_PERF_SAVE=$oldSave; $env:SHAPEZ2_COOP_PERF_SECONDS=$oldSeconds
    @{passed=$passed;problem=$problem;label=$Label;seconds=$Seconds;inputSha256=$inputHash;exitCodes=$exitCodes;
      modSha256=(Get-FileHash (Join-Path $run 'host/mods/Shapez2Multiplayer/Shapez2Multiplayer.dll')).Hash;logs=$run} |
      ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'result.json')
    Get-Content (Join-Path $run 'result.json')
}
if (!$passed) {throw "Performance QA failed: $problem"}
