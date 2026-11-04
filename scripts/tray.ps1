# tray.ps1 - start / stop / inspect the system-tray "AI" badge for zwcad-cooperate.
#
#   tray.ps1 -Start -Doc "<drawing name>" -Step "<what AI is doing>" [-Hwnd N] [-CadPid N]
#   tray.ps1 -Stop  -Doc "<drawing name>"
#   tray.ps1 -Status
#
# Why a wrapper: the tray exe is UI-only and Chinese text must not live in a .ps1 literal
# (PowerShell 5.1 parses BOM-less scripts with the ANSI code page), so the UI strings come from
# scripts\tray\ui.json (UTF-8) and the per-drawing state file is written as UTF-8 without BOM.
#
# Pure ASCII source on purpose.

param(
    [switch]$Start,
    [switch]$Stop,
    [switch]$Status,
    [string]$Doc = '',
    [string]$Step = '',
    [int]$Hwnd = 0,
    [int]$CadPid = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # this file lives in <root>\scripts
$trayDir = Join-Path $root 'scripts\tray'
$exe = Join-Path $root 'bin\tray\ZwcTray.exe'
$uiPath = Join-Path $trayDir 'ui.json'
$stateRoot = Join-Path $env:TEMP 'zwcad-cooperate'
if (-not (Test-Path $stateRoot)) { New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null }

function Write-Json($obj) {
    [Console]::Out.Write(($obj | ConvertTo-Json -Depth 6 -Compress))
}

function Sha8([string]$s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s))
    return (($bytes[0..3] | ForEach-Object { $_.ToString('x2') }) -join '')
}

function StatePath([string]$doc) { Join-Path $stateRoot ('tray-' + (Sha8 $doc) + '.json') }

function Ensure-TrayExe {
    if (Test-Path $exe) { return $true }
    $csc = $null
    foreach ($arch in @('Framework64', 'Framework')) {
        $cand = Join-Path $env:SystemRoot ('Microsoft.NET\' + $arch + '\v4.0.30319\csc.exe')
        if (Test-Path $cand) { $csc = $cand; break }
    }
    if (-not $csc) { return $false }
    $dir = Split-Path -Parent $exe
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $src = Join-Path $trayDir 'ZwcTray.cs'
    $log = & $csc /nologo /target:winexe ('/out:' + $exe) /r:System.Windows.Forms.dll /r:System.Drawing.dll /optimize+ $src 2>&1 | Out-String
    if (-not (Test-Path $exe)) { Write-Host $log; return $false }
    return $true
}

if ($Status) {
    $states = @()
    Get-ChildItem $stateRoot -Filter 'tray-*.json' -File -ErrorAction SilentlyContinue | ForEach-Object {
        $j = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $alive = $false
        if ($j.pid -gt 0) { $alive = [bool](Get-Process -Id $j.pid -ErrorAction SilentlyContinue) }
        $states += [pscustomobject]@{ file = $_.Name; doc = $j.doc; step = $j.step; pid = $j.pid; pidAlive = $alive }
    }
    $procs = @(Get-Process -Name ZwcTray -ErrorAction SilentlyContinue | Select-Object Id, StartTime)
    Write-Json ([ordered]@{ ok = $true; cmd = 'tray'; action = 'status'; states = $states; trayProcesses = $procs; exe = $exe; exeExists = (Test-Path $exe) })
    exit 0
}

if ($Stop) {
    if (-not $Doc) { Write-Json ([ordered]@{ ok = $false; error = 'Doc is required for -Stop' }); exit 2 }
    $sp = StatePath $Doc
    $removed = $false
    if (Test-Path $sp) { Remove-Item $sp -Force; $removed = $true }
    Write-Json ([ordered]@{ ok = $true; cmd = 'tray'; action = 'stop'; doc = $Doc; stateRemoved = $removed })
    exit 0
}

if (-not $Start) {
    Write-Json ([ordered]@{ ok = $false; error = 'use -Start | -Stop | -Status' })
    exit 2
}
if (-not $Doc) { Write-Json ([ordered]@{ ok = $false; error = 'Doc is required for -Start' }); exit 2 }

if (-not (Ensure-TrayExe)) { Write-Json ([ordered]@{ ok = $false; error = 'tray exe missing and csc unavailable' }); exit 2 }

$ui = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json
$state = [ordered]@{
    doc       = $Doc
    step      = $Step
    hwnd      = $Hwnd
    pid       = $CadPid
    startedAt = (Get-Date).ToString('o')
    prefix    = $ui.prefix
    balloonTitle = $ui.balloonTitle
    open      = $ui.open
    copy      = $ui.copy
    end       = $ui.end
}
$sp = StatePath $Doc
[System.IO.File]::WriteAllText($sp, ($state | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

$p = Start-Process -FilePath $exe -ArgumentList @('--state', $sp) -PassThru
Start-Sleep -Milliseconds 900
$running = [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue)
Write-Json ([ordered]@{
    ok       = $running
    cmd      = 'tray'
    action   = 'start'
    doc      = $Doc
    step     = $Step
    hwnd     = $Hwnd
    pid      = $CadPid
    trayPid  = $p.Id
    state    = $sp
    note     = 'icon may land in the hidden tray overflow area; a balloon is shown once on start'
})
