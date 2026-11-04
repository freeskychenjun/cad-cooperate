# run_bench.ps1 - M3-E4 CAD task benchmark runner (PS 5.1, PURE ASCII + BOM).
#
# Measures what an agent actually pays to finish a real CAD task: instead of timing one
# command in isolation it runs the task's natural command sequence (open -> read -> act
# -> verify) and records success, command count, wall clock, response bytes (~ tokens),
# failure class, one-click-undo and process hygiene.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench\run_bench.ps1 `
#       -Tasks all -Product zwcad2025 -Repeat 3
#   ... -Tasks Q1-layer-stats,Q4-big-read -Repeat 1 -Out tmp\bench\try
#   ... -Product zwcad16|zwcad2025|autocad     (ALWAYS explicit: three products installed)
#
# Design notes / discipline:
#   * One CAD instance for the whole run: launched by THIS script (result.launched:true),
#     finished by THIS script (finish -Quit on our own pid only). No other agent's
#     instance is touched; another product running next to ours is fine.
#   * -Freeze (default on): the CAD side (scripts\zwc.ps1 + scripts\helper\*.cs) is copied
#     into <Out>\frozen and the run uses that snapshot, so a teammate editing the helper
#     mid-run cannot mix two versions into one baseline. SHA256 of the snapshot is
#     recorded in summary.json. E1 comparison: run again with -Freeze on the new code.
#   * Every CAD command gets an explicit -ProgId/-App and an explicit -TimeoutSec
#     (ZWCAD 2025 has no LISP bridge: 624 entities took ~224 s on a cold read).
#   * Verdicts live in tests\bench\checkers\check.py (geometry/JSON assertions);
#     this script only orchestrates + measures.
#
# Exit codes: 0 = run completed (success or not, see summary), 2 = setup failure
#             (missing fixtures/CAD launch failed), 3 = process hygiene violated.

param(
    [string]$Tasks = 'all',
    [string]$TasksFile = '',
    [ValidateSet('zwcad16', 'zwcad2025', 'autocad')]
    [string]$Product = 'zwcad2025',
    [int]$Repeat = 3,
    [string]$Out = '',
    [ValidateSet('ps1', 'client', 'auto')]
    [string]$Entry = 'ps1',
    [string]$Python = '',
    [int]$MicroRepeat = 6,
    [int]$Warmup = 1,
    [bool]$Freeze = $true,
    [switch]$NoFreeze,
    [switch]$KeepInstance,
    [switch]$NoMicro
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Write-Log([string]$text) {
    $line = ('[' + (Get-Date).ToString('HH:mm:ss') + '] ' + $text)
    [Console]::Out.WriteLine($line)
    if ($script:LogPath) { [System.IO.File]::AppendAllText($script:LogPath, $line + "`n", (New-Object System.Text.UTF8Encoding($false))) }
}

function Median([object[]]$arr) {
    $vals = @($arr | Where-Object { $_ -ne $null } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($vals.Count -eq 0) { return 0 }
    $mid = [int][math]::Floor($vals.Count / 2)
    if ($vals.Count % 2 -eq 1) { return $vals[$mid] }
    return ($vals[$mid - 1] + $vals[$mid]) / 2.0
}

function Round1($v) { return [math]::Round([double]$v, 1) }

# ------------------------------------------------------------------ paths & tools
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent (Split-Path -Parent $scriptDir)
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$pyExe = $Python
if (-not $pyExe) { $pyExe = $env:ZWC_PYTHON }
if (-not $pyExe) {
    $candidate = 'python'
    if (Test-Path -LiteralPath $candidate) { $pyExe = $candidate } else { $pyExe = 'python' }
}

$tasksPath = Join-Path $root 'tests\bench\tasks.json'
if ($TasksFile) {
    if ([System.IO.Path]::IsPathRooted($TasksFile)) { $tasksPath = $TasksFile }
    else { $tasksPath = Join-Path $root $TasksFile }
}
$checkPy = Join-Path $root 'tests\bench\checkers\check.py'
$summarizePy = Join-Path $root 'tests\bench\checkers\summarize.py'
$fixturesDir = Join-Path $root 'tests\bench\fixtures'

if (-not (Test-Path -LiteralPath $tasksPath)) { Write-Log ('ERROR tasks.json not found: ' + $tasksPath); exit 2 }
if (-not (Test-Path -LiteralPath $checkPy)) { Write-Log ('ERROR check.py not found: ' + $checkPy); exit 2 }
$needFixture = Join-Path $fixturesDir 'expected.json'
if (-not (Test-Path -LiteralPath $needFixture)) {
    Write-Log ('ERROR fixtures missing: ' + $needFixture)
    Write-Log 'run: $env:PYTHONPATH=<repo>\tools\py-libs; python tests\bench\make_fixtures.py'
    exit 2
}

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
if (-not $Out) { $Out = Join-Path $root ('tmp\bench\' + $stamp) }
if (-not (Test-Path -LiteralPath $Out)) { New-Item -ItemType Directory -Force -Path $Out | Out-Null }
$outFull = (Resolve-Path -LiteralPath $Out).Path
$script:LogPath = Join-Path $outFull 'bench.log'

# ------------------------------------------------------------------ product mapping
$productArgs = @()
$expectedProduct = ''
switch ($Product) {
    'zwcad16' { $productArgs = @('-ProgId', 'ZWCAD.Application.16'); $expectedProduct = 'ZWCAD Classic 16' }
    'zwcad2025' { $productArgs = @('-ProgId', 'ZWCAD.Application.2025'); $expectedProduct = 'ZWCAD 2025' }
    'autocad' { $productArgs = @('-App', 'autocad'); $expectedProduct = 'AutoCAD' }
}
$productDesc = ($productArgs -join ' ')

# ---- run mutex: two benchmark sessions driving the same CAD product cross-talk through the
# shared helper state (%TEMP%\zwcad-cooperate\state.json) and the shared scratch drawing name,
# which shows up as "read returned a stale drawing" glitches. One session per product.
# FileShare.None: if the process dies the OS releases the handle, so there is no stale lock.
$lockPath = Join-Path $env:TEMP ('zwcad-bench-' + $Product + '.lock')
try {
    $script:benchLock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
} catch {
    Write-Log ('REFUSING to run: another "' + $Product + '" benchmark session holds ' + $lockPath)
    Write-Log '        (two sessions on one CAD product cross-talk via the shared helper state;'
    Write-Log '         wait for the other run to finish, or use a different -Product)'
    exit 2
}

function Get-PropSafe($obj, [string]$path) {
    $cur = $obj
    foreach ($part in $path.Split('.')) {
        if ($null -eq $cur) { return $null }
        try { $cur = $cur.$part } catch { return $null }
    }
    return $cur
}

function Get-CadProcesses {
    $procs = @()
    try {
        $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessName -eq 'ZWCAD' -or $_.ProcessName -eq 'acad'
        })
    } catch { $procs = @() }
    return $procs
}

function Get-TargetProductProcesses {
    # only instances of the product THIS run drives matter for launch/attachment safety
    $hits = @()
    foreach ($p in (Get-CadProcesses)) {
        $path = ''
        try { $path = [string]$p.Path } catch { $path = '' }
        if ($Product -eq 'autocad') {
            if ($p.ProcessName -eq 'acad') { $hits += $p }
        } elseif ($p.ProcessName -eq 'ZWCAD') {
            if ($Product -eq 'zwcad2025' -and $path -match '2025') { $hits += $p }
            elseif ($Product -eq 'zwcad16' -and $path -match 'Classic') { $hits += $p }
        }
    }
    return @($hits)
}

function Get-CadProductList {
    $list = @()
    foreach ($p in (Get-CadProcesses)) {
        $path = ''
        try { $path = [string]$p.Path } catch { $path = '' }
        $label = $p.ProcessName
        if ($path -match '2025') { $label = 'ZWCAD 2025' }
        elseif ($path -match 'Classic') { $label = 'ZWCAD Classic 16' }
        elseif ($p.ProcessName -eq 'acad') { $label = 'AutoCAD 2007' }
        $list += [pscustomobject]@{ pid = $p.Id; name = $p.ProcessName; path = $path; product = $label }
    }
    return @($list)
}

# ------------------------------------------------------------------ system under test
$zwcPath = Join-Path $root 'scripts\zwc.ps1'
$clientPath = Join-Path $root 'bin\ZwcClient.exe'
$freezeInfo = $null
$useFreeze = $Freeze
if ($NoFreeze) { $useFreeze = $false }
if ($useFreeze) {
    $frozen = Join-Path $outFull 'frozen'
    $fzScripts = Join-Path $frozen 'scripts'
    $fzHelper = Join-Path $fzScripts 'helper'
    New-Item -ItemType Directory -Force -Path $fzHelper | Out-Null
    Copy-Item -LiteralPath $zwcPath -Destination (Join-Path $fzScripts 'zwc.ps1') -Force
    Copy-Item -Path (Join-Path $root 'scripts\helper\*.cs') -Destination $fzHelper -Force
    if (Test-Path -LiteralPath $clientPath) {
        $fzBin = Join-Path $frozen 'bin'
        New-Item -ItemType Directory -Force -Path $fzBin | Out-Null
        Copy-Item -LiteralPath $clientPath -Destination (Join-Path $fzBin 'ZwcClient.exe') -Force
    }
    $zwcPath = Join-Path $fzScripts 'zwc.ps1'
    $clientPath = Join-Path $frozen 'bin\ZwcClient.exe'
    $freezeInfo = [ordered]@{ root = $frozen; files = @(); sha256 = [ordered]@{} }
    foreach ($f in @(Get-ChildItem -LiteralPath $fzScripts -File -Recurse)) {
        $rel = $f.FullName.Substring($frozen.Length + 1)
        $freezeInfo.files += $rel
        $freezeInfo.sha256[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLower()
    }
}
$liveHashes = [ordered]@{}
foreach ($rel in @('scripts\zwc.ps1', 'scripts\helper\ZwcHelper.cs', 'scripts\helper\SectionOverlay.cs')) {
    $full = Join-Path $root $rel
    if (Test-Path -LiteralPath $full) { $liveHashes[$rel] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower() }
}

$entryMode = $Entry
$script:scratchDoc = $null
$script:workdoc = $null
if ($Entry -eq 'auto') {
    if (Test-Path -LiteralPath $clientPath) { $entryMode = 'client' } else { $entryMode = 'ps1' }
}
if ($entryMode -eq 'client' -and -not (Test-Path -LiteralPath $clientPath)) {
    Write-Log ('WARNING: -Entry client requested but no ZwcClient.exe; falling back to ps1')
    $entryMode = 'ps1'
}

# ------------------------------------------------------------------ task model
$tasksDoc = [System.IO.File]::ReadAllText($tasksPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$allTasks = @($tasksDoc.tasks)
$defaults = $tasksDoc.defaults
$assets = @{}

function Resolve-Asset([string]$name) {
    $val = $null
    if ($assets.ContainsKey($name)) { $val = $assets[$name] }
    if (-not $val) { throw ('unknown asset: ' + $name) }
    # absolute path -> as is; relative path -> against repo root; plain scalar (e.g. a station
    # code like <站码>) -> as is, do NOT turn it into a path
    if ($val -match '^[A-Za-z]:' -or $val.StartsWith('\\')) { return $val }
    if ($val -match '[\\/]') { return (Join-Path $root $val) }
    return $val
}
foreach ($k in $tasksDoc.assets.PSObject.Properties.Name) { $assets[$k] = [string]$tasksDoc.assets.$k }

$selected = @()
if ($Tasks -eq 'all' -or $Tasks -eq '') {
    $selected = $allTasks
} else {
    $wanted = @($Tasks.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($id in $wanted) {
        $task = $allTasks | Where-Object { $_.id -eq $id }
        if (-not $task) { Write-Log ('ERROR unknown task id: ' + $id); exit 2 }
        $selected += $task
    }
}

# ------------------------------------------------------------------ step helpers
function Expand-Vars([string]$text, $stepJson, [string]$runDir) {
    if ($null -eq $text) { return $text }
    $s = [string]$text
    $s = $s.Replace('{repo}', $root).Replace('{rundir}', $runDir)
    if ($s.Contains('{scratch}')) {
        if (-not $script:scratchDoc) { throw ('{scratch} is not available yet (no scratch drawing was created)') }
        $s = $s.Replace('{scratch}', [string]$script:scratchDoc)
    }
    if ($s.Contains('{workdoc}')) {
        if (-not $script:workdoc) { throw ('{workdoc} is not available (task has no setup.copyFixture)') }
        $s = $s.Replace('{workdoc}', [string]$script:workdoc)
    }
    $s = [regex]::Replace($s, '\{fixture:([^}]+)\}', { param($m) Join-Path $fixturesDir $m.Groups[1].Value })
    $s = [regex]::Replace($s, '\{asset:([^}]+)\}', { param($m) Resolve-Asset $m.Groups[1].Value })
    $s = [regex]::Replace($s, '\{stepfile:([^}]+)\}', { param($m) Join-Path $runDir ($m.Groups[1].Value + '.json') })
    $s = [regex]::Replace($s, '\{ref:([^}.:]+)\.([^}]+)\}', {
        param($m)
        $step = $m.Groups[1].Value
        $path = $m.Groups[2].Value
        if (-not $stepJson.ContainsKey($step)) { throw ('{ref} to unknown step: ' + $step) }
        $obj = $stepJson[$step]
        foreach ($part in $path.Split('.')) {
            if ($null -eq $obj) { break }
            $obj = $obj.$part
        }
        if ($null -eq $obj) { throw ('{ref:' + $step + '.' + $path + '} resolved to null') }
        return [string]$obj
    })
    return $s
}

# Bounded invocation: a CAD call must NEVER wedge the whole benchmark. zwc.ps1 bounds the
# helper itself, but a CAD modal dialog / wedged COM call can still keep the child alive, so
# the runner adds its own watchdog (timeoutSec + 60 s) and kills the child if needed.
function Invoke-ProcessBounded([string]$exe, [string[]]$argv, [int]$timeoutSec,
                                [string]$stdoutPath, [string]$stderrPath) {
    # Direct System.Diagnostics.Process (not Start-Process): its .ExitCode is reliable, and the
    # async stdout/stderr readers avoid the classic pipe-buffer deadlock. Redirect files are kept
    # only as a debugging breadcrumb.
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $argLine = (($argv | ForEach-Object {
        $a = [string]$_
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }) -join ' ')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $root
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try {
        $null = $proc.Start()
    } catch {
        $watch.Stop()
        return [pscustomobject]@{ exit = -1; wallMs = [double]$watch.ElapsedMilliseconds
            text = ('RUNNER_START_FAILED: ' + $_.Exception.Message); watchdog = $false }
    }
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit(($timeoutSec + 60) * 1000)
    $watchdog = $false
    if (-not $exited) {
        $watchdog = $true
        try { $proc.Kill() } catch { }
        try { $null = $proc.WaitForExit(10000) } catch { }
    }
    $proc.WaitForExit()
    $watch.Stop()
    $out = ''
    $err = ''
    try { $out = [string]$outTask.Result } catch { }
    try { $err = [string]$errTask.Result } catch { }
    if (Test-Path -LiteralPath $stdoutPath) {
        try { [System.IO.File]::WriteAllText($stdoutPath, $out, (New-Object System.Text.UTF8Encoding($false))) } catch { }
    }
    if (Test-Path -LiteralPath $stderrPath) {
        try { [System.IO.File]::WriteAllText($stderrPath, $err, (New-Object System.Text.UTF8Encoding($false))) } catch { }
    }
    $exit = 0
    try { $exit = [int]$proc.ExitCode } catch { $exit = -1 }
    $text = $out
    if ($watchdog) {
        $exit = -9
        $text = 'RUNNER_WATCHDOG_TIMEOUT: the CAD child did not answer within ' +
                [string]($timeoutSec + 60) + ' s (modal dialog or wedged COM call?)'
    }
    if ($err.Trim()) {
        $tail = $err
        if ($tail.Length -gt 1500) { $tail = $tail.Substring($tail.Length - 1500) }
        $text = $text + "`n[stderr] " + $tail
    }
    return [pscustomobject]@{ exit = $exit; wallMs = [double]$watch.ElapsedMilliseconds
        text = $text; watchdog = $watchdog }
}
function Parse-JsonText([string]$text) {    if (-not $text) { return $null }
    $t = $text.Trim()
    try { return ($t | ConvertFrom-Json) } catch { }
    # python steps may print a human line before the json: fall back to the outermost {...}
    $start = $t.IndexOf('{')
    $end = $t.LastIndexOf('}')
    if ($start -ge 0 -and $end -gt $start) {
        try { return ($t.Substring($start, $end - $start + 1) | ConvertFrom-Json) } catch { }
    }
    return $null
}

function Invoke-ToolStep($step, [string]$runDir, $stepJson, [bool]$omitProduct) {
    $name = [string]$step.name
    $kind = 'cad'
    if ($step.kind) { $kind = [string]$step.kind }
    $timeoutSec = 300
    if ($step.timeoutSec) { $timeoutSec = [int]$step.timeoutSec }
    elseif ($defaults.timeoutSec) { $timeoutSec = [int]$defaults.timeoutSec }

    $expanded = @()
    foreach ($a in @($step.args)) { $expanded += (Expand-Vars ([string]$a) $stepJson $runDir) }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = 0
    $text = ''
    $label = $name
    $cadWallMs = $null
    $watchdogFlag = $false
    try {
        if ($kind -eq 'py') {
            $scriptRel = [string]$step.script
            $scriptFull = Join-Path $root $scriptRel
            if (-not (Test-Path -LiteralPath $scriptFull)) { throw ('python script not found: ' + $scriptFull) }
            $label = 'py:' + $scriptRel
            $env:PYTHONPATH = (Join-Path $root 'tools\py-libs')
            Push-Location -LiteralPath $root
            try {
                $out = & $pyExe $scriptFull @expanded 2>&1
                $exit = $LASTEXITCODE
                if ($null -ne $out) {
                    if ($out -is [array]) { $text = ($out -join "`n") } else { $text = [string]$out }
                }
            } finally { Pop-Location }
        } else {
            $cmd = [string]$step.cmd
            $label = 'cad:' + $cmd
            $argv = @($cmd)
            if (-not $omitProduct) { $argv += $productArgs }
            $argv += $expanded
            if ($timeoutSec -gt 0) { $argv += @('-TimeoutSec', [string]$timeoutSec) }
            $stdoutPath = Join-Path $runDir ($name + '.stdout.tmp')
            $stderrPath = Join-Path $runDir ($name + '.stderr.tmp')
            if ($entryMode -eq 'client') {
                $bounded = Invoke-ProcessBounded $clientPath $argv $timeoutSec $stdoutPath $stderrPath
            } else {
                $psArgv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $zwcPath) + $argv
                $bounded = Invoke-ProcessBounded $psExe $psArgv $timeoutSec $stdoutPath $stderrPath
            }
            $exit = $bounded.exit
            $text = $bounded.text
            $cadWallMs = $bounded.wallMs
            $watchdogFlag = $bounded.watchdog
            Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
        }
    } catch {
        $text = 'RUNNER_EXCEPTION: ' + $_.Exception.Message
        $exit = -1
    }
    $watch.Stop()
    $elapsedMs = [double]$watch.ElapsedMilliseconds
    if ($null -ne $cadWallMs) { $elapsedMs = [double]$cadWallMs }

    $rawPath = Join-Path $runDir ($name + '.raw.txt')
    [System.IO.File]::WriteAllText($rawPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    $parsed = Parse-JsonText $text
    $parseError = $null
    if ($null -eq $parsed -and $kind -eq 'cad') { $parseError = 'output is not valid JSON' }
    if ($null -ne $parsed) {
        [System.IO.File]::WriteAllText((Join-Path $runDir ($name + '.json')), $text.Trim(),
            (New-Object System.Text.UTF8Encoding($false)))
        $stepJson[$name] = $parsed
    }
    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($text)
    $helperMs = $null
    $reportedWallMs = $null
    if ($parsed) {
        try { if ($parsed.elapsedMs -ne $null) { $helperMs = [double]$parsed.elapsedMs } } catch { }
        try { if ($parsed.wallMs -ne $null) { $reportedWallMs = [double]$parsed.wallMs } } catch { }
    }
    $meta = [ordered]@{
        step = $name
        kind = $kind
        label = $label
        exit = $exit
        expectedExit = $(if ($step.expectExit -ne $null) { [int]$step.expectExit } else { 0 })
        wallMs = $elapsedMs
        helperMs = $helperMs
        wrapperWallMs = $reportedWallMs
        bytes = $bytes
        tokensEst = [math]::Round($bytes / 3.2, 0)
        parseError = $parseError
        errorCode = $(if ($parsed -and $parsed.error) { [string]$parsed.error.code } else { $null })
        watchdog = $watchdogFlag
        argv = @($argv)
    }
    [System.IO.File]::WriteAllText((Join-Path $runDir ($name + '.meta.json')),
        ($meta | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Write-Log ('    step ' + $name.PadRight(12) + ' exit=' + $exit + '  ' + [int]$elapsedMs + ' ms  ' + $bytes + ' B' + $(if ($parseError) { '  PARSE_ERROR' } else { '' }) + $(if ($watchdogFlag) { '  WATCHDOG_TIMEOUT' } else { '' }))
    return [pscustomobject]@{ name = $name; meta = $meta; parsed = $parsed; text = $text }
}

# ------------------------------------------------------------------ task summary
function New-TaskSummary($task, [object[]]$reps) {
    $walls = @($reps | ForEach-Object { [double]$_.wallMs })
    $cmds = @($reps | ForEach-Object { [int]$_.executedSteps })
    $bytes = @($reps | ForEach-Object { [int]$_.bytes })
    $okCount = @($reps | Where-Object { $_.ok }).Count
    $gapCount = @($reps | Where-Object { $_.kind -eq 'capability_gap' }).Count
    $kindSet = @($reps | ForEach-Object { [string]$_.kind } | Select-Object -Unique)
    $kind = 'failure'
    if ($kindSet.Count -eq 1) { $kind = [string]$kindSet[0] }
    elseif ($gapCount -gt 0 -and $okCount -gt 0) { $kind = 'mixed' }
    $medWall = Median $walls
    $medCmds = Median $cmds
    $medBytes = Median $bytes
    $perCmd = 0
    if ($medCmds -gt 0) { $perCmd = Round1 ($medWall / $medCmds) }
    $undo = $null
    $undoReps = @($reps | Where-Object { $null -ne $_.undo })
    if ($undoReps.Count -gt 0) {
        $undoOkCount = @($undoReps | Where-Object { $_.undo.ok }).Count
        $undo = [ordered]@{
            ok = ($undoOkCount -eq $undoReps.Count)
            okCount = $undoOkCount
            runs = $undoReps.Count
            medianWallMs = Round1 (Median @($undoReps | ForEach-Object { $_.undo.wallMs }))
            countAfter = @($undoReps | ForEach-Object { $_.undo.countAfter })
        }
    }
    $reasons = @()
    foreach ($r in $reps) { foreach ($why in @($r.reasons)) { if ($reasons.Count -lt 6) { $reasons += [string]$why } } }
    return [ordered]@{
        id = [string]$task.id
        category = [string]$task.category
        goal = [string]$task.goal
        why = [string]$task.why
        kind = $kind
        successRate = $(if ($reps.Count -gt 0) { Round1 (100.0 * $okCount / $reps.Count) } else { 0 })
        okCount = $okCount
        gapConfirmed = $gapCount
        runs = $reps.Count
        median = [ordered]@{
            wallMs = Round1 $medWall
            commands = [math]::Round($medCmds, 0)
            wallPerCommandMs = $perCmd
            bytes = [math]::Round($medBytes, 0)
            tokensEst = [math]::Round($medBytes / 3.2, 0)
        }
        undo = $undo
        procsAfter = [math]::Round((Median @($reps | ForEach-Object { $_.procsAfter })), 0)
        reasons = $reasons
        reps = @($reps)
    }
}

# ------------------------------------------------------------------ one task repetition
function Invoke-TaskRep($task, [int]$rep) {
    $runDir = Join-Path (Join-Path $outFull ([string]$task.id)) ('rep' + $rep)
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    $context = [ordered]@{
        taskId = [string]$task.id
        product = $Product
        expectedProduct = $expectedProduct
        zwcadProducts = @((Get-CadProductList) | ForEach-Object { $_.product })
        procsBefore = @(Get-CadProcesses).Count
        generatedAt = (Get-Date).ToString('o')
    }
    $contextPath = Join-Path $runDir 'context.json'
    [System.IO.File]::WriteAllText($contextPath, ($context | ConvertTo-Json -Depth 5),
        (New-Object System.Text.UTF8Encoding($false)))

    $stepJson = @{}
    $steps = @()
    $lastUndoToken = $null
    $failedStep = $null
    $omitProduct = $false
    if ($task.omitProduct -eq $true) { $omitProduct = $true }
    # per-rep fixture copy for write tasks: the drawing must be mutated, so each repetition
    # works on a private copy (the source fixture stays pristine); not counted as a CAD command
    $script:workdoc = $null
    $copySpec = $null
    if ($task.setup -and $task.setup.copyFixture) { $copySpec = [string]$task.setup.copyFixture }
    if ($copySpec) {
        $srcFixture = Join-Path $fixturesDir $copySpec
        if (-not (Test-Path -LiteralPath $srcFixture)) { throw ('fixture not found: ' + $srcFixture) }
        $workDocPath = Join-Path $runDir ('work-' + $copySpec)
        Copy-Item -LiteralPath $srcFixture -Destination $workDocPath -Force
        $script:workdoc = $workDocPath
        Write-Log ('    work drawing: ' + $workDocPath)
    }
    $t0 = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Log ('  rep ' + $rep + ' -> ' + $runDir)
    foreach ($step in @($task.steps)) {
        $expected = 0
        if ($step.expectExit -ne $null) { $expected = [int]$step.expectExit }
        try {
            $r = Invoke-ToolStep $step $runDir $stepJson $omitProduct
        } catch {
            Write-Log ('    step ' + [string]$step.name + ' RUNNER ERROR: ' + $_.Exception.Message)
            $failedStep = [string]$step.name
            break
        }
        $steps += $r.meta
        # remember an undo token from any step (section responses carry it)
        if ($r.parsed) {
            try {
                if ($r.parsed.result -and $r.parsed.result.undoToken) { $lastUndoToken = [string]$r.parsed.result.undoToken }
                elseif ($r.parsed.undoToken) { $lastUndoToken = [string]$r.parsed.undoToken }
            } catch { }
        }
        if ($r.meta.exit -ne $expected) {
            $failedStep = [string]$step.name
            break
        }
    }
    $t0.Stop()

    # ---- verdict
    $verdictPath = Join-Path $runDir 'verdict.json'
    $verdictArgs = @($checkPy, '--task', [string]$task.id, '--dir', $runDir,
                     '--expected', (Join-Path $fixturesDir 'expected.json'),
                     '--tasks', $tasksPath, '--context', $contextPath)
    $vText = ''
    try {
        $vText = (& $pyExe @verdictArgs 2>&1) -join "`n"
    } catch {
        $vText = '{"ok":false,"kind":"error","reasons":["runner could not run checker: ' + $_.Exception.Message + '"],"metrics":{}}'
    }
    [System.IO.File]::WriteAllText($verdictPath, $vText, (New-Object System.Text.UTF8Encoding($false)))
    $verdict = Parse-JsonText $vText
    if (-not $verdict) { $verdict = [pscustomobject]@{ ok = $false; kind = 'error'; reasons = @('unparsable verdict'); metrics = @{} } }

    # ---- one-click undo check (teardown, measured but NOT part of task cost)
    $undo = $null
    if ($task.undoCheck -and $lastUndoToken) {
        # DESIGN: every document-scoped command pins -Doc (else DOC_AMBIGUOUS with >1 doc open)
        $teardownDoc = $null
        if ($script:workdoc) { $teardownDoc = $script:workdoc } elseif ($script:scratchDoc) { $teardownDoc = $script:scratchDoc }
        $undoArgs = @()
        $undoReadArgs = @()
        if ($teardownDoc) { $undoArgs += @('-Doc', $teardownDoc); $undoReadArgs += @('-Doc', $teardownDoc) }
        $undoArgs += @('-Token', $lastUndoToken)
        $undoReadArgs += @('-Max', '20000')
        $undoStep = [pscustomobject]@{ name = 'teardown-undo'; kind = 'cad'; cmd = 'undo'; args = $undoArgs; timeoutSec = 120 }
        $u = Invoke-ToolStep $undoStep $runDir $stepJson $false
        $countAfter = $null
        $undoCountStep = [pscustomobject]@{ name = 'teardown-read'; kind = 'cad'; cmd = 'read'; args = $undoReadArgs; timeoutSec = 300 }
        $ur = Invoke-ToolStep $undoCountStep $runDir $stepJson $false
        if ($ur.parsed -and $ur.parsed.result -and $ur.parsed.result.counts) { $countAfter = $ur.parsed.result.counts.model }
        # undo is "one click" when it succeeded AND either its own verify says the drawing is
        # back to the pre-batch baseline, or (for a fresh scratch doc) the count is back to 0.
        # A fixture-copy task starts from a non-empty drawing, so countAfter>0 is fine there.
        $undoVerifyOk = $null
        if ($u.parsed -and $u.parsed.result -and $u.parsed.result.verify) {
            if ($u.parsed.result.verify.ok -ne $null) { $undoVerifyOk = [bool]$u.parsed.result.verify.ok }
        }
        $undoOk = $false
        if ($u.meta.exit -eq 0) {
            if ($undoVerifyOk -eq $true) { $undoOk = $true }
            elseif ($countAfter -eq 0) { $undoOk = $true }
        }
        $undo = [ordered]@{ ok = $undoOk; wallMs = [double]$u.meta.wallMs; countAfter = $countAfter; exit = $u.meta.exit }
        Write-Log ('    teardown undo: ok=' + $undoOk + ' countAfter=' + $countAfter + ' ' + [int]$u.meta.wallMs + ' ms')
    }

    $procs = Get-CadProductList
    $totalWall = [double]$t0.ElapsedMilliseconds
    $bytesSum = 0
    $helperSum = 0.0
    foreach ($s in $steps) {
        $bytesSum += [int]$s.bytes
        if ($s.helperMs -ne $null) { $helperSum += [double]$s.helperMs }
    }
    return [pscustomobject]@{
        rep = $rep
        ok = [bool]$verdict.ok
        kind = [string]$verdict.kind
        reasons = @($verdict.reasons)
        metrics = $verdict.metrics
        commands = @($task.steps).Count
        executedSteps = $steps.Count
        wallMs = $totalWall
        bytes = $bytesSum
        tokensEst = [math]::Round($bytesSum / 3.2, 0)
        helperMs = [math]::Round($helperSum, 1)
        failedStep = $failedStep
        steps = $steps
        undo = $undo
        procsAfter = @(Get-TargetProductProcesses).Count
        procsAfterList = @($procs | ForEach-Object { $_.product })
        dir = $runDir
    }
}

# ------------------------------------------------------------------ launch / finish
function Invoke-BenchCommand([string[]]$argv, [string]$label, [int]$timeoutSec) {
    if ($timeoutSec -le 0) { $timeoutSec = 180 }
    $stdoutPath = Join-Path $outFull ('_benchcmd-' + $label.Replace(' ', '_') + '.out')
    $stderrPath = Join-Path $outFull ('_benchcmd-' + $label.Replace(' ', '_') + '.err')
    if ($entryMode -eq 'client') {
        $bounded = Invoke-ProcessBounded $clientPath $argv $timeoutSec $stdoutPath $stderrPath
    } else {
        $psArgv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $zwcPath) + $argv
        $bounded = Invoke-ProcessBounded $psExe $psArgv $timeoutSec $stdoutPath $stderrPath
    }
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    $parsed = Parse-JsonText $bounded.text
    Write-Log ('  ' + $label + ': exit=' + $bounded.exit + ' ' + [int]$bounded.wallMs + ' ms')
    return [pscustomobject]@{
        exit = $bounded.exit
        wallMs = $bounded.wallMs
        text = $bounded.text
        parsed = $parsed
        helperMs = $(if ($parsed -and $parsed.elapsedMs -ne $null) { [double]$parsed.elapsedMs } else { $null })
    }
}

# ================================================================== RUN
Write-Log ('=== M3-E4 benchmark | product=' + $Product + ' (' + $productDesc + ') | repeat=' + $Repeat)
Write-Log ('    entry=' + $entryMode + ' | freeze=' + $useFreeze + ' | out=' + $outFull)
Write-Log ('    tasks=' + $selected.Count + ' / ' + $allTasks.Count)

$result = [ordered]@{
    meta = [ordered]@{
        schema = 'zwcad-bench-summary/1'
        stamp = $stamp
        product = $Product
        productArgs = $productDesc
        expectedProduct = $expectedProduct
        repeat = $Repeat
        entry = $entryMode
        python = $pyExe
        host = $env:COMPUTERNAME
        startedAt = (Get-Date).ToString('o')
        tasksPath = $tasksPath
        freeze = $freezeInfo
        liveHashes = $liveHashes
    }
    micro = [ordered]@{}
    launch = $null
    tasks = @()
    failures = @()
    productBugs = @()
    hygiene = [ordered]@{}
    endedAt = $null
    durationMs = $null
}

$startAll = [System.Diagnostics.Stopwatch]::StartNew()

# ---- phase A: tasks that do not need a CAD instance (run before we launch ours)
$preTasks = @($selected | Where-Object { $_.phase -eq 'pre-launch' -or $_.phase -eq 'no-cad' })
$mainTasks = @($selected | Where-Object { $_.phase -ne 'pre-launch' -and $_.phase -ne 'no-cad' })

if ($preTasks.Count -gt 0) {
    Write-Log '--- phase A: tasks without our CAD instance'
    $instances = @(Get-TargetProductProcesses)
    foreach ($t in $preTasks) {
        if ($t.requiresNoCad -eq $true -and $instances.Count -gt 0) {
            Write-Log ('  ' + $t.id + ': SKIP (a ' + $Product + ' instance is already running; cannot test the no-instance path)')
            $result.tasks += [ordered]@{ id = $t.id; category = $t.category; goal = $t.goal
                reps = @(); successRate = 0; kind = 'setup_unavailable'; median = $null
                reasons = @('a ' + $Product + ' instance was already running') }
            continue
        }
        $reps = @()
        for ($i = 1; $i -le $Repeat; $i++) {
            $r = Invoke-TaskRep $t $i
            $reps += $r
            Write-Log ('  -> ' + $r.kind + ' commands=' + $r.commands + ' wall=' + [int]$r.wallMs + ' ms')
            foreach ($why in $r.reasons) { Write-Log ('     reason: ' + $why) }
        }
        $result.tasks += (New-TaskSummary $t $reps)
    }
}

# ---- launch our own instance
$launchedPid = $null
if ($mainTasks.Count -gt 0) {
    $preexisting = @(Get-TargetProductProcesses)
    if ($preexisting.Count -gt 0) {
        $ids = ($preexisting | ForEach-Object { $_.Id.ToString() }) -join ', '
        Write-Log ('ERROR: a ' + $Product + ' instance is already running (pid ' + $ids + ').')
        Write-Log '       `open -Launch` would ATTACH to it instead of starting our own instance, and a later'
        Write-Log '       `finish -Quit` would then close another agent''s session. Refusing to run.'
        Write-Log '       Close that instance yourself (only if it is yours) and re-run.'
        [System.IO.File]::WriteAllText((Join-Path $outFull 'summary.json'),
            ($result | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        exit 2
    }
    Write-Log '--- launch (own instance, launched:true required)'
    $launch = Invoke-BenchCommand (@('open', '-New', '-Launch') + $productArgs + @('-TimeoutSec', '300')) 'open -New -Launch' 300
    [System.IO.File]::WriteAllText((Join-Path $outFull 'launch.json'), $launch.text,
        (New-Object System.Text.UTF8Encoding($false)))
    $launched = $false
    $launchedPid = $null
    if ($launch.parsed) {
        foreach ($p in @('result.launched', 'result.app.launched', 'launched', 'app.launched')) {
            $v = Get-PropSafe $launch.parsed $p
            if ($v -eq $true) { $launched = $true; break }
        }
        foreach ($p in @('result.app.pid', 'app.pid', 'result.pid')) {
            $v = Get-PropSafe $launch.parsed $p
            if ($v) { $launchedPid = [int]$v; break }
        }
    }
    $result.launch = [ordered]@{ exit = $launch.exit; wallMs = $launch.wallMs
        helperMs = $launch.helperMs; launched = $launched; pid = $launchedPid
        text = $(if ($launch.text.Length -gt 800) { $launch.text.Substring(0, 800) } else { $launch.text }) }
    if (-not $launched) {
        Write-Log 'ERROR: could not prove we own the CAD instance (launched:false). Refusing to continue'
        Write-Log '       (attach-only means the instance belongs to someone else: do not finish -Quit it)'
        Write-Log ('       our pid, if any: ' + $launchedPid)
        [System.IO.File]::WriteAllText((Join-Path $outFull 'summary.json'),
            ($result | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        exit 2
    }
    Write-Log ('  owned instance pid ' + $launchedPid)

    # one scratch drawing for every write task: opening a NEW drawing per repetition piled up
    # 20+ documents and once wedged ZWCAD's COM layer (section call hung > 6 min). The write
    # tasks open this single drawing by name and undo back to empty in their teardown.
    $scratch = Get-PropSafe $launch.parsed 'result.doc.name'
    if (-not $scratch) {
        $mk = Invoke-BenchCommand (@('open', '-New') + $productArgs + @('-TimeoutSec', '180')) 'open -New (scratch)' 200
        $scratch = Get-PropSafe $mk.parsed 'result.doc.name'
    }
    $script:scratchDoc = $scratch
    Write-Log ('  scratch drawing: ' + $script:scratchDoc)

    for ($w = 1; $w -le $Warmup; $w++) {
        $wu = Invoke-BenchCommand (@('doctor') + $productArgs + @('-TimeoutSec', '120')) ('warmup ' + $w) 150
        Write-Log ('  warmup doctor helperMs=' + $wu.helperMs)
    }

    if (-not $NoMicro) {
        Write-Log ('--- micro-bench: status x' + $MicroRepeat)
        $micro = @()
        for ($i = 1; $i -le $MicroRepeat; $i++) {
            $m = Invoke-BenchCommand (@('status') + $productArgs + @('-TimeoutSec', '120')) ('status ' + $i) 150
            $m2 = [ordered]@{ wallMs = $m.wallMs; helperMs = $m.helperMs; exit = $m.exit }
            if ($m.parsed -and $m.parsed.wallMs -ne $null) { $m2.reportedWrapperMs = [double]$m.parsed.wallMs }
            $micro += $m2
        }
        $walls = @($micro | ForEach-Object { $_.wallMs })
        $helpers = @($micro | Where-Object { $_.helperMs -ne $null } | ForEach-Object { $_.helperMs })
        $floor = @()
        foreach ($m in $micro) { if ($m.helperMs -ne $null) { $floor += ([double]$m.wallMs - [double]$m.helperMs) } }
        $result.micro = [ordered]@{
            runs = $micro
            avgWallMs = Round1 (($walls | Measure-Object -Average).Average)
            minWallMs = Round1 (($walls | Measure-Object -Minimum).Minimum)
            medianWallMs = Round1 (Median $walls)
            avgHelperMs = Round1 (($helpers | Measure-Object -Average).Average)
            medianFloorMs = Round1 (Median $floor)
        }
        Write-Log ('  status avg=' + $result.micro.avgWallMs + ' ms  min=' + $result.micro.minWallMs +
                   ' ms  helper avg=' + $result.micro.avgHelperMs + ' ms  floor(median wall-helper)=' + $result.micro.medianFloorMs + ' ms')
    }

    # ---- phase B: the task suite
    Write-Log '--- phase B: tasks'
    foreach ($t in $mainTasks) {
        $taskRepeat = $Repeat
        if ($t.repeat -ne $null) { $taskRepeat = [int]$t.repeat }
        Write-Log ('== ' + $t.id + ' (' + $t.category + ') repeat=' + $taskRepeat)
        $reps = @()
        for ($i = 1; $i -le $taskRepeat; $i++) {
            $r = Invoke-TaskRep $t $i
            $reps += $r
            $extra = ''
            if ($t.repeatNote) { $extra = ' [' + $t.repeatNote + ']' }
            Write-Log ('  -> ' + $r.kind + ' commands=' + $r.commands + ' wall=' + [int]$r.wallMs +
                       ' ms  bytes=' + $r.bytes + '  procsAfter=' + $r.procsAfter)
            foreach ($why in $r.reasons) { Write-Log ('     reason: ' + $why) }
            if ($extra) { Write-Log ('     note:' + $extra) }
        }
        $summaryT = New-TaskSummary $t $reps
        $result.tasks += $summaryT
        if ($summaryT.kind -eq 'failure') {
            $result.failures += [ordered]@{ id = $t.id; category = $t.category; kind = $summaryT.kind
                reasons = @($summaryT.reasons | Select-Object -First 5) }
        } elseif ($summaryT.kind -eq 'product_bug') {
            $result.productBugs += [ordered]@{ id = $t.id; category = $t.category; kind = $summaryT.kind
                reasons = @($summaryT.reasons | Select-Object -First 5) }
        }
        # a task that leaves extra instances of OUR product behind is a stability finding
        if ($summaryT.procsAfter -gt 1) {
            $result.hygiene['maxTaskProcesses'] = $summaryT.procsAfter
        }
    }

    # ---- finish our own instance
    Write-Log '--- finish (quit our own instance)'
    $fin = Invoke-BenchCommand (@('finish', '-Quit') + $productArgs + @('-TimeoutSec', '180')) 'finish -Quit' 240
    # keep the raw finish response: it carries processesAfter/forcedKill/warnings that the
    # acceptance report needs (core-cad's ACAD quit fix is asserted from these fields)
    try { [System.IO.File]::WriteAllText((Join-Path $outFull 'finish.json'), $fin.text, (New-Object System.Text.UTF8Encoding($false))) } catch { }
    Start-Sleep -Seconds 2
    $leftAll = @(Get-CadProcesses)
    $leftOwn = @(Get-TargetProductProcesses)
    $foreign = @($leftAll | Where-Object { $leftOwn.Id -notcontains $_.Id })
    $result.hygiene = [ordered]@{
        finishExit = $fin.exit
        finishWallMs = $fin.wallMs
        processesAfterFinish = $leftOwn.Count
        pidsAfterFinish = @($leftAll | ForEach-Object { $_.Id })
        foreignProcesses = @($foreign | ForEach-Object { [ordered]@{ pid = $_.Id; name = $_.ProcessName } })
    }
    Write-Log ('  target-product processes after finish: ' + $leftOwn.Count +
               ' (other CAD processes left alone: ' + $foreign.Count + ')')
    if (-not $KeepInstance -and $leftOwn.Count -gt 0) {
        if (@($leftOwn | Where-Object { $_.Id -eq $launchedPid }).Count -gt 0) {
            Write-Log ('  our pid ' + $launchedPid + ' did not exit; stopping it')
            try { Stop-Process -Id $launchedPid -Force } catch { }
            $result.hygiene['forcedStopPid'] = $launchedPid
            Start-Sleep -Seconds 2
            $result.hygiene.processesAfterFinish = @(Get-TargetProductProcesses).Count
        }
    }
}

$startAll.Stop()
$result.durationMs = $startAll.ElapsedMilliseconds
$result.endedAt = (Get-Date).ToString('o')

# ------------------------------------------------------------------ aggregate
$catAgg = [ordered]@{}
$cmdAgg = [ordered]@{}
$taskSummaries = @()
foreach ($t in $result.tasks) {
    if ($t.kind -eq 'setup_unavailable') { continue }
    $cat = [string]$t.category
    if (-not $catAgg.Contains($cat)) {
        $catAgg[$cat] = [ordered]@{ tasks = 0; ok = 0; gap = 0; bug = 0; failed = 0; commands = 0
            wallMs = 0.0; bytes = 0; undos = 0; undoOk = 0 }
    }
    $catAgg[$cat].tasks++
    $catAgg[$cat].commands += [int]$t.median.commands
    $catAgg[$cat].wallMs += [double]$t.median.wallMs
    $catAgg[$cat].bytes += [int]$t.median.bytes
    if ($t.kind -eq 'success') { $catAgg[$cat].ok++ }
    elseif ($t.kind -eq 'capability_gap') { $catAgg[$cat].gap++ }
    elseif ($t.kind -eq 'product_bug') { $catAgg[$cat].bug++ }
    else { $catAgg[$cat].failed++ }
    if ($t.undo -ne $null) {
        $catAgg[$cat].undos++
        if ($t.undo.ok) { $catAgg[$cat].undoOk++ }
    }
    foreach ($rep in $t.reps) {
        foreach ($s in $rep.steps) {
            $key = [string]$s.label
            if (-not $cmdAgg.Contains($key)) {
                $cmdAgg[$key] = [ordered]@{ count = 0; wallMs = 0.0; helperMs = 0.0; bytes = 0; helperSamples = 0 }
            }
            $cmdAgg[$key].count++
            $cmdAgg[$key].wallMs += [double]$s.wallMs
            $cmdAgg[$key].bytes += [int]$s.bytes
            if ($s.helperMs -ne $null) { $cmdAgg[$key].helperMs += [double]$s.helperMs; $cmdAgg[$key].helperSamples++ }
        }
    }
}
foreach ($cat in @($catAgg.Keys)) {
    $c = $catAgg[$cat]
    if ($c.tasks -gt 0) {
        $c.avgCommands = Round1 ($c.commands / $c.tasks)
        $c.medianWallMsPerTask = Round1 ($c.wallMs / $c.tasks)
        $c.avgBytesPerTask = [math]::Round($c.bytes / $c.tasks, 0)
    }
}
$cmdRows = @()
foreach ($key in @($cmdAgg.Keys)) {
    $c = $cmdAgg[$key]
    $avgHelper = $null
    if ($c.helperSamples -gt 0) { $avgHelper = Round1 ($c.helperMs / $c.helperSamples) }
    $cmdRows += [ordered]@{
        label = $key; count = $c.count
        avgWallMs = Round1 ($c.wallMs / $c.count)
        avgHelperMs = $avgHelper
        avgFloorMs = $(if ($avgHelper -ne $null) { Round1 ($c.wallMs / $c.count - $avgHelper) } else { $null })
        avgBytes = [math]::Round($c.bytes / $c.count, 0)
        totalWallMs = [math]::Round($c.wallMs, 0)
    }
}
$cmdRows = @($cmdRows | Sort-Object -Property totalWallMs -Descending)
$result.aggregate = [ordered]@{ byCategory = $catAgg; byCommand = $cmdRows }

Write-Log '--- summary'
foreach ($t in $result.tasks) {
    Write-Log ('  ' + ([string]$t.id).PadRight(26) + [string]$t.kind.PadRight(16) +
               ' cmds=' + [string]$t.median.commands + ' medianWall=' + [int]$t.median.wallMs + ' ms')
}

$summaryPath = Join-Path $outFull 'summary.json'
[System.IO.File]::WriteAllText($summaryPath, ($result | ConvertTo-Json -Depth 12),
    (New-Object System.Text.UTF8Encoding($false)))
Write-Log ('summary: ' + $summaryPath)

if (Test-Path -LiteralPath $summarizePy) {
    try {
        $mdText = (& $pyExe $summarizePy --summary $summaryPath --tasks $tasksPath --out (Join-Path $outFull 'summary.md') 2>&1) -join "`n"
        if ($mdText.Trim()) { Write-Log ('summarize: ' + ($mdText.Trim() -split "`n")[-1]) }
    } catch {
        Write-Log ('WARNING: summarize.py failed: ' + $_.Exception.Message)
    }
}

if ($result.hygiene.processesAfterFinish -gt 0) { exit 3 }
exit 0
