# 22-export-dxf.ps1 - DXF/export channel + entity count reconciliation (pure ASCII)
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
function WaitUntil([scriptblock]$cond, [int]$timeoutMs = 60000, [int]$pollMs = 20) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try { if (& $cond) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds } } } catch {}
        Start-Sleep -Milliseconds $pollMs
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds }
}

$dir = $script:ProbeTmp
$fwd = $dir.Replace('\', '/')
$src = Get-Content "$PSScriptRoot\probe1.lsp" -Raw
$src = $src.Replace('@@PRB_DIR@@', $fwd)
$lspPath = Join-Path $dir 'probe1.lsp'
[System.IO.File]::WriteAllText($lspPath, $src, [System.Text.Encoding]::ASCII)
$lspFwd = $lspPath.Replace('\', '/')
Get-ChildItem $dir -Filter '*.dxf' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $dir -Filter '*.dwg' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $dir -Filter '*.pdf' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $dir -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
[void]$global:RES.Add("RESULT`tcreate-instance`tOK`t$($sw.ElapsedMilliseconds)`tversion=$($app.Version)")
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
[void]$doc.SetVariable('FILEDIA', 0); [void]$doc.SetVariable('CMDDIA', 0)
[void]$doc.SetVariable('ISAVEPERCENT', 0)
[void]$doc.SendCommand('(load "' + $lspFwd + '")' + "`n")
Start-Sleep -Milliseconds 400

# known-content mix (7 entities) then bulk 10000
T 'build-known-mix' {
    $p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(10.0, 0.0, 0.0)
    for ($i = 0; $i -lt 3; $i++) { $p1[0] = [double]$i; $p2[0] = [double]($i + 10); $null = $ms.AddLine($p1, $p2) }
    $c = [double[]]@(5.0, 5.0, 0.0); $null = $ms.AddCircle($c, 2.0); $null = $ms.AddCircle($c, 4.0)
    $pts = [double[]]@(0.0, 0.0, 5.0, 0.0, 5.0, 5.0)
    $null = $ms.AddLightWeightPolyline($pts)
    $ip = [double[]]@(0.0, 30.0, 0.0); $null = $ms.AddText('probe-text', $ip, 2.0)
    $null = $ms.AddMText($ip, 40.0, 'probe-mtext')
    "count=$($doc.ModelSpace.Count)"
}
T 'build-10000-lines-lisp' {
    $doc.SetLispVariable('PRB_N', 10000)
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('PRB-MKN' + "`n")
    $w = WaitUntil { $doc.GetLispVariable('PRB_MKN_N') -ne $false } 180000
    $sw2.Stop()
    "wallMs=$($sw2.ElapsedMilliseconds) inCadMs=$($doc.GetLispVariable('PRB_MKN_MS')) created=$($doc.GetLispVariable('PRB_MKN_N')) countDelta=$($doc.ModelSpace.Count - $b)"
}
T 'count-after-build' { $doc.ModelSpace.Count }

# ---------- DXF export variants ----------
T 'Export-DXF-with-ext' {
    $p = Join-Path $dir 'export_with_ext.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'DXF')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_with_ext*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-DXF-null-ss' {
    $p = Join-Path $dir 'export_null_ss.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'DXF', $null)
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_null_ss*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-DXF-noext-arg' {
    $p = Join-Path $dir 'export_noext'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'DXF')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_noext*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-other-ext-pdf' {
    $p = Join-Path $dir 'export_pdf.pdf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'PDF')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_pdf*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-other-ext-bmp' {
    $p = Join-Path $dir 'export_bmp.bmp'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'BMP')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_bmp*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-other-ext-png' {
    $p = Join-Path $dir 'export_png.png'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'PNG')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_png*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}
T 'Export-other-ext-wmf' {
    $p = Join-Path $dir 'export_wmf.wmf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.Export($p, 'WMF')
    $sw2.Stop()
    $files = (Get-ChildItem $dir -Filter 'export_wmf*' | ForEach-Object { "$($_.Name)=$($_.Length)" }) -join ' '
    "ms=$($sw2.ElapsedMilliseconds) files=[$files]"
}

# ---------- DXF via LISP DXFOUT (version control) ----------
T 'lisp-DXFOUT-default' {
    $p = ($dir + '\dxfout_default.dxf').Replace('\', '/')
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('(command "_.DXFOUT" "' + $p + '")' + "`n")
    $w = WaitUntil { Test-Path ($dir + '\dxfout_default.dxf') } 60000 50
    $sw2.Stop()
    $sz = if (Test-Path ($dir + '\dxfout_default.dxf')) { (Get-Item ($dir + '\dxfout_default.dxf')).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) ok=$($w.ok) bytes=$sz"
}
T 'lisp-DXFOUT-2007' {
    $p = ($dir + '\dxfout_2007.dxf').Replace('\', '/')
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('(command "_.DXFOUT" "' + $p + '" "_V" "2007" "16")' + "`n")
    $w = WaitUntil { Test-Path ($dir + '\dxfout_2007.dxf') } 60000 50
    $sw2.Stop()
    $sz = if (Test-Path ($dir + '\dxfout_2007.dxf')) { (Get-Item ($dir + '\dxfout_2007.dxf')).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) ok=$($w.ok) bytes=$sz"
}
T 'lisp-DXFOUT-r12' {
    $p = ($dir + '\dxfout_r12.dxf').Replace('\', '/')
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand('(command "_.DXFOUT" "' + $p + '" "_V" "R12" "16")' + "`n")
    $w = WaitUntil { Test-Path ($dir + '\dxfout_r12.dxf') } 60000 50
    $sw2.Stop()
    $sz = if (Test-Path ($dir + '\dxfout_r12.dxf')) { (Get-Item ($dir + '\dxfout_r12.dxf')).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) ok=$($w.ok) bytes=$sz"
}

# ---------- SaveAs identity change ----------
T 'doc-identity-before-SaveAs' { "name=$($doc.Name) full=[$($doc.FullName)] saved=$($doc.Saved)" }
T 'SaveAs-DXF-2007-code37' {
    $p = Join-Path $dir 'saveas_2007.dxf'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SaveAs($p, 37)
    $sw2.Stop()
    $sz = if (Test-Path $p) { (Get-Item $p).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) bytes=$sz name=$($doc.Name) full=[$($doc.FullName)]"
}
T 'SaveAs-DWG-2004-code24' {
    $p = Join-Path $dir 'saveas_2004.dwg'
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SaveAs($p, 24)
    $sw2.Stop()
    $sz = if (Test-Path $p) { (Get-Item $p).Length } else { -1 }
    "ms=$($sw2.ElapsedMilliseconds) bytes=$sz name=$($doc.Name) full=[$($doc.FullName)]"
}
T 'doc-identity-after-SaveAs' { "name=$($doc.Name) full=[$($doc.FullName)] saved=$($doc.Saved)" }

# ---------- file headers (DGS encryption check) ----------
T 'file-header-check' {
    $o = @()
    foreach ($f in (Get-ChildItem $dir -Include '*.dxf', '*.dwg', '*.pdf' -File -ErrorAction SilentlyContinue)) {
        $fs = [System.IO.File]::OpenRead($f.FullName)
        $buf = New-Object byte[] 16
        $n = $fs.Read($buf, 0, 16); $fs.Close()
        $hex = (($buf[0..15] | ForEach-Object { '{0:X2}' -f $_ }) -join '')
        $ascii = ([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) -replace '[^\x20-\x7E]', '.'
        $o += "$($f.Name) bytes=$($f.Length) hex=$hex ascii=[$ascii]"
    }
    ($o -join ' || ')
}

# ---------- entity count via LISP ----------
T 'lisp-count-entities' {
    $doc.SendCommand('PRB-CNT' + "`n")
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw2.ElapsedMilliseconds -lt 60000) { $v = $doc.GetLispVariable('PRB_CNT'); if ($v -ne $false) { break }; Start-Sleep -Milliseconds 50 }
    "ms=$($sw2.ElapsedMilliseconds) lispCount=$v comCount=$($doc.ModelSpace.Count)"
}

# ---------- quit: mark saved then Quit ----------
T 'quit-saved-true' {
    $doc.Saved = $true
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $app.Quit()
    $w = WaitUntil { @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0 } 20000 100
    $sw2.Stop()
    "quitCallMs=$($sw2.ElapsedMilliseconds) exited=$($w.ok) exitAfterMs=$($w.ms)"
}

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== FILES ====="
Get-ChildItem $dir -Include '*.dxf', '*.dwg', '*.pdf', '*.bmp', '*.png', '*.wmf' -File -ErrorAction SilentlyContinue | Select-Object Name, Length | Sort-Object Name | Format-Table -AutoSize | Out-String
""
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle) title=$($_.MainWindowTitle)" } } else { "LEFTOVER_PROCS=0" }
