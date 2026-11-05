# lib.ps1 - shared helpers for the M3-E3 chaos / concurrency harness.
# PURE ASCII + BOM (PS 5.1 parses non-BOM scripts with the ANSI code page).
#
# Everything here is fault injection + measurement only: no helper changes, no product code.
# Discipline baked in:
#   * only ever drive ZWCAD.Application.2025 (our product); Classic/ACAD belong to other agents;
#   * the CAD instance we drive is launched by this harness and finished by it (own pid only);
#   * every command is bounded (kill + explicit timeout) so "let it hang" is never a test result.

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Get-ChaosRoot {
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
    return (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $here)))
}

$script:ChaosRoot = Get-ChaosRoot
$script:ClientExe = Join-Path $script:ChaosRoot 'bin\CadClient.exe'
$script:CadPs1 = Join-Path $script:ChaosRoot 'scripts\cad.ps1'
$script:Fixtures = Join-Path $script:ChaosRoot 'tests\bench\fixtures'
$script:PyExe = 'python'
if (-not (Test-Path -LiteralPath $script:PyExe)) { $script:PyExe = 'python' }

# ---------------------------------------------------------------- quiet-window gate
# Measured fact: real user load dominates our latency noise (user editing Excel in WPS, a stray
# helper at 94 s CPU, another agent's CAD/daemon => ±100-200 ms). A latency run outside a quiet
# window is not evidence, so scripts must refuse (exit 4, "QUIET_WINDOW_REQUIRED").
function Get-QuietWindowState([double]$MaxCpuPercent = 25, [double]$MinFreeMemGB = 2,
                              [int]$OwnPid = 0, [string[]]$IgnoreImages = @()) {
    $reasons = @()
    $cad = @(Get-Process ZWCAD, acad -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $OwnPid })
    $helpers = @(Get-Process CadHelper, ZwHelp -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $OwnPid })
    $officeNames = @('wps', 'et', 'WINWORD', 'EXCEL', 'wpp') + @($IgnoreImages)
    $office = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $officeNames -contains $_.ProcessName })
    $cpu = $null
    try {
        $sample = Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
        $cpu = [math]::Round([double]$sample.CounterSamples[0].CookedValue, 1)
    } catch {
        try {
            $cpu = [math]::Round((Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average, 1)
        } catch { $cpu = $null }
    }
    $freeGB = $null
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $freeGB = [math]::Round([double]$os.FreePhysicalMemory / 1MB, 1)
    } catch { }
    if ($cad.Count -gt 0) { $reasons += ('CAD instance running: ' + (($cad | ForEach-Object { $_.ProcessName + '#' + $_.Id }) -join ',')) }
    if ($helpers.Count -gt 0) { $reasons += ('helper/daemon running: ' + (($helpers | ForEach-Object { $_.ProcessName + '#' + $_.Id }) -join ',')) }
    if ($office.Count -gt 0) { $reasons += ('office app running: ' + (($office | ForEach-Object { $_.ProcessName + '#' + $_.Id }) -join ',')) }
    if ($null -ne $cpu -and $cpu -ge $MaxCpuPercent) { $reasons += ('CPU ' + $cpu + '% >= ' + $MaxCpuPercent + '%') }
    if ($null -ne $freeGB -and $freeGB -lt $MinFreeMemGB) { $reasons += ('free RAM ' + $freeGB + ' GB < ' + $MinFreeMemGB + ' GB') }
    return [ordered]@{
        at = (Get-Date).ToString('o')
        ok = ($reasons.Count -eq 0)
        verdict = $(if ($reasons.Count -eq 0) { 'QUIET' } else { 'QUIET_WINDOW_REQUIRED' })
        reasons = $reasons
        metrics = [ordered]@{ cpuPercent = $cpu; freeMemGB = $freeGB
            cadCount = $cad.Count; helperCount = $helpers.Count; officeCount = $office.Count }
    }
}

function Assert-QuietWindow([double]$MaxCpuPercent = 25, [double]$MinFreeMemGB = 2) {
    $st = Get-QuietWindowState -MaxCpuPercent $MaxCpuPercent -MinFreeMemGB $MinFreeMemGB
    if (-not $st.ok) {
        [Console]::Out.WriteLine(($st | ConvertTo-Json -Depth 6 -Compress))
        [Console]::Out.WriteLine('QUIET_WINDOW_REQUIRED - refusing to measure latency under real-user/noise load')
        exit 4
    }
    return $st
}

function Write-Chaos([string]$text) {
    [Console]::Out.WriteLine(('[' + (Get-Date).ToString('HH:mm:ss') + '] ' + $text))
}

function Get-PropSafe($obj, [string]$path) {
    $cur = $obj
    foreach ($part in $path.Split('.')) {
        if ($null -eq $cur) { return $null }
        try { $cur = $cur.$part } catch { return $null }
    }
    return $cur
}

function ConvertFrom-JsonText([string]$text) {
    if (-not $text) { return $null }
    $t = $text.Trim()
    try { return ($t | ConvertFrom-Json) } catch { }
    $start = $t.IndexOf('{')
    $end = $t.LastIndexOf('}')
    if ($start -ge 0 -and $end -gt $start) {
        try { return ($t.Substring($start, $end - $start + 1) | ConvertFrom-Json) } catch { }
    }
    return $null
}

# Bounded process invocation (same pattern as run_bench.ps1: reliable ExitCode + async readers).
function Invoke-Bounded([string]$exe, [string[]]$argv, [int]$timeoutSec) {
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
    $psi.WorkingDirectory = $script:ChaosRoot
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try { $null = $proc.Start() } catch {
        $watch.Stop()
        return [pscustomobject]@{ exit = -1; wallMs = [double]$watch.ElapsedMilliseconds
            text = ('START_FAILED: ' + $_.Exception.Message); watchdog = $false; parsed = $null }
    }
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit(($timeoutSec + 30) * 1000)
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
    $exit = 0
    try { $exit = [int]$proc.ExitCode } catch { $exit = -1 }
    $text = $out
    if ($watchdog) { $exit = -9; $text = 'WATCHDOG_TIMEOUT' }
    if ($err.Trim()) { $text = $text + "`n[stderr] " + $err }
    return [pscustomobject]@{ exit = $exit; wallMs = [double]$watch.ElapsedMilliseconds
        text = $text; watchdog = $watchdog; parsed = (ConvertFrom-JsonText $out) }
}

# One CAD command through the E1 client (fallback: cad.ps1 -File). Returns exit/wall/parsed.
function Invoke-Cad([string[]]$argv, [int]$timeoutSec, [string]$progId) {
    $full = @($argv)
    if ($progId) { $full = @($argv[0], '-ProgId', $progId) + @($argv | Select-Object -Skip 1) }
    if (Test-Path -LiteralPath $script:ClientExe) {
        return (Invoke-Bounded $script:ClientExe $full $timeoutSec)
    }
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $psArgv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:CadPs1) + $full
    return (Invoke-Bounded $psExe $psArgv $timeoutSec)
}

# Start a client command WITHOUT waiting (concurrency tests need real overlap).
function Start-CadAsync([string[]]$argv, [string]$progId, [string]$stdoutPath, [string]$stderrPath) {
    $full = @($argv)
    if ($progId) { $full = @($argv[0], '-ProgId', $progId) + @($argv | Select-Object -Skip 1) }
    if (-not (Test-Path -LiteralPath $script:ClientExe)) { throw 'CadClient.exe not found (concurrency needs the client entry)' }
    $argLine = (($full | ForEach-Object {
        $a = [string]$_
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }) -join ' ')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ClientExe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $script:ChaosRoot
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $proc.Start()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ proc = $proc; outTask = $outTask; errTask = $errTask; watch = $watch
        argv = $full; stdoutPath = $stdoutPath; stderrPath = $stderrPath }
}

function Wait-CadAsync($job, [int]$timeoutSec) {
    $exited = $job.proc.WaitForExit(($timeoutSec + 30) * 1000)
    $watchdog = $false
    if (-not $exited) { $watchdog = $true; try { $job.proc.Kill() } catch { } ; try { $null = $job.proc.WaitForExit(10000) } catch { } }
    $job.proc.WaitForExit()
    $job.watch.Stop()
    $out = ''
    $err = ''
    try { $out = [string]$job.outTask.Result } catch { }
    try { $err = [string]$job.errTask.Result } catch { }
    if ($job.stdoutPath) { try { [System.IO.File]::WriteAllText($job.stdoutPath, $out, (New-Object System.Text.UTF8Encoding($false))) } catch { } }
    if ($job.stderrPath) { try { [System.IO.File]::WriteAllText($job.stderrPath, $err, (New-Object System.Text.UTF8Encoding($false))) } catch { } }
    $exit = 0
    try { $exit = [int]$job.proc.ExitCode } catch { $exit = -1 }
    return [pscustomobject]@{ exit = $exit; wallMs = [double]$job.watch.ElapsedMilliseconds
        text = $out; stderr = $err; watchdog = $watchdog; parsed = (ConvertFrom-JsonText $out)
        argv = $job.argv }
}

function Get-TargetProcs([string]$progId) {
    $hits = @()
    if ($progId -match '2025') {
        $hits = @(Get-Process ZWCAD -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -match '2025' } catch { $false } })
    } elseif ($progId -match '\.16') {
        $hits = @(Get-Process ZWCAD -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -match 'Classic' } catch { $false } })
    } else {
        $hits = @(Get-Process acad -ErrorAction SilentlyContinue)
    }
    return @($hits)
}

# The E1 client refuses to run when bin\cache\<sha>\CadHelper.exe is missing for the CURRENT
# helper sources (it does not compile for live code). Chaos must therefore warm the helper
# compile cache through cad.ps1 once before any client call.
function Initialize-Helper([string]$progId, [int]$timeoutSec) {
    if (-not (Test-Path -LiteralPath $script:CadPs1)) { return $null }
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:CadPs1,
              'status', '-ProgId', $progId, '-TimeoutSec', '90')
    $r = Invoke-Bounded $psExe $argv $timeoutSec
    Write-Chaos ('helper warmup: exit=' + $r.exit + ' ' + [int]$r.wallMs + ' ms')
    return $r
}


# ---------------------------------------------------------------- frozen product snapshot
# Copy scripts\cad.ps1 + scripts\helper\*.cs + bin\CadClient.exe into a private mirror, then
# point all CAD calls at the mirror. The helper/daemon then live under that mirror with their own
# hash: a running teammate edit cannot change our hash mid-run, and C5's daemon kill is provably
# our own process.
function New-FrozenProduct([string]$baseDir) {
    $stamp = (Get-Date).ToString('HHmmss')
    $frozen = Join-Path $baseDir ('frozen-' + $stamp)
    $fzScripts = Join-Path $frozen 'scripts'
    $fzHelper = Join-Path $fzScripts 'helper'
    $fzBin = Join-Path $frozen 'bin'
    New-Item -ItemType Directory -Force -Path $fzHelper | Out-Null
    New-Item -ItemType Directory -Force -Path $fzBin | Out-Null
    Copy-Item -LiteralPath $script:CadPs1 -Destination (Join-Path $fzScripts 'cad.ps1') -Force
    Copy-Item -Path (Join-Path (Join-Path $script:ChaosRoot 'scripts') 'helper\*.cs') -Destination $fzHelper -Force
    $liveClient = Join-Path $script:ChaosRoot 'bin\CadClient.exe'
    if (Test-Path -LiteralPath $liveClient) { Copy-Item -LiteralPath $liveClient -Destination (Join-Path $fzBin 'CadClient.exe') -Force }

    $hashes = [ordered]@{}
    $files = @()
    if (Test-Path -LiteralPath (Join-Path $fzScripts 'cad.ps1')) { $files += (Join-Path $fzScripts 'cad.ps1') }
    $files += @(Get-ChildItem -LiteralPath $fzHelper -Filter *.cs -File | ForEach-Object { $_.FullName })
    if (Test-Path -LiteralPath (Join-Path $fzBin 'CadClient.exe')) { $files += (Join-Path $fzBin 'CadClient.exe') }
    foreach ($f in $files) {
        $rel = $f.Substring($frozen.Length + 1)
        $hashes[$rel] = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
    }
    $script:CadPs1 = Join-Path $fzScripts 'cad.ps1'
    $script:ClientExe = Join-Path $fzBin 'CadClient.exe'
    Write-Chaos ('frozen product snapshot: ' + $frozen)
    return [pscustomobject]@{ root = $frozen; hashes = $hashes }
}

# Other agents may briefly own an instance of the same product; poll until the window is free.
function Wait-TargetFree([string]$progId, [int]$timeoutSec = 180) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $slept = 0
    while ((Get-Date) -lt $deadline) {
        $procs = @(Get-TargetProcs $progId)
        if ($procs.Count -eq 0) {
            if ($slept -gt 0) { Write-Chaos ('target window free after ' + $slept + ' s') }
            return $true
        }
        Start-Sleep -Seconds 5
        $slept += 5
    }
    return (@(Get-TargetProcs $progId).Count -eq 0)
}

# Launch our own instance (refuses when the product already has one: -Launch would attach).

function Start-OwnCad([string]$progId, [int]$timeoutSec) {
    $null = Wait-TargetFree $progId 180
    $existing = @(Get-TargetProcs $progId)
    if ($existing.Count -gt 0) {
        throw ('refusing: a ' + $progId + ' instance is already running (pid ' +
               (($existing | ForEach-Object { $_.Id }) -join ',') + '); do not touch other agents'' instances')
    }
    # make sure the client can find a compiled helper for the CURRENT sources
    $null = Initialize-Helper $progId 120
    $r = Invoke-Cad @('open', '-New', '-Launch') $timeoutSec $progId
    $launched = $false
    $cadPid = $null
    if ($r.parsed) {
        foreach ($p in @('result.launched', 'result.app.launched', 'launched', 'app.launched')) {
            if ((Get-PropSafe $r.parsed $p) -eq $true) { $launched = $true; break }
        }
        foreach ($p in @('result.app.pid', 'app.pid', 'result.pid')) {
            $v = Get-PropSafe $r.parsed $p
            if ($v) { $cadPid = [int]$v; break }
        }
    }
    if (-not $launched) { throw ('could not prove we own the CAD instance: ' + $r.text) }
    return [pscustomobject]@{ pid = $cadPid; wallMs = $r.wallMs; response = $r.parsed }
}

function Stop-OwnCad([string]$progId, [int]$cadPid, [int]$timeoutSec) {
    $fin = Invoke-Cad @('finish', '-Quit') $timeoutSec $progId
    Start-Sleep -Seconds 1
    $left = @(Get-TargetProcs $progId)
    $forced = $false
    if ($left.Count -gt 0 -and $cadPid) {
        $ours = @($left | Where-Object { $_.Id -eq $cadPid })
        if ($ours.Count -gt 0) { try { Stop-Process -Id $cadPid -Force; $forced = $true } catch { } }
    }
    Start-Sleep -Seconds 1
    $left = @(Get-TargetProcs $progId)
    return [pscustomobject]@{ finishExit = $fin.exit; finishWallMs = $fin.wallMs
        remaining = $left.Count; forcedStop = $forced; response = $fin.parsed }
}

function New-WorkDrawing([string]$fixture, [string]$outDir, [string]$tag) {
    $src = Join-Path $script:Fixtures $fixture
    if (-not (Test-Path -LiteralPath $src)) { throw ('fixture not found: ' + $src) }
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $dst = Join-Path $outDir ('work-' + $tag + '-' + (Get-Date).ToString('HHmmss') + '.dxf')
    Copy-Item -LiteralPath $src -Destination $dst -Force
    return $dst
}

function Get-DocRead([string]$progId, [string]$doc, [int]$timeoutSec) {
    $r = Invoke-Cad @('read', '-Doc', $doc, '-Mode', 'geom', '-Max', '20000') $timeoutSec $progId
    $entities = @()
    if ($r.parsed) { $entities = @(Get-PropSafe $r.parsed 'result.entities') }
    return [pscustomobject]@{ exit = $r.exit; wallMs = $r.wallMs
        entities = @($entities | Where-Object { $_ }); count = @($entities | Where-Object { $_ }).Count
        parsed = $r.parsed; text = $r.text }
}

function Get-TextsOf($entities) {
    return @($entities | Where-Object { $_.text } | ForEach-Object { [string]$_.text })
}

function Get-Fingerprint($entities) {
    $map = @{}
    foreach ($e in $entities) {
        $h = [string](Get-PropSafe $e 'handle')
        if (-not $h) { continue }
        $t = ''
        if ($null -ne (Get-PropSafe $e 'text')) { $t = [string](Get-PropSafe $e 'text') }
        $hgt = Get-PropSafe $e 'height'
        $lyr = ''
        if ($null -ne (Get-PropSafe $e 'layer')) { $lyr = [string](Get-PropSafe $e 'layer') }
        $map[$h] = ($lyr + '|' + $t + '|' + [string]$hgt)
    }
    return $map
}

function Compare-Fingerprint($before, $after) {
    $changed = @()
    foreach ($k in $before.Keys) {
        if (-not $after.ContainsKey($k)) { $changed += ($k + ':missing'); continue }
        if ($before[$k] -ne $after[$k]) { $changed += $k }
    }
    foreach ($k in $after.Keys) { if (-not $before.ContainsKey($k)) { $changed += ($k + ':added') } }
    return @($changed)
}

function Get-Percentile([double[]]$vals, [double]$p) {
    if ($vals.Count -eq 0) { return 0 }
    $sorted = @($vals | Sort-Object)
    $idx = [int][math]::Ceiling($p * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return [double]$sorted[$idx]
}

function Write-JsonFile([string]$path, $obj) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------- COM fault injection
# We do NOT modify the helper: faults are injected directly through CAD's own COM API
# (SendCommand / Document.Close) against the instance this harness launched.
function Get-CadApp([string]$progId) {
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject($progId) } catch { return $null }
}

function Find-CadDoc($app, [string]$path) {
    if ($null -eq $app) { return $null }
    $normalized = ([string]$path).ToLowerInvariant()
    try {
        $count = [int]$app.Documents.Count
        for ($i = 0; $i -lt $count; $i++) {
            try {
                $d = $app.Documents.Item($i)
            } catch {
                try { $d = $app.Documents.Item($i + 1) } catch { continue }
            }
            if ($null -eq $d) { continue }
            $fn = ''
            try { $fn = [string]$d.FullName } catch { }
            if ($fn.ToLowerInvariant() -eq $normalized) { return $d }
        }
    } catch { }
    try {
        $ad = $app.ActiveDocument
        if ($null -ne $ad) {
            $fn = ''
            try { $fn = [string]$ad.FullName } catch { }
            if ($fn.ToLowerInvariant() -eq $normalized) { return $ad }
        }
    } catch { }
    return $null
}

function Send-CadCommand($doc, [string]$command) {
    if ($null -eq $doc) { throw 'no document for SendCommand' }
    $doc.SendCommand($command)
}

function Send-CadEscape($doc) {
    if ($null -eq $doc) { return }
    try { $doc.SendCommand([string][char]27) } catch { }
    try { $doc.SendCommand('') } catch { }
    try { $doc.SendCommand([string][char]3) } catch { }
}

function Close-CadDoc($doc) {
    if ($null -eq $doc) { return }
    $doc.Close($false)
}

# ---------------------------------------------------------------- async injector helpers
function Start-InjectAsync([string]$progId, [string]$work, [string]$action, [string]$command, [int]$holdSec = 0) {
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'inject.ps1'),
              '-ProgId', $progId, '-Action', $action, '-Doc', $work)
    if ($command) { $argv += @('-Command', $command) }
    if ($holdSec -gt 0) { $argv += @('-HoldSec', [string]$holdSec) }
    $argLine = (($argv | ForEach-Object {
        $a = [string]$_
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }) -join ' ')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $psExe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $script:ChaosRoot
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    return $proc
}

function Stop-InjectAsync($proc) {
    if ($null -eq $proc) { return }
    try { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } } catch { }
}

function Get-CmdActive([string]$progId, [string]$work) {
    $poll = Invoke-Inject $progId $work 'poll' '' 20
    return [int](Get-PropSafe $poll.parsed 'cmdActive')
}

function Get-LastPrompt([string]$progId, [string]$work) {
    $poll = Invoke-Inject $progId $work 'poll' '' 20
    return [string](Get-PropSafe $poll.parsed 'lastPrompt')
}

function New-TextItems([string]$path, [string]$tag, [int]$count, [string]$layer) {
    $items = @()
    for ($i = 1; $i -le $count; $i++) {
        $items += [ordered]@{
            op = 'add'; type = 'text'; layer = $layer
            geom = [ordered]@{ text = ($tag + '-' + $i); insert = @((600.0 + $i * 2.0), 600.0); height = 2.5 }
            props = @{}
        }
    }
    Write-JsonFile $path ([ordered]@{ items = $items })
    return $path
}

function Invoke-Inject([string]$progId, [string]$work, [string]$action, [string]$command, [int]$hardTimeoutSec) {
    if ($hardTimeoutSec -le 0) { $hardTimeoutSec = 20 }
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'inject.ps1'),
              '-ProgId', $progId, '-Action', $action, '-Doc', $work)
    if ($command) { $argv += @('-Command', $command) }
    $argLine = (($argv | ForEach-Object {
        $a = [string]$_
        if ($a -match '[\\s"]') { '"' + ($a -replace '"', '\\"') + '"' } else { $a }
    }) -join ' ')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $psExe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $script:ChaosRoot
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $proc.Start()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $null = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit($hardTimeoutSec * 1000)
    $killed = $false
    if (-not $exited) { $killed = $true; try { $proc.Kill() } catch { }; try { $null = $proc.WaitForExit(5000) } catch { } }
    $proc.WaitForExit()
    $watch.Stop()
    $out = ''
    try { $out = [string]$outTask.Result } catch { }
    $exit = 0
    try { $exit = [int]$proc.ExitCode } catch { $exit = -1 }
    return [pscustomobject]@{ exit = $exit; wallMs = [double]$watch.ElapsedMilliseconds
        killed = $killed; text = $out; parsed = (ConvertFrom-JsonText $out) }
}