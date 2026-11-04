// JMapStub.cs - test-only stand-in for the JMap ordered map that scripts\helper\Json.cs walks.
//
// The real JMap lives in ZwcHelper.cs, which drags in OLE/COM and System.Management and therefore
// cannot be compiled offline together with Json.cs (same situation as tests\lispwire\ZwcErrorStub.cs).
// Json.cs only needs JMap's public surface, and the class body below is copied verbatim from
// ZwcHelper.cs so the ordered/case-insensitive semantics the writer and parser rely on are identical.
//
// Compiled ONLY by tests\selftest_lisp.ps1 (JsonTests suite). It is never part of
// scripts\helper\*.cs, so bin\cache\<sha>\ZwcHelper.exe keeps using the real JMap.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZwcHelper
{
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
}
