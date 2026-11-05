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
//   R|handle|assoc40        (CIRCLE/ARC radius)
//   CL|handle|assoc70       (polyline flags; bit 1 = closed)
//   __DONE__|<n>
// '|' inside text is escaped as '\|' and CR/LF become spaces; the reader maps '\|' back to '|'.
//
// Injection safety (M3-E6 P1-4.1, after the real incident where a dying script left the user's
// CMDECHO at 0):
//   * the script saves the previous *error* handler and installs one that closes the dump file
//     handles, restores CMDECHO and re-installs the old handler - so a mid-way error inside CAD can
//     no longer leave the command line silent; the normal path restores *error* too;
//   * a __START__ sentinel is written into a separate .start file before any heavy work, so the
//     helper can tell "the injected command never ran" from "it ran and is stuck";
//   * only CMDECHO may be written through setvar (the read-only safety gate below enforces it).

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CadHelper
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
        /// (command/entdel/entmod/entmake/erase/SelectionSets/Delete/vla-/vlax-/...).
        /// `defun` is allowed only for the generated *error* handler, and its parameter list is skipped
        /// (the symbols in there are variable names, not function calls; *error* itself is a symbol,
        /// not a call, so it is never tokenized as a function).</summary>
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
                "sslength", "entget", "entnext", "open", "close", "write-line", "equal", "+", "-", "1+", "1-",
                "=", "/=", "<", "<=", ">", ">=", "quote", "null", "defun"
            };
            if (src == null) return "empty script";
            StringBuilder token = new StringBuilder();
            int line = 1;
            bool inString = false, inComment = false;
            bool skipDefunParams = false;
            int skipDepth = 0;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n') { line++; inComment = false; token.Length = 0; continue; }
                if (skipDefunParams)
                {
                    if (c == '(') skipDepth++;
                    else if (c == ')') { skipDepth--; if (skipDepth == 0) skipDefunParams = false; }
                    continue;
                }
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
                        if (sym == "defun") { skipDefunParams = true; skipDepth = 0; }
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
            if (!IsAscii(src)) throw new CadError("LISP_TEMPLATE_NOT_ASCII",
                "the generated LISP script contains non-ASCII characters",
                "keep non-ASCII out of the script; Chinese text belongs to the dumped data", null);
            string err = BalanceError(src);
            if (err != null) throw new CadError("LISP_TEMPLATE_UNBALANCED",
                "the generated LISP script is not balanced: " + err,
                "fix the template generator; nothing was injected", null);
            string unsafeErr = SafetyError(src);
            if (unsafeErr != null) throw new CadError("LISP_TEMPLATE_UNSAFE",
                "the generated LISP script failed the read-only safety gate: " + unsafeErr,
                "only ssget/entget/assoc/open/write-line/close/... are allowed; never command/entdel/SelectionSets/Delete", null);
            File.WriteAllText(path, src, new ASCIIEncoding());   // no preamble => no BOM
        }

        /// <summary>Append the inline sanitizer used for text (CR/LF -> space, '|' -> '\|') for one
        /// variable: LISP has no cheap string library here, so the loop is inlined per field.</summary>
        private static void AppendEscape(StringBuilder sb, string srcVar, string dstVar)
        {
            sb.Append("(setq " + dstVar + " \"\" _cad_j 1) ");
            sb.Append("(while (<= _cad_j (strlen " + srcVar + ")) ");
            sb.Append("(setq _cad_c (substr " + srcVar + " _cad_j 1)) ");
            sb.Append("(if (or (= _cad_c (chr 13)) (= _cad_c (chr 10))) (setq _cad_c \" \")) ");
            sb.Append("(if (= _cad_c \"|\") (setq _cad_c \"\\\\|\")) ");
            sb.Append("(setq " + dstVar + " (strcat " + dstVar + " _cad_c)) ");
            sb.Append("(setq _cad_j (1+ _cad_j))) ");
        }

        internal static string PathForLisp(string path)
        {
            if (path == null) return "";
            return path.Replace("\\", "/").Replace("\"", "");
        }

        /// <summary>Backward-compatible overload (used by the lispgen debug command): the __START__
        /// sentinel goes next to the dump file.</summary>
        internal static string BuildDump(bool geom, string txtPath, string donePath, string ssFilter)
        {
            return BuildDump(geom, txtPath, donePath, (txtPath == null ? null : txtPath + ".start"), ssFilter);
        }

        /// <summary>Build a dump script. geom = false writes the E5 four-field records only;
        /// geom = true also writes points / vertices / elevation / height / radius / flags.
        /// startPath (may be null) is the two-phase sentinel file written before any heavy work.</summary>
        internal static string BuildDump(bool geom, string txtPath, string donePath, string startPath, string ssFilter)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("(progn ");
            // --- P1-4.1: save CMDECHO + the previous *error* handler, then install one that restores
            // both. *error* is a symbol assignment, not a call; the safety gate skips the parameter list.
            sb.Append("(setq _cad_echo (getvar \"CMDECHO\")) ");
            sb.Append("(setq _cad_olderr *error*) ");
            sb.Append("(defun *error* (msg) ");
            sb.Append("(if _cad_f (close _cad_f)) ");
            sb.Append("(if _cad_fs (close _cad_fs)) ");
            sb.Append("(if _cad_echo (setvar \"CMDECHO\" _cad_echo)) ");
            sb.Append("(setq *error* _cad_olderr) ");
            sb.Append("(princ)) ");
            // --- two-phase sentinel: __START__ lands in its own .start file before the dump begins
            if (!string.IsNullOrEmpty(startPath))
            {
                sb.Append("(setq _cad_fs (open \"").Append(PathForLisp(startPath)).Append("\" \"w\")) ");
                sb.Append("(write-line \"__START__\" _cad_fs) ");
                sb.Append("(close _cad_fs) ");
                sb.Append("(setq _cad_fs nil) ");
            }
            sb.Append("(setvar \"CMDECHO\" 0) ");
            sb.Append("(setq _cad_f (open \"").Append(PathForLisp(txtPath)).Append("\" \"w\")) ");
            sb.Append("(setq _cad_ss (ssget \"X\"").Append(string.IsNullOrEmpty(ssFilter) ? "" : (" " + ssFilter)).Append(")) ");
            sb.Append("(setq _cad_n (if _cad_ss (sslength _cad_ss) 0)) ");
            sb.Append("(setq _cad_i 0) ");
            sb.Append("(setq _cad_cnt 0) ");
            sb.Append("(while (< _cad_i _cad_n) ");
            sb.Append("(setq _cad_e (ssname _cad_ss _cad_i)) ");
            sb.Append("(setq _cad_d (entget _cad_e)) ");
            sb.Append("(setq _cad_h (cdr (assoc 5 _cad_d))) ");
            sb.Append("(setq _cad_t (cdr (assoc 0 _cad_d))) ");
            sb.Append("(setq _cad_l (cdr (assoc 8 _cad_d))) ");
            sb.Append("(if (= _cad_h nil) (setq _cad_h \"\")) ");
            sb.Append("(if (= _cad_t nil) (setq _cad_t \"\")) ");
            sb.Append("(if (= _cad_l nil) (setq _cad_l \"\")) ");
            // sanitize text: CR/LF -> space, '|' -> '\|' (basic substring loop; no vl-* needed)
            sb.Append("(setq _cad_x (cdr (assoc 1 _cad_d))) ");
            sb.Append("(if (= _cad_x nil) (setq _cad_x \"\")) ");
            sb.Append("(setq _cad_xx \"\" _cad_j 1) ");
            sb.Append("(while (<= _cad_j (strlen _cad_x)) ");
            sb.Append("(setq _cad_c (substr _cad_x _cad_j 1)) ");
            sb.Append("(if (or (= _cad_c (chr 13)) (= _cad_c (chr 10))) (setq _cad_c \" \")) ");
            sb.Append("(if (= _cad_c \"|\") (setq _cad_c \"\\\\|\")) ");
            sb.Append("(setq _cad_xx (strcat _cad_xx _cad_c)) ");
            sb.Append("(setq _cad_j (1+ _cad_j))) ");
            sb.Append("(setq _cad_x _cad_xx) ");
            // nil-safe coordinate formatting
            sb.Append("(setq _cad_p (cdr (assoc 10 _cad_d))) ");
            sb.Append("(if (= _cad_p nil) (setq _cad_p (list 0.0 0.0 0.0))) ");
            sb.Append("(setq _cad_x1 (nth 0 _cad_p)) (if (= _cad_x1 nil) (setq _cad_x1 0.0)) ");
            sb.Append("(setq _cad_y1 (nth 1 _cad_p)) (if (= _cad_y1 nil) (setq _cad_y1 0.0)) ");
            sb.Append("(setq _cad_z1 (nth 2 _cad_p)) (if (= _cad_z1 nil) (setq _cad_z1 0.0)) ");
            sb.Append("(setq _cad_pv (list (rtos _cad_x1 2 6) (rtos _cad_y1 2 6) (rtos _cad_z1 2 6))) ");

            if (geom)
            {
                sb.Append("(setq _cad_vs '()) ");
                sb.Append("(foreach _cad_pp _cad_d (if (= (car _cad_pp) 10) (setq _cad_vs (cons (cdr _cad_pp) _cad_vs)))) ");
                sb.Append("(setq _cad_vs (reverse _cad_vs)) ");
                sb.Append("(setq _cad_nv (length _cad_vs)) ");
            }
            else
            {
                sb.Append("(setq _cad_nv 0) ");
            }
            sb.Append("(write-line (strcat \"E|\" _cad_h \"|\" _cad_t \"|\" _cad_l \"|\" _cad_x \"|\" (nth 0 _cad_pv) \"|\" (nth 1 _cad_pv) \"|\" (nth 2 _cad_pv) \"|\" (itoa _cad_nv)) _cad_f) ");

            if (geom)
            {
                // Old-style POLYLINE has exactly one (10 . point) group - (0,0,elevation) - and its real
                // vertices are VERTEX sub-entities dumped further down. Emitting that placeholder as a
                // vertex produced a bogus index 0 plus a duplicate index 0 from the walk.
                sb.Append("(if (not (equal _cad_t \"POLYLINE\")) (progn ");
                sb.Append("(setq _cad_ii 0) ");
                sb.Append("(foreach _cad_pp _cad_vs ");
                sb.Append("(setq _cad_px (nth 0 _cad_pp)) (if (= _cad_px nil) (setq _cad_px 0.0)) ");
                sb.Append("(setq _cad_py (nth 1 _cad_pp)) (if (= _cad_py nil) (setq _cad_py 0.0)) ");
                sb.Append("(setq _cad_pz (nth 2 _cad_pp)) (if (= _cad_pz nil) (setq _cad_pz 0.0)) ");
                sb.Append("(write-line (strcat \"V|\" _cad_h \"|\" (itoa _cad_ii) \"|\" (rtos _cad_px 2 6) \"|\" (rtos _cad_py 2 6) \"|\" (rtos _cad_pz 2 6)) _cad_f) ");
                sb.Append("(setq _cad_ii (1+ _cad_ii))) ");   // closes setq + foreach
                sb.Append(")) ");                            // closes (progn ...) + (if (not ...) ...)
                sb.Append("(setq _cad_p2 (cdr (assoc 11 _cad_d))) ");
                sb.Append("(if (/= _cad_p2 nil) (progn ");
                sb.Append("(setq _cad_p2x (nth 0 _cad_p2)) (if (= _cad_p2x nil) (setq _cad_p2x 0.0)) ");
                sb.Append("(setq _cad_p2y (nth 1 _cad_p2)) (if (= _cad_p2y nil) (setq _cad_p2y 0.0)) ");
                sb.Append("(setq _cad_p2z (nth 2 _cad_p2)) (if (= _cad_p2z nil) (setq _cad_p2z 0.0)) ");
                sb.Append("(write-line (strcat \"P2|\" _cad_h \"|\" (rtos _cad_p2x 2 6) \"|\" (rtos _cad_p2y 2 6) \"|\" (rtos _cad_p2z 2 6)) _cad_f))) ");
                sb.Append("(setq _cad_el (cdr (assoc 38 _cad_d))) ");
                sb.Append("(if (/= _cad_el nil) (write-line (strcat \"ELEV|\" _cad_h \"|\" (rtos _cad_el 2 6)) _cad_f)) ");
                sb.Append("(setq _cad_hh (cdr (assoc 40 _cad_d))) ");
                sb.Append("(if (and (/= _cad_hh nil) (or (= _cad_t \"TEXT\") (= _cad_t \"MTEXT\"))) (write-line (strcat \"H|\" _cad_h \"|\" (rtos _cad_hh 2 6)) _cad_f)) ");
                sb.Append("(if (or (= _cad_t \"CIRCLE\") (= _cad_t \"ARC\")) (if (/= _cad_hh nil) (write-line (strcat \"R|\" _cad_h \"|\" (rtos _cad_hh 2 6)) _cad_f))) ");
                sb.Append("(if (or (= _cad_t \"LWPOLYLINE\") (= _cad_t \"POLYLINE\")) (progn (setq _cad_fl (cdr (assoc 70 _cad_d))) (if (/= _cad_fl nil) (write-line (strcat \"CL|\" _cad_h \"|\" (itoa _cad_fl)) _cad_f)))) ");
                // Old-style 2D/3D POLYLINE: the group-10 sweep above only sees the placeholder
                // (0,0,elevation) because the real vertices are separate VERTEX sub-entities. Walking
                // them with (entnext) was measured WRONG on AutoCAD 2021: the chain crosses SEQEND and
                // continues into the next polyline (an 11-vertex contour came back with 99 vertices),
                // and it is slow (~2 ms per entget, 141k vertices took 12.5 minutes). So the LISP dump
                // only reports the elevation here and LispGeom fills the vertices through COM
                // (AcadPolyline.Coordinates returns the whole triple array in ONE call).
                sb.Append("(setq _cad_af (cdr (assoc 66 _cad_d))) ");
                sb.Append("(if (= _cad_t \"POLYLINE\") (if (/= _cad_z1 nil) (write-line (strcat \"ELEV|\" _cad_h \"|\" (rtos _cad_z1 2 6)) _cad_f))) ");
                // Block attributes are sub-entities too (ATTRIB ... SEQEND) and are gated the same way.
                // Without them a drawing whose elevation labels live inside elevation-point blocks
                // reads as "no text at all".
                sb.Append("(if (and (= _cad_t \"INSERT\") _cad_af (= _cad_af 1)) (progn ");
                sb.Append("(setq _cad_sub (entnext _cad_e) _cad_lim 0) ");
                sb.Append("(while (and _cad_sub (< _cad_lim 200000) (setq _cad_sd (entget _cad_sub)) (equal (cdr (assoc 0 _cad_sd)) \"ATTRIB\")) ");
                sb.Append("(setq _cad_lim (1+ _cad_lim)) ");
                sb.Append("(if (equal (cdr (assoc 0 _cad_sd)) \"ATTRIB\") (progn ");
                sb.Append("(setq _cad_tag (cdr (assoc 2 _cad_sd))) (if (= _cad_tag nil) (setq _cad_tag \"\")) ");
                sb.Append("(setq _cad_val (cdr (assoc 1 _cad_sd))) (if (= _cad_val nil) (setq _cad_val \"\")) ");
                AppendEscape(sb, "_cad_tag", "_cad_te");
                AppendEscape(sb, "_cad_val", "_cad_ve");
                sb.Append("(setq _cad_sp (cdr (assoc 10 _cad_sd))) ");
                sb.Append("(setq _cad_sx (nth 0 _cad_sp)) (if (= _cad_sx nil) (setq _cad_sx 0.0)) ");
                sb.Append("(setq _cad_sy (nth 1 _cad_sp)) (if (= _cad_sy nil) (setq _cad_sy 0.0)) ");
                sb.Append("(setq _cad_sz (nth 2 _cad_sp)) (if (= _cad_sz nil) (setq _cad_sz 0.0)) ");
                sb.Append("(write-line (strcat \"A|\" _cad_h \"|\" _cad_te \"|\" _cad_ve \"|\" (rtos _cad_sx 2 6) \"|\" (rtos _cad_sy 2 6) \"|\" (rtos _cad_sz 2 6)) _cad_f))) ");
                sb.Append("(setq _cad_sub (entnext _cad_sub))))) ");
            }

            sb.Append("(setq _cad_cnt (1+ _cad_cnt)) ");
            sb.Append("(setq _cad_i (1+ _cad_i))) ");
            sb.Append("(close _cad_f) ");
            sb.Append("(setq _cad_f nil) ");
            sb.Append("(setq _cad_fs (open \"").Append(PathForLisp(donePath)).Append("\" \"w\")) ");
            sb.Append("(write-line (strcat \"__DONE__|\" (itoa _cad_cnt)) _cad_fs) ");
            sb.Append("(close _cad_fs) ");
            sb.Append("(setq _cad_fs nil) ");
            sb.Append("(setvar \"CMDECHO\" _cad_echo) ");
            sb.Append("(setq *error* _cad_olderr) ");
            sb.Append("(princ))");
            return sb.ToString();
        }
    }
}
