// Commands.cs - request command implementations: shared argument helpers plus the
// session / diagnostic family (doctor, status, open, info, caption, cmdecho, finish).
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). This is
// one half of a partial class split: the read/section/plan/write/verify/rollback half
// lives in CommandsRead.cs. Member bodies are byte-identical, member order is preserved;
// only this class declaration line gained "partial".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ZwcHelper
{
    // ======================================================================================
    // Section model / plan / writer / verifier  (DESIGN-M1.md section 4)
    // ======================================================================================
    internal static partial class Commands
    {
        private static string Kind(JMap req, JMap args)
        {
            string k = args.Str("app", null);
            if (string.IsNullOrEmpty(k)) k = req.Str("app", null);
            if (string.IsNullOrEmpty(k)) k = "any";   // -App any: resolve from the running instances
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
            bool anyKind = Cad.IsAnyKind(kind);
            string anyError = null;
            JMap anyDetail = null;
            if (anyKind)
            {
                // -App any: doctor must be able to explain BOTH families. Pick the unambiguous one for
                // the capability probes; when it is ambiguous report the candidate list instead of
                // guessing (the caller sees instanceError + detectionAll).
                string code;
                string resolved = Cad.ResolveAnyKind(ctx, out anyDetail, out code);
                if (resolved == null) { anyError = code; kind = "zwcad"; }
                else kind = resolved;
            }
            Cad.ProgChoice pc = Cad.ResolveProgIds(kind, explicitProgId, ctx);

            JMap r = new JMap();
            r.Add("tempDir", State.Dir());
            r.Add("stateFile", State.FilePath());
            if (anyKind)
            {
                r.Add("appRequested", "any");
                r.Add("appResolved", anyError == null ? kind : null);
                r.Add("appAnyDetail", anyDetail);
            }

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

            if (anyError != null)
            {
                r.Add("instanceFound", false);
                r.Add("instanceError", anyError);
                ctx.AppMap = null;
                ctx.Result = r;
                throw new ZwcError(anyError,
                    anyError == "AMBIGUOUS_PRODUCT"
                        ? "both a ZWCAD and an AutoCAD instance are running, so -App any cannot tell which one is meant"
                        : "no ZWCAD and no AutoCAD instance is running",
                    "pass -App zwcad|autocad (plus -ProgId when several versions of that product run); see appAnyDetail for the candidate list",
                    Json.Write(anyDetail));
            }

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

            // AutoCAD 2014+ gates LISP file loading through SECURELOAD/TRUSTEDPATHS. Report it here so
            // "why is the fast path missing / why did a security dialog appear" is answerable without
            // guessing (see Trust.cs for the behaviour measured on AutoCAD 2021).
            if (doc != null)
            {
                try
                {
                    LispTrust trust = LispTrust.Read(doc, IsAutoCad(ctx), ctx);
                    r.Add("lispTrust", trust.ToMap());
                }
                catch (Exception eTrust) { ctx.Warn("TRUST_READ_FAILED", "lisp trust probe failed: " + Com.Hr(eTrust)); }
            }

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
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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

            // -App any: when both families run, the drawing that is ALREADY OPEN decides - that is the
            // whole point of "open this document" (no second copy in the wrong CAD, no asking the user).
            Cad cad = Cad.AttachFor(req, args, ctx, docArg);
            kind = ctx.Kind;
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
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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

        [ThreadStatic]
        private static List<object> expectedSeries;

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
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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

        // ------------------------------------------------------------------ trust
        /// <summary>True when the attached product is AutoCAD (case-insensitive, works with the
        /// dynamically discovered labels like "AutoCAD 2021").</summary>
        internal static bool IsAutoCad(Ctx ctx)
        {
            string p = ctx.AppMap == null ? "" : ctx.AppMap.Str("product", "");
            return p.IndexOf("autocad", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>`trust`: report and (optionally) fix the AutoCAD SECURELOAD gate that makes the
        /// LISP fast path pop the "Security - Unsigned Executable File" dialog for %TEMP% scripts.
        /// -Action status (default) | addTemp | removeTemp. Only an explicit action changes the
        /// user's TRUSTEDPATHS - a read/query never does.</summary>
        public static JMap TrustCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(DocArg(req, args), true, ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            string action = (args.Str("action", "status") ?? "status").Trim().ToLowerInvariant();
            LispTrust before = LispTrust.Read(doc, IsAutoCad(ctx), ctx);
            JMap r = new JMap();
            r.Add("action", action);
            r.Add("matchedBy", matchedBy);

            if (action == "addtemp" || action == "removetemp")
            {
                string want = action == "addtemp"
                    ? LispTrust.AddPath(before.TrustedPaths, before.TempDir)
                    : LispTrust.RemovePath(before.TrustedPaths, before.TempDir);
                bool changed = !string.Equals(want, before.TrustedPaths, StringComparison.Ordinal);
                if (changed)
                {
                    try { Com.Call(doc, "SetVariable", "TRUSTEDPATHS", want); }
                    catch (Exception e)
                    {
                        throw new ZwcError("TRUST_WRITE_FAILED",
                            "Document.SetVariable(\"TRUSTEDPATHS\") failed: " + Com.Hr(e),
                            "add the temp dir by hand: Options > Files > Trusted Locations", null);
                    }
                }
                LispTrust after = LispTrust.Read(doc, IsAutoCad(ctx), ctx);
                r.Add("changed", changed);
                r.Add("trustedPathsBefore", before.TrustedPaths);
                r.Add("trustedPathsAfter", after.TrustedPaths);
                r.Add("trust", after.ToMap());
                if (action == "addtemp" && !after.TempTrusted)
                    ctx.Warn("TRUST_NOT_APPLIED", "TRUSTEDPATHS was written but the temp dir still does not read back as trusted; restarting AutoCAD may be required");
            }
            else if (action == "status")
            {
                r.Add("trust", before.ToMap());
            }
            else
            {
                throw new ZwcError("BAD_ARGS", "unknown trust action '" + action + "'",
                    "use -Action status|addTemp|removeTemp", null);
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
            try { cad = Cad.AttachFor(req, args, ctx, DocArg(req, args)); }
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
            // counting must follow the family that was ACTUALLY attached ( -App any may have resolved
            int timeoutMs = args.Int("timeoutSec", 25) * 1000;
            Stopwatch sw = Stopwatch.StartNew();
            string kindForProcs = string.IsNullOrEmpty(ctx.Kind) ? Kind(req, args) : ctx.Kind;
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
}
