// ComCmdTests.cs - offline unit tests for scripts\helper\Com.cs (Com + Coll) and Cmd.cs
// (plan 5.1 batch 2). The "COM objects" are plain POCOs, so Get/Call/TryGet/TryCall/Set/TrySet
// run through the real reflection code without touching CAD or COM.
//
// The most valuable case here is tryget.permanent.no.retry: a property that always fails with
// DISP_E_MEMBERNOTFOUND must be attempted exactly ONCE even when Com.ReadRetries is raised -
// that is the M3-E1 fix that turned a 624-entity read from 209 s into <1 s.
//
// Compiled by tests\selftest_lisp.ps1 with:
//   tests\coreself\ComCmdTests.cs + scripts\helper\Core.cs + scripts\helper\Com.cs
//   + scripts\helper\Cmd.cs
// ASCII only (Chinese via \uXXXX); PASS/FAIL lines; exit code 0 = all pass, 1 = any FAIL.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ZwcHelper;

internal static class ComCmdTests
{
    private static int _pass;
    private static int _fail;
    private static int _savedReadRetries = 1;

    // ---------------------------------------------------------------- fake "COM" objects
    private sealed class FakeCom
    {
        public int Num { get; set; }
        public string Text { get; set; }
        public string ReadOnlyOnly { get { return "ro"; } }
        public int Silent { get; private set; }
        public int BoomCalls;
        public int UnknownNameCalls;
        public int FlakyCalls;
        public int AlwaysFlakyCalls;

        public string Boom
        {
            get { BoomCalls++; throw new COMException("perm", unchecked((int)0x80020003)); }
        }

        public string UnknownName
        {
            get { UnknownNameCalls++; throw new COMException("unknown name", unchecked((int)0x80020006)); }
        }

        public int Flaky
        {
            get { FlakyCalls++; if (FlakyCalls == 1) throw new InvalidOperationException("busy"); return 7; }
        }

        public int AlwaysFlaky
        {
            get { AlwaysFlakyCalls++; throw new InvalidOperationException("busy"); }
        }

        public int Add(int a, int b) { return a + b; }
        public void VoidM() { }
        public object HandleToObject(string handle) { return handle == null || handle.Length == 0 ? null : "entity:" + handle; }
        public int[] Point3 { get { return new int[] { 1, 2, 3 }; } }
    }

    private sealed class FakeDoc
    {
        public int SendCount;
        public string LastSent;
        public string Prompt = "Command:";
        public bool PolluteAfterSend;

        public string GetVariable(string name) { return Prompt; }

        public void SendCommand(string command)
        {
            SendCount++;
            LastSent = command;
            if (PolluteAfterSend) Prompt = "Missing: point";
        }
    }

    private sealed class FakeColl
    {
        public bool ZeroBased;
        public int ItemsAsked;

        public object Item(int i)
        {
            ItemsAsked++;
            if (ZeroBased)
            {
                if (i < 0 || i > 2) throw new ArgumentOutOfRangeException("i");
                return "z" + i;
            }
            if (i < 1 || i > 3) throw new ArgumentOutOfRangeException("i");
            return "o" + i;
        }
    }

    // ---------------------------------------------------------------- assertion helpers
    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + name); }
        else
        {
            _fail++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : " " + detail));
        }
    }

    private static void CheckEq(string name, string actual, string expected)
    {
        Check(name, string.Equals(actual, expected, StringComparison.Ordinal),
            "expected=[" + Show(expected) + "] actual=[" + Show(actual) + "]");
    }

    private static void CheckEqNum(string name, double actual, double expected)
    {
        Check(name, actual == expected,
            "expected=" + expected.ToString("R") + " actual=" + actual.ToString("R"));
    }

    private static void CheckZwcError(string name, string expectedCode, Action a)
    {
        try { a(); Check(name, false, "no exception thrown"); }
        catch (ZwcError e)
        {
            Check(name, string.Equals(e.Code, expectedCode, StringComparison.Ordinal),
                "expected code=" + expectedCode + " actual=" + e.Code + " msg=" + Show(e.Message));
        }
        catch (Exception e) { Check(name, false, "wrong exception " + e.GetType().Name); }
    }

    private static void CheckThrows(string name, Type expected, Action a)
    {
        try { a(); Check(name, false, "no exception thrown"); }
        catch (Exception e)
        {
            Check(name, expected.IsInstanceOfType(e),
                "expected=" + expected.Name + " actual=" + e.GetType().Name);
        }
    }

    private static string Show(string s)
    {
        if (s == null) return "<null>";
        string r = "";
        foreach (char c in s)
        {
            if (c == '\n') r += "\\n";
            else if (c == '\r') r += "\\r";
            else if (c == '\t') r += "\\t";
            else r += c;
        }
        return r;
    }

    internal static int Main()
    {
        int savedRetries = Com.ReadRetries;
        _savedReadRetries = savedRetries;
        try
        {
            RunGetCall();
            RunTryGetRetryPolicy();
            RunSetTryCall();
            RunScalarConversions();
            RunPointAndFlat();
            RunHrAndPermanent();
            RunColl();
            RunCmdStringFunctions();
            RunCmdDocumentFunctions();
        }
        catch (Exception e)
        {
            Check("harness.unexpected", false, e.GetType().Name + ": " + Show(e.Message));
        }
        finally
        {
            Com.ReadRetries = savedRetries;
            Com.LastError = null;
        }
        int total = _pass + _fail;
        Console.WriteLine("==== ComCmdTests summary: assertions=" + total + " pass=" + _pass + " fail=" + _fail);
        if (total < 50)
        {
            Console.WriteLine("FAIL coverage.too.few.assertions total=" + total);
            return 1;
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- Com.Get / Com.Call
    private static void RunGetCall()
    {
        FakeCom f = new FakeCom();
        f.Num = 5;
        f.Text = "hi";

        object got = Com.Get(f, "Num");
        Check("com.get.int", got is int && (int)got == 5, "value=" + (got == null ? "null" : got.ToString()));
        CheckEq("com.get.string", (string)Com.Get(f, "Text"), "hi");
        CheckEq("com.get.readonly.property", (string)Com.Get(f, "ReadOnlyOnly"), "ro");
        CheckThrows("com.get.missing.throws", typeof(MissingMethodException), delegate { Com.Get(f, "Missing"); });
        CheckZwcError("com.get.null.object.throws", "COM_NULL", delegate { Com.Get(null, "Num"); });

        object sum = Com.Call(f, "Add", 2, 3);
        Check("com.call.returns.value", sum is int && (int)sum == 5, "sum=" + (sum == null ? "null" : sum.ToString()));
        Check("com.call.void.returns.null", Com.Call(f, "VoidM") == null, "void call returned non-null");
        CheckThrows("com.call.missing.throws", typeof(MissingMethodException), delegate { Com.Call(f, "Nope"); });
        CheckZwcError("com.call.null.object.throws", "COM_NULL", delegate { Com.Call(null, "Add", 1, 2); });

        Check("com.tryget.readonly", Com.TryGet(f, "ReadOnlyOnly") != null, "TryGet returned null");
        Check("com.tryget.missing.null", Com.TryGet(f, "Missing") == null, "TryGet(missing) not null");
        Check("com.tryget.null.object.null", Com.TryGet(null, "Num") == null, "TryGet(null) not null");

        Com.LastError = null;
        object ignored = Com.TryGet(f, "Missing");
        Check("com.tryget.lastError.set", Com.LastError != null && Com.LastError.StartsWith("Missing:", StringComparison.Ordinal),
            "LastError=" + Show(Com.LastError));

        object okValue;
        bool ok = Com.TryGetOk(f, "Num", out okValue);
        Check("com.trygetok.ok", ok && okValue is int && (int)okValue == 5, "ok=" + ok);
        object badValue;
        Com.LastError = null;
        bool bad = Com.TryGetOk(f, "Missing", out badValue);
        Check("com.trygetok.missing.false", !bad && badValue == null && Com.LastError != null, "ok=" + bad);

        double[] min, max;
        Com.LastError = null;
        bool bbox = Com.TryGetBBox(f, out min, out max);
        Check("com.trygetbbox.missing.false", !bbox && min == null && max == null && Com.LastError != null, "bbox ok=" + bbox);
        Check("com.trygetbbox.null.false", !Com.TryGetBBox(null, out min, out max), "null entity returned true");

        object ent = Com.EntityByHandle(f, "1A");
        CheckEq("com.entitybyhandle.ok", ent as string, "entity:1A");
        Check("com.entitybyhandle.null.doc", Com.EntityByHandle(null, "1A") == null, "null doc returned entity");
        Check("com.entitybyhandle.empty.handle", Com.EntityByHandle(f, "") == null, "empty handle returned entity");
    }

    // ---------------------------------------------------------------- the retry policy
    private static void RunTryGetRetryPolicy()
    {
        FakeCom f = new FakeCom();
        Com.LastError = null;
        Com.ReadRetries = 5;
        object v = Com.TryGet(f, "Boom");
        Check("tryget.permanent.no.retry", f.BoomCalls == 1, "calls=" + f.BoomCalls + " (expected 1 with ReadRetries=5)");
        Check("tryget.permanent.returns.null", v == null, "permanent failure returned a value");
        Check("tryget.permanent.lastError.hr", Com.LastError != null && Com.LastError.IndexOf("0x80020003", StringComparison.Ordinal) >= 0,
            "LastError=" + Show(Com.LastError));

        Com.ReadRetries = 5;
        Com.TryGet(f, "UnknownName");
        Check("tryget.unknownname.no.retry", f.UnknownNameCalls == 1, "calls=" + f.UnknownNameCalls);

        FakeCom flaky = new FakeCom();
        Com.ReadRetries = 2;
        object fv = Com.TryGet(flaky, "Flaky");
        Check("tryget.transient.retried.then.ok", flaky.FlakyCalls == 2 && fv is int && (int)fv == 7,
            "calls=" + flaky.FlakyCalls + " value=" + (fv == null ? "null" : fv.ToString()));

        FakeCom always = new FakeCom();
        Com.ReadRetries = 2;
        object av = Com.TryGet(always, "AlwaysFlaky");
        Check("tryget.transient.exhausted.null", always.AlwaysFlakyCalls == 2 && av == null,
            "calls=" + always.AlwaysFlakyCalls);

        Com.ReadRetries = 1;
        FakeCom single = new FakeCom();
        single.AlwaysFlakyCalls = 0;
        Com.TryGet(single, "AlwaysFlaky");
        Check("tryget.single.try.default", single.AlwaysFlakyCalls == 1, "calls=" + single.AlwaysFlakyCalls);
        Check("tryget.readretries.restored", Com.ReadRetries == _savedReadRetries, "ReadRetries=" + Com.ReadRetries);
        Com.ReadRetries = 1;
    }

    // ---------------------------------------------------------------- Set / TrySet / TryCall
    private static void RunSetTryCall()
    {
        FakeCom f = new FakeCom();
        Com.Set(f, "Num", 9);
        Check("com.set.int", f.Num == 9, "Num=" + f.Num);
        Check("com.set.null.silent",
            NoThrow(delegate { Com.Set(null, "Num", 1); }), "Set(null) threw");
        Check("com.tryset.ok", Com.TrySet(f, "Num", 11) && f.Num == 11, "Num=" + f.Num);
        Check("com.tryset.readonly.false", !Com.TrySet(f, "ReadOnlyOnly", "x"), "read-only TrySet returned true");
        Check("com.tryset.missing.false", !Com.TrySet(f, "Missing", 1), "missing TrySet returned true");

        FakeCom c = new FakeCom();
        Check("com.trycall.value", Com.TryCall(c, "Add", 4, 5) is int && (int)Com.TryCall(c, "Add", 4, 5) == 9, "wrong sum");
        Check("com.trycall.missing.null", Com.TryCall(c, "Nope") == null, "missing call not null");
        Check("com.trycall.null.object.null", Com.TryCall(null, "Add", 1, 2) == null, "null object call not null");
        Check("com.trycall.void.null", Com.TryCall(c, "VoidM") == null, "void call not null");
        Check("com.tryinvoke.void.true", Com.TryInvoke(c, "VoidM"), "TryInvoke(void) false");
        Check("com.tryinvoke.missing.false", !Com.TryInvoke(c, "Nope"), "TryInvoke(missing) true");
    }

    private static bool NoThrow(Action a)
    {
        try { a(); return true; }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- scalar conversions
    private static void RunScalarConversions()
    {
        CheckEq("com.str.string", Com.Str("v"), "v");
        Check("com.str.null", Com.Str(null) == null, "Str(null) not null");
        CheckEq("com.str.int", Com.Str(42), "42");
        CheckEq("com.str.double", Com.Str(1.5), "1.5");

        CheckEqNum("com.dbl.int", Com.Dbl(2), 2.0);
        CheckEqNum("com.dbl.long", Com.Dbl(3L), 3.0);
        CheckEqNum("com.dbl.double", Com.Dbl(1.5), 1.5);
        CheckEqNum("com.dbl.float", Com.Dbl(1.5f), 1.5);
        CheckEqNum("com.dbl.string", Com.Dbl("2.5"), 2.5);
        CheckEqNum("com.dbl.null", Com.Dbl(null), 0.0);

        Check("com.i32.int", Com.I32(7) == 7, "I32(7)");
        Check("com.i32.double.rounds", Com.I32(2.6) == 3, "I32(2.6) != 3");
        Check("com.i32.null", Com.I32(null) == 0, "I32(null) != 0");

        Check("com.bool.true", Com.Bool(true), "Bool(true)");
        Check("com.bool.false", !Com.Bool(false), "Bool(false)");
        Check("com.bool.numeric.1", Com.Bool(1), "Bool(1)");
        Check("com.bool.numeric.0", !Com.Bool(0), "Bool(0)");
        Check("com.bool.string.True", Com.Bool("True"), "Bool(\"True\")");
        Check("com.bool.string.minus1", Com.Bool("-1"), "Bool(\"-1\")");
        Check("com.bool.string.false", !Com.Bool("false"), "Bool(\"false\")");
        Check("com.bool.null", !Com.Bool(null), "Bool(null)");
        Check("com.bool.empty", !Com.Bool(""), "Bool(\"\")");

        Check("com.hwndlong.int.negative", Com.HwndLong(-1) == 4294967295L, "HwndLong(-1)=" + Com.HwndLong(-1));
        Check("com.hwndlong.long", Com.HwndLong(1234567890123L) == 1234567890123L, "long hwnd lost");
        Check("com.hwndlong.null", Com.HwndLong(null) == 0, "HwndLong(null) != 0");
        Check("com.hwndlong.garbage", Com.HwndLong("abc") == 0, "HwndLong(\"abc\") != 0");
    }

    // ---------------------------------------------------------------- Pt / Flat
    private static void RunPointAndFlat()
    {
        double[] p3 = Com.Pt(new double[] { 1.5, 2.5, 3.5 });
        Check("com.pt.three", p3 != null && p3.Length == 3 && p3[0] == 1.5 && p3[2] == 3.5, "Pt(3) wrong");
        double[] p2 = Com.Pt(new int[] { 1, 2 });
        Check("com.pt.two.pads.zero", p2 != null && p2.Length == 3 && p2[0] == 1.0 && p2[1] == 2.0 && p2[2] == 0.0, "Pt(2) wrong");
        Check("com.pt.null", Com.Pt(null) == null, "Pt(null) not null");
        Check("com.pt.nonarray", Com.Pt(5) == null, "Pt(int) not null");

        double[] f3 = Com.Flat(new double[] { 1, 2, 3 });
        Check("com.flat.array", f3.Length == 3 && f3[0] == 1.0 && f3[2] == 3.0, "Flat(array) wrong");
        Check("com.flat.null.empty", Com.Flat(null).Length == 0, "Flat(null) not empty");
        Check("com.flat.nonarray.empty", Com.Flat(5).Length == 0, "Flat(int) not empty");
        double[] fm = Com.Flat(new object[] { 1, "2.5" });
        Check("com.flat.mixed", fm.Length == 2 && fm[1] == 2.5, "Flat(mixed) wrong");

        double[] ptProp = Com.Flat(Com.Get(new FakeCom(), "Point3"));
        Check("com.flat.property", ptProp.Length == 3 && ptProp[2] == 3.0, "Flat(property) wrong");
    }

    // ---------------------------------------------------------------- Hr / permanent codes
    private static void RunHrAndPermanent()
    {
        CheckEq("com.hr.com", Com.Hr(new COMException("m", unchecked((int)0x8002000B))), "0x8002000B m");
        CheckEq("com.hr.tie.com", Com.Hr(new TargetInvocationException(new COMException("m", unchecked((int)0x8002000B)))), "0x8002000B m");
        CheckEq("com.hr.generic", Com.Hr(new InvalidOperationException("g")), "InvalidOperationException: g");
        CheckEq("com.hr.tie.generic", Com.Hr(new TargetInvocationException(new InvalidOperationException("g"))), "InvalidOperationException: g");

        int[] permanent = new int[] {
            unchecked((int)0x80020006), unchecked((int)0x80020003),
            unchecked((int)0x8002000B), unchecked((int)0x80004002)
        };
        string[] names = new string[] { "0x80020006", "0x80020003", "0x8002000B", "0x80004002" };
        for (int i = 0; i < permanent.Length; i++)
        {
            Check("com.ispermanent." + names[i], Com.IsPermanentReadFailure(new COMException("x", permanent[i])), names[i] + " not permanent");
        }
        Check("com.ispermanent.other.code.false", !Com.IsPermanentReadFailure(new COMException("x", unchecked((int)0x80004005))), "0x80004005 flagged permanent");
        Check("com.ispermanent.noncom.false", !Com.IsPermanentReadFailure(new InvalidOperationException("x")), "non-COM flagged permanent");
        Check("com.ispermanent.null.false", !Com.IsPermanentReadFailure(null), "null flagged permanent");
        Check("com.ispermanent.tie.com.true", Com.IsPermanentReadFailure(new TargetInvocationException(new COMException("x", unchecked((int)0x80020006)))), "TIE(COM permanent) not detected");
        Check("com.ispermanent.tie.generic.false", !Com.IsPermanentReadFailure(new TargetInvocationException(new InvalidOperationException("x"))), "TIE(generic) flagged permanent");
    }

    // ---------------------------------------------------------------- Coll
    private static void RunColl()
    {
        Check("coll.detectbase.null", Coll.DetectBase(null, 3) == 1, "null collection base != 1");
        Check("coll.detectbase.empty", Coll.DetectBase(new FakeColl(), 0) == 1, "empty collection base != 1");

        FakeColl zero = new FakeColl();
        zero.ZeroBased = true;
        Check("coll.detectbase.zero.based", Coll.DetectBase(zero, 3) == 0, "0-based not detected");

        FakeColl one = new FakeColl();
        Check("coll.detectbase.one.based", Coll.DetectBase(one, 3) == 1, "1-based not detected");

        CheckEq("coll.at.one.based.first", Coll.At(one, 1, 0, 3) as string, "o1");
        CheckEq("coll.at.zero.based.second", Coll.At(zero, 0, 1, 3) as string, "z1");
        CheckEq("coll.at.one.based.last", Coll.At(one, 1, 2, 3) as string, "o3");
        int asked = one.ItemsAsked;
        Check("coll.at.out.of.range.null", Coll.At(one, 1, 3, 3) == null && one.ItemsAsked == asked,
            "out-of-range At still called Item()");
    }

    // ---------------------------------------------------------------- Cmd: pure string functions
    private static void RunCmdStringFunctions()
    {
        Check("cmd.balanced.empty", Cmd.Balanced(""), "empty not balanced");
        Check("cmd.balanced.plain", Cmd.Balanced("(+ 1 2)"), "plain expression unbalanced");
        Check("cmd.balanced.nested", Cmd.Balanced("((a) (b (c)))"), "nested expression unbalanced");
        Check("cmd.balanced.paren.in.string", Cmd.Balanced("(setq s \"(\")"), "paren inside string counted");
        Check("cmd.balanced.close.in.string", Cmd.Balanced("(setq s \")\")"), "close paren inside string counted");
        Check("cmd.balanced.escaped.quote", Cmd.Balanced("(setq s \"a\\\"b\")"), "escaped quote mishandled");
        Check("cmd.balanced.unclosed", !Cmd.Balanced("((("), "unclosed counted balanced");
        Check("cmd.balanced.extra.close", !Cmd.Balanced(")"), "stray close counted balanced");
        Check("cmd.balanced.close.then.open", !Cmd.Balanced(")("), "negative depth not rejected");
        Check("cmd.balanced.unterminated.string", !Cmd.Balanced("(setq s \"abc"), "unterminated string balanced");
        Check("cmd.balanced.quote.only", Cmd.Balanced("\"just a string\""), "string-only not balanced");
        Check("cmd.balanced.multiline", Cmd.Balanced("(a\nb)"), "multiline unbalanced");

        Check("cmd.prompt.null", !Cmd.PromptPolluted(null), "null prompt polluted");
        Check("cmd.prompt.empty", !Cmd.PromptPolluted(""), "empty prompt polluted");
        Check("cmd.prompt.normal", !Cmd.PromptPolluted("Command: "), "normal prompt polluted");
        Check("cmd.prompt.missing", Cmd.PromptPolluted("Missing: point"), "Missing: not detected");
        Check("cmd.prompt.missing.case.insensitive", Cmd.PromptPolluted("missing:"), "missing: not detected");
        Check("cmd.prompt.missing.midstring", Cmd.PromptPolluted("foo Missing: bar"), "Missing: mid-string not detected");
        Check("cmd.prompt.trailing.angle", Cmd.PromptPolluted("Specify: >"), "trailing > not detected");
        Check("cmd.prompt.trailing.angle.spaces", Cmd.PromptPolluted("Specify: >   "), "trailing > with spaces not detected");
        Check("cmd.prompt.angle.middle", !Cmd.PromptPolluted("a > b"), "angle in the middle flagged");
    }

    // ---------------------------------------------------------------- Cmd: doc-facing but POCO-safe
    private static void RunCmdDocumentFunctions()
    {
        FakeDoc doc = new FakeDoc();
        CheckEq("cmd.getvariable", Cmd.GetVariable(doc, "LASTPROMPT"), "Command:");
        CheckEq("cmd.getvariable.null.doc", Cmd.GetVariable(null, "LASTPROMPT"), null);
        CheckEq("cmd.lastprompt", Cmd.LastPrompt(doc), "Command:");

        Check("cmd.assertclean.ok", NoThrow(delegate { Cmd.AssertClean(doc, "test"); }), "clean prompt threw");
        FakeDoc stuck = new FakeDoc();
        stuck.Prompt = "Missing: point";
        CheckZwcError("cmd.assertclean.polluted", "CMD_STATE_POLLUTED",
            delegate { Cmd.AssertClean(stuck, "test"); });

        CheckZwcError("cmd.send.null.throws", "BAD_ARGS", delegate { Cmd.Send(doc, null, new Ctx(), "test"); });
        CheckZwcError("cmd.send.multiline.throws", "BAD_ARGS", delegate { Cmd.Send(doc, "L1\nL2", new Ctx(), "test"); });
        CheckZwcError("cmd.send.too.long.throws", "SENDCMD_TOO_LONG", delegate { Cmd.Send(doc, new string('a', 2001), new Ctx(), "test"); });
        Check("cmd.send.too.long.not.sent", doc.SendCount == 0, "oversized command was sent");

        FakeDoc ok = new FakeDoc();
        Cmd.Send(ok, "LINE", new Ctx(), "test");
        Check("cmd.send.ok.sent.once", ok.SendCount == 1 && ok.LastSent == "LINE", "count=" + ok.SendCount + " sent=" + Show(ok.LastSent));

        FakeDoc nl = new FakeDoc();
        Cmd.Send(nl, "LINE\n", new Ctx(), "test");
        Check("cmd.send.trailing.newline.kept", nl.SendCount == 1 && nl.LastSent == "LINE\n", "sent=" + Show(nl.LastSent));

        FakeDoc pollutedFirst = new FakeDoc();
        pollutedFirst.Prompt = "Missing: point";
        CheckZwcError("cmd.send.polluted.before", "CMD_STATE_POLLUTED",
            delegate { Cmd.Send(pollutedFirst, "LINE", new Ctx(), "test"); });
        Check("cmd.send.polluted.before.not.sent", pollutedFirst.SendCount == 0, "command sent into a stuck prompt");

        FakeDoc pollutedAfter = new FakeDoc();
        pollutedAfter.PolluteAfterSend = true;
        CheckZwcError("cmd.send.polluted.after", "CMD_STATE_POLLUTED",
            delegate { Cmd.Send(pollutedAfter, "LINE", new Ctx(), "test"); });
        Check("cmd.send.polluted.after.sent.once", pollutedAfter.SendCount == 1, "count=" + pollutedAfter.SendCount);
    }
}
