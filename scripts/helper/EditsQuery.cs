// EditsQuery.cs - M3-E2 query / snapshot / diff
//
// These are the read-side primitives that let an agent find things by predicate (not only by the
// handles it already knows) and answer "what did this round change?" without trusting its own
// memory. Everything uses the E1 tiered reader, so a query over a 624-entity drawing stays fast.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZwcHelper
{
    internal static class Query
    {
        // ------------------------------------------------------------------ query
        public static JMap QueryCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Commands.AppKindOf(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            string space = (args.Str("space", null) ?? args.Str("scope", "model") ?? "model").Trim().ToLowerInvariant();
            if (space != "model" && space != "paper" && space != "all") space = "model";
            string mode = (args.Str("mode", "summary") ?? "summary").Trim().ToLowerInvariant();
            if (mode == "handles" || mode == "handle") mode = "handles";
            else if (mode == "geom" || mode == "full") mode = "geom";
            else mode = "summary";

            string layer = args.Str("layer", null);
            string typeList = args.Str("type", null);
            string textRe = args.Str("text", null);
            string xdataSpec = args.Str("xdata", null);
            double[] box = ParseBBox(args.Str("bbox", null));
            int limit = args.Int("limit", 0);
            if (limit <= 0) limit = args.Int("max", 0);
            string cursor = args.Str("cursor", null);

            // text/xdata filters need the geometry tier (summary carries no text)
            string readMode = mode;
            if (!string.IsNullOrEmpty(textRe) || !string.IsNullOrEmpty(xdataSpec)) readMode = "geom";

            List<string> wantKinds = new List<string>();
            if (!string.IsNullOrEmpty(typeList))
                foreach (string part in typeList.Split(','))
                {
                    string k = Commands.KindOf(part.Trim());
                    if (k.Length > 0) wantKinds.Add(k);
                }

            Regex re = null;
            if (!string.IsNullOrEmpty(textRe))
            {
                try { re = new Regex(textRe, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch (Exception e)
                {
                    throw new ZwcError("BAD_ARGS", "-Text is not a valid regex: " + e.Message, "use a plain substring or a valid .NET regex", null);
                }
            }
            string xdataApp = null, xdataValue = null;
            if (!string.IsNullOrEmpty(xdataSpec))
            {
                int colon = xdataSpec.IndexOf(':');
                if (colon >= 0) { xdataApp = xdataSpec.Substring(0, colon).Trim(); xdataValue = xdataSpec.Substring(colon + 1).Trim(); }
                else xdataApp = xdataSpec.Trim();
            }

            Edits.SafeDoc = doc;
            List<object> items = new List<object>();
            int totalMatched = 0;
            bool truncated = false;
            string nextCursor = null;
            int skipCount = 0;
            string skipHandle = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                int ci;
                if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out ci)) skipCount = Math.Max(0, ci);
                else skipHandle = cursor.Trim();
            }
            bool skippingHandle = skipHandle != null;

            string readPath = "com";
            JMap lispMeta = null;
            // M3-E5: for a large model-space query push the type filter down into ssget and read the
            // dump file instead of sweeping COM (the same 0.4 s LISP trick used by `read`). Layer and
            // -Text stay client-side so their exact matching semantics do not change.
            if (!Commands.LispBridgeAvailable(doc) && space == "model" && box == null && xdataApp == null
                && Commands.CountOf(doc, "ModelSpace") > args.Int("lispThreshold", 2000))
            {
                try
                {
                    JMap meta;
                    JMap lf = LispFile.Scan(doc, ctx, null, LispFile.CadTypeFilter(typeList), args.Int("lispTimeoutSec", 60) * 1000, out meta);
                    List<object> all = lf.List("entities");
                    int allCount = all == null ? 0 : all.Count;
                    bool skipping = skipHandle != null;
                    for (int i = 0; i < allCount; i++)
                    {
                        JMap m = all[i] as JMap;
                        if (m == null) continue;
                        if (!string.IsNullOrEmpty(layer))
                        {
                            string actualLayer = m.Str("layer", null);
                            if (actualLayer == null || !string.Equals(actualLayer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        if (wantKinds.Count > 0)
                        {
                            string kind = (m.Str("kind", "") ?? "").ToUpperInvariant();
                            bool ok = false;
                            foreach (string wk in wantKinds) if (kind == wk) { ok = true; break; }
                            if (!ok) continue;
                        }
                        if (re != null)
                        {
                            string mtxt = m.Str("text", null);
                            if (mtxt == null || !re.IsMatch(mtxt)) continue;
                        }
                        if (skipping)
                        {
                            if (string.Equals(m.Str("handle", null), skipHandle, StringComparison.OrdinalIgnoreCase)) skipping = false;
                            continue;
                        }
                        if (skipCount > 0) { skipCount--; continue; }
                        totalMatched++;
                        if (limit > 0 && items.Count >= limit) { truncated = true; continue; }
                        items.Add(Mode(m, mode));
                    }
                    lispMeta = meta;
                    readPath = "lisp-file";
                }
                catch (ZwcError ze)
                {
                    ctx.Warn("LISP_FILE_FALLBACK", "LISP file query failed (" + ze.Code + ": " + ze.Message + "); falling back to COM");
                }
                catch (Exception ex)
                {
                    ctx.Warn("LISP_FILE_FALLBACK", "LISP file query failed (" + Com.Hr(ex) + "); falling back to COM");
                }
            }

            string[] spaces = space == "all" ? new string[] { "model", "paper" } : new string[] { space };
            Commands.ReadState state = new Commands.ReadState();
            if (readPath != "lisp-file") foreach (string sp in spaces)
            {
                string blockName = sp == "paper" ? "PaperSpace" : "ModelSpace";
                object coll = Com.TryGet(doc, blockName);
                if (coll == null) continue;
                int n = Com.I32(Com.TryGet(coll, "Count"));
                int baseIdx = Coll.DetectBase(coll, n);
                for (int k = 0; k < n; k++)
                {
                    object e = Coll.At(coll, baseIdx, k, n);
                    if (e == null) continue;
                    JMap m = Commands.DescribeEntity(e, sp, readMode, ctx, state);
                    if (m == null) continue;
                    if (!Matches(m, e, layer, wantKinds, re, box, xdataApp, xdataValue)) continue;
                    if (skippingHandle)
                    {
                        if (string.Equals(m.Str("handle", null), skipHandle, StringComparison.OrdinalIgnoreCase)) skippingHandle = false;
                        continue;
                    }
                    if (skipCount > 0) { skipCount--; continue; }
                    totalMatched++;
                    if (limit > 0 && items.Count >= limit) { truncated = true; continue; }
                    if (xdataApp != null)
                    {
                        string got2 = Edits.TryGetXData(e, xdataApp);
                        if (got2 != null) { m.Add("xdata", got2); m.Add("xdataPresent", new List<object> { xdataApp }); }
                    }
                    items.Add(Mode(m, mode));
                }
            }

            JMap r = new JMap();
            r.Add("items", items);
            r.Add("total", totalMatched);
            r.Add("returned", items.Count);
            r.Add("truncated", truncated);
            r.Add("nextCursor", truncated ? (object)(skipCount + items.Count) : null);
            r.Add("mode", mode);
            r.Add("readMode", readMode);
            r.Add("space", space);
            r.Add("matchedBy", matchedBy);
            r.Add("readPath", readPath);
            if (lispMeta != null) r.Add("lispFile", lispMeta);
            r.Add("filters", FiltersMap(layer, typeList, textRe, args.Str("bbox", null), space, xdataSpec));
            return r;
        }

        private static JMap Mode(JMap described, string requestedMode)
        {
            if (requestedMode == "handles")
            {
                JMap m = new JMap();
                m.Add("handle", described.Get("handle"));
                m.Add("type", described.Get("type"));
                m.Add("kind", described.Get("kind"));
                m.Add("layer", described.Get("layer"));
                m.Add("space", described.Get("space"));
                return m;
            }
            return described;
        }

        private static JMap FiltersMap(string layer, string types, string text, string bbox, string space, string xdata)
        {
            JMap m = new JMap();
            if (!string.IsNullOrEmpty(layer)) m.Add("layer", layer);
            if (!string.IsNullOrEmpty(types)) m.Add("type", types);
            if (!string.IsNullOrEmpty(text)) m.Add("text", text);
            if (!string.IsNullOrEmpty(bbox)) m.Add("bbox", bbox);
            if (!string.IsNullOrEmpty(space)) m.Add("space", space);
            if (!string.IsNullOrEmpty(xdata)) m.Add("xdata", xdata);
            return m;
        }

        private static bool Matches(JMap m, object ent, string layer, List<string> wantKinds, Regex re,
            double[] box, string xdataApp, string xdataValue)
        {
            if (!string.IsNullOrEmpty(layer))
            {
                string actual = m.Str("layer", null);
                if (actual == null || !string.Equals(actual, layer, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (wantKinds.Count > 0)
            {
                string kind = (m.Str("kind", "") ?? "").ToUpperInvariant();
                string type = (m.Str("type", "") ?? "").ToUpperInvariant();
                bool ok = false;
                foreach (string want in wantKinds)
                {
                    if (kind == want || type == want)
                    {
                        ok = true;
                        break;
                    }
                    if (want == "POLYLINE" && (kind == "LWPOLYLINE" || type.IndexOf("POLYLINE", StringComparison.Ordinal) >= 0)) { ok = true; break; }
                    if (want == "TEXT" && kind == "MTEXT") continue;   // TEXT filter must not silently include MTEXT
                    if (want == "MTEXT" && kind == "MTEXT") { ok = true; break; }
                }
                if (!ok) return false;
            }
            if (re != null)
            {
                string text = m.Str("text", null);
                if (text == null) return false;
                if (!re.IsMatch(text)) return false;
            }
            if (box != null)
            {
                List<object> bb = m.List("bbox");
                JMap bbm = m.Map("bbox");
                double[] entBox = null;
                if (bb != null && bb.Count >= 4) entBox = new double[] { Com.Dbl(bb[0]), Com.Dbl(bb[1]), Com.Dbl(bb[2]), Com.Dbl(bb[3]) };
                else if (bbm != null)
                {
                    List<object> mn = bbm.List("min"), mx = bbm.List("max");
                    if (mn != null && mx != null && mn.Count >= 2 && mx.Count >= 2)
                        entBox = new double[] { Com.Dbl(mn[0]), Com.Dbl(mn[1]), Com.Dbl(mx[0]), Com.Dbl(mx[1]) };
                }
                if (entBox == null) return false;
                bool overlap = !(entBox[2] < box[0] || entBox[0] > box[2] || entBox[3] < box[1] || entBox[1] > box[3]);
                if (!overlap) return false;
            }
            if (xdataApp != null)
            {
                string got = Edits.TryGetXData(ent, xdataApp);
                if (got == null) return false;
                if (!string.IsNullOrEmpty(xdataValue) && got.IndexOf(xdataValue, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private static double[] ParseBBox(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Split(',');
            if (parts.Length < 4) throw new ZwcError("BAD_ARGS", "-BBox needs x0,y0,x1,y1", null, null);
            double[] b = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out b[i]))
                    throw new ZwcError("BAD_ARGS", "-BBox values must be numbers", null, null);
            }
            return b;
        }

        // ------------------------------------------------------------------ snapshot
        public static string SnapDir()
        {
            string d = Path.Combine(State.Dir(), "snap");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch (Exception) { }
            return d;
        }

        public static JMap SnapshotCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Commands.AppKindOf(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);

            JMap snap = Edits.SnapshotOf(doc, args.Bool("paper", true), ctx);
            string outFile = args.Str("out", null);
            if (!string.IsNullOrEmpty(outFile))
            {
                string full = null;
                try { full = Path.GetFullPath(outFile); } catch (Exception) { }
                string snapDir = SnapDir();
                if (full == null || !full.StartsWith(snapDir, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Warn("SNAPSHOT_OUT_FORCED", "snapshot is always written under %TEMP% (never into a drawing folder); -Out redirected");
                    outFile = Path.Combine(snapDir, "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                }
                else outFile = full;
            }
            else
            {
                outFile = Path.Combine(SnapDir(), "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            }
            snap.Add("cmd", "snapshot");
            snap.Add("doc", Com.Str(Com.TryGet(doc, "FullName")));
            snap.Add("docName", Com.Str(Com.TryGet(doc, "Name")));
            snap.Add("at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            File.WriteAllText(outFile, Json.Write(snap), new UTF8Encoding(false));

            JMap st = State.Load();
            st.Add("lastSnapshot", outFile);
            State.Save(st);

            JMap r = new JMap();
            r.Add("path", outFile);
            r.Add("count", snap.Int("count", 0));
            r.Add("items", snap.List("items"));
            r.Add("matchedBy", matchedBy);
            return r;
        }

        // ------------------------------------------------------------------ diff
        public static JMap DiffCmd(JMap req, JMap args, Ctx ctx)
        {
            string since = args.Str("since", "last");
            if (string.IsNullOrEmpty(since)) since = "last";
            if (string.Equals(since, "last", StringComparison.OrdinalIgnoreCase))
            {
                JMap st = State.Load();
                since = st.Str("lastSnapshot", null);
                if (string.IsNullOrEmpty(since))
                    throw new ZwcError("NO_BASELINE", "no previous snapshot: run `snapshot` first (or pass -Since <file>)",
                        "snapshot then diff is the supported way to see what changed", null);
            }
            if (!File.Exists(since)) throw new ZwcError("BAD_ARGS", "snapshot file not found: " + since, null, null);

            Cad cad = Cad.Attach(Commands.AppKindOf(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            JMap current = Edits.SnapshotOf(doc, args.Bool("paper", true), ctx);

            JMap old = Json.ParseFile(since) as JMap;
            if (old == null) throw new ZwcError("BAD_ARGS", "snapshot file is not a JSON object: " + since, null, null);
            List<object> oldItems = old.List("items");
            List<object> newItems = current.List("items");
            Dictionary<string, JMap> oldByHandle = Index(oldItems);
            Dictionary<string, JMap> newByHandle = Index(newItems);

            List<object> added = new List<object>();
            List<object> removed = new List<object>();
            List<object> changed = new List<object>();
            int same = 0;
            int maxChanged = args.Int("limit", 500);
            if (maxChanged <= 0) maxChanged = 500;

            foreach (KeyValuePair<string, JMap> kv in newByHandle)
            {
                JMap now = kv.Value;
                JMap was = null;
                oldByHandle.TryGetValue(kv.Key, out was);
                if (was == null)
                {
                    added.Add(Lite(now));
                    continue;
                }
                JMap fields = DiffFields(was, now);
                if (fields.Count == 0) { same++; continue; }
                if (changed.Count < maxChanged)
                {
                    JMap c = new JMap();
                    c.Add("handle", kv.Key);
                    c.Add("type", now.Str("type", null));
                    c.Add("layer", now.Str("layer", null));
                    c.Add("fields", fields);
                    changed.Add(c);
                }
            }
            foreach (KeyValuePair<string, JMap> kv in oldByHandle)
            {
                if (!newByHandle.ContainsKey(kv.Key)) removed.Add(Lite(kv.Value));
            }

            JMap r = new JMap();
            r.Add("since", since);
            r.Add("added", added);
            r.Add("removed", removed);
            r.Add("changed", changed);
            r.Add("same", same);
            JMap counts = new JMap();
            counts.Add("added", added.Count);
            counts.Add("removed", removed.Count);
            counts.Add("changed", changed.Count);
            counts.Add("same", same);
            counts.Add("before", oldByHandle.Count);
            counts.Add("after", newByHandle.Count);
            r.Add("counts", counts);
            r.Add("truncated", changed.Count >= maxChanged && maxChanged > 0);
            r.Add("matchedBy", matchedBy);
            return r;
        }

        private static Dictionary<string, JMap> Index(List<object> items)
        {
            Dictionary<string, JMap> d = new Dictionary<string, JMap>(StringComparer.OrdinalIgnoreCase);
            if (items == null) return d;
            foreach (object o in items)
            {
                JMap m = o as JMap;
                if (m == null) continue;
                string h = m.Str("handle", null);
                if (string.IsNullOrEmpty(h)) continue;
                d[h] = m;
            }
            return d;
        }

        private static JMap Lite(JMap item)
        {
            JMap m = new JMap();
            m.Add("handle", item.Get("handle"));
            m.Add("type", item.Get("type"));
            m.Add("layer", item.Get("layer"));
            m.Add("space", item.Get("space"));
            return m;
        }

        private static JMap DiffFields(JMap was, JMap now)
        {
            JMap fields = new JMap();
            AddIfDifferent(fields, "type", was.Str("type", null), now.Str("type", null));
            AddIfDifferent(fields, "layer", was.Str("layer", null), now.Str("layer", null));
            AddIfDifferent(fields, "text", was.Str("text", null), now.Str("text", null));
            double[] ob = BBoxOf(was), nb = BBoxOf(now);
            if (ob != null || nb != null)
            {
                bool differs = false;
                if (ob == null || nb == null) differs = true;
                else for (int i = 0; i < 4; i++) if (Math.Abs(ob[i] - nb[i]) > 1e-6) { differs = true; break; }
                if (differs)
                {
                    JMap f = new JMap();
                    f.Add("before", ob == null ? null : (object)new List<object> { ob[0], ob[1], ob[2], ob[3] });
                    f.Add("after", nb == null ? null : (object)new List<object> { nb[0], nb[1], nb[2], nb[3] });
                    fields.Add("bbox", f);
                }
            }
            return fields;
        }

        private static void AddIfDifferent(JMap fields, string name, string before, string after)
        {
            if (string.Equals(before ?? "", after ?? "", StringComparison.Ordinal)) return;
            JMap f = new JMap();
            f.Add("before", before);
            f.Add("after", after);
            fields.Add(name, f);
        }

        private static double[] BBoxOf(JMap m)
        {
            List<object> bb = m.List("bbox");
            if (bb != null && bb.Count >= 4) return new double[] { Com.Dbl(bb[0]), Com.Dbl(bb[1]), Com.Dbl(bb[2]), Com.Dbl(bb[3]) };
            JMap bm = m.Map("bbox");
            if (bm != null)
            {
                JMap mn = bm.Map("min"), mx = bm.Map("max");
                List<object> lmn = bm.List("min"), lmx = bm.List("max");
                if (lmn != null && lmx != null && lmn.Count >= 2 && lmx.Count >= 2)
                    return new double[] { Com.Dbl(lmn[0]), Com.Dbl(lmn[1]), Com.Dbl(lmx[0]), Com.Dbl(lmx[1]) };
            }
            return null;
        }
    }
}
