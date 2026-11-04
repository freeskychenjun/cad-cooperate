# lib-com.ps1 - shared COM probe helpers (pure ASCII). Dot-source: . .\lib-com.ps1
$script:ProbeTmp = Join-Path $env:TEMP 'zwprobe'
if (-not (Test-Path $script:ProbeTmp)) { New-Item -ItemType Directory -Force -Path $script:ProbeTmp | Out-Null }

if (-not ('ComProbe' -as [type])) {
$cs = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDispatchProbe
{
    int GetTypeInfoCount(out uint pctinfo);
    int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
    int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames, uint cNames, uint lcid, [MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);
    int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
}

public static class ComProbe
{
    [DllImport("oleaut32.dll", PreserveSig = false, CharSet = CharSet.Unicode)]
    static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll", PreserveSig = false, CharSet = CharSet.Unicode)]
    static extern void CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static object GetActive(string progId)
    {
        Guid clsid;
        CLSIDFromProgID(progId, out clsid);
        object o;
        GetActiveObject(ref clsid, IntPtr.Zero, out o);
        return o;
    }

    public static int DispId(object o, string name)
    {
        try
        {
            IDispatchProbe d = (IDispatchProbe)o;
            Guid iid = Guid.Empty;
            string[] names = new string[] { name };
            int[] ids = new int[] { -1 };
            int hr = d.GetIDsOfNames(ref iid, names, 1, 0x0400, ids);
            if (hr != 0) return -1;
            return ids[0];
        }
        catch { return -1; }
    }

    public static int[] DispIds(object o, string[] names)
    {
        int[] r = new int[names.Length];
        for (int i = 0; i < names.Length; i++) r[i] = DispId(o, names[i]);
        return r;
    }

    public static string[] WindowsOfProcess(int pid)
    {
        List<string> res = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid)
            {
                StringBuilder t = new StringBuilder(512);
                GetWindowTextW(h, t, 512);
                StringBuilder c = new StringBuilder(256);
                GetClassNameW(h, c, 256);
                res.Add("hwnd=0x" + h.ToInt64().ToString("X") + " visible=" + IsWindowVisible(h) + " class=" + c.ToString() + " title=" + t.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return res.ToArray();
    }
}
'@
Add-Type -TypeDefinition $cs -Language CSharp
}

function Get-ActiveApp { param([string]$ProgId = 'ZWCAD.Application') [ComProbe]::GetActive($ProgId) }
function Has-Member { param($Obj, [string]$Name) ([ComProbe]::DispId($Obj, $Name) -ne -1) }
function Get-MemberIds { param($Obj, [string[]]$Names) [ComProbe]::DispIds($Obj, $Names) }

function Send-Cmd {
    param($Doc, [string]$Cmd, [string]$Eol = "`n")
    $Doc.SendCommand($Cmd + $Eol)
}

function Clear-ProbeOut {
    Get-ChildItem $script:ProbeTmp -Filter '*.out' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

function Wait-ProbeFile {
    param([string]$Name, [int]$TimeoutMs = 60000)
    $p = Join-Path $script:ProbeTmp $Name
    if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-Path $p) {
            $fi = Get-Item $p -ErrorAction SilentlyContinue
            if ($fi -and $fi.Length -gt 0) { $sw.Stop(); return @{ ok = $true; ms = $sw.ElapsedMilliseconds; path = $p } }
        }
        Start-Sleep -Milliseconds 50
    }
    $sw.Stop()
    return @{ ok = $false; ms = $sw.ElapsedMilliseconds; path = $p }
}

function Get-ProbeFile { param([string]$Name) $p = Join-Path $script:ProbeTmp $Name; if (Test-Path $p) { Get-Content $p } else { @("<missing $Name>") } }

function ZProcs {
    $z = Get-Process zwcad* -ErrorAction SilentlyContinue
    if ($z) { return $z } else { return @() }
}
