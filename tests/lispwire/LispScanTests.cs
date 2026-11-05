// LispScanTests.cs - offline unit tests for scripts\helper\LispScan.cs (M3-E7 P4.2).
//
// Pure C#, no CAD, no COM, no drawing: compiled together with LispScanStubs.cs (test-only JMap/Ctx/
// Com/Commands stand-ins), CadErrorStub.cs, LispScan.cs, LispWire.cs and LispTemplate.cs by
// tests\selftest_lisp.ps1. Every assertion prints "PASS <name>" or "FAIL <name>"; the exit code is
// 0 when all pass and 1 otherwise.
//
// What is covered (the decision logic the refactor moved into LispScan, plus the two pure classes it
// drives):
//   * ssget filter assembly (no filter / types only / layer only / both, quote sanitization),
//   * temp-path layout and the request factory,
//   * the injected (load "...") command (path normalization, quote stripping, length gate),
//   * the "__DONE__|<n>" sentinel count parse (missing bar, padding, garbage, negative),
//   * the GBK decode of a dump written with the local ANSI code page,
//   * the two-phase sentinel verdicts (completed / started / not-started -> error codes),
//   * the cleanup decision (delete vs *.orphan), the orphan candidate filter and the 24 h grace time,
//   * the six meta keys and their documented order.
//
// The test source itself is ASCII only: the Chinese-text case is built from GBK bytes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using CadHelper;

internal static class LispScanTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + name); }
        else { _fail++; Console.WriteLine("FAIL " + name); }
    }

    private static void CheckEq(string name, string actual, string expected)
    {
        bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
        if (!ok) Console.WriteLine("     expected [" + expected + "] got [" + actual + "]");
        Check(name, ok);
    }

    private static void CheckEqInt(string name, int actual, int expected)
    {
        if (actual != expected) Console.WriteLine("     expected " + expected + " got " + actual);
        Check(name, actual == expected);
    }

    private static void Main()
    {
        TestBuildSsFilter();
        TestTempPaths();
        TestNewRequest();
        TestLoadCommand();
        TestParseSentinelCount();
        TestDecodeGbk();
        TestSentinelPhase();
        TestCleanupDecision();
        TestOrphanCandidate();
        TestGraceTime();
        TestAddMeta();
        TestRunSuccess();
        TestRunStartedTimeout();
        TestRunNotStarted();
        TestRunBusyAndPolluted();

        Console.WriteLine("---- LispScanTests: " + _pass + " PASS / " + _fail + " FAIL");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- ssget filter assembly
    private static void TestBuildSsFilter()
    {
        Check("ssfilter.none.is-null", LispScan.BuildSsFilter(null, null) == null);
        Check("ssfilter.empty-strings.is-null", LispScan.BuildSsFilter("", "") == null);

        CheckEq("ssfilter.types-only", LispScan.BuildSsFilter("LINE,LWPOLYLINE", null),
            "(list (cons 0 \"LINE,LWPOLYLINE\"))");
        CheckEq("ssfilter.layer-only", LispScan.BuildSsFilter(null, "JMD"),
            "(list (cons 8 \"JMD\"))");
        CheckEq("ssfilter.layer-only-empty-types", LispScan.BuildSsFilter("", "KZD"),
            "(list (cons 8 \"KZD\"))");
        CheckEq("ssfilter.both", LispScan.BuildSsFilter("TEXT", "JMD"),
            "(list (cons 0 \"TEXT\") (cons 8 \"JMD\"))");

        // An unmatched quote inside the value would break the generated LISP string literal.
        CheckEq("ssfilter.strip-quotes-type", LispScan.BuildSsFilter("TEXT\"X", null),
            "(list (cons 0 \"TEXTX\"))");
        CheckEq("ssfilter.strip-quotes-layer", LispScan.BuildSsFilter(null, "a\"b"),
            "(list (cons 8 \"ab\"))");
        CheckEq("ssfilter.both-quotes-and-parens", LispScan.BuildSsFilter("CIRCLE\"", "L\"(\"1"),
            "(list (cons 0 \"CIRCLE\") (cons 8 \"L(1\"))");
    }

    // ---------------------------------------------------------------- temp paths / request
    private static void TestTempPaths()
    {
        string dir = Path.Combine("C:\\", "tmpdir");
        string[] p = LispScan.TempPaths(dir, "scan", "0123456789ab");
        CheckEqInt("temppaths.count-4", p.Length, 4);
        Check("temppaths.txt", p[0].EndsWith("scan-0123456789ab.txt", StringComparison.Ordinal));
        Check("temppaths.done", p[1].EndsWith("scan-0123456789ab.done", StringComparison.Ordinal));
        Check("temppaths.start", p[2].EndsWith("scan-0123456789ab.start", StringComparison.Ordinal));
        Check("temppaths.lsp", p[3].EndsWith("scan-0123456789ab.lsp", StringComparison.Ordinal));
        Check("temppaths.same-dir", Path.GetDirectoryName(p[0]) == dir && Path.GetDirectoryName(p[3]) == dir);

        string[] g = LispScan.TempPaths(dir, "geom", "aa");
        Check("temppaths.geom-prefix", Path.GetFileName(g[0]) == "geom-aa.txt");
    }

    private static void TestNewRequest()
    {
        LispScan.Request r = LispScan.NewRequest("scan", false, "C:\\tmp", "(list (cons 8 \"A\"))", 60000);
        CheckEq("newrequest.kind", r.Kind, "scan");
        Check("newrequest.geom-false", r.Geom == false);
        CheckEq("newrequest.ssfilter", r.SsFilter, "(list (cons 8 \"A\"))");
        CheckEqInt("newrequest.timeout", r.TimeoutMs, 60000);
        Check("newrequest.paths-set", !string.IsNullOrEmpty(r.TextPath) && !string.IsNullOrEmpty(r.DonePath)
            && !string.IsNullOrEmpty(r.StartPath) && !string.IsNullOrEmpty(r.LspPath));
        Check("newrequest.txt-suffix", r.TextPath.EndsWith(".txt", StringComparison.Ordinal));
        Check("newrequest.lsp-suffix", r.LspPath.EndsWith(".lsp", StringComparison.Ordinal));

        LispScan.Request r2 = LispScan.NewRequest("geom", true, "C:\\tmp", null, 1000);
        Check("newrequest.geom-true", r2.Geom == true);
        Check("newrequest.unique-guids", r.TextPath != r2.TextPath);

        // Not-sealed contract checks: the pre-refactor temp-name shape is "<kind>-<guid>.<ext>".
        string fileName = Path.GetFileName(r.TextPath);
        Check("newrequest.file-name-shape", fileName.StartsWith("scan-", StringComparison.Ordinal)
            && fileName.Length == "scan-".Length + 12 + ".txt".Length);
    }

    // ---------------------------------------------------------------- injected command
    private static void TestLoadCommand()
    {
        CheckEq("loadcmd.slashes", LispScan.ComposeLoadCommand("C:\\Users\\x\\AppData\\Local\\Temp\\scan-1.lsp"),
            "(load \"C:/Users/x/AppData/Local/Temp/scan-1.lsp\")");
        CheckEq("loadcmd.strips-quotes", LispScan.ComposeLoadCommand("C:\\a\"b\\scan-1.lsp"),
            "(load \"C:/ab/scan-1.lsp\")");
        Check("loadcmd.length-not-too-long", LispScan.LoadCommandTooLong(LispScan.ComposeLoadCommand("C:\\tmp\\scan-1.lsp")) == false);

        Check("loadcmd.length-at-limit-not-too-long",
            LispScan.LoadCommandTooLong(new string('a', LispScan.MaxLoadCommandLength)) == false);
        Check("loadcmd.length-above-limit-is-too-long",
            LispScan.LoadCommandTooLong(new string('a', LispScan.MaxLoadCommandLength + 1)));
        Check("loadcmd.null-is-not-too-long", LispScan.LoadCommandTooLong(null) == false);
    }

    // ---------------------------------------------------------------- sentinel count
    private static void TestParseSentinelCount()
    {
        CheckEqInt("sentinelcount.done-5", LispScan.ParseSentinelCount("__DONE__|5"), 5);
        CheckEqInt("sentinelcount.padded", LispScan.ParseSentinelCount("__DONE__| 12 "), 12);
        CheckEqInt("sentinelcount.negative", LispScan.ParseSentinelCount("__DONE__|-3"), -3);
        CheckEqInt("sentinelcount.no-bar", LispScan.ParseSentinelCount("__DONE__"), -1);
        CheckEqInt("sentinelcount.garbage", LispScan.ParseSentinelCount("no marker at all"), -1);
        CheckEqInt("sentinelcount.null", LispScan.ParseSentinelCount(null), -1);
        // A bar that does not parse keeps int.TryParse's out-value (0) - this is what the geometry
        // dump used to report and is deliberately preserved.
        CheckEqInt("sentinelcount.unparseable-after-bar", LispScan.ParseSentinelCount("__DONE__|abc"), 0);
    }

    // ---------------------------------------------------------------- GBK decode
    private static void TestDecodeGbk()
    {
        // GBK B2 E2 CA D4 = two Chinese characters (the dump is written by LISP in the local ANSI
        // code page, so the helper must decode cp936, never UTF-8).
        byte[] raw = new byte[] { 0xB2, 0xE2, 0xCA, 0xD4 };
        string expected = Encoding.GetEncoding(936).GetString(raw);
        string actual = LispScan.DecodeGbk(raw);
        CheckEq("decodegbk.chinese", actual, expected);
        CheckEqInt("decodegbk.chinese-length", actual.Length, 2);
        Check("decodegbk.not-utf8", actual != Encoding.UTF8.GetString(raw));
        CheckEq("decodegbk.empty", LispScan.DecodeGbk(new byte[0]), "");
        CheckEq("decodegbk.null", LispScan.DecodeGbk(null), "");
    }

    // ---------------------------------------------------------------- two-phase sentinel
    private static void TestSentinelPhase()
    {
        CheckEq("phase.completed", LispSentinelPhase.Classify(true, true), LispSentinelPhase.Completed);
        CheckEq("phase.completed-wins", LispSentinelPhase.Classify(true, false), LispSentinelPhase.Completed);
        CheckEq("phase.started", LispSentinelPhase.Classify(false, true), LispSentinelPhase.Started);
        CheckEq("phase.not-started", LispSentinelPhase.Classify(false, false), LispSentinelPhase.NotStarted);

        CheckEq("phase.error-not-started", LispSentinelPhase.ErrorCodeFor(LispSentinelPhase.NotStarted),
            "LISP_NOT_STARTED");
        CheckEq("phase.error-started-is-timeout", LispSentinelPhase.ErrorCodeFor(LispSentinelPhase.Started),
            "LISP_FILE_TIMEOUT");
        CheckEq("phase.error-completed-is-timeout", LispSentinelPhase.ErrorCodeFor(LispSentinelPhase.Completed),
            "LISP_FILE_TIMEOUT");
    }

    // ---------------------------------------------------------------- cleanup decision
    private static void TestCleanupDecision()
    {
        CheckEq("cleanup.completed-deletes", LispOrphans.EvidenceAction(true), LispOrphans.EvidenceDelete);
        CheckEq("cleanup.unfinished-keeps-orphan", LispOrphans.EvidenceAction(false), LispOrphans.EvidenceOrphan);
        CheckEq("cleanup.delete-literal", LispOrphans.EvidenceDelete, "delete");
        CheckEq("cleanup.orphan-suffix", LispOrphans.OrphanSuffix, ".orphan");
    }

    private static void TestOrphanCandidate()
    {
        Check("orphan.candidate.scan-txt", LispOrphans.IsOrphanCandidate("scan-0123456789ab.txt"));
        Check("orphan.candidate.geom-lsp", LispOrphans.IsOrphanCandidate("geom-0123456789ab.lsp"));
        Check("orphan.candidate.any-orphan", LispOrphans.IsOrphanCandidate("scan-0123456789ab.txt.orphan"));
        Check("orphan.candidate.uppercase-orphan",
            LispOrphans.IsOrphanCandidate("geom-abc.ORPHAN"));
        Check("orphan.not-candidate.state", LispOrphans.IsOrphanCandidate("state.json") == false);
        Check("orphan.not-candidate.dmg", LispOrphans.IsOrphanCandidate("drawing.dwg") == false);
        Check("orphan.not-candidate.empty", LispOrphans.IsOrphanCandidate("") == false);
        Check("orphan.not-candidate.null", LispOrphans.IsOrphanCandidate(null) == false);
    }

    private static void TestGraceTime()
    {
        DateTime now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        Check("grace.stale-25h", LispOrphans.IsStale(now.AddHours(-25), now, TimeSpan.FromHours(24)));
        Check("grace.fresh-1h", LispOrphans.IsStale(now.AddHours(-1), now, TimeSpan.FromHours(24)) == false);
        Check("grace.exactly-24h-not-stale",
            LispOrphans.IsStale(now.AddHours(-24), now, TimeSpan.FromHours(24)) == false);
        // A peer process with a clock slightly ahead must not make a fresh file look stale.
        Check("grace.future-clock-not-stale",
            LispOrphans.IsStale(now.AddMinutes(5), now, TimeSpan.FromHours(24)) == false);
    }

    // ---------------------------------------------------------------- meta keys
    private static void TestAddMeta()
    {
        LispScan.Result r = new LispScan.Result();
        Stopwatch sw = Stopwatch.StartNew();
        r.Clock = sw;
        r.InjectMs = 100;
        System.Threading.Thread.Sleep(5);
        r.SentinelMs = sw.ElapsedMilliseconds;   // a real reading, so parseMs cannot go negative
        r.Raw = new byte[] { 1, 2, 3, 4, 5 };
        r.Text = "x";
        JMap meta = new JMap();
        LispScan.AddMeta(meta, r);

        string[] expected = new string[] { "lispInjectMs", "lispSentinelMs", "parseMs", "totalMs", "fileBytes", "decodedAs" };
        CheckEqInt("meta.key-count", meta.Count, expected.Length);
        bool orderOk = meta.Count >= expected.Length;
        for (int i = 0; orderOk && i < expected.Length; i++)
            if (!string.Equals(meta.Keys[i], expected[i], StringComparison.Ordinal)) orderOk = false;
        Check("meta.key-order", orderOk);
        CheckEqInt("meta.inject-ms", meta.Int("lispInjectMs", -1), 100);
        CheckEqInt("meta.sentinel-ms", meta.Int("lispSentinelMs", -1), (int)r.SentinelMs);
        CheckEqInt("meta.file-bytes", meta.Int("fileBytes", -1), 5);
        CheckEq("meta.decoded-as", meta.Str("decodedAs", ""), "GBK/936");
        Check("meta.parse-ms-non-negative", meta.Int("parseMs", -999) >= 0);
        Check("meta.total-not-before-sentinel", meta.Int("totalMs", -1) >= meta.Int("lispSentinelMs", 0));
        CheckEqInt("meta.parse-ms-is-total-minus-sentinel",
            meta.Int("parseMs", -1), meta.Int("totalMs", -2) - meta.Int("lispSentinelMs", 0));

        // AddMeta on a null/absent result must be a no-op, never a crash (defensive contract).
        LispScan.AddMeta(meta, null);
        CheckEqInt("meta.null-result-noop", meta.Count, expected.Length);
    }

    // ---------------------------------------------------------------- end-to-end pipeline (COM stubbed)
    // These drive LispScan.Run with the test COM/Commands stubs (LispScanStubs.cs) playing the CAD
    // side. They assert the rendered error text, the injected command, the GBK read-back and the
    // delete/orphan decision mechanically instead of by eye; no CAD, no COM, no drawing is touched.

    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "zwscan-unittest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception) { }
    }

    private static void TestRunSuccess()
    {
        string dir = NewTempDir();
        try
        {
            Com.Reset();
            Commands.Reset();
            string cjk = Encoding.GetEncoding(936).GetString(new byte[] { 0xB2, 0xE2, 0xCA, 0xD4 });
            byte[] dump = Encoding.GetEncoding(936).GetBytes("E|1A|TEXT|0|" + cjk + "|1.5|2.5|0|0\n");
            LispScan.Request req = LispScan.NewRequest("scan", false, dir, null, 5000);
            Com.OnSendCommand = delegate(string cmd)
            {
                File.WriteAllText(req.StartPath, "__START__");
                File.WriteAllText(req.DonePath, "__DONE__|3");
                File.WriteAllBytes(req.TextPath, dump);
            };
            JMap meta = null;
            string phase = null;
            LispScan.Result res = LispScan.Run(null, null, req, out meta, out phase);
            CheckEq("run.success.phase", phase, LispSentinelPhase.Completed);
            Check("run.success.inject-before-sentinel", res.InjectMs <= res.SentinelMs);
            CheckEqInt("run.success.sentinel-count", res.SentinelCount, 3);
            CheckEq("run.success.gbk-readback", res.Text, "E|1A|TEXT|0|" + cjk + "|1.5|2.5|0|0\n");
            CheckEqInt("run.success.raw-bytes", res.Raw.Length, dump.Length);
            Check("run.success.files-deleted", !File.Exists(req.TextPath) && !File.Exists(req.DonePath)
                && !File.Exists(req.StartPath) && !File.Exists(req.LspPath));
            CheckEqInt("run.success.dir-empty", Directory.GetFiles(dir).Length, 0);
            CheckEqInt("run.success.cmdecho-restored-once", Commands.RestoreCalls, 1);
            CheckEqInt("run.success.meta-empty-until-addmeta", meta.Count, 0);
            CheckEqInt("run.success.one-injected-command", Com.Sent.Count, 1);
            Check("run.success.injected-load-command", Com.Sent.Count == 1
                && Com.Sent[0].StartsWith("(load \"", StringComparison.Ordinal)
                && Com.Sent[0].EndsWith(".lsp\")\n", StringComparison.Ordinal));
            Check("run.success.injected-forward-slashes", Com.Sent.Count == 1 && Com.Sent[0].IndexOf('\\') < 0);
            LispScan.AddMeta(meta, res);
            CheckEqInt("run.success.meta-keys-after-addmeta", meta.Count, 6);
            CheckEq("run.success.meta-decoded-as", meta.Str("decodedAs", ""), "GBK/936");
        }
        finally { SafeDeleteDir(dir); }
    }

    private static void TestRunStartedTimeout()
    {
        string dir = NewTempDir();
        try
        {
            Com.Reset();
            Commands.Reset();
            LispScan.Request req = LispScan.NewRequest("scan", false, dir, null, 1000);
            // The CAD side ran (wrote __START__) but the dump never finished: keep the evidence.
            Com.OnSendCommand = delegate(string cmd) { File.WriteAllText(req.StartPath, "__START__"); };
            JMap meta = null;
            string phase = null;
            string code = null, msg = null, hint = null, detail = null;
            try { LispScan.Run(null, null, req, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; hint = e.Hint; detail = e.Detail; }
            CheckEq("run.started.code", code, "LISP_FILE_TIMEOUT");
            CheckEq("run.started.message", msg, "LISP scan did not finish within 1000 ms");
            CheckEq("run.started.phase", phase, LispSentinelPhase.Started);
            Check("run.started.hint-mentions-orphan", hint != null && hint.IndexOf("*.orphan", StringComparison.Ordinal) >= 0);
            Check("run.started.detail-phase", detail != null
                && detail.StartsWith("phase=started; startSeen=true; elapsedMs=", StringComparison.Ordinal));
            Check("run.started.lsp-preserved", File.Exists(req.LspPath + LispOrphans.OrphanSuffix));
            Check("run.started.start-preserved", File.Exists(req.StartPath + LispOrphans.OrphanSuffix));
            CheckEqInt("run.started.cmdecho-restored-once", Commands.RestoreCalls, 1);
        }
        finally { SafeDeleteDir(dir); }
    }

    private static void TestRunNotStarted()
    {
        string dir = NewTempDir();
        try
        {
            Com.Reset();
            Commands.Reset();
            LispScan.Request req = LispScan.NewRequest("scan", false, dir, null, 1000);
            JMap meta = null;
            string phase = null;
            string code = null, msg = null;
            try { LispScan.Run(null, null, req, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; }
            CheckEq("run.notstarted.code", code, "LISP_NOT_STARTED");
            CheckEq("run.notstarted.file-message", msg,
                "the LISP file scan did not start within 1000 ms (no __START__ sentinel)");
            CheckEq("run.notstarted.phase", phase, LispSentinelPhase.NotStarted);
            CheckEqInt("run.notstarted.meta-still-empty", meta.Count, 0);
            Check("run.notstarted.evidence-kept", File.Exists(req.LspPath + LispOrphans.OrphanSuffix));

            // Geometry variant: same pipeline, different documented wording.
            string dir2 = NewTempDir();
            try
            {
                Com.Reset();
                Commands.Reset();
                LispScan.Request g = LispScan.NewRequest("geom", true, dir2, null, 1000);
                g.BusyLabel = "LISP geometry dump";
                g.TimeoutLabel = "LISP geometry dump";
                string gcode = null, gmsg = null;
                try { LispScan.Run(null, null, g, out meta, out phase); }
                catch (CadError e) { gcode = e.Code; gmsg = e.Message; }
                CheckEq("run.notstarted.geom-code", gcode, "LISP_NOT_STARTED");
                CheckEq("run.notstarted.geom-message", gmsg,
                    "the LISP geometry dump did not start within 1000 ms (no __START__ sentinel)");
            }
            finally { SafeDeleteDir(dir2); }
        }
        finally { SafeDeleteDir(dir); }
    }

    private static void TestRunBusyAndPolluted()
    {
        string dir = NewTempDir();
        try
        {
            JMap meta = null;
            string phase = null;
            string code = null, msg = null, hint = null;

            // BUSY: the flat scan's documented words + hint (never inject into a busy command line).
            Com.Reset();
            Commands.Reset();
            Com.CmdActive = 1;
            LispScan.Request req = LispScan.NewRequest("scan", false, dir, null, 5000);
            req.BusyLabel = "LISP file scan";
            req.BusyHint = "retry when the command prompt is idle";
            try { LispScan.Run(null, null, req, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; hint = e.Hint; }
            CheckEq("run.busy.code", code, "BUSY");
            CheckEq("run.busy.file-message", msg, "CAD is busy (CMDACTIVE != 0); LISP file scan not injected");
            CheckEq("run.busy.file-hint", hint, "retry when the command prompt is idle");
            CheckEqInt("run.busy.no-injection", Com.Sent.Count, 0);

            // BUSY: the geometry dump's words + hint (they historically differed).
            Com.Reset();
            Commands.Reset();
            Com.CmdActive = 1;
            LispScan.Request g = LispScan.NewRequest("geom", true, dir, null, 5000);
            g.BusyLabel = "LISP geometry dump";
            g.BusyHint = "retry when idle";
            try { LispScan.Run(null, null, g, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; hint = e.Hint; }
            CheckEq("run.busy.geom-code", code, "BUSY");
            CheckEq("run.busy.geom-message", msg, "CAD is busy (CMDACTIVE != 0); LISP geometry dump not injected");
            CheckEq("run.busy.geom-hint", hint, "retry when idle");

            // PROMPT_POLLUTED: same message for both scans, per-scan hint.
            Com.Reset();
            Commands.Reset();
            Com.LastPrompt = "Error: undo failed";
            LispScan.Request p = LispScan.NewRequest("scan", false, dir, null, 5000);
            p.PollutedHint = "send ESC in CAD, or let the tool retry";
            try { LispScan.Run(null, null, p, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; hint = e.Hint; }
            CheckEq("run.polluted.code", code, "PROMPT_POLLUTED");
            CheckEq("run.polluted.message", msg, "CAD command prompt is polluted: Error: undo failed");
            CheckEq("run.polluted.file-hint", hint, "send ESC in CAD, or let the tool retry");

            // The Chinese "Error"-equivalent prompt (built from GBK bytes to keep this file ASCII).
            string cjkErr = Encoding.GetEncoding(936).GetString(new byte[] { 0xB4, 0xED, 0xCE, 0xF3 });
            Com.Reset();
            Commands.Reset();
            Com.LastPrompt = cjkErr + ": abort";
            LispScan.Request p2 = LispScan.NewRequest("geom", true, dir, null, 5000);
            p2.PollutedHint = "send ESC in CAD first";
            try { LispScan.Run(null, null, p2, out meta, out phase); }
            catch (CadError e) { code = e.Code; msg = e.Message; hint = e.Hint; }
            CheckEq("run.polluted.geom-code", code, "PROMPT_POLLUTED");
            CheckEq("run.polluted.geom-hint", hint, "send ESC in CAD first");
            Check("run.polluted.chinese-prompt-detected",
                msg != null && msg.IndexOf(cjkErr, StringComparison.Ordinal) >= 0);
        }
        finally { SafeDeleteDir(dir); }
    }
}
