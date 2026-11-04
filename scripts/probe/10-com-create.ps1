# 10-com-create.ps1 - minimal COM create + timing + clean quit (pure ASCII)
$ErrorActionPreference = 'Continue'
function L($s) { Write-Host $s }
function Procs { (Get-Process zwcad* -ErrorAction SilentlyContinue | Measure-Object).Count }

L "== before: zwcad procs = $(Procs)"

$t0 = Get-Date
$app = $null
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $app = New-Object -ComObject 'ZWCAD.Application'
    $sw.Stop()
    L "CREATE_OK ms=$($sw.ElapsedMilliseconds)"
} catch {
    L "CREATE_FAIL ms=$((Get-Date)-$t0) hresult=$('0x{0:X8}' -f $_.Exception.HResult)"
    L "  exType=$($_.Exception.GetType().FullName)"
    L "  msg=$($_.Exception.Message)"
    if ($_.Exception.InnerException) { L "  inner=$($_.Exception.InnerException.Message)" }
    L "== after-fail: zwcad procs = $(Procs)"
    Get-Process zwcad* -ErrorAction SilentlyContinue | ForEach-Object { L "  PROC $($_.Id) $($_.ProcessName) start=$($_.StartTime)" }
    exit 1
}

L "== after create: zwcad procs = $(Procs)"
Get-Process zwcad* -ErrorAction SilentlyContinue | ForEach-Object { L "  PROC $($_.Id) $($_.ProcessName) title=$($_.MainWindowTitle)" }

# basic identity
foreach ($p in @('Version','Name','Caption','Visible','FullName','Path')) {
    try { $v = $app.$p; L "app.$p = $v" } catch { L "app.$p ERR: $($_.Exception.Message)" }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try { L "Documents.Count = $($app.Documents.Count)" } catch { L "Documents.Count ERR: $($_.Exception.Message)" }
$sw.Stop(); L "documents-count ms=$($sw.ElapsedMilliseconds)"

# try adding a new drawing
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $doc = $app.Documents.Add()
    $sw.Stop()
    L "Documents.Add OK ms=$($sw.ElapsedMilliseconds) name=$($doc.Name) full=$($doc.FullName)"
    L "  ModelSpace.Count=$($doc.ModelSpace.Count) PaperSpace.Count=$($doc.PaperSpace.Count)"
} catch {
    L "Documents.Add FAIL: $($_.Exception.Message) hr=$('0x{0:X8}' -f $_.Exception.HResult)"
}

# enumerate members present on Application (COM type info)
try {
    $t = $app.GetType()
    L "app type=$($t.FullName) isComObject=$($t.IsCOMObject)"
    $members = $t.GetMembers() | Where-Object { $_.MemberType -eq 'Method' } | ForEach-Object { $_.Name } | Sort-Object -Unique
    L "app methods count=$($members.Count)"
    L ("app methods: " + ($members -join ','))
} catch { L "reflect ERR: $($_.Exception.Message)" }

# clean quit
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $app.Quit()
    L "QUIT_OK ms=$($sw.ElapsedMilliseconds)"
} catch { L "QUIT_FAIL: $($_.Exception.Message)" }
try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch { L "release ERR: $($_.Exception.Message)" }
[GC]::Collect(); [GC]::WaitForPendingFinalizers()
Start-Sleep -Seconds 2
L "== after quit: zwcad procs = $(Procs)"
Get-Process zwcad* -ErrorAction SilentlyContinue | ForEach-Object { L "  LEFTOVER $($_.Id) $($_.ProcessName) title=$($_.MainWindowTitle)" }
