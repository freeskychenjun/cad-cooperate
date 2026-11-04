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
//         E|handle|type|layer|text|x|y|z|nverts
//      writes __START__ into scan-<guid>.start, then __DONE__ into scan-<guid>.done
//   4. helper polls the sentinels, decodes the text file as GBK (cp936 - the LISP file writer uses
//      the local ANSI code page; UTF-8 reads produce mojibake on Chinese annotations), parses the
//      rows and deletes all four temp files.
//
// Parsing lives in LispWire (one implementation shared with LispGeom: escape-aware SplitRecord, so a
// '|' inside text cannot shift the fields; the E5 row is read as handle|type|layer|text).
//
// Read-only by construction: only ssget/entget/open/write-line are used. No setvar beyond CMDECHO,
// no command, no modification of the drawing. The generated script also installs an AutoLISP *error*
// handler that restores CMDECHO, and an unfinished injection keeps its temp files as *.orphan
// instead of deleting files CAD may still be writing (M3-E6 P1-4.1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
            LispOrphans.CleanStale(dir, TimeSpan.FromHours(24));
            string guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            string lspPath = Path.Combine(dir, "scan-" + guid + ".lsp");
            string txtPath = Path.Combine(dir, "scan-" + guid + ".txt");
            string donePath = Path.Combine(dir, "scan-" + guid + ".done");
            string startPath = Path.Combine(dir, "scan-" + guid + ".start");
            Stopwatch sw = Stopwatch.StartNew();
            int echoBefore = 1;
            bool completed = false;
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
                string src = LispTemplate.BuildDump(false, txtPath, donePath, startPath, ssFilter);
                echoBefore = Commands.CmdEchoOf(doc);
                LispTemplate.WriteScript(lspPath, src);

                // ---------------- inject (short command) and wait for the sentinel
                string cmd = "(load \"" + LispEscape(lspPath) + "\")";
                if (cmd.Length > 1500) throw new ZwcError("BAD_ARGS", "LISP load command too long", null, null);
                Com.Call(doc, "SendCommand", cmd + "\n");
                long injectMs = sw.ElapsedMilliseconds;
                bool done = false;
                bool startSeen = false;
                long deadline = Math.Max(1000, timeoutMs);
                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (!startSeen && File.Exists(startPath)) startSeen = true;
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
                if (!done)
                {
                    // Two-phase sentinel: no __START__ = the command never ran; __START__ without
                    // __DONE__ = it is still running inside CAD (the temp files stay as evidence).
                    if (!startSeen) startSeen = File.Exists(startPath);
                    string phase = LispSentinelPhase.Classify(done, startSeen);
                    string detail = "phase=" + phase + "; startSeen=" + (startSeen ? "true" : "false")
                        + "; elapsedMs=" + sw.ElapsedMilliseconds;
                    if (phase == LispSentinelPhase.NotStarted)
                        throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                            "the LISP file scan did not start within " + timeoutMs + " ms (no __START__ sentinel)",
                            "CAD is probably busy or a modal dialog is open; run doctor, clear the command line, then retry",
                            detail);
                    throw new ZwcError(LispSentinelPhase.ErrorCodeFor(phase),
                        "LISP scan did not finish within " + timeoutMs + " ms",
                        "retry with a larger -TimeoutSec; the script is still running inside CAD - its temp files were kept as *.orphan in %TEMP%\\zwcad-cooperate",
                        detail);
                }
                long sentinelMs = sw.ElapsedMilliseconds;

                // ---------------- read + decode + parse (GBK / cp936)
                byte[] raw = File.ReadAllBytes(txtPath);
                string text;
                Encoding gbk;
                try { gbk = Encoding.GetEncoding(936); }
                catch (Exception) { gbk = Encoding.Default; }
                text = gbk.GetString(raw);
                int textCount;
                List<LispRecord> parsed = LispWire.ParseFlat(text, out textCount);
                List<object> entities = new List<object>();
                for (int i = 0; i < parsed.Count; i++)
                {
                    LispRecord rec = parsed[i];
                    string type = NormalizeType(rec.Type);
                    JMap m = new JMap();
                    m.Add("handle", rec.Handle);
                    m.Add("type", type);
                    m.Add("kind", Commands.KindOf(type));
                    m.Add("layer", rec.Layer);
                    m.Add("space", "model");
                    if (rec.Text.Length > 0) m.Add("text", rec.Text);
                    entities.Add(m);
                }
                JMap result = new JMap();
                result.Add("entities", entities);
                result.Add("count", entities.Count);
                result.Add("textCount", textCount);
                meta.Add("lispInjectMs", injectMs);
                meta.Add("lispSentinelMs", sentinelMs);
                meta.Add("parseMs", sw.ElapsedMilliseconds - sentinelMs);
                meta.Add("totalMs", sw.ElapsedMilliseconds);
                meta.Add("fileBytes", raw.Length);
                meta.Add("decodedAs", "GBK/936");
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
