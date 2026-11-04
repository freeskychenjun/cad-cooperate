# session_worker.ps1 - one concurrent session for shape-A: optional warmup command(s), then N
# sampled single-entity `entity` commands sequentially, recording each command's wall clock.
# Started 3x in parallel by shape_a.ps1 (the literal "3 sessions x (warmup + N) commands" shape).
param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string]$Work,
    [string]$Session = 'S1',
    [int]$Commands = 10,
    [int]$Warmup = 1,
    [string]$OutDir,
    [int]$TimeoutSec = 240,
    [string]$TagPrefix = 'A'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$rows = @()
$warmRows = @()
$total = $Warmup + $Commands
for ($i = 1; $i -le $total; $i++) {
    $isWarm = ($i -le $Warmup)
    $tag = if ($isWarm) { ($Session + '-' + $TagPrefix + '-WARM-' + $i) } else { ($Session + '-' + $TagPrefix + '-' + ('{0:D2}' -f ($i - $Warmup))) }
    $itemsPath = Join-Path $OutDir ($tag + '.json')
    $items = @([ordered]@{
        op = 'add'; type = 'text'; layer = 'CHASZ'
        geom = [ordered]@{ text = $tag; insert = @((900.0 + $i * 3.0), 900.0); height = 2.5 }
        props = @{}
    })
    Write-JsonFile $itemsPath ([ordered]@{ items = $items })
    $r = Invoke-Cad @('entity', '-Doc', $Work, '-Items', $itemsPath) $TimeoutSec $ProgId
    $row = [ordered]@{
        tag = $tag; i = $i; sample = ($i - $Warmup); warmup = $isWarm
        exit = $r.exit; wallMs = [double]$r.wallMs; watchdog = $r.watchdog
        code = [string](Get-PropSafe $r.parsed 'error.code')
        lockWaitMs = (Get-PropSafe $r.parsed 'result.lock.waitedMs')
        busyWaitMs = (Get-PropSafe $r.parsed 'result.busy.waitedMs')
    }
    if ($isWarm) { $warmRows += $row } else { $rows += $row }
}
Write-JsonFile (Join-Path $OutDir ('session-' + $Session + '.json')) ([ordered]@{
    session = $Session; warmupCount = $warmRows.Count; warmup = $warmRows; rows = $rows })
