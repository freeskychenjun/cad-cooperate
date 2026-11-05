// Com.cs - COM late-binding helpers (Com) and COM collection indexing (Coll).
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). Com wraps
// InvokeMember with the retry policy and the error/hint text every command relies on;
// Coll detects each collection's lower bound instead of assuming 0- or 1-based (getting it
// wrong silently drops the first entity of every read). Nothing was edited.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZwcHelper
{
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
}
