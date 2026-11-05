# run-real-validation.ps1 - reproducible real-CAD validation for the audit layer.
#
# Opens fixture drawings in a CAD instance that THIS SCRIPT owns, runs
# scripts\audit\run.ps1 (info + read + audit.py) on each of them, then quits its own
# instance and verifies the process list is back to zero.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\audit\run-real-validation.ps1
#   ... -ProgId ZWCAD.Application.2025
#   ... -RealDrawing "<真实图纸目录>\drawing.dwg"        # optional extra real drawing
#   ... -KeepInstance                                    # debug only: do not finish -Quit
#
# Why ZWCAD.Application.2025 and not Classic 16: on this machine several agents share the
# CAD boxes, and `open -Launch` ATTACHES to an already running instance instead of
# starting a new one (only `result.launched:true` proves the instance is ours). Running
# the audit validation on a different product keeps the two agents from fighting over one
# instance; -ProgId lets you pick another product when nothing else is using it.
#
# PURE ASCII: PowerShell 5.1 parses a .ps1 without BOM with the ANSI code page, so any
# non-ASCII literal in this file would become mojibake. Chinese paths are passed as
# runtime arguments (e.g. -RealDrawing), never as literals.
param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string]$App = '',
    [string]$OutDir = 'tmp\audit-real',
    [int]$TimeoutSec = 300,
    [string]$RealDrawing = '',
    [int]$Max = 20000,
    [switch]$KeepInstance,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Write-Info([string]$text) { [Console]::Out.WriteLine($text) }

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent (Split-Path -Parent $scriptDir)
$cadPath = Join-Path $root 'scripts\cad.ps1'
$runPs1 = Join-Path $root 'scripts\audit\run.ps1'
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Invoke-Cad([string[]]$cadArgs) {
    $text = & $psExe -NoProfile -ExecutionPolicy Bypass -File $cadPath @cadArgs 2>$null
    $code = $LASTEXITCODE
    $joined = ''
    if ($null -ne $text) {
        if ($text -is [array]) { $joined = ($text -join "`n") } else { $joined = [string]$text }
    }
    return [pscustomobject]@{ Code = $code; Text = $joined }
}

function Get-CadProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq 'ZWCAD' -or $_.ProcessName -eq 'acad'
    })
}

# Which product are we about to drive? Only a live instance of THAT product is a problem;
# another product (e.g. AutoCAD next to ZWCAD) cannot be attached by -ProgId.
function Get-TargetProcesses {
    $all = Get-CadProcesses
    if ($all.Count -eq 0) { return @() }
    if ($App -eq 'autocad') {
        return @($all | Where-Object { $_.ProcessName -eq 'acad' })
    }
    $marker = ''
    if ($ProgId -match '2025') { $marker = '2025' }
    elseif ($ProgId -match '\.16') { $marker = 'Classic' }
    $hits = @()
    foreach ($p in $all) {
        if ($p.ProcessName -ne 'ZWCAD') { continue }
        if (-not $marker) { $hits += $p; continue }
        $path = ''
        try { $path = [string]$p.Path } catch { $path = '' }
        if (-not $path) { $hits += $p; continue }
        if ($path -like ('*' + $marker + '*')) { $hits += $p }
    }
    return @($hits)
}

# ------------------------------------------------------------------ preflight: never attach
$existing = Get-TargetProcesses
if ($existing.Count -gt 0) {
    $list = ($existing | ForEach-Object { $_.Id.ToString() + ':' + $_.ProcessName }) -join ', '
    Write-Info ('REFUSING to run: a ' + $ProgId + ' instance is already running (' + $list + ').')
    Write-Info '`open -Launch` would ATTACH to one of them instead of starting our own instance,'
    Write-Info 'which lets `finish -Quit` kill another agent''s session. Close them (by pid, only'
    Write-Info 'if they are yours) and re-run.'
    exit 2
}

$target = @()
if ($App) { $target += @('-App', $App) } else { $target += @('-ProgId', $ProgId) }

$cases = New-Object System.Collections.ArrayList
[void]$cases.Add([pscustomobject]@{
    Name = 'problem-rich'; Doc = (Join-Path $root 'tests\audit\fixtures\problem-rich.dxf')
    Expect = 'blockers>=1, every rule exercised'
})
if (-not $NoClean) {
    [void]$cases.Add([pscustomobject]@{
        Name = 'clean'; Doc = (Join-Path $root 'tests\audit\fixtures\clean.dxf')
        Expect = 'zero issues (false-positive check)'
    })
}
if ($RealDrawing) {
    [void]$cases.Add([pscustomobject]@{
        Name = 'real-drawing'; Doc = $RealDrawing
        Expect = 'low issues, no blocker (false-positive check on real content)'
    })
}

$ownPid = $null
$results = New-Object System.Collections.ArrayList
$failure = $null

try {
    foreach ($case in $cases) {
        Write-Info ''
        Write-Info ('=== ' + $case.Name + ' : ' + $case.Doc)
        $openArgs = @('open') + $target + @('-Doc', $case.Doc, '-ReadOnly', '-TimeoutSec', [string]$TimeoutSec)
        if (-not $ownPid) { $openArgs += '-Launch' }
        $open = Invoke-Cad $openArgs
        if ($open.Code -ne 0) {
            Write-Info ('  ERROR open exit ' + $open.Code)
            $results.Add([pscustomobject]@{ Name = $case.Name; OpenExit = $open.Code; AuditExit = $null
                                             Launched = $null; Pid = $null }) | Out-Null
            $failure = 'open failed for ' + $case.Name
            break
        }
        $launched = $false
        $cadPid = $null
        $product = $null
        if ($open.Text -match '"launched"\s*:\s*(true|false)') { $launched = ($matches[1] -eq 'true') }
        if ($open.Text -match '"pid"\s*:\s*(\d+)') { $cadPid = [int]$matches[1] }
        if ($open.Text -match '"product"\s*:\s*"([^"]+)"') { $product = $matches[1] }
        if (-not $ownPid) {
            # this is the only place we accept to own an instance
            if (-not $launched) {
                Write-Info '  ERROR: open attached to an existing instance (launched:false); refusing to continue'
                $failure = 'not our instance'
                break
            }
            $ownPid = $cadPid
            Write-Info ('  owned instance: pid ' + $ownPid + ' (' + $product + ')')
        }

        $auditDir = Join-Path $OutDir $case.Name
        # no assignment here: run.ps1 must stream its summary to the console
        & $psExe -NoProfile -ExecutionPolicy Bypass -File $runPs1 @target `
            -Doc $case.Doc -Max $Max -TimeoutSec $TimeoutSec -OutDir $auditDir `
            -InfoJson (Join-Path $auditDir 'info.json') -ReadJson (Join-Path $auditDir 'read.json')
        $auditExit = $LASTEXITCODE

        # machine-check the expectation instead of eyeballing the console
        $issues = -1
        $blockers = -1
        $gatePass = $null
        $reportPath = Join-Path $auditDir 'report.json'
        if (Test-Path -LiteralPath $reportPath) {
            try {
                $rep = [System.IO.File]::ReadAllText($reportPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                $issues = [int]$rep.summary.issues
                $blockers = [int]$rep.summary.bySeverity.blocker
                $gatePass = [bool]$rep.gate.pass
            } catch {
                Write-Info ('  WARNING: cannot parse ' + $reportPath + ': ' + $_.Exception.Message)
            }
        }
        $expectation = 'n/a'
        $expectOk = $true
        if ($case.Name -eq 'problem-rich') {
            $expectation = 'blockers >= 1'
            $expectOk = ($blockers -ge 1)
        } elseif ($case.Name -eq 'clean') {
            $expectation = 'issues == 0'
            $expectOk = ($issues -eq 0)
        } elseif ($case.Name -eq 'real-drawing') {
            $expectation = 'blockers == 0 and issues <= 10'
            $expectOk = ($blockers -eq 0 -and $issues -ge 0 -and $issues -le 10)
        }
        if (-not $expectOk) { $failure = 'expectation failed for ' + $case.Name }
        $verdict = 'OK'
        if (-not $expectOk) { $verdict = 'FAILED' }
        Write-Info ('  issues=' + $issues + ' blockers=' + $blockers + ' gatePass=' + $gatePass `
                    + ' expected[' + $expectation + '] ' + $verdict)
        $results.Add([pscustomobject]@{ Name = $case.Name; OpenExit = $open.Code; AuditExit = $auditExit
                                         Launched = $launched; Pid = $cadPid; Issues = $issues
                                         Blockers = $blockers; Expectation = $expectation
                                         ExpectOk = $expectOk }) | Out-Null
        Write-Info ('  audit exit ' + $auditExit + ' -> ' + $auditDir)
        if ($failure) { break }
    }
}
finally {
    if ($ownPid -and -not $KeepInstance) {
        Write-Info ''
        Write-Info ('=== finish: quitting our own instance pid ' + $ownPid)
        $finish = Invoke-Cad (@('finish') + $target + @('-Quit', '-TimeoutSec', [string]$TimeoutSec))
        $closed = 'unknown'
        if ($finish.Text -match '"docsClosed"\s*:\s*(\d+)') { $closed = $matches[1] }
        Write-Info ('  finish exit ' + $finish.Code + ', docsClosed=' + $closed)
        Start-Sleep -Seconds 2
        $left = Get-CadProcesses
        if ($left.Count -gt 0) {
            $ids = ($left | ForEach-Object { $_.Id.ToString() }) -join ', '
            Write-Info ('  WARNING: CAD processes still running after finish: ' + $ids)
            Write-Info '  (only kill pids this script started; pid ' + $ownPid + ' is ours)'
            foreach ($p in $left) {
                if ($p.Id -eq $ownPid) {
                    Write-Info ('  stopping our pid ' + $p.Id)
                    try { Stop-Process -Id $p.Id -Force } catch { }
                }
            }
            Start-Sleep -Seconds 2
            $left = Get-CadProcesses
        }
        Write-Info ('  processes after finish: ' + $left.Count)
    }
}

Write-Info ''
Write-Info '=== results'
foreach ($r in $results) {
    $verdict = 'OK'
    if ($null -ne $r.ExpectOk -and -not $r.ExpectOk) { $verdict = 'FAILED' }
    Write-Info ('  ' + $r.Name.PadRight(14) + ' open=' + $r.OpenExit + ' audit=' + $r.AuditExit `
                + ' issues=' + $r.Issues + ' blockers=' + $r.Blockers + ' ' + $verdict)
}

$summaryPath = Join-Path $OutDir 'real-validation-summary.json'
$payload = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    progId = $ProgId
    app = $App
    ownedPid = $ownPid
    cases = @($results)
    failure = $failure
}
try {
    $json = $payload | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($summaryPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Info ('summary: ' + $summaryPath)
} catch {
    Write-Info ('WARNING: could not write summary: ' + $_.Exception.Message)
}

if ($failure) { exit 2 }
exit 0
