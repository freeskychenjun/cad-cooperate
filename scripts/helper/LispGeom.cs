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
//   CL|handle|assoc70        (polyline closed flag)
//   __DONE__|<n>
//
// Script generation and validation live in LispTemplate (ASCII/no-BOM + parenthesis balance).

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
            string guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            string lspPath = Path.Combine(dir, "geom-" + guid + ".lsp");
            string txtPath = Path.Combine(dir, "geom-" + guid + ".txt");
            string donePath = Path.Combine(dir, "geom-" + guid + ".done");
            Stopwatch sw = Stopwatch.StartNew();
            int echoBefore = 1;
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

                string src = LispTemplate.BuildDump(true, txtPath, donePath, ssFilter);
                echoBefore = Commands.CmdEchoOf(doc);
                LispTemplate.WriteScript(lspPath, src);   // ASCII/no-BOM + balance check (throws LISP_TEMPLATE_*)

                string cmd = "(load \"" + LispTemplate.PathForLisp(lspPath) + "\")";
                if (cmd.Length > 1500) throw new ZwcError("BAD_ARGS", "LISP load command too long", null, null);
                Com.Call(doc, "SendCommand", cmd + "\n");
                long injectMs = sw.ElapsedMilliseconds;

                bool done = false;
                int sentinelCount = -1;
                long deadline = Math.Max(1000, timeoutMs);
                while (sw.ElapsedMilliseconds < deadline)
                {
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
                if (!done) throw new ZwcError("LISP_FILE_TIMEOUT", "LISP geometry dump did not finish within " + timeoutMs + " ms", "retry with a larger -TimeoutSec or fall back to COM", null);
                long sentinelMs = sw.ElapsedMilliseconds;

                byte[] raw = File.ReadAllBytes(txtPath);
                Encoding enc;
                try { enc = Encoding.GetEncoding(936); } catch (Exception) { enc = Encoding.Default; }
                string text = enc.GetString(raw);

                List<object> entities = new List<object>();
                Dictionary<string, JMap> byHandle = new Dictionary<string, JMap>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();
                int records = 0;
                string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Length == 0 || line.IndexOf("__DONE__", StringComparison.Ordinal) >= 0) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 2) continue;
                    string kind = p[0];
                    string h = p[1];
                    if (kind == "E")
                    {
                        if (p.Length < 9) continue;
                        JMap m = new JMap();
                        m.Add("handle", h);
                        m.Add("type", LispFile.NormalizeType(p[2]));
                        m.Add("kind", Commands.KindOf(LispFile.NormalizeType(p[2])));
                        m.Add("layer", p[3]);
                        m.Add("space", "model");
                        string txt2 = Unescape(p[4]);
                        m.Add("text", txt2);
                        double x = Num(p[5]), y = Num(p[6]), z = Num(p[7]);
                        int nv = 0; int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out nv);
                        m.Add("x", x); m.Add("y", y); m.Add("z", z);
                        m.Add("point", new List<object> { x, y, z });
                        m.Add("nverts", nv);
                        entities.Add(m);
                        if (!byHandle.ContainsKey(h)) { byHandle[h] = m; order.Add(h); }
                        records++;
                    }
                    else
                    {
                        JMap m;
                        if (!byHandle.TryGetValue(h, out m)) continue;
                        if (kind == "V" && p.Length >= 6)
                        {
                            List<object> verts = m.List("vertices");
                            if (verts == null) { verts = new List<object>(); m.Add("vertices", verts); }
                            verts.Add(new List<object> { Num(p[3]), Num(p[4]), Num(p[5]) });
                        }
                        else if (kind == "P2" && p.Length >= 5)
                        {
                            m.Add("p2", new List<object> { Num(p[2]), Num(p[3]), Num(p[4]) });
                        }
                        else if (kind == "ELEV" && p.Length >= 3)
                        {
                            m.Add("elevation", Num(p[2]));
                        }
                        else if (kind == "H" && p.Length >= 3)
                        {
                            m.Add("height", Num(p[2]));
                        }
                        else if (kind == "CL" && p.Length >= 3)
                        {
                            int flags = (int)Num(p[2]);
                            bool closed = (flags & 1) == 1;
                            m.Add("closed", closed);
                        }
                    }
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
                return result;
            }
            finally
            {
                try { Commands.RestoreCmdEcho(doc, echoBefore); } catch (Exception) { }
                try { if (File.Exists(lspPath)) File.Delete(lspPath); } catch (Exception) { }
                try { if (File.Exists(txtPath)) File.Delete(txtPath); } catch (Exception) { }
                try { if (File.Exists(donePath)) File.Delete(donePath); } catch (Exception) { }
            }
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\|", "|");
        }

        private static double Num(string s)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0.0;
        }
    }
}
