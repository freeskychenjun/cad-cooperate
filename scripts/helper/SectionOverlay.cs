// SectionOverlay.cs - A-2 additions to the `section` command (zoecad-cooperate M1 helper).
//
// Two jobs:
//   1. multi-series overlay: model v2 carries series[] (one ground line per survey date) instead
//      of the legacy top-level points[]; the legacy shape keeps working unchanged;
//   2. readability: legend for the overlay, adaptive label density for dense real sections
//      (40-111 points), optional scale bar.
//
// Everything here is pure geometry/planning - no COM. Commands.SectionCmd creates exactly the
// entities this plan asks for and verifies them; that split keeps the drawing code tiny.
//
// Model v2 contract (frozen with the data side, tmp\real\SCHEMA-v2.md):
//   { schema:"zwcad-section-model/2", params{...},
//     series:[ { id, label, date, station, points:[[off,elev],...], pointCount,
//                waterLevels:[{value,label}], style:{color,linetype}, color, linetype } ] }
//   * points are in DB vert_num order and are NOT re-sorted by offset (a section may run right to
//     left, and real DB fixes sometimes leave a fold) - we only reverse a strictly descending
//     series so it draws left-to-right.
//   * there is no top-level points[] in v2; a legacy v1 model still has one, which is why the
//     loader accepts both.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CadHelper
{
    internal sealed class SecWater
    {
        public double Value;
        public string Label;
    }

    internal sealed class SecSeries
    {
        public string Id;
        public string Label;
        public string Date;
        public string Station;
        public List<double[]> Points = new List<double[]>();
        public int Color;          // 0 = auto (palette by series index)
        public string Linetype;    // null/empty = CONTINUOUS
        public List<SecWater> Waters = new List<SecWater>();
    }

    internal sealed class SecModel
    {
        // legacy single-series view (kept populated so v1 models and old callers keep working)
        public List<double[]> Points = new List<double[]>();
        public List<string[]> Waters = new List<string[]>();
        public List<SecWater> SharedWaters = new List<SecWater>();   // model-level waterLevels
        public List<SecSeries> Series = new List<SecSeries>();
        public bool MultiSeries;

        public double HScale = 500;
        public double VScale = 50;
        public string Paper = "A3";
        public double OriginX = 0;
        public double OriginY = 0;
        public int LabelEvery = 1;
        public bool LabelEveryExplicit;
        public bool NoAxis = false;
        public bool NoPointNo = false;
        public bool NoAutoScale = false;
        public bool Axis = false;
        public bool NoLegend = false;
        public bool ScaleBar = false;
        public bool PointNo = false;
        public string LayerPrefix = "AI-SEC";
        public double TextHeight = 2.0;
        public double WaterTextHeight = 2.5;
        public string SourceFile = null;
        public JMap Raw = null;

        private static double D2(object o, double dflt)
        {
            if (o == null) return dflt;
            if (o is double) return (double)o;
            if (o is long) return (long)o;
            if (o is int) return (int)o;
            double d;
            if (double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return dflt;
        }

        private static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            string s = Com.Str(v);
            if (s == null) return false;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        private static void ApplyParam(SecModel m, string key, object v)
        {
            if (v == null) return;              // v2 sends labelEvery:null => keep "auto"
            string k = (key ?? "").Trim().ToLowerInvariant();
            switch (k)
            {
                case "hscale": m.HScale = D2(v, m.HScale); break;
                case "vscale": m.VScale = D2(v, m.VScale); break;
                case "paper": m.Paper = Com.Str(v); break;
                case "labelevery":
                    m.LabelEvery = Math.Max(1, (int)Math.Round(D2(v, m.LabelEvery)));
                    m.LabelEveryExplicit = true;
                    break;
                case "noaxis": m.NoAxis = Truthy(v); break;
                case "nopointno": m.NoPointNo = Truthy(v); break;
                case "pointno": m.PointNo = Truthy(v); break;
                case "noautoscale": m.NoAutoScale = Truthy(v); break;
                case "axis": m.Axis = Truthy(v); break;
                case "nolegend": m.NoLegend = Truthy(v); break;
                case "scalebar": m.ScaleBar = Truthy(v); break;
                case "layerprefix": m.LayerPrefix = Com.Str(v); break;
                case "textheight": m.TextHeight = D2(v, m.TextHeight); break;
                case "watertextheight": m.WaterTextHeight = D2(v, m.WaterTextHeight); break;
                case "origin":
                    {
                        List<object> l = v as List<object>;
                        if (l != null && l.Count >= 2) { m.OriginX = D2(l[0], 0); m.OriginY = D2(l[1], 0); }
                        else
                        {
                            string s = Com.Str(v);
                            if (s != null)
                            {
                                string[] parts = s.Split(',');
                                if (parts.Length >= 2) { m.OriginX = D2(parts[0], 0); m.OriginY = D2(parts[1], 0); }
                            }
                        }
                        break;
                    }
            }
        }

        private static double[] ParsePoint(object item)
        {
            List<object> pair = item as List<object>;
            if (pair != null)
            {
                if (pair.Count >= 2) return new double[] { D2(pair[0], double.NaN), D2(pair[1], double.NaN) };
                return null;
            }
            JMap pm = item as JMap;
            if (pm != null)
            {
                double off = pm.Has("offset") ? D2(pm.Get("offset"), double.NaN)
                           : pm.Has("offsetM") ? D2(pm.Get("offsetM"), double.NaN)
                           : pm.Has("x") ? D2(pm.Get("x"), double.NaN) : double.NaN;
                double el = pm.Has("elev") ? D2(pm.Get("elev"), double.NaN)
                          : pm.Has("elevM") ? D2(pm.Get("elevM"), double.NaN)
                          : pm.Has("y") ? D2(pm.Get("y"), double.NaN) : double.NaN;
                return new double[] { off, el };
            }
            return null;
        }

        private static List<double[]> ParsePoints(object ptsObj)
        {
            List<double[]> outp = new List<double[]>();
            List<object> pts = ptsObj as List<object>;
            if (pts == null) return outp;
            foreach (object item in pts)
            {
                double[] pt = ParsePoint(item);
                if (pt != null) outp.Add(pt);
            }
            return outp;
        }

        private static List<SecWater> ParseWaters(object watObj)
        {
            List<SecWater> outp = new List<SecWater>();
            List<object> wats = watObj as List<object>;
            if (wats == null) return outp;
            foreach (object item in wats)
            {
                double val = double.NaN;
                string label = "";
                JMap wm = item as JMap;
                if (wm != null)
                {
                    val = wm.Has("value") ? D2(wm.Get("value"), double.NaN)
                        : wm.Has("elev") ? D2(wm.Get("elev"), double.NaN)
                        : D2(wm.Get("level"), double.NaN);
                    label = wm.Str("label", "");
                    if (string.IsNullOrEmpty(label)) label = wm.Str("name", "");
                }
                else
                {
                    string s = Com.Str(item);
                    if (s != null)
                    {
                        int eq = s.IndexOf('=');
                        if (eq >= 0) { val = D2(s.Substring(0, eq), double.NaN); label = s.Substring(eq + 1).Trim(); }
                        else val = D2(s, double.NaN);
                    }
                    else val = D2(item, double.NaN);
                }
                if (double.IsNaN(val)) continue;
                SecWater w = new SecWater();
                w.Value = val;
                w.Label = label ?? "";
                outp.Add(w);
            }
            return outp;
        }

        public static SecModel Load(string path, JMap args, Ctx ctx)
        {
            if (string.IsNullOrEmpty(path))
                throw new CadError("BAD_ARGS", "section needs -Data <section.model.json>",
                    "run scripts\\section\\prepare.py first (it turns the xlsx into the model JSON)", null);
            if (!File.Exists(path))
                throw new CadError("BAD_ARGS", "section model file not found: " + path, null, null);

            object parsed;
            try { parsed = Json.ParseFile(path); }
            catch (Exception e)
            {
                throw new CadError("BAD_DATA", "section model file is not valid JSON: " + e.Message, null, path);
            }
            JMap root = parsed as JMap;
            if (root == null) throw new CadError("BAD_DATA", "section model root must be a JSON object", null, null);
            if (!root.Bool("ok", true))
                throw new CadError("BAD_DATA", "the section model reports ok:false (" + root.Str("error", "no detail") + ")",
                    "fix the data side first (prepare.py output)", null);

            SecModel m = new SecModel();
            m.Raw = root;
            m.SourceFile = root.Str("source", null);

            // ---- legacy v1 top-level points / waterLevels (v2 has neither)
            object ptsObj = root.Get("points");
            if (ptsObj == null) ptsObj = root.Get("pointsDetail");
            m.Points = ParsePoints(ptsObj);

            List<SecWater> legacyWaters = ParseWaters(root.Get("waterLevels"));
            foreach (SecWater w in legacyWaters) m.SharedWaters.Add(w);
            foreach (SecWater w in legacyWaters)
                m.Waters.Add(new string[] { w.Value.ToString("R", CultureInfo.InvariantCulture), w.Label });

            // ---- series[] (v2). Also accepted from v1-style models that carry only points[].
            List<object> rawSeries = root.List("series");
            if (rawSeries != null && rawSeries.Count > 0)
            {
                m.MultiSeries = rawSeries.Count > 1;
                int idx = 0;
                foreach (object so in rawSeries)
                {
                    JMap sm = so as JMap;
                    if (sm == null) continue;
                    idx++;
                    SecSeries s = new SecSeries();
                    s.Id = sm.Str("id", null);
                    s.Label = sm.Str("label", null);
                    s.Date = sm.Str("date", null);
                    s.Station = sm.Str("station", null);
                    if (string.IsNullOrEmpty(s.Id)) s.Id = "s" + idx.ToString(CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(s.Label)) s.Label = string.IsNullOrEmpty(s.Date) ? s.Id : s.Date;
                    object sp = sm.Get("points");
                    if (sp == null) sp = sm.Get("pointsDetail");
                    List<double[]> sps = ParsePoints(sp);
                    if (sps.Count < 3)
                        throw new CadError("BAD_DATA", "series '" + s.Id + "' has fewer than 3 points (" + sps.Count + ")",
                            "every series needs at least 3 [offset,elev] points", null);
                    s.Points = sps;
                    JMap style = sm.Map("style");
                    if (style != null)
                    {
                        if (style.Has("color")) s.Color = (int)Math.Round(D2(style.Get("color"), 0));
                        if (style.Has("linetype")) s.Linetype = style.Str("linetype", null);
                    }
                    if (sm.Has("color")) s.Color = (int)Math.Round(D2(sm.Get("color"), 0));
                    if (sm.Has("linetype")) s.Linetype = sm.Str("linetype", null);
                    object sw = sm.Get("waterLevels");
                    if (sw == null) sw = sm.Get("waterlevels");
                    s.Waters = ParseWaters(sw);
                    m.Series.Add(s);
                }
                if (m.Series.Count == 0)
                    throw new CadError("BAD_DATA", "series[] is present but contains no usable series object", null, null);
                // keep the legacy view pointing at the primary series
                m.Points = m.Series[0].Points;
            }
            else
            {
                SecSeries s = new SecSeries();
                s.Id = "";
                s.Label = "";
                s.Points = m.Points;
                s.Waters = legacyWaters;
                m.Series.Add(s);
                m.MultiSeries = false;
            }

            // ---- params: nested map, then flat root keys, then CLI args (CLI wins)
            JMap p = root.Map("params");
            if (p != null) foreach (string k in p.Keys) ApplyParam(m, k, p.Get(k));
            foreach (string k in root.Keys)
            {
                if (k == "params" || k == "points" || k == "pointsDetail" || k == "series"
                    || k == "waterLevels" || k == "computed" || k == "warnings"
                    || k == "source" || k == "schema" || k == "ok" || k == "dataQuality") continue;
                ApplyParam(m, k, root.Get(k));
            }
            if (args != null) foreach (string k in args.Keys) ApplyParam(m, k, args.Get(k));

            List<object> mw = root.List("warnings");
            if (mw != null)
            {
                foreach (object w in mw)
                {
                    JMap wm = w as JMap;
                    if (wm != null) ctx.Warn(wm.Str("code", "MODEL_WARNING"), wm.Str("message", ""));
                }
            }
            return m;
        }
    }

    internal sealed class SecSpec
    {
        public string Kind;          // poly | line | text | circle
        public string Layer;
        public List<double[]> Verts; // poly
        public double[] P1, P2;      // line
        public double[] Center;      // circle
        public double Radius;        // circle
        public string Text;          // text
        public double[] Ins;         // text insertion point
        public double Height;        // text height
        public string Expect;        // Polyline | Line | Text | Circle
        public double[] Bmin, Bmax;  // expected bbox (null for text: font metrics unknown)
        public string Handle;
        public int Color;            // 0 = BYLAYER
        public string Linetype;      // null/empty = BYLAYER (series can carry its own dash)
        public string SeriesId;      // set on per-series entities (overlay mode)
        public string Role;          // ground | water-line | water-text | elev | pointno | axis | legend | ...
    }

    internal sealed class SecSeriesPlan
    {
        public string Id;
        public string Label;
        public int Color;
        public string Linetype;
        public string Layer;
        public List<double[]> Pts;
        public List<double[]> Verts;
        public SecSpec Ground;
        public string Handle;
    }

    internal sealed class SecWaterDraw
    {
        public double Value;
        public string Label;
        public int Color;
        public double Y;
    }

    internal sealed class SecPlan
    {
        public SecModel M;
        public List<SecSeriesPlan> Series = new List<SecSeriesPlan>();
        public SecSeriesPlan Primary;
        public List<double[]> Pts = new List<double[]>();     // primary series points (compat)
        public double OffMin, OffMax, ElevMin, ElevMax, ElevBase, W, H, HScale, VScale;
        public double OriginX, OriginY;
        public List<SecSpec> Specs = new List<SecSpec>();
        public int ExpectedTotal;
        public List<SecWaterDraw> Waters = new List<SecWaterDraw>();
        public double[] GroundBbox = new double[4];
        public double[] AllBbox = new double[4];
        public JMap LabelPolicy;
        public JMap Legend;
        public List<object> LayerList = new List<object>();

        public double X(double off) { return (off - OffMin) * 1000.0 / HScale + OriginX; }
        public double Y(double elev) { return (elev - ElevBase) * 1000.0 / VScale + OriginY; }

        public JMap ModelMap()
        {
            JMap m = new JMap();
            m.Add("schema", "zwcad-section-model/2");
            m.Add("multiSeries", Series.Count > 1);
            m.Add("seriesCount", Series.Count);
            List<object> sl = new List<object>();
            foreach (SecSeriesPlan sp in Series)
            {
                JMap sm = new JMap();
                sm.Add("id", sp.Id);
                sm.Add("label", sp.Label);
                sm.Add("points", sp.Pts.Count);
                sm.Add("color", sp.Color);
                sm.Add("linetype", string.IsNullOrEmpty(sp.Linetype) ? "CONTINUOUS" : sp.Linetype);
                sm.Add("layer", sp.Layer);
                sl.Add(sm);
            }
            m.Add("series", sl);
            m.Add("pointCount", Pts.Count);
            m.Add("points", Pts.Count);
            m.Add("hScale", HScale);
            m.Add("vScale", VScale);
            m.Add("paper", M.Paper);
            m.Add("origin", new List<object> { OriginX, OriginY });
            m.Add("offsetMin", OffMin);
            m.Add("offsetMax", OffMax);
            m.Add("elevMin", ElevMin);
            m.Add("elevMax", ElevMax);
            m.Add("elevBase", ElevBase);
            m.Add("widthMm", W);
            m.Add("heightMm", H - 10.0);
            m.Add("extentWidthMm", W);
            m.Add("extentHeightMm", H);
            JMap bb = new JMap();
            bb.Add("x0", GroundBbox[0]);
            bb.Add("y0", GroundBbox[1]);
            bb.Add("x1", GroundBbox[2]);
            bb.Add("y1", GroundBbox[3]);
            m.Add("bbox", bb);
            JMap ext = new JMap();
            ext.Add("min", new List<object> { GroundBbox[0], GroundBbox[1] });
            ext.Add("max", new List<object> { GroundBbox[2], GroundBbox[3] });
            m.Add("extents", ext);
            m.Add("waterLevels", Waters.Count);
            if (LabelPolicy != null) m.Add("labelPolicy", LabelPolicy);
            if (Legend != null) m.Add("legend", Legend);
            return m;
        }
    }

    internal static class Section
    {
        private static readonly int[] Palette = new int[] { 1, 3, 5, 2, 4, 6, 7 };

        private static void PaperSize(string paper, out double w, out double h)
        {
            string p = (paper == null ? "A3" : paper.Trim().ToUpperInvariant().Replace(" ", ""));
            if (p == "A4") { w = 297; h = 210; return; }
            if (p == "A2") { w = 594; h = 420; return; }
            if (p == "A1") { w = 841; h = 594; return; }
            if (p == "A0") { w = 1189; h = 841; return; }
            w = 420; h = 297;
        }

        public static string F2(double d) { return d.ToString("0.00", CultureInfo.InvariantCulture); }

        private static int AutoColor(int index) { return Palette[index % Palette.Length]; }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "s";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < id.Length && sb.Length < 24; i++)
            {
                char c = id[i];
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-' || c == '_') sb.Append(c);
                else sb.Append('-');
            }
            string s = sb.ToString().Trim('-');
            return s.Length == 0 ? "s" : s;
        }

        /// <summary>Validate one series and put its points in drawing order. v2 keeps DB order and
        /// only guarantees "may be descending"; a strictly descending series is reversed (so it
        /// draws left to right), anything else (a real fold, duplicate offsets) is drawn as given
        /// with a warning - refusing would throw away real survey data.</summary>
        private static List<double[]> Normalize(List<double[]> pts, string label, Ctx ctx)
        {
            if (pts.Count < 3)
                throw new CadError("BAD_DATA", "series '" + label + "' needs at least 3 points, got " + pts.Count,
                    "fix the source data", null);
            foreach (double[] pt in pts)
            {
                if (double.IsNaN(pt[0]) || double.IsNaN(pt[1]) || double.IsInfinity(pt[0]) || double.IsInfinity(pt[1]))
                    throw new CadError("BAD_DATA", "series '" + label + "' has an empty or non-numeric offset/elevation",
                        "fix the source data (header row included by mistake?)", null);
            }
            bool asc = true, desc = true;
            bool dup = false;
            for (int i = 1; i < pts.Count; i++)
            {
                if (!(pts[i][0] > pts[i - 1][0])) asc = false;
                if (!(pts[i][0] < pts[i - 1][0])) desc = false;
                if (Math.Abs(pts[i][0] - pts[i - 1][0]) < 1e-9) dup = true;
            }
            if (!asc && desc)
            {
                pts.Reverse();
                ctx.Warn("ORDER_REVERSED", "series '" + label + "': offsets were strictly decreasing; drawn left-to-right");
            }
            else if (!asc)
            {
                ctx.Warn("SERIES_DIST_NON_MONOTONIC", "series '" + label
                    + "': offsets are not monotonic in DB order; drawn exactly as given (section shape preserved)");
                if (dup) ctx.Warn("DUPLICATE_OFFSET", "series '" + label + "': duplicate offset values in DB order");
            }
            return pts;
        }

        public static SecPlan Build(SecModel m, Ctx ctx)
        {
            SecPlan p = new SecPlan();
            p.M = m;

            double offMin = double.MaxValue, offMax = double.MinValue;
            double elevMin = double.MaxValue, elevMax = double.MinValue;
            int si = 0;
            foreach (SecSeries s in m.Series)
            {
                si++;
                SecSeriesPlan sp = new SecSeriesPlan();
                sp.Id = string.IsNullOrEmpty(s.Id) ? "" : s.Id;
                sp.Label = string.IsNullOrEmpty(s.Label) ? (sp.Id.Length == 0 ? "" : sp.Id) : s.Label;
                sp.Pts = Normalize(new List<double[]>(s.Points), sp.Id.Length == 0 ? "(primary)" : sp.Id, ctx);
                sp.Color = s.Color > 0 ? s.Color : AutoColor(si - 1);
                sp.Linetype = s.Linetype;
                p.Series.Add(sp);
                foreach (double[] pt in sp.Pts)
                {
                    double off = pt[0], el = pt[1];
                    if (off < offMin) offMin = off;
                    if (off > offMax) offMax = off;
                    if (el < elevMin) elevMin = el;
                    if (el > elevMax) elevMax = el;
                }
            }
            p.Primary = p.Series[0];
            p.Pts = p.Primary.Pts;
            p.OffMin = offMin; p.OffMax = offMax; p.ElevMin = elevMin; p.ElevMax = elevMax;
            if (Math.Abs(elevMax - elevMin) < 1e-9)
                throw new CadError("BAD_DATA", "all elevations are identical (" + F2(elevMin) + " m) - that is not a cross section", null, null);

            p.VScale = m.VScale > 0 ? m.VScale : 50;
            p.OriginX = m.OriginX;
            p.OriginY = m.OriginY;
            p.ElevBase = p.ElevMin - 10.0 * p.VScale / 1000.0;

            double paperW, paperH;
            PaperSize(m.Paper, out paperW, out paperH);
            double usableW = paperW - 20.0;
            double usableH = paperH - 20.0;

            List<double> ladder = new List<double>();
            ladder.Add(m.HScale > 0 ? m.HScale : 500);
            if (!m.NoAutoScale)
            {
                double[] fixedStages = new double[] { 500, 1000, 2000 };
                foreach (double s in fixedStages) if (s > ladder[0] + 1e-9) ladder.Add(s);
            }
            double chosen = -1;
            double reqW = 0, reqH = 0;
            for (int i = 0; i < ladder.Count; i++)
            {
                p.HScale = ladder[i];
                p.W = (p.OffMax - p.OffMin) * 1000.0 / p.HScale;
                p.H = 10.0 + (p.ElevMax - p.ElevMin) * 1000.0 / p.VScale;
                if (i == 0) { reqW = p.W; reqH = p.H; }
                if (p.W <= usableW + 1e-9 && p.H <= usableH + 1e-9) { chosen = p.HScale; break; }
            }
            if (chosen < 0)
                throw new CadError("TOO_WIDE",
                    "the section does not fit the " + m.Paper + " usable area (" + usableW.ToString("0.#", CultureInfo.InvariantCulture)
                    + " x " + usableH.ToString("0.#", CultureInfo.InvariantCulture) + " mm) with hScale=" + m.HScale
                    + (m.NoAutoScale ? " and -NoAutoScale is set" : " nor any ladder step (500/1000/2000)"),
                    "pass a larger -HScale, a bigger -Paper, or drop -NoAutoScale", null);
            if (Math.Abs(chosen - (m.HScale > 0 ? m.HScale : 500)) > 1e-9)
                ctx.Warn("AUTO_SCALED", "hScale raised from " + (m.HScale > 0 ? m.HScale : 500) + " to " + chosen
                    + " (requested width " + reqW.ToString("0.##", CultureInfo.InvariantCulture) + " mm exceeded "
                    + usableW.ToString("0.#", CultureInfo.InvariantCulture) + " mm)");
            p.W = (p.OffMax - p.OffMin) * 1000.0 / p.HScale;
            p.H = 10.0 + (p.ElevMax - p.ElevMin) * 1000.0 / p.VScale;

            string prefix = string.IsNullOrEmpty(m.LayerPrefix) ? "AI-SEC" : m.LayerPrefix;
            string lWater = prefix + "-WATER";
            string lAnno = prefix + "-ANNO";
            string lAxis = prefix + "-AXIS";
            bool multi = p.Series.Count > 1;

            p.LayerList.Add(lAnno);
            p.LayerList.Add(lWater);
            p.LayerList.Add(lAxis);
            foreach (SecSeriesPlan sp in p.Series)
            {
                sp.Layer = multi ? (prefix + "-GROUND-" + SanitizeId(sp.Id)) : (prefix + "-GROUND");
                if (!p.LayerList.Contains(sp.Layer)) p.LayerList.Add(sp.Layer);
            }

            // ---- ground lines (one LWPOLYLINE per series, in series order)
            foreach (SecSeriesPlan sp in p.Series)
            {
                sp.Verts = new List<double[]>();
                foreach (double[] pt in sp.Pts) sp.Verts.Add(new double[] { p.X(pt[0]), p.Y(pt[1]) });
                SecSpec g = new SecSpec();
                g.Kind = "poly";
                g.Expect = "Polyline";
                g.Layer = sp.Layer;
                g.Verts = sp.Verts;
                g.SeriesId = multi ? sp.Id : null;
                g.Role = "ground";
                g.Color = multi ? sp.Color : 0;
                g.Linetype = sp.Linetype;
                double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
                foreach (double[] v in sp.Verts)
                {
                    if (v[0] < mnx) mnx = v[0];
                    if (v[0] > mxx) mxx = v[0];
                    if (v[1] < mny) mny = v[1];
                    if (v[1] > mxy) mxy = v[1];
                }
                g.Bmin = new double[] { mnx, mny };
                g.Bmax = new double[] { mxx, mxy };
                sp.Ground = g;
                p.Specs.Add(g);
            }
            p.GroundBbox = new double[] { 0.0 + p.OriginX, 10.0 + p.OriginY, p.W + p.OriginX, p.H + p.OriginY };

            double xMin = p.GroundBbox[0], yMin = p.GroundBbox[1], xMax = p.GroundBbox[2], yMax = p.GroundBbox[3];

            // ---- water levels: model level + per-series, deduped by elevation
            List<SecWaterDraw> waters = new List<SecWaterDraw>();
            List<SecWater> allW = new List<SecWater>();
            // model-level waterLevels are shared by all series; each series may add its own
            foreach (SecWater w in m.SharedWaters) allW.Add(w);
            foreach (SecSeries s in m.Series) foreach (SecWater w in s.Waters) allW.Add(w);
            foreach (SecWater w in allW)
            {
                bool dup = false;
                foreach (SecWaterDraw d in waters) if (Math.Abs(d.Value - w.Value) < 1e-6) { dup = true; break; }
                if (dup) continue;
                SecWaterDraw d2 = new SecWaterDraw();
                d2.Value = w.Value;
                d2.Label = w.Label;
                d2.Y = p.Y(w.Value);
                d2.Color = AutoColor(p.Series.Count + waters.Count);
                waters.Add(d2);
            }
            p.Waters = waters;
            bool waterMultiColor = waters.Count > 1;
            bool waterLabelWithName = waters.Count <= 3;
            foreach (SecWaterDraw w in waters)
            {
                if (w.Y < 0 || w.Y > p.H + 1e-9)
                    ctx.Warn("WATER_OUT_OF_RANGE", "water level " + F2(w.Value) + " m maps to y="
                        + w.Y.ToString("0.###", CultureInfo.InvariantCulture) + " mm, outside 0.."
                        + p.H.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                SecSpec wl = new SecSpec();
                wl.Kind = "line";
                wl.Expect = "Line";
                wl.Layer = lWater;
                wl.Role = "water-line";
                // NOTE (measured 2026-11-04): xMax is extended by 5 mm at the end of every iteration
                // below, so the 2nd water line starts where the 1st one ended and each subsequent
                // line/label sits 5 mm further right than the previous one. That cascade was NOT an
                // intentional "stagger" - it is a side effect - but it measurably helps keep the
                // water labels from sitting on the same vertical line, so it is kept and documented
                // instead of being changed under an already-accepted drawing. If a deterministic span
                // is wanted instead, hoist `double wspan = xMax + 5.0;` before this loop and use it.
                wl.P1 = new double[] { xMin - 5.0, w.Y };
                wl.P2 = new double[] { xMax + 5.0, w.Y };
                wl.Bmin = new double[] { wl.P1[0], w.Y };
                wl.Bmax = new double[] { wl.P2[0], w.Y };
                wl.Color = waterMultiColor ? w.Color : 0;
                p.Specs.Add(wl);

                string wtxt = waterLabelWithName
                    ? ((string.IsNullOrEmpty(w.Label) ? "\u6c34\u4f4d" : w.Label) + " " + F2(w.Value) + " m")
                    : F2(w.Value);
                SecSpec wt = TextSpec(wtxt, new double[] { wl.P2[0], w.Y + 2.0 }, m.WaterTextHeight, lAnno);
                wt.Role = "water-text";
                p.Specs.Add(wt);

                if (wl.Bmin[0] < xMin) xMin = wl.Bmin[0];
                if (wl.Bmax[0] > xMax) xMax = wl.Bmax[0];
                if (w.Y > yMax) yMax = w.Y;
            }

            // ---- adaptive label density (primary series only in overlay mode)
            List<double[]> pts = p.Primary.Pts;
            int n = pts.Count;
            double th = m.TextHeight;
            double labelW = Math.Max(th * 3.0, EstTextWidth("88.88", th) + 0.6);
            int every;
            string reason;
            if (m.LabelEveryExplicit)
            {
                every = Math.Max(1, m.LabelEvery);
                reason = "explicit -LabelEvery " + every.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                int fits = (int)Math.Floor(p.W / Math.Max(1e-6, labelW));
                if (fits < 2) fits = 2;
                every = (int)Math.Ceiling((double)n / (double)fits);
                if (every < 1) every = 1;
                reason = "auto: " + n.ToString(CultureInfo.InvariantCulture) + " points over "
                    + p.W.ToString("0.#", CultureInfo.InvariantCulture) + " mm, min label spacing "
                    + labelW.ToString("0.##", CultureInfo.InvariantCulture) + " mm => "
                    + fits.ToString(CultureInfo.InvariantCulture) + " labels fit => labelEvery="
                    + every.ToString(CultureInfo.InvariantCulture);
            }

            int thalweg = 0;
            for (int i = 1; i < n; i++) if (pts[i][1] < pts[thalweg][1]) thalweg = i;
            List<int> mandatory = new List<int>();
            addUnique(mandatory, 0);
            addUnique(mandatory, n - 1);
            addUnique(mandatory, thalweg);
            int leftTop = 0, rightTop = n - 1;
            for (int i = 0; i <= n / 2; i++) if (pts[i][1] > pts[leftTop][1]) leftTop = i;
            for (int i = n / 2; i < n; i++) if (pts[i][1] > pts[rightTop][1]) rightTop = i;
            addUnique(mandatory, leftTop);
            addUnique(mandatory, rightTop);
            foreach (SecWaterDraw w in waters)
            {
                for (int i = 1; i < n; i++)
                {
                    double a = pts[i - 1][1] - w.Value;
                    double b = pts[i][1] - w.Value;
                    if ((a <= 0 && b >= 0) || (a >= 0 && b <= 0))
                    {
                        addUnique(mandatory, i - 1);
                        addUnique(mandatory, i);
                    }
                }
            }
            mandatory.Sort();
            SortedSet<int> labelled = new SortedSet<int>();
            for (int i = 0; i < n; i += every) labelled.Add(i);
            labelled.Add(n - 1);
            foreach (int idx in mandatory) labelled.Add(idx);

            foreach (int i in labelled)
            {
                SecSpec t = TextSpec(F2(pts[i][1]), new double[] { p.X(pts[i][0]), p.Y(pts[i][1]) + 2.0 }, th, lAnno);
                t.Role = "elev";
                p.Specs.Add(t);
            }

            bool wantPointNo;
            if (multi) wantPointNo = m.PointNo && !m.NoPointNo;
            else wantPointNo = !m.NoPointNo;
            if (wantPointNo)
            {
                foreach (int i in labelled)
                {
                    SecSpec t = TextSpec((i + 1).ToString(CultureInfo.InvariantCulture),
                        new double[] { p.X(pts[i][0]), p.Y(pts[i][1]) - 3.0 }, th, lAnno);
                    t.Role = "pointno";
                    p.Specs.Add(t);
                }
            }

            List<object> mandatoryOut = new List<object>();
            foreach (int idx in mandatory) mandatoryOut.Add(idx);
            JMap lp = new JMap();
            lp.Add("labelEvery", every);
            lp.Add("reason", reason);
            lp.Add("mandatory", mandatoryOut);
            lp.Add("labelled", labelled.Count);
            lp.Add("pointCount", n);
            lp.Add("labelWidthMm", labelW);
            lp.Add("pointNumbers", wantPointNo);
            p.LabelPolicy = lp;

            // ---- axis
            if (!m.NoAxis)
            {
                p.Specs.Add(LineSpec(lAxis, "axis", new double[] { 0.0 + p.OriginX, 0.0 + p.OriginY },
                    new double[] { p.W + p.OriginX, 0.0 + p.OriginY }, ref xMin, ref yMin, ref xMax, ref yMax));
                p.Specs.Add(LineSpec(lAxis, "axis", new double[] { 0.0 + p.OriginX, 0.0 + p.OriginY },
                    new double[] { 0.0 + p.OriginX, 10.0 + p.OriginY }, ref xMin, ref yMin, ref xMax, ref yMax));
            }

            // ---- optional offset ticks every 50 m
            if (m.Axis)
            {
                double first = Math.Ceiling(p.OffMin / 50.0) * 50.0;
                for (double off = first; off <= p.OffMax + 1e-9; off += 50.0)
                {
                    double xt = p.X(off);
                    p.Specs.Add(LineSpec(lAxis, "tick", new double[] { xt, 0.0 + p.OriginY },
                        new double[] { xt, -2.0 + p.OriginY }, ref xMin, ref yMin, ref xMax, ref yMax));
                    SecSpec t = TextSpec(off.ToString("0", CultureInfo.InvariantCulture),
                        new double[] { xt, -5.0 + p.OriginY }, th, lAnno);
                    t.Role = "tick-text";
                    p.Specs.Add(t);
                }
            }

            // ---- optional scale bar
            if (m.ScaleBar)
            {
                double barY = -6.0 + p.OriginY;
                double spanM = p.OffMax - p.OffMin;
                double stepM = spanM >= 100 ? 50.0 : (spanM >= 40 ? 20.0 : 10.0);
                double first = Math.Ceiling(p.OffMin / stepM) * stepM;
                for (double off = first; off <= p.OffMax + 1e-9; off += stepM)
                {
                    double xt = p.X(off);
                    p.Specs.Add(LineSpec(lAxis, "scalebar", new double[] { xt, barY },
                        new double[] { xt, barY - 2.5 }, ref xMin, ref yMin, ref xMax, ref yMax));
                    SecSpec t = TextSpec(off.ToString("0", CultureInfo.InvariantCulture),
                        new double[] { xt, barY - 3.0 }, th * 0.9, lAnno);
                    t.Role = "scalebar-text";
                    p.Specs.Add(t);
                }
                p.Specs.Add(LineSpec(lAxis, "scalebar", new double[] { p.X(p.OffMin), barY },
                    new double[] { p.X(p.OffMax), barY }, ref xMin, ref yMin, ref xMax, ref yMax));
                string note = "H 1:" + ((int)Math.Round(p.HScale)).ToString(CultureInfo.InvariantCulture)
                    + "  V 1:" + ((int)Math.Round(p.VScale)).ToString(CultureInfo.InvariantCulture);
                SecSpec nt = TextSpec(note, new double[] { p.X(p.OffMin), barY - 8.0 }, th, lAnno);
                nt.Role = "scalebar-text";
                p.Specs.Add(nt);
            }

            // ---- thalweg marker (primary series)
            SecSpec circ = new SecSpec();
            circ.Kind = "circle";
            circ.Expect = "Circle";
            circ.Layer = lAnno;
            circ.Role = "thalweg";
            circ.Center = new double[] { p.X(pts[thalweg][0]), p.Y(pts[thalweg][1]) };
            circ.Radius = 0.6;
            circ.Bmin = new double[] { circ.Center[0] - 0.6, circ.Center[1] - 0.6 };
            circ.Bmax = new double[] { circ.Center[0] + 0.6, circ.Center[1] + 0.6 };
            p.Specs.Add(circ);
            if (circ.Bmin[0] < xMin) xMin = circ.Bmin[0];
            if (circ.Bmax[0] > xMax) xMax = circ.Bmax[0];
            if (circ.Bmax[1] > yMax) yMax = circ.Bmax[1];
            SecSpec tt = TextSpec("\u6cb3\u5e95 " + F2(pts[thalweg][1]) + " m",
                new double[] { circ.Center[0], circ.Center[1] - 7.0 }, th, lAnno);
            tt.Role = "thalweg-text";
            p.Specs.Add(tt);

            // ---- legend (overlay only): short series-coloured line + label; water labels when >3
            JMap legend = new JMap();
            List<object> rows = new List<object>();
            bool wantLegend = multi && !m.NoLegend;
            legend.Add("enabled", wantLegend);
            legend.Add("rows", rows);
            if (wantLegend)
            {
                List<string> rowTexts = new List<string>();
                List<int> rowColors = new List<int>();
                foreach (SecSeriesPlan sp in p.Series)
                {
                    rowTexts.Add(string.IsNullOrEmpty(sp.Label) ? sp.Id : sp.Label);
                    rowColors.Add(sp.Color);
                }
                if (!waterLabelWithName)
                {
                    foreach (SecWaterDraw w in waters)
                    {
                        rowTexts.Add((string.IsNullOrEmpty(w.Label) ? "\u6c34\u4f4d" : w.Label) + " " + F2(w.Value) + " m");
                        rowColors.Add(w.Color);
                    }
                }
                double rowH = Math.Max(th * 2.2, 4.0);
                double maxTextW = 0;
                foreach (string rt in rowTexts) maxTextW = Math.Max(maxTextW, EstTextWidth(rt, th));
                double legendW = 12.0 + 2.0 + maxTextW;
                double legendH = rowTexts.Count * rowH;
                double lx = xMax - legendW - 2.0;
                double lyTop = yMax - legendH - 2.0;
                double minX = p.X(p.OffMin) + 2.0;
                if (lx < minX) lx = minX;
                if (LegendCollides(p, lx, lyTop, legendW, legendH))
                {
                    // inside would cover the section: put the legend just above the drawing, right
                    // aligned. (Top-left was tried first but it sits on the elevation labels.)
                    lx = Math.Max(minX, xMax - legendW - 2.0);
                    lyTop = yMax + 4.0;
                    ctx.Warn("LEGEND_RELOCATED", "legend placed above the drawing to avoid the ground lines (anchor "
                        + lx.ToString("0.#", CultureInfo.InvariantCulture) + "," + lyTop.ToString("0.#", CultureInfo.InvariantCulture) + ")");
                }
                legend.Add("anchor", new List<object> { lx, lyTop });
                legend.Add("size", new List<object> { legendW, legendH });
                for (int i = 0; i < rowTexts.Count; i++)
                {
                    double rowY = lyTop + legendH - (i + 1) * rowH + rowH * 0.35;
                    SecSpec ln = LineSpec(lAnno, "legend", new double[] { lx, rowY },
                        new double[] { lx + 12.0, rowY }, ref xMin, ref yMin, ref xMax, ref yMax);
                    ln.Color = rowColors[i];
                    p.Specs.Add(ln);
                    SecSpec tx = TextSpec(rowTexts[i], new double[] { lx + 14.0, rowY - th * 0.35 }, th, lAnno);
                    tx.Role = "legend-text";
                    p.Specs.Add(tx);
                    JMap row = new JMap();
                    row.Add("text", rowTexts[i]);
                    row.Add("color", rowColors[i]);
                    row.Add("y", rowY);
                    rows.Add(row);
                }
            }
            else if (multi)
            {
                ctx.Warn("LEGEND_DISABLED", "overlay drawing without legend (-NoLegend): series are only separable by line colour");
            }
            p.Legend = legend;

            // keep the water-level labels clear of the legend box: right-align them to the left
            // edge of the legend when they would collide (they sit at the right end of the plot)
            if (wantLegend)
            {
                double lgx = 0, lgy = 0, lgw = 0, lgh = 0;
                List<object> anc = legend.List("anchor");
                List<object> siz = legend.List("size");
                if (anc != null && anc.Count >= 2) { lgx = Convert.ToDouble(anc[0], CultureInfo.InvariantCulture); lgy = Convert.ToDouble(anc[1], CultureInfo.InvariantCulture); }
                if (siz != null && siz.Count >= 2) { lgw = Convert.ToDouble(siz[0], CultureInfo.InvariantCulture); lgh = Convert.ToDouble(siz[1], CultureInfo.InvariantCulture); }
                foreach (SecSpec spec in p.Specs)
                {
                    if (spec.Role != "water-text") continue;
                    double tw = EstTextWidth(spec.Text, spec.Height);
                    bool inBand = spec.Ins[1] >= lgy - spec.Height && spec.Ins[1] <= lgy + lgh;
                    if (inBand && spec.Ins[0] + tw > lgx - 1.0)
                        spec.Ins[0] = Math.Max(p.X(p.OffMin) + 2.0, lgx - tw - 2.0);
                }
            }

            p.AllBbox = new double[] { xMin, yMin, xMax, yMax };
            p.ExpectedTotal = p.Specs.Count;
            return p;
        }

        private static void addUnique(List<int> list, int v)
        {
            if (v < 0) return;
            if (!list.Contains(v)) list.Add(v);
        }

        private static bool LegendCollides(SecPlan p, double x0, double y0, double w, double h)
        {
            double x1 = x0 + w, y1 = y0 + h;
            foreach (SecSeriesPlan sp in p.Series)
            {
                if (sp.Verts == null) continue;
                foreach (double[] v in sp.Verts)
                {
                    if (v[0] >= x0 - 1.0 && v[0] <= x1 + 1.0 && v[1] >= y0 - 1.0 && v[1] <= y1 + 1.0) return true;
                }
            }
            return false;
        }

        private static SecSpec TextSpec(string text, double[] ins, double height, string layer)
        {
            SecSpec s = new SecSpec();
            s.Kind = "text";
            s.Expect = "Text";
            s.Layer = layer;
            s.Text = text;
            s.Ins = new double[] { ins[0], ins[1] };
            s.Height = height;
            return s;
        }

        private static SecSpec LineSpec(string layer, string role, double[] p1, double[] p2,
            ref double xMin, ref double yMin, ref double xMax, ref double yMax)
        {
            SecSpec s = new SecSpec();
            s.Kind = "line";
            s.Expect = "Line";
            s.Layer = layer;
            s.Role = role;
            s.P1 = p1;
            s.P2 = p2;
            s.Bmin = new double[] { Math.Min(p1[0], p2[0]), Math.Min(p1[1], p2[1]) };
            s.Bmax = new double[] { Math.Max(p1[0], p2[0]), Math.Max(p1[1], p2[1]) };
            if (s.Bmin[0] < xMin) xMin = s.Bmin[0];
            if (s.Bmin[1] < yMin) yMin = s.Bmin[1];
            if (s.Bmax[0] > xMax) xMax = s.Bmax[0];
            if (s.Bmax[1] > yMax) yMax = s.Bmax[1];
            return s;
        }

        /// <summary>Rough text width for placement only (CJK glyphs ~1 em, ASCII ~0.56 em).</summary>
        public static double EstTextWidth(string text, double height)
        {
            if (text == null) return 0;
            double w = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c > 0x2E80) w += height;
                else w += height * 0.56;
            }
            return w;
        }

        public static JMap ByType(List<SecSpec> specs)
        {
            JMap m = new JMap();
            foreach (SecSpec s in specs)
            {
                string k = s.Kind == "poly" ? "LWPOLYLINE" : s.Kind == "line" ? "LINE" : s.Kind == "text" ? "TEXT" : "CIRCLE";
                int n = m.Has(k) ? m.Int(k, 0) : 0;
                m.Add(k, n + 1);
            }
            return m;
        }

        public static JMap ByLayer(List<SecSpec> specs)
        {
            JMap m = new JMap();
            foreach (SecSpec s in specs)
            {
                int n = m.Has(s.Layer) ? m.Int(s.Layer, 0) : 0;
                m.Add(s.Layer, n + 1);
            }
            return m;
        }

        public static List<object> LayerNames(SecPlan p)
        {
            List<object> l = new List<object>();
            foreach (object o in p.LayerList) l.Add(o);
            return l;
        }

        /// <summary>Canonical layer order for creation: annotation/water/axis first, then series layers.</summary>
        public static List<object> OrderedLayers(SecPlan p)
        {
            return LayerNames(p);
        }
    }
}
