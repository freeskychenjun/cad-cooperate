// LispScanStubs.cs - test-only stand-ins for the ZwcHelper types LispScan.cs touches at compile time.
//
// Compiled ONLY by tests\selftest_lisp.ps1 together with LispScanTests.cs (never into
// bin\cache\<sha>\ZwcHelper.exe, which compiles scripts\helper\*.cs and therefore the real types).
//
// The stubs are deliberately small but not inert: they simulate the CAD side of the pipeline so that
// tests\lispwire\LispScanTests.cs can drive LispScan.Run end to end offline -
//   * Com answers GetVariable("CMDACTIVE"/"LASTPROMPT"/"CMDECHO") from settable fields and lets a
//     test install an OnSendCommand hook that plays the generated LISP script (writes __START__,
//     __DONE__|<n> and the dump file),
//   * Commands records the CMDECHO restore so the finally-block contract can be asserted,
//   * JMap mirrors the real ordered-key/overwrite semantics, so AddMeta can be asserted key-for-key,
//   * Ctx is an empty type (Run ignores it, exactly like the pre-refactor Scans did).

using System;
using System.Collections.Generic;

namespace ZwcHelper
{
    internal sealed class Ctx
    {
    }

    internal static class Com
    {
        public static int CmdActive = 0;
        public static string LastPrompt = "";
        public static int CmdEcho = 1;
        public static readonly List<string> Sent = new List<string>();
        public static Action<string> OnSendCommand;

        public static object Call(object o, string name, params object[] a)
        {
            if (name == "GetVariable" && a != null && a.Length >= 1)
            {
                string v = a[0] as string;
                if (v == "CMDACTIVE") return CmdActive;
                if (v == "LASTPROMPT") return LastPrompt;
                if (v == "CMDECHO") return CmdEcho;
                return null;
            }
            if (name == "SendCommand" && a != null && a.Length >= 1)
            {
                string cmd = a[0] as string;
                Sent.Add(cmd);
                if (OnSendCommand != null) OnSendCommand(cmd);
                return null;
            }
            return null;
        }

        public static void Reset()
        {
            CmdActive = 0;
            LastPrompt = "";
            CmdEcho = 1;
            Sent.Clear();
            OnSendCommand = null;
        }

        public static string Str(object v) { return v as string; }

        public static int I32(object v)
        {
            if (v is int) return (int)v;
            return 0;
        }
    }

    internal static class Commands
    {
        public static int RestoreCalls = 0;
        public static int RestoredTo = -1;

        internal static int CmdEchoOf(object doc) { return Com.CmdEcho; }

        internal static void RestoreCmdEcho(object doc, int oldValue)
        {
            RestoreCalls++;
            RestoredTo = oldValue;
        }

        public static void Reset()
        {
            RestoreCalls = 0;
            RestoredTo = -1;
        }
    }

    /// <summary>Ordered-key map with the real JMap's semantics (case-insensitive keys, Add on an
    /// existing key replaces the value and keeps the original position).</summary>
    internal sealed class JMap
    {
        private readonly List<string> _keys = new List<string>();
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
            return Convert.ToString(v);
        }

        public int Int(string key, int dflt)
        {
            object v = Get(key);
            if (v == null) return dflt;
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            return dflt;
        }
    }
}
