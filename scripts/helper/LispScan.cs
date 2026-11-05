// LispScan.cs - M3-E7 P4.2: the ONE implementation of the LISP injection pipeline.
//
// LispFile.Scan (E5 flat dump) and LispGeom.Scan (E6 geometry dump) were written as two files and from
// then on kept in sync by hand: each carried a ~140-line verbatim copy of the same five steps. Every
// behaviour fix therefore had to be applied twice, and the two copies had already drifted (the load
// command was built by a local LispEscape in one file and by LispTemplate.PathForLisp in the other;
// the sentinel detail string used a different culture path; slightly different error hints). The
// P0-1/P0-2 wire bugs fixed in LispWire came from exactly the same copy-paste habit on the parse side.
//
// This file owns the pipeline (exactly once):
//   1. pre-flight guard    CMDACTIVE != 0 -> BUSY, polluted LASTPROMPT -> PROMPT_POLLUTED
//   2. ssget filter        type list + exact layer -> "(list (cons 0 "...") (cons 8 "..."))"
//   3. injection           LispTemplate.BuildDump -> WriteScript (LISP_TEMPLATE_*) -> SendCommand
//   4. two-phase sentinel  poll <job>.start / <job>.done ("__DONE__|<n>"); no __START__ ->
//                          LISP_NOT_STARTED, __START__ without __DONE__ -> LISP_FILE_TIMEOUT
//   5. read back + cleanup GBK/936 (cp936) decode, then delete on success / keep *.orphan evidence on
//                          failure (LispOrphans); CMDECHO is restored in the finally block either way
//
// What stays in LispFile / LispGeom ("request + parse callback"):
//   * the request (temp-file prefix "scan-"/"geom-", geom on/off, ssget filter inputs),
//   * LispWire.ParseFlat / ParseGeometry and the LispRecord -> JMap mapping (field names and order are
//     part of the documented output contract, so they stay next to each other),
//   * the per-path meta extra ("mode":"geom") and result extras ("textCount" / "records").
//
// The public contract is unchanged: LispFile.Scan / LispGeom.Scan keep their exact signature, result
// semantics, error codes and byte-identical error messages/hints/details. The only observable timing
// difference is internal: "completed" now means "the dump file was read back" instead of "read back +
// parse + map"; parsing is a pure in-memory step that cannot fail in a way that would matter for the
// temp files CAD may still hold. parseMs/totalMs are still measured after the caller parsed, because
// the caller calls AddMeta() last.
//
// Kept dependency-light on purpose: the decision helpers (BuildSsFilter, TempPaths, ComposeLoadCommand,
// ParseSentinelCount) are BCL-only and unit-tested offline by tests\lispwire\LispScanTests.cs, together
// with LispSentinelPhase / LispOrphans.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ZwcHelper
{
    internal static class LispScan
    {
        /// <summary>LISP rejects / truncates very long command lines; the pre-refactor check.</summary>
        internal const int MaxLoadCommandLength = 1500;

        /// <summary>Everything that differs between the flat (E5) and the geometry (E6) dump: the temp
        /// file names, whether the geometry rows are generated and the exact wording of the errors that
        /// user-facing docs and the real-machine A/B depend on.</summary>
        internal sealed class Request
        {
            public string Kind = "scan";        // temp-file prefix: "scan-" (flat) / "geom-" (geometry)
            public bool Geom;                   // LispTemplate.BuildDump(geom)
            public string TextPath;             // <dir>\<kind>-<guid>.txt   (the dump itself)
            public string DonePath;             // <dir>\<kind>-<guid>.done  (__DONE__|<n> sentinel)
            public string StartPath;            // <dir>\<kind>-<guid>.start (__START__ sentinel)
            public string LspPath;              // <dir>\<kind>-<guid>.lsp   (the injected script)
            public string SsFilter;             // "(list ...)" or null: built by BuildSsFilter
            public int TimeoutMs;
            // Wording kept as literals so the refactor cannot silently reword a documented error.
            public string BusyLabel = "LISP file scan";       // "...; <BusyLabel> not injected"
            public string TimeoutLabel = "LISP scan";         // "<TimeoutLabel> did not finish within ..."
            public string BusyHint = "retry when the command prompt is idle";
            public string PollutedHint = "send ESC in CAD, or let the tool retry";
            /// <summary>Optional caller-supplied probe polled inside the sentinel wait loop. Returning
            /// true aborts the injection (used by LispGate to cancel an unexpected modal security
            /// dialog). BCL-only delegate so this file stays unit-testable offline.</summary>
            public Func<bool> AbortProbe;
        }

        /// <summary>Raw pipeline outcome. Parsing/mapping happen in the caller, which then calls
        /// AddMeta() so parseMs/totalMs cover the parse exactly like before the refactor.</summary>
        internal sealed class Result
        {
            public byte[] Raw;
            public string Text;
            public bool StartSeen;
            public long InjectMs;
            public long SentinelMs;
            public int SentinelCount = -1;
            internal Stopwatch Clock;

            /// <summary>Wall clock of the whole pipeline, read when the caller asks (after parsing).</summary>
            public long TotalMs { get { return Clock == null ? 0L : Clock.ElapsedMilliseconds; } }
        }

        // ------------------------------------------------------------------ pure decision helpers
        // Everything below is BCL-only (no COM, no CAD, no JMap) and asserted offline by
        // tests\lispwire\LispScanTests.cs.

        /// <summary>Build a request with fresh temp-file paths, exactly like the two pre-refactor
        /// Scans did: "<kind>-<12 hex chars>.<ext>" under dir.</summary>
        internal static Request NewRequest(string kind, bool geom, string dir, string ssFilter, int timeoutMs)
        {
            Request req = new Request();
            req.Kind = kind;
            req.Geom = geom;
            req.SsFilter = ssFilter;
            req.TimeoutMs = timeoutMs;
            string[] p = TempPaths(dir, kind, Guid.NewGuid().ToString("N").Substring(0, 12));
            req.TextPath = p[0];
            req.DonePath = p[1];
            req.StartPath = p[2];
            req.LspPath = p[3];
            return req;
        }

        /// <summary>The four temp paths of one job, in the order the Scans created them: txt, done,
        /// start, lsp. All four share one stem so cleanup/orphan logic sees one unit.</summary>
        internal static string[] TempPaths(string dir, string kind, string guid)
        {
            string stem = Path.Combine(dir == null ? "" : dir, kind + "-" + guid);
            return new string[] { stem + ".txt", stem + ".done", stem + ".start", stem + ".lsp" };
        }

        /// <summary>ssget filter: DXF type list (group 0) + exact layer (group 8). Quotes are stripped
        /// because an unmatched quote would corrupt the generated script; null means "no filter" (the
        /// script then calls (ssget "X") with no list at all).</summary>
        internal static string BuildSsFilter(string cadTypes, string layer)
        {
            StringBuilder flt = new StringBuilder();
            bool has = false;
            if (!string.IsNullOrEmpty(cadTypes)) { flt.Append("(cons 0 \"").Append(cadTypes.Replace("\"", "")).Append("\")"); has = true; }
            if (!string.IsNullOrEmpty(layer))
            {
                if (has) flt.Append(' ');
                flt.Append("(cons 8 \"").Append(layer.Replace("\"", "")).Append("\")");
                has = true;
            }
            return has ? "(list " + flt.ToString() + ")" : null;
        }

        /// <summary>The short command that is injected: (load "C:/.../job.lsp"). Forward slashes only,
        /// no embedded quotes (a LISP string literal).</summary>
        internal static string ComposeLoadCommand(string lspPath)
        {
            return "(load \"" + LispTemplate.PathForLisp(lspPath) + "\")";
        }

        internal static bool LoadCommandTooLong(string cmd)
        {
            return cmd != null && cmd.Length > MaxLoadCommandLength;
        }

        /// <summary>Extract the record count from a "__DONE__|&lt;n&gt;" sentinel. Mirrors the previous
        /// inline parse exactly: no '|' -> unavailable (-1); a '|' that does not parse gives 0 (the
        /// out-value of a failed int.TryParse), which is what the geometry dump used to report.</summary>
        internal static int ParseSentinelCount(string marker)
        {
            if (marker == null) return -1;
            int bar = marker.IndexOf('|');
            if (bar < 0) return -1;
            int n = -1;
            int.TryParse(marker.Substring(bar + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            return n;
        }

        /// <summary>True when LASTPROMPT reports a cancelled LISP load (AutoCAD SECURELOAD dialog
        /// answered with "Do Not Load"). BCL-only so the offline suites can exercise it too.</summary>
        internal static bool IsCancelledLoadPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            if (prompt.IndexOf("\u6587\u4EF6\u52A0\u8F7D\u5DF2\u53D6\u6D88", StringComparison.Ordinal) >= 0) return true;   // "file load canceled" (zh)
            if (prompt.IndexOf("load canceled", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (prompt.IndexOf("load cancelled", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Decode the dump with the local ANSI code page (cp936/GBK): the LISP file writer uses
        /// it, so a UTF-8 read turns Chinese annotations into mojibake. Falls back to the platform
        /// default when the code page is not installed.</summary>
        internal static string DecodeGbk(byte[] raw)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(936); }
            catch (Exception) { enc = Encoding.Default; }
            return enc.GetString(raw == null ? new byte[0] : raw);
        }

        /// <summary>The six timing/size meta keys every LISP scan reports, in the documented order.
        /// Called by the caller AFTER parsing so parseMs/totalMs keep their pre-refactor meaning.</summary>
        internal static void AddMeta(JMap meta, Result r)
        {
            if (meta == null || r == null) return;
            long total = r.TotalMs;
            meta.Add("lispInjectMs", r.InjectMs);
            meta.Add("lispSentinelMs", r.SentinelMs);
            meta.Add("parseMs", total - r.SentinelMs);
            meta.Add("totalMs", total);
            meta.Add("fileBytes", r.Raw == null ? 0 : r.Raw.Length);
            meta.Add("decodedAs", "GBK/936");
        }

        // ------------------------------------------------------------------ the pipeline
        /// <summary>Run steps 1-5 for one job. Throws the pre-refactor ZwcError codes
        /// (BUSY / PROMPT_POLLUTED / BAD_ARGS / LISP_TEMPLATE_* / LISP_NOT_STARTED /
        /// LISP_FILE_TIMEOUT) with the pre-refactor messages, hints and details; "phase" is the
        /// LispSentinelPhase verdict ("completed" when the dump was read back).</summary>
        internal static Result Run(object doc, Ctx ctx, Request req, out JMap meta, out string phase)
        {
            meta = new JMap();
            if (req == null) throw new ZwcError("BAD_ARGS", "internal: LISP scan request is missing", null, null);
            phase = LispSentinelPhase.NotStarted;
            string dir = null;
            try { dir = Path.GetDirectoryName(req.TextPath); }
            catch (Exception) { }
            LispOrphans.CleanStale(dir, TimeSpan.FromHours(24));
            Stopwatch sw = Stopwatch.StartNew();
            Result res = new Result();
            res.Clock = sw;
            int echoBefore = 1;
            bool readBack = false;
            try
            {
                // ---------------- step 1: pre-flight guard (never inject into a busy / errored state)
                try
                {
                    object ca = Com.Call(doc, "GetVariable", "CMDACTIVE");
                    if (Com.I32(ca) != 0)
                        throw new ZwcError("BUSY", "CAD is busy (CMDACTIVE != 0); " + req.BusyLabel + " not injected",
                            req.BusyHint, null);
                    object lp = Com.Call(doc, "GetVariable", "LASTPROMPT");
                    string prompt = Com.Str(lp) ?? "";
                    // A cancelled SECURELOAD dialog (AutoCAD answered "Do Not Load") leaves
                    // "; Error: File load canceled: <path>" in LASTPROMPT. That is our own artifact and
                    // means the command line is idle - the next injection must not be refused because
                    // of it (measured 2026-11-05: it disabled the fast path until a manual Esc).
                    if (!IsCancelledLoadPrompt(prompt)
                        && (prompt.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                            || prompt.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0
                            || prompt.IndexOf("\u9519\u8BEF", StringComparison.Ordinal) >= 0))
                        throw new ZwcError("PROMPT_POLLUTED", "CAD command prompt is polluted: " + prompt,
                            req.PollutedHint, null);
                }
                catch (ZwcError) { throw; }
                catch (Exception) { /* probes are best effort */ }

                // ---------------- step 1b: nothing to do here; the AutoCAD SECURELOAD gate and its
                // dialog watchdog live in Trust.cs (LispGate.Run) so this pipeline stays BCL-only.

                // ---------------- step 2 + 3: generate the script (validated), write it, inject it
                string src = LispTemplate.BuildDump(req.Geom, req.TextPath, req.DonePath, req.StartPath, req.SsFilter);
                echoBefore = Commands.CmdEchoOf(doc);
                LispTemplate.WriteScript(req.LspPath, src);   // ASCII/no-BOM + balance + safety gate

                string cmd = ComposeLoadCommand(req.LspPath);
                if (LoadCommandTooLong(cmd)) throw new ZwcError("BAD_ARGS", "LISP load command too long", null, null);
                Com.Call(doc, "SendCommand", cmd + "\n");
                res.InjectMs = sw.ElapsedMilliseconds;

                // ---------------- step 4: two-phase sentinel wait
                bool done = false;
                bool startSeen = false;
                int sentinelCount = -1;
                long deadline = Math.Max(1000, req.TimeoutMs);
                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (req.AbortProbe != null && req.AbortProbe()) break;
                    if (!startSeen && File.Exists(req.StartPath)) startSeen = true;
                    if (File.Exists(req.DonePath))
                    {
                        try
                        {
                            string marker = File.ReadAllText(req.DonePath);
                            if (marker.IndexOf(LispWire.DoneMarker, StringComparison.Ordinal) >= 0)
                            {
                                done = true;
                                sentinelCount = ParseSentinelCount(marker);
                                break;
                            }
                        }
                        catch (Exception) { }
                    }
                    Thread.Sleep(25);
                }
                if (req.AbortProbe != null && req.AbortProbe() && !done)
                {
                    throw new ZwcError("LISP_ABORTED",
                        "the injection was aborted by the caller (AutoCAD SECURELOAD prompt or another external condition)",
                        "see LISP_TRUST_BLOCKED / the caller's warning for the one-time fix",
                        null);
                }
                if (!done)
                {
                    // no __START__  -> the injected command never ran (busy CAD / modal dialog / LISP off)
                    // __START__ but no __DONE__ -> it is running inside CAD; keep the files as evidence
                    if (!startSeen) startSeen = File.Exists(req.StartPath);
                    res.StartSeen = startSeen;
                    phase = LispSentinelPhase.Classify(done, startSeen);
                    string detail = "phase=" + phase + "; startSeen=" + (startSeen ? "true" : "false")
                        + "; elapsedMs=" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
                    if (phase == LispSentinelPhase.NotStarted)
                        throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                            "the " + req.BusyLabel + " did not start within " + req.TimeoutMs + " ms (no __START__ sentinel)",
                            "CAD is probably busy or a modal dialog is open; run doctor, clear the command line, then retry",
                            detail);
                    throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                        req.TimeoutLabel + " did not finish within " + req.TimeoutMs + " ms",
                        "retry with a larger -TimeoutSec; the script is still running inside CAD - its temp files were kept as *.orphan in %TEMP%\\zwcad-cooperate",
                        detail);
                }
                res.StartSeen = startSeen;
                res.SentinelCount = sentinelCount;
                res.SentinelMs = sw.ElapsedMilliseconds;
                phase = LispSentinelPhase.Completed;

                // ---------------- step 5: read back + decode (GBK/936; the caller parses)
                res.Raw = File.ReadAllBytes(req.TextPath);
                res.Text = DecodeGbk(res.Raw);
                readBack = true;
                return res;
            }
            finally
            {
                // ---------------- cleanup: restore CMDECHO, keep or drop the evidence files
                try { Commands.RestoreCmdEcho(doc, echoBefore); } catch (Exception) { }
                if (readBack)
                {
                    LispOrphans.DeleteQuiet(req.LspPath);
                    LispOrphans.DeleteQuiet(req.TextPath);
                    LispOrphans.DeleteQuiet(req.DonePath);
                    LispOrphans.DeleteQuiet(req.StartPath);
                }
                else
                {
                    // Never delete what CAD may still be writing: keep the evidence as *.orphan.
                    LispOrphans.Preserve(dir, req.LspPath, req.TextPath, req.DonePath, req.StartPath);
                }
            }
        }
    }
}
