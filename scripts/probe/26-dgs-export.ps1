# 26-dgs-export.ps1 - DGS encryption of CAD-written files, Export variants, write-scale, undo recheck (pure ASCII)
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
function PollVar($doc, [string]$name, [int]$timeoutMs = 60000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $v = $doc.GetLispVariable($name)
        if ($v -ne $false -and $null -ne $v) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds; v = $v } }
        Start-Sleep -Milliseconds 25
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds; v = $null }
}
function FileHead([string]$p) {
    if (-not (Test-Path $p)) { return "MISSING" }
    $fs = [System.IO.File]::OpenRead($p); $buf = New-Object byte[] 24; $n = $fs.Read($buf, 0, 24); $fs.Close()
    $hex = (($buf[0..15] | ForEach-Object { '{0:X2}' -f $_ }) -join '')
    $asc = ([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) -replace '[^\x20-\x7E]', '.'
    $dgs = if ($hex.StartsWith('17DA5FA01633CD9A')) { 'DGS_ENCRYPTED' } else { 'plaintext' }
    "$([System.IO.Path]::GetFileName($p)) bytes=$((Get-Item $p).Length) $dgs hex=$hex asc=[$asc]"
}

$tempDir = $script:ProbeTmp
$wsDir = '<repo>\tmp'
$tempFwd = $tempDir.Replace('\', '/')
$wsFwd = $wsDir.Replace('\', '/')
Get-ChildItem $wsDir -Include '*.dxf', '*.dwg', '*.txt' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tversion=$($app.Version)")
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
[void]$doc.SetVariable('FILEDIA', 0); [void]$doc.SetVariable('CMDDIA', 0)

# ---------- DGS: LISP writes .txt / .csv to two locations ----------
T 'lisp-write-txt-temp' {
    $p = ($tempDir + '\from_lisp_temp.txt').Replace('\', '/')
    $doc.SendCommand('(progn (setq f (open "' + $p + '" "w")) (write-line "hello-plaintext-from-lisp" f) (close f) (setq PRB_W1 "ok"))' + "`n")
    $r = PollVar $doc 'PRB_W1' 15000
    "ms=$($r.ms) $(FileHead ($tempDir + '\from_lisp_temp.txt'))"
}
T 'lisp-write-txt-workspace' {
    $p = ($wsDir + '\from_lisp_ws.txt').Replace('\', '/')
    $doc.SendCommand('(progn (setq f (open "' + $p + '" "w")) (write-line "hello-from-lisp-workspace" f) (close f) (setq PRB_W2 "ok"))' + "`n")
    $r = PollVar $doc 'PRB_W2' 15000
    "ms=$($r.ms) $(FileHead ($wsDir + '\from_lisp_ws.txt'))"
}
T 'lisp-write-dxf-fakeext-temp' {
    # write DXF-looking content with .dxf extension from LISP
    $p = ($tempDir + '\lisp_written.dxf').Replace('\', '/')
    $doc.SendCommand('(progn (setq f (open "' + $p + '" "w")) (write-line "0" f) (write-line "SECTION" f) (close f) (setq PRB_W3 "ok"))' + "`n")
    $r = PollVar $doc 'PRB_W3' 15000
    "ms=$($r.ms) $(FileHead ($tempDir + '\lisp_written.dxf'))"
}
T 'lisp-write-txt-dxf-content' {
    $p = ($tempDir + '\lisp_txt_dxfcontent.txt').Replace('\', '/')
    $doc.SendCommand('(progn (setq f (open "' + $p + '" "w")) (write-line "0" f) (write-line "SECTION" f) (write-line "2" f) (write-line "HEADER" f) (close f) (setq PRB_W4 "ok"))' + "`n")
    $r = PollVar $doc 'PRB_W4' 15000
    "ms=$($r.ms) $(FileHead ($tempDir + '\lisp_txt_dxfcontent.txt'))"
}

# ---------- build 3000 entities then test SaveAs / Export to both locations ----------
T 'build-3000' {
    $expr = '(progn (setq i 0) (while (< i 3000) (entmake (list (cons 0 "LINE") (cons 10 (list (* 1.0 i) 0.0 0.0)) (cons 11 (list (* 1.0 i) 10.0 0.0)))) (setq i (1+ i))) (setq PRB_B9 "ok"))'
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_B9' 60000
    "ms=$($r.ms) count=$($doc.ModelSpace.Count)"
}
T 'SaveAs-DXF2007-workspace' {
    $p = Join-Path $wsDir 'saveas_ws_2007.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SaveAs($p, 37)
    $sw2.Stop()
    "ms=$($sw2.ElapsedMilliseconds) $(FileHead $p) docName=$($doc.Name)"
}
T 'Export-DXF-variant1-two-args' {
    $p = Join-Path $wsDir 'export_v1.dxf'
    $doc.Export($p, 'DXF')
    "exists=$(Test-Path $p) $(if (Test-Path $p) { FileHead $p } else { '' })"
}
T 'Export-DXF-variant2-null-ss' {
    $p = Join-Path $wsDir 'export_v2.dxf'
    $doc.Export($p, 'DXF', $null)
    "exists=$(Test-Path $p)"
}
T 'Export-DXF-variant3-missing' {
    $p = Join-Path $wsDir 'export_v3.dxf'
    $miss = [System.Reflection.Missing]::Value
    $doc.Export($p, 'DXF', $miss)
    "exists=$(Test-Path $p)"
}

# ---------- write scale: generated LISP text with explicit reals ----------
T 'generated-lisp-500-explicit-reals' {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('(progn')
    for ($i = 0; $i -lt 500; $i++) {
        $x = '{0:F1}' -f ([double]$i)
        [void]$sb.Append("(entmake (list (cons 0 `"LINE`") (cons 10 (list $x 300.0 0.0)) (cons 11 (list $x 310.0 0.0))))")
    }
    [void]$sb.Append('(setq PRB_G1 "ok"))')
    $expr = $sb.ToString()
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_G1' 60000
    $sw2.Stop()
    "cmdLen=$($expr.Length) wallMs=$($sw2.ElapsedMilliseconds) ok=$($r.ok) delta=$($doc.ModelSpace.Count - $b) lastprompt=[$($doc.GetVariable('LASTPROMPT'))]"
}
T 'generated-lisp-2000-explicit-reals' {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('(progn')
    for ($i = 0; $i -lt 2000; $i++) {
        $x = '{0:F1}' -f ([double]$i)
        [void]$sb.Append("(entmake (list (cons 0 `"LINE`") (cons 10 (list $x 400.0 0.0)) (cons 11 (list $x 410.0 0.0))))")
    }
    [void]$sb.Append('(setq PRB_G2 "ok"))')
    $expr = $sb.ToString()
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_G2' 90000
    $sw2.Stop()
    "cmdLen=$($expr.Length) wallMs=$($sw2.ElapsedMilliseconds) ok=$($r.ok) delta=$($doc.ModelSpace.Count - $b)"
}

# ---------- undo U4 recheck: two marks, one U ----------
T 'undo-U4-recheck' {
    $p1 = [double[]]@(0.0, 500.0, 0.0); $p2 = [double[]]@(1.0, 500.0, 0.0)
    $b = $doc.ModelSpace.Count
    $doc.StartUndoMark(); for ($i = 0; $i -lt 5; $i++) { $p1[0] = [double](900 + $i); $p2[0] = [double](901 + $i); $null = $ms.AddLine($p1, $p2) }; $doc.EndUndoMark()
    $a1 = $doc.ModelSpace.Count
    $doc.StartUndoMark(); for ($i = 0; $i -lt 5; $i++) { $p1[0] = [double](950 + $i); $p2[0] = [double](951 + $i); $null = $ms.AddLine($p1, $p2) }; $doc.EndUndoMark()
    $a2 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n"); Start-Sleep -Milliseconds 500
    $a3 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n"); Start-Sleep -Milliseconds 500
    $a4 = $doc.ModelSpace.Count
    "base=$b afterMark1=$a1 afterMark2=$a2 afterU1=$a3 afterU2=$a4"
}
T 'undo-U5-single-sendcommand-10000-ents-one-U' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(progn (setq i 0) (while (< i 100) (entmake (list (cons 0 "POINT") (cons 10 (list (* 1.0 i) 600.0 0.0)))) (setq i (1+ i))) (setq PRB_U5 "ok"))' + "`n")
    $r = PollVar $doc 'PRB_U5' 30000
    $a1 = $doc.ModelSpace.Count
    $doc.SendCommand('_.U' + "`n"); Start-Sleep -Milliseconds 600
    $a2 = $doc.ModelSpace.Count
    "base=$b after=$a1 afterU=$a2 deltaAfterU=$($a2-$b)"
}
# ---------- IsQuiescent during a long operation ----------
T 'isquiescent-during-batch' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('(progn (setq i 0) (while (< i 30000) (entmake (list (cons 0 "POINT") (cons 10 (list (* 1.0 i) 700.0 0.0)))) (setq i (1+ i))) (setq PRB_Q "ok"))' + "`n")
    $samples = @()
    for ($k = 0; $k -lt 6; $k++) {
        $st = $app.GetAcadState()
        $samples += [string]$st.IsQuiescent
        Start-Sleep -Milliseconds 120
    }
    $r = PollVar $doc 'PRB_Q' 120000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) ok=$($r.ok) isQuiescentSamples=[$($samples -join ',')]"
}

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== FILE HEADERS (final) ====="
Get-ChildItem $tempDir, $wsDir -Include '*.dxf', '*.txt', '*.csv' -File -ErrorAction SilentlyContinue | ForEach-Object { FileHead $_.FullName }
""
"===== CLEAN EXIT ====="
try { $doc.Close($false); "doc.Close(false) OK" } catch { "doc.Close ERR: $($_.Exception.Message)" }
Start-Sleep -Milliseconds 400
try { $app.Quit(); "app.Quit OK" } catch { "app.Quit ERR: $($_.Exception.Message)" }
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw2.ElapsedMilliseconds -lt 20000) { if (@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0) { break }; Start-Sleep -Milliseconds 100 }
"exitMs=$($sw2.ElapsedMilliseconds)"
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle)" } } else { "LEFTOVER_PROCS=0" }
