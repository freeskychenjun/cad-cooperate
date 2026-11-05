// Procs.cs - process enumeration (the finish command reports it; acceptance requires that
// no CAD process is left behind after a run).
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). The class
// body, its filters and its messages are byte-identical to the pre-split file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace ZwcHelper
{
    // ======================================================================================
    // Process enumeration (finish reports it; acceptance requires 0 zwcad left behind)
    // ======================================================================================
    internal static class Procs
    {
        public static List<object> Zwcad()
        {
            List<object> found = new List<object>();
            try
            {
                Process[] all = Process.GetProcesses();
                foreach (Process p in all)
                {
                    try
                    {
                        string nm = p.ProcessName;
                        if (nm != null && nm.ToLowerInvariant().IndexOf("zwcad", StringComparison.Ordinal) >= 0)
                        {
                            JMap m = new JMap();
                            m.Add("name", nm);
                            m.Add("pid", p.Id);
                            found.Add(m);
                        }
                    }
                    catch (Exception) { }
                    finally { try { p.Dispose(); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
            return found;
        }

        public static int ZwcadCount() { return Zwcad().Count; }

        /// <summary>Processes of the product family we drive: "acad" for AutoCAD, "zwcad" for ZWCAD.
        /// "any"/empty counts BOTH families (that is what `-App any` means once a request never named a
        /// product, and what `finish` must sweep then).</summary>
        public static List<object> Family(string kind)
        {
            bool any = string.IsNullOrEmpty(kind) || kind.Trim().Length == 0
                || kind.Trim().Equals("any", StringComparison.OrdinalIgnoreCase)
                || kind.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            bool acad = !any && (kind ?? "").IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
            string needle = any ? null : (acad ? "acad" : "zwcad");
            List<object> found = new List<object>();
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string nm = p.ProcessName;
                        string low = nm == null ? "" : nm.ToLowerInvariant();
                        bool hit = needle == null
                            ? (low.Contains("acad") || low.Contains("zwcad"))
                            : low.Contains(needle);
                        if (hit)
                        {
                            JMap m = new JMap();
                            m.Add("name", nm);
                            m.Add("pid", p.Id);
                            found.Add(m);
                        }
                    }
                    catch (Exception) { }
                    finally { try { p.Dispose(); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
            return found;
        }


        /// <summary>Hidden automation instances of the product family (MainWindowHandle == 0 and a
        /// command line that asks for /Automation or -Embedding). Used by `finish -SweepHidden`.</summary>
        internal static List<object> HiddenAutomation(string kind)
        {
            List<object> list = new List<object>();
            bool any = string.IsNullOrEmpty(kind) || kind.Trim().Length == 0
                || kind.Trim().Equals("any", StringComparison.OrdinalIgnoreCase)
                || kind.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            bool acad = !any && (kind ?? "").IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
            string needle = any ? null : (acad ? "acad" : "zwcad");
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process"))
                {
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            string cmdline = mo["CommandLine"] as string;
                            string exePath = mo["ExecutablePath"] as string;
                            int pid = Convert.ToInt32(mo["ProcessId"], CultureInfo.InvariantCulture);
                            if (cmdline == null) continue;
                            bool cmdHit = needle == null
                                ? (cmdline.IndexOf("acad", StringComparison.OrdinalIgnoreCase) >= 0
                                   || cmdline.IndexOf("zwcad", StringComparison.OrdinalIgnoreCase) >= 0)
                                : cmdline.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!cmdHit) continue;
                            bool automation = cmdline.IndexOf("Automation", StringComparison.OrdinalIgnoreCase) >= 0
                                || cmdline.IndexOf("-Embedding", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!automation) continue;
                            bool hidden = false;
                            try { using (Process p2 = Process.GetProcessById(pid)) { hidden = p2.MainWindowHandle == IntPtr.Zero; } }
                            catch (Exception) { continue; }
                            if (!hidden) continue;
                            JMap m = new JMap();
                            m.Add("pid", pid);
                            m.Add("exePath", exePath);
                            m.Add("commandLine", cmdline);
                            m.Add("hidden", true);
                            list.Add(m);
                        }
                        catch (Exception) { }
                        finally { mo.Dispose(); }
                    }
                }
            }
            catch (Exception) { }
            return list;
        }
        public static int FamilyCount(string kind) { return Family(kind).Count; }
    }
}
