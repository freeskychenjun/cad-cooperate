// LispFile.cs - M3-E5 fast canvas scan for CAD builds without the LISP variable bridge.
//
// ZWCAD 2025 (and AutoCAD 2007) run LISP fine but cannot hand a value back through
// GetLispVariable. Measured on the user's 36,558-entity terrain drawing: a COM sweep is ~15 ms per
// entity (~9 minutes); a LISP ssget sweep that writes a text file takes **363 ms** end to end.
//
// Protocol (the injection pipeline itself lives in LispScan.cs - one implementation shared with
// LispGeom, so the CMDACTIVE guard, the ssget filter, the two-phase sentinel wait, the GBK read-back
// and the cleanup/orphan decision can no longer drift between the two scans):
//   1. helper writes  %TEMP%\zwcad-cooperate\scan-<guid>.lsp  (a single top-level (progn ...))
//   2. helper sends  (load "C:/.../scan-<guid>.lsp")  through Document.SendCommand (short command)
//   3. the LISP walks (ssget "X" <filter>), writes one line per entity:
//         E|handle|type|layer|text|x|y|z|nverts
//      writes __START__ into scan-<guid>.start, then __DONE__ into scan-<guid>.done
//   4. helper polls the sentinels, decodes the text file as GBK (cp936 - the LISP file writer uses
//      the local ANSI code page; UTF-8 reads produce mojibake on Chinese annotations), parses the
//      rows and deletes all four temp files.
//
// This file keeps only what is specific to the flat (E5) dump: the request parameters, the
// LispWire.ParseFlat call and the LispRecord -> JMap mapping (field names/order are the output
// contract of `read`/`query`).
//
// Read-only by construction: only ssget/entget/open/write-line are used. No setvar beyond CMDECHO,
// no command, no modification of the drawing. The generated script also installs an AutoLISP *error*
// handler that restores CMDECHO, and an unfinished injection keeps its temp files as *.orphan
// instead of deleting files CAD may still be writing (M3-E6 P1-4.1).

using System;
using System.Collections.Generic;

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

        /// <summary>Scan model space through LISP and return {entities:[...], count:int, textCount:int}.
        /// Pipeline in LispScan.Run; this method is the request + the flat-dump parse callback.</summary>
        internal static JMap Scan(object doc, Ctx ctx, string layer, string cadTypes, int timeoutMs, out JMap meta)
        {
            meta = new JMap();
            // request: E5 flat dump, "scan-" temp prefix (the orphan sweep keys on it), and the exact
            // wording of the documented errors for this scan.
            LispScan.Request req = LispScan.NewRequest("scan", false, State.Dir(),
                LispScan.BuildSsFilter(cadTypes, layer), timeoutMs);
            req.BusyLabel = "LISP file scan";
            req.TimeoutLabel = "LISP scan";
            req.BusyHint = "retry when the command prompt is idle";
            req.PollutedHint = "send ESC in CAD, or let the tool retry";

            JMap runMeta;
            string phase;
            LispScan.Result scan = LispGate.Run(doc, ctx, req, out runMeta, out phase);

            // parse callback: E5 four-field rows -> the entity maps the COM path would have produced
            int textCount;
            List<LispRecord> parsed = LispWire.ParseFlat(scan.Text, out textCount);
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
            LispScan.AddMeta(runMeta, scan);   // after the parse: parseMs/totalMs keep their meaning
            meta = runMeta;
            return result;
        }
    }
}
