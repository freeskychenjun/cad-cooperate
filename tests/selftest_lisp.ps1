# selftest_lisp.ps1 - offline regression tests for the LISP wire parser + dump template (M3-E6).
#
# Pure ASCII on purpose: on this machine the pwsh tool actually runs Windows PowerShell 5.1, which
# parses a BOM-less .ps1 with the ANSI code page, so any Chinese literal here would become mojibake
# and a syntax error. Everything Chinese belongs to the dumped data, never to this script.
#
# What it does (no CAD, no COM, no drawing is touched, nothing is injected):
#   1. compile tests\lispwire\LispWireTests.cs + scripts\helper\LispWire.cs             -> tmp\lispwire\
#   2. compile tests\lispwire\LispTemplateTests.cs + tests\lispwire\ZwcErrorStub.cs
#      + scripts\helper\LispTemplate.cs + scripts\helper\LispWire.cs                    -> tmp\lispwire\
#   3. run both exes, forward their PASS/FAIL lines, exit 1 when a suite fails to compile or reports
#      at least one FAIL.
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
    $cscArgs = @('-nologo', '-optimize+', '-target:exe', '-platform:anycpu', '-codepage:65001', ('-out:' + $Exe)) + $Sources
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
            (Join-Path $lispTests 'ZwcErrorStub.cs'),
            (Join-Path $helperDir 'LispTemplate.cs'),
            (Join-Path $helperDir 'LispWire.cs')
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
