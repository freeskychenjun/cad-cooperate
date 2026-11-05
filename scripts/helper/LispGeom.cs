// LispGeom.cs - M3-E6 geometry dump fast path for large drawings without a LISP variable bridge.
//
// E5 proved a LISP ssget sweep writing a text file runs in ~0.4 s for 36,558 entities, but it only
// returned handle/type/layer/text - the geometry (points, vertices, contour elevation) was missing,
// so real section work had to fall back to a 310 s COM sweep. This adds the geometry dump:
//
//   E|handle|type|layer|text|x|y|z|nverts
//   V|handle|i|x|y|z
//   P2|handle|x|y|z
//   ELEV|handle|assoc38      (contour polylines carry their height only here)
//   H|handle|assoc40         (TEXT/MTEXT height)
//   R|handle|assoc40         (CIRCLE/ARC radius)
//   CL|handle|assoc70        (polyline closed flag)
//   __DONE__|<n>
//
// The injection pipeline (guard, ssget filter, template write, two-phase sentinel wait, GBK read-back,
// CMDECHO restore and cleanup/orphan decision) lives in LispScan.cs - one implementation shared with
// LispFile since M3-E7 P4.2. Script generation/validation lives in LispTemplate (ASCII/no-BOM +
// balance + read-only safety gate); the wire parsing lives in LispWire (escape-aware, one
// implementation shared with LispFile, so a '|' inside a TEXT/MTEXT can no longer shift the geometry
// fields). This file keeps only what is specific to the geometry dump: the request parameters, the
// LispWire.ParseGeometry call and the LispRecord -> JMap mapping, which keeps the pre-refactor field
// names/order (the only addition is "radius" for CIRCLE/ARC, which the COM path already returned).
//
// Injection safety (M3-E6 P1-4.1, shared with the flat scan):
//   * the generated script writes a __START__ sentinel first and installs an AutoLISP *error* handler
//     that restores CMDECHO and the previous *error*, so a script that dies inside CAD can no longer
//     leave the user's command line silent;
//   * a timeout distinguishes "never started" (no __START__ -> LISP_NOT_STARTED) from "running but
//     stuck" (__START__ seen, no __DONE__ -> LISP_FILE_TIMEOUT with a phase detail);
//   * an unfinished injection renames its temp files to *.orphan instead of deleting them (CAD may
//     still be writing them) and every scan sweeps leftovers older than 24 h.

using System;
using System.Collections.Generic;

namespace CadHelper
{
    internal static class LispGeom
    {
        internal static JMap Scan(object doc, Ctx ctx, string layer, string cadTypes, int timeoutMs, out JMap meta)
        {
            meta = new JMap();
            // request: E6 geometry dump, "geom-" temp prefix (the orphan sweep keys on it), and the
            // exact wording of the documented errors for this scan.
            LispScan.Request req = LispScan.NewRequest("geom", true, State.Dir(),
                LispScan.BuildSsFilter(cadTypes, layer), timeoutMs);
            req.BusyLabel = "LISP geometry dump";
            req.TimeoutLabel = "LISP geometry dump";
            req.BusyHint = "retry when idle";
            req.PollutedHint = "send ESC in CAD first";

            JMap runMeta;
            string phase;
            LispScan.Result scan = LispGate.Run(doc, ctx, req, out runMeta, out phase);

            // parse callback: E6 rows + side records -> the entity maps the COM path would have produced
            List<object> entities = new List<object>();
            Dictionary<string, JMap> byHandle = new Dictionary<string, JMap>(System.StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            int records;
            List<LispRecord> parsed = LispWire.ParseGeometry(scan.Text, out records);
            for (int ri = 0; ri < parsed.Count; ri++)
            {
                LispRecord rec = parsed[ri];
                JMap m = new JMap();
                string type = LispFile.NormalizeType(rec.Type);
                m.Add("handle", rec.Handle);
                m.Add("type", type);
                m.Add("kind", Commands.KindOf(type));
                m.Add("layer", rec.Layer);
                m.Add("space", "model");
                m.Add("text", rec.Text);
                m.Add("x", rec.X);
                m.Add("y", rec.Y);
                m.Add("z", rec.Z);
                m.Add("point", new List<object> { rec.X, rec.Y, rec.Z });
                m.Add("nverts", rec.Nverts);
                // Replay the side-records in dump order so the JSON key order stays what it was.
                if (rec.AuxOrder != null)
                {
                    for (int ai = 0; ai < rec.AuxOrder.Count; ai++)
                    {
                        string aux = rec.AuxOrder[ai];
                        if (aux == "V")
                        {
                            List<object> verts = new List<object>();
                            if (rec.Vertices != null)
                                for (int vi = 0; vi < rec.Vertices.Count; vi++)
                                    verts.Add(new List<object> { rec.Vertices[vi][0], rec.Vertices[vi][1], rec.Vertices[vi][2] });
                            m.Add("vertices", verts);
                        }
                        else if (aux == "P2" && rec.P2 != null)
                        {
                            m.Add("p2", new List<object> { rec.P2[0], rec.P2[1], rec.P2[2] });
                        }
                        else if (aux == "ELEV" && rec.Elevation.HasValue)
                        {
                            m.Add("elevation", rec.Elevation.Value);
                        }
                        else if (aux == "H" && rec.Height.HasValue)
                        {
                            m.Add("height", rec.Height.Value);
                        }
                        else if (aux == "R" && rec.Radius.HasValue)
                        {
                            m.Add("radius", rec.Radius.Value);
                        }
                        else if (aux == "CL" && rec.Flags.HasValue)
                        {
                            m.Add("closed", (rec.Flags.Value & 1) == 1);
                        }
                        else if (aux == "A" && rec.Attributes != null)
                        {
                            List<object> atts = new List<object>();
                            for (int ai2 = 0; ai2 < rec.Attributes.Count; ai2++)
                            {
                                LispAttribute a = rec.Attributes[ai2];
                                JMap am = new JMap();
                                am.Add("tag", a.Tag);
                                am.Add("value", a.Value);
                                am.Add("x", a.X);
                                am.Add("y", a.Y);
                                am.Add("z", a.Z);
                                atts.Add(am);
                            }
                            m.Add("attributes", atts);
                        }
                    }
                }
                entities.Add(m);
                if (!byHandle.ContainsKey(rec.Handle)) { byHandle[rec.Handle] = m; order.Add(rec.Handle); }
            }

            // Old-style 2D/3D polylines: their vertices are VERTEX sub-entities that the LISP dump
            // cannot read reliably (entnext crosses SEQEND on AutoCAD 2021 - measured: an 11-vertex
            // contour came back with 99 vertices) and reading them vertex-by-vertex took 12.5 minutes
            // on one real contour drawing. AcadPolyline.Coordinates returns the whole (x,y,z) triple
            // array in ONE COM call, so the few polyline entities are filled through COM here and the
            // LISP dump keeps doing what it is good at (the entity inventory + block attributes).
            int polyFilled = FillPolylineVertices(doc, ctx, byHandle, order);

            // shape the geometry fields the COM path would return, so downstream code is path-agnostic
            foreach (string h in order)
            {
                JMap m = byHandle[h];
                string type = m.Str("type", "");
                List<object> v = m.List("vertices");
                List<object> p2 = m.List("p2");
                if (type == "AcDbLine" && p2 != null)
                {
                    List<object> start = m.List("point");
                    m.Add("start", new List<object> { start[0], start[1] });
                    m.Add("end", new List<object> { p2[0], p2[1] });
                    m.Add("points", new List<object> { new List<object> { start[0], start[1] }, new List<object> { p2[0], p2[1] } });
                }
                else if (type == "AcDbPolyline" || type == "AcDb2dPolyline")
                {
                    List<object> pts2 = new List<object>();
                    if (v != null) foreach (object o in v)
                    {
                        List<object> t3 = o as List<object>;
                        if (t3 != null && t3.Count >= 2) pts2.Add(new List<object> { t3[0], t3[1] });
                    }
                    m.Add("points", pts2);
                    if (v != null) m.Add("vertices", pts2);
                    // Old-style POLYLINE: the E row only sees the main entity, whose one group-10 point
                    // is (0,0,elevation). The real vertex count comes from the VERTEX sub-entity walk,
                    // so nverts must follow the collected V records or the number contradicts the list.
                    if (v != null) m.Add("nverts", v.Count);
                    if (!m.Has("closed")) m.Add("closed", false);
                }
                else if (type == "AcDbMText" && p2 != null)
                {
                    m.Add("insertion", m.Get("point"));
                }
            }

            JMap result = new JMap();
            result.Add("entities", entities);
            result.Add("count", entities.Count);
            result.Add("records", records);
            result.Add("sentinelCount", scan.SentinelCount);
            LispScan.AddMeta(runMeta, scan);   // after the parse: parseMs/totalMs keep their meaning
            runMeta.Add("mode", "geom");
            runMeta.Add("polylineComFilled", polyFilled);
            meta = runMeta;
            return result;
        }

        /// <summary>Fill vertices/elevation/closed of old-style polylines through COM (one
        /// Coordinates call per entity, triples for AcDb2d/3dPolyline). Returns how many were filled.
        /// Best effort: a failure leaves the LISP data as it is and never fails the read.</summary>
        private static int FillPolylineVertices(object doc, Ctx ctx, Dictionary<string, JMap> byHandle, List<string> order)
        {
            int filled = 0;
            int failed = 0;
            for (int i = 0; i < order.Count; i++)
            {
                JMap m = byHandle[order[i]];
                string type = m.Str("type", "");
                if (type != "AcDb2dPolyline" && type != "AcDb3dPolyline") continue;
                try
                {
                    object e = Com.EntityByHandle(doc, order[i]);
                    if (e == null) { failed++; continue; }
                    double[] flat = Com.Flat(Com.TryGet(e, "Coordinates"));
                    List<object> pts = new List<object>();
                    for (int k = 0; k + 1 < flat.Length; k += 3) pts.Add(new List<object> { flat[k], flat[k + 1] });
                    if (pts.Count == 0) { failed++; continue; }
                    m.Add("vertices", pts);
                    m.Add("points", pts);
                    m.Add("nverts", pts.Count);
                    object elev = Com.TryGet(e, "Elevation");
                    if (elev != null) m.Add("elevation", Com.Dbl(elev));
                    object closed = Com.TryGet(e, "Closed");
                    if (closed != null) m.Add("closed", Com.Bool(closed));
                    filled++;
                }
                catch (Exception) { failed++; }
            }
            if (failed > 0)
                ctx.Warn("POLYLINE_COM_FILL_INCOMPLETE", failed + " old-style polyline(s) could not be filled through COM; their vertices stay as dumped");
            return filled;
        }
    }
}
