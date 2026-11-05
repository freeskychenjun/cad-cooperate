# concurrency.ps1 - E3 concurrency baseline: 3 sessions writing to ONE drawing at the same time.
#
# What it does (all commands go through the E1 client, i.e. daemon + CadClient.exe):
#   1. launch our own ZWCAD 2025 instance, open a private copy of the benchmark fixture;
#   2. snapshot the drawing, then run R rounds; in each round 3 client processes are started
#      TOGETHER and each does ONE `entity -Items` call adding 5 distinct text entities;
#   3. afterwards: final entity count must be baseline + sessions*rounds*items (zero lost
#      updates), every session's texts must be present, handles must be unique, no duplicate
#      handles / parse errors; per-command wall clock -> p50/p95; lock wait (waitedMs) if the
#      product reports it; failures counted;
#   4. quit our own instance, assert process hygiene, write tmp\bench\e3\concurrency.json.
#
# This is the "before E3" baseline: with the current no-lock implementation the concurrent
# writes serialize badly (lead measured 15-20 s per command). After core-cad lands the
# document lock/queue we re-run the SAME script and compare p50 (target < 400 ms).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\concurrency.ps1
#   ... -Sessions 3 -ItemsPerCall 5 -Rounds 2 -OutDir tmp\bench\e3 -Keep

param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [int]$Sessions = 3,
    [int]$ItemsPerCall = 5,
    [int]$Rounds = 2,
    [string]$OutDir = 'tmp\bench\e3',
    [int]$TimeoutSec = 240,
    [switch]$NoFreeze,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$root = $script:ChaosRoot
$runDir = Join-Path $root (Join-Path $OutDir 'concurrency')
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$jsonPath = Join-Path $root (Join-Path $OutDir 'concurrency.json')
$frozenInfo = $null

function Get-ServeStatus([string]$progId) {
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $script:CadPs1 + '" serve -Action status'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $psExe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $null = $p.Start()
    $out = $p.StandardOutput.ReadToEnd()
    $null = $p.WaitForExit(20000)
    return (ConvertFrom-JsonText $out)
}

$result = [ordered]@{
    schema = 'zwcad-chaos/1'
    kind = 'concurrency'
    generatedAt = (Get-Date).ToString('o')
    product = $ProgId
    sessions = $Sessions
    itemsPerCall = $ItemsPerCall
    rounds = $Rounds
    expectedAdded = $Sessions * $Rounds * $ItemsPerCall
    serveStatusBefore = $null
    launch = $null
    baseline = $null
    commands = @()
    finalCount = $null
    expectedFinalCount = $null
    zeroLostUpdates = $null
    sessionsComplete = $null
    duplicateHandles = @()
    parseFailures = 0
    waitMsSamples = @()
    p50Ms = $null
    p95Ms = $null
    minMs = $null
    maxMs = $null
    failures = @()
    diff = $null
    hygiene = $null
    verdict = 'FAIL'
    notes = @()
}

$cad = $null
$work = $null
try {
    if (-not $NoFreeze) { $frozenInfo = New-FrozenProduct $runDir; $result.frozen = $frozenInfo }
    $result.hashesAtStart = @{}
    foreach ($rel in @('scripts\helper\CadHelper.cs', 'scripts\helper\Edits.cs', 'scripts\helper\Stability.cs', 'scripts\cad.ps1')) {
        $full = Join-Path $root $rel
        if (Test-Path -LiteralPath $full) { $result.hashesAtStart[$rel] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower() }
    }
    $result.serveStatusBefore = Get-ServeStatus $ProgId
    Write-Chaos ('serve status before: running=' + [string](Get-PropSafe $result.serveStatusBefore 'result.running'))

    $work = New-WorkDrawing 'design-water.dxf' $runDir 'concurrency'
    Write-Chaos ('work drawing: ' + $work)

    $null = Initialize-Helper $ProgId 120
    $cad = Start-OwnCad $ProgId $TimeoutSec
    $result.launch = [ordered]@{ pid = $cad.pid; wallMs = $cad.wallMs }
    Write-Chaos ('own instance pid ' + $cad.pid + ' (launch ' + [int]$cad.wallMs + ' ms)')

    $open = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
    if ($open.exit -ne 0) { throw ('open work drawing failed: ' + $open.text) }

    $baseRead = Get-DocRead $ProgId $work $TimeoutSec
    if ($baseRead.exit -ne 0) { throw ('baseline read failed: ' + $baseRead.text) }
    $baselineCount = $baseRead.count
    $result.baseline = [ordered]@{ count = $baselineCount; wallMs = $baseRead.wallMs }

    $snap = Invoke-Cad @('snapshot', '-Doc', $work) 120 $ProgId
    $snapPath = [string](Get-PropSafe $snap.parsed 'result.path')
    Write-Chaos ('baseline entities=' + $baselineCount + ' snapshot=' + $snapPath)

    $allWalls = @()
    $expectedTexts = @()
    for ($r = 1; $r -le $Rounds; $r++) {
        $jobs = @()
        for ($s = 1; $s -le $Sessions; $s++) {
            $session = 'S' + $s
            $itemsPath = Join-Path $runDir ('items-' + $session + '-r' + $r + '.json')
            & $script:PyExe (Join-Path $PSScriptRoot 'gen_bulk_add.py') --session $session --round $r `
                --count $ItemsPerCall --out $itemsPath | Out-Null
            for ($i = 1; $i -le $ItemsPerCall; $i++) {
                $expectedTexts += (('{0}-R{1}-{2:D2}' -f $session, $r, $i))
            }
            $stdoutPath = Join-Path $runDir ('client-' + $session + '-r' + $r + '.stdout.txt')
            $stderrPath = Join-Path $runDir ('client-' + $session + '-r' + $r + '.stderr.txt')
            $job = Start-CadAsync @('entity', '-Doc', $work, '-Items', $itemsPath) $ProgId $stdoutPath $stderrPath
            $jobs += [pscustomobject]@{ session = $session; round = $r; job = $job; items = $itemsPath }
        }
        Write-Chaos ('round ' + $r + ': started ' + $jobs.Count + ' concurrent entity calls')
        foreach ($j in $jobs) {
            $done = Wait-CadAsync $j.job $TimeoutSec
            $waitMs = $null
            $w = Get-PropSafe $done.parsed 'result.waitedMs'
            if ($null -eq $w) { $w = Get-PropSafe $done.parsed 'result.timings.waitMs' }
            if ($null -ne $w) { $result.waitMsSamples += [double]$w }
            $applied = Get-PropSafe $done.parsed 'result.applied'
            $code = Get-PropSafe $done.parsed 'error.code'
            $cmd = [ordered]@{
                session = $j.session; round = $r; exit = $done.exit; wallMs = $done.wallMs
                watchdog = $done.watchdog; applied = $applied; errorCode = $code
                waitedMs = $w; argv = ($done.argv -join ' ')
            }
            $result.commands += $cmd
            $allWalls += [double]$done.wallMs
            if ($done.exit -ne 0 -or $done.watchdog) {
                $result.failures += [ordered]@{ session = $j.session; round = $r; exit = $done.exit
                    watchdog = $done.watchdog; errorCode = $code
                    message = ((Get-PropSafe $done.parsed 'error.message')) }
            }
            Write-Chaos ('  ' + $j.session + ' r' + $r + ' exit=' + $done.exit + ' ' +
                         [int]$done.wallMs + ' ms applied=' + [string]$applied + ' code=' + [string]$code)
        }
    }

    # ---- independent verification (never trust the applied counters)
    $finalRead = Get-DocRead $ProgId $work $TimeoutSec
    if ($finalRead.exit -ne 0) { $result.parseFailures++; $result.failures += @{ note = 'final read failed'; text = $finalRead.text } }
    $result.finalCount = $finalRead.count
    $result.expectedFinalCount = $baselineCount + $result.expectedAdded
    $result.zeroLostUpdates = ($finalRead.count -eq $result.expectedFinalCount)

    $handles = @($finalRead.entities | ForEach-Object { [string](Get-PropSafe $_ 'handle') } | Where-Object { $_ })
    $dups = @($handles | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    $result.duplicateHandles = $dups

    $texts = Get-TextsOf $finalRead.entities
    $missing = @()
    foreach ($t in $expectedTexts) { if ($texts -notcontains $t) { $missing += $t } }
    $result.sessionsComplete = ($missing.Count -eq 0)
    if ($missing.Count -gt 0) { $result.notes += ('missing entity texts: ' + ($missing -join ',')) }

    $diffRes = Invoke-Cad @('diff', '-Doc', $work, '-Since', $snapPath) 120 $ProgId
    $added = @(Get-PropSafe $diffRes.parsed 'result.added')
    $result.diff = [ordered]@{ exit = $diffRes.exit; added = $added.Count
        removed = @(Get-PropSafe $diffRes.parsed 'result.removed').Count
        changed = @(Get-PropSafe $diffRes.parsed 'result.changed').Count }

    if ($allWalls.Count -gt 0) {
        $result.p50Ms = [math]::Round((Get-Percentile $allWalls 0.50), 1)
        $result.p95Ms = [math]::Round((Get-Percentile $allWalls 0.95), 1)
        $result.minMs = [math]::Round(($allWalls | Measure-Object -Minimum).Minimum, 1)
        $result.maxMs = [math]::Round(($allWalls | Measure-Object -Maximum).Maximum, 1)
    }

    $pass = ($result.zeroLostUpdates -and $result.sessionsComplete -and $result.duplicateHandles.Count -eq 0 `
             -and $result.failures.Count -eq 0 -and $result.parseFailures -eq 0)
    $result.verdict = $(if ($pass) { 'PASS' } else { 'FAIL' })
} catch {
    $result.notes += ('EXCEPTION: ' + $_.Exception.Message)
    $result.verdict = 'ERROR'
} finally {
    if ($cad -and -not $Keep) {
        $result.hygiene = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
        Write-Chaos ('hygiene: remaining=' + $result.hygiene.remaining + ' forcedStop=' + $result.hygiene.forcedStop)
    }
    Write-JsonFile $jsonPath $result
    Write-Chaos ('----')
    Write-Chaos ('concurrency verdict: ' + $result.verdict +
                 ' | entities ' + [string]$result.finalCount + '/' + [string]$result.expectedFinalCount +
                 ' | p50=' + [string]$result.p50Ms + ' ms p95=' + [string]$result.p95Ms + ' ms' +
                 ' | failures=' + $result.failures.Count +
                 ' | dups=' + $result.duplicateHandles.Count)
    Write-Chaos ('json: ' + $jsonPath)
}

if ($result.verdict -eq 'PASS') { exit 0 }
exit 3
