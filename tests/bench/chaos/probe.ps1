# probe.ps1 - E3 regression probes (run AFTER chaos, own 2025 instance only):
#   1) overlay-84 : draw the real 4-series overlay model (expected 84 entities) on ZWCAD 2025,
#                   capture section verify + read, so cross_app_diff can be run against the
#                   archived products' reads;
#   2) silent-latency : reproduce the "write succeeded but took ~20 s with zero warnings" case
#                   (C1's invalid-injection side effect) and show what stability/lock/busy
#                   fields and warnings the product now reports.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\probe.ps1 -Case overlay,latency

param(
    [string[]]$Case = @('overlay', 'latency'),
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string]$OutDir = 'tmp\bench\e3',
    [int]$TimeoutSec = 240,
    [switch]$NoFreeze
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

$Case = @($Case | ForEach-Object { [string]$_ } | ForEach-Object { $_.Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$root = $script:ChaosRoot
$runRoot = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$jsonPath = Join-Path $runRoot 'probe.json'
$result = [ordered]@{ schema = 'zwcad-chaos/1'; kind = 'probe'; generatedAt = (Get-Date).ToString('o')
    product = $ProgId; cases = @(); frozen = $null }

$cad = $null
try {
    if (-not $NoFreeze) { $result.frozen = New-FrozenProduct $runRoot }
    $cad = Start-OwnCad $ProgId $TimeoutSec
    Write-Chaos ('own instance pid ' + $cad.pid)

    # ---- overlay-84
    if ($Case -contains 'overlay') {
        $caseDir = Join-Path $runRoot 'overlay'
        New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
        $work = New-WorkDrawing 'blank.dxf' $caseDir 'overlay'
        $open = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
        $model = Join-Path $root 'tmp\real\db-overlay-<站码>.model.json'
        $sec = Invoke-Cad @('section', '-Doc', $work, '-Data', $model) $TimeoutSec $ProgId
        $expected = Get-PropSafe $sec.parsed 'result.expected'
        $verify = Get-PropSafe $sec.parsed 'result.verify'
        $total = -1
        try { $total = [int](Get-PropSafe $expected 'total') } catch { }
        $read = Invoke-Cad @('read', '-Doc', $work, '-Mode', 'geom', '-Max', '20000') $TimeoutSec $ProgId
        $readPath = Join-Path $caseDir 'read-zwcad2025.json'
        [System.IO.File]::WriteAllText($readPath, $read.text, (New-Object System.Text.UTF8Encoding($false)))
        $cnt = 0
        if ($read.parsed) { $cnt = @(Get-PropSafe $read.parsed 'result.entities').Count }
        $rec = [ordered]@{ case = 'overlay-84'; openExit = $open.exit; sectionExit = $sec.exit
            expectedTotal = $total; sectionVerifyOk = (Get-PropSafe $verify 'ok')
            readExit = $read.exit; readCount = $cnt; readPath = $readPath
            matched = ($sec.exit -eq 0 -and $total -eq 84 -and (Get-PropSafe $verify 'ok') -eq $true -and $cnt -eq 84) }
        Write-Chaos ('overlay: sectionExit=' + $sec.exit + ' expected.total=' + $total + ' verify.ok=' + [string](Get-PropSafe $verify 'ok') + ' readCount=' + $cnt)
        $result.cases += $rec
    }

    # ---- silent-latency reproduction (C1 invalid-injection side effect)
    if ($Case -contains 'latency') {
        $caseDir = Join-Path $runRoot 'latency'
        New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
        $work = New-WorkDrawing 'blank.dxf' $caseDir 'latency'
        $null = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
        # same shape as C1's failing attempt: hold an injector, then write while it is "pending"
        $injector = Start-InjectAsync $ProgId $work 'sendcmd' '_.LINE ' 40
        Start-Sleep -Milliseconds 1300
        $items = New-TextItems (Join-Path $caseDir 'lat-1.json') 'LAT' 1 'CHAOS'
        $write = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) $TimeoutSec $ProgId
        Stop-InjectAsync $injector
        $null = Invoke-Inject $ProgId $work 'escape' '' 30
        $warnings = @(Get-PropSafe $write.parsed 'warnings')
        $codes = @($warnings | ForEach-Object { [string](Get-PropSafe $_ 'code') })
        $rec = [ordered]@{ case = 'silent-latency'; writeExit = $write.exit; wallMs = $write.wallMs
            errorCode = [string](Get-PropSafe $write.parsed 'error.code'); warnings = $codes
            transport = [string](Get-PropSafe $write.parsed 'transport')
            elapsedMs = (Get-PropSafe $write.parsed 'elapsedMs')
            lock = (Get-PropSafe $write.parsed 'result.lock')
            busy = (Get-PropSafe $write.parsed 'result.busy')
            stability = (Get-PropSafe $write.parsed 'result.stability')
            explained = (@($codes | Where-Object { $_ -match 'LOCK_WAITED|BUSY_RETRIED|DAEMON_START_WAIT' }).Count -gt 0)
            matched = ((@($codes | Where-Object { $_ -match 'LOCK_WAITED|BUSY_RETRIED|DAEMON_START_WAIT' }).Count -gt 0)) }
        Write-Chaos ('latency: exit=' + $write.exit + ' wall=' + [int]$write.wallMs + 'ms warnings=' + ($codes -join ',') +
                     ' stability=' + (($rec.stability | ConvertTo-Json -Compress -Depth 5)))
        $result.cases += $rec
    }
} catch {
    $result['error'] = $_.Exception.Message
} finally {
    if ($cad -and -not $Keep) {
        $result['hygiene'] = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
        Write-Chaos ('hygiene: remaining=' + $result.hygiene.remaining)
    }
    Write-JsonFile $jsonPath $result
    Write-Chaos ('probe json: ' + $jsonPath)
}
