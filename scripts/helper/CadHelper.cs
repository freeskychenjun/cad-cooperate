// CadHelper.cs - cad-cooperate M1 helper (one-shot process, [STAThread], COM late binding).
//
// Contract (DESIGN-M1.md section 2/3):
//   CadHelper.exe --request <req.json> --response <resp.json>
//   request  { "cmd":"section", "doc":"<图纸目录>\\a.dwg", "app":"zwcad", "args":{...} }
//   response { ok, cmd, doc, result, warnings[{code,message}], undoToken, elapsedMs, app }
//            ok:false also carries error{code,message,hint,detail}
//
// Hard rules taken from research/com-probe.md and never violated here:
//   * COM is late bound only (Type.GetTypeFromProgID / Marshal.GetActiveObject + InvokeMember).
//     No interop assembly reference, no `dynamic` (keeps the csc command line minimal).
//   * Document.EvaluateLispExpression / Application.Eval are E_NOTIMPL shells - never used for
//     real work; doctor probes the Document one only to report its HRESULT (that call is safe).
//   * LISP results come back through GetLispVariable (scalars only) - no lists, no arrays.
//   * Every SendCommand string stays well below 2 KB and its LASTPROMPT is checked afterwards
//     (a "Missing:" prompt or a trailing ">" continuation means the command state is polluted).
//   * A burst of writes is wrapped in StartUndoMark/EndUndoMark so the user gets one Ctrl+Z.
//   * Temporary files only under %TEMP%\cad-cooperate\ (DGS encrypts .dxf/.dwg by extension).
//   * Instances we started are closed with Close(false) + Quit(); nothing is ever saved.
//
// Chinese literals are written as \uXXXX escapes on purpose so the source file stays pure ASCII
// (PowerShell 5.1 / csc round trips through ANSI code pages are the classic way to corrupt them).

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CadHelper
{
    // ======================================================================================
    // Entry point
    // ======================================================================================
    internal static class Program
    {
        /// <summary>Dispatch one request JSON and return the response JSON. Used by the one-shot
        /// path (file in / file out) and by the daemon (pipe in / pipe out), so both transports
        /// receive byte-identical request/response documents.</summary>
        internal static string HandleRequestText(string reqJson)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Ctx ctx = new Ctx();
            JMap req = null;
            JMap resultOut = null;
            string cmd = "";
            bool ok = false;
            CadError zerr = null;
            Exception uerr = null;

            try
            {
                object parsed = Json.Parse(reqJson);
                req = parsed as JMap;
                if (req == null) throw new CadError("BAD_ARGS", "request JSON must be an object", null, null);
                cmd = req.Str("cmd", "");
                JMap argMap = req.Map("args");
                if (argMap == null) argMap = new JMap();
                ctx.PlanOnly = argMap.Bool("dryRun", false)
                    || string.Equals(argMap.Str("mode", ""), "plan", StringComparison.OrdinalIgnoreCase);

                switch (cmd)
                {
                    case "ping": resultOut = new JMap().Add("pid", Process.GetCurrentProcess().Id); break;
                    case "doctor": resultOut = Commands.Doctor(req, argMap, ctx); break;
                    case "status": resultOut = Commands.Status(req, argMap, ctx); break;
                    case "open": resultOut = Commands.Open(req, argMap, ctx); break;
                    case "info": resultOut = Commands.Info(req, argMap, ctx); break;
                    case "read": resultOut = Commands.Read(req, argMap, ctx); break;
                    case "section": resultOut = Stability.RunWrite(req, argMap, ctx, "section", delegate { return Commands.SectionCmd(req, argMap, ctx); }); break;
                    case "undo": resultOut = Stability.RunWrite(req, argMap, ctx, "undo", delegate { return Commands.UndoCmd(req, argMap, ctx); }); break;
                    case "hint": resultOut = Commands.HintCmd(req, argMap, ctx); break;
                    case "trust": resultOut = Commands.TrustCmd(req, argMap, ctx); break;
                    case "show": resultOut = Commands.ShowCmd(req, argMap, ctx); break;
                    case "lispgen": resultOut = Commands.LispGenCmd(req, argMap, ctx); break;
                    case "finish": resultOut = Commands.Finish(req, argMap, ctx); break;
                    case "entity": resultOut = Stability.RunWrite(req, argMap, ctx, "entity", delegate { return Edits.EntityCmd(req, argMap, ctx); }); break;
                    case "query": resultOut = Query.QueryCmd(req, argMap, ctx); break;
                    case "snapshot": resultOut = Query.SnapshotCmd(req, argMap, ctx); break;
                    case "diff": resultOut = Query.DiffCmd(req, argMap, ctx); break;
                    case "xprobe": resultOut = Edits.XProbeCmd(req, argMap, ctx); break;
                    default:
                        throw new CadError("BAD_ARGS", "unknown cmd '" + cmd + "'; expected doctor|status|open|info|read|section|undo|hint|finish|entity|query|snapshot|diff", null, null);
                }
                try { Commands.ReapplyCaption(ctx); } catch (Exception) { }
                ok = true;
            }
            catch (CadError e) { zerr = e; resultOut = ctx.Result; }
            catch (Exception e) { uerr = e; resultOut = ctx.Result; }

            JMap root = new JMap();
            root.Add("ok", ok);
            root.Add("cmd", cmd);
            root.Add("doc", ctx.DocMap);
            root.Add("result", resultOut);
            root.Add("warnings", ctx.Warnings);
            root.Add("undoToken", ctx.UndoToken);
            root.Add("elapsedMs", sw.ElapsedMilliseconds);
            root.Add("app", ctx.AppMap);
            if (!ok)
            {
                JMap err = new JMap();
                if (zerr != null)
                {
                    err.Add("code", zerr.Code);
                    err.Add("message", zerr.Message);
                    err.Add("hint", zerr.Hint);
                    err.Add("detail", zerr.Detail);
                }
                else
                {
                    err.Add("code", "UNEXPECTED");
                    err.Add("message", uerr == null ? "unknown failure" : (uerr.GetType().Name + ": " + uerr.Message));
                    err.Add("hint", "this is a bug in the helper; re-run doctor and report the message");
                    err.Add("detail", uerr == null ? null : uerr.ToString());
                }
                root.Add("error", err);
            }
            return Json.Write(root);
        }

        [STAThread]
        internal static int Main(string[] args)
        {
            MessageFilter.Register();
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception) { }

            string reqPath = null, respPath = null, servePipe = null;
            int idleMs = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--request" || a == "-request") && i + 1 < args.Length) reqPath = args[++i];
                else if ((a == "--response" || a == "-response") && i + 1 < args.Length) respPath = args[++i];
                else if ((a == "--serve" || a == "-serve") && i + 1 < args.Length) servePipe = args[++i];
                else if (a == "--idle-ms" && i + 1 < args.Length) int.TryParse(args[++i], out idleMs);
            }

            if (!string.IsNullOrEmpty(servePipe))
                return Daemon.Run(servePipe, idleMs);

            Stopwatch sw = Stopwatch.StartNew();
            string text;
            try
            {
                if (reqPath == null || respPath == null)
                    throw new CadError("BAD_ARGS", "usage: CadHelper.exe --request <req.json> --response <resp.json> | --serve <pipe>", null, null);
                if (!File.Exists(reqPath))
                    throw new CadError("BAD_ARGS", "request file not found: " + reqPath, null, null);
                text = HandleRequestText(File.ReadAllText(reqPath, new UTF8Encoding(false)));
            }
            catch (CadError e)
            {
                JMap err = new JMap();
                err.Add("ok", false);
                err.Add("cmd", "");
                err.Add("error", new JMap().Add("code", e.Code).Add("message", e.Message).Add("hint", e.Hint));
                err.Add("elapsedMs", sw.ElapsedMilliseconds);
                text = Json.Write(err);
            }

            try { File.WriteAllText(respPath, text, new UTF8Encoding(false)); }
            catch (Exception e)
            {
                try { Console.Error.WriteLine("cannot write response file: " + e.Message); }
                catch (Exception) { }
                return 2;
            }
            try { Console.Out.Write(text); }
            catch (Exception) { }
            return text.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? 0 : 2;
        }
    }
}
