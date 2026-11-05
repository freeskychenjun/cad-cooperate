// State.cs - session state persisted across helper processes
// (%TEMP%\cad-cooperate\state.json: which instance we launched, caption originals, undo
// tokens; the only cross-process memory the helper has).
//
// Moved verbatim out of CadHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). No logic,
// no visibility, no field order and no message text was changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CadHelper
{
    // ======================================================================================
    // Session state (%TEMP%\cad-cooperate\state.json): which instance we launched, caption
    // originals, undo tokens. M1 has no daemon, so this file is the only cross-process memory.
    // ======================================================================================
    internal static class State
    {
        public static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "cad-cooperate");
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
}
