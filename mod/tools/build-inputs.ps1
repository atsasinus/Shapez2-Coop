param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
$paths = & git -c "safe.directory=$Root" -C $Root ls-files -co --exclude-standard -- mod/src mod/protocol
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate build inputs' }
foreach ($relative in ($paths | Sort-Object -Unique)) {
    if ([IO.Path]::GetExtension($relative) -in @('.cs','.csproj','.json')) {
        [pscustomobject]@{ file=$relative; sha256=(Get-FileHash -LiteralPath (Join-Path $Root $relative)).Hash }
    }
}
