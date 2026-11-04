# 21-write-undo.ps1 - COM entity creation baseline, undo attribution, LISP channel, clean quit (pure ASCII)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib-com.ps1"

$global:RES = New-Object System.Collections.ArrayList
function T($name, [scriptblock]$sb) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = & $sb
        $sw.Stop()
        if ($null -eq $r) { $r = '<null>' }
        if ($r -is [array]) { $r = '[' + (($r | ForEach-Object { "$_" }) -join ';') + ']' }
        [void]$global:RES.Add("RESULT`t$name`tOK`t$($sw.ElapsedMilliseconds)`t$r")
    } catch {
        $sw.Stop()
        $hr = ''; try { $hr = '0x{0:X8}' -f $_.Exception.HResult } catch {}
        [void]$global:RES.Add("RESULT`t$name`tERR`t$($sw.ElapsedMilliseconds)`t$($_.Exception.Message) hr=$hr")
    }
}
function WaitUntil([scriptblock]$cond, [int]$timeoutMs = 60000, [int]$pollMs = 20) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { if (& $cond) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds } } } catch {}
        Start-Sleep -Milliseconds $pollMs
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds }
}
function Poll-Var($doc, [string]$name, $timeoutMs = 20000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $v = $doc.GetLispVariable($name)
        if ($v -ne $false -and $null -ne $v) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds; v = $v } }
        Start-Sleep -Milliseconds 20
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds; v = $null }
}

$dir = $script:ProbeTmp
$fwd = $dir.Replace('\', '/')
# materialize probe1.lsp with baked ASCII path
$src = Get-Content "$PSScriptRoot\probe1.lsp" -Raw
$src = $src.Replace('@@PRB_DIR@@', $fwd)
$lspPath = Join-Path $dir 'probe1.lsp'
[System.IO.File]::WriteAllText($lspPath, $src, [System.Text.Encoding]::ASCII)
$lspFwd = $lspPath.Replace('\', '/')
"APARTMENT=$([System.Threading.Thread]::CurrentThread.GetApartmentState())  lsp=$lspFwd"

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tversion=$($app.Version)")
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
[void]$doc.SetVariable('FILEDIA', 0); [void]$doc.SetVariable('CMDDIA', 0)

# ---------- 1. explicit EvaluateLispExpression (Document) + app.Eval ----------
T 'DOC-EvaluateLispExpression(+ 1 2)' { $doc.EvaluateLispExpression('(+ 1 2)') }
T 'DOC-EvaluateLispExpression(getvar)' { $doc.EvaluateLispExpression('(getvar "DIMSCALE")') }
T 'DOC-EvaluateLispExpression(strcat)' { $doc.EvaluateLispExpression('(strcat "a" "b")') }
T 'DOC-EvaluateLispExpression(entlast)' { $doc.EvaluateLispExpression('(progn (entmake (list (cons 0 "POINT") (cons 10 (list 1.0 1.0 0.0)))) (cdr (assoc 5 (entget (entlast)))))') }
T 'APP-Eval(+ 1 2)' { $app.Eval('(+ 1 2)') }
T 'APP-Eval(getvar)' { $app.Eval('(getvar "DIMSCALE")') }

# ---------- 2. SendCommand sync semantics ----------
T 'SendCommand-sync-newline' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "POINT") (cons 10 (list 2.0 2.0 0.0))))' + "`n")
    $a = $doc.ModelSpace.Count
    "before=$b immediatelyAfterReturn=$a delta=$($a-$b)"
}
T 'SendCommand-no-newline' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "POINT") (cons 10 (list 3.0 3.0 0.0))))')
    $a0 = $doc.ModelSpace.Count
    Start-Sleep -Milliseconds 1500
    $a1 = $doc.ModelSpace.Count
    "before=$b immediatelyAfter=$a0 after1.5s=$a1"
}
T 'SendCommand-trailing-space' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "POINT") (cons 10 (list 4.0 4.0 0.0)))) ')
    $a0 = $doc.ModelSpace.Count
    Start-Sleep -Milliseconds 800
    $a1 = $doc.ModelSpace.Count
    "before=$b immediatelyAfter=$a0 after0.8s=$a1"
}
T 'SendCommand-crlf' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "POINT") (cons 10 (list 5.0 5.0 0.0))))' + "`r`n")
    $a = $doc.ModelSpace.Count
    "before=$b after=$a"
}

# ---------- 3. LISP channel: load file + global var readback ----------
T 'lisp-load' {
    $doc.SendCommand('(load "' + $lspFwd + '")' + "`n")
    $r = Poll-Var $doc 'PRB_HELLO' 10000
    "loaded=$($r.ok) ms=$($r.ms) hello=$($r.v)"
}
T 'lisp-roundtrip-scalar' {
    $doc.SetLispVariable('PRB_N', 7)
    $doc.SendCommand('(setq PRB_R (* 6 PRB_N))' + "`n")
    $r = Poll-Var $doc 'PRB_R' 10000
    "ms=$($r.ms) value=$($r.v) (expect 42)"
}
T 'lisp-load-baked-path-check' { $doc.GetLispVariable('PRB_HELLO') }

# ---------- 4. COM entity creation baseline ----------
T 'com-AddLine-1' {
    $p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(10.0, 0.0, 0.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $e = $ms.AddLine($p1, $p2)
    $sw2.Stop(); "ms=$($sw2.ElapsedMilliseconds) type=$($e.ObjectName) handle=$($e.Handle) layer=$($e.Layer)"
}
T 'com-AddLine-100' {
    $p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(10.0, 0.0, 0.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 100; $i++) { $p1[0] = [double]$i; $p2[0] = [double]$i; $null = $ms.AddLine($p1, $p2) }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/100.0,3))ms"
}
T 'com-AddLightWeightPolyline-1' {
    $pts = [double[]]@(0.0, 0.0, 10.0, 0.0, 10.0, 10.0, 0.0, 10.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $pl = $ms.AddLightWeightPolyline($pts)
    $sw2.Stop(); "ms=$($sw2.ElapsedMilliseconds) type=$($pl.ObjectName) closed=$($pl.Closed) coords=$($pl.Coordinates -join ',')"
}
T 'com-AddText-1' {
    $ip = [double[]]@(5.0, 20.0, 0.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $tx = $ms.AddText('hello-cad', $ip, 2.5)
    $sw2.Stop(); "ms=$($sw2.ElapsedMilliseconds) type=$($tx.ObjectName) text=$($tx.TextString) height=$($tx.Height)"
}
T 'com-AddMText-1' {
    $ip = [double[]]@(5.0, 30.0, 0.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $tx = $ms.AddMText($ip, 50.0, 'mtext-hello')
    $sw2.Stop(); "ms=$($sw2.ElapsedMilliseconds) type=$($tx.ObjectName) text=$($tx.TextString)"
}
T 'com-AddText-100' {
    $ip = [double[]]@(0.0, 0.0, 0.0)
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 100; $i++) { $ip[0] = [double]$i; $null = $ms.AddText("t$i", $ip, 1.0) }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/100.0,3))ms"
}

# ---------- 5. UNDO attribution ----------
T 'undo-U1-com-addline-10-no-mark-then-U' {
    $b = $doc.ModelSpace.Count
    $p1 = [double[]]@(0.0, 50.0, 0.0); $p2 = [double[]]@(10.0, 50.0, 0.0)
    for ($i = 0; $i -lt 10; $i++) { $p1[0] = [double]($i + 300); $p2[0] = [double]($i + 300); $null = $ms.AddLine($p1, $p2) }
    $a1 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n")
    $w = WaitUntil { $doc.ModelSpace.Count -ne $a1 } 5000
    Start-Sleep -Milliseconds 300
    $a2 = $doc.ModelSpace.Count
    "base=$b after10=$a1 afterU=$a2 (deltaFromBase=$($a2-$b))"
}
T 'undo-U2-com-addline-10-in-marks-then-U' {
    $b = $doc.ModelSpace.Count
    $doc.StartUndoMark()
    $p1 = [double[]]@(0.0, 60.0, 0.0); $p2 = [double[]]@(10.0, 60.0, 0.0)
    for ($i = 0; $i -lt 10; $i++) { $p1[0] = [double]($i + 400); $p2[0] = [double]($i + 400); $null = $ms.AddLine($p1, $p2) }
    $doc.EndUndoMark()
    $a1 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n")
    $w = WaitUntil { $doc.ModelSpace.Count -ne $a1 } 5000
    Start-Sleep -Milliseconds 300
    $a2 = $doc.ModelSpace.Count
    "base=$b after10=$a1 afterU=$a2 (deltaFromBase=$($a2-$b))"
}
T 'undo-U3-one-sendcommand-10-ents-then-U' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(progn (setq i 0) (while (< i 10) (entmake (list (cons 0 "POINT") (cons 10 (list (float (+ i 500)) 70.0 0.0)))) (setq i (1+ i))) (princ))' + "`n")
    $w1 = WaitUntil { $doc.ModelSpace.Count -ge ($b + 10) } 8000
    $a1 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n")
    $w = WaitUntil { $doc.ModelSpace.Count -ne $a1 } 5000
    Start-Sleep -Milliseconds 300
    $a2 = $doc.ModelSpace.Count
    "base=$b after10=$a1 afterU=$a2 (deltaFromBase=$($a2-$b))"
}
T 'undo-U4-two-marks-one-U' {
    $b = $doc.ModelSpace.Count
    foreach ($round in 1..2) {
        $doc.StartUndoMark()
        $p1 = [double[]]@(0.0, 80.0, 0.0); $p2 = [double[]]@(1.0, 80.0, 0.0)
        for ($i = 0; $i -lt 5; $i++) { $p1[0] = [double]($i + 600 + $round * 10); $p2[0] = [double]($i + 601 + $round * 10); $null = $ms.AddLine($p1, $p2) }
        $doc.EndUndoMark()
    }
    $a1 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n")
    $w = WaitUntil { $doc.ModelSpace.Count -ne $a1 } 5000
    Start-Sleep -Milliseconds 300
    $a2 = $doc.ModelSpace.Count
    "base=$b after20=$a1 afterOneU=$a2 (deltaFromBase=$($a2-$b) expect -10 if marks group)"
}

# ---------- 6. batch draw: one SendCommand vs per-entity COM ----------
T 'batch-lisp-1000-entmake' {
    $doc.SetLispVariable('PRB_N', 1000)
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('PRB-MKN' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_MKN_MS') -ne $false } 60000
    $sw2.Stop()
    $inCad = $doc.GetLispVariable('PRB_MKN_MS')
    $n = $doc.GetLispVariable('PRB_MKN_N')
    "wallMs=$($sw2.ElapsedMilliseconds) inCadMs=$inCad created=$n countDelta=$($doc.ModelSpace.Count - $b)"
}
T 'batch-lisp-100-circles-command' {
    $doc.SetLispVariable('PRB_N', 100)
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('PRB-MKC' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_MKC_MS') -ne $false } 120000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) inCadMs=$($doc.GetLispVariable('PRB_MKC_MS')) countDelta=$($doc.ModelSpace.Count - $b)"
}
T 'batch-generated-lisp-500-lines-length-limit' {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('(progn')
    for ($i = 0; $i -lt 500; $i++) {
        [void]$sb.Append("(entmake (list (cons 0 `"LINE`") (cons 10 (list $([double]$i) 200.0 0.0)) (cons 11 (list $([double]$i) 210.0 0.0))))")
    }
    [void]$sb.Append('(princ))')
    $expr = $sb.ToString()
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $w = WaitUntil { $doc.ModelSpace.Count -ge ($b + 500) } 60000
    $sw2.Stop()
    "cmdLen=$($expr.Length) chars wallMs=$($sw2.ElapsedMilliseconds) ok=$($w.ok) countDelta=$($doc.ModelSpace.Count - $b) (expect 500)"
}

# ---------- readback: handles + full dump as one string ----------
T 'read-hstr-one-call' {
    $doc.SendCommand('PRB-HSTR' + "`n")
    $r = Poll-Var $doc 'PRB_HSTR' 30000
    $s = [string]$r.v
    "ms=$($r.ms) len=$($s.Length) handles=$((($s -split ',').Count - 1))"
}
T 'read-dump-one-call' {
    $doc.SendCommand('PRB-DUMP' + "`n")
    $r = Poll-Var $doc 'PRB_DUMP' 60000
    $s = [string]$r.v
    "ms=$($r.ms) len=$($s.Length) rows=$((($s -split ';').Count - 1))"
}
T 'read-dump-file' {
    $doc.SendCommand('PRB-DUMPF' + "`n")
    $r = Poll-Var $doc 'PRB_DUMPF' 60000
    $p = Join-Path $dir 'dump.csv'
    $sz = if (Test-Path $p) { (Get-Item $p).Length } else { -1 }
    "ms=$($r.ms) fileBytes=$sz"
}
T 'read-ModelSpace.Count' { $doc.ModelSpace.Count }

# ---------- 7. quit / leftover analysis ----------
[void]$global:RES.Add("RESULT`tpre-quit-procs`tOK`t0`tcount=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count) hwnd=$($app.HWND) mainWindowHandleOfProc=$((Get-Process -Id (Get-Process zwcad* | Select-Object -First 1).Id).MainWindowHandle)")
T 'quit-set-Saved-true' { $doc.Saved = $true; "Saved=$($doc.Saved)" }
T 'quit-call' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $app.Quit()
    $w = WaitUntil { @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0 } 15000 100
    $sw2.Stop()
    "quitReturnedMs=$($sw2.ElapsedMilliseconds) exited=$($w.ok) exitAfterMs=$($w.ms) procsLeft=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"
}

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle) title=$($_.MainWindowTitle)" } } else { "LEFTOVER_PROCS=0" }
