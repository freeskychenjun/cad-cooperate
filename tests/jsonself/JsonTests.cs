// JsonTests.cs - offline unit tests for scripts\helper\Json.cs (plan 5.1 batch 1: Json extraction).
//
// Pure C#: no CAD, no COM, no named pipe, no drawing. Compiled by tests\selftest_lisp.ps1 together
// with scripts\helper\Json.cs and a verbatim JMap stand-in (tests\jsonself\JMapStub.cs). Every
// assertion prints "PASS <name>" or "FAIL <name> <detail>"; the exit code is 0 when everything
// passes and 1 otherwise, so CI only has to run the exe.
//
// The test source itself is ASCII only (it is compiled with -codepage:65001 but contains no
// non-ASCII byte): the Chinese round-trip cases are built from \u4E2D\u6587 style escapes, which is
// also exactly how the real helper keeps its literals.
//
// Contract under test (must not drift while Json.cs is refactored):
//   * writer: minimal escaping, insertion-order object keys, "R" doubles, NaN/Inf -> null,
//     integers stay integral, non-ASCII characters emitted raw;
//   * parser: UTF-8 text in, long for integer tokens, double otherwise, BOM tolerated,
//     FormatException (never a crash or a half-parsed value) for malformed input.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ZwcHelper;

internal static class JsonTests
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
            "expected=" + Show(expected) + " actual=" + Show(actual));
    }

    private static void CheckEqNum(string name, double actual, double expected)
    {
        Check(name, actual == expected, "expected=" + Show(R(expected)) + " actual=" + Show(R(actual)));
    }

    private static void CheckEqLong(string name, object actual, long expected)
    {
        Check(name, actual is long && (long)actual == expected,
            "expected=long:" + expected.ToString(CultureInfo.InvariantCulture) + " actual=" + Describe(actual));
    }

    private static void CheckThrowsFormat(string name, string text)
    {
        try
        {
            object v = Json.Parse(text);
            Check(name, false, "no exception, got " + Describe(v));
        }
        catch (FormatException) { Check(name, true, null); }
        catch (Exception e) { Check(name, false, "wrong exception " + e.GetType().Name + ": " + Show(e.Message)); }
    }

    private static string R(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }

    private static string Describe(object v)
    {
        if (v == null) return "<null>";
        return v.GetType().Name + ":" + Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // One-line rendering for failure messages (no raw newline may reach the PASS/FAIL stream).
    private static string Show(string s)
    {
        if (s == null) return "<null>";
        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    internal static int Main()
    {
        try
        {
            RunWriterStrings();
            RunWriterScalars();
            RunWriterContainers();
            RunParserBasics();
            RunParserNumbers();
            RunParserTolerance();
            RunParserRejects();
            RunJMapSurface();
            RunRoundTrip();
        }
        catch (Exception e)
        {
            Check("harness.unexpected", false, e.GetType().Name + ": " + Show(e.Message));
        }
        int total = _pass + _fail;
        Console.WriteLine("==== JsonTests summary: assertions=" + total + " pass=" + _pass + " fail=" + _fail);
        if (total < 30)
        {
            Console.WriteLine("FAIL coverage.too.few.assertions total=" + total);
            return 1;
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- writer: strings / escapes
    private static void RunWriterStrings()
    {
        CheckEq("w.string.empty", Json.Write(""), "\"\"");
        CheckEq("w.string.plain", Json.Write("abc123"), "\"abc123\"");
        CheckEq("w.string.backslash", Json.Write("a\\b"), "\"a\\\\b\"");
        CheckEq("w.string.quote", Json.Write("a\"b"), "\"a\\\"b\"");
        CheckEq("w.string.newline", Json.Write("a\nb"), "\"a\\nb\"");
        CheckEq("w.string.cr", Json.Write("a\rb"), "\"a\\rb\"");
        CheckEq("w.string.tab", Json.Write("a\tb"), "\"a\\tb\"");
        CheckEq("w.string.bs.formfeed", Json.Write("\b\f"), "\"\\b\\f\"");
        CheckEq("w.string.ctrl.1f", Json.Write("\u0001\u001f"), "\"\\u0001\\u001f\"");
        CheckEq("w.string.ctrl.nul", Json.Write("\0"), "\"\\u0000\"");
        CheckEq("w.string.unicode.raw", Json.Write("\u4E2D\u6587"), "\"\u4E2D\u6587\"");
        CheckEq("w.string.unicode.mixed", Json.Write("A\u4E2D\u6587z"), "\"A\u4E2D\u6587z\"");
        CheckEq("w.string.slash.not.escaped", Json.Write("a/b"), "\"a/b\"");
        CheckEq("w.string.controls.inside.text", Json.Write("x\ty\nz"), "\"x\\ty\\nz\"");
    }

    // ---------------------------------------------------------------- writer: scalars
    private static void RunWriterScalars()
    {
        CheckEq("w.bool.true", Json.Write(true), "true");
        CheckEq("w.bool.false", Json.Write(false), "false");
        CheckEq("w.null.object", Json.Write((object)null), "null");
        CheckEq("w.null.string", Json.Write((string)null), "null");

        CheckEq("w.number.int", Json.Write(42), "42");
        CheckEq("w.number.int.negative", Json.Write(-7), "-7");
        CheckEq("w.number.int.zero", Json.Write(0), "0");
        CheckEq("w.number.long", Json.Write(9007199254740993L), "9007199254740993");
        CheckEq("w.number.long.min", Json.Write(long.MinValue), "-9223372036854775808");
        CheckEq("w.number.double.integral", Json.Write(2.0), "2");
        CheckEq("w.number.double.fraction", Json.Write(1.5), "1.5");
        CheckEq("w.number.double.negative", Json.Write(-0.25), "-0.25");
        CheckEq("w.number.double.large", Json.Write(1e20), "1E+20");
        CheckEq("w.number.double.small", Json.Write(1e-7), "1E-07");
        CheckEq("w.number.double.nan", Json.Write(double.NaN), "null");
        CheckEq("w.number.double.inf", Json.Write(double.PositiveInfinity), "null");
        CheckEq("w.number.double.neginf", Json.Write(double.NegativeInfinity), "null");
        CheckEq("w.number.float", Json.Write(1.5f), "1.5");
        CheckEq("w.number.decimal", Json.Write(1.25m), "1.25");
    }

    // ---------------------------------------------------------------- writer: containers
    private static void RunWriterContainers()
    {
        CheckEq("w.array.empty.list", Json.Write(new List<object>()), "[]");
        CheckEq("w.array.mixed", Json.Write(new List<object> { 1, "two", true, null }), "[1,\"two\",true,null]");
        CheckEq("w.array.nested", Json.Write(new List<object> { new List<object> { 1 }, new List<object>() }), "[[1],[]]");
        CheckEq("w.array.netarray", Json.Write(new object[] { 1, 2 }), "[1,2]");
        CheckEq("w.array.enumerable", Json.Write(new List<string> { "a", "b" }), "[\"a\",\"b\"]");
        CheckEq("w.array.of.numbers", Json.Write(new List<object> { 1.5, -2, 1e20 }), "[1.5,-2,1E+20]");

        CheckEq("w.map.empty", Json.Write(new JMap()), "{}");

        JMap order = new JMap();
        order.Add("z", 1).Add("a", 2).Add("m", 3);
        CheckEq("w.map.insertion.order", Json.Write(order), "{\"z\":1,\"a\":2,\"m\":3}");
        CheckEq("w.map.keys.order", string.Join(",", order.Keys.ToArray()), "z,a,m");
        CheckEq("w.map.write.is.object", Json.Write((object)order), "{\"z\":1,\"a\":2,\"m\":3}");

        JMap nested = new JMap();
        JMap inner = new JMap();
        inner.Add("list", new List<object> { 1, 2 });
        inner.Add("s", "x\"y");
        nested.Add("outer", inner);
        nested.Add("b", true);
        nested.Add("nil", null);
        CheckEq("w.map.nested", Json.Write(nested),
            "{\"outer\":{\"list\":[1,2],\"s\":\"x\\\"y\"},\"b\":true,\"nil\":null}");

        JMap dup = new JMap();
        dup.Add("a", 1).Add("b", 2).Add("a", 3);
        CheckEq("w.map.duplicate.key", Json.Write(dup), "{\"a\":3,\"b\":2}");

        CheckEq("w.map.key.escape", Json.Write(new JMap().Add("a\"b\\c", 1)), "{\"a\\\"b\\\\c\":1}");
        CheckEq("w.map.unicode.key", Json.Write(new JMap().Add("\u4E2D", "\u6587")), "{\"\u4E2D\":\"\u6587\"}");

        string cjk = "\u4E2D\u6587\u56FE\u7EB8 A1";
        string cjkJson = Json.Write(new JMap().Add("doc", cjk));
        byte[] utf8 = Encoding.UTF8.GetBytes(cjkJson);
        CheckEq("w.utf8.bytes.decode", Encoding.UTF8.GetString(utf8), cjkJson);
        JMap cjkBack = (JMap)Json.Parse(Encoding.UTF8.GetString(utf8));
        CheckEq("w.utf8.roundtrip.value", cjkBack.Str("doc", null), cjk);
    }

    // ---------------------------------------------------------------- parser: basics
    private static void RunParserBasics()
    {
        object v = Json.Parse("{\"a\":1,\"b\":\"x\"}");
        Check("p.object.is.JMap", v is JMap, Describe(v));
        JMap m = (JMap)v;
        CheckEqLong("p.object.int.value", m.Get("a"), 1);
        CheckEq("p.object.string.value", m.Str("b", null), "x");
        CheckEq("p.object.keys.order", string.Join(",", m.Keys.ToArray()), "a,b");

        object av = Json.Parse("[1,2,3]");
        Check("p.array.is.List", av is List<object>, Describe(av));
        List<object> al = (List<object>)av;
        Check("p.array.count", al.Count == 3, "count=" + al.Count);
        CheckEqLong("p.array.first", al[0], 1);
        CheckEqLong("p.array.last", al[2], 3);

        CheckEq("p.string.escapes", (string)Json.Parse("\"a\\\"b\\\\c\\n\\t\\r\\b\\f\\/\""),
            "a\"b\\c\n\t\r\b\f/");
        CheckEq("p.string.unicode.lower", (string)Json.Parse("\"\\u4e2d\\u6587\""), "\u4E2D\u6587");
        CheckEq("p.string.unicode.upper", (string)Json.Parse("\"\\u4E2D\""), "\u4E2D");
        CheckEq("p.string.empty", (string)Json.Parse("\"\""), "");

        object bt = Json.Parse("true");
        object bf = Json.Parse("false");
        object nn = Json.Parse("null");
        Check("p.bool.true", bt is bool && (bool)bt, Describe(bt));
        Check("p.bool.false", bf is bool && !(bool)bf, Describe(bf));
        Check("p.null.value", nn == null, Describe(nn));
        CheckEq("p.bool.null.container", Json.Write(Json.Parse("[true,false,null]")), "[true,false,null]");
    }

    // ---------------------------------------------------------------- parser: numbers
    private static void RunParserNumbers()
    {
        Check("p.number.int.type", Json.Parse("42") is long, Describe(Json.Parse("42")));
        Check("p.number.int.not.double", !(Json.Parse("42") is double), Describe(Json.Parse("42")));
        CheckEqLong("p.number.int", Json.Parse("42"), 42);
        CheckEqLong("p.number.zero", Json.Parse("0"), 0);
        CheckEqLong("p.number.negative", Json.Parse("-7"), -7);
        CheckEqLong("p.number.long.max", Json.Parse("9223372036854775807"), long.MaxValue);
        CheckEqLong("p.number.long.min", Json.Parse("-9223372036854775808"), long.MinValue);

        object dv = Json.Parse("1.5");
        Check("p.number.double.type", dv is double, Describe(dv));
        CheckEqNum("p.number.double.value", (double)dv, 1.5);
        CheckEqNum("p.number.exponent", (double)Json.Parse("1e3"), 1000.0);
        CheckEqNum("p.number.exponent.plus", (double)Json.Parse("1E+2"), 100.0);
        CheckEqNum("p.number.double.negative", (double)Json.Parse("-0.25"), -0.25);

        CheckEq("p.number.int.writeback", Json.Write(Json.Parse("1")), "1");
        CheckEq("p.number.int.writeback.negative", Json.Write(Json.Parse("-12")), "-12");
        CheckEq("p.number.double.writeback", Json.Write(Json.Parse("1.5")), "1.5");
    }

    // ---------------------------------------------------------------- parser: tolerance
    private static void RunParserTolerance()
    {
        JMap bom = (JMap)Json.Parse("\uFEFF{\"a\":1}");
        CheckEqLong("p.bom.tolerated", bom.Get("a"), 1);

        object wsv = Json.Parse(" \r\n\t{ \"a\" : [ 1 , 2 ] } \n");
        CheckEq("p.whitespace.tolerated", Json.Write(wsv), "{\"a\":[1,2]}");

        JMap eo = (JMap)Json.Parse("{}");
        Check("p.empty.object", eo.Count == 0, "count=" + eo.Count);
        List<object> ea = (List<object>)Json.Parse("[]");
        Check("p.empty.array", ea.Count == 0, "count=" + ea.Count);
        CheckEq("p.empty.writeback", Json.Write(Json.Parse("{\"a\":{},\"b\":[]}")), "{\"a\":{},\"b\":[]}");

        string deepArr = new string('[', 50) + "1" + new string(']', 50);
        CheckEq("p.deep.array.roundtrip", Json.Write(Json.Parse(deepArr)), deepArr);
        string deepObj = "1";
        for (int i = 0; i < 50; i++) deepObj = "{\"k\":" + deepObj + "}";
        CheckEq("p.deep.object.roundtrip", Json.Write(Json.Parse(deepObj)), deepObj);

        JMap dup = (JMap)Json.Parse("{\"a\":1,\"a\":2}");
        Check("p.duplicate.key.count", dup.Count == 1, "count=" + dup.Count);
        CheckEqLong("p.duplicate.key.lastwins", dup.Get("a"), 2);

        JMap ci = (JMap)Json.Parse("{\"DryRun\":true}");
        Check("p.key.case.insensitive", ci.Bool("dryrun", false), "dryRun not found through lower-case key");
    }

    // ---------------------------------------------------------------- parser: rejects
    private static void RunParserRejects()
    {
        CheckThrowsFormat("p.invalid.truncated.object", "{\"a\":");
        CheckThrowsFormat("p.invalid.truncated.array", "[1,");
        CheckThrowsFormat("p.invalid.unterminated.string", "\"abc");
        CheckThrowsFormat("p.invalid.unterminated.object", "{\"a\":1");
        CheckThrowsFormat("p.invalid.trailing.garbage", "{} x");
        CheckThrowsFormat("p.invalid.comma.object", "{\"a\":1,}");
        CheckThrowsFormat("p.invalid.comma.array", "[1,,2]");
        CheckThrowsFormat("p.invalid.bareword", "foo");
        CheckThrowsFormat("p.invalid.bad.literal", "nul");
        CheckThrowsFormat("p.invalid.empty.text", "");
        CheckThrowsFormat("p.invalid.null.text", null);
        CheckThrowsFormat("p.invalid.bad.escape", "\"\\q\"");
        CheckThrowsFormat("p.invalid.short.unicode", "\"\\u12\"");
        CheckThrowsFormat("p.invalid.missing.colon", "{\"a\" 1}");
        CheckThrowsFormat("p.invalid.lone.minus", "-");
    }

    // ---------------------------------------------------------------- JMap surface used by Json
    private static void RunJMapSurface()
    {
        JMap typed = new JMap();
        typed.Add("s", "v").Add("d", 1.5).Add("i", 3L).Add("b", true);
        typed.Add("m", new JMap().Add("x", 1));
        typed.Add("l", new List<object> { 1, 2 });

        Check("jmap.count", typed.Count == 6, "count=" + typed.Count);
        Check("jmap.has", typed.Has("s") && !typed.Has("nope"), "Has mismatch");
        CheckEq("jmap.str", typed.Str("s", "dflt"), "v");
        CheckEq("jmap.str.convert", typed.Str("i", "dflt"), "3");
        CheckEqNum("jmap.dbl", typed.Dbl("d", -1), 1.5);
        CheckEqNum("jmap.dbl.from.long", typed.Dbl("i", -1), 3.0);
        Check("jmap.int", typed.Int("i", -1) == 3, "got " + typed.Int("i", -1));
        Check("jmap.bool", typed.Bool("b", false), "expected true");
        Check("jmap.bool.string", new JMap().Add("b", "yes").Bool("b", false), "expected true");
        Check("jmap.map", typed.Map("m") != null && typed.Map("m").Count == 1, "Map(name) failed");
        Check("jmap.list", typed.List("l") != null && typed.List("l").Count == 2, "List(name) failed");
        CheckEq("jmap.missing.default", typed.Str("nope", "dflt"), "dflt");
        Check("jmap.get.missing.null", typed.Get("nope") == null, "Get(missing) not null");

        JMap ci = new JMap();
        ci.Add("dryRun", 1);
        Check("jmap.key.case.insensitive", ci.Get("dryrun") is int && (int)ci.Get("dryrun") == 1,
            Describe(ci.Get("dryrun")));
    }

    // ---------------------------------------------------------------- full round trip
    private static void RunRoundTrip()
    {
        string text = "quote\" back\\ nl\n tab\t cr\r ctl\u0001 cjk\u4E2D\u6587";
        JMap root = new JMap();
        root.Add("cmd", "section");
        root.Add("ok", true);
        root.Add("n", 42);
        root.Add("neg", -3);
        root.Add("f", 0.125);
        root.Add("big", 1e20);
        root.Add("tiny", 1e-7);
        root.Add("s", text);
        root.Add("emptyMap", new JMap());
        root.Add("emptyList", new List<object>());
        root.Add("list", new List<object> { 1, "x", false, null, new JMap().Add("k", 2) });

        string first = Json.Write(root);
        byte[] bytes = Encoding.UTF8.GetBytes(first);
        string fromWire = Encoding.UTF8.GetString(bytes);
        object parsed = Json.Parse(fromWire);
        string second = Json.Write(parsed);

        Check("rt.parsed.is.JMap", parsed is JMap, Describe(parsed));
        CheckEq("rt.string.stable", second, first);

        byte[] bytes2 = Encoding.UTF8.GetBytes(second);
        bool same = bytes.Length == bytes2.Length;
        if (same)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != bytes2[i]) { same = false; break; }
            }
        }
        Check("rt.utf8.bytes.identical", same, "bytes differed after Write/Parse/Write");

        JMap back = (JMap)parsed;
        CheckEq("rt.cmd.value", back.Str("cmd", null), "section");
        Check("rt.ok.value", back.Bool("ok", false), "ok lost");
        CheckEqLong("rt.n.value", back.Get("n"), 42);
        Check("rt.n.type.is.long", back.Get("n") is long, Describe(back.Get("n")));
        CheckEqNum("rt.f.value", back.Dbl("f", -1), 0.125);
        CheckEq("rt.s.value", back.Str("s", null), text);
        Check("rt.empty.map", back.Map("emptyMap") != null && back.Map("emptyMap").Count == 0, "emptyMap lost");
        Check("rt.list.count", back.List("list") != null && back.List("list").Count == 5, "list lost");
        JMap listItem = back.List("list")[4] as JMap;
        Check("rt.list.nested.map", listItem != null && listItem.Count == 1, "nested map lost");
        CheckEqNum("rt.cjk.byte.count", Encoding.UTF8.GetByteCount(back.Str("s", "")), Encoding.UTF8.GetByteCount(text));
    }
}
