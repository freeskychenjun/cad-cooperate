# zwc.ps1 - zwcad-cooperate command line wrapper (M1).
#
#   zwc.ps1 <doctor|status|open|info|read|section|undo|hint|finish> [flags...]
#
# Contract: stdout is ALWAYS exactly one JSON document (DESIGN-M1.md section 2). Exit codes:
#   0 = ok:true, 2 = ok:false, 3 = ok:false on a gate failure (verify rejected the work).
#
# Why this file is pure ASCII: PowerShell 5.1 parses a .ps1 with no BOM using the ANSI code page,
# so any Chinese literal in here would turn into mojibake and syntax errors. Every argument that
# can carry Chinese (drawing path, water-level labels, hint text) is therefore passed at runtime
# and travels to the helper inside a UTF-8 JSON file, never as a literal in this script.
#
# Why the helper is a compiled exe behind a hash cache: csc.exe costs 1-2 s, the helper itself
# costs ~40 ms for an attached instance. The cache key is "source count + sizes + newest mtime"
# (a real edit always changes one of them) so a normal command pays only a few file stats.
#
# Why Start-Process is used with redirected files and WaitForExit(ms) instead of -Wait: -Wait also
# waits for GRANDCHILDREN (the CAD process the helper may have started), which never returns, and a
# synchronous pipe read would block forever on a helper stuck inside a COM call. Redirecting stdout
# to a file + a bounded WaitForExit gives us a hard timeout with no pipe deadlock.

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$Command = $null
$Rest = @()
if ($args.Count -ge 1) {
    $Command = [string]$args[0]
    if ($args.Count -gt 1) { $Rest = @($args | Select-Object -Skip 1) }
}

# ---------------------------------------------------------------- M3-E1 fast path
# Every helper command is handed to bin\ZwcClient.exe: the client starts in ~60-100 ms and speaks
# the daemon pipe, while `powershell -File` alone already costs ~1.9 s. Keeping the whole legacy
# PowerShell pipeline in the way made this entry point 5-7 s per command even with a warm daemon
# (measured 2026-11-04: zwc.ps1 status 5437-7814 ms vs ZwcClient status 84-176 ms), i.e. it ate the
# entire daemon win. serve / --build-client are management commands and stay in this script (the
# client delegates those back here, so delegating them would loop).
$delegateRoot = $PSScriptRoot
if (-not $delegateRoot) { $delegateRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$delegateClient = Join-Path (Split-Path $delegateRoot -Parent) 'bin\ZwcClient.exe'
$delegateIsAdmin = ($Command -eq 'serve' -or $Command -eq '--build-client' -or $Command -eq 'buildclient')
if ($Command -and -not $delegateIsAdmin -and (Test-Path -LiteralPath $delegateClient)) {
    # only delegate when the helper binary for the CURRENT source hash exists; otherwise fall
    # through to the legacy path below, which compiles it first
    $dh = @(Get-ChildItem -LiteralPath (Join-Path $delegateRoot 'helper') -Filter *.cs -File)
    $ds = @($dh | ForEach-Object { $_.Length } | Sort-Object)
    $dn = ($dh | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum.Ticks
    $dkey = '' + $dh.Count + '|' + $dn + '|' + ($ds -join ',')
    $dshaGen = [System.Security.Cryptography.SHA256]::Create()
    $dsha = ([System.BitConverter]::ToString($dshaGen.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($dkey))) -replace '-', '').ToLowerInvariant().Substring(0, 12)
    $dhelper = Join-Path (Join-Path (Join-Path (Split-Path $delegateRoot -Parent) 'bin\cache') $dsha) 'ZwcHelper.exe'
    if (Test-Path -LiteralPath $dhelper) {
        & $delegateClient @args
        exit $LASTEXITCODE
    }
}

function Write-JsonError {
    param([string]$Code, [string]$Message, [string]$Hint)
    $o = [ordered]@{
        ok    = $false
        cmd   = $Command
        error = [ordered]@{ code = $Code; message = $Message; hint = $Hint }
    }
    [Console]::Out.Write(($o | ConvertTo-Json -Depth 6 -Compress))
    exit 2
}

# zwc.ps1 --build-client : compile bin\ZwcClient.exe only (bootstrap used by zwc.cmd).
if ($Command -eq '--build-client' -or $Command -eq 'buildclient') {
    function Get-CscPathEarly {
        foreach ($arch in @('Framework64', 'Framework')) {
            $cand = Join-Path $env:SystemRoot ('Microsoft.NET\' + $arch + '\v4.0.30319\csc.exe')
            if (Test-Path -LiteralPath $cand) { return $cand }
        }
        return $null
    }
    $sr = $PSScriptRoot
    if (-not $sr) { $sr = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $repo = Split-Path $sr -Parent
    $src = Join-Path $sr 'client\ZwcClient.cs'
    $bin = Join-Path $repo 'bin'
    $exePath = Join-Path $bin 'ZwcClient.exe'
    $hashPath = Join-Path $bin 'ZwcClient.sha'
    if (Test-Path -LiteralPath $src) {
        $fi = Get-Item -LiteralPath $src
        $key = '' + $fi.Length + '|' + $fi.LastWriteTimeUtc.Ticks
        $stale = $true
        if ((Test-Path -LiteralPath $exePath) -and (Test-Path -LiteralPath $hashPath)) {
            if (((Get-Content -LiteralPath $hashPath -Raw).Trim()) -eq $key) { $stale = $false }
        }
        if ($stale) {
            $csc = Get-CscPathEarly
            if ($csc) {
                New-Item -ItemType Directory -Force -Path $bin | Out-Null
                $tmp = $exePath + '.build-' + $PID + '.exe'
                $log = (& $csc -nologo -optimize+ -target:exe -platform:anycpu -codepage:65001 ('-out:' + $tmp) -r:System.dll -r:System.Core.dll $src 2>&1 | Out-String)
                if (Test-Path -LiteralPath $tmp) { Move-Item -LiteralPath $tmp -Destination $exePath -Force; Set-Content -LiteralPath $hashPath -Value $key -Encoding ASCII }
                else { [Console]::Error.Write($log); exit 2 }
            }
        }
    }
    exit 0
}
$KnownCommands = @('doctor', 'status', 'open', 'info', 'read', 'section', 'undo', 'hint', 'finish', 'serve', 'entity', 'query', 'snapshot', 'diff', 'show')
if (-not $Command) {
    Write-JsonError 'BAD_ARGS' 'usage: zwc.ps1 <doctor|status|open|info|read|section|undo|hint|finish> [flags...]' 'see DESIGN-M1.md section 3 for the flag list'
}
if ($KnownCommands -notcontains $Command) {
    Write-JsonError 'BAD_ARGS' ("unknown command: " + $Command) 'expected doctor|status|open|info|read|section|undo|hint|finish|serve'
}
# ---------------------------------------------------------------- repo layout + daemon lifecycle (M3-E1)
function Get-CscPath {
    foreach ($arch in @('Framework64', 'Framework')) {
        $cand = Join-Path $env:SystemRoot ('Microsoft.NET\' + $arch + '\v4.0.30319\csc.exe')
        if (Test-Path -LiteralPath $cand) { return $cand }
    }
    return $null
}

function Get-RepoLayout {
    $scriptRoot = $PSScriptRoot
    if (-not $scriptRoot) { $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $helperDir = Join-Path $scriptRoot 'helper'
    $sources = @(Get-ChildItem -LiteralPath $helperDir -Filter *.cs -File | Sort-Object Name)
    $sizes = @($sources | ForEach-Object { $_.Length } | Sort-Object)
    $newest = ($sources | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum.Ticks
    $key = '' + $sources.Count + '|' + $newest + '|' + ($sizes -join ',')
    $shaGen = [System.Security.Cryptography.SHA256]::Create()
    $sha = ([System.BitConverter]::ToString($shaGen.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key))) -replace '-', '').ToLowerInvariant().Substring(0, 12)
    $repoRoot = Split-Path $scriptRoot -Parent
    $cacheDir = Join-Path $repoRoot ('bin\cache\' + $sha)
    return [ordered]@{
        root       = $repoRoot
        scriptRoot = $scriptRoot
        helperDir  = $helperDir
        sources    = $sources
        sha        = $sha
        cacheDir   = $cacheDir
        exe        = (Join-Path $cacheDir 'ZwcHelper.exe')
        pipe       = ('zwc-' + $sha)
        csc        = (Get-CscPath)
    }
}

function Build-HelperExe($layout) {
    if (Test-Path -LiteralPath $layout.exe) { return $layout.exe }
    if (-not $layout.csc) { return $null }
    New-Item -ItemType Directory -Force -Path $layout.cacheDir | Out-Null
    $tmpExe = Join-Path $layout.cacheDir ('build-' + $PID + '.exe')
    $cscArgs = @('-nologo', '-optimize+', '-target:exe', '-platform:anycpu', '-codepage:65001', ('-out:' + $tmpExe), '-r:System.dll', '-r:System.Core.dll', '-r:System.Management.dll')
    foreach ($s in $layout.sources) { $cscArgs += $s.FullName }
    $log = (& $layout.csc @cscArgs 2>&1 | Out-String)
    if (-not (Test-Path -LiteralPath $tmpExe)) { throw ('helper compile failed: ' + $log.Trim()) }
    Move-Item -LiteralPath $tmpExe -Destination $layout.exe -Force
    return $layout.exe
}

function Build-ClientExe {
    $scriptRoot = $PSScriptRoot
    if (-not $scriptRoot) { $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $repoRoot = Split-Path $scriptRoot -Parent
    $src = Join-Path $scriptRoot 'client\ZwcClient.cs'
    if (-not (Test-Path -LiteralPath $src)) { return $null }
    $binDir = Join-Path $repoRoot 'bin'
    $exe = Join-Path $binDir 'ZwcClient.exe'
    $hashFile = Join-Path $binDir 'ZwcClient.sha'
    $fi = Get-Item -LiteralPath $src
    $key = '' + $fi.Length + '|' + $fi.LastWriteTimeUtc.Ticks
    if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $hashFile)) {
        $old = (Get-Content -LiteralPath $hashFile -Raw).Trim()
        if ($old -eq $key) { return $exe }
    }
    $csc = Get-CscPath
    if (-not $csc) { return $null }
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    $tmpExe = $exe + '.build-' + $PID + '.exe'
    $log = (& $csc -nologo -optimize+ -target:exe -platform:anycpu -codepage:65001 ('-out:' + $tmpExe) -r:System.dll -r:System.Core.dll $src 2>&1 | Out-String)
    if (-not (Test-Path -LiteralPath $tmpExe)) { throw ('client compile failed: ' + $log.Trim()) }
    Move-Item -LiteralPath $tmpExe -Destination $exe -Force
    Set-Content -LiteralPath $hashFile -Value $key -Encoding ASCII
    # NGen native image cuts client startup (~70 -> ~35 ms); best effort, never fatal.
    try {
        $ngen = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
        if (Test-Path -LiteralPath $ngen) { Start-Process -FilePath $ngen -ArgumentList @('install', $exe) -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue | Out-Null }
    } catch { }
    return $exe
}

function Send-PipeJson([string]$pipeName, [string]$json, [int]$connectMs, [int]$readMs) {
    $p = $null
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect($connectMs)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $p.Write([System.BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
        $p.Write($bytes, 0, $bytes.Length)
        $p.Flush()
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $hdr = New-Object byte[] 4
        $got = 0
        while ($got -lt 4) {
            $remain = $readMs - [int]$watch.ElapsedMilliseconds
            if ($remain -le 0) { return $null }
            $task = $p.ReadAsync($hdr, $got, 4 - $got)
            if (-not $task.Wait($remain)) { return $null }
            if ($task.Result -le 0) { return $null }
            $got += $task.Result
        }
        $len = [System.BitConverter]::ToInt32($hdr, 0)
        if ($len -le 0 -or $len -gt 33554432) { return $null }
        $buf = New-Object byte[] $len
        $got = 0
        while ($got -lt $len) {
            $remain = $readMs - [int]$watch.ElapsedMilliseconds
            if ($remain -le 0) { return $null }
            $task = $p.ReadAsync($buf, $got, $len - $got)
            if (-not $task.Wait($remain)) { return $null }
            if ($task.Result -le 0) { return $null }
            $got += $task.Result
        }
        return [System.Text.Encoding]::UTF8.GetString($buf)
    } catch { return $null } finally { if ($p) { try { $p.Dispose() } catch { } } }
}

# --build-client: bootstrap used by zwc.cmd when bin\ZwcClient.exe is missing
if ($Command -eq '--build-client' -or $Command -eq 'buildclient') {
    $exe = Build-ClientExe
    if ($exe) { exit 0 }
    exit 2
}

# serve -Action start|status|stop|reset
if ($Command -eq 'serve') {
    $action = 'status'
    for ($ai = 0; $ai -lt $Rest.Count - 1; $ai++) { if ($Rest[$ai] -eq '-Action') { $action = [string]$Rest[$ai + 1] } }
    $layout = Get-RepoLayout
    if ($action -eq 'start') {
        $exePath = Build-HelperExe $layout
        $running = ($null -ne (Send-PipeJson $layout.pipe '{"cmd":"ping"}' 300 1500))
        if (-not $running -and $exePath) {
            Start-Process -FilePath $exePath -ArgumentList @('--serve', $layout.pipe, '--idle-ms', '1200000') -WindowStyle Hidden | Out-Null
            for ($i = 0; $i -lt 40 -and -not $running; $i++) {
                Start-Sleep -Milliseconds 150
                $running = ($null -ne (Send-PipeJson $layout.pipe '{"cmd":"ping"}' 300 1500))
            }
        }
        $o = [ordered]@{ ok = $running; cmd = 'serve'; result = [ordered]@{ action = 'start'; running = $running; pipe = $layout.pipe; exe = $exePath } }
        [Console]::Out.Write(($o | ConvertTo-Json -Depth 6 -Compress)); exit $(if ($running) { 0 } else { 2 })
    }
    elseif ($action -eq 'recover') {
        # Drop session state, rebuild the daemon, then re-attach through the pipe. Never touches CAD.
        $statePath = Join-Path $env:TEMP 'zwcad-cooperate\state.json'
        $backup = $statePath + '.pre-recover'
        if (Test-Path -LiteralPath $statePath) { Copy-Item -LiteralPath $statePath -Destination $backup -Force }
        try {
            if (Test-Path -LiteralPath $statePath) {
                $st = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if ($null -ne $st) {
                    $st.session = $null
                    $st.openedDocs = $null
                    $st | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $statePath -Encoding UTF8
                }
            }
        } catch { }
        $null = Send-PipeJson $layout.pipe '{"cmd":"daemon-stop"}' 500 1500
        Start-Sleep -Milliseconds 400
        $exePath = Build-HelperExe $layout
        Start-Process -FilePath $exePath -ArgumentList @('--serve', $layout.pipe, '--idle-ms', '1200000') -WindowStyle Hidden | Out-Null
        $running = $false
        for ($i = 0; $i -lt 40 -and -not $running; $i++) { Start-Sleep -Milliseconds 150; $running = ($null -ne (Send-PipeJson $layout.pipe '{"cmd":"ping"}' 300 1500)) }
        $docs = @()
        $reply = Send-PipeJson $layout.pipe '{"cmd":"status","app":"zwcad","args":{}}' 800 12000
        try { $sj = $reply | ConvertFrom-Json; if ($sj.result -and $sj.result.documents) { $docs = @($sj.result.documents) } } catch { }
        $o = [ordered]@{ ok = $true; cmd = 'serve'; result = [ordered]@{ action = 'recover'; recovered = $true; daemonRunning = $running; appReloaded = ($null -ne $sj); documents = $docs; pipe = $layout.pipe; stateBackup = $backup } }
        [Console]::Out.Write(($o | ConvertTo-Json -Depth 8 -Compress)); exit 0
    }
    elseif ($action -eq 'stop' -or $action -eq 'reset') {
        $stopped = ($null -ne (Send-PipeJson $layout.pipe '{"cmd":"daemon-stop"}' 500 2000))
        if ($action -eq 'reset') {
            Get-Process -Name 'ZwcHelper' -ErrorAction SilentlyContinue | ForEach-Object {
                try { if ($_.Path -eq $layout.exe) { Stop-Process -Id $_.Id -Force; $stopped = $true } } catch { }
            }
        }
        $o = [ordered]@{ ok = $true; cmd = 'serve'; result = [ordered]@{ action = $action; stopped = $stopped; pipe = $layout.pipe } }
        [Console]::Out.Write(($o | ConvertTo-Json -Depth 6 -Compress)); exit 0
    }
    else {
        $reply = Send-PipeJson $layout.pipe '{"cmd":"ping"}' 300 1500
        $daemonPid = 0
        if ($reply -and $reply -match '"pid":(\d+)') { $daemonPid = [int]$Matches[1] }
        $o = [ordered]@{ ok = $true; cmd = 'serve'; result = [ordered]@{ action = 'status'; running = ($null -ne $reply); pid = $daemonPid; pipe = $layout.pipe; exe = $layout.exe; exeExists = (Test-Path -LiteralPath $layout.exe) } }
        [Console]::Out.Write(($o | ConvertTo-Json -Depth 6 -Compress)); exit 0
    }
}

# ---------------------------------------------------------------- flag parsing
# Two lists instead of one guess: a boolean switch never consumes the next token, a value switch
# always does (even when the value starts with "-", e.g. -Origin "-10,0"), and an unknown flag
# consumes the next token only when it does not look like another flag.
#
# The single-list version used to break "-New -App autocad": "app" was not a known switch, so
# "-App" was swallowed as the VALUE of -New and the whole command silently fell back to zwcad.
$BoolSwitches = @(
    'launch', 'readonly', 'nohint', 'dryrun', 'nocad', 'noaxis', 'nopointno', 'noautoscale',
    'axis', 'handles', 'fast', 'clear', 'quit', 'new', 'deep', 'keep', 'help', 'slow', 'keeponfail',
    'nolegend', 'scalebar', 'pointno', 'continue', 'plan', 'paper', 'deep', 'reveal', 'sweephidden', 'noreveal', 'tray'
)
$ValueSwitches = @(
    'app', 'progid', 'doc', 'data', 'sheet', 'cols', 'paper', 'origin', 'waterlevels', 'text',
    'token', 'scope', 'layerprefix', 'out', 'hscale', 'vscale', 'labelevery', 'count', 'max',
    'comthreshold', 'timeoutsec', 'layer', 'type', 'text', 'bbox', 'xdata', 'items', 'since', 'tol', 'locktimeout', 'busytimeout'
)
$opts = @{}
for ($i = 0; $i -lt $Rest.Count; $i++) {
    $tok = [string]$Rest[$i]
    if ($tok.Length -gt 1 -and $tok.StartsWith('-')) {
        $key = $tok.TrimStart('-').ToLowerInvariant()
        if ($BoolSwitches -contains $key) { $opts[$key] = $true; continue }
        $val = $true
        if ($i + 1 -lt $Rest.Count) {
            $nxt = [string]$Rest[$i + 1]
            $looksLikeFlag = ($nxt.Length -gt 1 -and $nxt.StartsWith('-') -and
                ($BoolSwitches -contains $nxt.TrimStart('-').ToLowerInvariant() -or
                 $ValueSwitches -contains $nxt.TrimStart('-').ToLowerInvariant()))
            if (($ValueSwitches -contains $key) -or (-not $looksLikeFlag)) { $val = $nxt; $i++ }
        }
        $opts[$key] = $val
    }
}
if ($opts.ContainsKey('help')) {
    Write-JsonError 'BAD_ARGS' 'usage: zwc.ps1 <command> [flags...]' 'doctor|status|open|info|read|section|undo|hint|finish; see DESIGN-M1.md section 3'
}

$Keep = $opts.ContainsKey('keep')
$work = $null

function Resolve-ExistingPath {
    param([string]$p)
    if (-not $p) { return $p }
    try {
        if ([System.IO.Path]::IsPathRooted($p)) { return [System.IO.Path]::GetFullPath($p) }
        $cand = Join-Path (Get-Location).Path $p
        if (Test-Path -LiteralPath $cand) { return [System.IO.Path]::GetFullPath($cand) }
    } catch { }
    return $p
}

$finalText = $null
$exitCode = 0
try {
    # ---------------------------------------------------------------- request JSON
    $appKind = 'zwcad'
    if ($opts.ContainsKey('app')) { $appKind = [string]$opts['app'] }
    $docArg = $null
    if ($opts.ContainsKey('doc')) { $docArg = Resolve-ExistingPath ([string]$opts['doc']) }

    $numKeys = @('hscale', 'vscale', 'labelevery', 'count', 'max', 'timeoutsec', 'comthreshold')
    $boolKeys = @('launch', 'readonly', 'nohint', 'dryrun', 'nocad', 'noaxis', 'nopointno', 'noautoscale', 'axis', 'handles', 'fast', 'clear', 'quit', 'new', 'deep')
    $pathKeys = @('data', 'out')

    # The helper reads a few keys with COM-era camelCase names. The argument parser lowercases every
    # flag, so map them back here (otherwise "-ProgId X" is silently ignored and the wrong build is
    # driven - this machine carries both ZWCAD 2025 and Classic 16).
    $keyMap = @{ 'progid' = 'progId' }

    $a = [ordered]@{}
    foreach ($k in $opts.Keys) {
        if ($k -eq 'app' -or $k -eq 'doc' -or $k -eq 'keep' -or $k -eq 'timeoutsec') { continue }
        $kOut = $k
        if ($keyMap.ContainsKey($k)) { $kOut = $keyMap[$k] }
        $v = $opts[$k]
        try {
            if ($boolKeys -contains $k) { $a[$kOut] = [bool]$v }
            elseif ($numKeys -contains $k) { $a[$kOut] = [double]$v }
            elseif ($pathKeys -contains $k) { $a[$kOut] = (Resolve-ExistingPath ([string]$v)) }
            else { $a[$kOut] = [string]$v }
        } catch {
            Write-JsonError 'BAD_ARGS' ("bad value for -" + $k + ": " + $v) 'numbers must be numeric, switches take no value'
        }
    }

    $req = [ordered]@{ cmd = $Command; doc = $docArg; app = $appKind; args = $a }
    $reqJson = $req | ConvertTo-Json -Depth 12 -Compress

    # ---------------------------------------------------------------- helper compile cache
    $scriptRoot = $PSScriptRoot
    if (-not $scriptRoot) { $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $helperDir = Join-Path $scriptRoot 'helper'
    if (-not (Test-Path -LiteralPath $helperDir)) {
        Write-JsonError 'NO_HELPER' ("helper source folder not found: " + $helperDir) 'the repository is incomplete'
    }
    $sources = @(Get-ChildItem -LiteralPath $helperDir -Filter *.cs -File | Sort-Object Name)
    if ($sources.Count -eq 0) { Write-JsonError 'NO_HELPER' 'no .cs files next to zwc.ps1' 'the repository is incomplete' }

    $sizes = @($sources | ForEach-Object { $_.Length } | Sort-Object)
    $newest = ($sources | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum.Ticks
    $cacheKey = '' + $sources.Count + '|' + $newest + '|' + ($sizes -join ',')
    $shaGen = [System.Security.Cryptography.SHA256]::Create()
    $sha = ([System.BitConverter]::ToString($shaGen.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($cacheKey))) -replace '-', '').ToLowerInvariant().Substring(0, 12)

    $repoRoot = Split-Path $scriptRoot -Parent
    $cacheDir = Join-Path $repoRoot ('bin\cache\' + $sha)
    $exePath = Join-Path $cacheDir 'ZwcHelper.exe'

    if (-not (Test-Path -LiteralPath $exePath)) {
        $csc = $null
        foreach ($arch in @('Framework64', 'Framework')) {
            $cand = Join-Path $env:SystemRoot ('Microsoft.NET\' + $arch + '\v4.0.30319\csc.exe')
            if (Test-Path -LiteralPath $cand) { $csc = $cand; break }
        }
        if (-not $csc) { Write-JsonError 'NO_COMPILER' 'csc.exe (.NET Framework 4.x) not found' 'enable .NET Framework 4.8 in Windows Features' }
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        $tmpExe = Join-Path $cacheDir ('build-' + $PID + '.exe')
        $cscArgs = @(
            '-nologo', '-optimize+', '-target:exe', '-platform:anycpu', '-codepage:65001',
            ('-out:' + $tmpExe), '-r:System.dll', '-r:System.Core.dll', '-r:System.Management.dll'
        )
        foreach ($s in $sources) { $cscArgs += $s.FullName }
        $compileLog = (& $csc @cscArgs 2>&1 | Out-String)
        if (-not (Test-Path -LiteralPath $tmpExe)) {
            Write-JsonError 'COMPILE_FAILED' ('csc failed: ' + $compileLog.Trim()) 'check scripts\helper\ZwcHelper.cs'
        }
        Move-Item -LiteralPath $tmpExe -Destination $exePath -Force
    }

    # ---------------------------------------------------------------- run the helper
    $work = Join-Path $env:TEMP ('zwcad-cooperate\run-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $reqPath = Join-Path $work 'request.json'
    $respPath = Join-Path $work 'response.json'
    $outPath = Join-Path $work 'stdout.txt'
    $errPath = Join-Path $work 'stderr.txt'
    [System.IO.File]::WriteAllText($reqPath, $reqJson, (New-Object System.Text.UTF8Encoding($false)))

    $timeoutSec = 60
    if ($opts.ContainsKey('timeoutsec')) { $timeoutSec = [int]$opts['timeoutsec'] }
    if ($a.Contains('launch') -and $a['launch']) { if ($timeoutSec -lt 90) { $timeoutSec = 90 } }
    if ($Command -eq 'finish') { if ($timeoutSec -lt 60) { $timeoutSec = 60 } }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    try {
        $proc = Start-Process -FilePath $exePath `
            -ArgumentList @('--request', ('"' + $reqPath + '"'), '--response', ('"' + $respPath + '"')) `
            -PassThru -NoNewWindow -RedirectStandardOutput $outPath -RedirectStandardError $errPath
    } catch {
        Write-JsonError 'HELPER_START_FAILED' ('cannot start ' + $exePath + ': ' + $_.Exception.Message) 'check the bin\cache folder permissions'
    }

    $exited = $proc.WaitForExit($timeoutSec * 1000)
    if (-not $exited) {
        try { $proc.Kill() } catch { }
        try { Start-Sleep -Milliseconds 200 } catch { }
        Write-JsonError 'TIMEOUT' ('the helper did not answer within ' + $timeoutSec + ' s (a CAD modal dialog or a blocked COM call is the usual cause)') 'close any dialog in CAD, then run: zwc.ps1 doctor'
    }
    $stopwatch.Stop()

    $respText = ''
    if (Test-Path -LiteralPath $respPath) {
        try { $respText = [System.IO.File]::ReadAllText($respPath, [System.Text.Encoding]::UTF8) } catch { $respText = '' }
    }
    if (-not $respText) {
        $errText = ''
        if (Test-Path -LiteralPath $errPath) {
            try { $errText = [System.IO.File]::ReadAllText($errPath, [System.Text.Encoding]::Default) } catch { }
        }
        if ($errText.Length -gt 800) { $errText = $errText.Substring($errText.Length - 800) }
        Write-JsonError 'HELPER_CRASH' ('the helper exited with code ' + $proc.ExitCode + ' without writing a response') ('stderr tail: ' + $errText.Trim())
    }

    $obj = $null
    try { $obj = $respText | ConvertFrom-Json } catch { Write-JsonError 'BAD_RESPONSE' ('the helper response is not valid JSON: ' + $_.Exception.Message) $null }

    # attach the wrapper wall clock next to the helper's own elapsedMs (no re-serialization: the
    # payload can be large, and ConvertTo-Json would round-trip every number through PS objects)
    $respText = $respText.TrimEnd()
    if ($respText.StartsWith('{')) {
        $respText = '{"wallMs":' + $stopwatch.ElapsedMilliseconds + ',' + $respText.Substring(1)
    }

    $ok = $false
    try { $ok = [bool]$obj.ok } catch { $ok = $false }
    if ($ok) {
        $finalText = $respText
        $exitCode = 0
    } else {
        $code = 'HELPER_FAILED'
        try { if ($obj.error -and $obj.error.code) { $code = [string]$obj.error.code } } catch { }
        $finalText = $respText
        $gate = @('VERIFY_FAILED', 'BAD_DATA', 'TOO_WIDE', 'CMD_STATE_POLLUTED', 'SENDCMD_TOO_LONG', 'LISP_UNBALANCED', 'OPEN_FAILED')
        if ($gate -contains $code) { $exitCode = 3 } else { $exitCode = 2 }
    }
}
finally {
    if ($work -and -not $Keep) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch { }
    }
}

if ($finalText) { [Console]::Out.Write($finalText) }
exit $exitCode
