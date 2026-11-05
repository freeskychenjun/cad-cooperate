// CommandsVerify.cs - verification family of the Commands partial class: VerifySection and
// its small helpers Extend, AddIssue and TypeMatches.
//
// Split out of the former 1,382-line CommandsRead.cs (plan 5.1 batch 3, 2026-11-05;
// pure move). Member bodies are byte-identical; no call site changed.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZwcHelper
{
    internal static partial class Commands
    {
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
    }
}
