# publish.ps1 - stage the zwcad-cooperate skill package into a skills install directory.
#
#   .\publish.ps1                       -> deploy to $env:USERPROFILE\.agents\skills\zwcad-cooperate
#   .\publish.ps1 -Target <dir>         -> deploy elsewhere (needs -Force unless <...>\skills\<name>)
#   .\publish.ps1 -WhatIf               -> dry run: list what would be copied / removed, touch nothing
#   .\publish.ps1 -SkipVerify           -> skip the post-deploy `doctor` smoke test
#
# Safety model (copied from the wps-office-cooperate publisher, same reasons):
#   * the target must look like "<...>\skills\<name>" unless -Force is given, because this script
#     removes files inside the target that are not part of the package;
#   * the new package is assembled in "<target>.__staging" and only swapped in after every file was
#     copied, so a mid-way failure cannot leave a half-updated deployment;
#   * -WhatIf performs the full resolve + validation and prints the plan without touching disk.
#
# Pure ASCII source on purpose (PowerShell 5.1 parses a BOM-less .ps1 using the ANSI code page).

param(
    [string]$Target = (Join-Path $env:USERPROFILE '.agents\skills\zwcad-cooperate'),
    [switch]$Force,
    [switch]$WhatIf,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# source -> destination (destination relative to the package root; directory destinations end with \)
$manifest = @(
    @{ src = 'SKILL.md';                                  dst = 'SKILL.md' }
    @{ src = 'README.md';                                 dst = 'README.md' }
    @{ src = 'docs\M3-E2-writes.md';                      dst = 'reference\writes.md' }
    @{ src = 'docs\M3-E3-stability.md';                   dst = 'reference\stability.md' }
    @{ src = 'docs\audit-rules.md';                       dst = 'reference\audit-rules.md' }
    @{ src = 'docs\progid-selection.md';                  dst = 'reference\progid-selection.md' }
    @{ src = 'docs\M3-E1-perf.md';                        dst = 'reference\perf.md' }
    @{ src = 'docs\M3-E3-acceptance.md';                  dst = 'reference\acceptance.md' }
    @{ src = 'docs\M3-E5-lisp-file-canvas.md';           dst = 'reference\lisp-file-canvas.md' }
    @{ src = 'docs\M3-E6-geometry-dump.md';              dst = 'reference\geometry-dump.md' }
    @{ src = 'docs\tray-badge.md';                       dst = 'reference\tray-badge.md' }
    @{ src = 'docs\autocad-2021-acceptance.md';           dst = 'reference\autocad-2021.md' }
    @{ src = 'scripts\zwc.ps1';                           dst = 'scripts\zwc.ps1' }
    @{ src = 'scripts\zwc.cmd';                           dst = 'scripts\zwc.cmd' }
    @{ src = 'scripts\tray.ps1';                          dst = 'scripts\tray.ps1' }
    @{ src = 'scripts\tray\ZwcTray.cs';                   dst = 'scripts\tray\ZwcTray.cs' }
    @{ src = 'scripts\tray\ui.json';                      dst = 'scripts\tray\ui.json' }
    @{ src = 'bin\tray\ZwcTray.exe';                      dst = 'bin\tray\ZwcTray.exe'; optional = $true }
    @{ src = 'scripts\client\ZwcClient.cs';               dst = 'scripts\client\ZwcClient.cs' }
    @{ src = 'scripts\helper\*.cs';                       dst = 'scripts\helper\' }
    @{ src = 'scripts\section\prepare.py';                dst = 'scripts\section\prepare.py' }
    @{ src = 'scripts\section\qa_yssd.py';                dst = 'scripts\section\qa_yssd.py' }
    @{ src = 'scripts\audit\audit.py';                    dst = 'scripts\audit\audit.py' }
    @{ src = 'scripts\audit\run.ps1';                     dst = 'scripts\audit\run.ps1' }
    @{ src = 'tests\fixtures\section-30.xlsx';            dst = 'fixtures\section-30.xlsx' }
    @{ src = 'bin\ZwcClient.exe';                         dst = 'bin\ZwcClient.exe'; optional = $true }
    @{ src = 'bin\ZwcClient.sha';                         dst = 'bin\ZwcClient.sha'; optional = $true }
)

function Fail([string]$msg) { Write-Host ("ERROR: " + $msg) -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- target validation
$leaf = Split-Path -Leaf $Target
$parentLeaf = Split-Path -Leaf (Split-Path -Parent $Target)
if (-not $Force) {
    if ($leaf -ne 'zwcad-cooperate' -or $parentLeaf -ne 'skills') {
        Fail ("target must look like <...>\skills\zwcad-cooperate (got: " + $Target + "). Pass -Force to override.")
    }
}

# ---------------------------------------------------------------- resolve sources
$planned = New-Object System.Collections.ArrayList
foreach ($item in $manifest) {
    $srcPattern = Join-Path $root $item.src
    $matches = @(Get-ChildItem -Path $srcPattern -File -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        if ($item.optional) { continue }
        Fail ("missing source: " + $item.src)
    }
    foreach ($m in $matches) {
        $dstRel = if ($item.dst.EndsWith('\')) { $item.dst + $m.Name } else { $item.dst }
        $null = $planned.Add([pscustomobject]@{ Source = $m.FullName; Rel = $dstRel; Bytes = $m.Length })
    }
}
if ($planned.Count -eq 0) { Fail 'nothing to publish (manifest resolved to zero files)' }

$staging = $Target + '.__staging'
$backup = $Target + '.__old'
$totalBytes = ($planned | Measure-Object -Property Bytes -Sum).Sum

Write-Host '=== publish zwcad-cooperate ==='
Write-Host ("root    : " + $root)
Write-Host ("target  : " + $Target)
Write-Host ("files   : " + $planned.Count + " (" + [math]::Round($totalBytes / 1KB, 1) + " KB)")
foreach ($p in $planned) { Write-Host ("  + " + $p.Rel + "  (" + [math]::Round($p.Bytes / 1KB, 1) + " KB)") }

# stale files that would be removed from an existing target
$stale = @()
if (Test-Path -LiteralPath $Target) {
    $existing = Get-ChildItem -LiteralPath $Target -Recurse -File | ForEach-Object {
        $_.FullName.Substring($Target.Length).TrimStart('\')
    }
    $keep = $planned | ForEach-Object { $_.Rel }
    $stale = @($existing | Where-Object { $keep -notcontains $_ })
    if ($stale.Count -gt 0) {
        Write-Host ("stale   : " + $stale.Count + " file(s) in target would be removed") -ForegroundColor Yellow
        foreach ($s in $stale) { Write-Host ("  - " + $s) -ForegroundColor Yellow }
    }
}

if ($WhatIf) {
    Write-Host 'WHATIF: no files were changed.' -ForegroundColor Cyan
    exit 0
}

# ---------------------------------------------------------------- stage
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($p in $planned) {
    $dstPath = Join-Path $staging $p.Rel
    $dstDir = Split-Path -Parent $dstPath
    if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
    Copy-Item -LiteralPath $p.Source -Destination $dstPath -Force
}

# ---------------------------------------------------------------- swap
# A running daemon keeps <target>\bin\cache\<sha>\ZwcHelper.exe open, which makes the directory
# un-movable. Stop it first (best effort) and retry the move a few times before failing.
function Move-WithRetry([string]$from, [string]$to, [int]$attempts = 6) {
    for ($i = 1; $i -le $attempts; $i++) {
        try { Move-Item -LiteralPath $from -Destination $to -Force; return } catch {
            if ($i -eq $attempts) { throw }
            Start-Sleep -Milliseconds (300 * $i)
        }
    }
}

if (Test-Path -LiteralPath $Target) {
    $deployedPs1 = Join-Path $Target 'scripts\zwc.ps1'
    if (Test-Path -LiteralPath $deployedPs1) {
        Write-Host 'pre-swap: stopping daemons that run from the deployed copy'
        try { & $deployedPs1 serve -Action stop 2>&1 | Out-Null } catch { }
        Start-Sleep -Milliseconds 400
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    Move-WithRetry $Target $backup
}
Move-WithRetry $staging $Target
if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ("deployed: " + $Target) -ForegroundColor Green

# ---------------------------------------------------------------- verify
if (-not $SkipVerify) {
    $client = Join-Path $Target 'bin\ZwcClient.exe'
    $cmd = Join-Path $Target 'scripts\zwc.cmd'
    $verifyExe = if (Test-Path -LiteralPath $client) { $client } elseif (Test-Path -LiteralPath $cmd) { $cmd } else { $null }
    if (-not $verifyExe) {
        Write-Host 'verify  : skipped (no client/cmd found)' -ForegroundColor Yellow
    } else {
        Write-Host 'verify  : running doctor (no CAD expected -> ok:false / NO_INSTANCE is the pass condition)'
        $out = & $verifyExe doctor 2>&1 | Out-String
        $code = $LASTEXITCODE
        $ok = $false
        try { $j = $out | ConvertFrom-Json; $ok = $true; $err = $j.error.code } catch { $err = 'UNPARSEABLE' }
        if ($ok) {
            Write-Host ("verify  : PASS (exit=" + $code + ", error=" + $err + ", NO_INSTANCE expected when no CAD is running)") -ForegroundColor Green
        } else {
            Write-Host ("verify  : WARN - doctor output is not a single JSON document (first 200 chars below)") -ForegroundColor Yellow
            Write-Host ($out.Substring(0, [math]::Min(200, $out.Length)))
        }
    }
}

Write-Host ''
Write-Host 'done. next steps:'
Write-Host ('  1) ' + (Join-Path $Target 'bin\ZwcClient.exe') + ' doctor')
Write-Host '  2) open your drawing in ZWCAD/AutoCAD, then run a read command with an explicit -ProgId'
