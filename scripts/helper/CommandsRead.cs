// CommandsRead.cs - read family of the Commands partial class: ReadState, Read,
// LispBridgeAvailable, KindOf, DescribeEntity, TryBBoxFromEntity, LispDumpHandles.
//
// Split out of the former 1,382-line CommandsRead.cs (plan 5.1 batch 3, 2026-11-05;
// pure move). The section / write half now lives in CommandsSection.cs and the
// verification half in CommandsVerify.cs; all three are the same partial class, so
// member bodies are byte-identical and no call site changed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
            ctx.ForceLispInject = args.Bool("forceLispInject", false);   // see Trust.cs
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
            bool lispPathUsed = false;   // TRUE once the LISP dump produced the entity list: the COM
                                         // sweep must not run on top of it (that doubled every geom
                                         // read: 8,511 entities came back as 17,022 records)
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
                    lispPathUsed = true;
                    ctx.LispMeta = lispMeta;
                }
                catch (ZwcError ze)
                {
                    // Two very different reasons to fall back: a real failure, and the AutoCAD
                    // SECURELOAD gate refusing to load the .lsp at all (see Trust.cs).
                    if (ze.Code == "LISP_TRUST_BLOCKED")
                    {
                        if (!ctx.TrustReported)
                        {
                            ctx.TrustReported = true;
                            ctx.Warn("LISP_TRUST_BLOCKED", "AutoCAD SECURELOAD would prompt for the LISP fast path ("
                                + LispTrust.Read(doc, Commands.IsAutoCad(ctx), ctx).FastPathHint() + "); this read used the slower COM sweep instead");
                        }
                    }
                    else
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
            else if (!lispPathUsed)
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

        /// <summary>True when this polyline hands out flat (x,y,z) triples from the ActiveX
        /// Coordinates property (AcDb2dPolyline / AcDb3dPolyline / meshes) instead of (x,y) pairs
        /// (AcDbPolyline, i.e. LWPOLYLINE). Measured on AutoCAD 2021.</summary>
        internal static bool IsTripleCoordinatePolyline(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            string t = objectName.ToLowerInvariant();
            if (t.EndsWith("acdb2dpolyline") || t.EndsWith("acdb3dpolyline")) return true;
            if (t.IndexOf("polygonmesh") >= 0 || t.IndexOf("polyfacemesh") >= 0) return true;
            return false;
        }

        /// <summary>Block attributes (ATTRIB sub-entities) as [{tag,value,x,y,z}]. Late-bound:
        /// AcadBlockReference.GetAttributes returns a SAFEARRAY of attribute references, which is what
        /// a real drawing needs because elevation labels often live only inside block attributes.</summary>
        internal static List<object> ReadAttributes(object e)
        {
            List<object> list = new List<object>();
            object raw = null;
            try { raw = Com.TryCall(e, "GetAttributes"); }
            catch (Exception) { return list; }
            if (raw == null) return list;
            System.Collections.IEnumerable seq = raw as System.Collections.IEnumerable;
            if (seq == null) return list;
            foreach (object item in seq)
            {
                if (item == null) continue;
                JMap m = new JMap();
                object tag = Com.TryGet(item, "TagString");
                object val = Com.TryGet(item, "TextString");
                m.Add("tag", tag == null ? "" : Com.Str(tag));
                m.Add("value", val == null ? "" : Com.Str(val));
                double[] pt = Com.Pt(Com.TryGet(item, "InsertionPoint"));
                if (pt != null) { m.Add("x", pt[0]); m.Add("y", pt[1]); m.Add("z", pt[2]); }
                list.Add(m);
            }
            return list;
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
                    // ActiveX quirk (measured on AutoCAD 2021, handle 7F7F6): AcadPolyline.Coordinates
                    // for AcDb2dPolyline/AcDb3dPolyline is a flat (x,y,z) TRIPLE array, while
                    // AcadLWPolyline.Coordinates is a flat (x,y) PAIR array. Stepping both by 2 turned
                    // every old-style contour polyline into garbage vertices AND a garbage bbox
                    // (a 16-vertex contour came back as [x,y],[0,x],[y,0],...).
                    int step = IsTripleCoordinatePolyline(type) ? 3 : 2;
                    object coordsObj = Com.TryGet(e, "Coordinates");
                    if (coordsObj == null) state.Fail("Coordinates");
                    double[] flat = Com.Flat(coordsObj);
                    List<object> verts = new List<object>();
                    for (int i = 0; i + 1 < flat.Length; i += step) verts.Add(new List<object> { flat[i], flat[i + 1] });
                    m.Add("vertices", verts);
                    m.Add("points", verts);
                    m.Add("nverts", verts.Count);
                    object closed = Com.TryGet(e, "Closed");
                    object elev = Com.TryGet(e, "Elevation");
                    if (elev != null) m.Add("elevation", Com.Dbl(elev));
                    m.Add("closed", closed == null ? null : (object)Com.Bool(closed));
                }
                else if (kind == "INSERT")
                {
                    double[] ins = Com.Pt(Com.TryGet(e, "InsertionPoint"));
                    if (ins != null)
                    {
                        m.Add("insertion", new List<object> { ins[0], ins[1] });
                        m.Add("pos", new List<object> { ins[0], ins[1] });
                    }
                    object bname = Com.TryGet(e, "Name");
                    if (bname != null) m.Add("blockName", Com.Str(bname));
                    // Block attributes are the only place some drawings keep their elevation labels
                    // (measured 2026-11-05: 8,123 elevation-point blocks, zero stand-alone TEXT).
                    List<object> atts = ReadAttributes(e);
                    if (atts.Count > 0) m.Add("attributes", atts);
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
                    // A missing bbox is cosmetic in geom mode (the vertices/elevation are the geometry)
                    // and block references routinely refuse GetBoundingBox through late binding, so
                    // counting those as failures degraded real reads to handles-only (measured on
                    // AutoCAD 2021: 8,123 elevation blocks -> DEGRADED_TO_HANDLES after 40 entities).
                    bool expectedUnsupported = (kind == "HATCH" || kind == "UNKNOWN" || kind == "INSERT"
                        || mode == "geom"
                        || kind.StartsWith("ACDB", StringComparison.OrdinalIgnoreCase));
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
                // same ActiveX quirk as the vertex reader: AcDb2d/3dPolyline hands out (x,y,z) triples
                string objectName = Com.Str(Com.TryGet(e, "ObjectName"));
                int step = IsTripleCoordinatePolyline(objectName) ? 3 : 2;
                double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
                for (int i = 0; i + 1 < flat.Length; i += step)
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
    }
}
