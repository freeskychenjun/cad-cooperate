# 23-diagnose.ps1 - why LISP batch failed: LASTPROMPT, entmake rules, command-in-LISP, clean close (pure ASCII)
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
function WaitUntil([scriptblock]$cond, [int]$timeoutMs = 30000, [int]$pollMs = 20) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { try { if (& $cond) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds } } } catch {}; Start-Sleep -Milliseconds $pollMs }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds }
}
$dir = $script:ProbeTmp
$fwd = $dir.Replace('\', '/')
$src = (Get-Content "$PSScriptRoot\probe1.lsp" -Raw).Replace('@@PRB_DIR@@', $fwd)
$lspPath = Join-Path $dir 'probe1.lsp'
[System.IO.File]::WriteAllText($lspPath, $src, [System.Text.Encoding]::ASCII)
$lspFwd = $lspPath.Replace('\', '/')

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tversion=$($app.Version)")
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
[void]$doc.SetVariable('FILEDIA', 0); [void]$doc.SetVariable('CMDDIA', 0)

T 'LASTPROMPT-supported' { $doc.GetVariable('LASTPROMPT') }

# --- 1. plain inline LISP loop (proper reals, no layer) ---
T 'inline-loop-10-points' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(progn (setq i 0) (while (< i 10) (entmake (list (cons 0 "POINT") (cons 10 (list (* 1.0 i) 0.0 0.0)))) (setq i (1+ i))) (setq PRB_OK1 "done"))' + "`n")
    $a = $doc.ModelSpace.Count
    "before=$b after=$a delta=$($a-$b) var=$($doc.GetLispVariable('PRB_OK1')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 2. entmake with nonexistent layer ---
T 'entmake-nonexistent-layer' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(progn (setq r (entmake (list (cons 0 "POINT") (cons 10 (list 1.0 1.0 0.0)) (cons 8 "PRB_NOSUCH")))) (setq PRB_OK2 (if r "made" "nil")))' + "`n")
    "delta=$($doc.ModelSpace.Count - $b) var=$($doc.GetLispVariable('PRB_OK2')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 3. custom LISP command invocation after load ---
T 'load-lsp' {
    $doc.SendCommand('(load "' + $lspFwd + '")' + "`n")
    Start-Sleep -Milliseconds 500
    $doc.SendCommand('PRBHELLO' + "`n")
    Start-Sleep -Milliseconds 500
    "hello=$($doc.GetLispVariable('PRB_HELLO')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 4. custom command with loop, PRB_N small ---
T 'custom-cmd-PRB-MKN-10' {
    $doc.SetLispVariable('PRB_N', 10)
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('PRB-MKN' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_MKN_N') -ne $false } 15000
    "waited=$($w.ms) inCadMs=$($doc.GetLispVariable('PRB_MKN_MS')) n=$($doc.GetLispVariable('PRB_MKN_N')) delta=$($doc.ModelSpace.Count - $b) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 5. (command ...) inside SendCommand LISP ---
T 'command-call-inline' {
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('(progn (command "_.CIRCLE" (list 5.0 5.0 0.0) 2.0) (setq PRB_OK5 "done"))' + "`n")
    Start-Sleep -Milliseconds 600
    "delta=$($doc.ModelSpace.Count - $b) var=$($doc.GetLispVariable('PRB_OK5')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 6. custom command that uses (command ...) ---
T 'custom-cmd-PRB-MKC-5' {
    $doc.SetLispVariable('PRB_N', 5)
    $b = $doc.ModelSpace.Count
    $doc.SendCommand('PRB-MKC' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_MKC_MS') -ne $false } 15000
    "waited=$($w.ms) inCadMs=$($doc.GetLispVariable('PRB_MKC_MS')) delta=$($doc.ModelSpace.Count - $b) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 7. deliberately broken LISP -> does LASTPROMPT show the error? ---
T 'broken-lisp-error-text' {
    $doc.SendCommand('(progn (setq PRB_OK7 (nosuchfn 1)) (princ))' + "`n")
    Start-Sleep -Milliseconds 600
    "var=$($doc.GetLispVariable('PRB_OK7')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
T 'broken-lisp-syntax' {
    $doc.SendCommand('(progn (setq PRB_OK8 (+ 1 2)' + "`n")
    Start-Sleep -Milliseconds 800
    "LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))] count=$($doc.ModelSpace.Count)"
}
# --- 8. multi-command in one SendCommand ---
T 'multi-command-one-send' {
    $doc.SendCommand('(setq PRB_M1 1)' + "`n" + '(setq PRB_M2 2)' + "`n" + '(setq PRB_M3 (+ PRB_M1 PRB_M2))' + "`n")
    Start-Sleep -Milliseconds 600
    "m1=$($doc.GetLispVariable('PRB_M1')) m2=$($doc.GetLispVariable('PRB_M2')) m3=$($doc.GetLispVariable('PRB_M3'))"
}
# --- 9. batch 1000 inline (no layer, reals) ---
T 'inline-batch-1000' {
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('(progn (setq i 0) (while (< i 1000) (entmake (list (cons 0 "LINE") (cons 10 (list (* 1.0 i) 0.0 0.0)) (cons 11 (list (* 1.0 i) 10.0 0.0)))) (setq i (1+ i))) (setq PRB_B1 "done"))' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_B1') -ne $false } 60000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) ok=$($w.ok) delta=$($doc.ModelSpace.Count - $b) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 10. generated LISP text with explicit float literals, 500 lines ---
T 'generated-lisp-500-float-literals' {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('(progn')
    for ($i = 0; $i -lt 500; $i++) {
        $x = '{0:F1}' -f [double]$i
        [void]$sb.Append("(entmake (list (cons 0 `"LINE`") (cons 10 (list $x 200.0 0.0)) (cons 11 (list $x 210.0 0.0))))")
    }
    [void]$sb.Append('(setq PRB_B2 "done"))')
    $expr = $sb.ToString()
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_B2') -ne $false } 60000
    $sw2.Stop()
    "cmdLen=$($expr.Length) wallMs=$($sw2.ElapsedMilliseconds) ok=$($w.ok) delta=$($doc.ModelSpace.Count - $b) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}
# --- 11. SCRIPT (.scr) channel ---
T 'script-file-channel' {
    $scr = Join-Path $dir 'test.scr'
    $lines = @('(setq PRB_SCR "from-script")', '(setq PRB_SCR2 (getvar "ACADVER"))', '')
    [System.IO.File]::WriteAllText($scr, ($lines -join "`r`n"), [System.Text.Encoding]::ASCII)
    $scrFwd = $scr.Replace('\', '/')
    $doc.SendCommand('_.SCRIPT' + "`n" + $scrFwd + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_SCR') -ne $false } 15000
    "waited=$($w.ms) scr=$($doc.GetLispVariable('PRB_SCR')) ver=$($doc.GetLispVariable('PRB_SCR2')) LASTPROMPT=[$($doc.GetVariable('LASTPROMPT'))]"
}

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== CLEAN EXIT TEST ====="
"procsBefore=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"
try { $doc.Close($false); "doc.Close(false) OK" } catch { "doc.Close(false) ERR: $($_.Exception.Message)" }
Start-Sleep -Milliseconds 800
"procsAfterClose=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count) docsLeft=$($app.Documents.Count)"
try { $app.Quit(); "app.Quit OK" } catch { "app.Quit ERR: $($_.Exception.Message)" }
$w = WaitUntil { @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0 } 15000 100
"exited=$($w.ok) afterMs=$($w.ms)"
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle)" } } else { "LEFTOVER_PROCS=0" }
