# compare-harnesses.ps1 - same-window A/B of the two shape-A harnesses on ONE frozen build.
#
# A = core-cad timing points : <snap>\tmp\shapeA_v2.ps1 -Product <p> -Rounds 2 -Fresh
# B = audit timing points    : <snap>\tests\bench\chaos\shape_a.ps1 -Runs 1 -Mode continuous -NoFreeze
# Both run from ONE snapshot of the live code, so the only difference is the timing points.
#
# Quiet-window gate (lead decision): no Office (wps/et/wpp/EXCEL/WINWORD), no other CAD/daemon,
# CPU <25%, free RAM >2 GB. Refuses with QUIET_WINDOW_REQUIRED (exit 4) instead of producing
# noisy numbers. -ForceLoadNote bypasses the gate and labels the run load-environment/reference-only.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\compare-harnesses.ps1
#   ... -Runs 3 -Products ZWCAD.Application.2025,ZWCAD.Application.16
#   ... -ForceLoadNote     # reference only, never counted as acceptance

param(
    [string]$OutDir = 'tmp\bench\e3',
    [int]$Runs = 3,
    [int]$TimeoutSec = 600,
    [string[]]$Products = @('ZWCAD.Application.2025', 'ZWCAD.Application.16'),
    [switch]$ForceLoadNote,
    [switch]$NoQuietGate
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

$root = $script:ChaosRoot
$runRoot = Join-Path $root (Join-Path $OutDir 'compare-harnesses')
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$jsonPath = Join-Path $root (Join-Path $OutDir 'compare-harnesses.json')
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$loadNote = [bool]$ForceLoadNote
$gateHard = (-not $NoQuietGate) -and (-not $ForceLoadNote)

# ---------------------------------------------------------------- ONE frozen build for both
$snap = Join-Path $root ('tmp\bench\e3\cmp-snapshot-' + (Get-Date).ToString('HHmmss'))
New-Item -ItemType Directory -Force -Path (Join-Path $snap 'scripts\helper'),
    (Join-Path $snap 'bin'), (Join-Path $snap 'tests\bench\fixtures'), (Join-Path $snap 'tests\bench\chaos'),
    (Join-Path $snap 'tmp') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts\cad.ps1') -Destination (Join-Path $snap 'scripts\cad.ps1') -Force
Copy-Item -Path (Join-Path $root 'scripts\helper\*.cs') -Destination (Join-Path $snap 'scripts\helper') -Force
if (Test-Path -LiteralPath (Join-Path $root 'bin\CadClient.exe')) {
    Copy-Item -LiteralPath (Join-Path $root 'bin\CadClient.exe') -Destination (Join-Path $snap 'bin\CadClient.exe') -Force
}
foreach ($fx in @('design-water.dxf', 'blank.dxf')) {
    $src = Join-Path $root ('tests\bench\fixtures\' + $fx)
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $snap ('tests\bench\fixtures\' + $fx)) -Force }
}
foreach ($f in @('lib.ps1', 'shape_a.ps1', 'session_worker.ps1', 'inject.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root ('tests\bench\chaos\' + $f)) -Destination (Join-Path $snap ('tests\bench\chaos\' + $f)) -Force
}
foreach ($f in @('shapeA_v2.ps1', 'shapeA_v2_session.ps1')) {
    $src = Join-Path $root ('tmp\' + $f)
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $snap ('tmp\' + $f)) -Force }
}
$buildHashes = [ordered]@{}
foreach ($f in @('scripts\cad.ps1', 'scripts\helper\CadHelper.cs', 'scripts\helper\Edits.cs',
                 'scripts\helper\Stability.cs', 'bin\CadClient.exe', 'tmp\shapeA_v2.ps1')) {
    $full = Join-Path $snap $f
    if (Test-Path -LiteralPath $full) { $buildHashes[$f] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower() }
}
try {
    $ngen = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
    if (Test-Path -LiteralPath $ngen -and (Test-Path -LiteralPath (Join-Path $snap 'bin\CadClient.exe'))) {
        $null = Invoke-Bounded $ngen @('install', (Join-Path $snap 'bin\CadClient.exe')) 120
    }
} catch { }

function Get-ProductTag([string]$progId) {
    if ($progId -match '\.16') { return 'zwcad16' }
    if ($progId -match '2025') { return 'zwcad2025' }
    return 'autocad'
}

function Invoke-HarnessA([string]$tag, [int]$run) {
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $snap 'tmp\shapeA_v2.ps1'),
              '-Product', $tag, '-Rounds', '2', '-Sessions', '3', '-Writes', '10', '-Fresh', '-MinFreeMemGB', '2')
    if ($loadNote) { $argv += @('-ForceLoad') }
    $r = Invoke-Bounded $psExe $argv $TimeoutSec
    $hisJson = Join-Path $snap ('tmp\evidence-e3\lateny-opt-' + $tag + '\shape-a-' + $tag + '.json')
    $copy = Join-Path $runRoot ('A-' + $tag + '-run' + $run + '.json')
    if (Test-Path -LiteralPath $hisJson) { Copy-Item -LiteralPath $hisJson -Destination $copy -Force }
    $j = $null
    if (Test-Path -LiteralPath $copy) { $j = ConvertFrom-JsonText ([System.IO.File]::ReadAllText($copy, [System.Text.Encoding]::UTF8)) }
    return [pscustomobject]@{ exit = $r.exit; wallMs = $r.wallMs; text = $r.text; json = $j }
}

function Invoke-HarnessB([string]$progId, [string]$tag, [int]$run) {
    $rel = 'tmp\e3cmp\' + $tag + '-run' + $run
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $snap 'tests\bench\chaos\shape_a.ps1'),
              '-Runs', '1', '-Mode', 'continuous', '-ProgId', $progId, '-OutDir', $rel, '-NoFreeze')
    if ($loadNote) { $argv += @('-NoQuietGate') }
    $r = Invoke-Bounded $psExe $argv $TimeoutSec
    $jp = Join-Path $snap ($rel + '\independent-run.json')
    $copy = Join-Path $runRoot ('B-' + $tag + '-run' + $run + '.json')
    if (Test-Path -LiteralPath $jp) { Copy-Item -LiteralPath $jp -Destination $copy -Force }
    $j = $null
    if (Test-Path -LiteralPath $copy) { $j = ConvertFrom-JsonText ([System.IO.File]::ReadAllText($copy, [System.Text.Encoding]::UTF8)) }
    return [pscustomobject]@{ exit = $r.exit; wallMs = $r.wallMs; text = $r.text; json = $j }
}

$result = [ordered]@{
    schema = 'zwcad-chaos/1'; kind = 'harness-comparison'; generatedAt = (Get-Date).ToString('o')
    snapshot = $snap; buildHashes = $buildHashes; runs = $Runs
    environment = $(if ($loadNote) { 'load (forced, reference only)' } else { 'quiet-required' })
    products = @(); gateMetrics = @(); notes = @(); verdict = 'FAIL'
}

try {
    foreach ($prod in $Products) {
        $tag = Get-ProductTag $prod
        $entry = [ordered]@{ product = $prod; productTag = $tag; A = @(); B = @() }
        for ($i = 1; $i -le $Runs; $i++) {
            $st = Get-QuietWindowState
            $result.gateMetrics += $st
            if ($gateHard -and -not $st.ok) { throw 'QUIET_WINDOW_REQUIRED' }
            $a = Invoke-HarnessA $tag $i
            $rounds = @()
            foreach ($rd in @($a.json.rounds)) {
                if ($null -eq $rd) { continue }
                $rounds += [ordered]@{ round = [int]$rd.round; hotP50 = [double]$rd.hot.p50; hotP95 = [double]$rd.hot.p95
                    coldP50 = [double]$rd.cold.p50; errors = [int]$rd.errors
                    entityCount = [int]$rd.entityCount; expectedCount = [int]$rd.expectedCount
                    duplicateHandles = [int]$rd.duplicateHandles }
            }
            $entry.A += [ordered]@{ run = $i; exit = $a.exit; rounds = $rounds
                environment = (Get-PropSafe $a.json 'environment') }
            Write-Chaos ('A/' + $tag + ' run' + $i + ': ' + (($rounds | ForEach-Object { 'hot=' + $_.hotP50 }) -join ' , '))
        }
        for ($i = 1; $i -le $Runs; $i++) {
            $st = Get-QuietWindowState
            $result.gateMetrics += $st
            if ($gateHard -and -not $st.ok) { throw 'QUIET_WINDOW_REQUIRED' }
            $b = Invoke-HarnessB $prod $tag $i
            $rounds = @()
            foreach ($rd in @($b.json.runs)) {
                if ($null -eq $rd) { continue }
                $rounds += [ordered]@{ round = [int]$rd.run; hotP50 = [double]$rd.p50Ms; hotP95 = [double]$rd.p95Ms
                    coldP50 = [double]$rd.p50InclWarmup; errors = [int]$rd.errorCount
                    entityCount = [int]$rd.finalEntities; expectedCount = [int]$rd.expectedEntities
                    duplicateHandles = @($rd.duplicateHandles).Count }
            }
            $entry.B += [ordered]@{ run = $i; exit = $b.exit; rounds = $rounds }
            Write-Chaos ('B/' + $tag + ' run' + $i + ': ' + (($rounds | ForEach-Object { 'hot=' + $_.hotP50 }) -join ' , '))
        }
        foreach ($h in @('A', 'B')) {
            $vals = @()
            foreach ($run in $entry[$h]) { foreach ($rd in $run.rounds) { if ($rd.hotP50 -gt 0) { $vals += [double]$rd.hotP50 } } }
            $entry[$h + 'Values'] = $vals
            $entry[$h + 'Median'] = if ($vals.Count) { [math]::Round((Get-Percentile $vals 0.50), 1) } else { $null }
            $entry[$h + 'Min'] = if ($vals.Count) { ($vals | Measure-Object -Minimum).Minimum } else { $null }
            $entry[$h + 'Max'] = if ($vals.Count) { ($vals | Measure-Object -Maximum).Maximum } else { $null }
        }
        $entry['deltaMs'] = if ($null -ne $entry.AMedian -and $null -ne $entry.BMedian) { [math]::Round(($entry.BMedian - $entry.AMedian), 1) } else { $null }
        $entry['bothUnder400'] = ($null -ne $entry.AMedian -and $entry.AMedian -lt 400 -and $null -ne $entry.BMedian -and $entry.BMedian -lt 400)
        $result.products += $entry
    }
    $allBoth = (@($result.products | Where-Object { -not $_.bothUnder400 }).Count -eq 0)
    if ($loadNote) {
        $result.verdict = 'REFERENCE_ONLY (load environment, not an acceptance result)'
    } else {
        $result.verdict = $(if ($allBoth) { 'PASS (both harness medians <400 in one quiet window)' } else { 'CALIBRATION_SENSITIVE (at least one harness median >=400)' })
    }
} catch {
    if ($_.Exception.Message -eq 'QUIET_WINDOW_REQUIRED') { $result.verdict = 'QUIET_WINDOW_REQUIRED' }
    else { $result.notes += ('EXCEPTION: ' + $_.Exception.Message); $result.verdict = 'ERROR' }
} finally {
    Write-JsonFile $jsonPath $result
    Write-Chaos '---- summary'
    Write-Chaos ('build: ' + ($buildHashes.Keys -join ', '))
    foreach ($entry in $result.products) {
        Write-Chaos ('  ' + $entry.productTag + ': A=' + $entry.AMedian + ' [' + $entry.AMin + ',' + $entry.AMax + ']  B=' +
                     $entry.BMedian + ' [' + $entry.BMin + ',' + $entry.BMax + ']  delta=' + $entry.deltaMs + ' ms  both<400=' + $entry.bothUnder400)
    }
    Write-Chaos ('verdict: ' + $result.verdict)
    Write-Chaos ('json: ' + $jsonPath)
}
if ($result.verdict -like 'PASS*') { exit 0 }
if ($result.verdict -like 'QUIET_WINDOW_REQUIRED*') { exit 4 }
exit 3
