# 25-batch-export.ps1 - clean-state batch draw, one-shot read, SelectionSet batch, DXF export (pure ASCII)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib-com.ps1"

$global:RES = New-Object System.Collections.ArrayList
function T($name, [scriptblock]$sb) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = & $sb; $sw.Stop()
        if ($null -eq $r) { $r = '<null>' }
        if ($r -is [array]) { $r = '[' + (($r | ForEach-Object { "$_" }) -join ';') + ']' }
        [void]$global:RES.Add("RESULT`t$name`tOK`t$($sw.ElapsedMilliseconds)`t$r")
    } catch {
        $sw.Stop(); $hr = ''; try { $hr = '0x{0:X8}' -f $_.Exception.HResult } catch {}
        [void]$global:RES.Add("RESULT`t$name`tERR`t$($sw.ElapsedMilliseconds)`t$($_.Exception.Message) hr=$hr")
    }
}
function PollVar($doc, [string]$name, [int]$timeoutMs = 120000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $v = $doc.GetLispVariable($name)
        if ($v -ne $false -and $null -ne $v) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds; v = $v } }
        Start-Sleep -Milliseconds 25
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds; v = $null }
}
$dir = $script:ProbeTmp
Get-ChildItem $dir -Include '*.dxf', '*.dwg', '*.pdf', '*.bmp', '*.png', '*.wmf' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tversion=$($app.Version)")
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
[void]$doc.SetVariable('FILEDIA', 0); [void]$doc.SetVariable('CMDDIA', 0)

$TIM0 = '(* 86400.0 1000.0 (getvar "DATE"))'

# ---------- write side: one SendCommand per batch ----------
T 'batch-1000-lines-inline' {
    $b = $doc.ModelSpace.Count
    $expr = '(progn (setvar "CMDECHO" 0) (setvar "OSMODE" 0) (setq t0 ' + $TIM0 + ' i 0) (while (< i 1000) (entmake (list (cons 0 "LINE") (cons 10 (list (* 1.0 i) 0.0 0.0)) (cons 11 (list (* 1.0 i) 10.0 0.0)))) (setq i (1+ i))) (setq PRB_MS1 (- ' + $TIM0 + ' t0) PRB_N1 i))'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_MS1' 60000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) pollMs=$($r.ms) inCadMs=$($r.v) n=$($doc.GetLispVariable('PRB_N1')) delta=$($doc.ModelSpace.Count - $b) lastprompt=[$($doc.GetVariable('LASTPROMPT'))]"
}
T 'batch-10000-lines-inline' {
    $b = $doc.ModelSpace.Count
    $expr = '(progn (setq t0 ' + $TIM0 + ' i 0) (while (< i 10000) (entmake (list (cons 0 "LINE") (cons 10 (list (* 1.0 i) 0.0 0.0)) (cons 11 (list (* 1.0 i) 10.0 0.0)))) (setq i (1+ i))) (setq PRB_MS2 (- ' + $TIM0 + ' t0) PRB_N2 i))'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_MS2' 180000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) pollMs=$($r.ms) inCadMs=$($r.v) n=$($doc.GetLispVariable('PRB_N2')) delta=$($doc.ModelSpace.Count - $b)"
}
T 'count-after-batches' { "comCount=$($doc.ModelSpace.Count)" }

# ---------- read side: one-shot dump of ALL entities as one string ----------
T 'read-oneshot-handles' {
    $expr = '(progn (setq t0 ' + $TIM0 + ' s "" e (entnext) n 0) (while e (setq s (strcat s (cdr (assoc 5 (entget e))) ",") n (1+ n) e (entnext e))) (setq PRB_H s PRB_HN n PRB_HMS (- ' + $TIM0 + ' t0)))'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_H' 180000
    $sw2.Stop()
    $s = [string]$r.v
    "wallMs=$($sw2.ElapsedMilliseconds) inCadMs=$($doc.GetLispVariable('PRB_HMS')) n=$($doc.GetLispVariable('PRB_HN')) strLen=$($s.Length)"
}
T 'read-oneshot-fullcsv' {
    $expr = '(progn (setq t0 ' + $TIM0 + ' s "" e (entnext) n 0) (while e (setq d (entget e) p1 (cdr (assoc 10 d)) p2 (cdr (assoc 11 d))) (setq s (strcat s (cdr (assoc 5 d)) "|" (cdr (assoc 0 d)) "|" (cdr (assoc 8 d)) "|" (if p1 (strcat (rtos (car p1) 2 3) "," (rtos (cadr p1) 2 3)) "") "|" (if p2 (strcat (rtos (car p2) 2 3) "," (rtos (cadr p2) 2 3)) "") ";") n (1+ n) e (entnext e))) (setq PRB_D s PRB_DN n PRB_DMS (- ' + $TIM0 + ' t0)))'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_D' 240000
    $sw2.Stop()
    $s = [string]$r.v
    "wallMs=$($sw2.ElapsedMilliseconds) inCadMs=$($doc.GetLispVariable('PRB_DMS')) rows=$($doc.GetLispVariable('PRB_DN')) strLen=$($s.Length)"
}
# ---------- read side: COM enumeration baseline ----------
T 'read-com-enumerate-10000' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $n = 0; $handles = New-Object System.Collections.Generic.List[string]
    foreach ($e in $doc.ModelSpace) { $handles.Add($e.Handle); $n++; if ($n -ge 10000) { break } }
    $sw2.Stop()
    "n=$n ms=$($sw2.ElapsedMilliseconds) per=$([math]::Round($sw2.ElapsedMilliseconds/[math]::Max($n,1),3))ms"
}

# ---------- DXF export (count reconciliation) ----------
T 'Export-DXF' {
    $p = Join-Path $dir 'export.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'DXF')
    $sw2.Stop()
    $sz = if (Test-Path $p) { (Get-Item $p).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) bytes=$sz exists=$(Test-Path $p)"
}
T 'SaveAs-DXF-2007-code37' {
    $p = Join-Path $dir 'saveas_2007.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SaveAs($p, 37)
    $sw2.Stop()
    $sz = if (Test-Path $p) { (Get-Item $p).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) bytes=$sz name=$($doc.Name) full=[$($doc.FullName)]"
}
T 'file-headers' {
    $o = @()
    foreach ($f in (Get-ChildItem $dir -Include '*.dxf' -File -ErrorAction SilentlyContinue)) {
        $fs = [System.IO.File]::OpenRead($f.FullName); $buf = New-Object byte[] 24; $n = $fs.Read($buf, 0, 24); $fs.Close()
        $hex = (($buf[0..15] | ForEach-Object { '{0:X2}' -f $_ }) -join '')
        $asc = ([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) -replace '[^\x20-\x7E]', '.'
        $o += "$($f.Name) bytes=$($f.Length) hex=$hex asc=[$asc]"
    }
    ($o -join ' || ')
}

# ---------- SelectionSet batch (B12) ----------
T 'selectionset-add-and-selectall' {
    $ss = $doc.SelectionSets.Add('PRBSS')
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $ss.Select(11)
    $sw2.Stop()
    "ms=$($sw2.ElapsedMilliseconds) count=$($ss.Count)"
}
T 'selectionset-filter-by-layer' {
    $ss = $doc.SelectionSets.Item('PRBSS')
    $ss.Clear()
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $ss.Select(11, $null, $null, 8, 'PRB_NOSUCH')
    $sw2.Stop()
    "ms=$($sw2.ElapsedMilliseconds) count=$($ss.Count)"
}
T 'selectionset-erase-10000' {
    $ss = $doc.SelectionSets.Item('PRBSS')
    $ss.Clear()
    $ss.Select(11)
    $c0 = $ss.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $ss.Erase()
    $sw2.Stop()
    $c1 = $doc.ModelSpace.Count
    "selected=$c0 eraseMs=$($sw2.ElapsedMilliseconds) countAfter=$c1"
}
T 'selectionset-cleanup' {
    while ($doc.SelectionSets.Count -gt 0) { $doc.SelectionSets.Item(0).Delete() }
    "selSets=$($doc.SelectionSets.Count)"
}

# ---------- layer / XData / HandleToObject ----------
T 'layer-add-and-props' {
    $lay = $doc.Layers.Add('AI_TMP')
    $lay.Color = 3
    $doc.ActiveLayer = $lay
    "name=$($lay.Name) color=$($lay.Color) active=$($doc.ActiveLayer.Name) layers=$($doc.Layers.Count)"
}
T 'xdata-set-get' {
    $p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(1.0, 1.0, 0.0)
    $e = $ms.AddLine($p1, $p2)
    $xd = [object[]]@('PRB_APP', [object[]]@([object[]]@(1000, 'ai-marker'), [object[]]@(1001, 'run1')))
    $e.SetXData($xd)
    $r = $e.GetXData('PRB_APP')
    "type=$($r.GetType().Name) raw=[$($r -join '|')] handle=$($e.Handle)"
}
T 'handle-to-object' {
    $p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(2.0, 2.0, 0.0)
    $e = $ms.AddLine($p1, $p2)
    $h = $e.Handle
    $o = $doc.HandleToObject($h)
    "handle=$h objName=$($o.ObjectName) layer=$($o.Layer) sameType=$($o.ObjectName -eq $e.ObjectName)"
}
T 'zoom-extents-app' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $app.ZoomExtents()
    $sw2.Stop(); "ms=$($sw2.ElapsedMilliseconds)"
}
T 'statusbar-write' { $app.StatusBar = 'AI: 25-batch-export done'; Start-Sleep -Milliseconds 150; $app.StatusBar }

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== FILES ====="
Get-ChildItem $dir -Include '*.dxf', '*.dwg' -File -ErrorAction SilentlyContinue | Select-Object Name, Length | Sort-Object Name | Format-Table -AutoSize | Out-String

"===== CLEAN EXIT ====="
try { $doc.Close($false); "doc.Close(false) OK" } catch { "doc.Close ERR: $($_.Exception.Message)" }
Start-Sleep -Milliseconds 500
try { $app.Quit(); "app.Quit OK" } catch { "app.Quit ERR: $($_.Exception.Message)" }
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw2.ElapsedMilliseconds -lt 20000) { if (@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0) { break }; Start-Sleep -Milliseconds 100 }
"exitMs=$($sw2.ElapsedMilliseconds)"
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle)" } } else { "LEFTOVER_PROCS=0" }
