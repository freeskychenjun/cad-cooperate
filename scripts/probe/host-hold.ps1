# host-hold.ps1 - creates a ZWCAD COM instance and keeps it alive until a stop file appears (pure ASCII)
param([string]$ReadyFile, [string]$StopFile, [string]$LibPath)
$ErrorActionPreference = 'Continue'
. $LibPath
Remove-Item $ReadyFile, $StopFile -ErrorAction SilentlyContinue
try {
    $app = New-Object -ComObject 'ZWCAD.Application'
} catch {
    Set-Content -Path $ReadyFile -Value "CREATE_FAIL $($_.Exception.Message)" -Encoding ASCII
    exit 1
}
$app.Visible = $true
$hwnd = $app.HWND
$p = Get-Process zwcad* -ErrorAction SilentlyContinue | Select-Object -First 1
Set-Content -Path $ReadyFile -Value "OK pid=$($p.Id) hwnd=$hwnd version=$($app.Version)" -Encoding ASCII
$deadline = (Get-Date).AddMinutes(10)
while (-not (Test-Path $StopFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
try { $app.ActiveDocument.Close($false) } catch {}
Start-Sleep -Milliseconds 300
try { $app.Quit() } catch {}
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app)
[GC]::Collect(); [GC]::WaitForPendingFinalizers()
