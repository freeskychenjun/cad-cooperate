// Trust.cs - AutoCAD SECURELOAD gate for the LISP-file fast path.
//
// Measured on 2026-11-05 with AutoCAD 2021 (SECURELOAD=1, TRUSTEDPATHS empty):
//   the LISP fast path injects `(load "<%TEMP%>\cad-cooperate\geom-xxxx.lsp")`, and AutoCAD
//   answered with the modal dialog "Security - Unsigned Executable File" (buttons Always Load /
//   Load Once / Do Not Load). Autodesk documents SECURELOAD as covering LSP/FAS/VLX/MNL, so this is
//   by design and not a bug in the script. The dialog blocks the whole application: COM answers
//   with an empty Documents collection, every later command hangs, and the user sees a popup
//   "out of nowhere". ZWCAD has no such gate, so this only matters for -App autocad.
//
// Two independent defences, in this order:
//   1. preflight (LispTrust.CanInject): SECURELOAD=1 and the temp dir not inside TRUSTEDPATHS means
//      "do not inject" - the caller falls back to COM and reports LISP_TRUST_BLOCKED with the
//      one-time fix. No dialog can appear because no file is loaded.
//   2. dialog guard (DialogGuard): when the caller insists (-ForceLispInject, or the trust state
//      could not be read) a watchdog thread watches for the security dialog and clicks
//      "Do Not Load", so an unexpected prompt is cancelled within ~100 ms instead of blocking the
//      user's CAD. The caller then reports the same LISP_TRUST_BLOCKED.
//
// The fix offered to the user is the documented one: add the temp directory to TRUSTEDPATHS
// (`trust -Action addTemp`) or click "Always Load" once. SECURELOAD=0 is deliberately NOT offered:
// it is persisted in the registry and downgrades security everywhere, not just for this tool.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CadHelper
{
    /// <summary>SECURELOAD / TRUSTEDPATHS state of one attached CAD document, plus the path we load
    /// LISP files from.</summary>
    internal sealed class LispTrust
    {
        public int SecureLoad = -1;          // -1 = could not be read
        public string TrustedPaths = "";
        public string TempDir = "";
        public bool TempTrusted;
        public bool IsAutoCad;

        /// <summary>True when (as far as we can tell) loading a .lsp from TempDir cannot prompt.
        /// force bypasses the check (the user may have clicked "Always Load", which is recorded
        /// elsewhere in the registry, not in TRUSTEDPATHS).</summary>
        public bool CanInject(bool force)
        {
            if (!IsAutoCad) return true;     // ZWCAD has no SECURELOAD gate
            if (force) return true;
            if (SecureLoad != 1) return true; // 0 = legacy, 2 = refuse silently, unreadable = try
            return TempTrusted;
        }

        public JMap ToMap()
        {
            JMap m = new JMap();
            m.Add("secureLoad", SecureLoad);
            m.Add("trustedPaths", TrustedPaths);
            m.Add("tempDir", TempDir);
            m.Add("tempTrusted", TempTrusted);
            m.Add("isAutoCad", IsAutoCad);
            m.Add("canInject", CanInject(false));
            m.Add("fix", TempTrusted ? null : "add the temp dir to the trusted locations once: trust -Action addTemp (writes TRUSTEDPATHS), or click 'Always Load' in the AutoCAD prompt");
            return m;
        }

        /// <summary>Short one-line explanation for warning messages: which gate, and the one-time
        /// fix without dumping the whole state on every read.</summary>
        public string FastPathHint()
        {
            return "SECURELOAD=" + SecureLoad.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", trusted=" + (TempTrusted ? "yes" : "no")
                + ", fix=one-off `trust -Action addTemp`";
        }

        public static LispTrust Read(object doc, bool autoCad, Ctx ctx)
        {
            LispTrust t = new LispTrust();
            t.IsAutoCad = autoCad;
            t.TempDir = State.Dir();
            if (doc != null)
            {
                try
                {
                    string sl = Cmd.GetVariable(doc, "SECURELOAD");
                    int v;
                    if (sl != null && int.TryParse(sl.Trim(), out v)) t.SecureLoad = v;
                }
                catch (Exception e) { ctx.Warn("TRUST_READ_FAILED", "SECURELOAD could not be read: " + Com.Hr(e)); }
                try
                {
                    string tp = Cmd.GetVariable(doc, "TRUSTEDPATHS");
                    t.TrustedPaths = tp == null ? "" : tp;
                }
                catch (Exception e) { ctx.Warn("TRUST_READ_FAILED", "TRUSTEDPATHS could not be read: " + Com.Hr(e)); }
            }
            t.TempTrusted = IsTrusted(t.TrustedPaths, t.TempDir);
            return t;
        }

        /// <summary>True when dir is one of the ';'-separated TRUSTEDPATHS entries (quotes and a
        /// trailing separator are ignored; "\\..." means recursive, i.e. the directory itself).</summary>
        public static bool IsTrusted(string trustedPaths, string dir)
        {
            if (string.IsNullOrEmpty(trustedPaths) || string.IsNullOrEmpty(dir)) return false;
            string want = NormalizePath(dir);
            string[] parts = trustedPaths.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i] == null ? "" : parts[i].Trim();
                if (raw.Length == 0) continue;
                raw = raw.Trim('"').Trim();
                if (raw.StartsWith("\\", StringComparison.Ordinal)) raw = raw.Substring(1);   // recursive marker
                string norm = NormalizePath(Environment.ExpandEnvironmentVariables(raw));
                if (norm.Length == 0) continue;
                if (string.Equals(norm, want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Trim().Trim('"');
            if (s.Length == 0) return "";
            try { s = System.IO.Path.GetFullPath(s); } catch (Exception) { }
            s = s.Replace('/', '\\');
            while (s.Length > 3 && s.EndsWith("\\", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>TRUSTEDPATHS with dir appended (idempotent). Registries are edited by AutoCAD when
        /// the variable is changed, so there is nothing to write here.</summary>
        public static string AddPath(string trustedPaths, string dir)
        {
            if (IsTrusted(trustedPaths, dir)) return trustedPaths;
            string add = NormalizePath(dir);
            if (add.Length == 0) return trustedPaths;
            string current = trustedPaths == null ? "" : trustedPaths.Trim();
            while (current.EndsWith(";", StringComparison.Ordinal)) current = current.Substring(0, current.Length - 1);
            return current.Length == 0 ? add : current + ";" + add;
        }

        /// <summary>TRUSTEDPATHS with dir removed (idempotent, case-insensitive).</summary>
        public static string RemovePath(string trustedPaths, string dir)
        {
            if (string.IsNullOrEmpty(trustedPaths)) return trustedPaths;
            string want = NormalizePath(dir);
            StringBuilder sb = new StringBuilder();
            string[] parts = trustedPaths.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i] == null ? "" : parts[i].Trim();
                if (raw.Length == 0) continue;
                string probe = raw.Trim('"').Trim();
                if (probe.StartsWith("\\", StringComparison.Ordinal)) probe = probe.Substring(1);
                if (string.Equals(NormalizePath(Environment.ExpandEnvironmentVariables(probe)), want, StringComparison.OrdinalIgnoreCase)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(raw);
            }
            return sb.ToString();
        }
    }

    /// <summary>The single entry point every LISP injection goes through: preflight the AutoCAD
    /// SECURELOAD gate, arm the dialog watchdog, run the pipeline, and translate a guard hit into the
    /// documented LISP_TRUST_BLOCKED error. LispFile.Scan / LispGeom.Scan call this instead of
    /// LispScan.Run directly so the pipeline itself stays BCL-only and offline-testable.</summary>
    internal static class LispGate
    {
        internal static LispScan.Result Run(object doc, Ctx ctx, LispScan.Request req, out JMap meta, out string phase)
        {
            meta = new JMap();
            phase = LispSentinelPhase.NotStarted;

            string product = ctx.AppMap == null ? "" : ctx.AppMap.Str("product", "");
            bool autoCad = product.IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
            LispTrust trust = LispTrust.Read(doc, autoCad, ctx);
            if (!trust.CanInject(ctx.ForceLispInject))
            {
                throw new CadError("LISP_TRUST_BLOCKED",
                    "AutoCAD SECURELOAD=" + trust.SecureLoad.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " would show a security prompt for the LISP file in " + trust.TempDir
                    + ", so the fast path was skipped (nothing was injected)",
                    "one-time fix: `trust -Action addTemp` (adds the temp dir to TRUSTEDPATHS) or click 'Always Load' once; -ForceLispInject overrides the preflight",
                    Json.Write(trust.ToMap()));
            }

            int pid = ctx.AppMap == null ? 0 : ctx.AppMap.Int("pid", 0);
            DialogGuard guard = null;
            if (autoCad && pid > 0)
            {
                guard = new DialogGuard();
                guard.Start(pid, Math.Max(2000, req.TimeoutMs) + 3000);
                DialogGuard armed = guard;
                req.AbortProbe = delegate { return armed.Hit; };
            }
            try
            {
                return LispScan.Run(doc, ctx, req, out meta, out phase);
            }
            catch (CadError ze)
            {
                if (ze.Code == "LISP_ABORTED" && guard != null && guard.Hit)
                {
                    throw new CadError("LISP_TRUST_BLOCKED",
                        "AutoCAD's SECURELOAD prompt appeared while loading the LISP dump and was cancelled automatically",
                        "one-time fix: `trust -Action addTemp` (adds the temp dir to TRUSTEDPATHS), then retry; -ForceLispInject skips the preflight",
                        guard.Detail);
                }
                throw;
            }
            finally
            {
                if (guard != null) guard.Stop();
            }
        }
    }

    /// <summary>Watches for AutoCAD's "Security - Unsigned Executable File" dialog and cancels it
    /// (clicks "Do Not Load"), so a surprise prompt can never leave the user's CAD blocked.</summary>
    internal sealed class DialogGuard : IDisposable
    {
        private const int WM_CLOSE = 0x0010;
        private const int BM_CLICK = 0x00F5;

        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _hit;
        private volatile string _detail;
        private int _pid;

        public bool Hit { get { return _hit; } }
        public string Detail { get { return _detail; } }

        public void Start(int pid, int timeoutMs)
        {
            if (pid <= 0) return;
            _pid = pid;
            ThreadStart body = delegate { Loop(timeoutMs); };
            _thread = new Thread(body);
            _thread.IsBackground = true;
            _thread.Name = "cad-dialog-guard";
            _thread.Start();
        }

        private void Loop(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, timeoutMs));
            while (!_stop && DateTime.UtcNow < deadline)
            {
                try { CancelOnce(); }
                catch (Exception) { }
                Thread.Sleep(80);
            }
        }

        private void CancelOnce()
        {
            IntPtr dlg = FindSecurityDialog(_pid);
            if (dlg == IntPtr.Zero) return;
            string text = DialogText(dlg);
            IntPtr btn = FindButton(dlg, new string[] { "\u4E0D\u52A0\u8F7D", "Do Not Load" });
            if (btn != IntPtr.Zero) NativeMethods.PostMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            else NativeMethods.PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_hit) _detail = text;
            _hit = true;
        }

        public void Stop()
        {
            _stop = true;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(1500); } catch (Exception) { }
        }

        public void Dispose() { Stop(); }

        // ---------------- window helpers (best effort; every failure just means "no dialog seen")
        private static IntPtr FindSecurityDialog(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                int wpid;
                NativeMethods.GetWindowThreadProcessId(h, out wpid);
                if (wpid != pid) return true;
                if (!NativeMethods.IsWindowVisible(h)) return true;
                StringBuilder cls = new StringBuilder(64);
                NativeMethods.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                StringBuilder title = new StringBuilder(256);
                NativeMethods.GetWindowText(h, title, title.Capacity);
                string t = title.ToString();
                if (!IsSecurityTitle(t)) return true;
                found = h;
                return false;
            };
            NativeMethods.EnumWindows(cb, IntPtr.Zero);
            return found;
        }

        private static bool IsSecurityTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            // "Security - Unsigned Executable File" and its localized (Chinese) form, plus the
            // "signed but unverified" variants: all of them start with Security or the Chinese words.
            if (title.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (title.IndexOf("\u5B89\u5168", StringComparison.Ordinal) >= 0) return true;   // "security" (zh)
            if (title.IndexOf("\u7B7E\u540D", StringComparison.Ordinal) >= 0) return true;   // "signature" (zh)
            return false;
        }

        private static IntPtr FindButton(IntPtr dlg, string[] texts)
        {
            IntPtr found = IntPtr.Zero;
            EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                StringBuilder cls = new StringBuilder(64);
                NativeMethods.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "Button") return true;
                StringBuilder text = new StringBuilder(256);
                NativeMethods.GetWindowText(h, text, text.Capacity);
                string t = text.ToString().Trim();
                for (int i = 0; i < texts.Length; i++)
                {
                    if (t.IndexOf(texts[i], StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
                }
                return true;
            };
            NativeMethods.EnumChildWindows(dlg, cb, IntPtr.Zero);
            return found;
        }

        private static string DialogText(IntPtr dlg)
        {
            StringBuilder all = new StringBuilder();
            EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                StringBuilder text = new StringBuilder(512);
                NativeMethods.GetWindowText(h, text, text.Capacity);
                string t = text.ToString().Trim();
                if (t.Length > 0) { if (all.Length > 0) all.Append(" | "); all.Append(t); }
                return true;
            };
            NativeMethods.EnumChildWindows(dlg, cb, IntPtr.Zero);
            return all.ToString();
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        }
    }
}

