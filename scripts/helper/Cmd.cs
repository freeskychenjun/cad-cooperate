// Cmd.cs - SendCommand / LISP bridge with the discipline from com-probe.md.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 2, 2026-11-05; pure move). Every
// command string stays well below 2 KB and its LASTPROMPT is checked afterwards; LISP
// results come back through GetLispVariable (scalars only). No logic, visibility, sequence
// number or message text was changed.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ZwcHelper
{
    // ======================================================================================
    // SendCommand / LISP bridge with the discipline from com-probe.md
    // ======================================================================================
    internal static class Cmd
    {
        private static int _seq = 0;

        public static string GetVariable(object doc, string name)
        {
            object v = Com.TryCall(doc, "GetVariable", name);
            string s = Com.Str(v);
            return s;
        }

        public static string LastPrompt(object doc)
        {
            string p = GetVariable(doc, "LASTPROMPT");
            return p == null ? "" : p;
        }

        /// <summary>A prompt containing "Missing:" or ending in '>' means CAD is waiting for more
        /// input - every later SendCommand would be swallowed as a continuation line.</summary>
        public static bool PromptPolluted(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            if (prompt.IndexOf("Missing:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // our own artifact, not a waiting prompt: cancelling AutoCAD's SECURELOAD dialog leaves
            // "; Error: File load canceled: <path>" / the Chinese equivalent in LASTPROMPT
            if (IsCancelledLoad(prompt)) return false;
            return prompt.TrimEnd().EndsWith(">", StringComparison.Ordinal);
        }

        /// <summary>True when LASTPROMPT reports a cancelled LISP load (AutoCAD SECURELOAD dialog
        /// answered with "Do Not Load"). Treated as clean: nothing is waiting for input.</summary>
        public static bool IsCancelledLoad(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return false;
            if (prompt.IndexOf("\u6587\u4EF6\u52A0\u8F7D\u5DF2\u53D6\u6D88", StringComparison.Ordinal) >= 0) return true;   // file load canceled (zh)
            if (prompt.IndexOf("\u52A0\u8F7D\u5DF2\u53D6\u6D88", StringComparison.Ordinal) >= 0) return true;               // load canceled (zh)
            if (prompt.IndexOf("load canceled", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (prompt.IndexOf("load cancelled", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static void AssertClean(object doc, string what)
        {
            string p = LastPrompt(doc);
            if (PromptPolluted(p))
                throw new ZwcError("CMD_STATE_POLLUTED",
                    "CAD command line is stuck before '" + what + "' (LASTPROMPT = \"" + p + "\")",
                    "press ESC in CAD (or run _.U) until the prompt is back to normal, then retry",
                    null);
        }

        /// <summary>Send one command string. Enforces the <=2 KB ceiling and re-checks LASTPROMPT.
        /// The string must be a single line: SendCommand turns "\n" into Enter.</summary>
        public static void Send(object doc, string command, Ctx ctx, string what)
        {
            if (command == null) throw new ZwcError("BAD_ARGS", "empty command for " + what, null, null);
            // a single trailing newline is the "press Enter" that submits the command; anything
            // else would be split into several commands by CAD
            string body = command;
            while (body.EndsWith("\n", StringComparison.Ordinal) || body.EndsWith("\r", StringComparison.Ordinal))
                body = body.Substring(0, body.Length - 1);
            if (body.IndexOf('\n') >= 0 || body.IndexOf('\r') >= 0)
                throw new ZwcError("BAD_ARGS", "internal: multi-line SendCommand payload for " + what, null, null);
            if (command.Length > 2000)
                throw new ZwcError("SENDCMD_TOO_LONG", "internal: " + command.Length
                    + " chars would be silently dropped by CAD (limit ~2048)", null, null);
            AssertClean(doc, what);
            Com.Call(doc, "SendCommand", command);
            string p = LastPrompt(doc);
            if (PromptPolluted(p))
                throw new ZwcError("CMD_STATE_POLLUTED",
                    "after '" + what + "' the command line is stuck (LASTPROMPT = \"" + p + "\")",
                    "the LISP payload was rejected; check brackets/quotes, then press ESC in CAD", p);
        }

        /// <summary>Run an AutoLISP expression that ends by (setq &lt;var&gt; ...), then read the scalar
        /// back through GetLispVariable (lists are E_INVALIDARG, so only scalars are ever returned).</summary>
        public static string EvalLisp(object doc, string expr, Ctx ctx, string what, int timeoutMs)
        {
            _seq++;
            string varName = "ZWC_R" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + "_" + _seq;
            string full = "(progn " + expr + " (setq " + varName + " ZWC_OUT))";
            if (!Balanced(full))
                throw new ZwcError("LISP_UNBALANCED", "internal: unbalanced LISP payload for " + what, null, full);
            // clear any stale value first
            Com.TryCall(doc, "SetLispVariable", varName, "");
            Send(doc, full, ctx, what);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                object v = Com.TryCall(doc, "GetLispVariable", varName);
                if (v != null && !(v is bool))
                {
                    string s = Com.Str(v);
                    if (s != null) return s;
                }
                Thread.Sleep(20);
            }
            throw new ZwcError("LISP_TIMEOUT", "LISP result variable " + varName + " never got set by " + what,
                "CAD may still be processing the payload; re-run status to see LASTPROMPT", null);
        }

        /// <summary>Paren/quote balance check, so a typo can never poison the CAD command state.</summary>
        public static bool Balanced(string s)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth < 0) return false; }
            }
            return depth == 0 && !inStr;
        }
    }
}
