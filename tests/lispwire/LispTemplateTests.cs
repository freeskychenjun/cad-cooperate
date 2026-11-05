// LispTemplateTests.cs - offline unit tests for scripts\helper\LispTemplate.cs (M3-E6 P1-4.1).
//
// Compiled by tests\selftest_lisp.ps1 together with LispTemplate.cs + LispWire.cs + CadErrorStub.cs.
// No CAD, no COM, no JMap: everything here is pure string/file logic, so the safety gate and the
// generated script shape are asserted before anything is ever injected into a drawing.
//
// What is checked:
//   * the generated dump script installs the *error* handler, writes __START__ before the dump and
//     __DONE__ at the end, restores *error* on the normal path, and stays ASCII + balanced + safe;
//   * the read-only safety gate still rejects command/entdel/setvar-on-other-vars/vl-*/vla-*, and the
//     new defun allowance does not let a forbidden call hide inside a function body;
//   * WriteScript writes no-BOM ASCII (a BOM makes CAD answer "input list is defective").

using System;
using System.IO;
using System.Text;
using CadHelper;

internal static class LispTemplateTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + name); }
        else { _fail++; Console.WriteLine("FAIL " + name); }
    }

    private static int CountOf(string haystack, string needle)
    {
        if (haystack == null || needle == null || needle.Length == 0) return 0;
        int n = 0;
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static void Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "cad-lisptpl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(temp);
        try
        {
            string txt = Path.Combine(temp, "geom-t.txt");
            string done = Path.Combine(temp, "geom-t.done");
            string start = Path.Combine(temp, "geom-t.start");
            TestGeomScript(temp, txt, done, start);
            TestFlatScript(txt, done, start);
            TestOverloadStartPath(txt, done);
            TestWriteScriptAsciiNoBom(temp);
            TestSafetyGateStillBites();
            TestBalance();
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (Exception) { }
        }

        Console.WriteLine("---- LispTemplateTests: " + _pass + " PASS / " + _fail + " FAIL");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void TestGeomScript(string temp, string txt, string done, string start)
    {
        string src = LispTemplate.BuildDump(true, txt, done, start, null);
        Check("tpl.geom.ascii", LispTemplate.IsAscii(src));
        Check("tpl.geom.balance", LispTemplate.BalanceError(src) == null);
        Check("tpl.geom.safety", LispTemplate.SafetyError(src) == null);
        Check("tpl.geom.has-error-handler", src.IndexOf("(defun *error* (msg)", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.handler-closes-txt", src.IndexOf("(close _cad_f)", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.handler-restores-cmdecho", src.IndexOf("(setvar \"CMDECHO\" _cad_echo)", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.restores-old-error-twice", CountOf(src, "(setq *error* _cad_olderr)") == 2);
        Check("tpl.geom.has-start-sentinel", src.IndexOf("__START__", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.has-done-sentinel", src.IndexOf("__DONE__|", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.has-radius-record", src.IndexOf("\"R|\"", StringComparison.Ordinal) >= 0);
        Check("tpl.geom.has-entity-record", src.IndexOf("\"E|\"", StringComparison.Ordinal) >= 0);

        int iHandler = src.IndexOf("(defun *error* (msg)", StringComparison.Ordinal);
        int iStart = src.IndexOf("__START__", StringComparison.Ordinal);
        int iOpenTxt = src.IndexOf("(setq _cad_f (open", StringComparison.Ordinal);
        int iDone = src.IndexOf("__DONE__|", StringComparison.Ordinal);
        Check("tpl.geom.order-handler-first", iHandler >= 0 && iHandler < iStart);
        Check("tpl.geom.order-start-before-dump", iStart >= 0 && iStart < iOpenTxt);
        Check("tpl.geom.order-done-last", iOpenTxt >= 0 && iDone > iOpenTxt);

        string lispTxt = txt.Replace("\\", "/");
        string lispStart = start.Replace("\\", "/");
        Check("tpl.geom.txt-path-forward-slashes", src.IndexOf(lispTxt, StringComparison.Ordinal) >= 0);
        Check("tpl.geom.start-path-forward-slashes", src.IndexOf(lispStart, StringComparison.Ordinal) >= 0);
        Check("tpl.geom.no-backslash-path", src.IndexOf(temp.Replace("/", "\\"), StringComparison.Ordinal) < 0);
        Check("tpl.geom.no-bom-char", src.IndexOf('\uFEFF') < 0);
    }

    private static void TestFlatScript(string txt, string done, string start)
    {
        string src = LispTemplate.BuildDump(false, txt, done, start, null);
        Check("tpl.flat.ascii", LispTemplate.IsAscii(src));
        Check("tpl.flat.balance", LispTemplate.BalanceError(src) == null);
        Check("tpl.flat.safety", LispTemplate.SafetyError(src) == null);
        Check("tpl.flat.has-start-sentinel", src.IndexOf("__START__", StringComparison.Ordinal) >= 0);
        Check("tpl.flat.has-done-sentinel", src.IndexOf("__DONE__|", StringComparison.Ordinal) >= 0);
        Check("tpl.flat.no-vertex-records", src.IndexOf("\"V|\"", StringComparison.Ordinal) < 0);
        Check("tpl.flat.no-radius-records", src.IndexOf("\"R|\"", StringComparison.Ordinal) < 0);
        Check("tpl.flat.no-elevation-records", src.IndexOf("\"ELEV|\"", StringComparison.Ordinal) < 0);
        Check("tpl.flat.still-writes-entity", src.IndexOf("\"E|\"", StringComparison.Ordinal) >= 0);
    }

    private static void TestOverloadStartPath(string txt, string done)
    {
        // The 4-argument overload is what the lispgen debug command calls; it derives the .start path
        // next to the dump file so generated scripts always carry the two-phase sentinel.
        string src = LispTemplate.BuildDump(true, txt, done, null);
        string expect = (txt + ".start").Replace("\\", "/");
        Check("tpl.overload.ascii", LispTemplate.IsAscii(src));
        Check("tpl.overload.balance", LispTemplate.BalanceError(src) == null);
        Check("tpl.overload.safety", LispTemplate.SafetyError(src) == null);
        Check("tpl.overload.start-sentinel", src.IndexOf("__START__", StringComparison.Ordinal) >= 0);
        Check("tpl.overload.derived-start-path", src.IndexOf(expect, StringComparison.Ordinal) >= 0);
    }

    private static void TestWriteScriptAsciiNoBom(string temp)
    {
        string path = Path.Combine(temp, "write-test.lsp");
        string src = LispTemplate.BuildDump(true, Path.Combine(temp, "w.txt"), Path.Combine(temp, "w.done"), Path.Combine(temp, "w.start"), null);
        LispTemplate.WriteScript(path, src);
        byte[] bytes = File.ReadAllBytes(path);
        Check("tpl.write.file-created", bytes.Length > 0);
        bool asciiOnly = true;
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] > 127) { asciiOnly = false; break; }
        Check("tpl.write.ascii-bytes", asciiOnly);
        Check("tpl.write.no-bom", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        Check("tpl.write.roundtrip", string.Equals(File.ReadAllText(path, Encoding.ASCII), src, StringComparison.Ordinal));
    }

    private static void TestSafetyGateStillBites()
    {
        Check("safety.rejects-command", LispTemplate.SafetyError("(progn (command \"LINE\"))") != null);
        Check("safety.rejects-entdel", LispTemplate.SafetyError("(progn (entdel (car (ssget))))") != null);
        Check("safety.rejects-entmod", LispTemplate.SafetyError("(progn (entmod x))") != null);
        Check("safety.rejects-selectionset-delete", LispTemplate.SafetyError("(progn (ssget \"X\") (setq s (ssget \"X\")) (vla-Delete s))") != null);
        Check("safety.rejects-setvar-other", LispTemplate.SafetyError("(progn (setvar \"CLAYER\" \"0\"))") != null);
        Check("safety.rejects-vl-file-delete", LispTemplate.SafetyError("(progn (vl-file-delete \"x\"))") != null);
        Check("safety.rejects-command-inside-defun-body",
            LispTemplate.SafetyError("(defun f (a b) (command \"LINE\"))") != null);
        Check("safety.allows-defun-with-readonly-body",
            LispTemplate.SafetyError("(defun f (a b) (if a (princ b)))") == null);
        Check("safety.allows-plain-dump-calls",
            LispTemplate.SafetyError("(progn (ssget \"X\") (write-line \"x\" _cad_f) (close _cad_f) (princ))") == null);
        Check("safety.rejects-unknown-symbol", LispTemplate.SafetyError("(progn (frobnicate 1))") != null);
    }

    private static void TestBalance()
    {
        Check("balance.empty", LispTemplate.BalanceError("") == "empty script");
        Check("balance.null", LispTemplate.BalanceError(null) == "empty script");
        Check("balance.ok", LispTemplate.BalanceError("(progn (princ))") == null);
        Check("balance.missing-close", LispTemplate.BalanceError("(progn (if") != null);
        Check("balance.extra-close", LispTemplate.BalanceError("(progn))") != null);
        Check("balance.paren-in-string-ok", LispTemplate.BalanceError("(progn (princ \"(\") (princ))") == null);
        Check("balance.comment-ignored", LispTemplate.BalanceError("(progn ; )\n (princ))") == null);
    }
}
