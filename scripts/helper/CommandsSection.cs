// CommandsSection.cs - section / write family of the Commands partial class: SectionCmd
// and the drawing helpers it consumes (plan meta, roles, counts, entity creation, layers,
// text style, linetype, rollback) plus WaitIdle and UndoCmd.
//
// Split out of the former 1,382-line CommandsRead.cs (plan 5.1 batch 3, 2026-11-05;
// pure move). Member bodies are byte-identical; no call site changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace CadHelper
{
    internal static partial class Commands
    {
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

            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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

            JMap dashInfo = EnsureLinetype(doc, "DASHED", string.IsNullOrEmpty(ctx.Kind) ? Kind(req, args) : ctx.Kind, ctx);
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
                throw new CadError("BUSY", "CAD is not idle (CMDACTIVE=" + Com.I32(cmdActiveProbe)
                    + ", LASTPROMPT=\"" + promptProbe + "\"); refusing to draw",
                    "finish the pending command / close the dialog in CAD, then retry", null);

            int countBefore = CountOf(doc, "ModelSpace");
            if (countBefore < 0) throw new CadError("NO_MODELSPACE", "cannot read ModelSpace.Count", null, null);

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
                        throw new CadError("CREATE_FAILED", "CAD returned no object for " + spec.Kind + " on layer " + spec.Layer, null, null);
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
                    if (string.IsNullOrEmpty(h)) throw new CadError("HANDLE_MISSING", "entity created but its Handle property is empty", null, null);
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
                throw new CadError("VERIFY_FAILED",
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
                throw new CadError("CREATE_FAILED", "Add* failed for " + spec.Kind + ": " + Com.Hr(e), null, null);
            }
            throw new CadError("CREATE_FAILED", "unknown spec kind " + spec.Kind, null, null);
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
                        throw new CadError("LAYER_FAILED", "cannot create layer " + name + ": " + Com.Hr(e), null, null);
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

        // ------------------------------------------------------------------ undo
        public static JMap UndoCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.AttachFor(req, args, ctx, DocArg(req, args));
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
    }
}
