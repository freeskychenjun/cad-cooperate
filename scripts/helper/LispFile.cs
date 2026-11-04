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
            int echoBefore = 1;
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
                // ssget filter: type list + exact layer
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
                // M3-E6: the script is generated by LispTemplate, which enforces ASCII/no-BOM and
                // parentheses balance before anything is injected (LISP_TEMPLATE_* errors).
                string src = LispTemplate.BuildDump(false, txtPath, donePath, ssFilter);
                echoBefore = Commands.CmdEchoOf(doc);
                LispTemplate.WriteScript(lspPath, src);

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
                    // The dump rows carry a record-kind prefix (LispTemplate.BuildDump):
                    //   E|handle|type|layer|text|x|y|z|nverts
                    // Parsing them as "handle|type|layer|text" shifted every field by one: `handle`
                    // came back as "E", `type` as the real handle, `layer` as the real type and the
                    // real layer got glued to the front of the text - so `-Layer` / `-Type` filtered
                    // reads matched nothing on every drawing above the 2,000-entity threshold.
                    List<string> parts = SplitRecord(line);
                    if (parts.Count < 4) continue;
                    string recKind = parts[0].Trim();
                    string handle;
                    string type;
                    string lay;
                    string txt;
                    if (string.Equals(recKind, "E", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parts.Count < 5) continue;
                        handle = parts[1].Trim();
                        type = parts[2].Trim();
                        lay = parts[3].Trim();
                        txt = UnescapeField(parts[4]);
                    }
                    else if (IsAuxRecord(recKind))
                    {
                        continue;   // geometry side-records are LispGeom's business, not this scan
                    }
                    else
                    {
                        // legacy four-field rows: handle|type|layer|text
                        handle = parts[0].Trim();
                        type = parts[1].Trim();
                        lay = parts[2].Trim();
                        txt = parts.Count > 3 ? UnescapeField(parts[3]) : "";
                    }
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
                try { Commands.RestoreCmdEcho(doc, echoBefore); } catch (Exception) { }
                try { if (File.Exists(lspPath)) File.Delete(lspPath); } catch (Exception) { }
                try { if (File.Exists(txtPath)) File.Delete(txtPath); } catch (Exception) { }
                try { if (File.Exists(donePath)) File.Delete(donePath); } catch (Exception) { }
            }
        }

        /// <summary>Split one dump row on '|' while honouring the '\|' escape used inside text.</summary>
        private static List<string> SplitRecord(string line)
        {
            List<string> f = new List<string>();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length) { cur.Append(c); cur.Append(line[i + 1]); i++; continue; }
                if (c == '|') { f.Add(cur.ToString()); cur.Length = 0; continue; }
                cur.Append(c);
            }
            f.Add(cur.ToString());
            return f;
        }

        /// <summary>Map the '\|' escape back to a literal '|'. Everything else is left as written.</summary>
        private static string UnescapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|') { sb.Append('|'); i++; continue; }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>Record kinds that only appear in geometry dumps (LispGeom handles those).</summary>
        private static bool IsAuxRecord(string kind)
        {
            if (kind == null) return false;
            switch (kind.Trim().ToUpperInvariant())
            {
                case "V":
                case "P2":
                case "ELEV":
                case "H":
                case "CL":
                case "R":
                    return true;
                default:
                    return false;
            }
        }
    }
}
