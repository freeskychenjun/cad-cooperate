// Core.cs - core data model of the helper: ordered JSON map, error type, request context.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05: pure move of the
// Core / ComInterop / Com / Procs / Cad / State / Cmd / Daemon / Commands units). Class
// and member bodies are unchanged: no logic, no visibility, no field order and no comment
// text was edited. JMap carries every command result, ZwcError is the failure type.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZwcHelper
{
    // ======================================================================================
    // Ordered JSON map. The JSON writer/parser itself lives in Json.cs (moved out 2026-11-05);
    // JMap stays here because every command result and every request is built out of it.
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
        public string Kind;           // product family actually resolved for this request ("zwcad"|"autocad")
        public bool FpChanged;   // set when the drawing changed outside this session before a write
        public bool PlanOnly;       // dry-run/plan: resolutions may fall back to any open drawing (no writes)
        public JMap LispMeta;         // M3-E5: timing/size of the LISP file scan when used
        public bool ForceLispInject;  // -ForceLispInject: inject even when SECURELOAD says it may prompt
        public bool TrustReported;    // LISP_TRUST_BLOCKED was already explained for this request
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
}
