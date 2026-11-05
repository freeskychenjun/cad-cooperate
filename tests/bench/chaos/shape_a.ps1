# shape_a.ps1 - E3 task-20 independent re-run of the "shape A" latency target:
#   3 sessions x 10 single-entity `entity` commands = 30 commands on ONE private fixture copy,
#   two consecutive runs, both p50 must be < 400 ms to pass, plus a sequential baseline
#   (1 session, 10 single-entity commands) so a "concurrent looks fast but sequential got
#   slower" fake improvement is caught.
#
# Independent by construction: own frozen product snapshot, own instance (finished here),
# private work drawing per run, and the result assertions come from `read -Mode geom`
# (entity count == baseline+30, unique handles, per-session texts), never from self-reported
# counters.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\shape_a.ps1
#   ... -Runs 2 -Sessions 3 -CommandsPerSession 10 -OutDir tmp\bench\e3\latency-opt

param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [int]$Sessions = 3,
    [int]$CommandsPerSession = 10,
    [int]$Runs = 2,
    [int]$Warmup = 1,
    [string]$OutDir = 'tmp\bench\e3\latency-opt',
    [int]$TimeoutSec = 240,
    [string]$Mode = 'continuous',
    [switch]$NoFreeze,
    [switch]$NoQuietGate,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

# refuse to produce latency numbers under real-user / stray-process noise
if (-not $NoQuietGate) { $null = Assert-QuietWindow }

$root = $script:ChaosRoot
$runRoot = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$jsonPath = Join-Path $runRoot 'independent-run.json'

$result = [ordered]@{
    schema = 'zwcad-chaos/1'
    kind = 'shape-a-latency'
    generatedAt = (Get-Date).ToString('o')
    product = $ProgId
    shape = ('sessions=' + $Sessions + ' x commands=' + $CommandsPerSession + ' single-entity entity calls (step-synchronized)')
    frozen = $null
    runs = @()
    sequential = $null
    launch = $null
    hygiene = $null
    verdict = 'FAIL'
    notes = @()
}

function New-OneItem([string]$path, [string]$tag) {
    $items = @([ordered]@{
        op = 'add'; type = 'text'; layer = 'CHASZ'
        geom = [ordered]@{ text = $tag; insert = @((800.0 + (Get-Random -Minimum 0 -Maximum 500) / 10.0), 800.0); height = 2.5 }
        props = @{}
    })
    Write-JsonFile $path ([ordered]@{ items = $items })
    return $path
}

$cad = $null
try {
    if (-not $NoFreeze) {
        $result.frozen = New-FrozenProduct $runRoot
        # keep the client-startup optimisation fair on a frozen snapshot: pre-compile a native
        # image for the copied client (best effort; a failure only costs ~30 ms/command)
        if (-not $NoNgen -and (Test-Path -LiteralPath $script:ClientExe)) {
            try {
                $ngen = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
                if (Test-Path -LiteralPath $ngen) {
                    $null = Invoke-Bounded $ngen @('install', $script:ClientExe) 90
                    Write-Chaos 'ngen install (frozen client): done'
                }
            } catch { Write-Chaos ('ngen skipped: ' + $_.Exception.Message) }
        }
    }
    if (-not $Fresh) {
        $cad = Start-OwnCad $ProgId $TimeoutSec
        $result.launch = [ordered]@{ pid = $cad.pid; wallMs = $cad.wallMs }
        Write-Chaos ('own instance pid ' + $cad.pid)
    }

    # ---------------- two consecutive shape-A runs
    # -Mode continuous: 3 worker processes each run CommandsPerSession single-entity commands
    # sequentially at the same time (the literal "3 sessions x 10 commands" shape).

    for ($r = 1; $r -le $Runs; $r++) {
        if ($Fresh) {
            $cad = Start-OwnCad $ProgId $TimeoutSec
            $result['launches'] = @($result['launches']) + @([ordered]@{ round = $r; pid = $cad.pid; wallMs = $cad.wallMs })
            Write-Chaos ('round ' + $r + ': fresh instance pid ' + $cad.pid + ' (' + [int]$cad.wallMs + ' ms)')
        }
        $work = New-WorkDrawing 'blank.dxf' $runRoot ('shapeA-r' + $r)
        $open = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
        if ($open.exit -ne 0) { throw ('open failed: ' + $open.text) }
        $base = Get-DocRead $ProgId $work $TimeoutSec
        $expectedTexts = @()
        $walls = @()
        $errors = @()
        $warmupWalls = @()
        $allWallsInclWarm = @()
        $sequences = @{}
        if ($Mode -eq 'continuous') {
            $workers = @()
            $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            for ($s = 1; $s -le $Sessions; $s++) {
                $session = 'S' + $s
                $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'session_worker.ps1'),
                          '-ProgId', $ProgId, '-Work', $work, '-Session', $session,
                          '-Commands', [string]$CommandsPerSession, '-Warmup', [string]$Warmup, '-OutDir', $runRoot,
                          '-TimeoutSec', [string]$TimeoutSec, '-TagPrefix', ('A' + $r))
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $psExe
                $psi.Arguments = (($argv | ForEach-Object { $a = [string]$_; if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }) -join ' ')
                $psi.UseShellExecute = $false
                $psi.CreateNoWindow = $true
                $psi.WorkingDirectory = $script:ChaosRoot
                $proc = New-Object System.Diagnostics.Process
                $proc.StartInfo = $psi
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $null = $proc.Start()
                $workers += [pscustomobject]@{ session = $session; proc = $proc; watch = $sw }
            }
            foreach ($w in $workers) {
                $null = $w.proc.WaitForExit(($TimeoutSec + 120) * 1000)
                try { $w.proc.WaitForExit() } catch { }
                $w.watch.Stop()
            }
            for ($s = 1; $s -le $Sessions; $s++) {
                $session = 'S' + $s
                $sp = Join-Path $runRoot ('session-' + $session + '.json')
                if (-not (Test-Path -LiteralPath $sp)) {
                    $errors += [ordered]@{ session = $session; note = 'worker output missing' }
                    continue
                }
                $sj = ConvertFrom-JsonText ([System.IO.File]::ReadAllText($sp, [System.Text.Encoding]::UTF8))
                $seq = @()
                foreach ($row in @($sj.rows)) {
                    $expectedTexts += [string]$row.tag
                    $walls += [double]$row.wallMs
                    $seq += [ordered]@{ sample = [int]$row.sample; wallMs = [double]$row.wallMs }
                    if ($row.exit -ne 0 -or $row.watchdog) {
                        $errors += [ordered]@{ session = $session; tag = [string]$row.tag; exit = [int]$row.exit
                            watchdog = $row.watchdog; code = [string]$row.code }
                    }
                }
                foreach ($wrow in @($sj.warmup)) {
                    $expectedTexts += [string]$wrow.tag
                    $warmupWalls += [double]$wrow.wallMs
                    $allWallsInclWarm += [double]$wrow.wallMs
                    if ($wrow.exit -ne 0 -or $wrow.watchdog) {
                        $errors += [ordered]@{ session = $session; tag = [string]$wrow.tag; warmup = $true
                            exit = [int]$wrow.exit; watchdog = $wrow.watchdog; code = [string]$wrow.code }
                    }
                }
                $sequences[$session] = @($seq | Sort-Object sample)
                $allWallsInclWarm += @($seq | ForEach-Object { [double]$_.wallMs })
            }
        } else {
        for ($step = 1; $step -le $CommandsPerSession; $step++) {
            $jobs = @()
            for ($s = 1; $s -le $Sessions; $s++) {
                $session = 'S' + $s
                $tag = $session + '-A' + $r + '-' + ('{0:D2}' -f $step)
                $itemsPath = Join-Path $runRoot ($tag + '.json')
                $null = New-OneItem $itemsPath $tag
                $expectedTexts += $tag
                $job = Start-CadAsync @('entity', '-Doc', $work, '-Items', $itemsPath) $ProgId `
                    (Join-Path $runRoot ($tag + '.stdout.txt')) (Join-Path $runRoot ($tag + '.stderr.txt'))
                $jobs += [pscustomobject]@{ session = $session; step = $step; tag = $tag; job = $job }
            }
            foreach ($j in $jobs) {
                $done = Wait-CadAsync $j.job $TimeoutSec
                $walls += [double]$done.wallMs
                if ($done.exit -ne 0 -or $done.watchdog) {
                    $errors += [ordered]@{ tag = $j.tag; exit = $done.exit; watchdog = $done.watchdog
                        code = [string](Get-PropSafe $done.parsed 'error.code') }
                }
            }
        }
        }
        $final = Get-DocRead $ProgId $work $TimeoutSec
        $handles = @($final.entities | ForEach-Object { [string](Get-PropSafe $_ 'handle') } | Where-Object { $_ })
        $dups = @($handles | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
        $texts = Get-TextsOf $final.entities
        $missing = @($expectedTexts | Where-Object { $texts -notcontains $_ })
        $p50 = if ($walls.Count) { [math]::Round((Get-Percentile $walls 0.50), 1) } else { $null }
        $p95 = if ($walls.Count) { [math]::Round((Get-Percentile $walls 0.95), 1) } else { $null }
        $p50Incl = if ($allWallsInclWarm.Count) { [math]::Round((Get-Percentile $allWallsInclWarm 0.50), 1) } else { $null }
        $p95Incl = if ($allWallsInclWarm.Count) { [math]::Round((Get-Percentile $allWallsInclWarm 0.95), 1) } else { $null }
        $firstWalls = @()
        $restWalls = @()
        foreach ($sess in $sequences.Keys) {
            foreach ($it in $sequences[$sess]) {
                if ([int]$it.sample -eq 1) { $firstWalls += [double]$it.wallMs } else { $restWalls += [double]$it.wallMs }
            }
        }
        $p50First = if ($firstWalls.Count) { [math]::Round((Get-Percentile $firstWalls 0.50), 1) } else { $null }
        $p50Rest = if ($restWalls.Count) { [math]::Round((Get-Percentile $restWalls 0.50), 1) } else { $null }
        $p95Rest = if ($restWalls.Count) { [math]::Round((Get-Percentile $restWalls 0.95), 1) } else { $null }
        $rec = [ordered]@{
            run = $r; workDrawing = $work; commands = $walls.Count
            p50Ms = $p50; p95Ms = $p95
            minMs = if ($walls.Count) { [math]::Round(($walls | Measure-Object -Minimum).Minimum, 1) } else { $null }
            maxMs = if ($walls.Count) { [math]::Round(($walls | Measure-Object -Maximum).Maximum, 1) } else { $null }
            errors = $errors; errorCount = $errors.Count
            baselineEntities = $base.count; finalEntities = $final.count
            expectedEntities = $base.count + $Sessions * ($CommandsPerSession + $Warmup)
            zeroLoss = ($final.count -eq ($base.count + $Sessions * ($CommandsPerSession + $Warmup)))
            duplicateHandles = $dups; missingTexts = @($missing | Select-Object -First 5); missingCount = $missing.Count
            p50Under400 = ($null -ne $p50 -and $p50 -lt 400)
            p50InclWarmup = $p50Incl; p95InclWarmup = $p95Incl
            warmupWalls = @($warmupWalls)
            p50FirstSample = $p50First; p50Samples2to10 = $p50Rest; p95Samples2to10 = $p95Rest
            sequences = $sequences
        }
        Write-Chaos ('run ' + $r + ': sampled=' + $rec.commands + ' p50=' + $p50 + ' ms p95=' + $p95 +
                     ' | incl-warmup p50=' + $p50Incl + ' p95=' + $p95Incl +
                     ' | firstSample p50=' + $p50First + ' | samples2-10 p50=' + $p50Rest + ' p95=' + $p95Rest +
                     ' | errors=' + $rec.errorCount + ' entities=' + $rec.finalEntities + '/' + $rec.expectedEntities +
                     ' dups=' + $dups.Count + ' missing=' + $missing.Count + ' p50<400=' + $rec.p50Under400)
        $result.runs += $rec
        if ($Fresh) {
            # fresh daemon + instance for the next round
            try {
                $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $null = Invoke-Bounded $psExe @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:CadPs1, 'serve', '-Action', 'stop') 60
            } catch { }
            $hy = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
            Write-Chaos ('round ' + $r + ': fresh teardown remaining=' + $hy.remaining)
            $cad = $null
        }
    }

    # ---------------- sequential baseline (same build, one session, 10 single-entity calls)
    if ($Fresh) {
        $cad = Start-OwnCad $ProgId $TimeoutSec
        $result['sequentialLaunch'] = [ordered]@{ pid = $cad.pid; wallMs = $cad.wallMs }
    }
    $workSeq = New-WorkDrawing 'blank.dxf' $runRoot 'shapeA-seq'
    $null = Invoke-Cad @('open', '-Doc', $workSeq) 120 $ProgId
    $baseSeq = Get-DocRead $ProgId $workSeq $TimeoutSec
    $seqWalls = @()
    $seqErrors = 0
    for ($w = 1; $w -le $Warmup; $w++) {
        $wp = Join-Path $runRoot ('SEQ-WARM-' + $w + '.json')
        $null = New-OneItem $wp ('SEQ-WARM-' + $w)
        $null = Invoke-Cad @('entity', '-Doc', $workSeq, '-Items', $wp) $TimeoutSec $ProgId
    }
    for ($i = 1; $i -le $CommandsPerSession; $i++) {
        $itemsPath = Join-Path $runRoot ('SEQ-' + ('{0:D2}' -f $i) + '.json')
        $null = New-OneItem $itemsPath ('SEQ-' + ('{0:D2}' -f $i))
        $one = Invoke-Cad @('entity', '-Doc', $workSeq, '-Items', $itemsPath) $TimeoutSec $ProgId
        $seqWalls += [double]$one.wallMs
        if ($one.exit -ne 0) { $seqErrors++ }
    }
    $seqFinal = Get-DocRead $ProgId $workSeq $TimeoutSec
    $result.sequential = [ordered]@{
        commands = $seqWalls.Count
        p50Ms = [math]::Round((Get-Percentile $seqWalls 0.50), 1)
        p95Ms = [math]::Round((Get-Percentile $seqWalls 0.95), 1)
        minMs = [math]::Round(($seqWalls | Measure-Object -Minimum).Minimum, 1)
        maxMs = [math]::Round(($seqWalls | Measure-Object -Maximum).Maximum, 1)
        errors = $seqErrors
        baselineEntities = $baseSeq.count; finalEntities = $seqFinal.count
        zeroLoss = ($seqFinal.count -eq ($baseSeq.count + $CommandsPerSession + $Warmup))
    }
    Write-Chaos ('sequential: p50=' + $result.sequential.p50Ms + ' ms p95=' + $result.sequential.p95Ms +
                 ' errors=' + $seqErrors + ' entities=' + $seqFinal.count + '/' + ($baseSeq.count + $CommandsPerSession))

    $bothUnder = (@($result.runs | Where-Object { -not $_.p50Under400 }).Count -eq 0)
    $allZeroLoss = (@($result.runs | Where-Object { -not $_.zeroLoss }).Count -eq 0) -and $result.sequential.zeroLoss
    $allClean = (@($result.runs | Where-Object { $_.errorCount -ne 0 -or $_.duplicateHandles.Count -ne 0 -or $_.missingCount -ne 0 }).Count -eq 0)
    $result.verdict = $(if ($bothUnder -and $allZeroLoss -and $allClean) { 'PASS' } else { 'FAIL' })
    $result['mode'] = $(if ($Fresh) { 'fresh-instance-per-round' } else { 'same-instance-rounds' })
    $result['p50Under400BothRuns'] = $bothUnder
    $result['zeroLossAllRuns'] = $allZeroLoss
    $result['cleanAllRuns'] = $allClean
} catch {
    $result.notes += ('EXCEPTION: ' + $_.Exception.Message)
    $result.verdict = 'ERROR'
} finally {
    if ($cad -and -not $Keep) {
        $result.hygiene = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
        Write-Chaos ('hygiene: remaining=' + $result.hygiene.remaining + ' forcedStop=' + $result.hygiene.forcedStop)
    }
    Write-JsonFile $jsonPath $result
    Write-Chaos ('shape A verdict: ' + $result.verdict + ' | json: ' + $jsonPath)
}
if ($result.verdict -eq 'PASS') { exit 0 }
exit 3
