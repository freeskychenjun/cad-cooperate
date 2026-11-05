// Cad.cs - CAD application attachment, document resolution and product detection.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). Contains
// Cad plus its nested product helpers ProdInfo and ProgChoice. Cad attaches to the running
// ZWCAD/AutoCAD through late binding, resolves which drawing a request targets and never
// saves anything. No logic, visibility, field order or comment text was changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZwcHelper
{
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
                // Every installed AutoCAD release, discovered from the registry (ProductRegistry).
                // Measured 2026-11-05 with 2007 + 2021 installed: the hardcoded ".17 only" list made
                // `-App autocad` answer NO_INSTANCE while AutoCAD 2021 was running, and every response
                // labelled that instance "AutoCAD 2007".
                List<RegProduct> installed = ProductRegistry.AutoCadProducts();
                for (int i = 0; i < installed.Count; i++)
                {
                    RegProduct p = installed[i];
                    l.Add(new ProdInfo { Label = p.Label, ProgId = p.ProgId, Clsid = p.Clsid });
                }
                // documented fallback: the one build this project was first verified against
                if (l.Count == 0)
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
            // any other AutoCAD build: ask the registry (CLSID -> LocalServer32 -> folder name)
            string dynamicLabel = ProductRegistry.LabelFromClsid(c);
            if (!string.IsNullOrEmpty(dynamicLabel)) return dynamicLabel;
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
            {
                if (b.IndexOf("acad.exe", StringComparison.Ordinal) < 0 && b.IndexOf("autocad", StringComparison.Ordinal) < 0) return null;
                // label from the executable's folder ("...\AutoCAD 2021\acad.exe"), never a hardcoded
                // release: ProductRegistry stays the single place that maps a build to a name.
                string label = ProductRegistry.LabelFromExe(exePath);
                if (!string.IsNullOrEmpty(label)) return label;
                label = ProductRegistry.LabelFromExe(title);
                return string.IsNullOrEmpty(label) ? "AutoCAD" : label;
            }
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
            if (blob.IndexOf("acad.exe", StringComparison.Ordinal) >= 0)
            {
                // "...\AutoCAD 2021\acad.exe" -> "AutoCAD 2021"; never a hardcoded release name
                string label = ProductRegistry.LabelFromExe(full);
                if (!string.IsNullOrEmpty(label)) return label;
                string acadVer = Com.Str(Com.TryGet(app, "Version"));
                return string.IsNullOrEmpty(acadVer) ? "AutoCAD" : "AutoCAD " + acadVer;
            }
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
}
