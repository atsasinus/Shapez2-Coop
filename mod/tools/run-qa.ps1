param([ValidateRange(60,600)][int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$qa = Join-Path $root 'local/qa'
$exe = Join-Path $qa 'game/shapez 2.exe'
if (Get-Process -Name 'shapez 2' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'A QA game is already running. Finish that run first.'
}
$run = Join-Path $qa ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
$processes = @{}
$oldProfile = $env:SHAPEZ2_COOP_QA_PROFILE
$oldStop = $env:SHAPEZ2_COOP_QA_STOP_FILE
$passed = $false
$cleanExit = $true
$problem = ''
$exitCodes = @{}
function Read-Log([string]$role) {
    $file = Join-Path $run "$role.log"
    if (!(Test-Path -LiteralPath $file)) { return '' }
    if ((Get-Item -LiteralPath $file).Length -gt 16MB) { throw "$role log exceeded QA limit" }
    $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Start-Role([string]$role, [string]$profile) {
    $env:SHAPEZ2_COOP_QA_PROFILE = Join-Path $qa $profile
    $env:SHAPEZ2_COOP_QA_STOP_FILE = Join-Path $run "$role.stop"
    $processes[$role] = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -ArgumentList @(
        '-batchmode','-force-d3d11','--disable-store-sdk','--qa',"--coop-qa=$role",'-logFile',(Join-Path $run "$role.log")
    ) -PassThru
}
try {
    Start-Role 'host' 'profile'
    Write-Output "QA run: $run"
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $hostLog = Read-Log 'host'
        if ($processes.host.HasExited -or $hostLog -match 'QA:FAIL|Stopped:') { throw 'Host failed during startup' }
        if ($hostLog -match 'QA:HOST_READY') { break }
        if ([DateTime]::UtcNow -gt $until) { throw 'Host startup timed out' }
        Start-Sleep -Seconds 2
    } while ($true)
    Start-Role 'client' 'client-profile'
    Write-Output 'Host ready; client started.'
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $hostLog = Read-Log 'host'
        $clientLog = Read-Log 'client'
        if ($processes.host.HasExited -or $processes.client.HasExited -or "$hostLog`n$clientLog" -match 'QA:FAIL|Stopped:') {
            throw 'Runtime fault or unexpected process exit'
        }
        if ($hostLog -match 'QA:STEPS_PASS' -and $clientLog -match 'QA:STEPS_PASS') { break }
        if ([DateTime]::UtcNow -gt $until) { throw 'Snapshot/step test timed out' }
        Start-Sleep -Seconds 2
    } while ($true)
    $hostChecks = [regex]::Matches($hostLog, 'QA:CHECKSUM_HOST (step=\d+ digest=[A-F0-9-]+)') |
        ForEach-Object { $_.Groups[1].Value }
    $clientChecks = [regex]::Matches($clientLog, 'QA:CHECKSUM_CLIENT (step=\d+ digest=[A-F0-9-]+)') |
        ForEach-Object { $_.Groups[1].Value }
    $matches = @($clientChecks | Where-Object { $hostChecks -contains $_ })
    if ($matches.Count -lt 2 -or $hostLog -notmatch 'QA:SNAPSHOT_MATCH') { throw 'Insufficient shared-step checksum evidence' }
    if ($clientLog -notmatch 'QA:ACTIONS_PASS' -or $clientLog -notmatch 'QA:STALE_DELETE_PRESERVED' -or
        $clientLog -notmatch 'QA:INTENT_REJECT') { throw 'Missing client action/rejection evidence' }
    foreach ($log in @($hostLog,$clientLog)) {
        if ($log -notmatch 'QA:OUTPUT_CLEARED' -or $log -notmatch 'QA:OUTPUT_REFILLED') { throw 'Missing production evidence' }
    }
    if ("$hostLog`n$clientLog" -match '(?m)^.*Exception[: ]') { throw 'Network checks completed, but runtime exceptions remain; inspect logs' }
    $passed = $true
} catch {
    $problem = $_.Exception.Message
} finally {
    foreach ($role in $processes.Keys) { New-Item -ItemType File -Path (Join-Path $run "$role.stop") -Force | Out-Null }
    foreach ($role in $processes.Keys) {
        $process = $processes[$role]
        if (!$process.WaitForExit(30000)) {
            $cleanExit = $false
            $problem += " $role shutdown timed out."
            if ($process.Path -eq $exe) { $process.Kill(); $process.WaitForExit() }
        }
        $exitCodes[$role] = $process.ExitCode
        if ($process.ExitCode -ne 0) { $cleanExit = $false; $problem += " $role exit code $($process.ExitCode)." }
        if ((Read-Log $role) -match '(?m)^.*Exception[: ]|QA:FAIL|Stopped:') {
            $cleanExit = $false
            $problem += " $role log contains a runtime/shutdown fault."
        }
    }
    $env:SHAPEZ2_COOP_QA_PROFILE = $oldProfile
    $env:SHAPEZ2_COOP_QA_STOP_FILE = $oldStop
    $result = @{ passed=($passed -and $cleanExit); cleanExit=$cleanExit; exitCodes=$exitCodes; problem=$problem; logs=$run }
    $result | ConvertTo-Json | Set-Content (Join-Path $run 'result.json')
    $result | ConvertTo-Json | Write-Output
}
if (!$passed -or !$cleanExit) { throw "QA gate failed: $problem" }
