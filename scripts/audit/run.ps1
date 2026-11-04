# run.ps1 - drawing health audit runner (read-only layer for zwcad-cooperate).
#
#   scripts\zwc.ps1 info  --> info.json
#   scripts\zwc.ps1 read  --> read.json
#   scripts\audit\audit.py --info info.json --read read.json --md report.md --json report.json
#
# Prints an ASCII summary (rule ids / counts / report paths) so a GBK console never
# mangles it; the human readable Chinese report lives in the generated .md/.json.
#
# This file is PURE ASCII on purpose: PowerShell 5.1 parses a .ps1 without a BOM using
# the ANSI code page, so any non-ASCII literal here would become mojibake.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\audit\run.ps1 -ProgId ZWCAD.Application.16
#   ... -Doc "<你的图纸>.dwg" -Max 20000 -OutDir tmp\audit -ExitCode
#   ... -InfoJson info.json -ReadJson read.json          # audit existing JSON, no CAD
#   ... -ProgId ZWCAD.Application.2025 | -App autocad
#
# Exit codes: 0 = audit ran (gate passed), 2 = zwc/input failure, 3 = blockers found
#             (only with -ExitCode), 4 = audit.py failed.
param(
    [string]$ProgId = 'ZWCAD.Application.16',
    [string]$App = '',
    [string]$Doc = '',
    [int]$Max = 20000,
    [string]$Scope = 'model',
    [switch]$Launch,
    [string]$InfoJson = '',
    [string]$ReadJson = '',
    [string]$Rules = '',
    [string]$Config = '',
    [string]$OutDir = 'tmp\audit',
    [string]$Json = '',
    [string]$Md = '',
    [string]$Python = '',
    [int]$TimeoutSec = 180,
    [switch]$ExitCode,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Write-Info([string]$text) { [Console]::Out.WriteLine($text) }

# ------------------------------------------------------------------ paths / tools
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent (Split-Path -Parent $scriptDir)
$zwcPath = Join-Path $root 'scripts\zwc.ps1'
$auditPy = Join-Path $root 'scripts\audit\audit.py'
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$pyExe = $Python
if (-not $pyExe) { $pyExe = $env:ZWC_PYTHON }
if (-not $pyExe) {
    $candidate = 'python'
    if (Test-Path -LiteralPath $candidate) { $pyExe = $candidate } else { $pyExe = 'python' }
}

if (-not (Test-Path -LiteralPath $auditPy)) {
    Write-Info ('ERROR audit.py not found: ' + $auditPy)
    exit 2
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}
$outDirFull = (Resolve-Path -LiteralPath $OutDir).Path
if (-not $InfoJson) { $InfoJson = Join-Path $outDirFull 'info.json' }
if (-not $ReadJson) { $ReadJson = Join-Path $outDirFull 'read.json' }
if (-not $Json) { $Json = Join-Path $outDirFull 'report.json' }
if (-not $Md) { $Md = Join-Path $outDirFull 'report.md' }

function Test-ZwcResponse {
    param([string]$path, [string]$label, [switch]$Quiet)
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $Quiet) { Write-Info ('ERROR ' + $label + ' output missing: ' + $path) }
        return $false
    }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    if (-not $text.Trim()) {
        if (-not $Quiet) { Write-Info ('ERROR ' + $label + ' output is empty: ' + $path) }
        return $false
    }
    if ($text -notmatch '"ok"\s*:\s*true') {
        $code = 'unknown'
        $msg = ''
        if ($text -match '"code"\s*:\s*"([^"]+)"') { $code = $matches[1] }
        if ($text -match '"message"\s*:\s*"([^"]+)"') { $msg = $matches[1] }
        if (-not $Quiet) { Write-Info ('ERROR ' + $label + ' failed: code=' + $code + ' ' + $msg) }
        return $false
    }
    return $true
}

function Get-ZwcErrorCode([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    if ($text -match '"code"\s*:\s*"([^"]+)"') { return $matches[1] }
    return ''
}

# Run zwc.ps1 in a CHILD powershell: zwc.ps1 calls exit, which would kill this script.
function Invoke-Zwc([string[]]$zwcArgs, [string]$outPath, [switch]$AllowFail) {
    if (-not (Test-Path -LiteralPath $zwcPath)) {
        Write-Info ('ERROR zwc.ps1 not found: ' + $zwcPath)
        exit 2
    }
    Write-Info ('zwc: ' + ($zwcArgs -join ' '))
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $text = & $psExe -NoProfile -ExecutionPolicy Bypass -File $zwcPath @zwcArgs 2>$null
    $code = $LASTEXITCODE
    $watch.Stop()
    $joined = ''
    if ($null -ne $text) {
        if ($text -is [array]) { $joined = ($text -join "`n") } else { $joined = [string]$text }
    }
    [System.IO.File]::WriteAllText($outPath, $joined, (New-Object System.Text.UTF8Encoding($false)))
    Write-Info ('  -> ' + $outPath + ' (' + $watch.ElapsedMilliseconds + ' ms, zwc exit ' + $code + ')')
    if ($code -ne 0 -and -not $AllowFail) {
        $snippet = $joined
        if ($snippet.Length -gt 400) { $snippet = $snippet.Substring(0, 400) }
        Write-Info ('ERROR zwc.ps1 exit ' + $code + ': ' + $snippet)
        exit 2
    }
    return $code
}

# ------------------------------------------------------------------ collect input
if ($InfoJson -and $ReadJson -and (Test-Path -LiteralPath $InfoJson) -and (Test-Path -LiteralPath $ReadJson) `
        -and -not $Doc -and -not $Launch) {
    Write-Info 'using existing JSON (offline mode, CAD untouched)'
    if (-not (Test-ZwcResponse $InfoJson 'info')) { exit 2 }
    if (-not (Test-ZwcResponse $ReadJson 'read')) { exit 2 }
} else {
    $target = @()
    if ($App) { $target += @('-App', $App) } else { $target += @('-ProgId', $ProgId) }
    if ($Doc) { $target += @('-Doc', $Doc) }
    if ($Launch) { $target += '-Launch' }
    # big drawings are slow through COM (~100 ms/entity on this machine): the helper's
    # default 60 s timeout is not enough for a few hundred entities, so pass it through
    if ($TimeoutSec -gt 0) { $target += @('-TimeoutSec', [string]$TimeoutSec) }

    $infoArgs = @('info') + $target
    $infoCode = Invoke-Zwc $infoArgs $InfoJson -AllowFail
    if ($infoCode -ne 0) {
        # -Doc is only useful when that drawing is actually open. Two recoverable cases:
        #   DOC_NOT_FOUND          -> open it (read-only) and retry once
        #   NO_INSTANCE + -Launch  -> start CAD with the drawing, then retry once
        $err = Get-ZwcErrorCode $InfoJson
        $recoverable = $Doc -and ($err -eq 'DOC_NOT_FOUND' -or ($err -eq 'NO_INSTANCE' -and $Launch))
        if ($recoverable) {
            Write-Info ('  info returned ' + $err + '; opening the drawing first (open -Doc -ReadOnly)')
            $openArgs = @('open') + $target + @('-ReadOnly')
            $openPath = Join-Path $outDirFull 'open.json'
            $openCode = Invoke-Zwc $openArgs $openPath -AllowFail
            if ($openCode -ne 0 -or -not (Test-ZwcResponse $openPath 'open')) {
                Write-Info (('cannot open the drawing: ' + $err) + '; check the path, or open it in CAD yourself')
                exit 2
            }
            $infoCode = Invoke-Zwc $infoArgs $InfoJson -AllowFail
        }
    }
    if (-not (Test-ZwcResponse $InfoJson 'info')) { exit 2 }

    $readArgs = @('read') + $target + @('-Max', [string]$Max, '-Scope', $Scope)
    Invoke-Zwc $readArgs $ReadJson | Out-Null
    if (-not (Test-ZwcResponse $ReadJson 'read')) { exit 2 }
}

# ------------------------------------------------------------------ run the engine
$auditArgs = @($auditPy, '--read', $ReadJson, '--info', $InfoJson,
               '--json', $Json, '--md', $Md, '--exit-code')
if ($Rules) { $auditArgs += @('--rules', $Rules) }
if ($Config) { $auditArgs += @('--config', $Config) }
if ($Quiet) { $auditArgs += '--quiet' }

& $pyExe @auditArgs
$auditExit = $LASTEXITCODE

if ($auditExit -eq 3) { $gateState = 'BLOCKED' }
elseif ($auditExit -eq 0) { $gateState = 'PASS' }
else { $gateState = 'ERROR' }

Write-Info ''
Write-Info ('report (md)  : ' + $Md)
Write-Info ('report (json): ' + $Json)
Write-Info ('data (info)  : ' + $InfoJson)
Write-Info ('data (read)  : ' + $ReadJson)

if ($auditExit -ne 0 -and $auditExit -ne 3) {
    Write-Info ('AUDIT ERROR: audit.py exit ' + $auditExit)
    exit 4
}
if ($auditExit -eq 3) {
    Write-Info 'GATE: FAIL (blockers found; open the md report for the list)'
    if ($ExitCode) { exit 3 }
} else {
    Write-Info 'GATE: PASS (no blocker)'
}
exit 0
