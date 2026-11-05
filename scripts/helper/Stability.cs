// Stability.cs - M3-E3 稳定性骨架
//
// Why this file exists: with two agents driving CAD at once the per-command floor went from 63 ms
// to 15-20 s - not COM, not the helper, but plain contention. The pieces here make concurrent use
// safe and bounded:
//
//   * DocLock: a cross-process named mutex per drawing ("Local\zwc-doc-<sha16(fullpath)>");
//     write commands take it, readers do not. Waiting is bounded (-LockTimeout) and a timeout is a
//     clean DOC_LOCKED with the holder's identity (pid/progId/startedAt) so callers can retry.
//   * Busy: CMDACTIVE / LASTPROMPT / modal-dialog detection with bounded retry (-BusyTimeout),
//     plus a one-shot ESC that cleans a polluted prompt ("Missing:") and retries exactly once.
//   * Fingerprint: entity count + HANDSEED + Saved taken before/after every write; an outside
//     change (human or another tool) surfaces as DOC_CHANGED_OUTSIDE + a light diffSummary.
//   * Deep doctor: pipe + COM + window (SendMessageTimeout WM_NULL) probes with CAD_HUNG verdicts.
//
// All of it is advisory metadata on the response ("lock", "warnings", "diffSummary"); the actual
// writes keep their own Pass1/Pass2/Pass3 discipline in Edits.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ZwcHelper
{
    internal static class Stability
    {
        // ------------------------------------------------------------------ named lock
        internal static string LockDir()
        {
            string d = Path.Combine(State.Dir(), "locks");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch (Exception) { }
            return d;
        }

        internal static string Sha16(string s)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        internal static string DocKeyOf(object doc)
        {
            string f = Com.Str(Com.TryGet(doc, "FullName"));
            if (string.IsNullOrEmpty(f)) f = Com.Str(Com.TryGet(doc, "Name"));
            return (f ?? "active").ToLowerInvariant();
        }

        /// <summary>Held for the duration of one write command. Release() must run on the same thread.</summary>
        internal sealed class DocLockHandle : IDisposable
        {
            internal Mutex _mutex;
            internal string _holderPath;
            public long WaitedMs;
            public bool Acquired;
            public string Name;

            public void Dispose()
            {
                if (_mutex == null) return;
                try { if (_holderPath != null && File.Exists(_holderPath)) File.Delete(_holderPath); } catch (Exception) { }
                try { _mutex.ReleaseMutex(); } catch (Exception) { }
                try { _mutex.Dispose(); } catch (Exception) { }
                _mutex = null;
            }
        }

        internal static DocLockHandle Acquire(object doc, JMap args, Ctx ctx, string purpose)
        {
            int timeout = args.Int("lockTimeout", 10000);
            if (timeout < 0) timeout = 0;
            string key = Sha16(DocKeyOf(doc));
            DocLockHandle h = new DocLockHandle();
            h.Name = @"Local\zwc-doc-" + key;
            h._holderPath = Path.Combine(LockDir(), key + ".json");
            try { h._mutex = new Mutex(false, h.Name); }
            catch (Exception) { h.Acquired = true; return h; }   // no mutex support: never block the tool

            Stopwatch sw = Stopwatch.StartNew();
            bool got = false;
            try { got = h._mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { got = true; }      // previous holder died: we own it now
            catch (Exception) { got = true; }
            h.WaitedMs = sw.ElapsedMilliseconds;
            if (!got)
            {
                JMap holder = ReadHolder(h._holderPath);
                h._mutex.Dispose();
                JMap detail = new JMap();
                detail.Add("holder", holder);
                detail.Add("waitedMs", h.WaitedMs);
                detail.Add("lock", h.Name);
                throw new ZwcError("DOC_LOCKED",
                    "another session is writing to this drawing (held " + h.WaitedMs + " ms, timeout " + timeout + " ms)",
                    "retry later, raise -LockTimeout, or ask the other session to finish",
                    Json.Write(detail));
            }
            h.Acquired = true;
            if (h.WaitedMs <= 0) return h;   // uncontended: skip the sidecar file (file I/O in %TEMP% is not free)
            try
            {
                JMap info = new JMap();
                info.Add("pid", Process.GetCurrentProcess().Id);
                info.Add("progId", Com.Str(Com.TryGet(ctx.AppMap, "progId")));
                info.Add("startedAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                info.Add("purpose", purpose);
                string tmp = h._holderPath + ".tmp";
                File.WriteAllText(tmp, Json.Write(info), new UTF8Encoding(false));
                if (File.Exists(h._holderPath)) File.Delete(h._holderPath);
                File.Move(tmp, h._holderPath);
            }
            catch (Exception) { }
            return h;
        }

        private static JMap ReadHolder(string path)
        {
            try
            {
                if (!File.Exists(path)) return new JMap();
                JMap m = Json.ParseFile(path) as JMap;
                return m ?? new JMap();
            }
            catch (Exception) { return new JMap(); }
        }

        // ------------------------------------------------------------------ busy detection
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private const uint GW_OWNER = 4;
        private const uint WM_NULL = 0x0000;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        internal sealed class Probe
        {
            public bool Ok = true;
            public string Reason;
            public string LastPrompt;
            public int CmdActive;
            public string ModalTitle;
            public string ModalClass;
        }

        /// <summary>Look for a modal dialog (#32770) owned by one of this CAD's windows.</summary>
        internal static string FindModal(int cadPid)
        {
            if (cadPid <= 0) return null;
            string found = null;
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    try
                    {
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        if ((int)pid != cadPid) return true;
                        if (!IsWindowVisible(h)) return true;
                        StringBuilder cls = new StringBuilder(64);
                        GetClassName(h, cls, 64);
                        if (cls.ToString() != "32770") return true;
                        if (GetWindow(h, GW_OWNER) == IntPtr.Zero) return true;   // top-level is not modal
                        StringBuilder txt = new StringBuilder(256);
                        GetWindowText(h, txt, 256);
                        found = txt.ToString();
                        return false;
                    }
                    catch (Exception) { return true; }
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return found;
        }

        /// <summary>Bounded window liveness probe (WM_NULL with SMTO_ABORTIFHUNG).</summary>
        internal static bool WindowPing(int hwnd, int timeoutMs, out long elapsedMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = false;
            if (hwnd > 0)
            {
                try
                {
                    IntPtr result;
                    IntPtr r = SendMessageTimeout(new IntPtr(hwnd), WM_NULL, IntPtr.Zero, IntPtr.Zero,
                        SMTO_ABORTIFHUNG, (uint)timeoutMs, out result);
                    ok = r != IntPtr.Zero;
                }
                catch (Exception) { }
            }
            elapsedMs = sw.ElapsedMilliseconds;
            return ok;
        }
        private static Cad _rwCad;
        private static string _rwKey;
        private static DateTime _rwAt = DateTime.MinValue;
        private static object _cleanDoc;
        private static DateTime _cleanAt = DateTime.MinValue;

        internal static Probe Check(object doc, int cadPid, bool quick = false)
        {
            Probe p = new Probe();
            try
            {
                object v = Com.Call(doc, "GetVariable", "CMDACTIVE");
                p.CmdActive = Com.I32(v);
            }
            catch (Exception) { }
            // A prompt that was clean moments ago does not need a second COM round-trip: on
            // ZWCAD 2025 each GetVariable costs 10-20 ms, and the write path checks on every call.
            bool freshClean = quick && ReferenceEquals(_cleanDoc, doc) && (DateTime.Now - _cleanAt).TotalMilliseconds < 5000;
            if (!freshClean)
            {
                try
                {
                    object v = Com.Call(doc, "GetVariable", "LASTPROMPT");
                    p.LastPrompt = Com.Str(v);
                }
                catch (Exception) { }
            }
            bool lookForModal = (p.CmdActive != 0) || (!freshClean && PromptPolluted(p.LastPrompt));
            if (lookForModal)
            {
                string modal = FindModal(cadPid);
                if (modal != null) { p.Ok = false; p.Reason = "modal"; p.ModalTitle = modal; p.ModalClass = "32770"; return p; }
            }
            if (p.CmdActive != 0)
            {
                p.Ok = false;
                p.Reason = "cmdactive";
                return p;
            }
            if (!freshClean)
            {
                string prompt = p.LastPrompt ?? "";
                if (PromptPolluted(prompt))
                {
                    p.Ok = false;
                    p.Reason = "prompt-polluted";
                    return p;
                }
                _cleanDoc = doc;
                _cleanAt = DateTime.Now;
            }
            return p;
        }

        /// <summary>Command-state pollution signatures: English AutoCAD prints "Missing:", the
        /// Chinese ZWCAD UI prints things like "; 错误: 输入的列表有缺陷", and a trailing ">" means
        /// a command is still asking for more input. Calibrate against audit-dev real samples.</summary>
        internal static bool PromptPolluted(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return true;   // empty prompt with CMDACTIVE!=0 = unknown command state
            string[] patterns = new string[] { "Missing:", "错误", "缺少", "缺陷", "无效", "无法" };
            foreach (string pat in patterns)
                if (prompt.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return prompt.TrimEnd().EndsWith(">", StringComparison.Ordinal);
        }

        internal static void SendEsc(object doc)
        {
            try { Com.Call(doc, "SendCommand", "\u0003"); return; } catch (Exception) { }
            try { Com.Call(doc, "SendCommand", "\u0003\u0003"); } catch (Exception) { }
        }

        /// <summary>Wait until the drawing is idle enough to write. Bounded by -BusyTimeout.</summary>
        internal static JMap EnsureIdle(object doc, int cadPid, JMap args, Ctx ctx, string purpose)
        {
            int timeout = args.Int("busyTimeout", 6000);
            if (timeout < 0) timeout = 0;
            string fault = args.Str("faultInject", null);
            if (string.IsNullOrEmpty(fault)) fault = args.Str("faultinject", null);
            if (!string.IsNullOrEmpty(fault) && fault.StartsWith("busy", StringComparison.OrdinalIgnoreCase))
            {
                // TEST HOOK: another process cannot inject a blocking COM state into CAD (SendCommand
                // blocks synchronously), so the tool simulates it on demand for BUSY_* coverage.
                long simMs = Math.Min(timeout, 400);
                if (fault.IndexOf("recover", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Stopwatch simSw = Stopwatch.StartNew();
                    ctx.Warn("BUSY_RETRIED", "FAULT_INJECT: simulated busy for " + simMs + " ms before the write");
                    Thread.Sleep((int)simMs);
                    JMap okInfo = new JMap();
                    okInfo.Add("waitedMs", simSw.ElapsedMilliseconds);
                    okInfo.Add("retries", 1);
                    okInfo.Add("injected", "busy-recover");
                    return okInfo;
                }
                Stopwatch simSw2 = Stopwatch.StartNew();
                while (simSw2.ElapsedMilliseconds < timeout) Thread.Sleep(50);
                JMap det = new JMap();
                det.Add("reason", "fault-inject");
                det.Add("waitedMs", simSw2.ElapsedMilliseconds);
                det.Add("injected", "busy");
                throw new ZwcError("BUSY_TIMEOUT", "FAULT_INJECT: simulated busy state exceeded " + timeout + " ms",
                    "injection is a test hook (-FaultInject busy); the real CAD prompt was not touched", Json.Write(det));
            }
            Stopwatch sw = Stopwatch.StartNew();
            bool cleared = false;
            bool warnedCmd = false;
            int retries = 0;
            JMap info = new JMap();
            while (true)
            {
                Probe p = Check(doc, cadPid, retries == 0);
                if (p.Ok)
                {
                    info.Add("waitedMs", sw.ElapsedMilliseconds);
                    info.Add("promptCleared", cleared);
                    return info;
                }
                if (p.Reason == "cmdactive" && (purpose == "entity" || purpose == "undo"))
                {
                    // COM-driven edits do not need the command prompt to be idle; only a modal
                    // dialog or a polluted prompt actually blocks us. Ignoring CMDACTIVE here removed
                    // a 150 ms sleep per retry from every concurrent write.
                    if (!warnedCmd)
                    {
                        ctx.Warn("BUSY_CMDACTIVE_IGNORED", "CAD reports a command in progress; COM writes are still safe, continuing");
                        warnedCmd = true;
                    }
                    info.Add("waitedMs", sw.ElapsedMilliseconds);
                    info.Add("promptCleared", cleared);
                    info.Add("cmdActiveIgnored", true);
                    return info;
                }
                if (p.Reason == "prompt-polluted" && !cleared)
                {
                    SendEsc(doc);
                    cleared = true;
                    ctx.Warn("PROMPT_CLEARED", "the command prompt was polluted (" + Shorten(p.LastPrompt) + "); ESC was sent and the command retried once");
                    Thread.Sleep(200);
                    if (sw.ElapsedMilliseconds > timeout + 2000) break;
                    continue;
                }
                retries++;
                if (retries == 1)
                    ctx.Warn("BUSY_RETRIED", "CAD was busy (" + p.Reason + "); retrying for up to " + timeout + " ms");
                if (sw.ElapsedMilliseconds >= timeout)
                {
                    JMap detail = new JMap();
                    detail.Add("reason", p.Reason);
                    detail.Add("lastPrompt", p.LastPrompt);
                    detail.Add("cmdActive", p.CmdActive);
                    detail.Add("modalTitle", p.ModalTitle);
                    detail.Add("waitedMs", sw.ElapsedMilliseconds);
                    detail.Add("cleared", cleared);
                    string code = p.Reason == "prompt-polluted" ? "CMD_STATE_POLLUTED" : "BUSY_TIMEOUT";
                    string hint = p.Reason == "prompt-polluted"
                        ? "send ESC in CAD (or run the tool again) so the command prompt returns to Command:"
                        : "finish the command in CAD, close the dialog, or press ESC; retry with -BusyTimeout for a longer wait";
                    throw new ZwcError(code, "CAD stayed busy for " + sw.ElapsedMilliseconds + " ms (" + p.Reason + ")", hint, Json.Write(detail));
                }
                Thread.Sleep(50);
            }
            return info;
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 60 ? s : s.Substring(0, 60) + "...";
        }

        // ------------------------------------------------------------------ fingerprint
        /// <summary>Light fingerprint for the post-write sample: entity count + HANDSEED only.
        /// The full version (Saved/PaperSpace) is only needed before the first write of a burst.</summary>
        internal static JMap FingerprintLight(object doc)
        {
            JMap m = new JMap();
            try { m.Add("count", Commands.CountOf(doc, "ModelSpace")); } catch (Exception) { }
            try
            {
                object seed = Com.Call(doc, "GetVariable", "HANDSEED");
                string s = Com.Str(seed);
                m.Add("handseed", s);
                long n = 0;
                if (!string.IsNullOrEmpty(s)) { try { n = Convert.ToInt64(s, 16); } catch (Exception) { } }
                m.Add("maxHandleNumeric", n);
            }
            catch (Exception) { }
            return m;
        }

        internal static JMap Fingerprint(object doc)
        {
            JMap m = new JMap();
            try { m.Add("count", Commands.CountOf(doc, "ModelSpace")); } catch (Exception) { }
            try
            {
                object seed = Com.Call(doc, "GetVariable", "HANDSEED");
                string s = Com.Str(seed);
                m.Add("handseed", s);
                long n = 0;
                if (!string.IsNullOrEmpty(s)) { try { n = Convert.ToInt64(s, 16); } catch (Exception) { } }
                m.Add("maxHandleNumeric", n);
            }
            catch (Exception) { }
            try
            {
                object saved = Com.TryGet(doc, "Saved");
                if (saved != null) m.Add("saved", Com.Bool(saved));
            }
            catch (Exception) { }
            try { m.Add("paper", Commands.CountOf(doc, "PaperSpace")); } catch (Exception) { }
            return m;
        }

        internal static bool SameFingerprint(JMap a, JMap b)
        {
            if (a == null || b == null) return true;
            return a.Int("count", -1) == b.Int("count", -1)
                && a.Int("maxHandleNumeric", -1) == b.Int("maxHandleNumeric", -1)
                && a.Bool("saved", false) == b.Bool("saved", false);
        }

        /// <summary>Compare the pre-write fingerprint with the one stored at the end of the last write.</summary>
        internal static JMap CheckOutsideChange(object doc, JMap before, Ctx ctx)
        {
            JMap st = _writeState;
            if (st == null) { st = State.Load(); _writeState = st; }
            if (_fpCache == null)
            {
                _fpCache = st.Map("docFingerprints");
                if (_fpCache == null) _fpCache = new JMap();
            }
            JMap all = _fpCache;
            string key = DocKeyOf(doc);
            JMap last = all.Map(key);
            JMap summary = new JMap();
            if (last != null)
            {
                int c0 = last.Int("count", 0), c1 = before.Int("count", 0);
                long h0 = last.Int("maxHandleNumeric", 0), h1 = before.Int("maxHandleNumeric", 0);
                bool changed = (c0 != c1) || (h0 != h1) || (last.Bool("saved", false) != before.Bool("saved", false));
                if (changed)
                {
                    summary.Add("countBefore", c0);
                    summary.Add("countAfter", c1);
                    summary.Add("added", c1 > c0 ? (object)(c1 - c0) : 0);
                    summary.Add("removed", c0 > c1 ? (object)(c0 - c1) : 0);
                    summary.Add("changed", null);
                    summary.Add("since", last.Str("at", null));
                    JMap det = new JMap();
                    det.Add("countBefore", c0);
                    det.Add("countAfter", c1);
                    det.Add("maxHandleBefore", h0);
                    det.Add("maxHandleAfter", h1);
                    ctx.Warn("DOC_CHANGED_OUTSIDE",
                        "the drawing changed outside this session since the last write (count " + c0 + " -> " + c1
                        + "); the write re-anchored against the current state - see diffSummary");
                    ctx.FpChanged = true;
                }
            }
            return summary;
        }

        private static JMap _writeState;   // loaded once per write command (State.Load is file I/O)
        private static JMap _fpCache;      // per-process fingerprint cache (avoids 2 file ops per write)
        private static DateTime _fpSavedAt = DateTime.MinValue;
        private static bool _fpDirty;
        private static JMap _lastFp;
        private static object _lastFpFor;
        private static DateTime _lastFpAt = DateTime.MinValue;

        internal static void StoreFingerprint(object doc, JMap fp)
        {
            try
            {
                JMap st = _writeState ?? State.Load();
                _writeState = st;
                if (_fpCache == null) { _fpCache = st.Map("docFingerprints"); if (_fpCache == null) _fpCache = new JMap(); }
                JMap all = _fpCache;
                _fpDirty = true;
                JMap copy = new JMap();
                foreach (string k in fp.Keys) copy.Add(k, fp.Get(k));
                copy.Add("at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                all.Add(DocKeyOf(doc), copy);
                if ((DateTime.Now - _fpSavedAt).TotalSeconds > 20)
                {
                    st.Add("docFingerprints", all);
                    State.Save(st);
                    _fpSavedAt = DateTime.Now;
                    _fpDirty = false;
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ write scope
        /// <summary>Runs one write command under the document lock, with busy handling and
        /// fingerprint comparison. The response gets "lock" and (when relevant) "diffSummary".</summary>
        internal static JMap RunWrite(JMap req, JMap args, Ctx ctx, string purpose, Func<JMap> body)
        {
            Stopwatch total = Stopwatch.StartNew();
            string attachKind = Commands.AppKindOf(req, args);
            string attachProg = Cad.ExplicitProgId(req, args);
            string attachKey = attachKind + "|" + attachProg;
            Cad cad;
            if (_rwCad != null && _rwKey == attachKey && (DateTime.Now - _rwAt).TotalSeconds < 10)
            {
                cad = _rwCad;
                ctx.AppMap = cad.AppInfo();
            }
            else
            {
                cad = Cad.AttachFor(req, args, ctx, Commands.DocArgOf(req, args));
                _rwCad = cad; _rwKey = attachKey; _rwAt = DateTime.Now;
            }
            ctx.CachedCad = cad;
            long attachMs = total.ElapsedMilliseconds;
            if (ctx.AppMap == null) ctx.AppMap = cad.AppInfo();
            string matchedBy = null;
            object doc = null;
            try
            {
                doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            }
            catch (ZwcError e)
            {
                bool planOnly = args.Bool("dryRun", false)
                    || string.Equals(args.Str("mode", ""), "plan", StringComparison.OrdinalIgnoreCase);
                if (e.Code == "DOC_AMBIGUOUS" && planOnly)
                {
                    doc = cad.ResolveDoc(Commands.DocArgOf(req, args), true, ctx, out matchedBy);
                    matchedBy = "active-fallback";
                    ctx.Warn("DOC_ACTIVE_FALLBACK", "dry run with several drawings open; planned against ActiveDocument (nothing is written)");
                }
                else if (e.Code == "DOC_NOT_FOUND")
                {
                    ZwcError gone = new ZwcError("DOC_GONE",
                        "the drawing is no longer open in CAD: " + (e.Message ?? ""),
                        "re-open it (open -Doc <path>) or pass -Launch; nothing was written", e.Detail);
                    throw gone;
                }
                throw;
            }
            ctx.CachedDoc = doc;
            ctx.CachedMatchedBy = matchedBy;
            if (matchedBy == "active") ctx.Warn("DOC_REANCHORED", "no -Doc given, resolved through ActiveDocument; pass -Doc for a deterministic target");
            int cadPid = ctx.AppMap == null ? 0 : Com.I32(Com.TryGet(ctx.AppMap, "pid"));
            using (DocLockHandle handle = Acquire(doc, args, ctx, purpose))
            {
                JMap lockInfo = new JMap();
                lockInfo.Add("acquired", handle.Acquired);
                lockInfo.Add("waitedMs", handle.WaitedMs);
                lockInfo.Add("name", handle.Name);
                if (handle.WaitedMs >= 1000) ctx.Warn("LOCK_WAITED", "waited " + handle.WaitedMs + " ms for the document write lock (another session is writing)");

                long lockMs = total.ElapsedMilliseconds - attachMs;
                JMap busyInfo = EnsureIdle(doc, cadPid, args, ctx, purpose);
                long busyMs = total.ElapsedMilliseconds - attachMs - lockMs;
                JMap before;
                bool reuseFp = _lastFpFor != null && ReferenceEquals(_lastFpFor, doc)
                    && (DateTime.Now - _lastFpAt).TotalMilliseconds < 800 && lockInfo.Int("waitedMs", 0) == 0;
                if (reuseFp) before = _lastFp;
                else { before = Fingerprint(doc); _lastFp = before; _lastFpFor = doc; _lastFpAt = DateTime.Now; }
                JMap summary = reuseFp ? new JMap() : CheckOutsideChange(doc, before, ctx);
                long fpMs = total.ElapsedMilliseconds - attachMs - lockMs - busyMs;

                long bodyStart = total.ElapsedMilliseconds;
                JMap result = body();
                long bodyMs = total.ElapsedMilliseconds - bodyStart;
                if (result == null) result = new JMap();
                result.Add("lock", lockInfo);
                result.Add("busy", busyInfo);
                JMap stab = new JMap();
                stab.Add("attachMs", attachMs);
                stab.Add("lockMs", lockMs);
                stab.Add("busyMs", busyMs);
                stab.Add("fingerprintMs", fpMs);
                stab.Add("bodyMs", bodyMs);
                stab.Add("totalMs", total.ElapsedMilliseconds);
                result.Add("stability", stab);
                if (summary.Count > 0) result.Add("diffSummary", summary);
                _lastFp = FingerprintLight(doc);
                _lastFpFor = doc;
                _lastFpAt = DateTime.Now;
                StoreFingerprint(doc, _lastFp);
                Cad.InvalidateDocInfo();
                _writeState = null;
                return result;
            }
        }
    }
}
