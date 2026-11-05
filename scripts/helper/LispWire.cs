// LispWire.cs - M3-E6/M3-E7: the ONE parser for the LISP text-file wire format.
//
// Before this file the format had TWO readers (LispFile for the E5 four-field dump, LispGeom for the
// E6 geometry dump) and they disagreed in two ways that produced silent wrong data on real drawings:
//
//   P0-1  LispGeom split every row with a bare line.Split('|') and ignored the '\|' escape that the
//         generator writes for text. Any TEXT/MTEXT containing a vertical bar therefore shifted the
//         whole geometry row by one field: x|y|z|nverts moved one slot right, Num() then parsed a
//         non-number as 0.0, i.e. the coordinate silently became 0 and vertices were attached to the
//         wrong entity.
//   P0-2  The generator writes "R|handle|radius" for CIRCLE/ARC, but nothing consumed it (LispFile
//         classified it as an ignored aux record and LispGeom had no branch), so the same drawing
//         read through the LISP path carried no radius while the COM path did.
//
// This file is deliberately dependency-free (BCL only: no JMap, no ZwcError, no COM, no CAD), so it
// can be compiled on its own together with tests\lispwire\*.cs and asserted offline. LispFile.Scan
// and LispGeom.Scan keep only the pipeline + the LispRecord -> JMap mapping.
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
// SplitRecord keeps an escape pair intact; UnescapeField maps '\|' back. Neither ever throws.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZwcHelper
{
    /// <summary>One block attribute read through the LISP dump (A record). Kept BCL-only so the
    /// parser stays testable without COM/JMap.</summary>
    internal sealed class LispAttribute
    {
        public string Tag = "";
        public string Value = "";
        public double X, Y, Z;
    }

    /// <summary>One parsed entity record plus the geometry side-records that belong to it.
    /// Vertices are kept as double[3]; the consumer decides how many components it needs.</summary>
    internal sealed class LispRecord
    {
        public string Kind = "E";        // wire record kind ("E" for an entity row)
        public string Handle = "";
        public string Type = "";         // DXF group-code-0 name as written by the script (TEXT, LINE, ...)
        public string Layer = "";
        public string Text = "";         // already unescaped ('\|' -> '|')
        public double X;
        public double Y;
        public double Z;
        public int Nverts;
        public List<double[]> Vertices;  // V records, in file order
        public double[] P2;              // P2 record (assoc 11: line end / text alignment point)
        public double? Elevation;        // ELEV record (assoc 38)
        public double? Height;           // H record (assoc 40)
        public double? Radius;           // R record (assoc 40 on CIRCLE/ARC)
        public int? Flags;               // CL record (assoc 70)
        public List<LispAttribute> Attributes;   // A records (block attributes, ATTRIB sub-entities)
        /// <summary>Aux kinds in the order they appeared in the dump ("V","P2","ELEV","H","R","CL").
        /// Consumers replay this order so the emitted JSON keeps the pre-refactor key order.</summary>
        public List<string> AuxOrder;
        public int Index;                // 0-based position among the entity rows
    }

    internal static class LispWire
    {
        public const string DoneMarker = "__DONE__";
        public const string StartMarker = "__START__";

        /// <summary>Split one dump row on '|' while honouring the '\|' escape used inside text.
        /// A backslash always consumes the following character, so the two-character escape can never
        /// be mistaken for a separator.</summary>
        public static string[] SplitRecord(string line)
        {
            if (line == null) return new string[0];
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
            return f.ToArray();
        }

        /// <summary>Map the '\|' escape back to a literal '|'. Everything else is left as written.</summary>
        public static string UnescapeField(string s)
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

        /// <summary>Invariant-culture number parse that never throws. Returns false (and 0.0) for an
        /// empty/garbage field, which is what the callers used to do with a local Num() helper.</summary>
        public static bool TryNum(string s, out double d)
        {
            d = 0.0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>Record kinds that only appear in geometry dumps.</summary>
        public static bool IsAuxKind(string kind)
        {
            if (kind == null) return false;
            switch (kind.Trim().ToUpperInvariant())
            {
                case "V":
                case "P2":
                case "ELEV":
                case "H":
                case "R":
                case "CL":
                case "A":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Parse a geometry dump (E6 format). Returns the entity records in dump order with
        /// their side-records folded in; recordCount is the number of "E" rows seen (the sentinel's
        /// own count is checked separately). Aux rows whose handle has no preceding E row are dropped,
        /// exactly like the previous inline parser did.</summary>
        public static List<LispRecord> ParseGeometry(string text, out int recordCount)
        {
            recordCount = 0;
            List<LispRecord> list = new List<LispRecord>();
            if (text == null || text.Length == 0) return list;
            Dictionary<string, LispRecord> firstByHandle = new Dictionary<string, LispRecord>(StringComparer.OrdinalIgnoreCase);
            string[] lines = SplitLines(text);
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                if (line.Length == 0 || line.IndexOf(DoneMarker, StringComparison.Ordinal) >= 0) continue;
                string[] p = SplitRecord(line);
                if (p.Length < 2) continue;
                string kind = p[0];
                string h = p[1];
                if (string.Equals(kind, "E", StringComparison.Ordinal))
                {
                    if (p.Length < 9) continue;
                    LispRecord rec = new LispRecord();
                    rec.Kind = "E";
                    rec.Handle = h;
                    rec.Type = p[2];
                    rec.Layer = p[3];
                    rec.Text = UnescapeField(p[4]);
                    TryNum(p[5], out rec.X);
                    TryNum(p[6], out rec.Y);
                    TryNum(p[7], out rec.Z);
                    int nv;
                    if (int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out nv)) rec.Nverts = nv;
                    rec.Index = list.Count;
                    list.Add(rec);
                    recordCount++;
                    if (!firstByHandle.ContainsKey(h)) firstByHandle[h] = rec;
                    continue;
                }
                LispRecord target;
                if (!firstByHandle.TryGetValue(h, out target)) continue;
                if (string.Equals(kind, "V", StringComparison.Ordinal) && p.Length >= 6)
                {
                    double vx, vy, vz;
                    TryNum(p[3], out vx); TryNum(p[4], out vy); TryNum(p[5], out vz);
                    if (target.Vertices == null) target.Vertices = new List<double[]>();
                    target.Vertices.Add(new double[] { vx, vy, vz });
                    MarkAux(target, "V");
                }
                else if (string.Equals(kind, "P2", StringComparison.Ordinal) && p.Length >= 5)
                {
                    double px, py, pz;
                    TryNum(p[2], out px); TryNum(p[3], out py); TryNum(p[4], out pz);
                    target.P2 = new double[] { px, py, pz };
                    MarkAux(target, "P2");
                }
                else if (string.Equals(kind, "ELEV", StringComparison.Ordinal) && p.Length >= 3)
                {
                    double el; TryNum(p[2], out el);
                    target.Elevation = el;
                    MarkAux(target, "ELEV");
                }
                else if (string.Equals(kind, "H", StringComparison.Ordinal) && p.Length >= 3)
                {
                    double hh; TryNum(p[2], out hh);
                    target.Height = hh;
                    MarkAux(target, "H");
                }
                else if (string.Equals(kind, "R", StringComparison.Ordinal) && p.Length >= 3)
                {
                    double rad; TryNum(p[2], out rad);
                    target.Radius = rad;
                    MarkAux(target, "R");
                }
                else if (string.Equals(kind, "CL", StringComparison.Ordinal) && p.Length >= 3)
                {
                    double fl; TryNum(p[2], out fl);
                    target.Flags = (int)fl;
                    MarkAux(target, "CL");
                }
                else if (string.Equals(kind, "A", StringComparison.Ordinal) && p.Length >= 7)
                {
                    LispAttribute att = new LispAttribute();
                    att.Tag = UnescapeField(p[2]);
                    att.Value = UnescapeField(p[3]);
                    TryNum(p[4], out att.X); TryNum(p[5], out att.Y); TryNum(p[6], out att.Z);
                    if (target.Attributes == null) target.Attributes = new List<LispAttribute>();
                    target.Attributes.Add(att);
                    MarkAux(target, "A");
                }
            }
            return list;
        }

        /// <summary>Parse the E5 four-field dump (handle|type|layer|text in the E row, legacy rows
        /// without the "E|" prefix are still accepted). textCount counts rows whose sanitized text is
        /// non-empty, matching what LispFile.Scan used to report.</summary>
        public static List<LispRecord> ParseFlat(string text, out int textCount)
        {
            textCount = 0;
            List<LispRecord> list = new List<LispRecord>();
            if (text == null || text.Length == 0) return list;
            string[] lines = SplitLines(text);
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                if (line.Length == 0 || line.IndexOf(DoneMarker, StringComparison.Ordinal) >= 0) continue;
                string[] parts = SplitRecord(line);
                if (parts.Length < 4) continue;
                string recKind = parts[0].Trim();
                LispRecord rec = new LispRecord();
                if (string.Equals(recKind, "E", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length < 5) continue;
                    rec.Handle = parts[1].Trim();
                    rec.Type = parts[2].Trim();
                    rec.Layer = parts[3].Trim();
                    rec.Text = UnescapeField(parts[4]);
                }
                else if (IsAuxKind(recKind))
                {
                    continue;   // geometry side-records are LispGeom's business, not this scan
                }
                else
                {
                    // legacy four-field rows: handle|type|layer|text
                    rec.Handle = parts[0].Trim();
                    rec.Type = parts[1].Trim();
                    rec.Layer = parts[2].Trim();
                    rec.Text = parts.Length > 3 ? UnescapeField(parts[3]) : "";
                }
                rec.Text = rec.Text.Replace('\r', ' ').Replace('\n', ' ');
                if (rec.Text.Length > 0) textCount++;
                rec.Index = list.Count;
                list.Add(rec);
            }
            return list;
        }

        private static void MarkAux(LispRecord rec, string kind)
        {
            if (rec.AuxOrder == null) rec.AuxOrder = new List<string>();
            if (!rec.AuxOrder.Contains(kind)) rec.AuxOrder.Add(kind);
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    // ======================================================================================
    // Two-phase sentinel classification (M3-E6 P1-4.1).
    //
    // The generated script writes __START__ into <job>.start before any heavy work and __DONE__|<n>
    // into <job>.done at the end. When the wait loop times out, the pair of booleans tells two very
    // different failures apart:
    //   * no __START__  -> the injected (load ...) never ran: CAD busy / modal dialog / LISP disabled
    //                      -> LISP_NOT_STARTED (a pipeline or command-state problem)
    //   * __START__ but no __DONE__ -> the script is running but stuck -> LISP_FILE_TIMEOUT (keep the
    //                      temp files as *.orphan: CAD may still be writing them)
    // Kept pure (BCL only) so the decision is unit-tested offline; the real end-to-end timeout is not
    // exercised on the user's machine because a failed LISP scan falls back to a ~10 minute COM sweep.
    // ======================================================================================
    internal static class LispSentinelPhase
    {
        public const string NotStarted = "not-started";
        public const string Started = "started";
        public const string Completed = "completed";
        public const string ErrorNotStarted = "LISP_NOT_STARTED";
        public const string ErrorTimeout = "LISP_FILE_TIMEOUT";

        /// <summary>Classify a finished/aborted wait: done wins, otherwise startSeen decides.</summary>
        public static string Classify(bool done, bool startSeen)
        {
            if (done) return Completed;
            return startSeen ? Started : NotStarted;
        }

        /// <summary>Error code for a non-completed phase (LISP_NOT_STARTED or LISP_FILE_TIMEOUT).</summary>
        public static string ErrorCodeFor(string phase)
        {
            return string.Equals(phase, NotStarted, StringComparison.Ordinal) ? ErrorNotStarted : ErrorTimeout;
        }
    }

    // ======================================================================================
    // Temp-file hygiene for the injection pipeline (M3-E6 P1-4.1, after the real CMDECHO incident).
    //
    // A LISP_FILE_TIMEOUT used to delete scan-*.lsp/txt/done in a finally block while the script was
    // still running inside CAD: CAD then re-created the txt/done files (%TEMP% garbage) and, if the
    // script died halfway, the (setvar "CMDECHO" ...) at its end never ran and the user's command line
    // stayed silent. Now an unfinished injection renames its files to *.orphan (evidence) instead of
    // deleting them, and every scan first sweeps leftovers older than 24 h.
    //
    // Kept here (BCL only) so the decision logic can be unit-tested offline without CAD.
    // ======================================================================================
    internal static class LispOrphans
    {
        public const string OrphanSuffix = ".orphan";
        public const string EvidenceDelete = "delete";
        public const string EvidenceOrphan = "orphan";

        /// <summary>"delete" when the injection provably finished, "orphan" otherwise. A timed-out or
        /// failed injection must never delete files CAD may still be writing.</summary>
        public static string EvidenceAction(bool completed)
        {
            return completed ? EvidenceDelete : EvidenceOrphan;
        }

        /// <summary>True for a preserved *.orphan file and for any scan-/geom- pipeline file (the
        /// sweep only removes such files once they are stale, so a concurrent scan is never touched).</summary>
        public static bool IsOrphanCandidate(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            if (fileName.EndsWith(OrphanSuffix, StringComparison.OrdinalIgnoreCase)) return true;
            string n = fileName.ToLowerInvariant();
            return n.StartsWith("scan-") || n.StartsWith("geom-");
        }

        /// <summary>True when lastWriteUtc is older than maxAge relative to nowUtc (negative ages,
        /// e.g. a file written by a peer process with a slightly ahead clock, are not stale).</summary>
        public static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan maxAge)
        {
            TimeSpan age = nowUtc - lastWriteUtc;
            return age > maxAge;
        }

        /// <summary>Rename the given files to "&lt;path&gt;.orphan". Missing files and files locked by
        /// CAD are skipped silently (the file then stays where it is, which is also evidence). Returns
        /// the paths that were actually renamed.</summary>
        public static string[] Preserve(string dir, params string[] paths)
        {
            List<string> renamed = new List<string>();
            if (paths == null) return renamed.ToArray();
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    if (!File.Exists(p)) continue;
                    string dest = p + OrphanSuffix;
                    try { if (File.Exists(dest)) File.Delete(dest); } catch (Exception) { }
                    File.Move(p, dest);
                    renamed.Add(dest);
                }
                catch (Exception) { }
            }
            return renamed.ToArray();
        }

        /// <summary>Delete pipeline leftovers (scan-*/geom-* and *.orphan) older than maxAge in dir.
        /// Every failure is swallowed: this runs on the hot path and must never fail a read.</summary>
        public static void CleanStale(string dir, TimeSpan maxAge)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                DateTime now = DateTime.UtcNow;
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (!IsOrphanCandidate(Path.GetFileName(files[i]))) continue;
                        if (!IsStale(File.GetLastWriteTimeUtc(files[i]), now, maxAge)) continue;
                        File.Delete(files[i]);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>Best-effort delete, never throws.</summary>
        public static void DeleteQuiet(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
