# selftest_lisp.ps1 - offline regression tests for the helper's CAD-free logic: the LISP wire
# parser + dump template (M3-E6) and, since the 5.1 split, the core/COM/state/command units.
#
# Pure ASCII on purpose: on this machine the pwsh tool actually runs Windows PowerShell 5.1, which
# parses a BOM-less .ps1 with the ANSI code page, so any Chinese literal here would become mojibake
# and a syntax error. Everything Chinese belongs to the dumped data, never to this script.
#
# What it does (no CAD, no COM server, no drawing is touched, nothing is injected):
#   1. compile tests\lispwire\LispWireTests.cs + scripts\helper\LispWire.cs             -> tmp\lispwire\
#   2. compile tests\lispwire\LispTemplateTests.cs + tests\lispwire\CadErrorStub.cs
#      + scripts\helper\LispTemplate.cs + scripts\helper\LispWire.cs                    -> tmp\lispwire\
#   3. compile tests\jsonself\JsonTests.cs + scripts\helper\{Json,Core}.cs
#      (the real JMap from Core.cs, no stand-in)                                        -> tmp\lispwire\
#   4. compile tests\lispwire\LispScanTests.cs + tests\lispwire\LispScanStubs.cs
#      + tests\lispwire\CadErrorStub.cs + scripts\helper\{LispScan,LispWire,LispTemplate}.cs
#      (the injection pipeline: ssget filter, temp paths, load command, sentinel count, GBK
#       decode, phase classification, cleanup decision - all pure functions, no CAD)      -> tmp\lispwire\
#   5. compile tests\coreself\CoreTests.cs + scripts\helper\{Core,Json,State}.cs        -> tmp\lispwire\
#      (JMap/CadError/Ctx, Json round-trip, State's in-memory session/token helpers)
#   6. compile tests\coreself\ComCmdTests.cs + scripts\helper\{Core,Com,Cmd}.cs         -> tmp\lispwire\
#      (COM late-binding via reflection on POCOs, retry policy, Cmd's prompt/bracket guards)
#   7. run all suites, forward their PASS/FAIL lines, exit 1 when a suite fails to compile or
#      reports at least one FAIL.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tests\selftest_lisp.ps1
#         exit code 0 = every suite passed, 1 = compile failure or a FAIL line.

$ErrorActionPreference = 'Stop'

function Get-CscPath {
    foreach ($arch in @('Framework64', 'Framework')) {
        $cand = Join-Path $env:SystemRoot ('Microsoft.NET\' + $arch + '\v4.0.30319\csc.exe')
        if (Test-Path -LiteralPath $cand) { return $cand }
    }
    return $null
}

$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent $here
$helperDir = Join-Path $root 'scripts\helper'
$lispTests = Join-Path $here 'lispwire'
$jsonTests = Join-Path $here 'jsonself'
$coreTests = Join-Path $here 'coreself'
$outDir = Join-Path $root 'tmp\lispwire'

$csc = Get-CscPath
if (-not $csc) {
    Write-Host 'FAIL csc.exe (.NET Framework 4.x) not found - enable .NET Framework 4.8'
    exit 1
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Write-Host ('csc: ' + $csc)
Write-Host ('out: ' + $outDir)

function Compile-Suite {
    param([string]$Name, [string]$Exe, [string[]]$Sources)
    foreach ($src in $Sources) {
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Host ('FAIL compile ' + $Name + ': missing source ' + $src)
            return $false
        }
    }
    if (Test-Path -LiteralPath $Exe) {
        try { Remove-Item -LiteralPath $Exe -Force } catch { }
    }
    $cscArgs = @('-nologo', '-optimize+', '-target:exe', '-platform:anycpu', '-codepage:65001', '-nowarn:0649', ('-out:' + $Exe)) + $Sources
    $log = (& $csc @cscArgs 2>&1 | Out-String)
    if (-not (Test-Path -LiteralPath $Exe)) {
        Write-Host ('FAIL compile ' + $Name + ': ' + $log.Trim())
        return $false
    }
    if ($log.Trim().Length -gt 0) { Write-Host ('NOTE compile ' + $Name + ': ' + $log.Trim()) }
    return $true
}

$suites = @(
    [ordered]@{
        Name    = 'LispWireTests'
        Exe     = (Join-Path $outDir 'LispWireTests.exe')
        Sources = @((Join-Path $lispTests 'LispWireTests.cs'), (Join-Path $helperDir 'LispWire.cs'))
    },
    [ordered]@{
        Name    = 'LispTemplateTests'
        Exe     = (Join-Path $outDir 'LispTemplateTests.exe')
        Sources = @(
            (Join-Path $lispTests 'LispTemplateTests.cs'),
            (Join-Path $lispTests 'CadErrorStub.cs'),
            (Join-Path $helperDir 'LispTemplate.cs'),
            (Join-Path $helperDir 'LispWire.cs')
        )
    },
    [ordered]@{
        Name    = 'JsonTests'
        Exe     = (Join-Path $outDir 'JsonTests.exe')
        Sources = @(
            (Join-Path $jsonTests 'JsonTests.cs'),
            (Join-Path $helperDir 'Json.cs'),
            (Join-Path $helperDir 'Core.cs')
        )
    },
    [ordered]@{
        Name    = 'LispScanTests'
        Exe     = (Join-Path $outDir 'LispScanTests.exe')
        Sources = @(
            (Join-Path $lispTests 'LispScanTests.cs'),
            (Join-Path $lispTests 'LispScanStubs.cs'),
            (Join-Path $lispTests 'CadErrorStub.cs'),
            (Join-Path $helperDir 'LispScan.cs'),
            (Join-Path $helperDir 'LispWire.cs'),
            (Join-Path $helperDir 'LispTemplate.cs')
        )
    },
    [ordered]@{
        Name    = 'CoreTests'
        Exe     = (Join-Path $outDir 'CoreTests.exe')
        Sources = @(
            (Join-Path $coreTests 'CoreTests.cs'),
            (Join-Path $helperDir 'Core.cs'),
            (Join-Path $helperDir 'Json.cs'),
            (Join-Path $helperDir 'State.cs')
        )
    },
    [ordered]@{
        Name    = 'ComCmdTests'
        Exe     = (Join-Path $outDir 'ComCmdTests.exe')
        Sources = @(
            (Join-Path $coreTests 'ComCmdTests.cs'),
            (Join-Path $helperDir 'Core.cs'),
            (Join-Path $helperDir 'Com.cs'),
            (Join-Path $helperDir 'Cmd.cs')
        )
    }
)

$failed = 0
foreach ($suite in $suites) {
    Write-Host ('==== suite ' + $suite.Name)
    if (-not (Compile-Suite -Name $suite.Name -Exe $suite.Exe -Sources $suite.Sources)) { $failed++; continue }
    & $suite.Exe
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Write-Host ('==== selftest_lisp summary: suites=' + $suites.Count + ' failed=' + $failed)
if ($failed -ne 0) { exit 1 }
Write-Host '==== selftest_lisp: ALL PASS'
exit 0
