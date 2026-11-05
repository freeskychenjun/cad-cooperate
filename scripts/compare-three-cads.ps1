# compare-three-cads.ps1 - draw the SAME section model in every CAD product on this machine,
# capture evidence, and emit one comparison JSON. Pure ASCII on purpose (PS 5.1 reads a BOM-less
# .ps1 as ANSI, so Chinese literals here would break).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\compare-three-cads.ps1 `
#       -Model tmp\real\sample-station-2025.model.json -OutDir tmp\compare
#
# What one target goes through (identical to manual acceptance):
#   open -Launch -New  ->  section  ->  read  ->  screenshot  ->  undo  ->  finish -Quit
# Every step's JSON is kept under <OutDir>\<target>\ and the exit code is nonzero if any step
# failed. No CAD instance is left behind: finish -Quit only ever quits instances this tool started.

param(
    [Parameter(Mandatory=$true)][string]$Model,
    [string]$OutDir = 'tmp\compare',
    [string]$Include = 'zwcad16,zwcad2025,autocad2007',
    [int]$SectionTimeoutSec = 240,
    [int]$MaxEntities = 5000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cad = Join-Path $root 'scripts\cad.ps1'
$capture = Join-Path $root 'tmp\capture-section.ps1'

if (-not (Test-Path -LiteralPath $Model)) { throw "model not found: $Model" }
$OutDir = Join-Path $root $OutDir
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

function Invoke-Cad {
    param([string[]]$Arguments)
    $raw = & powershell -NoProfile -ExecutionPolicy Bypass -File $cad @Arguments 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $obj = $null
    try { $obj = $raw | ConvertFrom-Json } catch { }
    [pscustomobject]@{ exit = $exit; text = $raw; json = $obj }
}

function Save-Text {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$targets = @(
    [pscustomobject]@{ name = 'zwcad16';     app = 'zwcad';  progId = 'ZWCAD.Application.16';    openTimeout = 200 },
    [pscustomobject]@{ name = 'zwcad2025';   app = 'zwcad';  progId = 'ZWCAD.Application.2025';  openTimeout = 200 },
    [pscustomobject]@{ name = 'autocad2007'; app = 'autocad'; progId = 'AutoCAD.Application.17'; openTimeout = 320 }
) | Where-Object { $Include -split ',' -contains $_.name }

$summary = New-Object System.Collections.ArrayList

foreach ($t in $targets) {
    $dir = Join-Path $OutDir $t.name
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $appArgs = if ($t.app -eq 'autocad') { @('-App', 'autocad') } else { @('-ProgId', $t.progId) }
    Write-Output ("==== " + $t.name + " ====")

    $row = [ordered]@{
        target = $t.name; app = $t.app; progId = $t.progId
        ok = $false; openMs = $null; sectionMs = $null; readMs = $null; undoMs = $null
        created = $null; verify = $null; readCount = $null; removed = $null; afterCount = $null
        version = $null; pid = $null; png = $null; error = $null
    }

    # 1) launch a fresh drawing and attach
    $o = Invoke-Cad (@('open', '-Launch', '-New') + $appArgs + @('-TimeoutSec', [string]$t.openTimeout))
    Save-Text (Join-Path $dir 'open.json') $o.text
    if (-not ($o.json -and $o.json.ok)) {
        $row.error = 'OPEN_FAILED: ' + ($o.json.error.code, $o.json.error.message -join ' ')
        $summary.Add([pscustomobject]$row) | Out-Null
        Write-Output ("  open FAILED: " + $row.error)
        continue
    }
    $row.openMs = $o.json.wallMs
    $row.version = $o.json.result.app.version
    $row.pid = $o.json.result.app.pid
    Write-Output ("  open ok  version=" + $row.version + "  pid=" + $row.pid + "  wallMs=" + $row.openMs)

    # 2) draw the section
    $s = Invoke-Cad (@('section', '-Data', $Model) + $appArgs + @('-TimeoutSec', [string]$SectionTimeoutSec))
    Save-Text (Join-Path $dir 'section.json') $s.text
    if (-not ($s.json -and $s.json.ok)) {
        $row.error = 'SECTION_FAILED: ' + ($s.json.error.code, $s.json.error.message -join ' ')
        Invoke-Cad (@('finish', '-Quit') + $appArgs + @('-TimeoutSec', '120')) | Out-Null
        $summary.Add([pscustomobject]$row) | Out-Null
        Write-Output ("  section FAILED: " + $row.error)
        continue
    }
    $row.sectionMs = $s.json.elapsedMs
    $row.created = $s.json.result.created.total
    $row.verify = $s.json.result.verify.ok
    $token = $s.json.undoToken
    Write-Output ("  section ok  created=" + $row.created + "  verify=" + $row.verify + "  ms=" + $row.sectionMs)

    # 3) read back
    $r = Invoke-Cad (@('read') + $appArgs + @('-Max', [string]$MaxEntities))
    Save-Text (Join-Path $dir 'read.json') $r.text
    if ($r.json -and $r.json.ok) {
        $row.readMs = $r.json.elapsedMs
        $row.readCount = $r.json.result.counts.returned
        Write-Output ("  read ok  returned=" + $row.readCount + "  ms=" + $row.readMs)
    } else {
        Write-Output ("  read FAILED: " + ($r.json.error.code))
    }

    # 4) screenshot of the CAD window showing the drawing
    try {
        $png = Join-Path $env:TEMP ('cmp-' + $t.name + '.png')
        $capOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -App $t.app -ProgId $t.progId -Out $png 2>&1 | Out-String
        if (Test-Path -LiteralPath $png) {
            $dst = Join-Path $OutDir ('compare-' + $t.name + '.png')
            Copy-Item -LiteralPath $png -Destination $dst -Force
            $row.png = $dst
            Write-Output ("  screenshot saved: " + $dst)
        } else {
            Write-Output ("  screenshot FAILED: " + $capOut.Trim())
        }
    } catch {
        Write-Output ("  screenshot EXCEPTION: " + $_.Exception.Message)
    }

    # 5) one-step undo
    if ($token) {
        $u = Invoke-Cad (@('undo', '-Token', $token) + $appArgs)
        Save-Text (Join-Path $dir 'undo.json') $u.text
        if ($u.json -and $u.json.ok) {
            $row.undoMs = $u.json.elapsedMs
            $row.removed = $u.json.result.removed
            $row.afterCount = $u.json.result.countAfter
            Write-Output ("  undo ok  removed=" + $row.removed + "  after=" + $row.afterCount + "  ms=" + $row.undoMs)
        } else {
            Write-Output ("  undo FAILED: " + ($u.json.error.code))
        }
    }

    # 6) clean shutdown (only instances this tool started)
    $f = Invoke-Cad (@('finish', '-Quit') + $appArgs + @('-TimeoutSec', '180'))
    Save-Text (Join-Path $dir 'finish.json') $f.text
    if ($f.json -and $f.json.ok) {
        $left = @($f.json.result.processes).Count
        Write-Output ("  finish ok  docsClosed=" + $f.json.result.docsClosed + "  processesLeftInTool=" + $left)
    } else {
        Write-Output ("  finish FAILED: " + ($f.json.error.code))
    }

    $row.ok = [bool]$row.verify
    $summary.Add([pscustomobject]$row) | Out-Null
}

$summaryPath = Join-Path $OutDir 'summary.json'
Save-Text $summaryPath ($summary | ConvertTo-Json -Depth 6)
Write-Output ''
Write-Output '==== summary ===='
$summary | Format-Table target, ok, version, created, verify, readCount, sectionMs, readMs, undoMs, removed -AutoSize | Out-String | Write-Output
Write-Output ('saved: ' + $summaryPath)

$failed = @($summary | Where-Object { -not $_.ok }).Count
if ($failed -gt 0) { exit 2 } else { exit 0 }
