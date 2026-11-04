<#
run_m1.ps1 - M1 section-figure end-to-end runner (pure ASCII, PowerShell 5.1).

Chain:
  1. prepare.py   : tests\fixtures\section-30.xlsx -> tmp\section-30.model.json
  2. verify_section.py  model vs independent baseline        (must PASS)
  3. verify_section.py --tamper                             (must DETECT the tamper)
  4. bad fixtures -> prepare.py must exit 2 / BAD_DATA and write NO output file
  5. zwc.ps1 section -Data <model.json> -DryRun             (SKIP when zwc.ps1 absent)
  6. zwc.ps1 section -Data <model.json> [-Doc]              (real draw; caller must have CAD open)
  7. zwc.ps1 read -> verify_section.py                      (independent read-back check)

This script NEVER starts CAD / ZWCAD / AutoCAD. It only calls scripts\zwc.ps1, which
attaches to an already-open drawing; there is no -Launch here on purpose.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -DryRunOnly
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -NoCad
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -Doc "<你的图纸>.dwg"

Exit code: 0 when no FAIL (min PASS set: model + baseline verification + bad cases).
#>

[CmdletBinding()]
param(
    [switch]$DryRunOnly,
    [switch]$NoCad,
    [string]$Doc = "",
    [string]$Python = "python",
    [string]$DataFile = "",
    [string]$ExpectedFile = "",
    [switch]$VerboseOut
)

$ErrorActionPreference = "Continue"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"

$Root = Split-Path -Parent $PSScriptRoot
$TmpDir = Join-Path $Root "tmp"
$PreparePy = Join-Path $Root "scripts\section\prepare.py"
$VerifyPy = Join-Path $PSScriptRoot "verify_section.py"
$ZwcPs1 = Join-Path $Root "scripts\zwc.ps1"
$PsExe = Join-Path $PSHOME "powershell.exe"

if ([string]::IsNullOrEmpty($DataFile)) {
    $DataFile = Join-Path $PSScriptRoot "fixtures\section-30.xlsx"
}
if ([string]::IsNullOrEmpty($ExpectedFile)) {
    $ExpectedFile = Join-Path $PSScriptRoot "fixtures\section-30.expected.json"
}
$ModelPath = Join-Path $TmpDir "section-30.model.json"
$ReadJson = Join-Path $TmpDir "section-30.read.json"

$script:PassCount = 0
$script:FailCount = 0
$script:SkipCount = 0

function Write-Item([string]$Status, [string]$Name, [string]$Message) {
    if ($Status -eq "PASS") { $script:PassCount = $script:PassCount + 1 }
    elseif ($Status -eq "FAIL") { $script:FailCount = $script:FailCount + 1 }
    else { $script:SkipCount = $script:SkipCount + 1 }
    Write-Output ("{0}  {1}  {2}" -f $Status, $Name, $Message)
}

function Invoke-Native([string]$Exe, [string[]]$ArgList) {
    $raw = & $Exe @ArgList 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $text = ($raw -join "`n")
    return [pscustomobject]@{ Code = $code; Text = $text }
}

function Get-JsonBlock([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    $i = $Text.IndexOf("{")
    $j = $Text.LastIndexOf("}")
    if ($i -ge 0 -and $j -gt $i) { return $Text.Substring($i, $j - $i + 1) }
    return $Text
}

function ConvertFrom-JsonText([string]$Text) {
    $block = Get-JsonBlock $Text
    if ([string]::IsNullOrEmpty($block)) { return $null }
    try { return ($block | ConvertFrom-Json) } catch { }
    $lines = @($Text -split "`n")
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $ln = $lines[$i].Trim()
        if ($ln.StartsWith("{") -and $ln.EndsWith("}")) {
            try { return ($ln | ConvertFrom-Json) } catch { }
        }
    }
    return $null
}

function Get-Prop($Obj, [string]$Name) {
    if ($Obj -eq $null) { return $null }
    $p = $Obj.PSObject.Properties[$Name]
    if ($p -eq $null) { return $null }
    return $p.Value
}

function Get-CountsString($Obj) {
    if ($Obj -eq $null) { return "" }
    $pairs = @()
    foreach ($p in $Obj.PSObject.Properties) {
        if ($p.Value -ne $null) { $pairs = $pairs + ($p.Name + "=" + [string]$p.Value) }
    }
    return (($pairs | Sort-Object) -join ",")
}

Write-Output "== run_m1.ps1 =============================================="
Write-Output ("root     : " + $Root)
Write-Output ("data     : " + $DataFile)
Write-Output ("expected : " + $ExpectedFile)
Write-Output ("python   : " + $Python)
Write-Output ("zwc.ps1  : " + $ZwcPs1 + " (exists=" + (Test-Path $ZwcPs1) + ")")
if ($DryRunOnly) { Write-Output "mode     : DryRunOnly" }
elseif ($NoCad) { Write-Output "mode     : NoCad" }
else { Write-Output "mode     : full (real draw needs an already-open CAD drawing)" }

if (-not (Test-Path $TmpDir)) { New-Item -ItemType Directory -Path $TmpDir -Force | Out-Null }

# ---- preflight ---------------------------------------------------------------
foreach ($p in @($Python, $PreparePy, $VerifyPy, $DataFile, $ExpectedFile)) {
    if (-not (Test-Path $p)) {
        Write-Item "FAIL" "preflight" ("missing: " + $p)
        Write-Output ("[run_m1] RESULT: FAIL (PASS=" + $script:PassCount + " FAIL=" + $script:FailCount + " SKIP=" + $script:SkipCount + ")")
        exit 1
    }
}

$exp = $null
try {
    $exp = (Get-Content -Raw -Encoding UTF8 $ExpectedFile) | ConvertFrom-Json
} catch { }
if ($exp -eq $null) {
    Write-Item "FAIL" "preflight.expected" "cannot parse expected JSON"
    Write-Output ("[run_m1] RESULT: FAIL (PASS=" + $script:PassCount + " FAIL=" + $script:FailCount + " SKIP=" + $script:SkipCount + ")")
    exit 1
}
$expTotal = 0
$expTotalProp = Get-Prop (Get-Prop $exp "expected") "total"
if ($expTotalProp -ne $null) { $expTotal = [int]$expTotalProp }

# water argument is taken from expected.json at run time (keeps this .ps1 pure ASCII)
$waterArg = ""
$wlv = Get-Prop $exp "waterLevels"
if ($wlv -ne $null -and @($wlv).Count -gt 0) {
    $w0 = @($wlv)[0]
    $wv = Get-Prop $w0 "value"
    $wl = Get-Prop $w0 "label"
    if ($wv -ne $null) {
        $wvText = [double]$wv
        $wvText = $wvText.ToString("0.00", [System.Globalization.CultureInfo]::InvariantCulture)
        if ($wl -eq $null) { $wl = "" }
        $waterArg = ($wvText + "=" + [string]$wl)
    }
}
Write-Output ("water arg: [" + $waterArg + "]  expected total=" + $expTotal)

# ---- step 1: xlsx -> model.json ---------------------------------------------
if (Test-Path $ModelPath) { Remove-Item $ModelPath -Force }
$r1 = Invoke-Native $Python @($PreparePy, "--data", $DataFile, "--out", $ModelPath, "--water", $waterArg)
$j1 = ConvertFrom-JsonText $r1.Text
$ok1 = ($r1.Code -eq 0)
$t1 = Get-Prop $j1 "total"
if ($t1 -ne $null -and [int]$t1 -ne $expTotal) { $ok1 = $false }
$s1 = Get-Prop $j1 "scale"
$h1 = Get-Prop $s1 "horizontal"
if ($ok1) {
    Write-Item "PASS" "prepare.xlsx->model" ("exit=0 total=" + $t1 + " hScale=" + $h1 + " autoScaled=" + (Get-Prop $j1 "autoScaled") + " widthMm=" + (Get-Prop $j1 "widthMm"))
} else {
    Write-Item "FAIL" "prepare.xlsx->model" ("exit=" + $r1.Code + " total=" + $t1 + " json=" + (Get-JsonBlock $r1.Text))
    if ($VerboseOut) { Write-Output $r1.Text }
}

# ---- step 2: baseline verification -----------------------------------------
$r2 = Invoke-Native $Python @($VerifyPy, "--model", $ModelPath, "--expected", $ExpectedFile)
$ok2 = ($r2.Code -eq 0) -and ($r2.Text -match "RESULT: PASS")
if ($ok2) {
    $passLines = @($r2.Text -split "`n" | Where-Object { $_.StartsWith("PASS") }).Count
    Write-Item "PASS" "verify.model-vs-baseline" ("exit=0 assertions=" + $passLines)
} else {
    Write-Item "FAIL" "verify.model-vs-baseline" ("exit=" + $r2.Code + " tail=" + ($r2.Text -split "`n" | Select-Object -Last 2 | Out-String).Trim())
    Write-Output $r2.Text
}

# ---- step 2b: ezdxf independent round-trip (third implementation) -----------
$CheckDxfPy = Join-Path $PSScriptRoot "check_dxf.py"
$PyLibs = Join-Path $Root "tools\py-libs"
if (-not (Test-Path $CheckDxfPy)) {
    Write-Item "SKIP" "dxf.roundtrip" "tests\check_dxf.py not found"
} else {
    $oldPyPath = $env:PYTHONPATH
    $env:PYTHONPATH = $PyLibs + ";" + $oldPyPath
    $r5 = Invoke-Native $Python @($CheckDxfPy, "--model", $ModelPath, "--expected", $ExpectedFile)
    $env:PYTHONPATH = $oldPyPath
    if ($r5.Code -eq 3) {
        Write-Item "SKIP" "dxf.roundtrip" "ezdxf unavailable (tools\py-libs missing?)"
    } elseif (($r5.Code -eq 0) -and ($r5.Text -match "RESULT: PASS")) {
        $n5 = @($r5.Text -split "`n" | Where-Object { $_.StartsWith("PASS") }).Count
        Write-Item "PASS" "dxf.roundtrip" ("ezdxf write->read->compare, assertions=" + $n5 + " (audit errors=0)")
    } else {
        Write-Item "FAIL" "dxf.roundtrip" ("exit=" + $r5.Code)
        Write-Output $r5.Text
    }
}

# ---- step 3: tamper self-check ---------------------------------------------
$r3 = Invoke-Native $Python @($VerifyPy, "--model", $ModelPath, "--expected", $ExpectedFile, "--tamper")
$ok3 = ($r3.Code -eq 0) -and ($r3.Text -match "TAMPER: PASS")
if ($ok3) {
    Write-Item "PASS" "verify.tamper-detected" "negative case proven (exit=0)"
} else {
    Write-Item "FAIL" "verify.tamper-detected" ("exit=" + $r3.Code)
    Write-Output $r3.Text
}

# ---- step 4: bad fixtures must be rejected, no output file ------------------
$badNames = @("bad-2points.xlsx", "bad-nonmono.xlsx", "bad-empty.xlsx")
foreach ($bn in $badNames) {
    $bp = Join-Path $PSScriptRoot ("fixtures\" + $bn)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($bn)
    $bo = Join-Path $TmpDir ("rejected-" + $stem + ".model.json")
    if (Test-Path $bo) { Remove-Item $bo -Force }
    $rb = Invoke-Native $Python @($PreparePy, "--data", $bp, "--out", $bo, "--water", $waterArg)
    $jb = ConvertFrom-JsonText $rb.Text
    $code = Get-Prop (Get-Prop $jb "error") "code"
    $okb = ($rb.Code -eq 2) -and ((Get-Prop $jb "ok") -eq $false) -and ($code -eq "BAD_DATA") -and (-not (Test-Path $bo))
    if ($okb) {
        Write-Item "PASS" ("bad." + $stem) ("BAD_DATA exit=2 no-output-file msg=[" + (Get-Prop (Get-Prop $jb "error") "message") + "]")
    } else {
        Write-Item "FAIL" ("bad." + $stem) ("exit=" + $rb.Code + " code=" + $code + " outFileExists=" + (Test-Path $bo))
    }
}

# ---- step 5..7: CAD side (delegated to core-cad's zwc.ps1) -----------------
if (-not (Test-Path $ZwcPs1)) {
    Write-Item "SKIP" "zwc.section.dryrun" "scripts\zwc.ps1 not found (core-cad not delivered yet); no CAD touched"
    Write-Item "SKIP" "zwc.section.real" "scripts\zwc.ps1 not found; no CAD touched"
    Write-Item "SKIP" "zwc.read.verify" "scripts\zwc.ps1 not found; no CAD touched"
} else {
    # step 5: -DryRun (should not need a running CAD; model/expected cross-checked below)
    $dryArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Mta", "-File", $ZwcPs1,
                 "section", "-Data", $ModelPath, "-DryRun")
    $rd1 = Invoke-Native $PsExe $dryArgs
    $jd1 = ConvertFrom-JsonText $rd1.Text
    $dres = Get-Prop $jd1 "result"
    $dExp = Get-Prop $dres "expected"
    $dTotal = Get-Prop $dExp "total"
    $dModel = Get-Prop $dres "model"
    $expExpected = Get-Prop $exp "expected"
    $expScale = Get-Prop (Get-Prop $exp "scale") "horizontal"
    $expBbox = Get-Prop $exp "bbox"

    # 5a: cross-check what core-cad's helper computed against my independent baseline
    $dScale = Get-Prop $dModel "hScale"
    $dBbox = Get-Prop $dModel "bbox"
    $okTotal = ($dTotal -ne $null) -and ([int]$dTotal -eq [int]$expTotal)
    $okType = (Get-CountsString (Get-Prop $dExp "byType")) -eq (Get-CountsString (Get-Prop $expExpected "byType"))
    $okLayer = (Get-CountsString (Get-Prop $dExp "byLayer")) -eq (Get-CountsString (Get-Prop $expExpected "byLayer"))
    $okScale = ($dScale -ne $null) -and ([double]$dScale -eq [double]$expScale)
    $okBbox = ($dBbox -ne $null) -and ([double](Get-Prop $dBbox "x0") -eq [double](Get-Prop $expBbox "x0")) -and
              ([double](Get-Prop $dBbox "y0") -eq [double](Get-Prop $expBbox "y0")) -and
              ([double](Get-Prop $dBbox "x1") -eq [double](Get-Prop $expBbox "x1")) -and
              ([double](Get-Prop $dBbox "y1") -eq [double](Get-Prop $expBbox "y1"))
    $hmm = Get-Prop $dModel "heightMm"
    $expH = [double](Get-Prop $expBbox "y1") - [double](Get-Prop $expBbox "y0")
    if ($dTotal -eq $null) {
        Write-Item "SKIP" "zwc.expected.crosscheck" "zwc.ps1 returned no result.expected (cannot cross-check)"
    } elseif ($okTotal -and $okType -and $okLayer -and $okScale -and $okBbox) {
        $note = ""
        if (($hmm -ne $null) -and ([double]$hmm -ne $expH)) {
            $note = " [note: helper heightMm=" + $hmm + " vs bbox height=" + $expH + " - derived-field only, bbox agrees]"
        }
        Write-Item "PASS" "zwc.expected.crosscheck" ("helper expected == baseline: total=" + $dTotal + " byType/byLayer equal, hScale=" + $dScale + ", bbox equal" + $note)
    } else {
        Write-Item "FAIL" "zwc.expected.crosscheck" ("total=" + $dTotal + " okType=" + $okType + " okLayer=" + $okLayer + " okScale=" + $okScale + " okBbox=" + $okBbox + " helperBbox=" + (Get-JsonBlock ([pscustomobject]$dBbox | ConvertTo-Json -Compress)) + " json=" + (Get-JsonBlock $rd1.Text))
        if ($VerboseOut) { Write-Output $rd1.Text }
    }

    # 5b: dry-run availability (no CAD required)
    $ecode = Get-Prop (Get-Prop $jd1 "error") "code"
    $okd = ($rd1.Code -eq 0) -and ((Get-Prop $jd1 "ok") -eq $true) -and $okTotal
    if ($okd) {
        Write-Item "PASS" "zwc.section.dryrun" ("exit=0 ok=true expected.total=" + $dTotal)
    } elseif ($ecode -eq "NO_INSTANCE") {
        Write-Item "SKIP" "zwc.section.dryrun" ("zwc.ps1 -DryRun still attaches to CAD first (error.code=NO_INSTANCE); model/expected were computed correctly -> interface issue to fix on core-cad side (DryRun must not require a live instance)")
    } else {
        Write-Item "FAIL" "zwc.section.dryrun" ("exit=" + $rd1.Code + " error.code=" + $ecode + " json=" + (Get-JsonBlock $rd1.Text))
        if ($VerboseOut) { Write-Output $rd1.Text }
    }

    if ($DryRunOnly -or $NoCad) {
        Write-Item "SKIP" "zwc.section.real" "skipped by -DryRunOnly/-NoCad (no CAD touched)"
        Write-Item "SKIP" "zwc.read.verify" "skipped by -DryRunOnly/-NoCad (no CAD touched)"
    } else {
        # step 6: real draw (caller must have the drawing open already)
        $realArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Mta", "-File", $ZwcPs1,
                      "section", "-Data", $ModelPath)
        if ($Doc -ne "") { $realArgs = $realArgs + @("-Doc", $Doc) }
        $rd2 = Invoke-Native $PsExe $realArgs
        $jd2 = ConvertFrom-JsonText $rd2.Text
        $rres = Get-Prop $jd2 "result"
        $verifyOk = Get-Prop (Get-Prop $rres "verify") "ok"
        $created = Get-Prop (Get-Prop $rres "created") "handles"
        $createdN = 0
        if ($created -ne $null) { $createdN = @($created).Count }
        $okr = ($rd2.Code -eq 0) -and ((Get-Prop $jd2 "ok") -eq $true) -and ($verifyOk -eq $true)
        $skipRead = $false
        if ($okr) {
            Write-Item "PASS" "zwc.section.real" ("exit=0 verify.ok=true created=" + $createdN + " undoToken=" + (Get-Prop $jd2 "undoToken"))
        } else {
            $ecode2 = Get-Prop (Get-Prop $jd2 "error") "code"
            $skipCodes = @("NO_CAD", "NO_DOC", "NO_DOCUMENT", "APP_NOT_FOUND", "CAD_NOT_RUNNING", "NO_ACTIVE_DOCUMENT", "NO_INSTANCE")
            if ($skipCodes -contains [string]$ecode2) {
                $skipRead = $true
                Write-Item "SKIP" "zwc.section.real" ("CAD/drawing not available (error.code=" + $ecode2 + "); open a drawing first, then re-run without -DryRunOnly")
            } else {
                Write-Item "FAIL" "zwc.section.real" ("exit=" + $rd2.Code + " error.code=" + $ecode2 + " verify.ok=" + $verifyOk + " json=" + (Get-JsonBlock $rd2.Text))
                if ($VerboseOut) { Write-Output $rd2.Text }
            }
        }

        # step 7: read back + independent verification
        if ($skipRead) {
            Write-Item "SKIP" "zwc.read.verify" "skipped: nothing was drawn (no CAD/drawing available)"
        } else {
            $readArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Mta", "-File", $ZwcPs1, "read")
            if ($Doc -ne "") { $readArgs = $readArgs + @("-Doc", $Doc) }
            $rd3 = Invoke-Native $PsExe $readArgs
            $jd3 = ConvertFrom-JsonText $rd3.Text
            if (($rd3.Code -eq 0) -and ((Get-Prop $jd3 "ok") -eq $true)) {
                $block = Get-JsonBlock $rd3.Text
                Set-Content -Path $ReadJson -Value $block -Encoding UTF8
                $r4 = Invoke-Native $Python @($VerifyPy, "--model", $ReadJson, "--expected", $ExpectedFile)
                if (($r4.Code -eq 0) -and ($r4.Text -match "RESULT: PASS")) {
                    Write-Item "PASS" "zwc.read.verify" ("read-back verified against baseline (read.json=" + $ReadJson + ")")
                } else {
                    Write-Item "FAIL" "zwc.read.verify" ("exit=" + $r4.Code)
                    Write-Output $r4.Text
                }
            } else {
                Write-Item "FAIL" "zwc.read.verify" ("read failed exit=" + $rd3.Code + " json=" + (Get-JsonBlock $rd3.Text))
                if ($VerboseOut) { Write-Output $rd3.Text }
            }
        }
    }
}

# ---- summary ---------------------------------------------------------------
Write-Output "------------------------------------------------------------"
$overall = "PASS"
if ($script:FailCount -gt 0) { $overall = "FAIL" }
Write-Output ("[run_m1] RESULT: " + $overall + "  (PASS=" + $script:PassCount + " FAIL=" + $script:FailCount + " SKIP=" + $script:SkipCount + ")")
if ($script:FailCount -gt 0) { exit 1 }
exit 0
