// LispWireTests.cs - offline unit tests for scripts\helper\LispWire.cs (M3-E6 P0-1/P0-2).
//
// Pure C#, no CAD, no COM, no JMap: compiled on its own together with LispWire.cs by
// tests\selftest_lisp.ps1. Every assertion prints "PASS <name>" or "FAIL <name>"; the exit code is
// 0 when all pass and 1 otherwise, so CI can just run the exe.
//
// The test source itself is ASCII only (it is compiled with -codepage:65001 but there is nothing
// non-ASCII in it): the Chinese-text case is built from GBK bytes so it also proves the GBK decode
// path that the real helper uses (LISP writes the local ANSI code page, the helper decodes cp936).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CadHelper;

internal static class LispWireTests
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

    private static void CheckEqNum(string name, double actual, double expected)
    {
        bool ok = Math.Abs(actual - expected) < 1e-9;
        if (!ok) Console.WriteLine("     expected " + expected.ToString("R") + " got " + actual.ToString("R"));
        Check(name, ok);
    }

    // GBK bytes B2 E2 CA D4 = two Chinese characters (the real dump is decoded as cp936).
    private static readonly Encoding Gbk = Encoding.GetEncoding(936);
    private static readonly string Cjk = Gbk.GetString(new byte[] { 0xB2, 0xE2, 0xCA, 0xD4 });

    private static void Main()
    {
        TestSplitRecord();
        TestUnescape();
        TestTryNum();
        TestGeometryEscapedPipe();
        TestGeometryRadius();
        TestGeometryAuxRecords();
        TestGeometryBadNumbers();
        TestGeometryChineseGbk();
        TestGeometrySkipLines();
        TestGeometryAuxWithoutEntity();
        TestFlatRecords();
        TestFlatChineseGbk();
        TestSentinelPhase();
        TestOrphanDecisions();
        TestOrphanStaleSweep();
        TestOrphanPreserve();

        Console.WriteLine("---- LispWireTests: " + _pass + " PASS / " + _fail + " FAIL");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- SplitRecord / Unescape / TryNum
    private static void TestSplitRecord()
    {
        // The core P0-1 case: an escaped '|' inside the text field must not split the record.
        string[] f = LispWire.SplitRecord("E|1A|TEXT|0|A\\|B|1.5|2.5|0|0");
        Check("split.escaped-pipe.field-count-9", f.Length == 9);
        CheckEq("split.escaped-pipe.raw-text-keeps-escape", f[4], "A\\|B");
        CheckEq("split.escaped-pipe.x-slot", f[5], "1.5");
        CheckEq("split.escaped-pipe.last-slot", f[8], "0");

        string[] g = LispWire.SplitRecord("E|2|LINE|0||1|2|0|1");
        Check("split.plain.field-count-9", g.Length == 9);
        CheckEq("split.plain.empty-text", g[4], "");

        string[] h = LispWire.SplitRecord("V|H1|0|10|20|30");
        Check("split.vertex.field-count-6", h.Length == 6);
        CheckEq("split.vertex.x", h[3], "10");

        Check("split.empty-line-one-field", LispWire.SplitRecord("").Length == 1);
        Check("split.null-no-fields", LispWire.SplitRecord(null).Length == 0);
    }

    private static void TestUnescape()
    {
        CheckEq("unescape.pipe", LispWire.UnescapeField("A\\|B"), "A|B");
        CheckEq("unescape.two-pipes", LispWire.UnescapeField("A\\|B\\|C"), "A|B|C");
        CheckEq("unescape.no-backslash", LispWire.UnescapeField("plain"), "plain");
        CheckEq("unescape.null", LispWire.UnescapeField(null), "");
        CheckEq("unescape.empty", LispWire.UnescapeField(""), "");
    }

    private static void TestTryNum()
    {
        double d;
        Check("num.integer", LispWire.TryNum("42", out d) && Math.Abs(d - 42.0) < 1e-9);
        Check("num.decimal", LispWire.TryNum("-1.25", out d) && Math.Abs(d + 1.25) < 1e-9);
        Check("num.garbage-false", !LispWire.TryNum("abc", out d) && Math.Abs(d) < 1e-12);
        Check("num.empty-false", !LispWire.TryNum("", out d));
        Check("num.null-false", !LispWire.TryNum(null, out d));
    }

    // ---------------------------------------------------------------- ParseGeometry
    private static void TestGeometryEscapedPipe()
    {
        // This is the exact row shape that used to shift x|y|z|nverts one slot to the right.
        string text = "E|1A|TEXT|0|A\\|B|1.5|2.5|0|0\r\n";
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(text, out records);
        Check("geom.escaped-pipe.record-count-1", records == 1 && recs.Count == 1);
        if (recs.Count == 1)
        {
            CheckEq("geom.escaped-pipe.text", recs[0].Text, "A|B");
            CheckEqNum("geom.escaped-pipe.x", recs[0].X, 1.5);
            CheckEqNum("geom.escaped-pipe.y", recs[0].Y, 2.5);
            CheckEqNum("geom.escaped-pipe.z", recs[0].Z, 0.0);
            Check("geom.escaped-pipe.nverts-0", recs[0].Nverts == 0);
            CheckEq("geom.escaped-pipe.handle", recs[0].Handle, "1A");
            CheckEq("geom.escaped-pipe.type", recs[0].Type, "TEXT");
            CheckEq("geom.escaped-pipe.layer", recs[0].Layer, "0");
        }

        string text2 = "E|1B|MTEXT|DLSS|A\\|B\\|C|10|20|30|1\r\nV|1B|0|10|20|30\r\n";
        int records2;
        List<LispRecord> recs2 = LispWire.ParseGeometry(text2, out records2);
        Check("geom.two-escaped-pipes.record-count-1", records2 == 1 && recs2.Count == 1);
        if (recs2.Count == 1)
        {
            CheckEq("geom.two-escaped-pipes.text", recs2[0].Text, "A|B|C");
            CheckEqNum("geom.two-escaped-pipes.x", recs2[0].X, 10.0);
            Check("geom.two-escaped-pipes.one-vertex", recs2[0].Vertices != null && recs2[0].Vertices.Count == 1);
        }
    }

    private static void TestGeometryRadius()
    {
        string text = "E|AA|CIRCLE|0||10|20|0|1\r\nV|AA|0|10|20|0\r\nR|AA|2.5\r\n"
                    + "E|AB|ARC|0||30|40|0|1\r\nR|AB|0.75\r\n";
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(text, out records);
        Check("geom.radius.record-count-2", records == 2 && recs.Count == 2);
        if (recs.Count == 2)
        {
            Check("geom.radius.circle", recs[0].Radius.HasValue && Math.Abs(recs[0].Radius.Value - 2.5) < 1e-9);
            Check("geom.radius.arc", recs[1].Radius.HasValue && Math.Abs(recs[1].Radius.Value - 0.75) < 1e-9);
            Check("geom.radius.aux-order-has-R", recs[0].AuxOrder != null && recs[0].AuxOrder.Contains("R"));
            Check("geom.radius.no-radius-on-plain-record", !recs[1].Height.HasValue);
        }
    }

    private static void TestGeometryAuxRecords()
    {
        string text = "E|L1|LINE|JMD||1|2|0|1\r\n"
                    + "V|L1|0|1|2|0\r\n"
                    + "P2|L1|3|4|0\r\n"
                    + "ELEV|L1|12.75\r\n"
                    + "H|L1|3.5\r\n"
                    + "CL|L1|5\r\n";
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(text, out records);
        Check("geom.aux.record-count-1", records == 1 && recs.Count == 1);
        if (recs.Count != 1) return;
        LispRecord r = recs[0];
        Check("geom.aux.vertex-count", r.Vertices != null && r.Vertices.Count == 1);
        if (r.Vertices != null && r.Vertices.Count == 1)
        {
            CheckEqNum("geom.aux.vertex-x", r.Vertices[0][0], 1.0);
            CheckEqNum("geom.aux.vertex-y", r.Vertices[0][1], 2.0);
            CheckEqNum("geom.aux.vertex-z", r.Vertices[0][2], 0.0);
        }
        Check("geom.aux.p2", r.P2 != null && Math.Abs(r.P2[0] - 3.0) < 1e-9 && Math.Abs(r.P2[1] - 4.0) < 1e-9);
        Check("geom.aux.elevation", r.Elevation.HasValue && Math.Abs(r.Elevation.Value - 12.75) < 1e-9);
        Check("geom.aux.height", r.Height.HasValue && Math.Abs(r.Height.Value - 3.5) < 1e-9);
        Check("geom.aux.flags", r.Flags.HasValue && r.Flags.Value == 5);
        Check("geom.aux.closed-bit", r.Flags.HasValue && (r.Flags.Value & 1) == 1);
        string order = r.AuxOrder == null ? "" : string.Join(",", r.AuxOrder.ToArray());
        CheckEq("geom.aux.encounter-order", order, "V,P2,ELEV,H,CL");
    }

    private static void TestGeometryBadNumbers()
    {
        string text = "E|B1|LINE|0||notnum|bad|0|x\r\n";
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(text, out records);   // must not throw
        Check("geom.badnum.record-count-1", records == 1 && recs.Count == 1);
        if (recs.Count == 1)
        {
            CheckEqNum("geom.badnum.x-defaults-0", recs[0].X, 0.0);
            CheckEqNum("geom.badnum.y-defaults-0", recs[0].Y, 0.0);
            Check("geom.badnum.nverts-defaults-0", recs[0].Nverts == 0);
        }
        // A short E row (fewer than 9 fields) is skipped, not fatal.
        int records2;
        List<LispRecord> recs2 = LispWire.ParseGeometry("E|C1|TEXT|0|x\r\n", out records2);
        Check("geom.short-row-skipped", records2 == 0 && recs2.Count == 0);
    }

    private static void TestGeometryChineseGbk()
    {
        // Simulate the real pipeline: the LISP writer emits ANSI/cp936, the helper decodes cp936.
        string line = "E|D1|TEXT|DLSS|" + Cjk + "|7|8|0|1";
        byte[] raw = Gbk.GetBytes(line);
        string decoded = Gbk.GetString(raw);
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(decoded, out records);
        Check("geom.gbk.record-count-1", records == 1 && recs.Count == 1);
        if (recs.Count == 1)
        {
            CheckEq("geom.gbk.text", recs[0].Text, Cjk);
            CheckEqNum("geom.gbk.x", recs[0].X, 7.0);
            CheckEqNum("geom.gbk.y", recs[0].Y, 8.0);
        }

        // Chinese text plus an escaped pipe must still keep every field in place.
        string line2 = "E|D2|TEXT|DLSS|" + Cjk + "\\|" + Cjk + "|9|8|0|0";
        string decoded2 = Gbk.GetString(Gbk.GetBytes(line2));
        int records2;
        List<LispRecord> recs2 = LispWire.ParseGeometry(decoded2, out records2);
        Check("geom.gbk-pipe.record-count-1", records2 == 1 && recs2.Count == 1);
        if (recs2.Count == 1)
        {
            CheckEq("geom.gbk-pipe.text", recs2[0].Text, Cjk + "|" + Cjk);
            CheckEqNum("geom.gbk-pipe.x", recs2[0].X, 9.0);
        }
    }

    private static void TestGeometrySkipLines()
    {
        string text = "\r\n\r\nE|E1|LINE|0||0|0|0|0\r\n\r\n__DONE__|1\r\n\r\n";
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry(text, out records);
        Check("geom.skip.one-record", records == 1 && recs.Count == 1);
        Check("geom.skip.no-done-record", recs.Count == 1 && recs[0].Handle == "E1");
        int records2;
        List<LispRecord> none = LispWire.ParseGeometry(null, out records2);
        Check("geom.null-input-empty", records2 == 0 && none.Count == 0);
    }

    private static void TestGeometryAuxWithoutEntity()
    {
        int records;
        List<LispRecord> recs = LispWire.ParseGeometry("V|ZZ|0|1|2|0\r\nR|ZZ|2.0\r\n", out records);
        Check("geom.aux-orphan-dropped", records == 0 && recs.Count == 0);
    }

    // ---------------------------------------------------------------- ParseFlat (E5 four-field dump)
    private static void TestFlatRecords()
    {
        string text = "E|H1|TEXT|DLSS|hello|0|0|0|0\r\n"
                    + "E|H2|TEXT|0|A\\|B|0|0|0|0\r\n"
                    + "E|H3|LINE|0||0|0|0|0\r\n"
                    + "H4|TEXT|0|legacy-row\r\n"
                    + "V|H1|0|1|2|0\r\n"
                    + "__DONE__|4\r\n"
                    + "\r\n";
        int textCount;
        List<LispRecord> recs = LispWire.ParseFlat(text, out textCount);
        Check("flat.record-count-4", recs.Count == 4);
        Check("flat.text-count-3", textCount == 3);
        if (recs.Count == 4)
        {
            CheckEq("flat.e-row.handle", recs[0].Handle, "H1");
            CheckEq("flat.e-row.type", recs[0].Type, "TEXT");
            CheckEq("flat.e-row.layer", recs[0].Layer, "DLSS");
            CheckEq("flat.e-row.text", recs[0].Text, "hello");
            CheckEq("flat.escaped-pipe.text", recs[1].Text, "A|B");
            CheckEq("flat.empty-text", recs[2].Text, "");
            CheckEq("flat.legacy.handle", recs[3].Handle, "H4");
            CheckEq("flat.legacy.text", recs[3].Text, "legacy-row");
        }
        int textCount2;
        List<LispRecord> shortRow = LispWire.ParseFlat("E|H5|TEXT|0\r\n", out textCount2);
        Check("flat.e-row-with-4-fields-skipped", shortRow.Count == 0);
    }

    private static void TestFlatChineseGbk()
    {
        string line = "E|G1|TEXT|DLSS|" + Cjk + "|0|0|0|0";
        string decoded = Gbk.GetString(Gbk.GetBytes(line));
        int textCount;
        List<LispRecord> recs = LispWire.ParseFlat(decoded, out textCount);
        Check("flat.gbk.record-count-1", recs.Count == 1 && textCount == 1);
        if (recs.Count == 1) CheckEq("flat.gbk.text", recs[0].Text, Cjk);
    }

    // ---------------------------------------------------------------- two-phase sentinel (P1-4.1)
    private static void TestSentinelPhase()
    {
        CheckEq("phase.completed", LispSentinelPhase.Classify(true, false), LispSentinelPhase.Completed);
        CheckEq("phase.completed-with-start", LispSentinelPhase.Classify(true, true), LispSentinelPhase.Completed);
        CheckEq("phase.started", LispSentinelPhase.Classify(false, true), LispSentinelPhase.Started);
        CheckEq("phase.not-started", LispSentinelPhase.Classify(false, false), LispSentinelPhase.NotStarted);
        CheckEq("phase.error.not-started", LispSentinelPhase.ErrorCodeFor(LispSentinelPhase.NotStarted), "LISP_NOT_STARTED");
        CheckEq("phase.error.started-is-timeout", LispSentinelPhase.ErrorCodeFor(LispSentinelPhase.Started), "LISP_FILE_TIMEOUT");
        CheckEq("phase.error.unknown-phase-is-timeout", LispSentinelPhase.ErrorCodeFor("whatever"), "LISP_FILE_TIMEOUT");
    }
    // ---------------------------------------------------------------- LispOrphans (P1-4.1 decisions)
    private static void TestOrphanDecisions()
    {
        CheckEq("orphan.action.completed", LispOrphans.EvidenceAction(true), LispOrphans.EvidenceDelete);
        CheckEq("orphan.action.timed-out", LispOrphans.EvidenceAction(false), LispOrphans.EvidenceOrphan);
        Check("orphan.candidate.geom", LispOrphans.IsOrphanCandidate("geom-abc123.txt"));
        Check("orphan.candidate.scan", LispOrphans.IsOrphanCandidate("scan-abc123.done"));
        Check("orphan.candidate.orphan-suffix", LispOrphans.IsOrphanCandidate("geom-abc.txt.orphan"));
        Check("orphan.candidate.state-not-candidate", !LispOrphans.IsOrphanCandidate("state.json"));
        Check("orphan.candidate.lispgen-not-candidate", !LispOrphans.IsOrphanCandidate("lispgen-out.txt"));
        DateTime now = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        Check("orphan.stale.25h", LispOrphans.IsStale(now.AddHours(-25), now, TimeSpan.FromHours(24)));
        Check("orphan.stale.1h", !LispOrphans.IsStale(now.AddHours(-1), now, TimeSpan.FromHours(24)));
        Check("orphan.stale.future", !LispOrphans.IsStale(now.AddHours(1), now, TimeSpan.FromHours(24)));
    }

    private static void TestOrphanStaleSweep()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cad-lispwire-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            DateTime old = DateTime.UtcNow.AddHours(-25);
            DateTime fresh = DateTime.UtcNow.AddHours(-1);
            string staleOrphan = Path.Combine(dir, "geom-old.txt.orphan");
            string staleScan = Path.Combine(dir, "scan-old.lsp");
            string freshOrphan = Path.Combine(dir, "scan-new.txt.orphan");
            string unrelated = Path.Combine(dir, "state.json");
            File.WriteAllText(staleOrphan, "x");
            File.WriteAllText(staleScan, "x");
            File.WriteAllText(freshOrphan, "x");
            File.WriteAllText(unrelated, "x");
            File.SetLastWriteTimeUtc(staleOrphan, old);
            File.SetLastWriteTimeUtc(staleScan, old);
            File.SetLastWriteTimeUtc(freshOrphan, fresh);
            File.SetLastWriteTimeUtc(unrelated, old);

            LispOrphans.CleanStale(dir, TimeSpan.FromHours(24));

            Check("orphan.sweep.stale-orphan-deleted", !File.Exists(staleOrphan));
            Check("orphan.sweep.stale-scan-deleted", !File.Exists(staleScan));
            Check("orphan.sweep.fresh-orphan-kept", File.Exists(freshOrphan));
            Check("orphan.sweep.unrelated-kept", File.Exists(unrelated));
            LispOrphans.CleanStale(null, TimeSpan.FromHours(24));          // must not throw
            LispOrphans.CleanStale(dir + "-missing", TimeSpan.FromHours(24)); // must not throw
            Check("orphan.sweep.bad-input-safe", true);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void TestOrphanPreserve()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cad-lispwire-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string lsp = Path.Combine(dir, "scan-p.lsp");
            string txt = Path.Combine(dir, "scan-p.txt");
            File.WriteAllText(lsp, "script");
            File.WriteAllText(txt, "data");
            string missing = Path.Combine(dir, "scan-p.done");
            string[] renamed = LispOrphans.Preserve(dir, lsp, txt, missing);
            Check("orphan.preserve.renamed-2", renamed.Length == 2);
            Check("orphan.preserve.lsp-renamed", !File.Exists(lsp) && File.Exists(lsp + LispOrphans.OrphanSuffix));
            Check("orphan.preserve.txt-renamed", !File.Exists(txt) && File.Exists(txt + LispOrphans.OrphanSuffix));
            Check("orphan.preserve.missing-skipped", !File.Exists(missing + LispOrphans.OrphanSuffix));
            string[] none = LispOrphans.Preserve(dir, (string[])null);
            Check("orphan.preserve.null-input", none.Length == 0);
            LispOrphans.DeleteQuiet(lsp + LispOrphans.OrphanSuffix);
            Check("orphan.preserve.delete-quiet", !File.Exists(lsp + LispOrphans.OrphanSuffix));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
