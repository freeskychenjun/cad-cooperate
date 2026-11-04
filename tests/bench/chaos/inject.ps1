# inject.ps1 - bounded, single-purpose COM fault injector for the E3 chaos harness.
#
# IMPORTANT: on ZWCAD 2025 `Document.SendCommand` is SYNCHRONOUS - if the command waits for
# input (`_.LINE`), the call never returns. The harness therefore runs this injector as a
# SEPARATE process and kills it after a short timeout (see Invoke-Inject in chaos.ps1); CAD is
# left in the command state, which is exactly the fault we want to inject. Escape/close actions
# finish quickly and are not killed.
#
# Actions:
#   poll     - print app/doc facts (names, FullName, CMDACTIVE) as JSON
#   sendcmd  - Command=<text>; may block (caller kills)
#   escape   - send ESC / Ctrl+C to clear a pending command or polluted prompt
#   close    - close the document given by -Doc (without saving)
#
# Output: one JSON line {"ok":true/false,"action":...,"detail":...,"error":...}

param(
    [string]$ProgId = 'ZWCAD.Application.2025',
    [string]$Doc = '',
    [string]$Action = 'poll',
    [string]$Command = '',
    [int]$Retries = 6,
    [int]$HoldSec = 0
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Out-Json($obj) {
    [Console]::Out.Write(($obj | ConvertTo-Json -Depth 6 -Compress))
    exit 0
}

function Get-CadApp {
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            $app = [Runtime.InteropServices.Marshal]::GetActiveObject($ProgId)
            if ($null -ne $app) { return $app }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Has-CloseMethod($obj) {
    if ($null -eq $obj -or $obj -is [string]) { return $false }
    try { return ($null -ne $obj.PSObject.Methods['Close']) } catch { return $false }
}

function Find-CadDoc($app, [string]$path) {
    if ($null -eq $app) { return $null }
    $want = ([string]$path).ToLowerInvariant()
    for ($attempt = 0; $attempt -lt $Retries; $attempt++) {
        try {
            $count = [int]$app.Documents.Count
            for ($i = 0; $i -lt $count; $i++) {
                foreach ($idx in @($i, $i + 1)) {
                    $d = $null
                    try { $d = $app.Documents.Item($idx) } catch { continue }
                    if (-not (Has-CloseMethod $d)) { continue }
                    $fn = ''
                    try { $fn = [string]$d.FullName } catch { }
                    if ($fn.ToLowerInvariant() -eq $want) { return $d }
                }
            }
            # fallback: the active document
            $ad = $null
            try { $ad = $app.ActiveDocument } catch { }
            if (Has-CloseMethod $ad) {
                $fn = ''
                try { $fn = [string]$ad.FullName } catch { }
                if ($fn.ToLowerInvariant() -eq $want) { return $ad }
            }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

$app = Get-CadApp
if ($null -eq $app) { Out-Json @{ ok = $false; action = $Action; error = 'NO_APP' } }

$doc = $null
if ($Doc) { $doc = Find-CadDoc $app $Doc } else { try { $doc = $app.ActiveDocument } catch { } }

try {
    switch ($Action) {
        'poll' {
            $names = @()
            try {
                $count = [int]$app.Documents.Count
                for ($i = 0; $i -lt $count; $i++) {
                    $d = $null
                    try { $d = $app.Documents.Item($i) } catch { }
                    if ($null -ne $d) {
                        $fn = ''
                        try { $fn = [string]$d.FullName } catch { }
                        $names += @{ name = [string]$d.Name; fullName = $fn }
                    }
                }
            } catch { }
            $cmdActive = $null
            try { $cmdActive = [int]$app.ActiveDocument.GetVariable('CMDACTIVE') } catch { }
            $prompt = ''
            try { $prompt = [string]$app.ActiveDocument.GetVariable('LASTPROMPT') } catch { }
            Out-Json @{ ok = $true; action = 'poll'; docs = $names; cmdActive = $cmdActive; lastPrompt = $prompt
                       activeName = $(try { [string]$app.ActiveDocument.Name } catch { '' }) }
        }
        'sendcmd' {
            if ($null -eq $doc) { Out-Json @{ ok = $false; action = $Action; error = 'NO_DOC' } }
            $doc.SendCommand($Command)
            # -HoldSec keeps this process (and CAD's pending command) alive after the call returns;
            # if SendCommand itself blocks (pending input) the process is alive anyway.
            if ($HoldSec -gt 0) { Start-Sleep -Seconds $HoldSec }
            Out-Json @{ ok = $true; action = 'sendcmd'; command = $Command; heldSec = $HoldSec }
        }
        'closeall' {
            $closed = 0
            try {
                $docs = $app.Documents
                $docs.Close()
                $closed = -1
            } catch {
                for ($i = 0; $i -lt 5; $i++) {
                    $d = $null
                    try { $d = $app.ActiveDocument } catch { }
                    if (-not (Has-CloseMethod $d)) { break }
                    try { $d.Close($false); $closed++ } catch { break }
                    Start-Sleep -Milliseconds 200
                }
            }
            Out-Json @{ ok = $true; action = 'closeall'; closed = $closed
                        remaining = $(try { [int]$app.Documents.Count } catch { -1 }) }
        }
        'escape' {
            $target = $doc
            if ($null -eq $target) { try { $target = $app.ActiveDocument } catch { } }
            if ($null -eq $target) { Out-Json @{ ok = $false; action = 'escape'; error = 'NO_DOC' } }
            $sent = 0
            foreach ($seq in @([string][char]27, [string][char]3, '')) {
                try { $target.SendCommand($seq); $sent++ } catch { }
            }
            Out-Json @{ ok = ($sent -gt 0); action = 'escape'; sent = $sent }
        }
        'close' {
            if ($null -eq $doc) { Out-Json @{ ok = $false; action = 'close'; error = 'NO_DOC' } }
            $doc.Close($false)
            Out-Json @{ ok = $true; action = 'close'; doc = $Doc }
        }
        default { Out-Json @{ ok = $false; action = $Action; error = 'UNKNOWN_ACTION' } }
    }
} catch {
    Out-Json @{ ok = $false; action = $Action; error = $_.Exception.Message
                hresult = $(try { ('0x' + ('{0:X8}' -f $_.Exception.HResult)) } catch { '' }) }
}
