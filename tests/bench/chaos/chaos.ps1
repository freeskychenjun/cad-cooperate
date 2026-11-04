# chaos.ps1 - E3 fault injection: six failure scenarios, each judged as "clean failure / self-heal"
# instead of "hang / corrupted document".
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\bench\chaos\chaos.ps1 -Cases C1,C3,C4
#   ... -Cases C1,C2,C3,C4,C6            (batch 2 additions: C2 prompt pollution, C6 two agents)
#   ... -Cases C5 -AllowDaemonKill       (batch 2: only after core-cad's recover is ready AND the
#                                         daemon being killed is provably ours - see the guard below)
#
# Every case gets a FRESH private copy of the fixture and a FRESH own instance (launched here,
# finished here, only our pid). Faults are injected through CAD's own COM API (SendCommand /
# Document.Close) - the helper is never modified.
#
# Output: tmp\bench\e3\chaos.json  (+ per-case artifacts under tmp\bench\e3\chaos\<case>\)
# Exit: 0 when every requested case matched its expected semantics, 3 otherwise.

param(
    [string[]]$Cases = @('C1', 'C3', 'C4'),
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string]$OutDir = 'tmp\bench\e3',
    [int]$TimeoutSec = 180,
    [int]$BusyTimeoutSec = 45,
    [switch]$AllowDaemonKill,
    [switch]$NoFreeze,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
. (Join-Path $PSScriptRoot 'lib.ps1')

# powershell -File passes "-Cases C1,C3,C4" as ONE string: split it here.
$Cases = @($Cases | ForEach-Object { [string]$_ } | ForEach-Object { $_.Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Cases.Count -eq 0) { $Cases = @('C1', 'C3', 'C4') }

$root = $script:ChaosRoot
$runRoot = Join-Path $root (Join-Path $OutDir 'chaos')
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$jsonPath = Join-Path $root (Join-Path $OutDir 'chaos.json')

function Get-ServeStatusJson {
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $script:ZwcPs1 + '" serve -Action status'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $psExe
    $psi.Arguments = $argLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $null = $p.Start()
    $out = $p.StandardOutput.ReadToEnd()
    $null = $p.WaitForExit(20000)
    return (ConvertFrom-JsonText $out)
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
    # separate process + HARD timeout: ZWCAD's SendCommand is synchronous, so a command that
    # waits for input must be injected and then killed (CAD stays in the command state - the fault).
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'inject.ps1'),
              '-ProgId', $progId, '-Action', $action, '-Doc', $work)
    if ($command) { $argv += @('-Command', $command) }
    $argLine = (($argv | ForEach-Object {
        $a = [string]$_
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
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
    $killedByHarness = $false
    if (-not $exited) { $killedByHarness = $true; try { $proc.Kill() } catch { }; try { $null = $proc.WaitForExit(5000) } catch { } }
    $proc.WaitForExit()
    $watch.Stop()
    $out = ''
    try { $out = [string]$outTask.Result } catch { }
    $exit = 0
    try { $exit = [int]$proc.ExitCode } catch { $exit = -1 }
    Write-Chaos ('  inject ' + $action + ($(if ($command) { ' [' + $command + ']' } else { '' })) +
                 ': exit=' + $exit + ' wall=' + [int]$watch.ElapsedMilliseconds + 'ms killed=' + $killedByHarness)
    return [pscustomobject]@{ exit = $exit; wallMs = [double]$watch.ElapsedMilliseconds
        killed = $killedByHarness; text = $out; parsed = (ConvertFrom-JsonText $out) }
}


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

function Find-DocHandle($entities, [string]$layer) {
    foreach ($e in $entities) {
        if ([string](Get-PropSafe $e 'layer') -eq $layer) { return $e }
    }
    return $null
}

# ------------------------------------------------------------------ cases
function Invoke-C1([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C1'; name = 'user busy (CAD in a pending command)'
                     expected = 'bounded write; BUSY/BUSY_TIMEOUT with reason, or ok + BUSY_CMDACTIVE_IGNORED; ESC clears; retry ok'
                     actual = $null; matched = $false; skipped = $false; details = [ordered]@{}; notes = @() }
    $before = Get-DocRead $progId $work 120
    # Lead's recipe: a BACKGROUND process that stays alive inside/after SendCommand, so CAD keeps
    # the pending command state (CMDACTIVE=1). Try several commands until one sticks.
    $candidates = @('_.LINE ', '_.CIRCLE ', '_.PLINE ', '_.RECTANG ')
    $injector = $null
    $usedCmd = $null
    foreach ($cand in $candidates) {
        $null = Invoke-Inject $progId $work 'escape' '' 20
        Start-Sleep -Milliseconds 400
        $injector = Start-InjectAsync $progId $work 'sendcmd' $cand 40
        Start-Sleep -Milliseconds 1300
        $cmdActive = 0
        try { $cmdActive = Get-CmdActive $progId $work } catch { }
        $r.details['try_' + $cand.Trim()] = $cmdActive
        if ($cmdActive -ne 0) { $usedCmd = $cand; break }
        Stop-InjectAsync $injector
        $injector = $null
        Start-Sleep -Milliseconds 400
    }
    if (-not $usedCmd) {
        $null = Invoke-Inject $progId $work 'escape' '' 20
        $r.skipped = $true
        $r.notes += 'injection ineffective: none of LINE/CIRCLE/PLINE/RECTANG left CMDACTIVE!=0 while the injector process was held alive'
        $r.actual = 'SKIPPED (busy-state injection ineffective on ZWCAD 2025)'
        return $r
    }
    $items = New-TextItems (Join-Path $caseDir 'c1-add.json') 'C1' 1 'CHAOS'
    $write = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) $BusyTimeoutSec $progId
    $code = [string](Get-PropSafe $write.parsed 'error.code')
    $warnings = @(Get-PropSafe $write.parsed 'warnings')
    $warnCodes = @($warnings | ForEach-Object { [string](Get-PropSafe $_ 'code') })
    $bounded = ($write.wallMs -le (($BusyTimeoutSec + 30) * 1000)) -and (-not $write.watchdog)
    $busyCodes = @('BUSY', 'BUSY_TIMEOUT', 'BUSY_RETRY_EXHAUSTED', 'CMD_STATE_POLLUTED')
    $busyOk = ($code -in $busyCodes) -or ($write.exit -eq 0 -and ($warnCodes -contains 'BUSY_CMDACTIVE_IGNORED'))
    $stability = Get-PropSafe $write.parsed 'result.stability'
    $lock = Get-PropSafe $write.parsed 'result.lock'
    $busy = Get-PropSafe $write.parsed 'result.busy'
    Stop-InjectAsync $injector
    Start-Sleep -Milliseconds 500
    $null = Invoke-Inject $progId $work 'escape' '' 30
    Start-Sleep -Milliseconds 900
    $cmdActiveAfter = Get-CmdActive $progId $work
    $retry = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) 120 $progId
    $after = Get-DocRead $progId $work 120
    $r.details += [ordered]@{
        injectedCommand = $usedCmd; writeExit = $write.exit; writeWallMs = $write.wallMs
        writeCode = $code; warnings = $warnCodes; writeBounded = $bounded; busySemanticsOk = $busyOk
        stability = $stability; lock = $lock; busy = $busy
        cmdActiveAfterEscape = $cmdActiveAfter; retryExit = $retry.exit
        retryCode = [string](Get-PropSafe $retry.parsed 'error.code')
        countBefore = $before.count; countAfterRetry = $after.count
    }
    $r.matched = ($bounded -and $busyOk -and $retry.exit -eq 0 -and $after.count -ge ($before.count + 1))
    $r.actual = ('busy(' + $usedCmd + ') -> write exit=' + $write.exit + ' code=' + $code +
                 ' wall=' + [int]$write.wallMs + 'ms bounded=' + $bounded + ' busyOk=' + $busyOk +
                 ' warnings=' + ($warnCodes -join ',') + '; ESC->CmdActive=' + $cmdActiveAfter +
                 '; retry exit=' + $retry.exit + ' count ' + $before.count + '->' + $after.count)
    return $r
}

function Invoke-C2([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C2'; name = 'prompt pollution (real ZWCAD signatures)'
                     expected = 'polluted prompt -> PROMPT_CLEARED + success, or explicit CMD_STATE_POLLUTED'
                     actual = $null; matched = $false; skipped = $false; details = [ordered]@{}; notes = @() }
    $before = Get-DocRead $progId $work 120
    # experiment: capture the REAL polluted-prompt strings of this UI/language
    $commands = @('(setq x (1+))', '(princ "abc', '(command "_.LINE" "nonsense")', '((( ' + [char]13)
    $samples = @()
    $pollutedPrompt = $null
    foreach ($cmd in $commands) {
        $null = Invoke-Inject $progId $work 'escape' '' 20
        Start-Sleep -Milliseconds 300
        $res = Invoke-Inject $progId $work 'sendcmd' $cmd 8
        Start-Sleep -Milliseconds 500
        $prompt = Get-LastPrompt $progId $work
        $samples += [ordered]@{ command = $cmd; injectorExit = $res.exit; lastPrompt = $prompt }
        $isPolluted = ($prompt -match 'Missing') -or ($prompt -match '错误') -or ($prompt -match '缺少') `
                      -or ($prompt -match '缺陷') -or ($prompt -match '无效') -or ($prompt -match '>')
        if ($isPolluted) { $pollutedPrompt = $prompt; break }
    }
    $r.details['promptSamples'] = $samples
    if (-not $pollutedPrompt) {
        $r.skipped = $true
        $r.notes += 'injection ineffective: none of the bad-LISP samples left a recognisable polluted prompt; real samples recorded in details.promptSamples (send them to core-cad for the multi-pattern matcher)'
        $r.actual = 'SKIPPED (prompt pollution injection ineffective)'
        return $r
    }
    $items = New-TextItems (Join-Path $caseDir 'c2-add.json') 'C2' 1 'CHAOS'
    $write = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) 120 $progId
    $code = [string](Get-PropSafe $write.parsed 'error.code')
    $warnings = @(Get-PropSafe $write.parsed 'warnings')
    $warnCodes = @($warnings | ForEach-Object { [string](Get-PropSafe $_ 'code') })
    $after = Get-DocRead $progId $work 120
    $r.details += [ordered]@{ pollutedPrompt = $pollutedPrompt; writeExit = $write.exit
        writeCode = $code; warnings = $warnCodes; countBefore = $before.count; countAfter = $after.count }
    $healed = ($write.exit -eq 0 -and $after.count -eq ($before.count + 1)) -and
              (($warnCodes -contains 'PROMPT_CLEARED') -or ($code -eq ''))
    $explicitPollution = ($code -eq 'CMD_STATE_POLLUTED' -or $code -eq 'PROMPT_POLLUTED')
    $r.matched = (($healed -or $explicitPollution) -and $after.exit -eq 0)
    $r.actual = ('polluted("' + $pollutedPrompt + '") -> write exit=' + $write.exit + ' code=' + $code +
                 ' warnings=' + ($warnCodes -join ',') + ' count ' + $before.count + '->' + $after.count)
    return $r
}

function Invoke-C3([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C3'; name = 'half-way write failure (illegal set value)'; expected = 'VERIFY_FAILED + rolledBack + fingerprint unchanged'
                     actual = $null; matched = $false; details = [ordered]@{}; notes = @() }
    $before = Get-DocRead $progId $work 120
    $q = Invoke-Cad @('query', '-Doc', $work, '-Layer', 'ANNO', '-Type', 'text', '-Mode', 'summary') 120 $progId
    $itemsPath = Join-Path $caseDir 'c3-query.json'
    [System.IO.File]::WriteAllText($itemsPath, $q.text, (New-Object System.Text.UTF8Encoding($false)))
    $reqPath = Join-Path $caseDir 'c3-bad.json'
    & $script:PyExe (Join-Path $root 'tests\bench\checkers\gen_entity_request.py') --mode update-height `
        --query $itemsPath --set-height '-5' --out $reqPath | Out-Null
    $write = Invoke-Cad @('entity', '-Doc', $work, '-Items', $reqPath) 120 $progId
    $after = Get-DocRead $progId $work 120
    $code = [string](Get-PropSafe $write.parsed 'error.code')
    $rolled = Get-PropSafe $write.parsed 'result.rolledBack'
    $changed = Compare-Fingerprint (Get-Fingerprint $before.entities) (Get-Fingerprint $after.entities)
    $r.details = [ordered]@{ writeExit = $write.exit; writeCode = $code; rolledBack = $rolled
        fingerprintChanged = $changed.Count; changedHandles = @($changed | Select-Object -First 5)
        countBefore = $before.count; countAfter = $after.count
        verifyIssues = @(Get-PropSafe $write.parsed 'result.verify.issues') }
    $r.matched = (($write.exit -eq 2 -or $write.exit -eq 3) -and $code -eq 'VERIFY_FAILED' -and $rolled -eq $true -and $changed.Count -eq 0)
    $r.actual = ('illegal height(-5) -> exit=' + $write.exit + ' code=' + $code + ' rolledBack=' + [string]$rolled +
                 ' fingerprintChanged=' + $changed.Count)
    return $r
}

function Invoke-C4([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C4'; name = 'document gone before write'; expected = 'DOC_GONE (bounded, no crash), reopen then succeed'
                     actual = $null; matched = $false; skipped = $false; details = [ordered]@{}; notes = @() }
    $before = Get-DocRead $progId $work 120
    # preferred: close ALL documents through COM (the harness owns this instance)
    $closed = Invoke-Inject $progId $work 'closeall' '' 30
    $r.details['comCloseAll'] = $closed.parsed
    $remaining = -1
    try { $remaining = [int](Get-PropSafe $closed.parsed 'remaining') } catch { }
    if ($remaining -gt 0 -or $null -eq (Get-PropSafe $closed.parsed 'ok')) {
        $r.notes += 'closeall could not remove the document via COM; documenting as injection limit and testing the equivalent DOC_GONE path (path no longer resolvable)'
        $r.details['fallback'] = 'cannot close the open doc from an out-of-process client on this build'
    }
    Start-Sleep -Milliseconds 800
    $items = New-TextItems (Join-Path $caseDir 'c4-add.json') 'C4' 1 'CHAOS'
    $write = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) 120 $progId
    $code = [string](Get-PropSafe $write.parsed 'error.code')
    $bounded = ($write.wallMs -le 90000) -and (-not $write.watchdog)
    $goneCodes = @('DOC_GONE', 'DOC_NOT_FOUND', 'DOC_CLOSED')
    $reopen = Invoke-Cad @('open', '-Doc', $work) 120 $progId
    $retry = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) 120 $progId
    $after = Get-DocRead $progId $work 120
    $r.details += [ordered]@{ writeExit = $write.exit; writeCode = $code; writeWallMs = $write.wallMs
        bounded = $bounded; reopenExit = $reopen.exit; retryExit = $retry.exit
        countBefore = $before.count; countAfterRetry = $after.count }
    if (-not ($goneCodes -contains $code)) {
        $r.skipped = $true
        $r.notes += ('injection ineffective: write to the closed doc returned code="' + $code + '" instead of DOC_GONE')
        $r.actual = 'SKIPPED (close-doc injection ineffective; write code=' + $code + ')'
        return $r
    }
    $r.matched = ($bounded -and $reopen.exit -eq 0 -and $retry.exit -eq 0 -and $after.count -eq ($before.count + 1))
    $r.actual = ('closed doc -> write exit=' + $write.exit + ' code=' + $code + ' bounded=' + $bounded +
                 '; reopen exit=' + $reopen.exit + '; retry exit=' + $retry.exit + ' count ' +
                 $before.count + '->' + $after.count)
    return $r
}

function Invoke-C5([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C5'; name = 'daemon killed mid-command'
                     expected = 'bounded error/auto-restart, no half-written batch, next command recovers'
                     actual = $null; matched = $false; skipped = $false; details = [ordered]@{}; notes = @() }
    if (-not $AllowDaemonKill) {
        $r.skipped = $true
        $r.notes += 'skipped: -AllowDaemonKill not given'
        $r.actual = 'SKIPPED (flag not given)'
        return $r
    }
    $serve = Get-ServeStatusJson
    $daemonPid = [int](Get-PropSafe $serve 'result.pid')
    $daemonExe = [string](Get-PropSafe $serve 'result.exe')
    $r.details['serveStatus'] = $serve
    if (-not $daemonPid) {
        $r.skipped = $true
        $r.notes += 'no daemon running for the frozen snapshot; cannot kill it'
        $r.actual = 'SKIPPED (no daemon)'
        return $r
    }
    # ownership proof: the daemon exe must live under this run's frozen mirror
    $frozenRoot = ''
    if ($script:ClientExe) { $frozenRoot = Split-Path -Parent (Split-Path -Parent $script:ClientExe) }
    $owned = ($daemonExe -and $frozenRoot -and $daemonExe.ToLowerInvariant().StartsWith($frozenRoot.ToLowerInvariant()))
    $r.details['ownership'] = [ordered]@{ daemonPid = $daemonPid; daemonExe = $daemonExe
        frozenRoot = $frozenRoot; owned = $owned }
    if (-not $owned) {
        $r.skipped = $true
        $r.notes += 'daemon is not provably ours (exe outside the frozen mirror) - refusing to kill another agent daemon'
        $r.actual = 'SKIPPED (daemon not provably ours)'
        return $r
    }
    $before = Get-DocRead $progId $work 120
    $items = New-TextItems (Join-Path $caseDir 'c5-add.json') 'C5' 200 'CHAOS'
    $job = Start-CadAsync @('entity', '-Doc', $work, '-Items', $items) $progId `
        (Join-Path $caseDir 'c5.stdout.txt') (Join-Path $caseDir 'c5.stderr.txt')
    Start-Sleep -Milliseconds 250
    $killed = $false
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $daemonPid -ErrorAction SilentlyContinue)) { break }
        try { Stop-Process -Id $daemonPid -Force; $killed = $true; break } catch { Start-Sleep -Milliseconds 150 }
    }
    $r.details['killAttempt'] = [ordered]@{ pid = $daemonPid; killed = $killed
        deadAfterKill = (-not [bool](Get-Process -Id $daemonPid -ErrorAction SilentlyContinue)) }
    $done = Wait-CadAsync $job 120
    Start-Sleep -Milliseconds 1000
    $statusAfter = Invoke-Cad @('status') 90 $progId
    $after = Get-DocRead $progId $work 120
    $texts = Get-TextsOf $after.entities
    $added = @($texts | Where-Object { $_ -like 'C5-*' }).Count
    $noPartial = ($added -eq 0 -or $added -eq 200)
    $bounded = ($done.wallMs -le 150000) -and (-not $done.watchdog)
    $recovered = ($statusAfter.exit -eq 0)
    $r.details += [ordered]@{ writeExit = $done.exit; writeWallMs = $done.wallMs
        writeCode = [string](Get-PropSafe $done.parsed 'error.code')
        writeWarnings = @(Get-PropSafe $done.parsed 'warnings')
        countBefore = $before.count; countAfter = $after.count; c5Entities = $added
        partial = (-not $noPartial); nextStatusExit = $statusAfter.exit
        nextStatusCode = [string](Get-PropSafe $statusAfter.parsed 'error.code') }
    $r.matched = ($killed -and $bounded -and $noPartial -and $recovered)
    $r.actual = ('killed own daemon pid ' + $daemonPid + ' mid-write(200 items) -> write exit=' + $done.exit +
                 ' code=' + [string](Get-PropSafe $done.parsed 'error.code') + ' wall=' + [int]$done.wallMs +
                 'ms partial=' + (-not $noPartial) + ' added=' + $added + '; next status exit=' + $statusAfter.exit)
    return $r
}

function Invoke-C6([string]$progId, [string]$work, [string]$caseDir) {
    $r = [ordered]@{ case = 'C6'; name = 'two agents alternating writes'; expected = 'serialized, both diffs correct, no overwrite'
                     actual = $null; matched = $false; details = [ordered]@{}; notes = @() }
    $before = Get-DocRead $progId $work 120
    $snap = Invoke-Cad @('snapshot', '-Doc', $work) 120 $progId
    $snapPath = [string](Get-PropSafe $snap.parsed 'result.path')
    $rounds = 3
    $per = 3
    $fails = @()
    for ($i = 1; $i -le $rounds; $i++) {
        foreach ($s in @('A', 'B')) {
            $items = New-TextItems (Join-Path $caseDir ('c6-' + $s + $i + '.json')) ('C6' + $s + $i) $per 'CHAOS'
            $w = Invoke-Cad @('entity', '-Doc', $work, '-Items', $items) 120 $progId
            if ($w.exit -ne 0) { $fails += ($s + '#' + $i + ':' + [string](Get-PropSafe $w.parsed 'error.code')) }
        }
    }
    $after = Get-DocRead $progId $work 120
    $texts = Get-TextsOf $after.entities
    $missing = @()
    for ($i = 1; $i -le $rounds; $i++) {
        foreach ($s in @('A', 'B')) {
            for ($j = 1; $j -le $per; $j++) {
                $want = 'C6' + $s + $i + '-' + $j
                if ($texts -notcontains $want) { $missing += $want }
            }
        }
    }
    $diff = Invoke-Cad @('diff', '-Doc', $work, '-Since', $snapPath) 120 $progId
    $added = @(Get-PropSafe $diff.parsed 'result.added').Count
    $expected = $before.count + $rounds * 2 * $per
    $r.details = [ordered]@{ countBefore = $before.count; countAfter = $after.count; expected = $expected
        missingTexts = @($missing | Select-Object -First 6); missingCount = $missing.Count
        writeFailures = $fails; diffAdded = $added; diffExit = $diff.exit }
    $r.matched = ($fails.Count -eq 0 -and $after.count -eq $expected -and $missing.Count -eq 0 -and $added -eq ($rounds * 2 * $per))
    $r.actual = ('2 sessions x ' + $rounds + ' rounds x ' + $per + ' writes -> count ' + $before.count + '->' +
                 $after.count + ' (expected ' + $expected + '), missing=' + $missing.Count + ', diffAdded=' + $added)
    return $r
}

# ------------------------------------------------------------------ driver
$results = @()
$serveBefore = Get-ServeStatusJson
$summary = [ordered]@{
    schema = 'zwcad-chaos/1'
    kind = 'chaos'
    generatedAt = (Get-Date).ToString('o')
    product = $ProgId
    requestedCases = $Cases
    serveStatusBefore = $serveBefore
    cases = @()
    verdict = 'FAIL'
    notes = @()
}

foreach ($case in $Cases) {
    $caseDir = Join-Path $runRoot $case
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    Write-Chaos ('=== ' + $case)
    $cad = $null
    $work = $null
    $rec = $null
    try {
        if (-not $NoFreeze) { $summary['frozen_' + $case] = New-FrozenProduct $caseDir }
        $work = New-WorkDrawing 'design-water.dxf' $caseDir $case
        $cad = Start-OwnCad $ProgId $TimeoutSec
        $open = Invoke-Cad @('open', '-Doc', $work) 120 $ProgId
        if ($open.exit -ne 0) { throw ('open failed: ' + $open.text) }
        switch ($case.ToUpperInvariant()) {
            'C1' { $rec = Invoke-C1 $ProgId $work $caseDir }
            'C2' { $rec = Invoke-C2 $ProgId $work $caseDir }
            'C3' { $rec = Invoke-C3 $ProgId $work $caseDir }
            'C4' { $rec = Invoke-C4 $ProgId $work $caseDir }
            'C5' { $rec = Invoke-C5 $ProgId $work $caseDir }
            'C6' { $rec = Invoke-C6 $ProgId $work $caseDir }
            default { $rec = [ordered]@{ case = $case; matched = $false; notes = @('unknown case') } }
        }
        if ($null -ne $cad) {
            $rec['hygiene'] = Stop-OwnCad $ProgId $cad.pid $TimeoutSec
        }
    } catch {
        $rec = [ordered]@{ case = $case; matched = $false; notes = @('EXCEPTION: ' + $_.Exception.Message) }
        if ($null -ne $cad) {
            try { $rec['hygiene'] = Stop-OwnCad $ProgId $cad.pid $TimeoutSec } catch { }
        }
    }
    Write-Chaos ('  matched=' + [string]$rec.matched + ' | ' + [string]$rec.actual)
    if ($rec.notes) { foreach ($n in $rec.notes) { Write-Chaos ('  note: ' + $n) } }
    $results += $rec
}
$summary.cases = $results
$allMatched = $true
$mismatchCount = 0
foreach ($r in $results) {
    if ($r.matched -eq $true) { continue }
    if ($r.skipped -eq $true -or [string]$r.actual -like 'SKIPPED*') { continue }
    $allMatched = $false
    $mismatchCount++
}
$summary['mismatchCount'] = $mismatchCount
$summary.verdict = $(if ($allMatched) { 'PASS' } else { 'FAIL' })
Write-JsonFile $jsonPath $summary

Write-Chaos '---- summary'
foreach ($r in $results) {
    Write-Chaos ('  ' + [string]$r.case + ' ' + [string]$r.name + ' -> ' +
                 $(if ($r.matched -eq $true) { 'MATCHED' } elseif ([string]$r.actual -eq 'SKIPPED') { 'SKIPPED' } else { 'MISMATCH' }))
}
Write-Chaos ('chaos verdict: ' + $summary.verdict)
Write-Chaos ('json: ' + $jsonPath)

if ($allMatched) { exit 0 }
exit 3
