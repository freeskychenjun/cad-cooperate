// LispTemplate.cs - M3-E6: generate LISP dump scripts and validate them before injection.
//
// Lesson from the real 36,558-entity job: a hand-written .lsp must be ASCII/no-BOM (a BOM makes CAD
// answer "Error: the input list is defective") and its parentheses must balance (otherwise CAD
// answers a syntax error and the dump silently never happens). This class generates the script from
// one template and refuses to inject anything that fails the checks.
//
// The generated script uses ONLY basic AutoLISP (ssget/entget/assoc/strcat/rtos/itoa/strlen/substr/
// while/foreach/if/progn) - no vl-* functions, because Visual LISP extensions are not guaranteed on
// every ZWCAD build. 2D LISP points (LWPOLYLINE group 10) have no Z slot, so every coordinate is
// formatted through a nil-safe helper that substitutes 0.0.
//
// Wire format (one record per line, '|' separated; text never spans lines):
//   E|handle|type|layer|text|x|y|z|nverts
//   V|handle|i|x|y|z
//   P2|handle|x|y|z
//   ELEV|handle|assoc38     (contour polylines carry their height only here)
//   H|handle|assoc40        (TEXT/MTEXT height)
//   CL|handle|assoc70       (polyline flags; bit 1 = closed)
//   __DONE__|<n>
// '|' inside text is escaped as '\|' and CR/LF become spaces; the reader maps '\|' back to '|'.

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZwcHelper
{
    internal static class LispTemplate
    {
        internal static bool IsAscii(string src)
        {
            if (src == null) return false;
            foreach (char c in src) if (c > 127) return false;
            return true;
        }

        /// <summary>Parenthesis balance check that ignores parentheses inside strings ("...") and
        /// line comments (; ...). Returns null when balanced, else a reason.</summary>
        internal static string BalanceError(string src)
        {
            if (src == null || src.Length == 0) return "empty script";
            int depth = 0;
            bool inString = false;
            bool inComment = false;
            int line = 1;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n') { line++; inComment = false; continue; }
                if (inComment) continue;
                if (inString)
                {
                    if (c == '\\' && i + 1 < src.Length) { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == ';') { inComment = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth < 0) return "unbalanced ')' at line " + line; }
            }
            if (inString) return "unterminated string literal";
            if (depth != 0) return "unbalanced parentheses: depth " + depth + " at end";
            return null;
        }

        /// <summary>Read-only safety gate for every generated script (M3-E6 hard constraint after the
        /// SelectionSet.Delete incident): only whitelisted read-only AutoLISP functions are allowed,
        /// `setvar` may only touch CMDECHO, and the selection-set / modify family is rejected outright
        /// (command/entdel/entmod/entmake/erase/SelectionSets/Delete/vla-/vlax-/...).</summary>
        internal static string SafetyError(string src)
        {
            string[] forbidden = new string[] {
                "command", "entdel", "entmod", "entmake", "entmakex", "erase",
                "selectionsets", "selectionset", "delete", "vla-", "vlax-", "vlr-", "vl-load",
                "vl-file-delete", "vl-file-rename", "undo", "_.u", "saveas", "qsave",
                "break", "trim", "extend", "explode", "pedit", "-layer", "-insert", "xref"
            };
            string[] allowed = new string[] {
                "progn", "setq", "while", "foreach", "if", "and", "or", "not", "car", "cdr",
                "assoc", "nth", "length", "reverse", "cons", "list", "strcat", "rtos", "itoa",
                "strlen", "substr", "chr", "princ", "getvar", "setvar", "ssget", "ssname",
                "sslength", "entget", "open", "close", "write-line", "+", "-", "1+", "1-",
                "=", "/=", "<", "<=", ">", ">=", "quote", "null"
            };
            if (src == null) return "empty script";
            StringBuilder token = new StringBuilder();
            int line = 1;
            bool inString = false, inComment = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n') { line++; inComment = false; token.Length = 0; continue; }
                if (inComment) continue;
                if (inString)
                {
                    if (c == '\\' && i + 1 < src.Length) { i++; continue; }
                    if (c == '"') { inString = false; }
                    continue;
                }
                if (c == ';') { inComment = true; continue; }
                if (c == '"') { inString = true; token.Length = 0; continue; }
                if (c == '(')
                {
                    token.Length = 0;
                    int j = i + 1;
                    while (j < src.Length && src[j] != ' ' && src[j] != '(' && src[j] != ')' && src[j] != '"' && src[j] != '\n')
                    {
                        token.Append(src[j]);
                        j++;
                    }
                    string sym = token.ToString().ToLowerInvariant();
                    if (sym.Length > 0)
                    {
                        foreach (string bad in forbidden)
                            if (sym.IndexOf(bad, StringComparison.Ordinal) >= 0)
                                return "forbidden function '" + sym + "' (read-only scripts only) at line " + line;
                        bool ok = false;
                        foreach (string good in allowed) if (sym == good) { ok = true; break; }
                        if (!ok)
                        {
                            char first = sym[0];
                            if (!(char.IsDigit(first) || first == '.' || first == '-' || first == '+')) 
                                return "unknown symbol '" + sym + "' outside the read-only whitelist at line " + line;
                        }
                    }
                    continue;
                }
                if (c == ')') { token.Length = 0; continue; }
            }
            // setvar: CMDECHO only (saved/restored by the generated script)
            MatchCollection setvars = Regex.Matches(src, "setvar\\s+\"([^\"]+)\"");
            foreach (Match m in setvars)
            {
                if (!string.Equals(m.Groups[1].Value, "CMDECHO", StringComparison.OrdinalIgnoreCase))
                    return "setvar on '" + m.Groups[1].Value + "' is not allowed (only CMDECHO may be saved/restored)";
            }
            return null;
        }
        internal static void WriteScript(string path, string src)
        {
            if (!IsAscii(src)) throw new ZwcError("LISP_TEMPLATE_NOT_ASCII",
                "the generated LISP script contains non-ASCII characters",
                "keep non-ASCII out of the script; Chinese text belongs to the dumped data", null);
            string err = BalanceError(src);
            if (err != null) throw new ZwcError("LISP_TEMPLATE_UNBALANCED",
                "the generated LISP script is not balanced: " + err,
                "fix the template generator; nothing was injected", null);
            string unsafeErr = SafetyError(src);
            if (unsafeErr != null) throw new ZwcError("LISP_TEMPLATE_UNSAFE",
                "the generated LISP script failed the read-only safety gate: " + unsafeErr,
                "only ssget/entget/assoc/open/write-line/close/... are allowed; never command/entdel/SelectionSets/Delete", null);
            File.WriteAllText(path, src, new ASCIIEncoding());   // no preamble => no BOM
        }

        internal static string PathForLisp(string path)
        {
            if (path == null) return "";
            return path.Replace("\\", "/").Replace("\"", "");
        }

        /// <summary>Build a dump script. geom = false writes the E5 four-field records only;
        /// geom = true also writes points / vertices / elevation / height / flags.</summary>
        internal static string BuildDump(bool geom, string txtPath, string donePath, string ssFilter)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("(progn ");
            sb.Append("(setq _zwc_echo (getvar \"CMDECHO\")) ");
            sb.Append("(setvar \"CMDECHO\" 0) ");
            sb.Append("(setq _zwc_f (open \"").Append(PathForLisp(txtPath)).Append("\" \"w\")) ");
            sb.Append("(setq _zwc_ss (ssget \"X\"").Append(string.IsNullOrEmpty(ssFilter) ? "" : (" " + ssFilter)).Append(")) ");
            sb.Append("(setq _zwc_n (if _zwc_ss (sslength _zwc_ss) 0)) ");
            sb.Append("(setq _zwc_i 0) ");
            sb.Append("(setq _zwc_cnt 0) ");
            sb.Append("(while (< _zwc_i _zwc_n) ");
            sb.Append("(setq _zwc_e (ssname _zwc_ss _zwc_i)) ");
            sb.Append("(setq _zwc_d (entget _zwc_e)) ");
            sb.Append("(setq _zwc_h (cdr (assoc 5 _zwc_d))) ");
            sb.Append("(setq _zwc_t (cdr (assoc 0 _zwc_d))) ");
            sb.Append("(setq _zwc_l (cdr (assoc 8 _zwc_d))) ");
            sb.Append("(if (= _zwc_h nil) (setq _zwc_h \"\")) ");
            sb.Append("(if (= _zwc_t nil) (setq _zwc_t \"\")) ");
            sb.Append("(if (= _zwc_l nil) (setq _zwc_l \"\")) ");
            // sanitize text: CR/LF -> space, '|' -> '\|' (basic substring loop; no vl-* needed)
            sb.Append("(setq _zwc_x (cdr (assoc 1 _zwc_d))) ");
            sb.Append("(if (= _zwc_x nil) (setq _zwc_x \"\")) ");
            sb.Append("(setq _zwc_xx \"\" _zwc_j 1) ");
            sb.Append("(while (<= _zwc_j (strlen _zwc_x)) ");
            sb.Append("(setq _zwc_c (substr _zwc_x _zwc_j 1)) ");
            sb.Append("(if (or (= _zwc_c (chr 13)) (= _zwc_c (chr 10))) (setq _zwc_c \" \")) ");
            sb.Append("(if (= _zwc_c \"|\") (setq _zwc_c \"\\\\|\")) ");
            sb.Append("(setq _zwc_xx (strcat _zwc_xx _zwc_c)) ");
            sb.Append("(setq _zwc_j (1+ _zwc_j))) ");
            sb.Append("(setq _zwc_x _zwc_xx) ");
            // nil-safe coordinate formatting
            sb.Append("(setq _zwc_p (cdr (assoc 10 _zwc_d))) ");
            sb.Append("(if (= _zwc_p nil) (setq _zwc_p (list 0.0 0.0 0.0))) ");
            sb.Append("(setq _zwc_x1 (nth 0 _zwc_p)) (if (= _zwc_x1 nil) (setq _zwc_x1 0.0)) ");
            sb.Append("(setq _zwc_y1 (nth 1 _zwc_p)) (if (= _zwc_y1 nil) (setq _zwc_y1 0.0)) ");
            sb.Append("(setq _zwc_z1 (nth 2 _zwc_p)) (if (= _zwc_z1 nil) (setq _zwc_z1 0.0)) ");
            sb.Append("(setq _zwc_pv (list (rtos _zwc_x1 2 6) (rtos _zwc_y1 2 6) (rtos _zwc_z1 2 6))) ");

            if (geom)
            {
                sb.Append("(setq _zwc_vs '()) ");
                sb.Append("(foreach _zwc_pp _zwc_d (if (= (car _zwc_pp) 10) (setq _zwc_vs (cons (cdr _zwc_pp) _zwc_vs)))) ");
                sb.Append("(setq _zwc_vs (reverse _zwc_vs)) ");
                sb.Append("(setq _zwc_nv (length _zwc_vs)) ");
            }
            else
            {
                sb.Append("(setq _zwc_nv 0) ");
            }
            sb.Append("(write-line (strcat \"E|\" _zwc_h \"|\" _zwc_t \"|\" _zwc_l \"|\" _zwc_x \"|\" (nth 0 _zwc_pv) \"|\" (nth 1 _zwc_pv) \"|\" (nth 2 _zwc_pv) \"|\" (itoa _zwc_nv)) _zwc_f) ");

            if (geom)
            {
                sb.Append("(setq _zwc_ii 0) ");
                sb.Append("(foreach _zwc_pp _zwc_vs ");
                sb.Append("(setq _zwc_px (nth 0 _zwc_pp)) (if (= _zwc_px nil) (setq _zwc_px 0.0)) ");
                sb.Append("(setq _zwc_py (nth 1 _zwc_pp)) (if (= _zwc_py nil) (setq _zwc_py 0.0)) ");
                sb.Append("(setq _zwc_pz (nth 2 _zwc_pp)) (if (= _zwc_pz nil) (setq _zwc_pz 0.0)) ");
                sb.Append("(write-line (strcat \"V|\" _zwc_h \"|\" (itoa _zwc_ii) \"|\" (rtos _zwc_px 2 6) \"|\" (rtos _zwc_py 2 6) \"|\" (rtos _zwc_pz 2 6)) _zwc_f) ");
                sb.Append("(setq _zwc_ii (1+ _zwc_ii))) ");
                sb.Append("(setq _zwc_p2 (cdr (assoc 11 _zwc_d))) ");
                sb.Append("(if (/= _zwc_p2 nil) (progn ");
                sb.Append("(setq _zwc_p2x (nth 0 _zwc_p2)) (if (= _zwc_p2x nil) (setq _zwc_p2x 0.0)) ");
                sb.Append("(setq _zwc_p2y (nth 1 _zwc_p2)) (if (= _zwc_p2y nil) (setq _zwc_p2y 0.0)) ");
                sb.Append("(setq _zwc_p2z (nth 2 _zwc_p2)) (if (= _zwc_p2z nil) (setq _zwc_p2z 0.0)) ");
                sb.Append("(write-line (strcat \"P2|\" _zwc_h \"|\" (rtos _zwc_p2x 2 6) \"|\" (rtos _zwc_p2y 2 6) \"|\" (rtos _zwc_p2z 2 6)) _zwc_f))) ");
                sb.Append("(setq _zwc_el (cdr (assoc 38 _zwc_d))) ");
                sb.Append("(if (/= _zwc_el nil) (write-line (strcat \"ELEV|\" _zwc_h \"|\" (rtos _zwc_el 2 6)) _zwc_f)) ");
                sb.Append("(setq _zwc_hh (cdr (assoc 40 _zwc_d))) ");
                sb.Append("(if (and (/= _zwc_hh nil) (or (= _zwc_t \"TEXT\") (= _zwc_t \"MTEXT\"))) (write-line (strcat \"H|\" _zwc_h \"|\" (rtos _zwc_hh 2 6)) _zwc_f)) ");
                sb.Append("(if (or (= _zwc_t \"CIRCLE\") (= _zwc_t \"ARC\")) (if (/= _zwc_hh nil) (write-line (strcat \"R|\" _zwc_h \"|\" (rtos _zwc_hh 2 6)) _zwc_f))) ");
                sb.Append("(if (or (= _zwc_t \"LWPOLYLINE\") (= _zwc_t \"POLYLINE\")) (progn (setq _zwc_fl (cdr (assoc 70 _zwc_d))) (if (/= _zwc_fl nil) (write-line (strcat \"CL|\" _zwc_h \"|\" (itoa _zwc_fl)) _zwc_f)))) ");
            }

            sb.Append("(setq _zwc_cnt (1+ _zwc_cnt)) ");
            sb.Append("(setq _zwc_i (1+ _zwc_i))) ");
            sb.Append("(close _zwc_f) ");
            sb.Append("(setq _zwc_fs (open \"").Append(PathForLisp(donePath)).Append("\" \"w\")) ");
            sb.Append("(write-line (strcat \"__DONE__|\" (itoa _zwc_cnt)) _zwc_fs) ");
            sb.Append("(close _zwc_fs) ");
            sb.Append("(setvar \"CMDECHO\" _zwc_echo) ");
            sb.Append("(princ))");
            return sb.ToString();
        }
    }
}
