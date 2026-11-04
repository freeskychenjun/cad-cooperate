// LispFile.cs - M3-E5 fast canvas scan for CAD builds without the LISP variable bridge.
//
// ZWCAD 2025 (and AutoCAD 2007) run LISP fine but cannot hand a value back through
// GetLispVariable. Measured on the user's 36,558-entity terrain drawing: a COM sweep is ~15 ms per
// entity (~9 minutes); a LISP ssget sweep that writes a text file takes **363 ms** end to end.
//
// Protocol:
//   1. helper writes  %TEMP%\zwcad-cooperate\scan-<guid>.lsp  (a single top-level (progn ...))
//   2. helper sends  (load "C:/.../scan-<guid>.lsp")  through Document.SendCommand (short command)
//   3. the LISP walks (ssget "X" <filter>), writes one line per entity:
//         handle|type|layer|text
//      then writes  __DONE__  into scan-<guid>.done
//   4. helper polls the sentinel, decodes the text file as GBK (cp936 - the LISP file writer uses
//      the local ANSI code page; UTF-8 reads produce mojibake on Chinese annotations), parses the
//      rows and deletes all three temp files.
//
// Read-only by construction: only ssget/entget/open/write-line are used. No setvar, no command,
// no modification of the drawing (CMDECHO is not touched either).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZwcHelper
{
    internal static class LispFile
    {
        /// <summary>Map our kind vocabulary to the DXF type list used by ssget.</summary>
        internal static string CadTypeFilter(string typeList)
        {
            if (string.IsNullOrEmpty(typeList)) return null;
            List<string> want = new List<string>();
            foreach (string raw in typeList.Split(','))
            {
                string k = Commands.KindOf(raw.Trim());
                switch (k)
                {
                    case "TEXT": want.Add("TEXT"); break;
                    case "MTEXT": want.Add("MTEXT"); break;
                    case "LINE": want.Add("LINE"); break;
                    case "LWPOLYLINE":
                    case "POLYLINE": want.Add("LWPOLYLINE"); want.Add("POLYLINE"); break;
                    case "CIRCLE": want.Add("CIRCLE"); break;
                    case "ARC": want.Add("ARC"); break;
                    case "INSERT": want.Add("INSERT"); break;
                    case "HATCH": want.Add("HATCH"); break;
                    case "POINT": want.Add("POINT"); break;
                    case "DIMENSION": want.Add("DIMENSION"); break;
                    default: want.Add(raw.Trim().ToUpperInvariant()); break;
                }
            }
            want.Sort(StringComparer.Ordinal);
            List<string> uniq = new List<string>();
            foreach (string w in want) if (uniq.Count == 0 || uniq[uniq.Count - 1] != w) uniq.Add(w);
            return uniq.Count == 0 ? null : string.Join(",", uniq.ToArray());
        }

        /// <summary>DXF group-code 0 name (what LISP sees) to the AcDb object name that the COM
        /// path returns. Keeps `read`/`query` responses identical no matter which path produced them.</summary>
        internal static string NormalizeType(string dxfType)
        {
            switch ((dxfType ?? "").Trim().ToUpperInvariant())
            {
                case "TEXT": return "AcDbText";
                case "MTEXT": return "AcDbMText";
                case "LINE": return "AcDbLine";
                case "LWPOLYLINE": return "AcDbPolyline";
                case "POLYLINE": return "AcDb2dPolyline";
                case "CIRCLE": return "AcDbCircle";
                case "ARC": return "AcDbArc";
                case "INSERT": return "AcDbBlockReference";
                case "POINT": return "AcDbPoint";
                case "HATCH": return "AcDbHatch";
                case "DIMENSION": return "AcDbDimension";
                case "SPLINE": return "AcDbSpline";
                case "ELLIPSE": return "AcDbEllipse";
                case "ACAD_PROXY_ENTITY": return "AcDbProxyEntity";
                default: return dxfType;
            }
        }
        private static string LispEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "/").Replace("\"", "");
        }

        /// <summary>Scan model space through LISP and return {entities:[...], count:int, textCount:int}.</summary>
        internal static JMap Scan(object doc, Ctx ctx, string layer, string cadTypes, int timeoutMs, out JMap meta)
        {
            meta = new JMap();
            string dir = State.Dir();
            string guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            string lspPath = Path.Combine(dir, "scan-" + guid + ".lsp");
            string txtPath = Path.Combine(dir, "scan-" + guid + ".txt");
            string donePath = Path.Combine(dir, "scan-" + guid + ".done");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // ---------------- pre-flight guard (never inject into a busy / errored command state)
                try
                {
                    object ca = Com.Call(doc, "GetVariable", "CMDACTIVE");
                    if (Com.I32(ca) != 0) throw new ZwcError("BUSY", "CAD is busy (CMDACTIVE != 0); LISP file scan not injected", "retry when the command prompt is idle", null);
                    object lp = Com.Call(doc, "GetVariable", "LASTPROMPT");
                    string prompt = Com.Str(lp) ?? "";
                    if (prompt.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                        || prompt.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0
                        || prompt.IndexOf("错误", StringComparison.Ordinal) >= 0)
                        throw new ZwcError("PROMPT_POLLUTED", "CAD command prompt is polluted: " + prompt, "send ESC in CAD, or let the tool retry", null);
                }
                catch (ZwcError) { throw; }
                catch (Exception) { /* probes are best effort */ }

                // ---------------- build the LISP source (ASCII only: paths are %TEMP%, no Chinese)
                StringBuilder flt = new StringBuilder();
                bool hasFilter = false;
                if (!string.IsNullOrEmpty(cadTypes))
                {
                    flt.Append("(cons 0 \"").Append(cadTypes.Replace("\"", "")).Append("\")");
                    hasFilter = true;
                }
                if (!string.IsNullOrEmpty(layer))
                {
                    if (hasFilter) flt.Append(' ');
                    flt.Append("(cons 8 \"").Append(layer.Replace("\"", "")).Append("\")");
                    hasFilter = true;
                }
                string ssFilter = hasFilter ? "(list " + flt.ToString() + ")" : null;
                string outPath = LispEscape(txtPath);
                string doneF = LispEscape(donePath);
                StringBuilder lsp = new StringBuilder();
                lsp.Append("(progn ");
                lsp.Append("(setq _zwc_echo (getvar \"CMDECHO\")) ");
                lsp.Append("(setvar \"CMDECHO\" 0) ");
                lsp.Append("(setq _zwc_f (open \"").Append(outPath).Append("\" \"w\")) ");
                lsp.Append("(setq _zwc_ss (ssget \"X\"").Append(ssFilter == null ? "" : (" " + ssFilter)).Append(")) ");
                lsp.Append("(setq _zwc_n (if _zwc_ss (sslength _zwc_ss) 0)) ");
                lsp.Append("(setq _zwc_i 0) ");
                lsp.Append("(while (< _zwc_i _zwc_n) ");
                lsp.Append("(setq _zwc_e (ssname _zwc_ss _zwc_i)) ");
                lsp.Append("(setq _zwc_d (entget _zwc_e)) ");
                lsp.Append("(setq _zwc_h (cdr (assoc 5 _zwc_d))) ");
                lsp.Append("(setq _zwc_t (cdr (assoc 0 _zwc_d))) ");
                lsp.Append("(setq _zwc_l (cdr (assoc 8 _zwc_d))) ");
                lsp.Append("(setq _zwc_x (cdr (assoc 1 _zwc_d))) ");
                lsp.Append("(if (= _zwc_x nil) (setq _zwc_x \"\")) ");
                lsp.Append("(if (= _zwc_h nil) (setq _zwc_h \"\")) ");
                lsp.Append("(if (= _zwc_t nil) (setq _zwc_t \"\")) ");
                lsp.Append("(if (= _zwc_l nil) (setq _zwc_l \"\")) ");
                lsp.Append("(write-line (strcat _zwc_h \"|\" _zwc_t \"|\" _zwc_l \"|\" _zwc_x) _zwc_f) ");
                lsp.Append("(setq _zwc_i (1+ _zwc_i))) ");
                lsp.Append("(close _zwc_f) ");
                lsp.Append("(setq _zwc_fs (open \"").Append(doneF).Append("\" \"w\")) ");
                lsp.Append("(write-line \"__DONE__\" _zwc_fs) ");
                lsp.Append("(close _zwc_fs) ");
                lsp.Append("(setvar \"CMDECHO\" _zwc_echo) ");
                lsp.Append("(princ))");
                File.WriteAllText(lspPath, lsp.ToString(), Encoding.ASCII);

                // ---------------- inject (short command) and wait for the sentinel
                string cmd = "(load \"" + LispEscape(lspPath) + "\")";
                if (cmd.Length > 1500) throw new ZwcError("BAD_ARGS", "LISP load command too long", null, null);
                Com.Call(doc, "SendCommand", cmd + "\n");
                long injectMs = sw.ElapsedMilliseconds;
                bool done = false;
                long deadline = Math.Max(1000, timeoutMs);
                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (File.Exists(donePath))
                    {
                        try
                        {
                            string marker = File.ReadAllText(donePath);
                            if (marker.IndexOf("__DONE__", StringComparison.Ordinal) >= 0) { done = true; break; }
                        }
                        catch (Exception) { }
                    }
                    Thread.Sleep(25);
                }
                if (!done) throw new ZwcError("LISP_FILE_TIMEOUT", "LISP scan did not finish within " + timeoutMs + " ms", "retry with a larger -TimeoutSec or fall back to COM", null);
                long sentinelMs = sw.ElapsedMilliseconds;

                // ---------------- read + decode + parse (GBK / cp936)
                byte[] raw = File.ReadAllBytes(txtPath);
                string text;
                Encoding gbk;
                try { gbk = Encoding.GetEncoding(936); }
                catch (Exception) { gbk = Encoding.Default; }
                text = gbk.GetString(raw);
                JMap result = new JMap();
                List<object> entities = new List<object>();
                int textCount = 0;
                string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Length == 0 || line.IndexOf("__DONE__", StringComparison.Ordinal) >= 0) continue;
                    string[] parts = line.Split(new char[] { '|' }, 4);
                    if (parts.Length < 3) continue;
                    string handle = parts[0].Trim();
                    string type = parts[1].Trim();
                    string lay = parts[2].Trim();
                    string txt = parts.Length > 3 ? parts[3] : "";
                    txt = txt.Replace('\r', ' ').Replace('\n', ' ');
                    JMap m = new JMap();
                    m.Add("handle", handle);
                    m.Add("type", NormalizeType(type));
                    m.Add("kind", Commands.KindOf(NormalizeType(type)));
                    m.Add("layer", lay);
                    m.Add("space", "model");
                    if (txt.Length > 0) { m.Add("text", txt); textCount++; }
                    entities.Add(m);
                }
                result.Add("entities", entities);
                result.Add("count", entities.Count);
                result.Add("textCount", textCount);
                meta.Add("lispInjectMs", injectMs);
                meta.Add("lispSentinelMs", sentinelMs);
                meta.Add("parseMs", sw.ElapsedMilliseconds - sentinelMs);
                meta.Add("totalMs", sw.ElapsedMilliseconds);
                meta.Add("fileBytes", raw.Length);
                meta.Add("decodedAs", "GBK/936");
                return result;
            }
            finally
            {
                try { if (File.Exists(lspPath)) File.Delete(lspPath); } catch (Exception) { }
                try { if (File.Exists(txtPath)) File.Delete(txtPath); } catch (Exception) { }
                try { if (File.Exists(donePath)) File.Delete(donePath); } catch (Exception) { }
            }
        }
    }
}
