// CommandsRead.cs - request command implementations: the read / section / plan / write /
// verify / rollback family of the Commands partial class.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). This is
// the second half of the partial class split with Commands.cs (shared helpers + session /
// diagnostic family). Member bodies are byte-identical, member order is preserved; only
// this class declaration line gained "partial".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ZwcHelper
{
    internal static partial class Commands
    {
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
    }
}
