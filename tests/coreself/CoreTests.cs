// CoreTests.cs - offline unit tests for scripts\helper\Core.cs (JMap / ZwcError / Ctx) and the
// in-memory half of scripts\helper\State.cs (plan 5.1 batch 2: these units are pure C# now).
//
// No CAD, no COM, no file I/O: every assertion below runs against plain objects. State.Load /
// Save / Flush and everything else that touches %TEMP%\zwcad-cooperate\state.json is deliberately
// NOT exercised here (it would write the real user state file) - see the report for that list.
//
// Compiled by tests\selftest_lisp.ps1 with:
//   tests\coreself\CoreTests.cs + scripts\helper\Core.cs + scripts\helper\Json.cs
//   + scripts\helper\State.cs
// ASCII only (Chinese via \uXXXX); PASS/FAIL lines; exit code 0 = all pass, 1 = any FAIL.

using System;
using System.Collections.Generic;
using ZwcHelper;

internal static class CoreTests
{
    private static int _pass;
    private static int _fail;

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

    private static void CheckEqLong(string name, object actual, long expected)
    {
        Check(name, actual is long && (long)actual == expected,
            "expected=long:" + expected + " actual=" + Describe(actual));
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

    private static void CheckNoThrow(string name, Action a)
    {
        try { a(); Check(name, true, null); }
        catch (Exception e) { Check(name, false, "threw " + e.GetType().Name + ": " + Show(e.Message)); }
    }

    private static string Describe(object v)
    {
        if (v == null) return "<null>";
        return v.GetType().Name + ":" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
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
        try
        {
            RunJMapBasics();
            RunJMapConversions();
            RunJMapContainers();
            RunErrorAndContext();
            RunStatePure();
        }
        catch (Exception e)
        {
            Check("harness.unexpected", false, e.GetType().Name + ": " + Show(e.Message));
        }
        int total = _pass + _fail;
        Console.WriteLine("==== CoreTests summary: assertions=" + total + " pass=" + _pass + " fail=" + _fail);
        if (total < 40)
        {
            Console.WriteLine("FAIL coverage.too.few.assertions total=" + total);
            return 1;
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- JMap: basics
    private static void RunJMapBasics()
    {
        JMap m = new JMap();
        Check("jmap.empty.count", m.Count == 0, "count=" + m.Count);
        Check("jmap.empty.keys", m.Keys.Count == 0, "keys=" + m.Keys.Count);
        Check("jmap.empty.has", !m.Has("a"), "Has(a) true on empty map");
        Check("jmap.empty.get", m.Get("a") == null, "Get(a) not null");
        CheckEq("jmap.empty.str.default", m.Str("a", "dflt"), "dflt");
        CheckEqNum("jmap.empty.dbl.default", m.Dbl("a", -1.5), -1.5);
        Check("jmap.empty.int.default", m.Int("a", -3) == -3, "Int default lost");
        Check("jmap.empty.bool.default", m.Bool("a", true), "Bool default lost");
        Check("jmap.empty.map.null", m.Map("a") == null, "Map(a) not null");
        Check("jmap.empty.list.null", m.List("a") == null, "List(a) not null");

        JMap returned = m.Add("a", 1);
        Check("jmap.add.returns.this", ReferenceEquals(returned, m), "Add did not return the map");
        Check("jmap.add.count", m.Count == 1, "count=" + m.Count);
        Check("jmap.add.value", m.Get("a") is int && (int)m.Get("a") == 1, "value=" + Describe(m.Get("a")));
        Check("jmap.add.has", m.Has("a"), "Has(a) false after Add");
        CheckEq("jmap.add.keys", string.Join(",", m.Keys.ToArray()), "a");

        m.Add("b", "two").Add("c", true);
        Check("jmap.add.chain.count", m.Count == 3, "count=" + m.Count);
        CheckEq("jmap.add.order", string.Join(",", m.Keys.ToArray()), "a,b,c");

        m.Add("a", 99);
        Check("jmap.overwrite.count", m.Count == 3, "count=" + m.Count);
        CheckEq("jmap.overwrite.order", string.Join(",", m.Keys.ToArray()), "a,b,c");
        Check("jmap.overwrite.value", m.Get("a") is int && (int)m.Get("a") == 99, "value=" + Describe(m.Get("a")));

        JMap ci = new JMap();
        ci.Add("DryRun", false).Add("dryrun", true);
        Check("jmap.case.insensitive.count", ci.Count == 1, "count=" + ci.Count);
        Check("jmap.case.insensitive.keeps.first.key", ci.Keys[0] == "DryRun", "keys[0]=" + ci.Keys[0]);
        Check("jmap.case.insensitive.value", ci.Bool("DRYRUN", false), "case-insensitive Bool failed");

        JMap nv = new JMap();
        nv.Add("nil", null);
        Check("jmap.null.has", nv.Has("nil"), "stored null not visible through Has");
        Check("jmap.null.get", nv.Get("nil") == null, "stored null not null");
        CheckEq("jmap.null.str.default", nv.Str("nil", "d"), "d");
        Check("jmap.null.count", nv.Count == 1, "count=" + nv.Count);
    }

    // ---------------------------------------------------------------- JMap: typed getters
    private static void RunJMapConversions()
    {
        JMap sv = new JMap();
        sv.Add("s", "text").Add("i", 42).Add("d", 1.5).Add("b", true).Add("l", 9007199254740993L);
        CheckEq("jmap.str.string", sv.Str("s", null), "text");
        CheckEq("jmap.str.int", sv.Str("i", null), "42");
        CheckEq("jmap.str.double.invariant", sv.Str("d", null), "1.5");
        CheckEq("jmap.str.bool", sv.Str("b", null), "True");
        CheckEq("jmap.str.long", sv.Str("l", null), "9007199254740993");

        CheckEqNum("jmap.dbl.double", sv.Dbl("d", -1), 1.5);
        CheckEqNum("jmap.dbl.int", sv.Dbl("i", -1), 42.0);
        CheckEqNum("jmap.dbl.long", sv.Dbl("l", -1), 9007199254740992.0);
        CheckEqNum("jmap.dbl.string", new JMap().Add("x", "2.5").Dbl("x", -1), 2.5);
        CheckEqNum("jmap.dbl.unparsable.default", new JMap().Add("x", "abc").Dbl("x", -7), -7);
        CheckEqNum("jmap.dbl.bool.default", new JMap().Add("x", true).Dbl("x", -7), -7);

        Check("jmap.int.rounds.up", new JMap().Add("x", 1.6).Int("x", -1) == 2, "1.6 -> not 2");
        Check("jmap.int.rounds.even", new JMap().Add("x", 2.5).Int("x", -1) == 2, "2.5 -> not 2 (banker's)");
        Check("jmap.int.from.string", new JMap().Add("x", "3.4").Int("x", -1) == 3, "3.4 -> not 3");

        Check("jmap.bool.true", new JMap().Add("x", true).Bool("x", false), "true lost");
        Check("jmap.bool.false", !new JMap().Add("x", false).Bool("x", true), "false lost");
        Check("jmap.bool.string.1", new JMap().Add("x", "1").Bool("x", false), "\"1\" not true");
        Check("jmap.bool.string.true.upper", new JMap().Add("x", "TRUE").Bool("x", false), "\"TRUE\" not true");
        Check("jmap.bool.string.trimmed.yes", new JMap().Add("x", " yes ").Bool("x", false), "\" yes \" not true");
        Check("jmap.bool.string.on", new JMap().Add("x", "on").Bool("x", false), "\"on\" not true");
        Check("jmap.bool.string.no", !new JMap().Add("x", "no").Bool("x", true), "\"no\" not false");
        Check("jmap.bool.number.0", !new JMap().Add("x", 0).Bool("x", true), "0 not false");
        Check("jmap.bool.number.1", new JMap().Add("x", 1).Bool("x", false), "1 not true");
        Check("jmap.bool.unparsable.returns.false", !new JMap().Add("x", "maybe").Bool("x", true), "unparsable value did not fall back to false");

        JMap typed = new JMap();
        typed.Add("s", "v").Add("i", 3L).Add("d", 1.5).Add("b", true);
        CheckEq("jmap.typed.str", typed.Str("s", "dflt"), "v");
        CheckEqNum("jmap.typed.dbl", typed.Dbl("d", -1), 1.5);
        CheckEqNum("jmap.typed.dbl.from.long", typed.Dbl("i", -1), 3.0);
        Check("jmap.typed.int", typed.Int("i", -1) == 3, "Int(3L) != 3");
        Check("jmap.typed.bool", typed.Bool("b", false), "Bool(true) false");
    }

    // ---------------------------------------------------------------- JMap: Map/List
    private static void RunJMapContainers()
    {
        JMap child = new JMap().Add("k", 1);
        List<object> items = new List<object> { 1, "x" };
        JMap ml = new JMap();
        ml.Add("m", child).Add("l", items).Add("notmap", "s").Add("notlist", 5);

        Check("jmap.map.returns.same", ReferenceEquals(ml.Map("m"), child), "Map returned a copy");
        Check("jmap.map.wrong.type.null", ml.Map("notmap") == null, "Map(string) not null");
        Check("jmap.map.missing.null", ml.Map("nope") == null, "Map(missing) not null");
        Check("jmap.list.returns.same", ReferenceEquals(ml.List("l"), items), "List returned a copy");
        Check("jmap.list.wrong.type.null", ml.List("notlist") == null, "List(int) not null");
        Check("jmap.list.missing.null", ml.List("nope") == null, "List(missing) not null");
        Check("jmap.map.nested.get", ml.Map("m").Int("k", -1) == 1, "nested Int != 1");
        Check("jmap.list.count", ml.List("l").Count == 2, "nested list count");
    }

    // ---------------------------------------------------------------- ZwcError + Ctx
    private static void RunErrorAndContext()
    {
        ZwcError ze = new ZwcError("CODE_X", "message X", "hint X", "detail X");
        CheckEq("zwerr.code", ze.Code, "CODE_X");
        CheckEq("zwerr.message", ze.Message, "message X");
        CheckEq("zwerr.hint", ze.Hint, "hint X");
        CheckEq("zwerr.detail", ze.Detail, "detail X");
        Check("zwerr.is.exception", ze is Exception, "not an Exception");
        Check("zwerr.catch.as.exception", CatchAsException(ze), "could not be caught as Exception");

        Ctx ctx = new Ctx();
        Check("ctx.warnings.empty", ctx.Warnings.Count == 0, "warnings not empty");
        Check("ctx.defaults",
            ctx.DocMap == null && ctx.AppMap == null && ctx.UndoToken == null && ctx.Result == null
            && !ctx.FpChanged && !ctx.PlanOnly && ctx.LispMeta == null
            && ctx.CachedCad == null && ctx.CachedDoc == null && ctx.CachedMatchedBy == null,
            "Ctx defaults changed");

        ctx.Warn("W1", "first");
        ctx.Warn("W2", "second");
        Check("ctx.warn.count", ctx.Warnings.Count == 2, "count=" + ctx.Warnings.Count);
        JMap w0 = ctx.Warnings[0] as JMap;
        JMap w1 = ctx.Warnings[1] as JMap;
        Check("ctx.warn.item.is.JMap", w0 != null && w1 != null, "warning is not a JMap");
        CheckEq("ctx.warn.code", w0.Str("code", null), "W1");
        CheckEq("ctx.warn.message", w0.Str("message", null), "first");
        CheckEq("ctx.warn.order", w1.Str("code", null), "W2");

        ctx.DocMap = new JMap().Add("doc", "a.dwg");
        ctx.UndoToken = "tok";
        ctx.PlanOnly = true;
        Check("ctx.fields.assignable", ctx.DocMap.Str("doc", null) == "a.dwg" && ctx.UndoToken == "tok" && ctx.PlanOnly, "fields lost");
    }

    private static bool CatchAsException(Exception e)
    {
        try { throw e; }
        catch (Exception caught) { return ReferenceEquals(caught, e); }
    }

    // ---------------------------------------------------------------- State: pure, in-memory only
    private static void RunStatePure()
    {
        JMap st = new JMap();
        Check("state.session.null", State.Session(st) == null, "Session on fresh map not null");

        JMap tokens = State.Tokens(st);
        Check("state.tokens.creates", tokens != null, "Tokens returned null");
        Check("state.tokens.attached", ReferenceEquals(st.Map("tokens"), tokens), "tokens not attached");
        tokens.Add("tk1", new JMap().Add("cmd", "section"));
        Check("state.tokens.reuses", State.Tokens(st).Has("tk1"), "second Tokens() call lost the entry");

        JMap captions = State.Captions(st);
        Check("state.captions.creates", captions != null, "Captions returned null");
        Check("state.captions.reuses", ReferenceEquals(st.Map("captions"), captions), "captions not attached");
        captions.Add("h1", "orig");
        CheckEq("state.captions.value", State.Captions(st).Str("h1", null), "orig");

        JMap sess = new JMap().Add("session", new JMap().Add("launched", true).Add("hwnd", 42));
        Check("state.session.returns.map", State.Session(sess) != null, "Session not found");
        Check("state.session.value", State.Session(sess).Int("hwnd", -1) == 42, "hwnd not 42");
        State.ClearSession(sess);
        Check("state.clear.session", State.Session(sess) == null && sess.Get("session") == null, "session not cleared");
        CheckNoThrow("state.clear.session.safe", delegate { State.ClearSession(sess); });

        JMap own = new JMap().Add("session", new JMap().Add("launched", true).Add("hwnd", 42));
        Check("state.owns.single.hwnd", State.OwnsHwnd(own, 42), "single hwnd not owned");
        Check("state.owns.other.false", !State.OwnsHwnd(own, 43), "foreign hwnd reported owned");
        own.Map("session").Add("instances", new List<object> { new JMap().Add("hwnd", 100), new JMap().Add("hwnd", 42) });
        Check("state.owns.instance.list", State.OwnsHwnd(own, 100), "instances list not honoured");
        JMap notLaunched = new JMap().Add("session", new JMap().Add("launched", false).Add("hwnd", 42));
        Check("state.owns.not.launched.false", !State.OwnsHwnd(notLaunched, 42), "not-launched session owns hwnd");
        Check("state.owns.no.session.false", !State.OwnsHwnd(new JMap(), 42), "empty state owns hwnd");

        JMap ri = new JMap();
        JMap rsess = new JMap().Add("launched", true);
        rsess.Add("instances", new List<object> { new JMap().Add("hwnd", 1), new JMap().Add("hwnd", 2) });
        ri.Add("session", rsess).Add("openedDocs", new List<object> { "doc" });
        State.RemoveInstance(ri, 1);
        List<object> left = ri.Map("session").List("instances");
        Check("state.removeInstance.keeps.other", left != null && left.Count == 1, "count=" + (left == null ? -1 : left.Count));
        Check("state.removeInstance.other.hwnd", left != null && left.Count == 1 && (left[0] as JMap).Int("hwnd", -1) == 2, "wrong survivor");
        State.RemoveInstance(ri, 2);
        Check("state.removeInstance.clears.session", ri.Map("session") == null, "session not cleared");
        Check("state.removeInstance.clears.openedDocs", ri.Map("openedDocs") == null, "openedDocs not cleared");
        CheckNoThrow("state.removeInstance.no.session.safe", delegate { State.RemoveInstance(new JMap(), 7); });

        JMap pt = new JMap();
        JMap tk = new JMap();
        for (int i = 0; i < 25; i++) tk.Add("t" + i, i);
        pt.Add("tokens", tk);
        State.PruneTokens(pt);
        JMap after = pt.Map("tokens");
        Check("state.prune.count", after != null && after.Count == 20, "count=" + (after == null ? -1 : after.Count));
        Check("state.prune.newest.kept", after != null && after.Has("t24") && after.Has("t5"), "newest entries lost");
        Check("state.prune.oldest.dropped", after != null && !after.Has("t0") && !after.Has("t4"), "old entries kept");
        Check("state.prune.order", after != null && after.Keys[0] == "t5" && after.Keys[19] == "t24", "order changed");
    }
}
