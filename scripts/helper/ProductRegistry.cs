// ProductRegistry.cs - M4/AutoCAD: discover the AutoCAD builds that are actually installed.
//
// Before this file KnownProducts("autocad") returned exactly ONE hardcoded entry
// (AutoCAD.Application.17 / "AutoCAD 2007"). On a machine with several AutoCAD releases that had
// three consequences, all measured on 2026-11-05 with AutoCAD 2007 + 2021 installed:
//   * `-App autocad` without -ProgId answered NO_INSTANCE even though AutoCAD 2021 was running,
//     because only .17 was ever tried (ROT lookup for the 2007 CLSID cannot find the 2021 instance);
//   * with -Launch the same list would have STARTED AutoCAD 2007 (135 s cold start) instead of
//     attaching to the running 2021;
//   * every response labelled the attached 2021 as "AutoCAD 2007", because the label was hardcoded
//     next to the ProgID.
//
// So the list is built from the registry instead: every "AutoCAD.Application.NN" key that is
// registered becomes one product, its CLSID gives us the executable (LocalServer32) and the
// executable's folder name gives the user-facing label ("...\AutoCAD 2021\acad.exe" -> "AutoCAD
// 2021"). No release-number table is hardcoded, so 2022/2023/... work without a code change.
//
// Registry access is best effort: a locked/absent key must never fail a command, it only makes the
// list shorter (the caller keeps the documented 2007 fallback).
//
// Note: the unversioned ProgID "AutoCAD.Application" is NOT trusted for identification. On this
// machine it still points at the AutoCAD 2007 CLSID after AutoCAD 2021 was installed, so it is only
// used as a last-resort attach candidate (see Cad.UnversionedProgId).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZwcHelper
{
    /// <summary>One installed AutoCAD build: the COM identity plus the information needed to talk
    /// about it to the user.</summary>
    internal sealed class RegProduct
    {
        public string Label;      // "AutoCAD 2021" (folder name of acad.exe, else "AutoCAD <NN>")
        public string ProgId;     // "AutoCAD.Application.24"
        public string Clsid;      // lowercase, no braces
        public string ExePath;    // "C:\Program Files\Autodesk\AutoCAD 2021\acad.exe" (may be null)
        public int Version;       // 24
    }

    internal static class ProductRegistry
    {
        private const string ProgIdRoot = "AutoCAD.Application";

        /// <summary>Installed AutoCAD builds, newest COM version first. Empty when the registry
        /// cannot be read at all (caller falls back to the documented default).</summary>
        public static List<RegProduct> AutoCadProducts()
        {
            SortedDictionary<int, RegProduct> byVersion = new SortedDictionary<int, RegProduct>();
            string[] subs = null;
            try { using (RegistryKey k = Registry.ClassesRoot) { if (k != null) subs = k.GetSubKeyNames(); } }
            catch (Exception) { }
            if (subs != null)
            {
                for (int i = 0; i < subs.Length; i++)
                {
                    string name = subs[i];
                    if (name == null) continue;
                    if (!name.StartsWith(ProgIdRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    // Official mapping (measured against Autodesk's ACADVER table): the ProgID suffix is
                    // ACADVER without the trailing ".0", so 2021 = ".24", 2022 = ".24.1", 2023 = ".24.2",
                    // 2024 = ".24.3", 2025 = ".25". One NN therefore covers several years and only the
                    // three-part form pins the year, so both shapes are enumerated (24 -> 240, 24.1 -> 241).
                    string suffix = name.Substring(ProgIdRoot.Length).TrimStart('.');
                    if (suffix.Length == 0) continue;                                // skip unversioned
                    int ver;
                    if (!ParseProgIdVersion(suffix, out ver)) continue;
                    if (ver < 100) continue;
                    if (byVersion.ContainsKey(ver)) continue;
                    RegProduct p = new RegProduct();
                    p.ProgId = name;
                    p.Version = ver;
                    p.Clsid = ClsidOfProgId(name);
                    p.ExePath = ExeOfClsid(p.Clsid);
                    p.Label = LabelFromExe(p.ExePath);
                    if (string.IsNullOrEmpty(p.Label)) p.Label = "AutoCAD " + ver.ToString(CultureInfo.InvariantCulture);
                    byVersion[ver] = p;
                }
            }
            List<RegProduct> list = new List<RegProduct>();
            foreach (KeyValuePair<int, RegProduct> kv in byVersion) list.Add(kv.Value);
            list.Reverse();   // newest first: the best default when nothing is running
            return list;
        }

        /// <summary>CLSID registered for a ProgID, lowercase without braces, or null.</summary>
        public static string ClsidOfProgId(string progId)
        {
            if (string.IsNullOrEmpty(progId)) return null;
            try
            {
                using (RegistryKey k = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID"))
                {
                    if (k == null) return null;
                    return NormalizeClsid(Convert.ToString(k.GetValue(null), CultureInfo.InvariantCulture));
                }
            }
            catch (Exception) { }
            // Type.GetTypeFromProgID also resolves it, but it is slower and needs the typelib to load
            return null;
        }

        /// <summary>Executable behind a CLSID (LocalServer32), looking at the 64-bit AND the 32-bit
        /// registry view: the installed AutoCAD 2007 registers LocalServer32 only under WOW6432Node
        /// while a 64-bit helper reads the native view, so one view alone is not enough.</summary>
        public static string ExeOfClsid(string clsid)
        {
            string c = NormalizeClsid(clsid);
            if (c == null) return null;
            string key = "CLSID\\{" + c + "}\\LocalServer32";
            string[] roots = new string[] { "HKCR", "HKCR32" };
            for (int i = 0; i < roots.Length; i++)
            {
                try
                {
                    RegistryKey baseKey = roots[i] == "HKCR"
                        ? RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64)
                        : RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                    using (baseKey)
                    {
                        using (RegistryKey k = baseKey.OpenSubKey(key))
                        {
                            if (k == null) continue;
                            string value = Convert.ToString(k.GetValue(null), CultureInfo.InvariantCulture);
                            string exe = PathFromCommand(value);
                            if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe)) return exe;
                            if (!string.IsNullOrEmpty(exe)) return exe;
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>User-facing label from an acad.exe path: "...\\AutoCAD 2021\\acad.exe" ->
        /// "AutoCAD 2021". Falls back to a year inside the path, else null (caller decides).</summary>
        public static string LabelFromExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            Match m = Regex.Match(exePath, @"AutoCAD[ _-]*(\d{4})", RegexOptions.IgnoreCase);
            if (m.Success) return "AutoCAD " + m.Groups[1].Value;
            return null;
        }

        /// <summary>CLSID -> label for every installed build (used by migration of old responses that
        /// report product names from a CLSID).</summary>
        public static string LabelFromClsid(string clsid)
        {
            string c = NormalizeClsid(clsid);
            if (c == null) return null;
            List<RegProduct> list = AutoCadProducts();
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Clsid, c, StringComparison.OrdinalIgnoreCase)) return list[i].Label;
            return null;
        }

        /// <summary>"24" -> 240, "24.1" -> 241, anything else -> false. Sorted numerically, so 2024
        /// (.24.3 = 243) sorts after 2021 (.24 = 240).</summary>
        public static bool ParseProgIdVersion(string suffix, out int version)
        {
            version = 0;
            if (string.IsNullOrEmpty(suffix)) return false;
            string[] parts = suffix.Split('.');
            int major, minor = 0;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major)) return false;
            if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor)) return false;
            if (parts.Length > 2) return false;
            if (major < 10 || minor < 0 || minor > 9) return false;
            version = major * 10 + minor;
            return true;
        }

        public static string NormalizeClsid(string clsid)
        {
            if (string.IsNullOrEmpty(clsid)) return null;
            string c = clsid.Trim();
            if (c.Length == 0) return null;
            return c.Trim('{', '}').ToLowerInvariant();
        }

        /// <summary>First token of a LocalServer32 value: the value is usually a quoted path, often
        /// followed by arguments ("\"C:\...\acad.exe\" /Automation").</summary>
        private static string PathFromCommand(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string v = value.Trim();
            if (v.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = v.IndexOf('"', 1);
                return end > 1 ? v.Substring(1, end - 1) : v.Trim('"');
            }
            Match m = Regex.Match(v, @"^(.*?\.exe)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            int sp = v.IndexOf(' ');
            return sp > 0 ? v.Substring(0, sp) : v;
        }
    }
}
