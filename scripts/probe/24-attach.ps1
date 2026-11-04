# 24-attach.ps1 - cross-process ROT attach, second-CreateObject semantics, SetWindowText title mark (pure ASCII)
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
function WaitUntil([scriptblock]$cond, [int]$timeoutMs = 30000, [int]$pollMs = 100) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { try { if (& $cond) { return @{ ok = $true; ms = $sw.ElapsedMilliseconds } } } catch {}; Start-Sleep -Milliseconds $pollMs }
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds }
}

$dir = $script:ProbeTmp
$ready = Join-Path $dir 'host-ready.txt'
$stop = Join-Path $dir 'host-stop.txt'
Remove-Item $ready, $stop -Force -ErrorAction SilentlyContinue

# ---------- 1. ROT attach to an instance owned by ANOTHER process ----------
"procs before = $(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"
$child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSScriptRoot\host-hold.ps1", '-ReadyFile', $ready, '-StopFile', $stop, '-LibPath', "$PSScriptRoot\lib-com.ps1") -PassThru
$w = WaitUntil { Test-Path $ready } 60000 200
"childPid=$($child.Id) ready=$($w.ok) waited=$($w.ms) content=[$(Get-Content $ready -ErrorAction SilentlyContinue)]"
$hostInfo = Get-Content $ready -ErrorAction SilentlyContinue
$hostPid = $null; $hostHwnd = $null
if ($hostInfo -match 'pid=(\d+)') { $hostPid = [int]$Matches[1] }
if ($hostInfo -match 'hwnd=(\d+)') { $hostHwnd = [long]$Matches[1] }
"hostPid=$hostPid hostHwnd=$hostHwnd procsNow=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"

T 'ROT-GetActiveObject' { $a = [ComProbe]::GetActive('ZWCAD.Application'); "hwnd=$($a.HWND) version=$($a.Version) docs=$($a.Documents.Count) active=$($a.ActiveDocument.Name)" }
T 'ROT-attach-matches-host-pid' {
    $a = [ComProbe]::GetActive('ZWCAD.Application')
    $h = [long]$a.HWND
    $p = Get-Process zwcad* -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $hostPid }
    "attachedHwnd=$h hostHwnd=$hostHwnd same=$($h -eq $hostHwnd) hostAlive=$([bool]$p)"
}
T 'drive-attached-instance' {
    $a = [ComProbe]::GetActive('ZWCAD.Application')
    $d = $a.ActiveDocument
    [void]$d.SetVariable('CMDECHO', 0)
    $d.SendCommand('(setq PRB_ATT "attached-write-ok")' + "`n")
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw2.ElapsedMilliseconds -lt 10000) { $v = $d.GetLispVariable('PRB_ATT'); if ($v -ne $false) { break }; Start-Sleep -Milliseconds 20 }
    "ms=$($sw2.ElapsedMilliseconds) value=$v docName=$($d.Name)"
}
# ---------- 2. second CreateObject while an instance is alive: reuse or new process? ----------
T 'second-CreateObject-behavior' {
    $n0 = @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count
    $a2 = New-Object -ComObject 'ZWCAD.Application'
    Start-Sleep -Milliseconds 500
    $n1 = @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count
    "procsBefore=$n0 procsAfter=$n1 hwnd2=$($a2.HWND) hostHwnd=$hostHwnd sameHwnd=$([long]$a2.HWND -eq $hostHwnd) docs=$($a2.Documents.Count)"
}
# ---------- 3. title marking via SetWindowText on app.HWND ----------
$cs = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinTxt {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr h, string s);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
  public static string Set(IntPtr h, string s) { bool ok = SetWindowTextW(h, s); return "SetWindowText ok=" + ok; }
  public static string Get(IntPtr h) { StringBuilder sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
}
'@
Add-Type -TypeDefinition $cs -Language CSharp
T 'setwindowtext-title-mark' {
    $a = [ComProbe]::GetActive('ZWCAD.Application')
    $h = [IntPtr]([long]$a.HWND)
    $before = [WinTxt]::Get($h)
    $r = [WinTxt]::Set($h, $before + '  [AI-COLLAB]')
    Start-Sleep -Milliseconds 400
    $after = [WinTxt]::Get($h)
    "set=$r before=[$before] after=[$after] appCaption=[$($a.Caption)]"
}
# ---------- 4. multi-document semantics ----------
T 'multi-doc' {
    $a = [ComProbe]::GetActive('ZWCAD.Application')
    $d0 = $a.ActiveDocument.Name
    $nd = $a.Documents.Add()
    Start-Sleep -Milliseconds 400
    $d1 = $a.ActiveDocument.Name
    $cnt = $a.Documents.Count
    $names = @(); for ($i = 0; $i -lt $cnt; $i++) { $names += $a.Documents.Item($i).Name }
    $a.Documents.Item(0).Activate()
    Start-Sleep -Milliseconds 300
    $d2 = $a.ActiveDocument.Name
    "initial=$d0 afterAdd=$d1 count=$cnt names=[$($names -join ',')] afterActivate0=$d2"
}
T 'close-extra-doc' {
    $a = [ComProbe]::GetActive('ZWCAD.Application')
    while ($a.Documents.Count -gt 1) { $a.Documents.Item($a.Documents.Count - 1).Close($false); Start-Sleep -Milliseconds 300 }
    "docs=$($a.Documents.Count)"
}

""
"===== RESULTS ====="
$global:RES | ForEach-Object { $_ }
""
"===== stop host ====="
Set-Content -Path $stop -Value 'stop' -Encoding ASCII
$w2 = WaitUntil { @(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0 } 25000 200
"exited=$($w2.ok) afterMs=$($w2.ms) childAlive=$(-not $child.HasExited)"
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { "LEFTOVER_PROCS=$(@($z).Count)"; $z | ForEach-Object { "  id=$($_.Id) hwnd=$($_.MainWindowHandle)" } } else { "LEFTOVER_PROCS=0" }
