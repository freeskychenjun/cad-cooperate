# 27-sendcmd-limit.ps1 - bracket the single-SendCommand string length limit (pure ASCII)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib-com.ps1"
function PollVar($doc, [string]$name, [int]$timeoutMs = 20000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $v = $doc.GetLispVariable($name)
        if ($v -ne $false -and $null -ne $v) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds; v = $v } }
        Start-Sleep -Milliseconds 20
    }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds; v = $null }
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
$sw.Stop()
"createMs=$($sw.ElapsedMilliseconds)"
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)

foreach ($n in @(10, 20, 40, 80, 160, 320)) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('(progn')
    for ($i = 0; $i -lt $n; $i++) {
        $x = '{0:F2}' -f ([double]$i / 7.0)
        [void]$sb.Append("(entmake (list (cons 0 `"LINE`") (cons 10 (list $x 500.0 0.0)) (cons 11 (list $x 510.0 0.0))))")
    }
    [void]$sb.Append('(setq PRB_L "ok"))')
    $expr = $sb.ToString()
    $b = $doc.ModelSpace.Count
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $doc.SendCommand($expr + "`n")
    $r = PollVar $doc 'PRB_L' 15000
    $sw2.Stop()
    $delta = $doc.ModelSpace.Count - $b
    "entmakeCalls=$n cmdLen=$($expr.Length) wallMs=$($sw2.ElapsedMilliseconds) completed=$($r.ok) delta=$delta expected=$n lastprompt=[$($doc.GetVariable('LASTPROMPT'))]"
    if ($r.ok) { $doc.SendCommand('(setq PRB_L nil)' + "`n"); Start-Sleep -Milliseconds 200 }
}
"count=$($doc.ModelSpace.Count)"
try { $doc.Close($false) } catch {}
Start-Sleep -Milliseconds 300
try { $app.Quit() } catch {}
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw2.ElapsedMilliseconds -lt 20000) { if (@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0) { break }; Start-Sleep -Milliseconds 100 }
"exitMs=$($sw2.ElapsedMilliseconds) leftover=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"
