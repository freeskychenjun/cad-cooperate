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
// Script generation and validation live in LispTemplate (ASCII/no-BOM + parenthesis balance); the
// wire parsing lives in LispWire (one implementation shared with LispFile, escape-aware, so a '|'
// inside a TEXT/MTEXT can no longer shift the geometry fields). This file is only the pipeline plus
// the LispRecord -> JMap mapping, and the mapping keeps the pre-refactor field names/order (the only
// addition is "radius" for CIRCLE/ARC, which the COM path already returned).
//
// Injection safety (M3-E6 P1-4.1):
//   * the generated script writes a __START__ sentinel first and installs an AutoLISP *error* handler
//     that restores CMDECHO and the previous *error*, so a script that dies inside CAD can no longer
//     leave the user's command line silent;
//   * a timeout distinguishes "never started" (no __START__ -> LISP_NOT_STARTED) from "running but
//     stuck" (__START__ seen, no __DONE__ -> LISP_FILE_TIMEOUT with a phase detail);
//   * an unfinished injection renames its temp files to *.orphan instead of deleting them (CAD may
//     still be writing them) and every scan sweeps leftovers older than 24 h.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ZwcHelper
{
    internal static class LispGeom
    {
        internal static JMap Scan(object doc, Ctx ctx, string layer, string cadTypes, int timeoutMs, out JMap meta)
        {
            meta = new JMap();
            string dir = State.Dir();
            LispOrphans.CleanStale(dir, TimeSpan.FromHours(24));
            string guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            string lspPath = Path.Combine(dir, "geom-" + guid + ".lsp");
            string txtPath = Path.Combine(dir, "geom-" + guid + ".txt");
            string donePath = Path.Combine(dir, "geom-" + guid + ".done");
            string startPath = Path.Combine(dir, "geom-" + guid + ".start");
            Stopwatch sw = Stopwatch.StartNew();
            int echoBefore = 1;
            bool completed = false;
            try
            {
                // guard: never inject into a busy / errored command state
                try
                {
                    object ca = Com.Call(doc, "GetVariable", "CMDACTIVE");
                    if (Com.I32(ca) != 0) throw new ZwcError("BUSY", "CAD is busy (CMDACTIVE != 0); LISP geometry dump not injected", "retry when idle", null);
                    object lp = Com.Call(doc, "GetVariable", "LASTPROMPT");
                    string prompt = Com.Str(lp) ?? "";
                    if (prompt.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                        || prompt.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0
                        || prompt.IndexOf("错误", StringComparison.Ordinal) >= 0)
                        throw new ZwcError("PROMPT_POLLUTED", "CAD command prompt is polluted: " + prompt, "send ESC in CAD first", null);
                }
                catch (ZwcError) { throw; }
                catch (Exception) { }

                // ssget filter: type list + exact layer (both are cheap and let CAD walk less)
                StringBuilder flt = new StringBuilder();
                bool has = false;
                if (!string.IsNullOrEmpty(cadTypes)) { flt.Append("(cons 0 \"").Append(cadTypes.Replace("\"", "")).Append("\")"); has = true; }
                if (!string.IsNullOrEmpty(layer))
                {
                    if (has) flt.Append(' ');
                    flt.Append("(cons 8 \"").Append(layer.Replace("\"", "")).Append("\")");
                    has = true;
                }
                string ssFilter = has ? "(list " + flt.ToString() + ")" : null;

                string src = LispTemplate.BuildDump(true, txtPath, donePath, startPath, ssFilter);
                echoBefore = Commands.CmdEchoOf(doc);
                LispTemplate.WriteScript(lspPath, src);   // ASCII/no-BOM + balance check (throws LISP_TEMPLATE_*)

                string cmd = "(load \"" + LispTemplate.PathForLisp(lspPath) + "\")";
                if (cmd.Length > 1500) throw new ZwcError("BAD_ARGS", "LISP load command too long", null, null);
                Com.Call(doc, "SendCommand", cmd + "\n");
                long injectMs = sw.ElapsedMilliseconds;

                bool done = false;
                bool startSeen = false;
                int sentinelCount = -1;
                long deadline = Math.Max(1000, timeoutMs);
                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (!startSeen && File.Exists(startPath)) startSeen = true;
                    if (File.Exists(donePath))
                    {
                        try
                        {
                            string marker = File.ReadAllText(donePath);
                            if (marker.IndexOf("__DONE__", StringComparison.Ordinal) >= 0)
                            {
                                done = true;
                                int bar = marker.IndexOf('|');
                                if (bar >= 0) int.TryParse(marker.Substring(bar + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sentinelCount);
                                break;
                            }
                        }
                        catch (Exception) { }
                    }
                    Thread.Sleep(25);
                }
                if (!done)
                {
                    // Two-phase sentinel: no __START__ means the injected command never ran (busy CAD,
                    // modal dialog, LISP disabled); __START__ without __DONE__ means it is still running.
                    if (!startSeen) startSeen = File.Exists(startPath);
                    string phase = LispSentinelPhase.Classify(done, startSeen);
                    string detail = "phase=" + phase + "; startSeen=" + (startSeen ? "true" : "false")
                        + "; elapsedMs=" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
                    if (phase == LispSentinelPhase.NotStarted)
                        throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                            "the LISP geometry dump did not start within " + timeoutMs + " ms (no __START__ sentinel)",
                            "CAD is probably busy or a modal dialog is open; run doctor, clear the command line, then retry",
                            detail);
                    throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                        "LISP geometry dump did not finish within " + timeoutMs + " ms",
                        "retry with a larger -TimeoutSec; the script is still running inside CAD - its temp files were kept as *.orphan in %TEMP%\\zwcad-cooperate",
                        detail);
                }
                long sentinelMs = sw.ElapsedMilliseconds;

                byte[] raw = File.ReadAllBytes(txtPath);
                Encoding enc;
                try { enc = Encoding.GetEncoding(936); } catch (Exception) { enc = Encoding.Default; }
                string text = enc.GetString(raw);

                List<object> entities = new List<object>();
                Dictionary<string, JMap> byHandle = new Dictionary<string, JMap>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();
                int records;
                List<LispRecord> parsed = LispWire.ParseGeometry(text, out records);
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
                        }
                    }
                    entities.Add(m);
                    if (!byHandle.ContainsKey(rec.Handle)) { byHandle[rec.Handle] = m; order.Add(rec.Handle); }
                }

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
                result.Add("sentinelCount", sentinelCount);
                meta.Add("lispInjectMs", injectMs);
                meta.Add("lispSentinelMs", sentinelMs);
                meta.Add("parseMs", sw.ElapsedMilliseconds - sentinelMs);
                meta.Add("totalMs", sw.ElapsedMilliseconds);
                meta.Add("fileBytes", raw.Length);
                meta.Add("decodedAs", "GBK/936");
                meta.Add("mode", "geom");
                completed = true;
                return result;
            }
            finally
            {
                try { Commands.RestoreCmdEcho(doc, echoBefore); } catch (Exception) { }
                if (completed)
                {
                    LispOrphans.DeleteQuiet(lspPath);
                    LispOrphans.DeleteQuiet(txtPath);
                    LispOrphans.DeleteQuiet(donePath);
                    LispOrphans.DeleteQuiet(startPath);
                }
                else
                {
                    // Never delete what CAD may still be writing: keep the evidence as *.orphan.
                    LispOrphans.Preserve(dir, lspPath, txtPath, donePath, startPath);
                }
            }
        }
    }
}
