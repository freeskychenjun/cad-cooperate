# 20-core.ps1 - core capability probe: Eval-LISP, undo marks, quiescent, statusbar, hwnd, version (pure ASCII)
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
        $hr = ''
        try { $hr = '0x{0:X8}' -f $_.Exception.HResult } catch {}
        [void]$global:RES.Add("RESULT`t$name`tERR`t$($sw.ElapsedMilliseconds)`t$($_.Exception.Message) hr=$hr")
    }
}

"APARTMENT=$([System.Threading.Thread]::CurrentThread.GetApartmentState())  PS=$($PSVersionTable.PSVersion)  Is64=$([Environment]::Is64BitProcess)"

# ---------- create ----------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tvisible=$($app.Visible) version=$($app.Version)")

T 'app.Visible=false-then-true' { $app.Visible = $false; Start-Sleep -Milliseconds 300; $app.Visible = $true; 'ok' }
T 'app.HWND' { $app.HWND }
T 'app.Caption' { $app.Caption }
T 'app.Version' { $app.Version }
T 'app.Name' { $app.Name }
T 'app.FullName' { $app.FullName }
T 'app.Documents.Count' { $app.Documents.Count }
T 'app.ActiveDocument.Name' { $app.ActiveDocument.Name }

# member existence sweep (A1/A2/A3/B5/B6/B7)
$appMembers = @('GetAcadState','StatusBar','HWND','ZoomExtents','ZoomAll','ZoomWindow','Update','Eval','GetInterfaceObject','ListARX','LoadARX','UnloadARX','LoadZVB','UnloadZVB','Preferences','ActiveDocument','Documents','BeginUndoMark','EndUndoMark','Undo','Quit','Visible','Caption','WindowState','Width','Height')
$ids = [ComProbe]::DispIds($app, $appMembers)
$found = @(); $missing = @()
for ($i = 0; $i -lt $appMembers.Count; $i++) { if ($ids[$i] -ne -1) { $found += $appMembers[$i] } else { $missing += $appMembers[$i] } }
[void]$global:RES.Add("RESULT`tapp-members`tOK`t0`tFOUND=[$($found -join ',')] MISSING=[$($missing -join ',')]")

# ---------- document ----------
$doc = $app.ActiveDocument
T 'doc.Name' { $doc.Name }
T 'doc.FullName' { $doc.FullName }
T 'doc.ModelSpace.Count' { $doc.ModelSpace.Count }
T 'doc.WindowTitle()' { $doc.WindowTitle() }
T 'doc.ReadOnly' { $doc.ReadOnly }
T 'doc.Saved' { $doc.Saved }

$docMembers = @('EvaluateLispExpression','GetLispVariable','SetLispVariable','StartUndoMark','EndUndoMark','SendCommand','HandleToObject','ObjectIdToObject','GetVariable','SetVariable','Save','SaveAs','Export','Import','Plot','Regen','ZoomExtents','PurgeAll','WBlock','WindowTitle','SelectionSets','Utility','ModelSpace','PaperSpace','Layers','Blocks','Layouts','PlotConfigurations','Dictionaries','ActiveLayer','ActiveLinetype','ActiveDimStyle','ActiveTextStyle','ActiveViewport','MSpace','ReadOnly','Saved','Name','FullName','Path','Handle','ObjectID','PickfirstSelectionSet','SetVariable','GetVariable','AuditInfo','GetEntityType')
$ids = [ComProbe]::DispIds($doc, $docMembers)
$found = @(); $missing = @()
for ($i = 0; $i -lt $docMembers.Count; $i++) { if ($ids[$i] -ne -1) { $found += $docMembers[$i] } else { $missing += $docMembers[$i] } }
[void]$global:RES.Add("RESULT`tdoc-members`tOK`t0`tFOUND=[$($found -join ',')] MISSING=[$($missing -join ',')]")

# ---------- A1 EvaluateLispExpression: return value marshaling ----------
T 'eval-arith' { $doc.EvaluateLispExpression('(+ 1 2)') }
T 'eval-getvar-real' { $doc.EvaluateLispExpression('(getvar "DIMSCALE")') }
T 'eval-strcat' { $doc.EvaluateLispExpression('(strcat "ab" "cd")') }
T 'eval-list3' { $doc.EvaluateLispExpression('(list 1 2 3)') }
T 'eval-point' { $doc.EvaluateLispExpression('(list 1.5 2.5 0.0)') }
T 'eval-list-type' { $r = $doc.EvaluateLispExpression('(list 1 2 3)'); "type=$($r.GetType().FullName) count=$(@($r).Count)" }
T 'eval-nil' { $doc.EvaluateLispExpression('(progn nil)') }
T 'eval-err-unknown-func' { $doc.EvaluateLispExpression('(nonexistent-fn-xyz 1 2)') }
T 'eval-err-type' { $doc.EvaluateLispExpression('(+ 1 "a")') }

# ---------- A2 LispVariable channel ----------
T 'set-lispvar-num' { $doc.SetLispVariable('PRB_NUM', 42); $doc.GetLispVariable('PRB_NUM') }
T 'set-lispvar-str' { $doc.SetLispVariable('PRB_STR', 'hello-agent'); $doc.GetLispVariable('PRB_STR') }
T 'set-lispvar-list' { $doc.SetLispVariable('PRB_LIST', @(1, 2, 3)); $doc.GetLispVariable('PRB_LIST') }
T 'get-lispvar-unset' { $doc.GetLispVariable('PRB_UNSET_XYZ') }
T 'roundtrip-lisp-to-agent' { $doc.SetLispVariable('PRB_IN', 'v1'); $doc.EvaluateLispExpression('(strcat (getvar "DWGNAME") "|" PRB_IN)') }

# ---------- A1 perf: per-call COM/LISP round trip ----------
T 'perf-eval-100-arith' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 100; $i++) { [void]$doc.EvaluateLispExpression('(+ 1 2)') }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/100.0,3))ms"
}
T 'perf-getvariable-100' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 100; $i++) { [void]$doc.GetVariable('DIMSCALE') }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/100.0,3))ms"
}
T 'perf-setvariable-100' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 100; $i++) { [void]$doc.SetVariable('DIMSCALE', 1) }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/100.0,3))ms"
}

# ---------- draw via LISP (command inside Eval?) ----------
T 'setup-vars' { $doc.SetVariable('CMDECHO', 0); $doc.SetVariable('OSMODE', 0); $doc.SetVariable('FILEDIA', 0); $doc.SetVariable('CMDDIA', 0); 'cmdEcho=0 osmode=0 filedia=0' }
T 'draw-via-eval-entmake' { $r = $doc.EvaluateLispExpression('(progn (entmake (list (cons 0 "LINE") (cons 10 (list 0.0 0.0 0.0)) (cons 11 (list 10.0 10.0 0.0)))) (cdr (assoc 5 (entget (entlast)))))'); "handle=$r mscount=$($doc.ModelSpace.Count)" }
T 'draw-via-eval-command' { $r = $doc.EvaluateLispExpression('(progn (command "_.LINE" (list 0.0 5.0 0.0) (list 10.0 5.0 0.0) "") (cdr (assoc 5 (entget (entlast)))))'); "handle=$r mscount=$($doc.ModelSpace.Count)" }
T 'draw-via-eval-cmdecho-on' { $r = $doc.EvaluateLispExpression('(progn (setvar "CMDECHO" 1) (command "_.CIRCLE" (list 5.0 5.0 0.0) 2.0) (setvar "CMDECHO" 0) (cdr (assoc 5 (entget (entlast)))))'); "handle=$r mscount=$($doc.ModelSpace.Count)" }

# ---------- A4 SendCommand ----------
T 'sendcommand-with-newline' {
    $before = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "CIRCLE") (cons 10 (list 1.0 1.0 0.0)) (cons 40 3.0)))' + "`n")
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew(); $c = $before
    while ($sw2.ElapsedMilliseconds -lt 10000) { $c = $doc.ModelSpace.Count; if ($c -gt $before) { break }; Start-Sleep -Milliseconds 20 }
    $sw2.Stop(); "before=$before after=$c waitMs=$($sw2.ElapsedMilliseconds)"
}
T 'sendcommand-no-trailing-nl' {
    $before = $doc.ModelSpace.Count
    $doc.SendCommand('(entmake (list (cons 0 "CIRCLE") (cons 10 (list 2.0 2.0 0.0)) (cons 40 1.0)))')
    Start-Sleep -Milliseconds 1200
    "before=$before after=$($doc.ModelSpace.Count)"
}

# ---------- A3 undo semantics ----------
T 'undo-test-U1-one-eval-10ents-then-undo1' {
    $b = $doc.ModelSpace.Count
    [void]$doc.EvaluateLispExpression('(progn (setq i 0) (while (< i 10) (entmake (list (cons 0 "POINT") (cons 10 (list (float i) 0.0 0.0)))) (setq i (1+ i))) (princ))')
    $a1 = $doc.ModelSpace.Count
    [void]$doc.EvaluateLispExpression('(command "_.UNDO" "1")')
    Start-Sleep -Milliseconds 200
    $a2 = $doc.ModelSpace.Count
    "base=$b afterCreate=$a1 afterUndo=$a2"
}
T 'undo-test-U2-10evals-then-undo1' {
    $b = $doc.ModelSpace.Count
    for ($i = 0; $i -lt 10; $i++) { [void]$doc.EvaluateLispExpression("(entmake (list (cons 0 ""POINT"") (cons 10 (list $([float]($i+100)) 0.0 0.0))))") }
    $a1 = $doc.ModelSpace.Count
    [void]$doc.EvaluateLispExpression('(command "_.UNDO" "1")')
    Start-Sleep -Milliseconds 200
    $a2 = $doc.ModelSpace.Count
    "base=$b afterCreate=$a1 afterUndo=$a2"
}
T 'undo-test-U3-marks-10evals-then-undo1' {
    $b = $doc.ModelSpace.Count
    $doc.StartUndoMark()
    for ($i = 0; $i -lt 10; $i++) { [void]$doc.EvaluateLispExpression("(entmake (list (cons 0 ""POINT"") (cons 10 (list $([float]($i+200)) 0.0 0.0))))") }
    $doc.EndUndoMark()
    $a1 = $doc.ModelSpace.Count
    [void]$doc.EvaluateLispExpression('(command "_.UNDO" "1")')
    Start-Sleep -Milliseconds 200
    $a2 = $doc.ModelSpace.Count
    "base=$b afterCreate=$a1 afterUndo=$a2"
}

# ---------- B5 quiescent ----------
T 'acadstate-isquiescent' { $st = $app.GetAcadState(); "type=$($st.GetType().FullName) IsQuiescent=$($st.IsQuiescent)" }
T 'perf-acadstate-50' {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 50; $i++) { $s = $app.GetAcadState(); [void]$s.IsQuiescent }
    $sw2.Stop(); "total=$($sw2.ElapsedMilliseconds)ms per=$([math]::Round($sw2.ElapsedMilliseconds/50.0,3))ms"
}

# ---------- B6 status bar / B7 window title ----------
T 'statusbar-put-get' { $app.StatusBar = 'AI: probing'; Start-Sleep -Milliseconds 100; $app.StatusBar }
T 'windowtitle-set-attempt' { $t = $doc.WindowTitle(); "title=$t" }

# ---------- version detection (F) ----------
T 'sysvars-version' {
    $vars = @('ACADVER','ACADMAINTVER','DWGNAME','DWGCODEPAGE','SAVEVER','PROGRAM','_ZWCAD_VER','ZWCADVER','PLATFORM','PRODUCT')
    $o = @()
    foreach ($v in $vars) {
        try { $o += "$v=$($doc.GetVariable($v))" } catch { $o += "$v=<ERR>" }
    }
    ($o -join ' ')
}
T 'eval-version-probe' { $doc.EvaluateLispExpression('(strcat (getvar "ACADVER") "|" (getvar "PROGRAM") "|" (vl-string-subst "" " " (getvar "PLATFORM")))') }

# ---------- summary ----------
""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== cleanup ====="
try { $app.Quit(); "QUIT_OK" } catch { "QUIT_FAIL: $($_.Exception.Message)" }
try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch {}
[GC]::Collect(); [GC]::WaitForPendingFinalizers()
Start-Sleep -Seconds 2
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  $($_.Id) $($_.ProcessName)" } } else { "LEFTOVER_PROCS=0" }
