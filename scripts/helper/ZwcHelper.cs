// ZwcHelper.cs - zwcad-cooperate M1 helper (one-shot process, [STAThread], COM late binding).
//
// Contract (DESIGN-M1.md section 2/3):
//   ZwcHelper.exe --request <req.json> --response <resp.json>
//   request  { "cmd":"section", "doc":"<图纸目录>\\a.dwg", "app":"zwcad", "args":{...} }
//   response { ok, cmd, doc, result, warnings[{code,message}], undoToken, elapsedMs, app }
//            ok:false also carries error{code,message,hint,detail}
//
// Hard rules taken from research/com-probe.md and never violated here:
//   * COM is late bound only (Type.GetTypeFromProgID / Marshal.GetActiveObject + InvokeMember).
//     No interop assembly reference, no `dynamic` (keeps the csc command line minimal).
//   * Document.EvaluateLispExpression / Application.Eval are E_NOTIMPL shells - never used for
//     real work; doctor probes the Document one only to report its HRESULT (that call is safe).
//   * LISP results come back through GetLispVariable (scalars only) - no lists, no arrays.
//   * Every SendCommand string stays well below 2 KB and its LASTPROMPT is checked afterwards
//     (a "Missing:" prompt or a trailing ">" continuation means the command state is polluted).
//   * A burst of writes is wrapped in StartUndoMark/EndUndoMark so the user gets one Ctrl+Z.
//   * Temporary files only under %TEMP%\zwcad-cooperate\ (DGS encrypts .dxf/.dwg by extension).
//   * Instances we started are closed with Close(false) + Quit(); nothing is ever saved.
//
// Chinese literals are written as \uXXXX escapes on purpose so the source file stays pure ASCII
// (PowerShell 5.1 / csc round trips through ANSI code pages are the classic way to corrupt them).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZwcHelper
{
    // ======================================================================================
    // OLE message filter - busy retry (port of the WPS helper, policy from dsh-office).
    // A CAD that is mid-command / showing a modal dialog rejects incoming COM calls; without
    // this the command fails instantly. With it OLE retries every 150 ms for up to 6 s.
    // ======================================================================================
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal sealed class MessageFilter : IOleMessageFilter
    {
        public const int RetryAfterMs = 150;
        public const int GiveUpAfterMs = 6000;
        private const int ServerCallRetryLater = 2;
        private const int PendingWaitDefault = 2;
        private const int ServerCallHandled = 0;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        private static MessageFilter _installed;
        public static int LastHResult = -1;

        public static int Register()
        {
            MessageFilter filter = new MessageFilter();
            IOleMessageFilter old = null;
            int hr;
            try { hr = CoRegisterMessageFilter(filter, out old); }
            catch (Exception) { LastHResult = -1; return -1; }
            _installed = filter;
            LastHResult = hr;
            return hr;
        }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        { return ServerCallHandled; }

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType == ServerCallRetryLater && dwTickCount < GiveUpAfterMs) return RetryAfterMs;
            return -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        { return PendingWaitDefault; }
    }

    // ======================================================================================
    // Ordered JSON map + writer + tiny recursive-descent parser (no external assembly).
    // ======================================================================================
    internal sealed class JMap
    {
        private readonly List<string> _keys = new List<string>();
        // Keys are matched case-insensitively because the CLI wrapper lower-cases flag names
        // ("-DryRun" arrives as "dryrun") while the helper asks for "dryRun".
        private readonly Dictionary<string, object> _vals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _keys.Count; } }
        public List<string> Keys { get { return _keys; } }

        public JMap Add(string key, object value)
        {
            if (_vals.ContainsKey(key)) _vals[key] = value;
            else { _keys.Add(key); _vals[key] = value; }
            return this;
        }

        public bool Has(string key) { return _vals.ContainsKey(key); }

        public object Get(string key)
        {
            object v;
            if (_vals.TryGetValue(key, out v)) return v;
            return null;
        }

        public string Str(string key, string dflt)
        {
            object v = Get(key);
            if (v == null) return dflt;
            string s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public double Dbl(string key, double dflt)
        {
            object v = Get(key);
            if (v == null) return dflt;
            double d;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return dflt;
        }

        public int Int(string key, int dflt) { return (int)Math.Round(Dbl(key, dflt)); }

        public bool Bool(string key, bool dflt)
        {
            object v = Get(key);
            if (v == null) return dflt;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return dflt;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        public JMap Map(string key)
        {
            object v = Get(key);
            JMap m = v as JMap;
            return m;
        }

        public List<object> List(string key)
        {
            object v = Get(key);
            List<object> l = v as List<object>;
            return l;
        }
    }

    internal static class Json
    {
        // ---------------------------------------------------------------- writer
        public static string Write(object v)
        {
            StringBuilder sb = new StringBuilder(4096);
            WriteValue(sb, v);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is JMap) { WriteMap(sb, (JMap)v); return; }
            List<object> list = v as List<object>;
            if (list != null) { WriteList(sb, list); return; }
            if (v is int || v is long) { sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is Array)
            {
                List<object> tmp = new List<object>();
                foreach (object o in (Array)v) tmp.Add(o);
                WriteList(sb, tmp);
                return;
            }
            System.Collections.IEnumerable en = v as System.Collections.IEnumerable;
            if (en != null)
            {
                List<object> tmp = new List<object>();
                foreach (object o in en) tmp.Add(o);
                WriteList(sb, tmp);
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteList(StringBuilder sb, List<object> list)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, list[i]);
            }
            sb.Append(']');
        }

        private static void WriteMap(StringBuilder sb, JMap m)
        {
            sb.Append('{');
            for (int i = 0; i < m.Keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteString(sb, m.Keys[i]);
                sb.Append(':');
                WriteValue(sb, m.Get(m.Keys[i]));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- parser
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("empty JSON");
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length) throw new FormatException("trailing garbage at " + i);
            return v;
        }

        public static object ParseFile(string path)
        {
            return Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("expected " + lit + " at " + i);
            i += lit.Length;
        }

        private static JMap ParseObject(string s, ref int i)
        {
            JMap m = new JMap();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return m; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected : at " + i);
                i++;
                object val = ParseValue(s, ref i);
                m.Add(key, val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return m; }
                throw new FormatException("expected , or } at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected , or ] at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("expected value at " + start);
            string tok = s.Substring(start, i - start);
            long li;
            if (tok.IndexOf('.') < 0 && tok.IndexOf('e') < 0 && tok.IndexOf('E') < 0 && long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out li))
                return li;
            double d;
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("bad number " + tok);
        }
    }

    // ======================================================================================
    // Errors / context
    // ======================================================================================
    internal sealed class ZwcError : Exception
    {
        public string Code;
        public string Hint;
        public string Detail;
        public ZwcError(string code, string message, string hint, string detail)
            : base(message)
        {
            Code = code; Hint = hint; Detail = detail;
        }
    }

    internal sealed class Ctx
    {
        public List<object> Warnings = new List<object>();
        public JMap DocMap;
        public JMap AppMap;
        public string UndoToken;
        public bool FpChanged;   // set when the drawing changed outside this session before a write
        public bool PlanOnly;       // dry-run/plan: resolutions may fall back to any open drawing (no writes)
        public JMap LispMeta;         // M3-E5: timing/size of the LISP file scan when used
        public object CachedCad;       // per-request app cache so the body does not re-attach
        public object CachedDoc;        // resolved once per write command (Stability.RunWrite)
        public string CachedMatchedBy;
        public JMap Result;

        public void Warn(string code, string message)
        {
            JMap w = new JMap();
            w.Add("code", code);
            w.Add("message", message);
            Warnings.Add(w);
        }
    }

    // ======================================================================================
    // COM late-binding helpers
    // ======================================================================================
    internal static class Com
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Diagnostic text of the most recent property-read failure (see TryGet).</summary>
        public static string LastError = null;

        public static object Get(object o, string name)
        {
            if (o == null) throw new ZwcError("COM_NULL", "COM object is null while reading '" + name + "'", null, null);
            return o.GetType().InvokeMember(name, F | BindingFlags.GetProperty, null, o, null);
        }

        /// <summary>How many times a property read is retried. M3-E1 default is 1 (no retry): the
        /// old unconditional 6x60 ms loop turned one 624-entity read into 209 seconds, because most
        /// failures were PERMANENT (a property that does not exist on that entity type) and each
        /// attempt paid 60 ms for nothing. -Strict restores the old behaviour for diagnostics.</summary>
        public static int ReadRetries = 1;

        /// <summary>Permanent COM failures: retrying cannot help, so fail fast.</summary>
        public static bool IsPermanentReadFailure(Exception e)
        {
            COMException ce = e as COMException;
            if (ce == null)
            {
                TargetInvocationException tie = e as TargetInvocationException;
                if (tie != null) ce = tie.InnerException as COMException;
            }
            if (ce == null) return false;
            int hr = ce.HResult;
            return hr == unchecked((int)0x80020006)   // DISP_E_UNKNOWNNAME
                || hr == unchecked((int)0x80020003)   // DISP_E_MEMBERNOTFOUND
                || hr == unchecked((int)0x8002000B)   // DISP_E_BADINDEX
                || hr == unchecked((int)0x80004002);  // E_NOINTERFACE
        }

        /// <summary>Read a property with the M3-E1 policy: one attempt by default, and no retry at
        /// all for permanent failures (unknown name / member not found). A transient rejection
        /// (CAD momentarily busy right after a burst of writes) still gets retried when -Strict
        /// raised ReadRetries.</summary>
        public static object TryGet(object o, string name)
        {
            if (o == null) return null;
            int tries = ReadRetries < 1 ? 1 : ReadRetries;
            for (int attempt = 0; attempt < tries; attempt++)
            {
                try { return Get(o, name); }
                catch (Exception ex)
                {
                    LastError = name + ": " + Hr(ex);
                    if (IsPermanentReadFailure(ex)) break;
                    if (attempt + 1 < tries) Thread.Sleep(60);
                }
            }
            return null;
        }

        /// <summary>Read a property and report whether it really came back (null is a legitimate
        /// value for some properties, so callers that need certainty use this).</summary>
        public static bool TryGetOk(object o, string name, out object value)
        {
            value = null;
            if (o == null) return false;
            try { value = Get(o, name); return true; }
            catch (Exception ex) { LastError = name + ": " + Hr(ex); return false; }
        }

        /// <summary>BoundingBox via the single GetBoundingBox(min,max) call; much cheaper than
        /// reading every vertex. Returns false when the entity does not support it.</summary>
        public static bool TryGetBBox(object e, out double[] min, out double[] max)
        {
            min = null; max = null;
            if (e == null) return false;
            object[] parms = new object[] { null, null };
            try
            {
                e.GetType().InvokeMember("GetBoundingBox",
                    F | BindingFlags.InvokeMethod, null, e, parms);
            }
            catch (Exception ex) { LastError = "GetBoundingBox: " + Hr(ex); return false; }
            min = Pt(parms[0]);
            max = Pt(parms[1]);
            return min != null && max != null;
        }

        public static object Call(object o, string name, params object[] a)
        {
            if (o == null) throw new ZwcError("COM_NULL", "COM object is null while calling '" + name + "'", null, null);
            return o.GetType().InvokeMember(name, F | BindingFlags.InvokeMethod, null, o, a);
        }

        public static object TryCall(object o, string name, params object[] a)
        {
            if (o == null) return null;
            try { return Call(o, name, a); }
            catch (Exception) { return null; }
        }

        /// <summary>Invoke a void COM method and report whether it succeeded. Needed because a
        /// successful void call also returns null, so TryCall() != null cannot be used as a test
        /// (StartUndoMark/EndUndoMark are void and were being mis-reported as unavailable).</summary>
        public static bool TryInvoke(object o, string name, params object[] a)
        {
            if (o == null) return false;
            try { Call(o, name, a); return true; }
            catch (Exception) { return false; }
        }

        public static void Set(object o, string name, object v)
        {
            if (o == null) return;
            o.GetType().InvokeMember(name, F | BindingFlags.SetProperty, null, o, new object[] { v });
        }

        public static bool TrySet(object o, string name, object v)
        {
            try { Set(o, name, v); return true; }
            catch (Exception) { return false; }
        }

        public static string Str(object v)
        {
            if (v == null) return null;
            string s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Dbl(object v)
        {
            if (v == null) return 0.0;
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is float) return (float)v;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static int I32(object v)
        {
            if (v == null) return 0;
            if (v is int) return (int)v;
            return (int)Math.Round(Dbl(v));
        }

        public static bool Bool(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;
            long l;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l != 0;
            return string.Equals(s, "True", StringComparison.OrdinalIgnoreCase) || s == "-1";
        }

        /// <summary>Convert a COM point (VARIANT with 2-3 doubles) into a double[3].</summary>
        public static double[] Pt(object v)
        {
            if (v == null) return null;
            Array a = v as Array;
            if (a == null) return null;
            double[] r = new double[] { 0, 0, 0 };
            int k = 0;
            foreach (object o in a)
            {
                if (k > 2) break;
                r[k++] = Dbl(o);
            }
            return r;
        }

        /// <summary>Flatten any COM double array / safe array into double[].</summary>
        public static double[] Flat(object v)
        {
            if (v == null) return new double[0];
            Array a = v as Array;
            if (a == null) return new double[0];
            double[] r = new double[a.Length];
            int k = 0;
            foreach (object o in a) r[k++] = Dbl(o);
            return r;
        }

        public static long HwndLong(object v)
        {
            if (v == null) return 0;
            if (v is int) { uint u = unchecked((uint)(int)v); return (long)u; }
            if (v is long) return (long)v;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        /// <summary>HandleToObject with a few retries (read-only, so retrying is safe).</summary>
        public static object EntityByHandle(object doc, string handle)
        {
            if (doc == null || string.IsNullOrEmpty(handle)) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                object e = TryCall(doc, "HandleToObject", handle);
                if (e != null) return e;
                Thread.Sleep(20);
            }
            return null;
        }

        public static string Hr(Exception e)
        {
            COMException ce = e as COMException;
            if (ce != null) return "0x" + ce.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ce.Message;
            TargetInvocationException tie = e as TargetInvocationException;
            if (tie != null && tie.InnerException != null)
            {
                COMException ci = tie.InnerException as COMException;
                if (ci != null) return "0x" + ci.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ci.Message;
                return tie.InnerException.GetType().Name + ": " + tie.InnerException.Message;
            }
            return e.GetType().Name + ": " + e.Message;
        }
    }

    // ======================================================================================
    // COM collection indexing
    //
    // ZWCAD Classic 16 is inconsistent about the lower bound: on this machine
    //   Document.ModelSpace.Item(0) -> first entity    (0-based, Item(Count) is out of range)
    //   Application.Documents.Item(1) -> first drawing (1-based)
    // AutoCAD's own collections are 1-based, so the base is detected per collection instead of
    // being assumed. Getting this wrong silently drops the first entity of every read (the first
    // section run lost its ground polyline exactly this way).
    // ======================================================================================
    internal static class Coll
    {
        public static int DetectBase(object coll, int count)
        {
            if (coll == null || count <= 0) return 1;
            object zero = Com.TryCall(coll, "Item", 0);
            return zero == null ? 1 : 0;
        }

        public static object At(object coll, int baseIndex, int k, int count)
        {
            int idx = baseIndex + k;
            if (idx >= count + baseIndex) return null;
            return Com.TryCall(coll, "Item", idx);
        }
    }

    // ======================================================================================
    // Session state (%TEMP%\zwcad-cooperate\state.json): which instance we launched, caption
    // originals, undo tokens. M1 has no daemon, so this file is the only cross-process memory.
    // ======================================================================================
    internal static class State
    {
        public static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "zwcad-cooperate");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
            catch (Exception) { }
            return d;
        }

        public static string FilePath() { return Path.Combine(Dir(), "state.json"); }

        public const int SchemaVersion = 2;
        // In-memory session cache: every `entity` used to Load+Save %TEMP%\state.json, i.e. file
        // operations on the hot path (~20-40 ms with the endpoint security driver). The file is
        // written at most once per second (or on Flush), so write latency no longer depends on it.
        private static JMap _cached;
        private static bool _dirty;
        private static DateTime _lastSave = DateTime.MinValue;


        public static JMap Load()
        {
            if (_cached != null) return _cached;
            JMap m = null;
            try
            {
                string p = FilePath();
                if (File.Exists(p))
                {
                    object o = Json.ParseFile(p);
                    m = o as JMap;
                }
            }
            catch (Exception) { m = null; }
            if (m == null) m = new JMap();
            bool dirty = Sanitize(m);
            if (dirty) { try { Save(m); } catch (Exception) { } }
            _cached = m;
            return m;
        }

        /// <summary>Self-healing for the session file: a crashed/closed CAD leaves dead pids behind,
        /// and stale entries make launched/ours wrong (open then reports launched:false for an
        /// instance that no longer exists, or finish thinks it owns someone else's process).
        /// Drop every launched-instance entry whose pid is not alive, clear the session when none
        /// survive, and rebuild the file when the schema version is unknown/old.</summary>
        private static bool Sanitize(JMap m)
        {
            bool dirty = false;
            object sv = m.Get("schemaVersion");
            long ver = 0;
            try { ver = sv == null ? 0 : Convert.ToInt64(sv, CultureInfo.InvariantCulture); }
            catch (Exception) { ver = 0; }
            if (ver != SchemaVersion) { m.Add("schemaVersion", SchemaVersion); dirty = true; }
            JMap sess = m.Map("session");
            if (sess == null) return dirty;
            List<object> all = sess.List("instances");
            if (all == null)
            {
                if (sess.Int("hwnd", 0) != 0 || sess.Int("pid", 0) != 0)
                {
                    m.Add("session", null);
                    m.Add("openedDocs", null);
                    return true;
                }
                return dirty;
            }
            List<object> alive = new List<object>();
            foreach (object o in all)
            {
                JMap om = o as JMap;
                if (om == null) continue;
                int pid = om.Int("pid", 0);
                if (pid > 0 && ProcessAlive(pid)) alive.Add(om);
                else dirty = true;
            }
            if (alive.Count != all.Count) { sess.Add("instances", alive); dirty = true; }
            if (alive.Count == 0)
            {
                m.Add("session", null);
                m.Add("openedDocs", null);
                return true;
            }
            return dirty;
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch (Exception) { return false; }
        }

        public static void Save(JMap st)
        {
            _cached = st;
            _dirty = true;
            if ((DateTime.Now - _lastSave).TotalMilliseconds > 1000) Flush();
        }

        /// <summary>Write the cached state file if it is dirty (no-op when clean).</summary>
        public static void Flush()
        {
            if (!_dirty || _cached == null) return;
            try
            {
                string p = FilePath();
                string tmp = p + "." + Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tmp, Json.Write(_cached), new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            catch (Exception) { }
        }

        public static JMap Session(JMap st) { return st.Map("session"); }

        public static void ClearSession(JMap st)
        {
            st.Add("session", null);
        }

        public static JMap Tokens(JMap st)
        {
            JMap t = st.Map("tokens");
            if (t == null) { t = new JMap(); st.Add("tokens", t); }
            return t;
        }

        public static JMap Captions(JMap st)
        {
            JMap c = st.Map("captions");
            if (c == null) { c = new JMap(); st.Add("captions", c); }
            return c;
        }

        /// <summary>Keep the token table small: drop everything but the newest 20 tokens.</summary>
        /// <summary>True when this hwnd belongs to an instance started by this tool. A session can
        /// have launched more than one product (Classic and 2025 side by side), so the identity is
        /// kept as a list, not a single hwnd.</summary>
        public static bool OwnsHwnd(JMap st, long hwnd)
        {
            JMap sess = Session(st);
            if (sess == null || !sess.Bool("launched", false)) return false;
            if (sess.Int("hwnd", 0) == (int)hwnd) return true;
            List<object> all = sess.List("instances");
            if (all != null)
            {
                foreach (object o in all)
                {
                    JMap om = o as JMap;
                    if (om != null && om.Int("hwnd", 0) == (int)hwnd) return true;
                }
            }
            return false;
        }

        /// <summary>Forget one launched instance; clears the session once none are left.</summary>
        public static void RemoveInstance(JMap st, long hwnd)
        {
            JMap sess = Session(st);
            if (sess == null) return;
            List<object> keep = new List<object>();
            List<object> all = sess.List("instances");
            if (all != null)
            {
                foreach (object o in all)
                {
                    JMap om = o as JMap;
                    if (om != null && om.Int("hwnd", 0) != (int)hwnd) keep.Add(o);
                }
            }
            sess.Add("instances", keep);
            if (keep.Count == 0)
            {
                ClearSession(st);
                st.Add("openedDocs", null);
            }
        }
        public static void PruneTokens(JMap st)
        {
            try
            {
                JMap t = Tokens(st);
                while (t.Keys.Count > 20) t.Keys.RemoveAt(0);
                JMap fresh = new JMap();
                foreach (string k in t.Keys) fresh.Add(k, t.Get(k));
                st.Add("tokens", fresh);
            }
            catch (Exception) { }
        }
    }

    // ======================================================================================
    // CAD application attachment and document resolution
    // ======================================================================================
    internal sealed class Cad
    {
        public object App;
        public string ProgId;
        public bool Launched;
        public long Hwnd;
        public static bool SuppressReveal;   // headless/test: -NoReveal
        public bool WindowVisible;
        public string Product;          // product that actually answered (auto-detected)
        public string MappedBy;         // explicit | process | default
        public string ExpectedProduct;  // product the resolver picked from the running processes
        public JMap Detection;          // full detection record (also surfaced by doctor)

        // ZWCAD Classic 16.0 registers ZWCAD.Application (and .16). AutoCAD is the M4 target;
        // the ProgID list is already here so -App autocad keeps working once that is verified.
        // ZWCAD ships MULTIPLE products that all register the same UNVERSIONED ProgID, and the last
        // product started rewrites that mapping. Measured A/B (2026-11-04): after ZWCAD 2025 ran,
        //   ZWCAD.Application -> {2F671EA1} (2025);
        // after Classic 16 started, the same key -> {FB081ED6} (Classic).
        // So the unversioned ProgID identifies "whatever started last", NOT the product the user is
        // looking at, and hardcoding one version is wrong in the other direction (a user with only
        // 2025 open would silently get a fresh Classic started). Resolution order is therefore:
        //   explicit -ProgId  >  the product of the RUNNING process(es)  >  documented default.
        // With two different products running we refuse to guess and return MULTI_PRODUCT.
        internal sealed class ProdInfo
        {
            public string Label;
            public string ProgId;
            public string Clsid;
        }

        internal sealed class ProgChoice
        {
            public string[] Candidates;
            public string MappedBy;        // explicit | process | default
            public bool MultiProduct;
            public List<JMap> Running = new List<JMap>();
            public JMap Detection;
            public string SelectedProduct;
        }

        public static string ExplicitProgId(JMap req, JMap args)
        {
            string p = args.Str("progId", null);
            if (string.IsNullOrEmpty(p)) p = req.Str("progId", null);
            return string.IsNullOrEmpty(p) ? null : p.Trim();
        }
        public static List<ProdInfo> KnownProducts(string kind)
        {
            List<ProdInfo> l = new List<ProdInfo>();
            string k = (kind == null ? "" : kind.Trim().ToLowerInvariant());
            if (k.Length == 0 || k == "zwcad")
            {
                l.Add(new ProdInfo { Label = "ZWCAD Classic 16", ProgId = "ZWCAD.Application.16", Clsid = "fb081ed6-0d7e-489f-a6b7-f7ceaec10661" });
                l.Add(new ProdInfo { Label = "ZWCAD 2025", ProgId = "ZWCAD.Application.2025", Clsid = "2f671ea1-669f-11e7-91b7-bc5ff42ac839" });
            }
            else if (k == "autocad")
            {
                // Only one AutoCAD is installed here, and no dynamic rewrite of the unversioned
                // ProgID was observed; keep the versioned ProgID first because on this machine
                // "AutoCAD.Application" is not registered at all in the 64-bit view.
                l.Add(new ProdInfo { Label = "AutoCAD 2007", ProgId = "AutoCAD.Application.17", Clsid = "28b7aa99-c0f9-4c47-995e-8a8d729603a1" });
            }
            return l;
        }

        public static string UnversionedProgId(string kind)
        {
            string k = (kind == null ? "" : kind.Trim().ToLowerInvariant());
            if (k.IndexOf("autocad", StringComparison.Ordinal) >= 0) return "AutoCAD.Application";
            if (k.IndexOf('.') >= 0) return kind;
            return "ZWCAD.Application";
        }

        public static string ProductFromClsid(string clsid)
        {
            if (string.IsNullOrEmpty(clsid)) return null;
            string c = clsid.Trim().Trim('{', '}').ToLowerInvariant();
            if (c == "fb081ed6-0d7e-489f-a6b7-f7ceaec10661") return "ZWCAD Classic 16";
            if (c == "2f671ea1-669f-11e7-91b7-bc5ff42ac839") return "ZWCAD 2025";
            if (c == "2f671eb4-669f-11e7-91b7-bc5ff42ac839") return "ZWCAD 2025";
            if (c == "28b7aa99-c0f9-4c47-995e-8a8d729603a1") return "AutoCAD 2007";
            return null;
        }

        private static string ProcessPath(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    string f = p.MainModule.FileName;
                    if (!string.IsNullOrEmpty(f)) return f;
                }
            }
            catch (Exception) { }
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + pid);
                foreach (ManagementObject mo in s.Get())
                {
                    object v = mo["ExecutablePath"];
                    if (v != null)
                    {
                        string f = Convert.ToString(v, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(f)) return f;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string ProcessTitle(Process p)
        {
            try { return p.MainWindowTitle; }
            catch (Exception) { return null; }
        }

        private static string ClassifyProcess(string kind, string exePath, string title)
        {
            string b = ((exePath == null ? "" : exePath) + "|" + (title == null ? "" : title)).ToLowerInvariant();
            string k = (kind == null ? "" : kind.Trim().ToLowerInvariant());
            if (k.IndexOf("autocad", StringComparison.Ordinal) >= 0)
                return (b.IndexOf("acad.exe", StringComparison.Ordinal) >= 0 || b.IndexOf("autocad", StringComparison.Ordinal) >= 0)
                    ? "AutoCAD 2007" : null;
            if (b.IndexOf("zwcad 2025", StringComparison.Ordinal) >= 0) return "ZWCAD 2025";
            if (b.IndexOf("classic", StringComparison.Ordinal) >= 0) return "ZWCAD Classic 16";
            if (b.IndexOf("2025", StringComparison.Ordinal) >= 0) return "ZWCAD 2025";
            return null;
        }

        /// <summary>The CAD processes that are actually running, classified by product using their
        /// executable path (WMI fallback) and window title fallback.</summary>
        public static List<JMap> DetectRunning(string kind)
        {
            List<JMap> found = new List<JMap>();
            string k = (kind == null ? "" : kind.Trim().ToLowerInvariant());
            string[] names = k.IndexOf("autocad", StringComparison.Ordinal) >= 0
                ? new string[] { "acad" } : new string[] { "ZWCAD" };
            foreach (string nm in names)
            {
                Process[] ps = null;
                try { ps = Process.GetProcessesByName(nm); }
                catch (Exception) { continue; }
                foreach (Process p in ps)
                {
                    int pid = 0;
                    string path = null, title = null;
                    try { pid = p.Id; } catch (Exception) { }
                    try { path = ProcessPath(pid); } catch (Exception) { }
                    try { title = ProcessTitle(p); } catch (Exception) { }
                    JMap m = new JMap();
                    m.Add("pid", pid);
                    m.Add("product", ClassifyProcess(kind, path, title));
                    m.Add("exePath", path);
                    m.Add("title", title);
                    found.Add(m);
                    try { p.Dispose(); } catch (Exception) { }
                }
            }
            return found;
        }

        public static JMap DescribeUnversioned(string kind)
        {
            string un = UnversionedProgId(kind);
            JMap m = new JMap();
            m.Add("progId", un);
            bool registered = false;
            string clsid = null;
            try
            {
                Type t = Type.GetTypeFromProgID(un, false);
                if (t != null) { registered = true; clsid = t.GUID.ToString("D"); }
            }
            catch (Exception) { }
            m.Add("registered", registered);
            m.Add("clsid", clsid);
            m.Add("product", ProductFromClsid(clsid));
            return m;
        }

        public static ProgChoice ResolveProgIds(string kind, string explicitProgId, Ctx ctx)
        {
            ProgChoice pc = new ProgChoice();
            List<ProdInfo> known = KnownProducts(kind);
            List<JMap> running = DetectRunning(kind);
            pc.Running = running;

            List<string> labels = new List<string>();
            List<object> runningOut = new List<object>();
            foreach (JMap r in running)
            {
                JMap m = new JMap();
                m.Add("product", r.Str("product", null));
                m.Add("pid", r.Int("pid", 0));
                m.Add("exePath", r.Str("exePath", null));
                m.Add("title", r.Str("title", null));
                runningOut.Add(m);
                string lab = r.Str("product", null);
                if (!string.IsNullOrEmpty(lab) && !labels.Contains(lab)) labels.Add(lab);
            }

            List<object> prodList = new List<object>();
            foreach (ProdInfo pi in known)
            {
                JMap pm = new JMap();
                pm.Add("product", pi.Label);
                pm.Add("progId", pi.ProgId);
                bool registered = false;
                string clsid = pi.Clsid;
                if (!string.IsNullOrEmpty(pi.ProgId))
                {
                    try
                    {
                        Type tp = Type.GetTypeFromProgID(pi.ProgId, false);
                        if (tp != null) { registered = true; clsid = tp.GUID.ToString("D"); }
                    }
                    catch (Exception) { }
                }
                pm.Add("registered", registered);
                pm.Add("clsid", clsid);
                JMap inst = null;
                foreach (JMap r in running)
                {
                    if (string.Equals(r.Str("product", null), pi.Label, StringComparison.OrdinalIgnoreCase)) { inst = r; break; }
                }
                pm.Add("runningInstance", inst);
                prodList.Add(pm);
            }

            JMap det = new JMap();
            det.Add("products", prodList);
            List<object> labelsOut = new List<object>(); foreach (string lab in labels) labelsOut.Add(lab); det.Add("runningProducts", labelsOut);
            det.Add("unversionedProgIdPointsTo", DescribeUnversioned(kind));
            det.Add("processes", runningOut);
            pc.Detection = det;

            JMap unInfo = det.Map("unversionedProgIdPointsTo");

            if (!string.IsNullOrEmpty(explicitProgId))
            {
                pc.MappedBy = "explicit";
                pc.Candidates = new string[] { explicitProgId };
                return pc;
            }

            if (labels.Count > 1)
            {
                // two different products are running: guessing here would drive the wrong CAD
                pc.MappedBy = "process";
                pc.MultiProduct = true;
                return pc;
            }

            if (labels.Count == 1)
            {
                pc.MappedBy = "process";
                pc.SelectedProduct = labels[0];
                List<string> cands = new List<string>();
                foreach (ProdInfo pi in known)
                {
                    if (string.Equals(pi.Label, pc.SelectedProduct, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(pi.ProgId)) cands.Add(pi.ProgId);
                }
                string un = UnversionedProgId(kind);
                if (!cands.Contains(un)) cands.Add(un);
                foreach (ProdInfo pi in known)
                {
                    if (!string.IsNullOrEmpty(pi.ProgId) && !cands.Contains(pi.ProgId)) cands.Add(pi.ProgId);
                }
                pc.Candidates = cands.ToArray();
                return pc;
            }

            // nothing running: this only matters for -Launch, where we keep the documented default
            // order and say so (the unversioned ProgID may have been rewritten by the last product).
            pc.MappedBy = "default";
            List<string> dflt = new List<string>();
            string unProg = UnversionedProgId(kind);
            foreach (ProdInfo pi in known)
            {
                if (!string.IsNullOrEmpty(pi.ProgId)) { dflt.Add(pi.ProgId); break; }
            }
            if (!dflt.Contains(unProg)) dflt.Add(unProg);
            foreach (ProdInfo pi in known)
            {
                if (!string.IsNullOrEmpty(pi.ProgId) && !dflt.Contains(pi.ProgId)) dflt.Add(pi.ProgId);
            }
            pc.Candidates = dflt.ToArray();
            return pc;
        }

        public static string SuggestProgId(string kind, string product)
        {
            foreach (ProdInfo pi in KnownProducts(kind))
            {
                if (string.Equals(pi.Label, product, StringComparison.OrdinalIgnoreCase)) return pi.ProgId;
            }
            return null;
        }

        public static string ProductName(object app)
        {
            string full = Com.Str(Com.TryGet(app, "FullName"));
            string blob = (full == null ? "" : full).ToLowerInvariant();
            if (blob.IndexOf("zwcad 2025", StringComparison.Ordinal) >= 0) return "ZWCAD 2025";
            if (blob.IndexOf("classic", StringComparison.Ordinal) >= 0) return "ZWCAD Classic 16";
            if (blob.IndexOf("acad.exe", StringComparison.Ordinal) >= 0) return "AutoCAD 2007";
            string ver = Com.Str(Com.TryGet(app, "Version"));
            return ver == null ? "unknown" : ver;
        }

        private static bool SameProduct(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
            string x = a.ToLowerInvariant(), y = b.ToLowerInvariant();
            if (x == y) return true;
            if (x.IndexOf("2025", StringComparison.Ordinal) >= 0 && y.IndexOf("2025", StringComparison.Ordinal) >= 0) return true;
            if (x.IndexOf("classic", StringComparison.Ordinal) >= 0 && y.IndexOf("classic", StringComparison.Ordinal) >= 0) return true;
            if (x.IndexOf("autocad", StringComparison.Ordinal) >= 0 && y.IndexOf("autocad", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch (Exception) { return false; }
        }

        // Cross-request app cache (E3): ResolveProgIds + WMI detection costs 100-300 ms per
        // request and dominated the concurrent floor. Reuse the COM app only while the same
        // PID/HWND is alive and answering; a restarted CAD invalidates it.
        private static Cad _cacheCad;
        private static int _cachePid = -1;
        private static int _cacheHwnd;
        private static string _cacheKind;
        private static string _cacheProgId;
        private static DateTime _cacheAt = DateTime.MinValue;

        public static Cad Attach(string kind, bool launch, Ctx ctx, string explicitProgId)
        {
            if (!launch && _cacheCad != null && string.Equals(_cacheKind, kind, StringComparison.OrdinalIgnoreCase)
                && (_cacheProgId == explicitProgId || string.IsNullOrEmpty(explicitProgId))
                && (DateTime.Now - _cacheAt).TotalSeconds < 120)
            {
                // Trust the cached app for 3 s without a COM round-trip: on ZWCAD 2025 each
                // cross-process COM validation costs 20-40 ms, which was the whole attach budget.
                if (ProcessAlive(_cachePid))
                {
                    if ((DateTime.Now - _cacheAt).TotalSeconds < 3.0)
                    {
                        ctx.AppMap = _cacheCad.AppInfo();
                        return _cacheCad;
                    }
                    try
                    {
                        if (Com.TryGet(_cacheCad.App, "Name") != null)
                        {
                            ctx.AppMap = _cacheCad.AppInfo();
                            return _cacheCad;
                        }
                    }
                    catch (Exception) { }
                }
                _cacheCad = null;
            }
            ProgChoice pc = ResolveProgIds(kind, explicitProgId, ctx);
            Cad c = AttachWithChoice(kind, launch, ctx, pc, explicitProgId);
            try
            {
                JMap info = c.AppInfo();
                _cacheCad = c;
                _cachePid = Com.I32(Com.TryGet(info, "pid"));
                _cacheHwnd = Com.I32(Com.TryGet(info, "hwnd"));
                _cacheKind = kind;
                _cacheProgId = explicitProgId;
                _cacheAt = DateTime.Now;
            }
            catch (Exception) { }
            return c;
        }

        public static Cad AttachWithChoice(string kind, bool launch, Ctx ctx, ProgChoice pc, string explicitProgId)
        {
            Cad c = new Cad();
            c.MappedBy = pc.MappedBy;
            c.Detection = pc.Detection;
            c.ExpectedProduct = pc.SelectedProduct;

            if (pc.MultiProduct)
            {
                List<string> names = new List<string>();
                List<object> prods = new List<object>();
                List<string> seen = new List<string>();
                foreach (JMap r in pc.Running)
                {
                    string lab = r.Str("product", null);
                    if (string.IsNullOrEmpty(lab) || seen.Contains(lab)) continue;
                    seen.Add(lab);
                    names.Add(lab + " (pid " + r.Int("pid", 0) + ")");
                    JMap m = new JMap();
                    m.Add("product", lab);
                    m.Add("pid", r.Int("pid", 0));
                    m.Add("exePath", r.Str("exePath", null));
                    m.Add("suggestedProgId", SuggestProgId(kind, lab));
                    prods.Add(m);
                }
                throw new ZwcError("MULTI_PRODUCT",
                    "two different CAD products are running (" + string.Join("; ", names.ToArray())
                    + "); refusing to guess which one to drive",
                    "pass -ProgId <ZWCAD.Application.16|ZWCAD.Application.2025> to choose, or close one of them",
                    Json.Write(prods));
            }

            string[] pids = pc.Candidates;
            if (pids == null || pids.Length == 0)
                throw new ZwcError("NO_PRODUCT", "no usable ProgID for kind '" + kind + "'", null, Json.Write(pc.Detection));
            Exception last = null;

            // 1) attach to a running instance - never start one implicitly
            foreach (string pid in pids)
            {
                try
                {
                    c.App = Marshal.GetActiveObject(pid);
                    if (c.App != null) { c.ProgId = pid; c.Launched = false; break; }
                }
                catch (Exception e) { last = e; }
            }

            // 2) -Launch: create the instance
            if (c.App == null && launch)
            {
                foreach (string pid in pids)
                {
                    try
                    {
                        Type t = Type.GetTypeFromProgID(pid, true);
                        if (t == null) continue;
                        c.App = Activator.CreateInstance(t);
                        if (c.App != null) { c.ProgId = pid; c.Launched = true; break; }
                    }
                    catch (Exception e) { last = e; }
                }
            }

            if (c.App == null)
            {
                string msg = "no running " + pids[0] + " instance";
                string hint = "open CAD first, or pass -Launch to start it";
                string detail = last == null ? null : Com.Hr(last);
                if (launch && last != null)
                {
                    msg = "could not attach to or start " + pids[0];
                    hint = "check that CAD is installed, licensed and not showing a modal dialog";
                }
                throw new ZwcError("NO_INSTANCE", msg, hint, detail);
            }

            c.Hwnd = Com.HwndLong(Com.TryGet(c.App, "HWND"));
            // UX: a COM CreateObject instance (or a leftover /Automation -Embedding process) has no
            // visible window. Users read "take over my drawing" as "a window shows up", so reveal it
            // before returning - and warn when we attached to something the user cannot see.
            try
            {
                bool winVisible = c.Hwnd != 0 && IsWindowVisible(new IntPtr(unchecked((int)c.Hwnd)));
                bool revealed = false;
                bool allowReveal = !(ctx != null && false);
                if (c.Launched || !winVisible)
                {
                    for (int attempt = 0; attempt < 24 && !revealed; attempt++)
                    {
                        c.Hwnd = Com.HwndLong(Com.TryGet(c.App, "HWND"));
                        if (c.Hwnd != 0)
                        {
                            JMap rev = Reveal(c, ctx);
                            c.WindowVisible = (bool)rev.Get("visible");
                            revealed = c.WindowVisible;
                        }
                        if (!revealed) Thread.Sleep(250);
                    }
                    if (!revealed) ctx.Warn("REVEAL_FAILED", "the CAD window could not be made visible (it may still be starting up)");
                }
                else c.WindowVisible = true;
            }
            catch (Exception) { }
            c.Product = ProductName(c.App);

            if (pc.MappedBy == "default")
                ctx.Warn("LAUNCH_DEFAULT_PRODUCT",
                    "no CAD process was running, so the default ProgID order was used ("
                    + string.Join(",", pids) + "); the unversioned ProgID may have been rewritten by the last product started");
            if (pc.MappedBy == "process" && !SameProduct(c.Product, pc.SelectedProduct))
                ctx.Warn("PRODUCT_MISMATCH", "process detection said " + pc.SelectedProduct
                    + " but the attached application reports " + c.Product);
            if (!string.IsNullOrEmpty(explicitProgId))
            {
                foreach (JMap r in pc.Running)
                {
                    string lab = r.Str("product", null);
                    if (!string.IsNullOrEmpty(lab) && !SameProduct(lab, c.Product))
                        ctx.Warn("PRODUCT_MISMATCH", "explicit -ProgId " + explicitProgId
                            + " attached " + c.Product + ", but a " + lab + " instance is also running (pid "
                            + r.Int("pid", 0) + ")");
                }
            }
            return c;
        }
        public List<object> Documents()
        {
            List<object> list = new List<object>();
            object coll = Com.TryGet(App, "Documents");
            if (coll == null) return list;
            int n = Com.I32(Com.TryGet(coll, "Count"));
            int b = Coll.DetectBase(coll, n);
            for (int k = 0; k < n; k++)
            {
                object d = Coll.At(coll, b, k, n);
                if (d != null) list.Add(d);
            }
            return list;
        }

        public object DocumentsCollection() { return Com.Get(App, "Documents"); }

        public object ActiveDocument() { return Com.TryGet(App, "ActiveDocument"); }

        private JMap _appInfoCache;
        public JMap AppInfo()
        {
            if (_appInfoCache != null) return _appInfoCache;
            JMap m = new JMap();
            m.Add("progId", ProgId);
            m.Add("product", Product);
            m.Add("mappedBy", MappedBy);
            m.Add("version", Com.Str(Com.TryGet(App, "Version")));
            m.Add("hwnd", Hwnd);
            m.Add("found", true);
            m.Add("launched", Launched);
            m.Add("pid", PidOfHwnd(Hwnd));
            m.Add("name", Com.Str(Com.TryGet(App, "Name")));
            m.Add("fullName", Com.Str(Com.TryGet(App, "FullName")));
            m.Add("caption", Com.Str(Com.TryGet(App, "Caption")));
            m.Add("windowVisible", WindowVisible);
            _appInfoCache = m;
            return m;
        }

        private static object _docInfoFor;
        private static JMap _docInfoCache;
        private static DateTime _docInfoAt = DateTime.MinValue;
        public static void InvalidateDocInfo() { _docInfoCache = null; }

        public static JMap DocInfo(object doc)
        {
            if (ReferenceEquals(_docInfoFor, doc) && _docInfoCache != null && (DateTime.Now - _docInfoAt).TotalMilliseconds < 3000)
                return _docInfoCache;
            JMap m = new JMap();
            string full = Com.Str(Com.TryGet(doc, "FullName"));
            m.Add("path", full);
            m.Add("fullName", full);
            m.Add("name", Com.Str(Com.TryGet(doc, "Name")));
            object saved = Com.TryGet(doc, "Saved");
            m.Add("saved", saved == null ? null : (object)Com.Bool(saved));
            object ro = Com.TryGet(doc, "ReadOnly");
            m.Add("readOnly", ro == null ? null : (object)Com.Bool(ro));
            object active = Com.TryGet(doc, "Active");
            if (active != null) m.Add("active", Com.Bool(active));
            int cnt = -1;
            try { cnt = Com.I32(Com.Get(Com.Get(doc, "ModelSpace"), "Count")); }
            catch (Exception) { }
            m.Add("count", cnt);
            _docInfoFor = doc; _docInfoCache = m; _docInfoAt = DateTime.Now;
            return m;
        }

        public static int PidOfHwnd(long hwnd)
        {
            if (hwnd == 0) return 0;
            try
            {
                int pid;
                GetWindowThreadProcessId(new IntPtr(unchecked((int)hwnd)), out pid);
                return pid;
            }
            catch (Exception) { return 0; }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        public static bool SetWindowCaption(long hwnd, string text)
        {
            if (hwnd == 0) return false;
            try { return SetWindowTextW(new IntPtr(unchecked((int)hwnd)), text); }
            catch (Exception) { return false; }
        }

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

        /// <summary>Make the CAD window visible, restore it from the taskbar and bring it to the
        /// foreground. This is the fix for the "user cannot see the window" incident: COM
        /// CreateObject starts a hidden /Automation -Embedding instance that never shows itself.</summary>
        internal static JMap Reveal(Cad cad, Ctx ctx)
        {
            JMap r = new JMap();
            r.Add("hwnd", cad == null ? 0L : cad.Hwnd);
            r.Add("visible", false);
            r.Add("shown", false);
            r.Add("foreground", false);
            r.Add("title", null);
            if (cad == null) return r;
            try { Com.TrySet(cad.App, "Visible", true); } catch (Exception) { }
            long hwndLong = cad.Hwnd;
            try { hwndLong = Com.HwndLong(Com.TryGet(cad.App, "HWND")); } catch (Exception) { }
            cad.Hwnd = hwndLong;
            r.Add("hwnd", hwndLong);
            if (hwndLong == 0)
            {
                ctx.Warn("REVEAL_FAILED", "the application has no main window handle (HWND=0)");
                return r;
            }
            IntPtr h = new IntPtr(unchecked((int)hwndLong));
            try
            {
                bool iconic = IsIconic(h);
                bool shown = ShowWindow(h, iconic ? 9 : 5);   // SW_RESTORE or SW_SHOW
                r.Add("shown", shown);
                SetForegroundWindow(h);
                r.Add("foreground", GetForegroundWindow() == h);
                r.Add("visible", IsWindowVisible(h));
                StringBuilder sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                r.Add("title", sb.ToString());
                if (!(bool)r.Get("visible"))
                    ctx.Warn("REVEAL_FAILED", "ShowWindow/SetForegroundWindow did not make the CAD window visible");
            }
            catch (Exception e) { ctx.Warn("REVEAL_FAILED", "reveal failed: " + e.Message); }
            return r;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
        private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        private static bool Eq(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool EqPath(string a, string b)
        {
            if (a == null || b == null) return false;
            string x = a.Trim().Replace('/', '\\');
            string y = b.Trim().Replace('/', '\\');
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private ZwcError DocNotFound(string wanted, List<object> docs)
        {
            List<object> cands = new List<object>();
            List<string> names = new List<string>();
            foreach (object d in docs)
            {
                JMap c = new JMap();
                string f = Com.Str(Com.TryGet(d, "FullName"));
                string n = Com.Str(Com.TryGet(d, "Name"));
                c.Add("name", n);
                c.Add("fullName", f);
                cands.Add(c);
                names.Add(n + " (" + f + ")");
            }
            string detail = Json.Write(cands);
            string msg = "document not found by exact FullName: " + (wanted == null ? "(none)" : wanted)
                       + "; open documents: " + string.Join("; ", names.ToArray());
            return new ZwcError("DOC_NOT_FOUND", msg,
                "pass -Doc with the exact full path (or the drawing name) of one of the candidates below", detail);
        }

        private static object _docCache;
        private static string _docCacheKey;

        internal static void RememberDoc(string docArg, string fullName, object doc)
        {
            // cache the FullName->Document resolution inside the daemon process; invalidated when
            // the document object stops answering or a write reports DOC_GONE
            string key = string.IsNullOrEmpty(docArg) ? fullName : docArg;
            if (string.IsNullOrEmpty(key)) return;
            _docCache = doc;
            _docCacheKey = key;
        }

        internal static void ForgetDocCache() { _docCache = null; _docCacheKey = null; }

        public object ResolveDoc(string docArg, bool allowActive, Ctx ctx, out string matchedBy)
        {
            matchedBy = null;
            if (!string.IsNullOrEmpty(docArg) && _docCache != null && string.Equals(_docCacheKey, docArg, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Com.TryGet(_docCache, "Name") != null)
                    {
                        matchedBy = "cached";
                        return _docCache;
                    }
                }
                catch (Exception) { }
                ForgetDocCache();
            }
            if (ctx != null && ctx.CachedDoc != null) { matchedBy = ctx.CachedMatchedBy; return ctx.CachedDoc; }
            List<object> docs = Documents();

            if (string.IsNullOrEmpty(docArg))
            {
                if (docs.Count == 0)
                    throw new ZwcError("NO_DOCUMENT", "no drawing is open in CAD",
                        "open one (open -New / -Doc <path>) or pass -Launch", null);
                if (docs.Count == 1) { matchedBy = "only"; Select(docs[0]); RememberDoc(docArg, null, docs[0]); return docs[0]; }
                if (allowActive)
                {
                    object ad = ActiveDocument();
                    if (ad != null)
                    {
                        matchedBy = "active";
                        ctx.Warn("DOC_ACTIVE_FALLBACK", "no -Doc given and " + docs.Count
                            + " drawings are open; falling back to ActiveDocument (can drift) - pass -Doc for a deterministic target");
                        return ad;
                    }
                }
                // Several drawings open and no -Doc: refuse instead of guessing. Reading whatever
                // drawing happens to be active is how a read returned 0 entities for a drawing that
                // had just been drawn into (ActiveDocument had drifted to an empty one).
                if (ctx != null && ctx.PlanOnly)
                {
                    object ad2 = ActiveDocument();
                    object pick = ad2 != null ? ad2 : (docs.Count > 0 ? docs[0] : null);
                    if (pick != null)
                    {
                        matchedBy = "active-fallback";
                        ctx.Warn("DOC_ACTIVE_FALLBACK", "plan-only run with " + docs.Count + " drawings open; planned against an arbitrary open drawing (nothing is written)");
                        return pick;
                    }
                }
                throw new ZwcError("DOC_AMBIGUOUS",
                    "no -Doc given and " + docs.Count + " drawings are open; refusing to guess which one to use",
                    "pass -Doc with the exact full path (or the drawing name); open documents: " + Candidates(docs),
                    Json.Write(CandidatesJson(docs)));
            }

            string target = docArg;
            string full = null;
            try { full = Path.GetFullPath(docArg); }
            catch (Exception) { }

            foreach (object d in docs)
            {
                string f = Com.Str(Com.TryGet(d, "FullName"));
                string n = Com.Str(Com.TryGet(d, "Name"));
                if (EqPath(f, target) || (full != null && EqPath(f, full))) { matchedBy = "fullName"; Select(d); RememberDoc(docArg, f, d); return d; }
                if (Eq(n, target)) { matchedBy = "name"; Select(d); RememberDoc(docArg, n, d); return d; }
            }
            throw DocNotFound(docArg, docs);
        }

        /// <summary>Make the resolved drawing the active one. Cheap, and it keeps later commands
        /// that omit -Doc (or the user's view) pointing at the drawing we just used.</summary>
        private static void Select(object doc)
        {
            try { Com.Call(doc, "Activate"); } catch (Exception) { }
        }

        private static string Candidates(List<object> docs)
        {
            List<string> parts = new List<string>();
            foreach (object d in docs)
            {
                string n = Com.Str(Com.TryGet(d, "Name"));
                string f = Com.Str(Com.TryGet(d, "FullName"));
                parts.Add(string.IsNullOrEmpty(f) ? n : (n + " (" + f + ")"));
            }
            return string.Join("; ", parts.ToArray());
        }

        private static List<object> CandidatesJson(List<object> docs)
        {
            List<object> list = new List<object>();
            foreach (object d in docs)
            {
                JMap m = new JMap();
                m.Add("name", Com.Str(Com.TryGet(d, "Name")));
                m.Add("fullName", Com.Str(Com.TryGet(d, "FullName")));
                list.Add(m);
            }
            return list;
        }
        public object OpenDoc(string path, bool readOnly)
        {
            object coll = DocumentsCollection();
            object doc;
            try { doc = Com.Call(coll, "Open", path, readOnly); }
            catch (Exception e)
            {
                throw new ZwcError("OPEN_FAILED", "Documents.Open failed for " + path + ": " + Com.Hr(e),
                    "check the path, that the file is not locked by another application, and that it is a real drawing", null);
            }
            return doc;
        }

        public object NewDoc()
        {
            object coll = DocumentsCollection();
            object doc;
            try { doc = Com.Call(coll, "Add"); }
            catch (Exception e)
            {
                throw new ZwcError("NEW_DOC_FAILED", "Documents.Add failed: " + Com.Hr(e), null, null);
            }
            return doc;
        }
    }

    // ======================================================================================
    // SendCommand / LISP bridge with the discipline from com-probe.md
    // ======================================================================================
    internal static class Cmd
    {
        private static int _seq = 0;

        public static string GetVariable(object doc, string name)
        {
            object v = Com.TryCall(doc, "GetVariable", name);
            string s = Com.Str(v);
            return s;
        }

        public static string LastPrompt(object doc)
        {
            string p = GetVariable(doc, "LASTPROMPT");
            return p == null ? "" : p;
        }

        /// <summary>A prompt containing "Missing:" or ending in '>' means CAD is waiting for more
        /// input - every later SendCommand would be swallowed as a continuation line.</summary>
        public static bool PromptPolluted(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            if (prompt.IndexOf("Missing:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return prompt.TrimEnd().EndsWith(">", StringComparison.Ordinal);
        }

        public static void AssertClean(object doc, string what)
        {
            string p = LastPrompt(doc);
            if (PromptPolluted(p))
                throw new ZwcError("CMD_STATE_POLLUTED",
                    "CAD command line is stuck before '" + what + "' (LASTPROMPT = \"" + p + "\")",
                    "press ESC in CAD (or run _.U) until the prompt is back to normal, then retry",
                    null);
        }

        /// <summary>Send one command string. Enforces the <=2 KB ceiling and re-checks LASTPROMPT.
        /// The string must be a single line: SendCommand turns "\n" into Enter.</summary>
        public static void Send(object doc, string command, Ctx ctx, string what)
        {
            if (command == null) throw new ZwcError("BAD_ARGS", "empty command for " + what, null, null);
            // a single trailing newline is the "press Enter" that submits the command; anything
            // else would be split into several commands by CAD
            string body = command;
            while (body.EndsWith("\n", StringComparison.Ordinal) || body.EndsWith("\r", StringComparison.Ordinal))
                body = body.Substring(0, body.Length - 1);
            if (body.IndexOf('\n') >= 0 || body.IndexOf('\r') >= 0)
                throw new ZwcError("BAD_ARGS", "internal: multi-line SendCommand payload for " + what, null, null);
            if (command.Length > 2000)
                throw new ZwcError("SENDCMD_TOO_LONG", "internal: " + command.Length
                    + " chars would be silently dropped by CAD (limit ~2048)", null, null);
            AssertClean(doc, what);
            Com.Call(doc, "SendCommand", command);
            string p = LastPrompt(doc);
            if (PromptPolluted(p))
                throw new ZwcError("CMD_STATE_POLLUTED",
                    "after '" + what + "' the command line is stuck (LASTPROMPT = \"" + p + "\")",
                    "the LISP payload was rejected; check brackets/quotes, then press ESC in CAD", p);
        }

        /// <summary>Run an AutoLISP expression that ends by (setq &lt;var&gt; ...), then read the scalar
        /// back through GetLispVariable (lists are E_INVALIDARG, so only scalars are ever returned).</summary>
        public static string EvalLisp(object doc, string expr, Ctx ctx, string what, int timeoutMs)
        {
            _seq++;
            string varName = "ZWC_R" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + _seq;
            string full = "(progn " + expr + " (setq " + varName + " ZWC_OUT))";
            if (!Balanced(full))
                throw new ZwcError("LISP_UNBALANCED", "internal: unbalanced LISP payload for " + what, null, full);
            // clear any stale value first
            Com.TryCall(doc, "SetLispVariable", varName, "");
            Send(doc, full, ctx, what);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                object v = Com.TryCall(doc, "GetLispVariable", varName);
                if (v != null && !(v is bool))
                {
                    string s = Com.Str(v);
                    if (s != null) return s;
                }
                Thread.Sleep(20);
            }
            throw new ZwcError("LISP_TIMEOUT", "LISP result variable " + varName + " never got set by " + what,
                "CAD may still be processing the payload; re-run status to see LASTPROMPT", null);
        }

        /// <summary>Paren/quote balance check, so a typo can never poison the CAD command state.</summary>
        public static bool Balanced(string s)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth < 0) return false; }
            }
            return depth == 0 && !inStr;
        }
    }

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

        /// <summary>Count processes of the product family we drive: "acad" for AutoCAD, "zwcad" for ZWCAD.</summary>
        public static List<object> Family(string kind)
        {
            bool acad = (kind ?? "").IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
            string needle = acad ? "acad" : "zwcad";
            List<object> found = new List<object>();
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string nm = p.ProcessName;
                        if (nm != null && nm.ToLowerInvariant().Contains(needle))
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
            bool acad = (kind ?? "").IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
            string needle = acad ? "acad" : "zwcad";
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
                            if (cmdline.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
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

    // ======================================================================================
    // Section model / plan / writer / verifier  (DESIGN-M1.md section 4)
    // ======================================================================================
    internal static class Commands
    {
        private static string Kind(JMap req, JMap args)
        {
            string k = args.Str("app", null);
            if (string.IsNullOrEmpty(k)) k = req.Str("app", null);
            if (string.IsNullOrEmpty(k)) k = "zwcad";
            return k;
        }

        private static string OptStr(JMap args, string key)
        {
            string s = args.Str(key, null);
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>The drawing to work on: "doc" lives at the top level of the request
        /// ({"cmd":"section","doc":"..."}), but tolerate it inside args as well.</summary>
        internal static string DocArgOf(JMap req, JMap args) { return DocArg(req, args); }
        internal static string AppKindOf(JMap req, JMap args) { return Kind(req, args); }
        private static string DocArg(JMap req, JMap args)
        {
            string d = OptStr(args, "doc");
            if (string.IsNullOrEmpty(d)) d = OptStr(req, "doc");
            return d;
        }

        // ------------------------------------------------------------------ doctor
        public static JMap Doctor(JMap req, JMap args, Ctx ctx)
        {
            string kind = Kind(req, args);
            string explicitProgId = Cad.ExplicitProgId(req, args);
            Cad.ProgChoice pc = Cad.ResolveProgIds(kind, explicitProgId, ctx);

            JMap r = new JMap();
            r.Add("tempDir", State.Dir());
            r.Add("stateFile", State.FilePath());

            string csc = null;
            string[] archs = new string[] { "Framework64", "Framework" };
            foreach (string a in archs)
            {
                string cand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET", a, "v4.0.30319", "csc.exe");
                if (File.Exists(cand)) { csc = cand; break; }
            }
            r.Add("csc", csc);
            r.Add("cscFound", csc != null);

            bool anyProgId = false;
            List<object> prods = pc.Detection.List("products");
            if (prods != null)
            {
                foreach (object o in prods)
                {
                    JMap pm = o as JMap;
                    if (pm != null && pm.Bool("registered", false)) anyProgId = true;
                }
            }
            // progIds[] now carries product/clsid/registered/runningInstance per known product, and
            // the two fields that actually explain "which CAD will this drive":
            //   mappedBy                    -> explicit | process | default
            //   unversionedProgIdPointsTo   -> where ZWCAD.Application/AutoCAD.Application points NOW
            //                                  (it is rewritten by the last product started)
            r.Add("progIds", pc.Detection.Get("products"));
            r.Add("runningProducts", pc.Detection.Get("runningProducts"));
            r.Add("unversionedProgIdPointsTo", pc.Detection.Get("unversionedProgIdPointsTo"));
            r.Add("mappedBy", pc.MappedBy);
            r.Add("multiProduct", pc.MultiProduct);

            JMap caps = new JMap();
            caps.Add("com", anyProgId);
            caps.Add("lisp", "expected");
            caps.Add("sendCommand", "expected");
            caps.Add("undoMark", "expected");
            caps.Add("addLine", "expected");
            caps.Add("addLightWeightPolyline", "expected");
            caps.Add("addText", "expected");
            caps.Add("addCircle", "expected");
            caps.Add("handleToObject", "expected");
            caps.Add("saveasDxf", "expected");
            caps.Add("plot", "unverified");
            r.Add("capabilities", caps);
            r.Add("messageFilter", MessageFilter.LastHResult);
            r.Add("processes", Procs.Zwcad());

            JMap st = State.Load();
            r.Add("session", st.Get("session"));

            ctx.Result = r;

            Cad cad;
            try { cad = Cad.AttachWithChoice(kind, false, ctx, pc, explicitProgId); }
            catch (ZwcError e)
            {
                r.Add("instanceFound", false);
                ctx.AppMap = null;
                // doctor is a diagnostic command: a multi-product box is a RESULT to report, not a
                // failure (status/open/section still return MULTI_PRODUCT, because they must act).
                if (e.Code == "MULTI_PRODUCT")
                {
                    r.Add("instanceError", "MULTI_PRODUCT");
                    ctx.Warn("MULTI_PRODUCT", "several ZWCAD/AutoCAD builds are running; pass -ProgId or -App to pick one");
                }
                throw e;
            }

            ctx.AppMap = cad.AppInfo();
            r.Add("instanceFound", true);
            r.Add("app", cad.AppInfo());

            object doc = cad.ActiveDocument();
            JMap probes = new JMap();
            List<object> eNot = new List<object>();

            // EvaluateLispExpression is a declared-but-empty shell; probing it is safe and tells us
            // whether this build changed. Application.Eval is NOT probed (RPC_E_SERVERFAULT crashed
            // the server twice during the probe run) - it is recorded as known-bad instead.
            try
            {
                object v = Com.Call(doc, "EvaluateLispExpression", "(+ 1 2)");
                probes.Add("evaluateLispExpression", "WORKS: " + Com.Str(v) + "  <-- behaviour changed, re-check");
            }
            catch (Exception e)
            {
                JMap m = new JMap();
                m.Add("api", "Document.EvaluateLispExpression");
                m.Add("hr", Com.Hr(e));
                m.Add("note", "empty shell in ZWCAD v16; never used by this helper");
                eNot.Add(m);
                probes.Add("evaluateLispExpression", "probe failed - see eNotImpl[0].hr (ZWCAD: E_NOTIMPL empty shell; AutoCAD 2007: DISP_E_UNKNOWNNAME = no such method)");
            }
            JMap appEval = new JMap();
            appEval.Add("api", "Application.Eval");
            appEval.Add("hr", "0x80010105 RPC_E_SERVERFAULT (measured, not re-probed: unsafe)");
            appEval.Add("note", "never used by this helper");
            eNot.Add(appEval);
            r.Add("eNotImpl", eNot);

            if (doc != null)
            {
                probes.Add("acadver", Cmd.GetVariable(doc, "ACADVER"));
                probes.Add("zwcadver", Cmd.GetVariable(doc, "ZWCADVER"));
                probes.Add("cmdactive", Cmd.GetVariable(doc, "CMDACTIVE"));
                string lp = Cmd.LastPrompt(doc);
                probes.Add("lastPrompt", lp);
                probes.Add("promptPolluted", Cmd.PromptPolluted(lp));
                try
                {
                    object lv = Com.Call(doc, "GetLispVariable", "ZWC_DOCTOR_PROBE_" + DateTime.Now.Ticks);
                    probes.Add("getLispVariable", lv == null ? "null" : (lv is bool ? "unset(False)" : "value:" + Com.Str(lv)));
                }
                catch (Exception e) { probes.Add("getLispVariable", "ERROR " + Com.Hr(e)); }
                try
                {
                    Com.Call(doc, "SetLispVariable", "ZWC_DOCTOR_PROBE", "ok");
                    object v = Com.Call(doc, "GetLispVariable", "ZWC_DOCTOR_PROBE");
                    probes.Add("setLispVariableRoundTrip", Com.Str(v));
                }
                catch (Exception e) { probes.Add("setLispVariableRoundTrip", "ERROR " + Com.Hr(e)); }
                string deepMatched;
            if (args.Bool("deep", false))
                {
                    try
                    {
                        Com.Call(doc, "StartUndoMark");
                        Com.Call(doc, "EndUndoMark");
                        probes.Add("undoMark", "ok");
                    }
                    catch (Exception e) { probes.Add("undoMark", "ERROR " + Com.Hr(e)); }
                }
                else probes.Add("undoMark", "not probed (pass -Deep to test it)");
            }
            r.Add("probes", probes);
            r.Add("documents", cad.Documents().Count);

            if (args.Bool("deep", false))
            {
                object ddoc = doc;
                if (ddoc == null)
                {
                    string deepMatched;
                    try { ddoc = cad.ResolveDoc(null, true, ctx, out deepMatched); } catch (Exception) { }
                }
                if (ddoc != null)
                {
                    JMap dp = DeepProbes(ddoc, ctx.AppMap);
                    r.Add("deep", dp);
                    if (dp.Str("hung", null) != null)
                        ctx.Warn("CAD_HUNG", "deep probe timed out at " + dp.Str("hung", "?") + "; CAD is not answering - use serve -Action recover");
                }
            }
            ctx.DocMap = doc == null ? null : Cad.DocInfo(doc);
            return r;
        }

        // ------------------------------------------------------------------ status
        /// <summary>doctor -Deep: three bounded probes (pipe is checked by the client, here COM and
        /// the window message loop). Any timeout is reported as CAD_HUNG instead of hanging.</summary>
        internal static JMap DeepProbes(object doc, JMap appMap)
        {
            JMap m = new JMap();
            int pid = appMap == null ? 0 : Com.I32(Com.TryGet(appMap, "pid"));
            int hwnd = appMap == null ? 0 : Com.I32(Com.TryGet(appMap, "hwnd"));
            m.Add("pid", pid);
            JMap com = new JMap();
            Stopwatch sw = Stopwatch.StartNew();
            bool comOk = false;
            try
            {
                object v = Com.Call(doc, "GetVariable", "CMDACTIVE");
                int dummy = Com.I32(v);
                comOk = true;
            }
            catch (Exception) { }
            com.Add("ok", comOk);
            long comMs = sw.ElapsedMilliseconds;
            com.Add("elapsedMs", comMs);
            m.Add("com", com);

            JMap win = new JMap();
            long winMs;
            bool winOk = Stability.WindowPing(hwnd, 2000, out winMs);
            win.Add("ok", winOk);
            win.Add("elapsedMs", winMs);
            win.Add("hwnd", hwnd);
            m.Add("window", win);

            string hung = null;
            if (!comOk && comMs > 1500) hung = "com";
            if (!winOk && hwnd > 0 && winMs >= 1900) hung = hung == null ? "window" : hung + "+window";
            m.Add("hung", hung);
            m.Add("verdict", hung == null ? "ok" : "CAD_HUNG");
            return m;
        }
        public static JMap Status(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            List<object> docs = cad.Documents();
            object active = cad.ActiveDocument();
            string activeFull = active == null ? null : Com.Str(Com.TryGet(active, "FullName"));
            // unsaved drawings report an empty FullName; fall back to the drawing name so the
            // caller can see which document is active at all
            if (string.IsNullOrEmpty(activeFull) && active != null) activeFull = Com.Str(Com.TryGet(active, "Name"));

            List<object> outDocs = new List<object>();
            foreach (object d in docs) outDocs.Add(Cad.DocInfo(d));

            JMap r = new JMap();
            r.Add("documents", outDocs);
            r.Add("count", docs.Count);
            r.Add("active", activeFull);
            object cmdActive = Com.TryCall(active, "GetVariable", "CMDACTIVE");
            r.Add("cmdActive", cmdActive == null ? null : (object)Com.I32(cmdActive));
            string lp = active == null ? null : Cmd.LastPrompt(active);
            r.Add("lastPrompt", lp);
            r.Add("promptPolluted", Cmd.PromptPolluted(lp));
            ctx.DocMap = active == null ? null : Cad.DocInfo(active);
            return r;
        }

        // ------------------------------------------------------------------ open
        public static JMap Open(JMap req, JMap args, Ctx ctx)
        {
            Cad.SuppressReveal = args.Bool("noReveal", false);
            string kind = Kind(req, args);
            bool launch = args.Bool("launch", false);
            bool readOnly = args.Bool("readOnly", false);
            bool noHint = args.Bool("noHint", false);
            bool wantNew = args.Bool("new", false);
            string docArg = DocArg(req, args);

            Cad cad = Cad.Attach(kind, launch, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();

            object doc = null;
            string matchedBy = null;
            bool opened = false;
            bool created = false;

            if (wantNew || (cad.Launched && string.IsNullOrEmpty(docArg)))
            {
                doc = cad.NewDoc();
                created = true;
                matchedBy = "new";
            }
            else if (!string.IsNullOrEmpty(docArg))
            {
                try { doc = cad.ResolveDoc(docArg, false, ctx, out matchedBy); }
                catch (ZwcError e)
                {
                    if (e.Code != "DOC_NOT_FOUND") throw;
                    if (!File.Exists(docArg)) throw;
                    doc = cad.OpenDoc(docArg, readOnly);
                    opened = true;
                    matchedBy = "opened";
                }
            }
            else
            {
                doc = cad.ResolveDoc(null, args.Bool("allowActive", false), ctx, out matchedBy);
            }

            if (doc == null)
                throw new ZwcError("NO_DOCUMENT", "no drawing available after open", null, null);

            // activate so the user sees what we are touching (and so ActiveDocument is sane)
            try { Com.Call(doc, "Activate"); } catch (Exception) { }

            JMap hint = new JMap();
            hint.Add("captionSet", false);
            if (!noHint) hint = ApplyCaption(cad, ctx, null);

            // remember instance identity only for instances WE started: finish must never quit a
            // CAD the user had already opened himself.
            if (cad.Launched)
            {
                JMap st = State.Load();
                JMap sess = new JMap();
                sess.Add("launched", true);
                sess.Add("hwnd", cad.Hwnd);
                sess.Add("pid", Cad.PidOfHwnd(cad.Hwnd));
                sess.Add("progId", cad.ProgId);
                sess.Add("startedAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                // remember EVERY instance this session launched (Classic + 2025 can coexist)
                List<object> insts = new List<object>();
                JMap prevSess = State.Session(st);
                if (prevSess != null)
                {
                    List<object> old = prevSess.List("instances");
                    if (old != null) foreach (object o in old) insts.Add(o);
                }
                bool dupHwnd = false;
                foreach (object o in insts)
                {
                    JMap om = o as JMap;
                    if (om != null && om.Int("hwnd", 0) == (int)cad.Hwnd) dupHwnd = true;
                }
                if (!dupHwnd)
                {
                    JMap me = new JMap();
                    me.Add("hwnd", cad.Hwnd);
                    me.Add("pid", Cad.PidOfHwnd(cad.Hwnd));
                    me.Add("progId", cad.ProgId);
                    insts.Add(me);
                }
                sess.Add("instances", insts);
                st.Add("session", sess);
                JMap docs = new JMap();
                string full = Com.Str(Com.TryGet(doc, "FullName"));
                if (!string.IsNullOrEmpty(full)) docs.Add(full, true);
                st.Add("openedDocs", docs);
                State.Save(st);
                State.Flush();   // ownership must be visible immediately (finish/recover read the file)
                // expose the launched instance through the top-level app map
                ctx.AppMap.Add("launched", true);
            }

            JMap r = new JMap();
            r.Add("doc", Cad.DocInfo(doc));
            r.Add("app", cad.AppInfo());
            r.Add("hint", hint);
            if (args.Bool("reveal", false))
            {
                r.Add("reveal", Cad.Reveal(cad, ctx));
                ctx.AppMap = cad.AppInfo();
            }
            r.Add("launched", cad.Launched);
            r.Add("opened", opened);
            r.Add("created", created);
            r.Add("matchedBy", matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            return r;
        }

        // ------------------------------------------------------------------ info
        public static JMap Info(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(DocArg(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            JMap r = new JMap();
            JMap units = new JMap();
            string[] vars = new string[] { "INSUNITS", "DIMSCALE", "LTSCALE", "TEXTSIZE", "MEASUREMENT", "ANNOALLVISIBLE" };
            foreach (string v in vars)
            {
                object val = Com.TryCall(doc, "GetVariable", v);
                if (val == null) ctx.Warn("VAR_UNSUPPORTED", "GetVariable(" + v + ") is not available in this build");
                else units.Add(v.ToLowerInvariant(), Com.Dbl(val));
            }
            r.Add("units", units);

            JMap ext = new JMap();
            object emin = Com.TryCall(doc, "GetVariable", "EXTMIN");
            object emax = Com.TryCall(doc, "GetVariable", "EXTMAX");
            double[] mn = Com.Pt(emin);
            double[] mx = Com.Pt(emax);
            ext.Add("min", mn == null ? null : (object)new List<object> { mn[0], mn[1], mn[2] });
            ext.Add("max", mx == null ? null : (object)new List<object> { mx[0], mx[1], mx[2] });
            r.Add("extents", ext);

            List<object> layers = new List<object>();
            object ls = Com.TryGet(doc, "Layers");
            int ln = ls == null ? 0 : Com.I32(Com.TryGet(ls, "Count"));
            int lb = Coll.DetectBase(ls, ln);
            for (int k = 0; k < ln; k++)
            {
                object l = Coll.At(ls, lb, k, ln);
                if (l == null) continue;
                JMap m = new JMap();
                m.Add("name", Com.Str(Com.TryGet(l, "Name")));
                object col = Com.TryGet(l, "Color");
                m.Add("color", col == null ? null : (object)Com.I32(col));
                object frz = Com.TryGet(l, "Freeze");
                if (frz == null) frz = Com.TryGet(l, "LayerOn");
                m.Add("frozen", frz == null ? null : (object)Com.Bool(frz));
                object lk = Com.TryGet(l, "Lock");
                m.Add("locked", lk == null ? null : (object)Com.Bool(lk));
                m.Add("linetype", Com.Str(Com.TryGet(l, "Linetype")));
                layers.Add(m);
            }
            r.Add("layers", layers);

            List<object> styles = new List<object>();
            object tss = Com.TryGet(doc, "TextStyles");
            int tn = tss == null ? 0 : Com.I32(Com.TryGet(tss, "Count"));
            int tb = Coll.DetectBase(tss, tn);
            for (int k = 0; k < tn; k++)
            {
                object t = Coll.At(tss, tb, k, tn);
                if (t == null) continue;
                JMap m = new JMap();
                m.Add("name", Com.Str(Com.TryGet(t, "Name")));
                m.Add("font", Com.Str(Com.TryGet(t, "FontFile")));
                object th = Com.TryGet(t, "Height");
                m.Add("height", th == null ? null : (object)Com.Dbl(th));
                styles.Add(m);
            }
            r.Add("textStyles", styles);

            JMap counts = new JMap();
            counts.Add("model", CountOf(doc, "ModelSpace"));
            counts.Add("paper", CountOf(doc, "PaperSpace"));
            counts.Add("blocks", CountOf(doc, "Blocks"));
            counts.Add("layouts", CountOf(doc, "Layouts"));
            counts.Add("documents", cad.Documents().Count);
            r.Add("counts", counts);
            r.Add("matchedBy", matchedBy);
            return r;
        }

        internal static int CountOf(object doc, string coll)
        {
            object c = Com.TryGet(doc, coll);
            if (c == null) return -1;
            object n = Com.TryGet(c, "Count");
            return n == null ? -1 : Com.I32(n);
        }

        // ------------------------------------------------------------------ read
        /// <summary>Per-call read budget. A drawing full of block references / proxy objects can
        /// make an individual property unreadable on MANY entities; after 20 failed reads of the
        /// same kind (or 40 in total) we stop trying geometry and continue in handles mode, so the
        /// command returns DEGRADED_TO_HANDLES instead of grinding to a timeout.</summary>
        internal sealed class ReadState
        {
            public int Failures;
            public bool Degraded;
            public Dictionary<string, int> ByProperty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public const int PerPropertyLimit = 20;
            public const int TotalLimit = 40;

            public void Fail(string property)
            {
                Failures++;
                int n;
                ByProperty.TryGetValue(property, out n);
                ByProperty[property] = n + 1;
                if (n + 1 >= PerPropertyLimit || Failures >= TotalLimit) Degraded = true;
            }
        }
        public static JMap Read(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(DocArg(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            // ---- M3-E1: tiered read + paging + fail-fast property policy
            string mode = args.Str("mode", null);
            if (string.IsNullOrEmpty(mode))
                mode = args.Bool("handles", false) ? "handles" : (args.Bool("summary", false) ? "summary" : "summary");
            mode = mode.Trim().ToLowerInvariant();
            if (mode == "fast" || mode == "handle" || mode == "handles") mode = "handles";
            else if (mode == "summary" || mode == "info") mode = "summary";
            else if (mode == "geom" || mode == "full" || mode == "geometry" || mode == "detail") mode = "geom";
            else throw new ZwcError("BAD_ARGS", "unknown read mode '" + args.Str("mode", "?") + "'",
                "use -Mode handles|summary|geom", null);

            bool strict = args.Bool("strict", false);
            Com.ReadRetries = strict ? 6 : 1;
            int limit = args.Int("limit", 0);
            if (limit <= 0) limit = args.Int("max", 0);          // -Max is the legacy alias

            string scope = args.Str("scope", "model");
            if (string.IsNullOrEmpty(scope)) scope = args.Str("space", "model");   // -Space alias
            if (string.IsNullOrEmpty(scope)) scope = "model";
            scope = scope.Trim().ToLowerInvariant();
            ReadState state = new ReadState();

            int modelCount = CountOf(doc, "ModelSpace");
            int paperCount = CountOf(doc, "PaperSpace");
            int total = (scope == "paper") ? paperCount : modelCount;

            bool lispBridge = LispBridgeAvailable(doc);
            if (!lispBridge && mode == "handles")
                ctx.Warn("LISP_BRIDGE_UNAVAILABLE", "this CAD build has no Get/SetLispVariable bridge; handles mode falls back to COM (3 properties per entity)");

            List<object> entities = new List<object>();
            string readPath = "com";
            bool truncated = false;
            int skipped = 0;
            string cursor = args.Str("cursor", null);
            int skipCount = 0;
            string skipHandle = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                int ci;
                if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out ci)) skipCount = Math.Max(0, ci);
                else skipHandle = cursor.Trim();
            }

            // ---- M3-E5: on builds without the LISP variable bridge a COM sweep costs ~15 ms per
            // entity (36k entities = minutes). A LISP ssget sweep writing a text file takes ~0.4 s;
            // it is tried automatically for large model-space reads and falls back to COM on error.
            if ((!lispBridge || args.Bool("forceLispFile", false)) && scope == "model" && (mode == "handles" || mode == "summary" || mode == "geom")
                && total > args.Int("lispThreshold", 2000))
            {
                try
                {
                    JMap lispMeta;
                    JMap lf = (mode == "geom")
                        ? LispGeom.Scan(doc, ctx, null, null, args.Int("lispTimeoutSec", 60) * 1000, out lispMeta)
                        : LispFile.Scan(doc, ctx, null, null, args.Int("lispTimeoutSec", 60) * 1000, out lispMeta);
                    List<object> all = lf.List("entities");
                    int allCount = all == null ? 0 : all.Count;
                    total = lf.Int("count", allCount);
                    int start = 0;
                    if (skipHandle != null)
                    {
                        for (int i = 0; i < allCount; i++)
                        {
                            JMap mm = all[i] as JMap;
                            if (mm != null && string.Equals(mm.Str("handle", null), skipHandle, StringComparison.OrdinalIgnoreCase)) { start = i + 1; break; }
                        }
                    }
                    else start = Math.Min(skipCount, allCount);
                    for (int i = start; i < allCount; i++)
                    {
                        if (limit > 0 && entities.Count >= limit) { truncated = true; break; }
                        entities.Add(all[i]);
                    }
                    skipped = start;
                    readPath = (mode == "geom") ? "lisp-file-geom" : "lisp-file";
                    ctx.LispMeta = lispMeta;
                }
                catch (ZwcError ze)
                {
                    ctx.Warn("LISP_FILE_FALLBACK", "LISP file scan failed (" + ze.Code + ": " + ze.Message + "); falling back to the COM sweep");
                }
                catch (Exception ex)
                {
                    ctx.Warn("LISP_FILE_FALLBACK", "LISP file scan failed (" + Com.Hr(ex) + "); falling back to the COM sweep");
                }
            }

            if (scope.StartsWith("handle:", StringComparison.OrdinalIgnoreCase))
            {
                string h = scope.Substring("handle:".Length).Trim();
                object e = Com.EntityByHandle(doc, h);
                if (e == null) throw new ZwcError("ENTITY_NOT_FOUND", "HandleToObject(" + h + ") returned nothing", null, null);
                entities.Add(DescribeEntity(e, "model", mode, ctx, state));
                readPath = "com";
                total = 1;
            }
            else if (mode == "handles" && lispBridge)
            {
                // one SendCommand + one GetLispVariable: handle|type|layer|space per entity
                string dump = LispDumpHandles(doc, ctx);
                readPath = "lisp";
                bool skipping = skipHandle != null;
                foreach (string row in dump.Split(';'))
                {
                    if (row.Length == 0) continue;
                    string[] f = row.Split('|');
                    if (f.Length < 3) continue;
                    if (skipping)
                    {
                        if (string.Equals(f[0], skipHandle, StringComparison.OrdinalIgnoreCase)) skipping = false;
                        continue;
                    }
                    if (skipped < skipCount) { skipped++; continue; }
                    if (limit > 0 && entities.Count >= limit) { truncated = true; break; }
                    JMap m = new JMap();
                    m.Add("handle", f[0]);
                    m.Add("type", f[1]);
                    m.Add("kind", KindOf(f[1]));
                    m.Add("layer", f[2]);
                    m.Add("space", f.Length > 3 ? f[3] : "model");
                    entities.Add(m);
                }
            }
            else if (readPath != "lisp-file")
            {
                object space;
                string spaceName;
                if (scope == "paper") { space = Com.Get(doc, "PaperSpace"); spaceName = "paper"; }
                else { space = Com.Get(doc, "ModelSpace"); spaceName = "model"; }

                int baseIdx = Coll.DetectBase(space, total);
                bool skipping = skipHandle != null;
                for (int k = 0; k < total; k++)
                {
                    object e = Coll.At(space, baseIdx, k, total);
                    if (e == null) continue;
                    if (skipping)
                    {
                        string h = Com.Str(Com.TryGet(e, "Handle"));
                        if (string.Equals(h, skipHandle, StringComparison.OrdinalIgnoreCase)) skipping = false;
                        continue;
                    }
                    if (skipped < skipCount) { skipped++; continue; }
                    if (limit > 0 && entities.Count >= limit) { truncated = true; break; }
                    if (state.Degraded)
                        entities.Add(DescribeEntity(e, spaceName, "handles", ctx, state));
                    else
                        entities.Add(DescribeEntity(e, spaceName, mode, ctx, state));
                }
                if (state.Degraded)
                    ctx.Warn("DEGRADED_TO_HANDLES", "geometry reads failed on too many entities ("
                        + state.Failures + " failures); the rest of this read returned handles only - "
                        + "this drawing is probably full of block references / proxy objects");
            }

            // next cursor: an opaque token = the NEXT entity index (numeric) so a follow-up call is
            // O(1) for the COM path and also works for the LISP path
            int nextIndex = skipped + entities.Count;
            bool hasMore = nextIndex < total && (limit > 0 || skipped > 0);
            JMap counts = new JMap();
            counts.Add("model", modelCount);
            counts.Add("paper", paperCount);
            counts.Add("returned", entities.Count);
            counts.Add("total", total);
            if (scope.StartsWith("handle:", StringComparison.OrdinalIgnoreCase)) counts.Add("scope", "handle");

            JMap r = new JMap();
            r.Add("entities", entities);
            r.Add("counts", counts);
            r.Add("mode", mode);
            r.Add("readPath", readPath);
            r.Add("scope", scope);
            r.Add("truncated", truncated);
            r.Add("returned", entities.Count);
            r.Add("nextCursor", hasMore ? (object)nextIndex : null);
            r.Add("strict", strict);
            r.Add("matchedBy", matchedBy);
            if (ctx.LispMeta != null) r.Add("lispFile", ctx.LispMeta);
            return r;
        }

        /// <summary>True when the document exposes GetLispVariable (ZWCAD). AutoCAD 2007 raises
        /// DISP_E_UNKNOWNNAME, which means the whole LISP-variable channel is absent.</summary>
        internal static bool LispBridgeAvailable(object doc)
        {
            try { Com.Call(doc, "GetLispVariable", "ZWC_PROBE_BRIDGE"); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Entity type -> our kind vocabulary. Order matters: "spline" contains "line" and
        /// "mtext" contains "text", so the specific names are matched first (a real drawing lost
        /// 321 splines to a StartPoint failure storm because they were classified as LINE).</summary>
        internal static string KindOf(string type)
        {
            if (type == null) return "UNKNOWN";
            string t = type.ToLowerInvariant();
            if (t.IndexOf("spline") >= 0) return "SPLINE";
            if (t.IndexOf("mtext") >= 0) return "MTEXT";
            if (t.IndexOf("polyline") >= 0) return "LWPOLYLINE";
            if (t.IndexOf("hatch") >= 0) return "HATCH";
            if (t.IndexOf("insert") >= 0 || t.IndexOf("blockreference") >= 0) return "INSERT";
            if (t.IndexOf("acdbtext") >= 0) return "TEXT";
            if (t.IndexOf("text") >= 0) return "TEXT";
            if (t.IndexOf("acdbline") >= 0) return "LINE";
            if (t.IndexOf("arc") >= 0) return "ARC";
            if (t.IndexOf("circle") >= 0) return "CIRCLE";
            if (t.IndexOf("line") >= 0) return "LINE";
            return type.ToUpperInvariant();
        }

        /// <summary>Describe one entity at the requested read tier.
        ///   handles : handle / type / kind / layer / space         (3 COM reads)
        ///   summary : + bbox (one GetBoundingBox call)             (~4 reads)
        ///   geom    : + vertices / text / height / style / radius / center / points
        /// Only properties that exist for the entity's own type are touched, so the old "read a
        /// property that cannot exist, retry it six times" pattern is gone.</summary>
        internal static JMap DescribeEntity(object e, string spaceName, string mode, Ctx ctx, ReadState state)
        {
            JMap m = new JMap();
            string type = Com.Str(Com.TryGet(e, "ObjectName"));
            string kind = KindOf(type);
            string handle = Com.Str(Com.TryGet(e, "Handle"));
            string layer = Com.Str(Com.TryGet(e, "Layer"));
            if (handle == null) { ctx.Warn("PROPERTY_READ_FAILED", "entity Handle could not be read"); }
            m.Add("handle", handle);
            m.Add("type", type);
            m.Add("kind", kind);
            m.Add("layer", layer);
            m.Add("space", spaceName);

            double[] bmin = null, bmax = null;
            bool wantBBox = (mode == "summary" || mode == "geom");

            if (mode == "geom")
            {
                if (kind == "LWPOLYLINE" || kind == "POLYLINE")
                {
                    object coordsObj = Com.TryGet(e, "Coordinates");
                    if (coordsObj == null) state.Fail("Coordinates");
                    double[] flat = Com.Flat(coordsObj);
                    List<object> verts = new List<object>();
                    for (int i = 0; i + 1 < flat.Length; i += 2) verts.Add(new List<object> { flat[i], flat[i + 1] });
                    m.Add("vertices", verts);
                    m.Add("points", verts);
                    object closed = Com.TryGet(e, "Closed");
                    object elev = Com.TryGet(e, "Elevation");
                    if (elev != null) m.Add("elevation", Com.Dbl(elev));
                    m.Add("closed", closed == null ? null : (object)Com.Bool(closed));
                }
                else if (kind == "LINE")
                {
                    double[] p1 = Com.Pt(Com.TryGet(e, "StartPoint"));
                    double[] p2 = Com.Pt(Com.TryGet(e, "EndPoint"));
                    if (p1 != null) m.Add("start", new List<object> { p1[0], p1[1] });
                    if (p2 != null) m.Add("end", new List<object> { p2[0], p2[1] });
                    if (p1 != null && p2 != null)
                        m.Add("points", new List<object> { new List<object> { p1[0], p1[1] }, new List<object> { p2[0], p2[1] } });
                    object lt = Com.TryGet(e, "Linetype");
                    if (lt != null) m.Add("linetype", Com.Str(lt));
                }
                else if (kind == "CIRCLE" || kind == "ARC")
                {
                    double[] c = Com.Pt(Com.TryGet(e, "Center"));
                    object radObj = Com.TryGet(e, "Radius");
                    double rad = radObj == null ? 0 : Com.Dbl(radObj);
                    if (c != null) m.Add("center", new List<object> { c[0], c[1] });
                    m.Add("radius", rad);
                }
                else if (kind == "TEXT" || kind == "MTEXT")
                {
                    object txt = Com.TryGet(e, "TextString");
                    if (txt == null) txt = Com.TryGet(e, "Text");
                    m.Add("text", Com.Str(txt));
                    double[] ins = Com.Pt(Com.TryGet(e, "InsertionPoint"));
                    if (ins != null) { m.Add("insertion", new List<object> { ins[0], ins[1] }); m.Add("pos", new List<object> { ins[0], ins[1] }); }
                    object h = Com.TryGet(e, "Height");
                    if (h != null) m.Add("height", Com.Dbl(h));
                    object st = Com.TryGet(e, "StyleName");
                    if (st != null) m.Add("style", Com.Str(st));
                }
            }
            // summary carries the text for text entities too: `query -Mode summary` is what feeds
            // `entity` expect.text, so a summary without text made every W1-style update fail its anchor
            if (mode == "summary" && (kind == "TEXT" || kind == "MTEXT"))
            {
                object txt = Com.TryGet(e, "TextString");
                if (txt == null) txt = Com.TryGet(e, "Text");
                if (txt != null) m.Add("text", Com.Str(txt));
            }

            // handles mode is intentionally 3 reads and nothing else

            if (wantBBox)
            {
                if (!TryBBoxFromEntity(e, kind, out bmin, out bmax))
                {
                    bool expectedUnsupported = (kind == "HATCH" || kind == "UNKNOWN" || kind.StartsWith("ACDB", StringComparison.OrdinalIgnoreCase));
                    if (expectedUnsupported)
                    {
                        int n;
                        state.ByProperty.TryGetValue("bbox:" + kind, out n);
                        if (n == 0)
                            ctx.Warn("BBOX_UNAVAILABLE_FOR_TYPE", "no cheap bounding box for entity type " + kind + " on this CAD build; those entities carry no bbox");
                        state.ByProperty["bbox:" + kind] = n + 1;
                    }
                    else
                    {
                        state.Fail("BoundingBox");
                    }
                }
                if (bmin != null)
                {
                    JMap bb = new JMap();
                    bb.Add("min", new List<object> { bmin[0], bmin[1] });
                    bb.Add("max", new List<object> { bmax[0], bmax[1] });
                    m.Add("bbox", bb);
                }
            }
            return m;
        }

        /// <summary>Bounding box from one type-specific property pair. GetBoundingBox is tried only
        /// for entity types we have no cheap geometry for (HATCH and anything unknown) because
        /// ZWCAD Classic does not implement it for regular entities at all.</summary>
        private static bool TryBBoxFromEntity(object e, string kind, out double[] min, out double[] max)
        {
            min = null; max = null;
            if (kind == "LINE")
            {
                double[] p1 = Com.Pt(Com.TryGet(e, "StartPoint"));
                double[] p2 = Com.Pt(Com.TryGet(e, "EndPoint"));
                if (p1 == null || p2 == null) return false;
                min = new double[] { Math.Min(p1[0], p2[0]), Math.Min(p1[1], p2[1]) };
                max = new double[] { Math.Max(p1[0], p2[0]), Math.Max(p1[1], p2[1]) };
                return true;
            }
            if (kind == "CIRCLE" || kind == "ARC")
            {
                double[] c = Com.Pt(Com.TryGet(e, "Center"));
                object ro = Com.TryGet(e, "Radius");
                if (c == null || ro == null) return false;
                double r = Com.Dbl(ro);
                min = new double[] { c[0] - r, c[1] - r };
                max = new double[] { c[0] + r, c[1] + r };
                return true;
            }
            if (kind == "TEXT" || kind == "MTEXT")
            {
                double[] ins = Com.Pt(Com.TryGet(e, "InsertionPoint"));
                if (ins == null) return false;
                object hObj = Com.TryGet(e, "Height");
                double h = hObj == null ? 0 : Com.Dbl(hObj);
                min = new double[] { ins[0], ins[1] };
                max = new double[] { ins[0] + h * 0.6, ins[1] + h };
                return true;
            }
            if (kind == "LWPOLYLINE" || kind == "POLYLINE")
            {
                double[] flat = Com.Flat(Com.TryGet(e, "Coordinates"));
                if (flat.Length < 2) return false;
                double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
                for (int i = 0; i + 1 < flat.Length; i += 2)
                {
                    if (flat[i] < mnx) mnx = flat[i];
                    if (flat[i] > mxx) mxx = flat[i];
                    if (flat[i + 1] < mny) mny = flat[i + 1];
                    if (flat[i + 1] > mxy) mxy = flat[i + 1];
                }
                min = new double[] { mnx, mny };
                max = new double[] { mxx, mxy };
                return true;
            }
            if (kind == "SPLINE")
            {
                // AcadSpline exposes its control points directly; no GetBoundingBox needed
                object cp = Com.TryGet(e, "ControlPoints");
                double[] flat = Com.Flat(cp);
                if (flat.Length < 3) return false;
                double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
                for (int i = 0; i + 2 < flat.Length; i += 3)
                {
                    if (flat[i] < mnx) mnx = flat[i];
                    if (flat[i] > mxx) mxx = flat[i];
                    if (flat[i + 1] < mny) mny = flat[i + 1];
                    if (flat[i + 1] > mxy) mxy = flat[i + 1];
                }
                min = new double[] { mnx, mny };
                max = new double[] { mxx, mxy };
                return true;
            }
            return Com.TryGetBBox(e, out min, out max);
        }

        /// <summary>One LISP expression -> one scalar string of "handle|type|layer|space;" rows.
        /// Well under the 2 KB SendCommand ceiling and the proven shape from the probe report.</summary>
        private static string LispDumpHandles(object doc, Ctx ctx)
        {
            string expr =
                "(progn (setq ZWC_OUT \"\" e (entnext) n 0)"
                + " (while e"
                + " (setq d (entget e) ZWC_OUT (strcat ZWC_OUT (cdr (assoc 5 d)) \"|\" (cdr (assoc 0 d)) \"|\" (cdr (assoc 8 d)) \"|\" (if (equal 1 (cdr (assoc 67 d))) \"paper\" \"model\") \";\")"
                + " n (1+ n) e (entnext e))) (setq ZWC_N n))";
            string dump = Cmd.EvalLisp(doc, expr, ctx, "read/lisp-dump", 20000);
            return dump;
        }
        // ------------------------------------------------------------------ section
        public static JMap SectionCmd(JMap req, JMap args, Ctx ctx)
        {
            string dataPath = OptStr(args, "data");
            SecModel model = SecModel.Load(dataPath, args, ctx);
            SecPlan plan = Section.Build(model, ctx);

            JMap result = new JMap();
            result.Add("model", plan.ModelMap());
            JMap expected = new JMap();
            expected.Add("total", plan.ExpectedTotal);
            expected.Add("byType", Section.ByType(plan.Specs));
            expected.Add("byLayer", Section.ByLayer(plan.Specs));
            expected.Add("byRole", ByRole(plan.Specs));
            result.Add("expected", expected);
            result.Add("waterLevels", WatersOut(plan));
            ctx.Result = result;

            bool dryRun = args.Bool("dryRun", false) || args.Bool("noCad", false);
            if (dryRun)
            {
                JMap dryCreated = new JMap();
                dryCreated.Add("handles", new List<object>());
                dryCreated.Add("byType", new JMap());
                dryCreated.Add("total", 0);
                result.Add("created", dryCreated);
                JMap dryVerify = new JMap();
                dryVerify.Add("ok", true);
                dryVerify.Add("skipped", true);
                dryVerify.Add("issues", new List<object>());
                result.Add("verify", dryVerify);
                result.Add("layers", Section.LayerNames(plan));
                AddPlanMeta(result, plan);
            if (expectedSeries != null) expected.Add("series", expectedSeries);
                JMap expDry = result.Map("expected");
                if (expDry != null && expectedSeries != null) expDry.Add("series", expectedSeries);
                result.Add("undoToken", null);
                result.Add("dryRun", true);
                return result;
            }

            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(DocArg(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            result.Add("matchedBy", matchedBy);

            string prefix = string.IsNullOrEmpty(model.LayerPrefix) ? "AI-SEC" : model.LayerPrefix;
            string styleName = prefix + "-TEXT";

            // ---- preflight: layers (annotation/water/axis + one per series), text style, linetype
            object layers = Com.Get(doc, "Layers");
            foreach (object lo in plan.LayerList)
            {
                string ln = Com.Str(lo);
                if (!string.IsNullOrEmpty(ln)) EnsureLayer(layers, ln, ctx);
            }
            // overlay: give every series layer its colour so BYLAYER entities are separable too
            if (plan.Series.Count > 1)
            {
                foreach (SecSeriesPlan sp in plan.Series)
                {
                    object sl = null;
                    try { sl = Com.Call(layers, "Item", sp.Layer); } catch (Exception) { }
                    if (sl != null) Com.TrySet(sl, "Color", sp.Color);
                }
            }

            // text style: must carry a CJK-capable font or Chinese renders as "????" (measured on
            // AutoCAD 2007; ZWCAD happened to have a CJK default font, which hid the bug there)
            JMap styleInfo = EnsureTextStyle(doc, styleName, Com.Str(Com.TryGet(cad.App, "FullName")), ctx);
            result.Add("textStyle", styleInfo);

            JMap dashInfo = EnsureLinetype(doc, "DASHED", Kind(req, args), ctx);
            bool dashed = dashInfo.Bool("ok", false);
            double dashScale = dashInfo.Dbl("scale", 1.0);
            if (!dashed) ctx.Warn("LINETYPE_UNAVAILABLE", "DASHED is not available in this drawing; the water lines stay CONTINUOUS");
            if (dashed)
            {
                // also set the layer linetype so anything drawn later on AI-SEC-WATER inherits it
                object waterLayer = null;
                try { waterLayer = Com.Call(layers, "Item", prefix + "-WATER"); }
                catch (Exception) { }
                if (waterLayer != null) Com.TrySet(waterLayer, "Linetype", "DASHED");
            }
            result.Add("linetype", dashInfo);

            // never start drawing into an instance that is mid-command or showing a modal dialog:
            // a wedged instance accepts some COM writes and silently ignores others (measured on
            // ZWCAD Classic: Linetype stayed ByLayer and a later _.U did nothing)
            object cmdActiveProbe = Com.TryCall(doc, "GetVariable", "CMDACTIVE");
            string promptProbe = Cmd.LastPrompt(doc);
            if (Com.I32(cmdActiveProbe) != 0 || Cmd.PromptPolluted(promptProbe))
                throw new ZwcError("BUSY", "CAD is not idle (CMDACTIVE=" + Com.I32(cmdActiveProbe)
                    + ", LASTPROMPT=\"" + promptProbe + "\"); refusing to draw",
                    "finish the pending command / close the dialog in CAD, then retry", null);

            int countBefore = CountOf(doc, "ModelSpace");
            if (countBefore < 0) throw new ZwcError("NO_MODELSPACE", "cannot read ModelSpace.Count", null, null);

            // AutoCAD: BeginUndoMark / ZWCAD: StartUndoMark - same semantics, different names.
            // (EndUndoMark exists on both.)
            bool markOk = Com.TryInvoke(doc, "BeginUndoMark") || Com.TryInvoke(doc, "StartUndoMark");
            if (!markOk) ctx.Warn("UNDO_MARK_UNAVAILABLE", "StartUndoMark failed; the burst will not be a single undo step");

            object ms = Com.Get(doc, "ModelSpace");
            List<object> handles = new List<object>();
            try
            {
                foreach (SecSpec spec in plan.Specs)
                {
                    object ent = CreateEntity(ms, spec);
                    if (ent == null)
                        throw new ZwcError("CREATE_FAILED", "CAD returned no object for " + spec.Kind + " on layer " + spec.Layer, null, null);
                    Com.TrySet(ent, "Layer", spec.Layer);
                    if (spec.Color > 0) Com.TrySet(ent, "Color", spec.Color);
                    if (spec.Kind == "poly")
                    {
                        Com.TrySet(ent, "Closed", false);
                        if (!string.IsNullOrEmpty(spec.Linetype)
                            && !string.Equals(spec.Linetype, "CONTINUOUS", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(spec.Linetype, "BYLAYER", StringComparison.OrdinalIgnoreCase))
                            Com.TrySet(ent, "Linetype", spec.Linetype);
                    }
                    if (spec.Kind == "line" && dashed && spec.Layer.EndsWith("-WATER", StringComparison.OrdinalIgnoreCase))
                    {
                        // explicit (not ByLayer) so the dash survives a user layer-linetype change,
                        // and scaled so the dash is visible at normal zoom in both CADs.
                        // A wedged instance can reject the property once; retry before believing it.
                        bool ltOk = Com.TrySet(ent, "Linetype", "DASHED");
                        if (!ltOk) { Thread.Sleep(60); ltOk = Com.TrySet(ent, "Linetype", "DASHED"); }
                        if (!ltOk) ctx.Warn("LINETYPE_SET_FAILED", "CAD refused Linetype=DASHED on water line " + spec.Handle + " (verify will flag LINETYPE_NOT_APPLIED)");
                        else Com.TrySet(ent, "LinetypeScale", dashScale);
                    }
                    if (spec.Kind == "text") Com.TrySet(ent, "StyleName", styleName);
                    string h = Com.Str(Com.TryGet(ent, "Handle"));
                    if (string.IsNullOrEmpty(h)) throw new ZwcError("HANDLE_MISSING", "entity created but its Handle property is empty", null, null);
                    spec.Handle = h;
                    handles.Add(h);
                }
            }
            catch (Exception)
            {
                Com.TryCall(doc, "EndUndoMark");
                Rollback(doc, countBefore, ctx);
                throw;
            }
            Com.TryCall(doc, "EndUndoMark");

            JMap created = new JMap();
            created.Add("handles", handles);
            created.Add("byType", CountTypes(plan.Specs));
            created.Add("byLayer", CountLayers(plan.Specs));
            created.Add("byRole", ByRole(plan.Specs));
            created.Add("total", handles.Count);
            result.Add("created", created);
            result.Add("layers", Section.LayerNames(plan));

            AddPlanMeta(result, plan);
            if (expectedSeries != null) expected.Add("series", expectedSeries);

            JMap verify = VerifySection(doc, plan, countBefore, dashed, dashScale, ctx);
            result.Add("verify", verify);

            if (!verify.Bool("ok", false) && !args.Bool("keepOnFail", false))
            {
                Rollback(doc, countBefore, ctx);
                verify.Add("rolledBack", true);
                verify.Add("countAfterRollback", CountOf(doc, "ModelSpace"));
                throw new ZwcError("VERIFY_FAILED",
                    "the section was drawn but read-back verification failed ("
                    + (verify.List("issues") == null ? 0 : verify.List("issues").Count) + " issue(s)); the whole batch was undone",
                    "nothing was left on the drawing - see result.verify.issues", Json.Write(verify));
            }

            string token = "u-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
                + new Random(Environment.TickCount).Next(0x1000, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
            JMap st = State.Load();
            JMap tok = new JMap();
            tok.Add("cmd", "section");
            tok.Add("doc", Com.Str(Com.TryGet(doc, "FullName")));
            tok.Add("baseline", countBefore);
            tok.Add("countAfter", CountOf(doc, "ModelSpace"));
            tok.Add("undoGroups", 1);
            tok.Add("expectedTotal", plan.ExpectedTotal);
            tok.Add("at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            JMap tokens = State.Tokens(st);
            tokens.Add(token, tok);
            State.PruneTokens(st);
            State.Save(st);
            ctx.UndoToken = token;
            result.Add("undoToken", token);
            ctx.DocMap = Cad.DocInfo(doc);
            return result;
        }

        /// <summary>Series / label-policy / legend part of the response. Called by both the dry-run
        /// and the real path (handles are filled in as soon as the entities exist).</summary>
        private static void AddPlanMeta(JMap result, SecPlan plan)
        {
            List<object> seriesOut = new List<object>();
            foreach (SecSeriesPlan sp in plan.Series)
            {
                if (sp.Ground != null && !string.IsNullOrEmpty(sp.Ground.Handle)) sp.Handle = sp.Ground.Handle;
                JMap sm = new JMap();
                sm.Add("id", sp.Id);
                sm.Add("label", sp.Label);
                sm.Add("handle", sp.Handle);
                sm.Add("points", sp.Pts.Count);
                sm.Add("color", sp.Color);
                sm.Add("layer", sp.Layer);
                sm.Add("linetype", string.IsNullOrEmpty(sp.Linetype) ? "CONTINUOUS" : sp.Linetype);
                sm.Add("first", new List<object> { sp.Pts[0][0], sp.Pts[0][1] });
                sm.Add("last", new List<object> { sp.Pts[sp.Pts.Count - 1][0], sp.Pts[sp.Pts.Count - 1][1] });
                seriesOut.Add(sm);
            }
            result.Add("series", seriesOut);
            if (plan.LabelPolicy != null) result.Add("labelPolicy", plan.LabelPolicy);
            if (plan.Legend != null)
            {
                JMap lg = plan.Legend;
                List<object> legendHandles = new List<object>();
                List<object> legendTexts = new List<object>();
                foreach (SecSpec spec in plan.Specs)
                {
                    if (spec.Role == "legend" && !string.IsNullOrEmpty(spec.Handle)) legendHandles.Add(spec.Handle);
                    if (spec.Role == "legend-text") legendTexts.Add(spec.Text);
                }
                lg.Add("handles", legendHandles);
                lg.Add("texts", legendTexts);
                result.Add("legend", lg);
            }
            List<object> expSeries = new List<object>();
            foreach (SecSeriesPlan sp in plan.Series)
            {
                JMap em = new JMap();
                em.Add("id", sp.Id);
                em.Add("points", sp.Pts.Count);
                em.Add("color", sp.Color);
                em.Add("layer", sp.Layer);
                expSeries.Add(em);
            }
            expectedSeries = expSeries;
        }

        [ThreadStatic]
        private static List<object> expectedSeries;
        /// <summary>Entities grouped by their role (ground/water-line/water-text/elev/pointno/axis/
        /// tick/legend/thalweg/...). The data side uses it to reconcile its own expectations.</summary>
        private static JMap ByRole(List<SecSpec> specs)
        {
            JMap m = new JMap();
            foreach (SecSpec s in specs)
            {
                string k = string.IsNullOrEmpty(s.Role) ? "other" : s.Role;
                m.Add(k, (m.Has(k) ? m.Int(k, 0) : 0) + 1);
            }
            return m;
        }

        /// <summary>Water-level details (value/label/y/color and the label text + insertion point).</summary>
        private static List<object> WatersOut(SecPlan plan)
        {
            List<object> l = new List<object>();
            foreach (SecWaterDraw w in plan.Waters)
            {
                JMap m = new JMap();
                m.Add("value", w.Value);
                m.Add("label", w.Label);
                m.Add("y", w.Y);
                m.Add("color", w.Color);
                l.Add(m);
            }
            foreach (SecSpec s in plan.Specs)
            {
                if (s.Role != "water-text") continue;
                foreach (object o in l)
                {
                    JMap m = o as JMap;
                    if (m == null || m.Has("text")) continue;
                    if (Math.Abs(m.Dbl("y", 0) + 2.0 - s.Ins[1]) < 1e-6)
                    {
                        m.Add("text", s.Text);
                        m.Add("insertion", new List<object> { s.Ins[0], s.Ins[1] });
                        break;
                    }
                }
            }
            return l;
        }
        private static JMap CountTypes(List<SecSpec> specs)
        {
            JMap m = new JMap();
            foreach (SecSpec s in specs)
            {
                string k = s.Kind == "poly" ? "LWPOLYLINE" : s.Kind == "line" ? "LINE" : s.Kind == "text" ? "TEXT" : "CIRCLE";
                m.Add(k, (m.Has(k) ? m.Int(k, 0) : 0) + 1);
            }
            return m;
        }

        private static JMap CountLayers(List<SecSpec> specs)
        {
            JMap m = new JMap();
            foreach (SecSpec s in specs)
            {
                m.Add(s.Layer, (m.Has(s.Layer) ? m.Int(s.Layer, 0) : 0) + 1);
            }
            return m;
        }

        private static object CreateEntity(object ms, SecSpec spec)
        {
            try
            {
                if (spec.Kind == "poly")
                {
                    List<double> flat = new List<double>();
                    foreach (double[] v in spec.Verts) { flat.Add(v[0]); flat.Add(v[1]); }
                    return Com.Call(ms, "AddLightWeightPolyline", (object)flat.ToArray());
                }
                if (spec.Kind == "line")
                    return Com.Call(ms, "AddLine", (object)new double[] { spec.P1[0], spec.P1[1], 0.0 },
                        (object)new double[] { spec.P2[0], spec.P2[1], 0.0 });
                if (spec.Kind == "text")
                    return Com.Call(ms, "AddText", (object)spec.Text,
                        (object)new double[] { spec.Ins[0], spec.Ins[1], 0.0 }, (object)spec.Height);
                if (spec.Kind == "circle")
                    return Com.Call(ms, "AddCircle", (object)new double[] { spec.Center[0], spec.Center[1], 0.0 }, (object)spec.Radius);
            }
            catch (Exception e)
            {
                throw new ZwcError("CREATE_FAILED", "Add* failed for " + spec.Kind + ": " + Com.Hr(e), null, null);
            }
            throw new ZwcError("CREATE_FAILED", "unknown spec kind " + spec.Kind, null, null);
        }

        private static void EnsureLayer(object layers, string name, Ctx ctx)
        {
            object lyr = null;
            try { lyr = Com.Call(layers, "Item", name); }
            catch (Exception) { lyr = null; }
            if (lyr == null)
            {
                try { lyr = Com.Call(layers, "Add", name); }
                catch (Exception e)
                {
                    // maybe it was created concurrently
                    try { lyr = Com.Call(layers, "Item", name); }
                    catch (Exception) { lyr = null; }
                    if (lyr == null)
                        throw new ZwcError("LAYER_FAILED", "cannot create layer " + name + ": " + Com.Hr(e), null, null);
                }
            }
            if (name.EndsWith("-WATER", StringComparison.OrdinalIgnoreCase)) Com.TrySet(lyr, "Color", 5);
            else if (name.EndsWith("-AXIS", StringComparison.OrdinalIgnoreCase)) Com.TrySet(lyr, "Color", 8);
            else Com.TrySet(lyr, "Color", 7);
            if (name.EndsWith("-GROUND", StringComparison.OrdinalIgnoreCase)) Com.TrySet(lyr, "Lineweight", 30);
            Com.TrySet(lyr, "LayerOn", true);
        }

        /// <summary>First file from <paramref name="names"/> that exists in the CAD's Fonts folder.
        /// Windows file lookups are case-insensitive, so "HZTXT.SHX" and "hztxt.shx" both match.</summary>
        private static string FirstExistingFont(string fontDir, string[] names)
        {
            if (string.IsNullOrEmpty(fontDir) || !Directory.Exists(fontDir)) return null;
            foreach (string n in names)
            {
                try { if (File.Exists(Path.Combine(fontDir, n))) return n; }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Create - or repair - the tool's own text style so Chinese actually renders.
        ///
        /// Both CADs default a new style to a Western-only SHX (txt.shx), which is why Chinese
        /// text came out as "????" in AutoCAD 2007 while the strings themselves were always
        /// correct (read-back and the 268-assertion cross-check agreed). We set an SHX pair taken
        /// from the fonts that really exist in THIS installation's Fonts folder - a Western SHX
        /// plus a GB2312 "big font" - and fall back to a TrueType CJK face (simsun.ttc) when no
        /// usable SHX pair is found. Only styles carrying the tool's prefix are ever touched, and
        /// an existing style is corrected in place (idempotent), so a style left behind by an
        /// earlier run with the wrong font gets fixed too.</summary>
        private static JMap EnsureTextStyle(object doc, string name, string exePath, Ctx ctx)
        {
            JMap info = new JMap();
            info.Add("name", name);
            info.Add("created", false);
            info.Add("method", null);
            info.Add("fontFile", null);
            info.Add("bigFontFile", null);
            info.Add("cjk", false);

            object tss = Com.TryGet(doc, "TextStyles");
            if (tss == null)
            {
                ctx.Warn("TEXTSTYLE_UNAVAILABLE", "document has no TextStyles collection");
                return info;
            }

            object st = null;
            try { st = Com.Call(tss, "Item", name); }
            catch (Exception) { st = null; }
            if (st == null)
            {
                try { st = Com.Call(tss, "Add", name); info.Add("created", true); }
                catch (Exception e)
                {
                    ctx.Warn("TEXTSTYLE_FAILED", "cannot create text style " + name + ": " + Com.Hr(e));
                    return info;
                }
            }

            string fontDir = null;
            try
            {
                if (!string.IsNullOrEmpty(exePath))
                {
                    string d = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(d)) fontDir = Path.Combine(d, "Fonts");
                }
            }
            catch (Exception) { }

            // 1) SHX pair: Western base font + GB2312 big font (AutoCAD 2007: gbenor+gbcbig,
            //    ZWCAD Classic: txt+HZTXT - both discovered by probing the Fonts folder).
            string western = FirstExistingFont(fontDir, new string[] { "gbenor.shx", "txt.shx", "simplex.shx", "ic-txt.shx" });
            string big = FirstExistingFont(fontDir, new string[] { "gbcbig.shx", "hztxt.shx", "SHZTXT.SHX", "whgtxt.shx" });
            bool ok = false;
            if (western != null && big != null)
            {
                bool a = Com.TrySet(st, "FontFile", western);
                bool b = Com.TrySet(st, "BigFontFile", big);
                if (a && b) { ok = true; info.Add("method", "shx"); }
            }

            // 2) TrueType CJK fallback (system font, present on every Windows box).
            if (!ok)
            {
                string ttf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Path.Combine("Fonts", "simsun.ttc"));
                if (File.Exists(ttf))
                {
                    Com.TrySet(st, "BigFontFile", "");
                    if (Com.TrySet(st, "FontFile", "simsun.ttc")) { ok = true; info.Add("method", "ttf"); }
                }
            }

            info.Add("fontFile", Com.Str(Com.TryGet(st, "FontFile")));
            info.Add("bigFontFile", Com.Str(Com.TryGet(st, "BigFontFile")));
            info.Add("cjk", ok);
            if (!ok)
                ctx.Warn("TEXTSTYLE_CJK_UNAVAILABLE",
                    "no CJK-capable font could be applied to text style " + name + "; Chinese may render as '????'");
            return info;
        }

        /// <summary>Make the dash linetype available and report where it came from plus a sane
        /// per-entity scale.
        ///
        /// AutoCAD ships TWO definitions of DASHED: acad.lin is imperial (0.5 / -0.25 drawing
        /// units) and acadiso.lin is metric (12.7 / -6.35 mm). Loading acad.lin in a 1 unit = 1 mm
        /// drawing makes the dashes 0.5 mm long - i.e. the water line LOOKS solid at any normal
        /// zoom, which is exactly what the AutoCAD 2007 screenshot showed while the entity really
        /// carried linetype DASHED. So: try acadiso.lin first, and if only the imperial file could
        /// be loaded bump the entity LinetypeScale to 20 (0.5 mm * 20 = 10 mm dashes).</summary>
        private static JMap EnsureLinetype(object doc, string name, string kind, Ctx ctx)
        {
            JMap info = new JMap();
            info.Add("name", name);
            info.Add("ok", false);
            info.Add("source", null);
            info.Add("scale", 1.0);

            object lts = Com.TryGet(doc, "Linetypes");
            if (lts == null) return info;

            try
            {
                object lt = Com.Call(lts, "Item", name);
                if (lt != null)
                {
                    // already in the drawing (ZWCAD templates ship it): keep its definition and
                    // use a neutral scale - verified visually, see docs\section-zwcad16.png
                    info.Add("ok", true);
                    info.Add("source", "already-loaded");
                    return info;
                }
            }
            catch (Exception) { }

            string[] files;
            if (!string.IsNullOrEmpty(kind) && kind.IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0)
                files = new string[] { "acadiso.lin", "acad.lin" };
            else
                files = new string[] { "zwcadiso.lin", "zwcad.lin", "acadiso.lin", "acad.lin" };
            foreach (string f in files)
            {
                try
                {
                    Com.Call(lts, "Load", name, f);
                    info.Add("ok", true);
                    info.Add("source", "loaded:" + f);
                    bool metric = f.ToLowerInvariant().IndexOf("iso") >= 0;
                    info.Add("scale", metric ? 1.0 : 20.0);
                    return info;
                }
                catch (Exception) { }
            }
            return info;
        }
        internal static void Rollback(object doc, int countBefore, Ctx ctx)
        {
            Com.TryCall(doc, "EndUndoMark");
            try
            {
                int now = CountOf(doc, "ModelSpace");
                int guard = 0;
                while (now > countBefore && guard < 12)
                {
                    Cmd.Send(doc, "_.U\n", ctx, "rollback-undo");
                    guard++;
                    int next = WaitIdle(doc, 4000);
                    if (next >= now)
                    {
                        // a slow/wedged instance sometimes applies the undo only on the next press
                        Thread.Sleep(250);
                        Cmd.Send(doc, "_.U\n", ctx, "rollback-undo");
                        guard++;
                        next = WaitIdle(doc, 4000);
                    }
                    if (next >= now) break; // nothing moved; do not loop forever
                    now = next;
                }
                if (CountOf(doc, "ModelSpace") != countBefore)
                    ctx.Warn("ROLLBACK_INCOMPLETE", "rollback left ModelSpace.Count=" + CountOf(doc, "ModelSpace")
                        + " (baseline " + countBefore + "); the user may need one more Ctrl+Z");
            }
            catch (Exception e)
            {
                ctx.Warn("ROLLBACK_FAILED", "rollback failed: " + e.Message);
            }
        }

        /// <summary>Wait until CAD is idle (CMDACTIVE=0, prompt clean) AND ModelSpace.Count has been
        /// stable for two consecutive samples, then return that count. Needed because SendCommand is
        /// only near-synchronous: ZWCAD 2025 applies "_.U" after the call returns, so reading the
        /// count immediately made a successful undo look like "removed 0".</summary>
        internal static int WaitIdle(object doc, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int last = CountOf(doc, "ModelSpace");
            int stable = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(40);
                int now = CountOf(doc, "ModelSpace");
                object ca = Com.TryCall(doc, "GetVariable", "CMDACTIVE");
                bool idle = Com.I32(ca) == 0 && !Cmd.PromptPolluted(Cmd.LastPrompt(doc));
                if (now == last && idle)
                {
                    stable++;
                    if (stable >= 2) return now;
                }
                else
                {
                    stable = 0;
                    last = now;
                }
            }
            return CountOf(doc, "ModelSpace");
        }

        private static JMap VerifySection(object doc, SecPlan plan, int countBefore, bool dashed, double dashScale, Ctx ctx)
        {
            List<object> issues = new List<object>();

            // Right after a burst of Add* calls CAD may still be busy; reading properties then makes
            // single entities look empty (measured symptom: two random texts reported TextString=""
            // Height=0 on every retry). Wait for quiescence before the read-back pass.
            // NOTE: do NOT call Document.Regen here - on ZWCAD Classic 16 a COM Regen() after a
            // batch left the instance unusable (ROT entry gone, SendCommand stopped applying).
            WaitIdle(doc, 2500);

            int countAfter = CountOf(doc, "ModelSpace");
            int delta = countAfter - countBefore;
            if (delta != plan.ExpectedTotal)
                AddIssue(issues, "COUNT_DELTA", "ModelSpace.Count grew by " + delta + " but expected " + plan.ExpectedTotal,
                    countBefore, countAfter);

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool anyGeom = false;
            int checkedCount = 0;

            foreach (SecSpec spec in plan.Specs)
            {
                checkedCount++;
                if (string.IsNullOrEmpty(spec.Handle))
                {
                    AddIssue(issues, "HANDLE_MISSING", "no handle recorded for a " + spec.Kind, null, null);
                    continue;
                }
                object e = Com.EntityByHandle(doc, spec.Handle);
                if (e == null)
                {
                    AddIssue(issues, "ENTITY_NOT_FOUND", "HandleToObject(" + spec.Handle + ") returned nothing", spec.Handle, null);
                    continue;
                }
                string type = Com.Str(Com.TryGet(e, "ObjectName"));
                if (!TypeMatches(type, spec.Expect))
                    AddIssue(issues, "TYPE_MISMATCH", "handle " + spec.Handle + " type is '" + type + "', expected *" + spec.Expect, spec.Handle, type);
                string layer = Com.Str(Com.TryGet(e, "Layer"));
                if (!string.Equals(layer, spec.Layer, StringComparison.OrdinalIgnoreCase))
                    AddIssue(issues, "LAYER_MISMATCH", "handle " + spec.Handle + " layer is '" + layer + "', expected '" + spec.Layer + "'", spec.Handle, layer);

                if (spec.Kind == "poly")
                {
                    double[] flat = Com.Flat(Com.TryGet(e, "Coordinates"));
                    if (flat.Length != spec.Verts.Count * 2)
                    {
                        AddIssue(issues, "GEOMETRY_MISMATCH", "ground polyline has " + (flat.Length / 2) + " vertices, expected " + spec.Verts.Count, spec.Handle, null);
                    }
                    else
                    {
                        for (int i = 0; i < spec.Verts.Count; i++)
                        {
                            if (Math.Abs(flat[i * 2] - spec.Verts[i][0]) > 1e-6 || Math.Abs(flat[i * 2 + 1] - spec.Verts[i][1]) > 1e-6)
                            {
                                AddIssue(issues, "GEOMETRY_MISMATCH", "ground vertex " + (i + 1) + " is ("
                                    + flat[i * 2].ToString("R", CultureInfo.InvariantCulture) + ","
                                    + flat[i * 2 + 1].ToString("R", CultureInfo.InvariantCulture) + ") expected ("
                                    + spec.Verts[i][0].ToString("R", CultureInfo.InvariantCulture) + ","
                                    + spec.Verts[i][1].ToString("R", CultureInfo.InvariantCulture) + ")", spec.Handle, null);
                                break;
                            }
                        }
                    }
                    // the polyline is the ground line: it defines the top/bottom of the drawing, so it
                    // MUST take part in the bbox union (missing this made every section report a
                    // BBOX_MISMATCH against its own correct output)
                    for (int i = 0; i + 1 < flat.Length; i += 2)
                    {
                        Extend(ref minX, ref minY, ref maxX, ref maxY, flat[i], flat[i + 1]);
                        anyGeom = true;
                    }
                }
                else if (spec.Kind == "line")
                {
                    double[] p1 = Com.Pt(Com.TryGet(e, "StartPoint"));
                    double[] p2 = Com.Pt(Com.TryGet(e, "EndPoint"));
                    if (p1 == null || p2 == null)
                        AddIssue(issues, "GEOMETRY_MISMATCH", "line " + spec.Handle + " has no StartPoint/EndPoint", spec.Handle, null);
                    else if (Math.Abs(p1[0] - spec.P1[0]) > 1e-6 || Math.Abs(p1[1] - spec.P1[1]) > 1e-6
                          || Math.Abs(p2[0] - spec.P2[0]) > 1e-6 || Math.Abs(p2[1] - spec.P2[1]) > 1e-6)
                        AddIssue(issues, "GEOMETRY_MISMATCH", "line " + spec.Handle + " endpoints differ from the plan", spec.Handle, null);
                    if (dashed && spec.Layer.EndsWith("-WATER", StringComparison.OrdinalIgnoreCase))
                    {
                        string lt = Com.Str(Com.TryGet(e, "Linetype"));
                        if (!"DASHED".Equals(lt, StringComparison.OrdinalIgnoreCase))
                            AddIssue(issues, "LINETYPE_NOT_APPLIED", "water line " + spec.Handle + " linetype is '" + lt + "' expected 'DASHED'", spec.Handle, lt);
                        object lso = Com.TryGet(e, "LinetypeScale");
                        double ls = lso == null ? double.NaN : Com.Dbl(lso);
                        if (!double.IsNaN(ls) && Math.Abs(ls - dashScale) > 1e-6)
                            AddIssue(issues, "LINETYPE_SCALE_MISMATCH", "water line " + spec.Handle + " LinetypeScale is "
                                + ls.ToString("R", CultureInfo.InvariantCulture) + " expected " + dashScale.ToString("R", CultureInfo.InvariantCulture), spec.Handle, null);
                    }
                    if (p1 == null || p2 == null) { continue; }
                    Extend(ref minX, ref minY, ref maxX, ref maxY, Math.Min(p1[0], p2[0]), Math.Min(p1[1], p2[1]));
                    Extend(ref minX, ref minY, ref maxX, ref maxY, Math.Max(p1[0], p2[0]), Math.Max(p1[1], p2[1]));
                    anyGeom = true;
                }
                else if (spec.Kind == "circle")
                {
                    double[] c = Com.Pt(Com.TryGet(e, "Center"));
                    double rad = Com.Dbl(Com.TryGet(e, "Radius"));
                    if (c == null || Math.Abs(c[0] - spec.Center[0]) > 1e-6 || Math.Abs(c[1] - spec.Center[1]) > 1e-6
                        || Math.Abs(rad - spec.Radius) > 1e-6)
                        AddIssue(issues, "GEOMETRY_MISMATCH", "circle " + spec.Handle + " center/radius differ from the plan", spec.Handle, null);
                    if (c != null)
                    {
                        Extend(ref minX, ref minY, ref maxX, ref maxY, c[0] - rad, c[1] - rad);
                        Extend(ref minX, ref minY, ref maxX, ref maxY, c[0] + rad, c[1] + rad);
                        anyGeom = true;
                    }
                }
                else if (spec.Kind == "text")
                {
                    object to = Com.TryGet(e, "TextString");
                    if (to == null) to = Com.TryGet(e, "Text");
                    if (to == null)
                    {
                        AddIssue(issues, "PROPERTY_READ_FAILED", "cannot read TextString of handle " + spec.Handle + " (" + Com.LastError + ")", spec.Handle, null);
                    }
                    else
                    {
                        string actual = Com.Str(to);
                        if (!string.Equals(actual, spec.Text, StringComparison.Ordinal))
                            AddIssue(issues, "TEXT_MISMATCH", "handle " + spec.Handle + " text is '" + actual + "' expected '" + spec.Text + "'", spec.Handle, actual);
                    }
                    object hObj = Com.TryGet(e, "Height");
                    if (hObj == null)
                    {
                        AddIssue(issues, "PROPERTY_READ_FAILED", "cannot read Height of handle " + spec.Handle + " (" + Com.LastError + ")", spec.Handle, null);
                    }
                    else if (Math.Abs(Com.Dbl(hObj) - spec.Height) > 1e-6)
                    {
                        AddIssue(issues, "TEXT_HEIGHT_MISMATCH", "handle " + spec.Handle + " height is " + Com.Dbl(hObj).ToString("R", CultureInfo.InvariantCulture)
                            + " expected " + spec.Height.ToString("R", CultureInfo.InvariantCulture), spec.Handle, null);
                    }
                    double[] ins = Com.Pt(Com.TryGet(e, "InsertionPoint"));
                    if (ins != null && (Math.Abs(ins[0] - spec.Ins[0]) > 1e-6 || Math.Abs(ins[1] - spec.Ins[1]) > 1e-6))
                        AddIssue(issues, "TEXT_POS_MISMATCH", "handle " + spec.Handle + " insertion point differs from the plan", spec.Handle, null);
                }
            }

            // ground-line bbox must match the model (0.01 mm tolerance) - this is the check that
            // catches "drawn at the wrong scale" even when every individual entity looks plausible.
            double[] gb = plan.GroundBbox;
            double tol = 0.01;
            if (anyGeom)
            {
                if (Math.Abs(minX - plan.AllBbox[0]) > tol || Math.Abs(minY - plan.AllBbox[1]) > tol
                    || Math.Abs(maxX - plan.AllBbox[2]) > tol || Math.Abs(maxY - plan.AllBbox[3]) > tol)
                {
                    AddIssue(issues, "BBOX_MISMATCH", "created geometry bbox is ["
                        + minX.ToString("0.###", CultureInfo.InvariantCulture) + "," + minY.ToString("0.###", CultureInfo.InvariantCulture)
                        + " .. " + maxX.ToString("0.###", CultureInfo.InvariantCulture) + "," + maxY.ToString("0.###", CultureInfo.InvariantCulture)
                        + "] but the plan expects ["
                        + plan.AllBbox[0].ToString("0.###", CultureInfo.InvariantCulture) + "," + plan.AllBbox[1].ToString("0.###", CultureInfo.InvariantCulture)
                        + " .. " + plan.AllBbox[2].ToString("0.###", CultureInfo.InvariantCulture) + "," + plan.AllBbox[3].ToString("0.###", CultureInfo.InvariantCulture) + "]", null, null);
                }
            }
            else
            {
                AddIssue(issues, "BBOX_MISMATCH", "no geometry could be read back for the bbox check", null, null);
            }
            // GroundBbox already contains the origin offset, so compare the ORIGIN-RELATIVE values;
            // without subtracting it a drawing placed at -Origin 50,0 failed its own self-check.
            if (Math.Abs(gb[0] - plan.OriginX) > 1e-6 || Math.Abs(gb[1] - plan.OriginY - 10.0) > tol
                || Math.Abs(gb[2] - plan.OriginX - plan.W) > tol || Math.Abs(gb[3] - plan.OriginY - plan.H) > tol)
                AddIssue(issues, "MODEL_BBOX_INTERNAL", "internal: ground bbox " + Json.Write(new List<object> { gb[0], gb[1], gb[2], gb[3] })
                    + " does not match W/H", null, null);

            // ---- overlay semantics: every series must have been created and verified; the legend
            // rows must exist as text; the adaptive label policy must have produced its labels.
            int seriesChecked = 0;
            foreach (SecSeriesPlan sp in plan.Series)
            {
                seriesChecked++;
                if (sp.Ground == null || string.IsNullOrEmpty(sp.Ground.Handle))
                {
                    AddIssue(issues, "SERIES_MISSING", "series '" + sp.Id + "' has no ground-line handle", null, null);
                    continue;
                }
                object se = Com.EntityByHandle(doc, sp.Ground.Handle);
                if (se == null)
                {
                    AddIssue(issues, "SERIES_MISSING", "series '" + sp.Id + "' ground handle " + sp.Ground.Handle + " does not resolve", sp.Ground.Handle, null);
                    continue;
                }
                double[] flat = Com.Flat(Com.TryGet(se, "Coordinates"));
                if (flat.Length != sp.Pts.Count * 2)
                {
                    AddIssue(issues, "SERIES_VERTEX_COUNT_MISMATCH", "series '" + sp.Id + "' has " + (flat.Length / 2)
                        + " vertices, expected " + sp.Pts.Count, sp.Ground.Handle, null);
                    continue;
                }
                double fx = plan.X(sp.Pts[0][0]), fy = plan.Y(sp.Pts[0][1]);
                double lx = plan.X(sp.Pts[sp.Pts.Count - 1][0]), ly = plan.Y(sp.Pts[sp.Pts.Count - 1][1]);
                if (Math.Abs(flat[0] - fx) > 1e-6 || Math.Abs(flat[1] - fy) > 1e-6)
                    AddIssue(issues, "SERIES_ENDPOINT_MISMATCH", "series '" + sp.Id + "' first vertex differs from the plan", sp.Ground.Handle, null);
                int last = flat.Length - 2;
                if (Math.Abs(flat[last] - lx) > 1e-6 || Math.Abs(flat[last + 1] - ly) > 1e-6)
                    AddIssue(issues, "SERIES_ENDPOINT_MISMATCH", "series '" + sp.Id + "' last vertex differs from the plan", sp.Ground.Handle, null);
                string sl = Com.Str(Com.TryGet(se, "Layer"));
                if (!string.Equals(sl, sp.Layer, StringComparison.OrdinalIgnoreCase))
                    AddIssue(issues, "SERIES_LAYER_MISMATCH", "series '" + sp.Id + "' is on layer '" + sl + "', expected '" + sp.Layer + "'", sp.Ground.Handle, sl);
            }

            int legendTextSpecs = 0, legendTextHandles = 0;
            int elevSpecs = 0, pointNoSpecs = 0;
            foreach (SecSpec spec in plan.Specs)
            {
                if (spec.Role == "legend-text") { legendTextSpecs++; if (!string.IsNullOrEmpty(spec.Handle)) legendTextHandles++; }
                if (spec.Role == "elev") elevSpecs++;
                if (spec.Role == "pointno") pointNoSpecs++;
            }
            if (plan.Legend != null && plan.Legend.Bool("enabled", false))
            {
                List<object> rows = plan.Legend.List("rows");
                int want = rows == null ? 0 : rows.Count;
                if (legendTextSpecs != want || legendTextHandles != want)
                    AddIssue(issues, "LEGEND_MISSING", "legend has " + want + " rows but " + legendTextHandles
                        + " legend texts were created/verified", null, null);
            }
            if (plan.LabelPolicy != null)
            {
                int wantElev = plan.LabelPolicy.Int("labelled", -1);
                if (wantElev >= 0 && elevSpecs != wantElev)
                    AddIssue(issues, "LABEL_POLICY_MISMATCH", "label policy asked for " + wantElev
                        + " elevation labels, plan has " + elevSpecs, null, null);
                bool policySaysPointNo = plan.LabelPolicy.Bool("pointNumbers", false);
                if (policySaysPointNo && pointNoSpecs == 0)
                    AddIssue(issues, "LABEL_POLICY_MISMATCH", "label policy wants point numbers but none were planned", null, null);
            }

            JMap v = new JMap();
            v.Add("ok", issues.Count == 0);
            v.Add("seriesChecked", seriesChecked);
            v.Add("legendRows", legendTextHandles);
            v.Add("labels", elevSpecs);
            v.Add("issues", issues);
            v.Add("checked", checkedCount);
            v.Add("countBefore", countBefore);
            v.Add("countAfter", countAfter);
            v.Add("bbox", new List<object> { minX, minY, maxX, maxY });
            v.Add("expectedBbox", new List<object> { plan.AllBbox[0], plan.AllBbox[1], plan.AllBbox[2], plan.AllBbox[3] });
            return v;
        }

        private static void Extend(ref double minX, ref double minY, ref double maxX, ref double maxY, double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        private static void AddIssue(List<object> issues, string code, string message, object handle, object actual)
        {
            JMap m = new JMap();
            m.Add("code", code);
            m.Add("message", message);
            if (handle != null) m.Add("handle", handle);
            if (actual != null) m.Add("actual", actual);
            issues.Add(m);
        }

        private static bool TypeMatches(string type, string expect)
        {
            if (type == null || expect == null) return false;
            if (string.Equals(type, expect, StringComparison.OrdinalIgnoreCase)) return true;
            return type.EndsWith(expect, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ undo
        public static JMap UndoCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();

            string token = OptStr(args, "token");
            int count = args.Int("count", 0);
            JMap tok = null;
            JMap st = null;
            if (!string.IsNullOrEmpty(token))
            {
                st = State.Load();
                tok = State.Tokens(st).Map(token);
                if (tok == null)
                {
                    ctx.Warn("TOKEN_NOT_FOUND", "undoToken " + token + " is not in the state file (state was cleared or the token belongs to another temp dir)");
                    if (count <= 0) count = 1;
                }
            }
            if (count <= 0) count = 1;

            string matchedBy;
            object doc = cad.ResolveDoc(DocArg(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            int countBefore = CountOf(doc, "ModelSpace");
            int groups = tok == null ? count : Math.Max(1, tok.Int("undoGroups", 1));
            int target = tok == null ? -1 : tok.Int("baseline", -1);

            // A plain "send N x _.U" is not enough: view operations (the screenshot helper calls
            // ZoomExtents right before undo) can occupy undo slots, so the first _.U reverts a
            // zoom and the entity count does not move - which made a perfectly good undo look like
            // "removed 0". Keep pressing _.U (a few extra times at most) until the token baseline
            // is reached, and stop the moment we get there so nothing older gets reverted.
            int done = 0;
            int maxSends = groups + 4;
            int cur = countBefore;
            while (done < maxSends)
            {
                Cmd.Send(doc, "_.U\n", ctx, "undo");
                done++;
                cur = WaitIdle(doc, 3000);
                if (target >= 0 && cur <= target) break;
                if (target < 0 && done >= groups) break;
            }
            int countAfter = cur;

            JMap verify = new JMap();
            bool ok = true;
            if (tok != null)
            {
                int baseline = tok.Int("baseline", -1);
                ok = (baseline >= 0 && countAfter == baseline);
                verify.Add("baseline", baseline);
                if (!ok)
                    ctx.Warn("UNDO_BASELINE_MISMATCH", "ModelSpace.Count is " + countAfter + " but the token baseline is " + baseline
                        + " (other edits happened in between, or the drawing was modified outside this tool)");
            }
            verify.Add("ok", ok);
            verify.Add("countBefore", countBefore);
            verify.Add("countAfter", countAfter);
            verify.Add("requestedGroups", groups);

            JMap r = new JMap();
            r.Add("removed", countBefore - countAfter);
            r.Add("countBefore", countBefore);
            r.Add("countAfter", countAfter);
            r.Add("undoGroups", done);
            r.Add("token", token);
            r.Add("verify", verify);
            r.Add("matchedBy", matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            return r;
        }

        /// <summary>Cheap idempotent re-apply of the 〔AI协作〕 title marker after every command.
        /// CAD resets its window text on document switches/regens, so setting it once in `hint` is
        /// not enough (observed in the user's screenshot review).</summary>
        internal static void ReapplyCaption(Ctx ctx)
        {
            try
            {
                if (ctx == null || ctx.AppMap == null) return;
                long hwnd = Com.I32(Com.TryGet(ctx.AppMap, "hwnd"));
                if (hwnd <= 0) return;
                JMap st = State.Load();
                JMap caps = st.Map("captions");
                if (caps == null) return;
                string key = hwnd.ToString(CultureInfo.InvariantCulture);
                string baseCaption = caps.Str(key, null);
                if (baseCaption == null) return;
                JMap hints = st.Map("hintTexts");
                string hintText = hints == null ? null : hints.Str(key, null);
                string want = hintText != null && hintText.Length > 0
                    ? CaptionPrefix + baseCaption + " - " + hintText
                    : CaptionPrefix + baseCaption;
                StringBuilder sb = new StringBuilder(512);
                Cad.GetWindowTextW(new IntPtr(unchecked((int)hwnd)), sb, 512);
                if (sb.ToString() != want) Cad.SetWindowCaption(hwnd, want);
            }
            catch (Exception) { }
        }
        // ------------------------------------------------------------------ lispgen (debug)
        /// <summary>lispgen: write the generated dump script to a file without touching CAD.
        /// Used by tests and by the "validate before inject" workflow (ASCII/no-BOM + balance).</summary>
        public static JMap LispGenCmd(JMap req, JMap args, Ctx ctx)
        {
            bool geom = args.Bool("geom", true);
            string outFile = args.Str("out", null);
            if (string.IsNullOrEmpty(outFile))
                outFile = System.IO.Path.Combine(State.Dir(), "lispgen-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".lsp");
            string txt = System.IO.Path.Combine(State.Dir(), "lispgen-out.txt");
            string done = System.IO.Path.Combine(State.Dir(), "lispgen-out.done");
            string filter = args.Str("filter", null);
            string src = LispTemplate.BuildDump(geom, txt, done, string.IsNullOrEmpty(filter) ? null : filter);
            LispTemplate.WriteScript(outFile, src);
            JMap r = new JMap();
            r.Add("path", outFile);
            r.Add("bytes", src.Length);
            r.Add("ascii", LispTemplate.IsAscii(src));
            r.Add("balance", LispTemplate.BalanceError(src));
            r.Add("filter", filter);
            return r;
        }
        // ------------------------------------------------------------------ cmdEcho guard (M3-E6)
        /// <summary>Read CMDECHO before an injection so the finally block can restore it even when the
        /// LISP script dies mid-way (a failed script once left the user's command line silent).</summary>
        internal static int CmdEchoOf(object doc)
        {
            try { return Com.I32(Com.Call(doc, "GetVariable", "CMDECHO")); }
            catch (Exception) { return 1; }
        }

        internal static void RestoreCmdEcho(object doc, int oldValue)
        {
            try
            {
                int now = Com.I32(Com.Call(doc, "GetVariable", "CMDECHO"));
                if (now == oldValue) return;
                Com.Call(doc, "SendCommand", "(setvar \"CMDECHO\" " + oldValue + ")\n");
            }
            catch (Exception) { }
        }
        // ------------------------------------------------------------------ show / reveal
        /// <summary>`show`: make the currently attached CAD window visible and bring it forward.</summary>
        public static JMap ShowCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            JMap r = Cad.Reveal(cad, ctx);
            ctx.AppMap = cad.AppInfo();
            r.Add("product", cad.Product);
            r.Add("progId", cad.ProgId);
            r.Add("documents", cad.Documents().Count);
            return r;
        }
        // ------------------------------------------------------------------ hint
        public static JMap HintCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string text = args.Str("text", null);
            bool clear = args.Bool("clear", false);
            JMap res = ApplyCaption(cad, ctx, clear ? "" : text);
            return res;
        }

        private const string CaptionPrefix = "\u3014AI\u534f\u4f5c\u3015 ";

        private static JMap ApplyCaption(Cad cad, Ctx ctx, string text)
        {
            JMap r = new JMap();
            r.Add("captionSet", false);
            r.Add("statusBar", false);
            if (cad == null || cad.Hwnd == 0) return r;

            string key = cad.Hwnd.ToString(CultureInfo.InvariantCulture);
            JMap st = State.Load();
            JMap caps = State.Captions(st);

            string baseCaption = caps.Str(key, null);
            if (baseCaption == null)
            {
                baseCaption = Com.Str(Com.TryGet(cad.App, "Caption"));
                if (baseCaption == null) baseCaption = "";
                caps.Add(key, baseCaption);
                State.Save(st);
            }

            string want;
            if (text == null || text.Length == 0)
                want = CaptionPrefix + baseCaption;
            else
                want = CaptionPrefix + baseCaption + " - " + text;

            r.Add("base", baseCaption);
            r.Add("caption", want);
            bool set = Cad.SetWindowCaption(cad.Hwnd, want);
            r.Add("captionSet", set);
            if (!set) ctx.Warn("CAPTION_FAILED", "SetWindowText failed for HWND " + cad.Hwnd);

            if (text != null && text.Length > 0)
            {
                bool barOk = Com.TrySet(cad.App, "StatusBar", text);
                r.Add("statusBar", barOk);
                if (!barOk) ctx.Warn("STATUSBAR_FAILED", "Application.StatusBar rejected the text");
            }
            return r;
        }

        // ------------------------------------------------------------------ finish
        public static JMap Finish(JMap req, JMap args, Ctx ctx)
        {
            bool quit = args.Bool("quit", false);
            int procsBefore = Procs.ZwcadCount();
            JMap st = State.Load();
            JMap sess = State.Session(st);
            JMap caps = State.Captions(st);

            JMap r = new JMap();
            r.Add("processesBefore", procsBefore);
            r.Add("session", sess);
            r.Add("quitRequested", quit);

            // UX: leftover /Automation -Embedding instances have no visible window; list them and,
            // on -SweepHidden, terminate only those hidden automation processes of this product.
            List<object> hidden = Procs.HiddenAutomation(Kind(req, args));
            r.Add("hiddenInstances", hidden);
            if (args.Bool("sweepHidden", false) && hidden.Count > 0)
            {
                List<object> swept = new List<object>();
                foreach (object o in hidden)
                {
                    JMap h = o as JMap;
                    if (h == null) continue;
                    int pid = h.Int("pid", 0);
                    try
                    {
                        using (Process hp = Process.GetProcessById(pid)) { hp.Kill(); hp.WaitForExit(4000); }
                        swept.Add(pid);
                    }
                    catch (Exception e) { ctx.Warn("SWEEP_FAILED", "hidden instance pid " + pid + " could not be killed: " + e.Message); }
                }
                r.Add("sweptHidden", swept);
            }

            Cad cad = null;
            try { cad = Cad.Attach(Kind(req, args), false, ctx, Cad.ExplicitProgId(req, args)); }
            catch (ZwcError) { cad = null; }

            bool ours = false;
            if (cad != null)
            {
                ctx.AppMap = cad.AppInfo();
                ours = sess != null && State.OwnsHwnd(st, cad.Hwnd);
                r.Add("attached", true);
                r.Add("ours", ours);

                // restore the window caption first: if we do not quit the user keeps a clean title
                bool hintCleared = false;
                if (cad.Hwnd != 0)
                {
                    string key = cad.Hwnd.ToString(CultureInfo.InvariantCulture);
                    string baseCaption = caps.Str(key, null);
                    if (baseCaption != null)
                    {
                        hintCleared = Cad.SetWindowCaption(cad.Hwnd, baseCaption);
                        caps.Add(key, null);
                        // drop the key
                        JMap fresh = new JMap();
                        foreach (string k in caps.Keys) if (caps.Get(k) != null) fresh.Add(k, caps.Get(k));
                        st.Add("captions", fresh);
                        State.Save(st);
                    }
                }
                r.Add("hintCleared", hintCleared);

                if (ours && quit)
                {
                    // close every document WITHOUT saving, then Quit - the only combination that
                    // leaves no zombie process (a bare Quit on a dirty document pops the save dialog).
                    int closed = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        List<object> docs = cad.Documents();
                        if (docs.Count == 0) break;
                        try { Com.Call(docs[docs.Count - 1], "Close", false); closed++; }
                        catch (Exception e)
                        {
                            ctx.Warn("CLOSE_FAILED", "Document.Close(false) failed: " + Com.Hr(e));
                            break;
                        }
                    }
                    r.Add("docsClosed", closed);
                    Com.TryCall(cad.App, "Quit");
                    State.RemoveInstance(st, cad.Hwnd);
                    State.Save(st);
                    // release every COM reference we still hold and collect, otherwise this process
                    // (in daemon mode it outlives the command) keeps the CAD app alive and its
                    // shutdown is delayed - measured: finish took 49.7 s instead of ~20 s
                    try
                    {
                        if (cad.App != null) Marshal.ReleaseComObject(cad.App);
                    }
                    catch (Exception) { }
                    cad.App = null;
                    cad = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                else if (ours && !quit)
                {
                    ctx.Warn("NOT_QUIT", "this instance was started by the tool but -Quit was not given; it is still running");
                }
                else if (!ours && quit)
                {
                    ctx.Warn("SKIP_QUIT", "the running CAD instance was NOT started by this tool - refusing to quit it");
                }

                // docs left after the optional close/quit
                int docsLeft = -1;
                try { docsLeft = cad.Documents().Count; }
                catch (Exception) { docsLeft = -1; }
                r.Add("docsLeft", docsLeft);
            }
            else
            {
                r.Add("attached", false);
                r.Add("ours", false);
                r.Add("docsLeft", 0);
                if (quit)
                    ctx.Warn("NO_INSTANCE", "no CAD instance was running, nothing to quit");
            }

            // poll until the processes are really gone (measured 5.4-6.9 s for Close+Quit)
            // polling for the CAD process to disappear is a courtesy, not a contract: 25 s is
            // plenty for a clean quit, and the caller gets its answer sooner (measured: the daemon
            // path used to spend the whole 45 s cap because the old COM reference stayed rooted)
            // Count OUR product family (acad vs zwcad), not always ZWCAD: AutoCAD 2007 answered
            // Quit() successfully but kept its process alive, and the old counter watched the wrong
            // image name, so callers had to kill it by hand. Every step below stays bounded.
            string kindForProcs = Kind(req, args);
            int timeoutMs = args.Int("timeoutSec", 25) * 1000;
            Stopwatch sw = Stopwatch.StartNew();
            int after = Procs.FamilyCount(kindForProcs);
            while (after > 0 && ours && quit && sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(250);
                after = Procs.FamilyCount(kindForProcs);
            }
            bool forcedKill = false;
            int ownPid = sess == null ? 0 : sess.Int("pid", 0);
            if (ours && quit && after > 0)
            {
                // second Quit, then terminate the PID WE started (never a foreign instance)
                try { if (cad != null && cad.App != null) Com.Call(cad.App, "Quit"); } catch (Exception) { }
                Stopwatch sw2 = Stopwatch.StartNew();
                while (after > 0 && sw2.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(250);
                    after = Procs.FamilyCount(kindForProcs);
                }
                if (after > 0 && ownPid > 0)
                {
                    try
                    {
                        using (Process own = Process.GetProcessById(ownPid))
                        {
                            own.Kill();
                            own.WaitForExit(5000);
                        }
                        forcedKill = true;
                        ctx.Warn("QUIT_FORCED", "the CAD build accepted Quit() but kept running; the PID we started (" + ownPid + ") was terminated");
                    }
                    catch (Exception eKill) { ctx.Warn("QUIT_FORCED_FAILED", "forced kill of pid " + ownPid + " failed: " + eKill.Message); }
                    Stopwatch sw3 = Stopwatch.StartNew();
                    after = Procs.FamilyCount(kindForProcs);
                    while (after > 0 && sw3.ElapsedMilliseconds < 5000) { Thread.Sleep(250); after = Procs.FamilyCount(kindForProcs); }
                }
            }
            r.Add("processesAfter", after);
            r.Add("processes", Procs.Family(kindForProcs));
            r.Add("processWaitMs", sw.ElapsedMilliseconds);
            r.Add("forcedKill", forcedKill);
            return r;
        }
    }

    // ======================================================================================
    // Entry point
    // ======================================================================================
    // ======================================================================================
    // M3-E1 performance base: resident daemon + pipe protocol + request dispatcher
    // ======================================================================================
    internal static class PipeIo
    {
        public static string ReadMessage(Stream s)
        {
            byte[] hdr = new byte[4];
            if (!ReadFully(s, hdr, 4)) return null;
            int len = BitConverter.ToInt32(hdr, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            byte[] buf = new byte[len];
            if (!ReadFully(s, buf, len)) return null;
            return Encoding.UTF8.GetString(buf);
        }

        public static void WriteMessage(Stream s, string text)
        {
            byte[] buf = Encoding.UTF8.GetBytes(text ?? "");
            byte[] hdr = BitConverter.GetBytes(buf.Length);
            s.Write(hdr, 0, 4);
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        private static bool ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    internal static class Daemon
    {
        public static int Run(string pipeName, int idleMs)
        {
            if (idleMs <= 0) idleMs = 20 * 60 * 1000;
            int bindFailures = 0;
            while (true)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    Task wait = pipe.WaitForConnectionAsync();
                    if (!wait.Wait(idleMs)) return 0;            // idle -> self destruct
                    bindFailures = 0;
                }
                catch (Exception)
                {
                    try { if (pipe != null) pipe.Dispose(); } catch (Exception) { }
                    bindFailures++;
                    if (bindFailures >= 10) return 0;   // ~2 s: another daemon owns the pipe   // another daemon owns this pipe: stop piling up
                    Thread.Sleep(200);
                    continue;
                }

                string respJson = null;
                try
                {
                    string reqJson = PipeIo.ReadMessage(pipe);
                    if (reqJson == null)
                    {
                        respJson = "{\"ok\":false,\"cmd\":\"\",\"error\":{\"code\":\"BAD_REQUEST\",\"message\":\"empty or truncated pipe message\"}}";
                    }
                    else
                    {
                        // P2-5.4: route the two pipe-level commands by the request's top-level "cmd"
                        // field, not by searching the raw text. The old IndexOf("\"ping\"") match sent
                        // ANY request that merely mentioned ping into the ping branch - e.g.
                        // {"cmd":"query","args":{"text":"ping"}} came back as {"cmd":"ping"} instead
                        // of a query result (verified against the live daemon before this fix).
                        // Parsing costs nothing here: this daemon serves a handful of requests per
                        // minute, not a hot loop. A payload that does not parse falls through
                        // unchanged so the normal dispatcher answers BAD_ARGS exactly as before -
                        // a malformed request must never be refused by this branch.
                        string pipeCmd = null;
                        try
                        {
                            JMap reqMap = Json.Parse(reqJson) as JMap;
                            if (reqMap != null) pipeCmd = reqMap.Str("cmd", "");
                        }
                        catch (Exception) { pipeCmd = null; }

                        if (pipeCmd != null && string.Equals(pipeCmd, "daemon-stop", StringComparison.Ordinal))
                        {
                            // no WaitForPipeDrain here: a client that never reads its answer would
                            // block the daemon forever inside the pipe (measured: the daemon stayed
                            // alive after "stop" and then refused every later connection)
                            PipeIo.WriteMessage(pipe, "{\"ok\":true,\"cmd\":\"daemon-stop\",\"result\":{\"stopped\":true},\"elapsedMs\":0}");
                            try { pipe.Dispose(); } catch (Exception) { }
                            return 0;
                        }
                        else if (pipeCmd != null && string.Equals(pipeCmd, "ping", StringComparison.Ordinal))
                        {
                            PipeIo.WriteMessage(pipe, "{\"ok\":true,\"cmd\":\"ping\",\"result\":{\"pid\":" + Process.GetCurrentProcess().Id + "},\"elapsedMs\":0}");
                            respJson = null;
                        }
                        else
                        {
                            respJson = Program.HandleRequestText(reqJson);
                        }
                    }
                    if (respJson != null) PipeIo.WriteMessage(pipe, respJson);
                }
                catch (Exception e)
                {
                    try { PipeIo.WriteMessage(pipe, "{\"ok\":false,\"error\":{\"code\":\"DAEMON_ERROR\",\"message\":" + Json.Write(e.Message) + "}}"); }
                    catch (Exception) { }
                }
                finally
                {
                    try { pipe.Dispose(); } catch (Exception) { }
                }
            }
        }
    }

    internal static class Program
    {
        /// <summary>Dispatch one request JSON and return the response JSON. Used by the one-shot
        /// path (file in / file out) and by the daemon (pipe in / pipe out), so both transports
        /// receive byte-identical request/response documents.</summary>
        internal static string HandleRequestText(string reqJson)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Ctx ctx = new Ctx();
            JMap req = null;
            JMap resultOut = null;
            string cmd = "";
            bool ok = false;
            ZwcError zerr = null;
            Exception uerr = null;

            try
            {
                object parsed = Json.Parse(reqJson);
                req = parsed as JMap;
                if (req == null) throw new ZwcError("BAD_ARGS", "request JSON must be an object", null, null);
                cmd = req.Str("cmd", "");
                JMap argMap = req.Map("args");
                if (argMap == null) argMap = new JMap();
                ctx.PlanOnly = argMap.Bool("dryRun", false)
                    || string.Equals(argMap.Str("mode", ""), "plan", StringComparison.OrdinalIgnoreCase);

                switch (cmd)
                {
                    case "ping": resultOut = new JMap().Add("pid", Process.GetCurrentProcess().Id); break;
                    case "doctor": resultOut = Commands.Doctor(req, argMap, ctx); break;
                    case "status": resultOut = Commands.Status(req, argMap, ctx); break;
                    case "open": resultOut = Commands.Open(req, argMap, ctx); break;
                    case "info": resultOut = Commands.Info(req, argMap, ctx); break;
                    case "read": resultOut = Commands.Read(req, argMap, ctx); break;
                    case "section": resultOut = Stability.RunWrite(req, argMap, ctx, "section", delegate { return Commands.SectionCmd(req, argMap, ctx); }); break;
                    case "undo": resultOut = Stability.RunWrite(req, argMap, ctx, "undo", delegate { return Commands.UndoCmd(req, argMap, ctx); }); break;
                    case "hint": resultOut = Commands.HintCmd(req, argMap, ctx); break;
                    case "show": resultOut = Commands.ShowCmd(req, argMap, ctx); break;
                    case "lispgen": resultOut = Commands.LispGenCmd(req, argMap, ctx); break;
                    case "finish": resultOut = Commands.Finish(req, argMap, ctx); break;
                    case "entity": resultOut = Stability.RunWrite(req, argMap, ctx, "entity", delegate { return Edits.EntityCmd(req, argMap, ctx); }); break;
                    case "query": resultOut = Query.QueryCmd(req, argMap, ctx); break;
                    case "snapshot": resultOut = Query.SnapshotCmd(req, argMap, ctx); break;
                    case "diff": resultOut = Query.DiffCmd(req, argMap, ctx); break;
                    case "xprobe": resultOut = Edits.XProbeCmd(req, argMap, ctx); break;
                    default:
                        throw new ZwcError("BAD_ARGS", "unknown cmd '" + cmd + "'; expected doctor|status|open|info|read|section|undo|hint|finish|entity|query|snapshot|diff", null, null);
                }
                try { Commands.ReapplyCaption(ctx); } catch (Exception) { }
                ok = true;
            }
            catch (ZwcError e) { zerr = e; resultOut = ctx.Result; }
            catch (Exception e) { uerr = e; resultOut = ctx.Result; }

            JMap root = new JMap();
            root.Add("ok", ok);
            root.Add("cmd", cmd);
            root.Add("doc", ctx.DocMap);
            root.Add("result", resultOut);
            root.Add("warnings", ctx.Warnings);
            root.Add("undoToken", ctx.UndoToken);
            root.Add("elapsedMs", sw.ElapsedMilliseconds);
            root.Add("app", ctx.AppMap);
            if (!ok)
            {
                JMap err = new JMap();
                if (zerr != null)
                {
                    err.Add("code", zerr.Code);
                    err.Add("message", zerr.Message);
                    err.Add("hint", zerr.Hint);
                    err.Add("detail", zerr.Detail);
                }
                else
                {
                    err.Add("code", "UNEXPECTED");
                    err.Add("message", uerr == null ? "unknown failure" : (uerr.GetType().Name + ": " + uerr.Message));
                    err.Add("hint", "this is a bug in the helper; re-run doctor and report the message");
                    err.Add("detail", uerr == null ? null : uerr.ToString());
                }
                root.Add("error", err);
            }
            return Json.Write(root);
        }

        [STAThread]
        internal static int Main(string[] args)
        {
            MessageFilter.Register();
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception) { }

            string reqPath = null, respPath = null, servePipe = null;
            int idleMs = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--request" || a == "-request") && i + 1 < args.Length) reqPath = args[++i];
                else if ((a == "--response" || a == "-response") && i + 1 < args.Length) respPath = args[++i];
                else if ((a == "--serve" || a == "-serve") && i + 1 < args.Length) servePipe = args[++i];
                else if (a == "--idle-ms" && i + 1 < args.Length) int.TryParse(args[++i], out idleMs);
            }

            if (!string.IsNullOrEmpty(servePipe))
                return Daemon.Run(servePipe, idleMs);

            Stopwatch sw = Stopwatch.StartNew();
            string text;
            try
            {
                if (reqPath == null || respPath == null)
                    throw new ZwcError("BAD_ARGS", "usage: ZwcHelper.exe --request <req.json> --response <resp.json> | --serve <pipe>", null, null);
                if (!File.Exists(reqPath))
                    throw new ZwcError("BAD_ARGS", "request file not found: " + reqPath, null, null);
                text = HandleRequestText(File.ReadAllText(reqPath, new UTF8Encoding(false)));
            }
            catch (ZwcError e)
            {
                JMap err = new JMap();
                err.Add("ok", false);
                err.Add("cmd", "");
                err.Add("error", new JMap().Add("code", e.Code).Add("message", e.Message).Add("hint", e.Hint));
                err.Add("elapsedMs", sw.ElapsedMilliseconds);
                text = Json.Write(err);
            }

            try { File.WriteAllText(respPath, text, new UTF8Encoding(false)); }
            catch (Exception e)
            {
                try { Console.Error.WriteLine("cannot write response file: " + e.Message); }
                catch (Exception) { }
                return 2;
            }
            try { Console.Out.Write(text); }
            catch (Exception) { }
            return text.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? 0 : 2;
        }
    }
}
