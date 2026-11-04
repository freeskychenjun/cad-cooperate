# scaling.ps1 - does single-entity `entity` latency scale with the number of entities in the
# drawing? This matters more than a p50 race: real project drawings have thousands of entities.
#
# Two parts:
#   1) calibration: blank work drawing pre-populated with N entities (0/20/50/100/200/500);
#      sequential 1 warmup + 10 sampled single-entity `entity` ADD commands at each N.
#   2) reality check: a private writable copy of the real 624-entity river-system DWG
#      (tmp\real\real-map-624.dwg by default, or -BigDrawing <path>); same 1 warmup + 10 samples.
#
# Output: tmp\bench\e3\scaling.json  { rows:[{entityCount,p50Ms,p95Ms,helperMs,stability,...}] }
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\scaling.ps1
#   ... -Counts 0,20,50,100,200,500 -Samples 10 -BigDrawing <dwg-or-dxf>

param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string[]]$Counts = @('0', '20', '50', '100', '200', '500'),
    [int]$Samples = 10,
    [int]$Warmup = 1,
    [string]$BigDrawing = '',
    [string]$OutDir = 'tmp\bench\e3',
    [int]$TimeoutSec = 300,
    [switch]$NoFreeze,
    [switch]$NoNgen,
    [switch]$NoQuietGate,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
# powershell -File passes "-Counts 0,20,50" as ONE string: parse it here
$Counts = @($Counts | ForEach-Object { $_ -split '[, ]+' } | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ } | Where-Object { $_ -ge 0 })
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

# refuse to produce latency numbers under real-user / stray-process noise
if (-not $NoQuietGate) { $null = Assert-QuietWindow }

$root = $script:ChaosRoot
$runRoot = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$jsonPath = Join-Path $runRoot 'scaling.json'

$result = [ordered]@{
    schema = 'zwcad-chaos/1'; kind = 'entity-latency-scaling'; generatedAt = (Get-Date).ToString('o')
    product = $ProgId; samples = $Samples; warmup = $Warmup
    frozen = $null; launch = $null; rows = @(); bigDrawing = $null; hygiene = $null; notes = @()
}

# batch of N "add line" items -> one entity command
function New-BatchItems([string]$path, [int]$n, [string]$prefix) {
    $items = @()
    for ($i = 1; $i -le $n; $i++) {
        $items += [ordered]@{
            op = 'add'; type = 'line'; layer = 'PRE'
            geom = [ordered]@{ start = @((1000.0 + $i * 0.5), 0.0); end = @((1000.0 + $i * 0.5), 5.0) }
            props = @{}
        }
    }
    Write-JsonFile $path ([ordered]@{ items = $items })
    return $path
}

function New-OneAdd([string]$path, [string]$tag, [int]$i) {
    $items = @([ordered]@{
        op = 'add'; type = 'line'; layer = 'MEAS'
        geom = [ordered]@{ start = @((2000.0 + $i), 10.0); end = @((2000.0 + $i + 0.5), 13.0) }
        props = @{}
    })
    Write-JsonFile $path ([ordered]@{ items = $items })
    return $path
}

function Measure-Series([string]$progId, [string]$work, [string]$tag) {
    # 1 warmup (not sampled) + $Samples sampled single-entity entity calls
    $warmPath = New-OneAdd (Join-Path $runRoot ($tag + '-warm.json')) ($tag + '-W') 0
    $warm = Invoke-Cad @('entity', '-Doc', $work, '-Items', $warmPath) $TimeoutSec $progId
    $rows = @()
    $stabs = @()
    for ($i = 1; $i -le $Samples; $i++) {
        $p = New-OneAdd (Join-Path $runRoot ($tag + '-' + ('{0:D2}' -f $i) + '.json')) $tag $i
        $r = Invoke-Cad @('entity', '-Doc', $work, '-Items', $p) $TimeoutSec $progId
        $helperMs = Get-PropSafe $r.parsed 'elapsedMs'
        $st = Get-PropSafe $r.parsed 'result.stability'
        if ($null -ne $st) { $stabs += $st }
        $rows += [ordered]@{
            i = $i; exit = $r.exit; wallMs = [double]$r.wallMs; helperMs = $helperMs
            watchdog = $r.watchdog; code = [string](Get-PropSafe $r.parsed 'error.code')
        }
    }
    $walls = @($rows | ForEach-Object { [double]$_.wallMs })
    $agg = [ordered]@{}
    foreach ($k in @('attachMs', 'lockMs', 'busyMs', 'fingerprintMs', 'bodyMs', 'totalMs')) {
        $vals = @($stabs | ForEach-Object { Get-PropSafe $_ $k } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
        if ($vals.Count -gt 0) { $agg[$k] = [math]::Round((($vals | Measure-Object -Average).Average), 1) }
    }
    return [ordered]@{
        warmupExit = $warm.exit; warmupWallMs = [double]$warm.wallMs
        samples = $rows; p50Ms = if ($walls.Count) { [math]::Round((Get-Percentile $walls 0.50), 1) } else { $null }
        p95Ms = if ($walls.Count) { [math]::Round((Get-Percentile $walls 0.95), 1) } else { $null }
        minMs = if ($walls.Count) { [math]::Round(($walls | Measure-Object -Minimum).Minimum, 1) } else { $null }
        maxMs = if ($walls.Count) { [math]::Round(($walls | Measure-Object -Maximum).Maximum, 1) } else { $null }
        helperMsAvg = if ($walls.Count) {
            $h = @($rows | ForEach-Object { $_.helperMs } | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
            if ($h.Count) { [math]::Round((($h | Measure-Object -Average).Average), 1) } else { $null }
        } else { $null }
        stabilityAvg = $agg
        errorCount = @($rows | Where-Object { $_.exit -ne 0 -or $_.watchdog }).Count
    }
}

$cad = $null
try {
    if (-not $NoFreeze) {
        $result.frozen = New-FrozenProduct $runRoot
        if (-not $NoNgen -and (Test-Path -LiteralPath $script:ClientExe)) {
            try {
                $ngen = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
                if (Test-Path -LiteralPath $ngen) { $null = Invoke-Bounded $ngen @('install', $script:ClientExe) 90 }
            } catch { }
        }
    }
    $cad = Start-OwnCad $ProgId $TimeoutSec
    $result.launch = [ordered]@{ pid = $cad.pid; wallMs = $cad.wallMs }
    Write-Chaos ('own instance pid ' + $cad.pid)

    # ---------------- calibration
    foreach ($n in $Counts) {
        $work = New-WorkDrawing 'blank.dxf' $runRoot ('scale-' + $n)
        $open = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
        if ($open.exit -ne 0) { throw ('open failed: ' + $open.text) }
        if ($n -gt 0) {
            $batch = New-BatchItems (Join-Path $runRoot ('pre-' + $n + '.json')) $n 'PRE'
            $b = Invoke-Cad @('entity', '-Doc', $work, '-Items', $batch) $TimeoutSec $ProgId
            Write-Chaos ('pre-populate N=' + $n + ': exit=' + $b.exit + ' ' + [int]$b.wallMs + ' ms')
        }
        $rd = Invoke-Cad @('read', '-Doc', $work, '-Mode', 'handles', '-Max', '20000') $TimeoutSec $ProgId
        $actual = 0
        if ($rd.parsed) { $actual = @(Get-PropSafe $rd.parsed 'result.entities').Count }
        $m = Measure-Series $ProgId $work ('N' + $n)
        $row = [ordered]@{ entityCount = $actual; requested = $n }
        foreach ($k in $m.Keys) { $row[$k] = $m[$k] }
        $result.rows += $row
        Write-Chaos ('N=' + $actual + ': p50=' + $m.p50Ms + ' ms p95=' + $m.p95Ms +
                     ' helper=' + $m.helperMsAvg + ' ms stability=' + (($m.stabilityAvg | ConvertTo-Json -Compress -Depth 4)))
    }

    # ---------------- big-drawing reality check
    $bigSrc = $BigDrawing
    if (-not $bigSrc) {
        $cand = @((Join-Path $root 'tmp\real\real-map-624.dwg'),
                  '<big-map-dir>\<示例大图>.dwg')
        foreach ($c in $cand) { if (Test-Path -LiteralPath $c) { $bigSrc = $c; break } }
    }
    if ($bigSrc -and (Test-Path -LiteralPath $bigSrc)) {
        $ext = [System.IO.Path]::GetExtension($bigSrc)
        $bigDst = Join-Path $runRoot ('big-' + (Get-Date).ToString('HHmmss') + $ext)
        Copy-Item -LiteralPath $bigSrc -Destination $bigDst -Force
        $open = Invoke-Cad @('open', '-Doc', $bigDst) 300 $ProgId
        $rd = Invoke-Cad @('read', '-Doc', $bigDst, '-Mode', 'handles', '-Max', '20000') 300 $ProgId
        $actual = 0
        if ($rd.parsed) { $actual = @(Get-PropSafe $rd.parsed 'result.entities').Count }
        Write-Chaos ('big drawing: ' + $bigSrc + ' -> copied; open exit=' + $open.exit + ' entities=' + $actual)
        $m = Measure-Series $ProgId $bigDst 'BIG'
        $result.bigDrawing = [ordered]@{ source = $bigSrc; copy = $bigDst; openExit = $open.exit; entityCount = $actual }
        foreach ($k in $m.Keys) { $result.bigDrawing[$k] = $m[$k] }
        Write-Chaos ('big: N=' + $actual + ' p50=' + $m.p50Ms + ' ms p95=' + $m.p95Ms + ' helper=' + $m.helperMsAvg +
                     ' ms stability=' + (($m.stabilityAvg | ConvertTo-Json -Compress -Depth 4)))
    } else {
        $result.notes += 'big drawing not found (pass -BigDrawing <path>)'
        Write-Chaos 'big drawing: not found, skipped'
    }
} catch {
    $result.notes += ('EXCEPTION: ' + $_.Exception.Message)
} finally {
    if ($cad -and -not $Keep) {
        $result.hygiene = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
        Write-Chaos ('hygiene: remaining=' + $result.hygiene.remaining)
    }
    Write-JsonFile $jsonPath $result
    Write-Chaos ('scaling json: ' + $jsonPath)
}
