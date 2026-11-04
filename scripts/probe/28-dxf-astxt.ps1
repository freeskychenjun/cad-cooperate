# 28-dxf-astxt.ps1 - SaveAs DXF2007 into a .txt extension so DGS does not encrypt it, then reconcile (pure ASCII)
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib-com.ps1"
function PollVar($doc, [string]$name, [int]$timeoutMs = 30000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { $v = $doc.GetLispVariable($name); if ($v -ne $false -and $null -ne $v) { return @{ ok = $true; v = $v } }; Start-Sleep -Milliseconds 20 }
    return @{ ok = $false; v = $null }
}
function Head([string]$p) {
    if (-not (Test-Path $p)) { return "MISSING" }
    $fs = [System.IO.File]::OpenRead($p); $buf = New-Object byte[] 24; $n = $fs.Read($buf, 0, 24); $fs.Close()
    $hex = (($buf[0..15] | ForEach-Object { '{0:X2}' -f $_ }) -join '')
    $asc = ([System.Text.Encoding]::ASCII.GetString($buf, 0, $n)) -replace '[^\x20-\x7E]', '.'
    $tag = if ($hex.StartsWith('17DA5FA01633CD9A')) { 'DGS_ENCRYPTED' } else { 'plaintext' }
    "$([System.IO.Path]::GetFileName($p)) bytes=$((Get-Item $p).Length) $tag asc=[$asc]"
}
$dir = $script:ProbeTmp
Get-ChildItem $dir -Filter 'dxf_as*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$app = New-Object -ComObject 'ZWCAD.Application'
"createMs=$($sw.ElapsedMilliseconds)"
$doc = $app.ActiveDocument
$ms = $doc.ModelSpace
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)

# known mix: 100 lines + 2 circles + 1 polyline + 1 text + 1 mtext = 105
$p1 = [double[]]@(0.0, 0.0, 0.0); $p2 = [double[]]@(10.0, 0.0, 0.0)
for ($i = 0; $i -lt 100; $i++) { $p1[0] = [double]$i; $p2[0] = [double]($i + 1); $null = $ms.AddLine($p1, $p2) }
$c = [double[]]@(5.0, 5.0, 0.0); $null = $ms.AddCircle($c, 2.0); $null = $ms.AddCircle($c, 3.0)
$pts = [double[]]@(0.0, 0.0, 5.0, 0.0, 5.0, 5.0); $null = $ms.AddLightWeightPolyline($pts)
$ip = [double[]]@(0.0, 40.0, 0.0); $null = $ms.AddText('recon-text', $ip, 2.0); $null = $ms.AddMText($ip, 30.0, 'recon-mtext')
$comCount = $doc.ModelSpace.Count
"comCount=$comCount"

# SaveAs with DXF version code 37 but a .txt extension -> content is DXF text, DGS should leave it plain
$p = Join-Path $dir 'dxf_as_txt.txt'
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
try { $doc.SaveAs($p, 37); "SaveAs37-to-txt OK ms=$($sw2.ElapsedMilliseconds)" } catch { "SaveAs ERR: $($_.Exception.Message)" }
"header: $(Head $p)"

# also control: same SaveAs with .dxf extension
$p2f = Join-Path $dir 'dxf_as_dxf.dxf'
try { $doc.SaveAs($p2f, 37); "SaveAs37-to-dxf OK" } catch { "SaveAs-dxf ERR: $($_.Exception.Message)" }
"header: $(Head $p2f)"

try { $doc.Close($false) } catch {}
Start-Sleep -Milliseconds 300
try { $app.Quit() } catch {}
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw2.ElapsedMilliseconds -lt 20000) { if (@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count -eq 0) { break }; Start-Sleep -Milliseconds 100 }
"exitMs=$($sw2.ElapsedMilliseconds) leftover=$(@(Get-Process zwcad* -ErrorAction SilentlyContinue).Count)"
