# quiet-gate.ps1 - is this machine quiet enough to trust a latency measurement?
#
# Real-user load is the dominant noise source we measured (±100-200 ms): the user editing Excel
# in WPS, a stray helper eating CPU, a leftover CAD instance, another agent's daemon...
# Measurements must NOT run through that noise. Exit codes:
#   0 = QUIET (safe to measure)
#   4 = QUIET_WINDOW_REQUIRED (printed as JSON with reasons; do not measure)
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\quiet-gate.ps1
#   ... -MaxCpuPercent 25 -MinFreeMemGB 4 -OwnPid 1234

param(
    [double]$MaxCpuPercent = 25,
    [double]$MinFreeMemGB = 2,
    [int]$OwnPid = 0,
    [string[]]$IgnoreImages = @()
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

$state = Get-QuietWindowState -MaxCpuPercent $MaxCpuPercent -MinFreeMemGB $MinFreeMemGB -OwnPid $OwnPid -IgnoreImages $IgnoreImages
Write-JsonFile (Join-Path $script:ChaosRoot 'tmp\bench\e3\quiet-gate.json') $state
[Console]::Out.WriteLine(($state | ConvertTo-Json -Depth 6 -Compress))
if ($state.ok) { exit 0 }
exit 4
