# 00-env.ps1 - read-only environment facts (pure ASCII)
$ErrorActionPreference = 'Continue'
$out = @()
function L($s) { $script:out += $s }

$exe = 'C:\Program Files (x86)\ZWCAD Classic Chs\ZWCAD.EXE'
L "PSVersion=$($PSVersionTable.PSVersion)  Is64BitProcess=$([Environment]::Is64BitProcess)"
L "ExeExists=$(Test-Path $exe)"
if (Test-Path $exe) {
    $vi = (Get-Item $exe).VersionInfo
    L "FileVersion=$($vi.FileVersion) ProductVersion=$($vi.ProductVersion) FileDesc=$($vi.FileDescription)"
    # PE machine type
    $fs = [System.IO.File]::OpenRead($exe)
    $br = New-Object System.IO.BinaryReader($fs)
    $fs.Position = 0x3C
    $peOff = $br.ReadInt32()
    $fs.Position = $peOff + 4
    $machine = $br.ReadUInt16()
    $br.Close(); $fs.Close()
    L ("PE machine=0x{0:X4} ({1})" -f $machine, $(if ($machine -eq 0x14c) {'x86'} elseif ($machine -eq 0x8664) {'x64'} else {'?'}))
}

L "=== COM registration ==="
foreach ($pid2 in @('ZWCAD.Application','ZWCAD.Application.16','ZWCAD.Drawing','ZWCAD.Drawing.16')) {
    $k = "Registry::HKEY_CLASSES_ROOT\$pid2\CLSID"
    if (Test-Path $k) { L "$pid2 CLSID=$((Get-ItemProperty $k).'(default)')" } else { L "$pid2 <not registered>" }
}
foreach ($clsid in @('{FB081ED6-0D7E-489F-A6B7-F7CEAEC10661}')) {
    $k = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid"
    if (Test-Path $k) {
        L "CLSID $clsid default=$((Get-ItemProperty $k).'(default)')"
        foreach ($sub in @('LocalServer32','InprocServer32','ProgID','TypeLib','VersionIndependentProgID')) {
            $kk = "$k\$sub"
            if (Test-Path $kk) { L ("  {0} = {1}" -f $sub, (Get-ItemProperty $kk).'(default)') }
        }
    } else { L "CLSID $clsid <missing>" }
}

L "=== TypeLib / version ==="
$tl = 'Registry::HKEY_CLASSES_ROOT\TypeLib'
if (Test-Path $tl) {
    Get-ChildItem $tl -ErrorAction SilentlyContinue | ForEach-Object {
        $g = $_.PSChildName
        if ($g -match 'ZWCAD|ZWSOFT|ZCAD') { L "TypeLib $g" }
    }
}

L "=== license / dongle artifacts ==="
$dir = 'C:\Program Files (x86)\ZWCAD Classic Chs'
foreach ($f in @('LocalTrial_ZWCADCLASSIC.asr','Rockey4ND.dll','zwlm_ts.exe','zwco.exe','zwco.ini')) {
    $p = Join-Path $dir $f
    L "$f exists=$(Test-Path $p)"
}
$licDirs = @("$env:ProgramData\ZWSOFT", "$env:ProgramData\ZWCAD", "$env:APPDATA\ZWSOFT", "$env:LOCALAPPDATA\ZWSOFT", "$env:APPDATA\ZWCAD")
foreach ($d in $licDirs) { if (Test-Path $d) { L "DIR $d :"; Get-ChildItem $d -Force -ErrorAction SilentlyContinue | Select-Object -First 20 | ForEach-Object { L "   $($_.Name) $($_.Length)" } } }

L "=== license service / driver ==="
Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'rockey|zw|rainbow|sense' } | ForEach-Object { L "SVC $($_.Name) $($_.Status)" }
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'rockey|zw|hardlock|hasplms' } | ForEach-Object { L "PROC $($_.Id) $($_.ProcessName)" }

L "=== running zwcad ==="
$z = Get-Process zwcad* -ErrorAction SilentlyContinue
if ($z) { $z | ForEach-Object { L "RUN $($_.Id) $($_.ProcessName)" } } else { L "RUN none" }

$out | ForEach-Object { $_ }
