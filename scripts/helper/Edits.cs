// Edits.cs - M3-E2 通用写原语：entity / query / snapshot / diff
//
// The point of this file is to stop hard-coding "the features the tool knows about". The agent gets
// primitives (write by handle with anchors, read by predicate, snapshot+diff) and composes them.
//
// Discipline copied from the WPS side (DESIGN-E2):
//   * Pass 1 validates EVERY item (handle exists, type, expect fields) before anything is touched;
//     one bad anchor means the whole batch is left untouched (ANCHOR_MISSING with a per-item detail)
//     unless -Continue asks for per-item isolation.
//   * Pass 2 applies the batch inside ONE undo group.
//   * Pass 3 reads every touched handle back and compares; any mismatch rolls the whole group back
//     and reports ok:false (never "reported success but nothing happened").
//   * Only model space is written; locked/frozen layers and read-only documents are refused up front.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZwcHelper
{
    internal sealed class EditItemResult
    {
        public JMap Map = new JMap();
        public bool Ok = true;
    }

    internal static class Edits
    {
        // ------------------------------------------------------------------ small helpers
        private static double Tol(JMap args) { return args.Dbl("tol", 1e-6); }

        private static bool EqStr(string a, string b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
        }

        private static double[] Pair(object o)
        {
            List<object> l = o as List<object>;
            if (l != null && l.Count >= 2) return new double[] { Com.Dbl(l[0]), Com.Dbl(l[1]) };
            return null;
        }

        private static string TextOf(object ent)
        {
            object t = Com.TryGet(ent, "TextString");
            if (t == null) t = Com.TryGet(ent, "Text");
            return Com.Str(t);
        }

        private static double[] BBoxOf(object ent)
        {
            double[] mn = null, mx = null;
            if (!Com.TryGetBBox(ent, out mn, out mx)) return null;
            return new double[] { mn[0], mn[1], mx[0], mx[1] };
        }

        private static JMap Pt(double[] p)
        {
            JMap m = new JMap();
            m.Add("x", p == null ? 0 : p[0]);
            m.Add("y", p == null ? 0 : p[1]);
            return m;
        }

        private static JMap BBoxMap(double[] b)
        {
            if (b == null) return null;
            JMap m = new JMap();
            m.Add("min", new List<object> { b[0], b[1] });
            m.Add("max", new List<object> { b[2], b[3] });
            return m;
        }

        private static List<object> BBoxList(double[] b)
        {
            if (b == null) return null;
            return new List<object> { b[0], b[1], b[2], b[3] };
        }

        private static bool BBoxEq(double[] a, double[] b, double tol)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > tol) return false;
            return true;
        }

        internal static JMap SnapshotOf(object doc, bool includePaper, Ctx ctx)
        {
            JMap snap = new JMap();
            List<object> items = new List<object>();
            string[] spaces = includePaper ? new string[] { "ModelSpace", "PaperSpace" } : new string[] { "ModelSpace" };
            foreach (string spaceName in spaces)
            {
                object space = Com.TryGet(doc, spaceName);
                if (space == null) continue;
                int n = Com.I32(Com.TryGet(space, "Count"));
                int b = Coll.DetectBase(space, n);
                Commands.ReadState state = new Commands.ReadState();
                for (int k = 0; k < n; k++)
                {
                    object e = Coll.At(space, b, k, n);
                    if (e == null) continue;
                    JMap m = Commands.DescribeEntity(e, spaceName == "PaperSpace" ? "paper" : "model", "summary", ctx, state);
                    if (m != null) items.Add(m);
                }
            }
            snap.Add("items", items);
            snap.Add("count", items.Count);
            return snap;
        }

        // ------------------------------------------------------------------ entity
        public static JMap EntityCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Commands.AppKindOf(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            bool readOnly = false;
            object ro = Com.TryGet(doc, "ReadOnly");
            if (ro != null) readOnly = Com.Bool(ro);
            if (readOnly) throw new ZwcError("READONLY_DOC", "the drawing is read-only; refusing to modify it", "open it writable or work on a copy", null);

            Edits.SafeDoc = doc;
            _layerCache.Clear();
            List<object> raw = LoadItems(args, ctx);
            if (raw.Count == 0) throw new ZwcError("BAD_ARGS", "entity needs items (use -Items <file> or -RequestFile <full request>)", "see docs/M3-E2-writes.md", null);

            string mode = args.Str("mode", args.Bool("dryRun", false) ? "plan" : "apply");
            if (mode == null) mode = "apply";
            mode = mode.ToLowerInvariant();
            bool planOnly = (mode == "plan" || mode == "dryrun");
            bool cont = args.Bool("continue", false);
            double tol = Tol(args);

            List<JMap> items = new List<JMap>();
            foreach (object o in raw) { JMap it = o as JMap; if (it != null) items.Add(it); }

            // ---- layer safety: refuse locked/frozen layers up front (add/update)
            object layers = Com.TryGet(doc, "Layers");

            Stopwatch swPass1 = Stopwatch.StartNew();
            // ---- Pass 1: validate every item
            List<object> plan = new List<object>();
            List<object> anchorFailures = new List<object>();
            List<int> valid = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                JMap it = items[i];
                JMap fail = ValidateItem(doc, layers, it, i, tol, ctx);
                if (fail != null)
                {
                    anchorFailures.Add(fail);
                    if (cont) continue;
                }
                valid.Add(i);
                JMap pm = PlanOf(doc, it, i);
                plan.Add(pm);
            }
            if (anchorFailures.Count > 0 && !cont)
            {
                string detail = Json.Write(anchorFailures);
                throw new ZwcError("ANCHOR_MISSING",
                    "anchor validation failed for " + anchorFailures.Count + " item(s); the whole batch was left untouched",
                    "fix the handles/expect values or pass -Continue to isolate failures", detail);
            }

            swPass1.Stop();
            JMap result = new JMap();
            result.Add("plan", plan);
            result.Add("matchedBy", matchedBy);
            if (planOnly)
            {
                result.Add("applied", 0);
                JMap v0 = new JMap();
                v0.Add("ok", true);
                v0.Add("skipped", true);
                v0.Add("issues", new List<object>());
                result.Add("verify", v0);
                result.Add("items", new List<object>());
                return result;
            }

            Stopwatch swPass2 = Stopwatch.StartNew();
            int countBefore = Commands.CountOf(doc, "ModelSpace");
            bool useUndoMark = !args.Bool("noUndoMark", false);
            Stopwatch swUndoMark = Stopwatch.StartNew();
            bool markOk = useUndoMark && (Com.TryInvoke(doc, "BeginUndoMark") || Com.TryInvoke(doc, "StartUndoMark"));
            swUndoMark.Stop();
            if (!markOk) ctx.Warn("UNDO_MARK_UNAVAILABLE", "undo group could not be opened; a failed batch will be rolled back item by item");

            List<object> itemResults = new List<object>();
            List<object> failures = new List<object>();
            List<object> touched = new List<object>();       // JMap {handle, before...}
            Stopwatch swApply = Stopwatch.StartNew();
            Stopwatch swUndoEnd = new Stopwatch();
            try
            {
                foreach (int i in valid)
                {
                    JMap it = items[i];
                    JMap ir = ApplyItem(doc, it, i, ctx, touched);
                    itemResults.Add(ir);
                    if (!ir.Bool("ok", false))
                    {
                        failures.Add(ir);
                        if (!cont) break;
                    }
                }
            }
            catch (Exception e)
            {
            swApply.Stop();
            swUndoEnd.Start();
                if (useUndoMark) Com.TryCall(doc, "EndUndoMark");
            swUndoEnd.Stop();
                RollbackGroup(doc, countBefore, ctx);
                throw new ZwcError("APPLY_FAILED", "applying the batch failed: " + Com.Hr(e), "the undo group was rolled back", null);
            }
            if (useUndoMark) Com.TryCall(doc, "EndUndoMark");

            if (failures.Count > 0 && !cont)
            {
                RollbackGroup(doc, countBefore, ctx);
                JMap v = VerifyItems(doc, args, items, itemResults, tol, ctx);
                result.Add("applied", 0);
                result.Add("rolledBack", true);
                result.Add("failed", failures);
                result.Add("items", itemResults);
                result.Add("verify", v);
                ctx.Result = result;
                throw new ZwcError("APPLY_FAILED",
                    failures.Count + " item(s) failed to apply; the undo group was rolled back",
                    "see result.failed", Json.Write(failures));
            }

            swPass2.Stop();
            Stopwatch swReadback = Stopwatch.StartNew();
            // ---- Pass 3: read back and verify
            JMap verify = VerifyItems(doc, args, items, itemResults, tol, ctx);
            result.Add("items", itemResults);
            result.Add("failed", failures);
            swReadback.Stop();
            if (!verify.Bool("ok", false))
            {
                RollbackGroup(doc, countBefore, ctx);
                result.Add("applied", 0);
                result.Add("rolledBack", true);
                result.Add("verify", verify);
                ctx.Result = result;
                throw new ZwcError("VERIFY_FAILED",
                    "write succeeded but read-back verification failed; the undo group was rolled back",
                    "see result.verify.issues", Json.Write(verify));
            }
            result.Add("applied", itemResults.Count);
            result.Add("rolledBack", false);
            result.Add("verify", verify);
            JMap bodyDetail = new JMap();
            bodyDetail.Add("plan", swPass1.ElapsedMilliseconds);
            bodyDetail.Add("undoMark", swUndoMark.ElapsedMilliseconds);
            bodyDetail.Add("apply", swApply.ElapsedMilliseconds);
            bodyDetail.Add("readback", swReadback.ElapsedMilliseconds);
            bodyDetail.Add("undoEnd", swUndoEnd.ElapsedMilliseconds);
            result.Add("bodyDetail", bodyDetail);
            result.Add("timings", new JMap().Add("pass1", swPass1.ElapsedMilliseconds).Add("pass2", swPass2.ElapsedMilliseconds).Add("readback", swReadback.ElapsedMilliseconds).Add("items", items.Count));

            string token = "u-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
                + new Random(Environment.TickCount).Next(0x1000, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
            JMap st = State.Load();
            JMap tok = new JMap();
            tok.Add("cmd", "entity");
            tok.Add("doc", Com.Str(Com.TryGet(doc, "FullName")));
            tok.Add("baseline", countBefore);
            int countAfterWrites = countBefore;
            foreach (object o in itemResults) { JMap ir0 = o as JMap; if (ir0 != null && ir0.Bool("ok", false)) countAfterWrites += (ir0.Str("op", "") == "delete" ? -1 : 1); }
            tok.Add("countAfter", countAfterWrites);
            tok.Add("undoGroups", 1);
            tok.Add("at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            State.Tokens(st).Add(token, tok);
            State.PruneTokens(st);
            State.Save(st);
            ctx.UndoToken = token;
            result.Add("undoToken", token);
            ctx.DocMap = Cad.DocInfo(doc);
            return result;
        }

        private static List<object> LoadItems(JMap args, Ctx ctx)
        {
            List<object> items = args.List("items");
            if (items != null) return items;
            // -Items <file> arrives as a plain string argument (the CLI has no notion of lists);
            // an inline items array is also accepted for small programmatic batches.
            string file = args.Str("itemsFile", null);
            if (string.IsNullOrEmpty(file)) file = args.Str("requestFile", null);
            if (string.IsNullOrEmpty(file))
            {
                string inline = args.Str("items", null);
                if (!string.IsNullOrEmpty(inline) && File.Exists(inline)) file = inline;
            }
            if (string.IsNullOrEmpty(file)) return new List<object>();
            if (!File.Exists(file)) throw new ZwcError("BAD_ARGS", "items file not found: " + file, null, null);
            object parsed = Json.ParseFile(file);
            JMap root = parsed as JMap;
            if (root != null)
            {
                List<object> inner = root.List("items");
                if (inner != null) return inner;
                JMap a = root.Map("args");
                if (a != null)
                {
                    inner = a.List("items");
                    if (inner != null) return inner;
                }
                return new List<object> { root };
            }
            List<object> l = parsed as List<object>;
            return l == null ? new List<object>() : l;
        }

        /// <summary>Pass 1 for one item. Returns a failure JMap or null when the anchor is good.</summary>
        private static JMap ValidateItem(object doc, object layers, JMap it, int i, double tol, Ctx ctx)
        {
            string op = (it.Str("op", "") ?? "").ToLowerInvariant();
            JMap fail = null;
            Action<string, object, object, bool> bad = delegate(string field, object want, object actual, bool reloc)
            {
                if (fail != null) return;
                fail = new JMap();
                fail.Add("i", i);
                fail.Add("op", op);
                fail.Add("handle", it.Str("handle", null));
                fail.Add("field", field);
                fail.Add("expect", want);
                fail.Add("actual", actual);
                fail.Add("relocatable", reloc);
            };

            if (op == "add")
            {
                string type = (it.Str("type", "") ?? "").ToLowerInvariant();
                if (type != "line" && type != "polyline" && type != "lwpolyline" && type != "text" && type != "mtext" && type != "circle")
                    bad("type", "line|polyline|text|mtext|circle", type, false);
                string layer = it.Str("layer", null);
                if (!string.IsNullOrEmpty(layer) && layers != null)
                {
                    string why = LayerProblem(layers, layer);
                    if (why != null) bad("layer", layer, why, false);
                }
                return fail;
            }
            if (op != "update" && op != "delete" && op != "move" && op != "copy")
            {
                bad("op", "add|update|delete|move|copy", op, false);
                return fail;
            }

            JMap expect = it.Map("expect");
            if (expect == null) expect = new JMap();
            string handle = it.Str("handle", null);
            if (string.IsNullOrEmpty(handle)) { bad("handle", "(required)", null, false); return fail; }
            object ent = Com.EntityByHandle(doc, handle);
            if (ent == null)
            {
                // The handle is gone but the drawing did not change outside this session: try to
                // re-locate the entity by its expected fingerprint (type/layer/text) and continue
                // with its current handle (relocated:true). Anything less certain stays ANCHOR_MISSING.
                object reloc = null;
                if (!ctx.FpChanged) reloc = RelocateByExpect(doc, expect);
                if (reloc != null)
                {
                    string newHandle = Com.Str(Com.TryGet(reloc, "Handle"));
                    it.Add("handle", newHandle);
                    it.Add("_relocated", true);
                    ctx.Warn("ANCHOR_RELOCATED", "handle " + handle + " was re-located by its expect fingerprint as " + newHandle);
                    return null;
                }
                bad("handle", handle, "(not found)", ctx.FpChanged);
                return fail;
            }
            foreach (string field in expect.Keys)
            {
                object ev = expect.Get(field);
                string f = field.ToLowerInvariant();
                if (f == "type")
                {
                    string actual = Com.Str(Com.TryGet(ent, "ObjectName")) ?? "";
                    string want = Com.Str(ev) ?? "";
                    string actualKind = Commands.KindOf(actual);
                    string wantKind = Commands.KindOf(want);
                    if (!string.Equals(actual, want, StringComparison.OrdinalIgnoreCase) && actualKind != wantKind)
                        bad("type", want, actual, false);
                }
                else if (f == "layer")
                {
                    string actual = Com.Str(Com.TryGet(ent, "Layer"));
                    if (!EqStr(actual, Com.Str(ev))) bad("layer", Com.Str(ev), actual, false);
                }
                else if (f == "text")
                {
                    string actual = TextOf(ent);
                    if (!EqStr(actual, Com.Str(ev))) bad("text", Com.Str(ev), actual, false);
                }
                else if (f == "height")
                {
                    double actual = Com.Dbl(Com.TryGet(ent, "Height"));
                    if (Math.Abs(actual - Com.Dbl(ev)) > tol) bad("height", Com.Dbl(ev), actual, false);
                }
                else if (f == "bbox")
                {
                    double[] actual = BBoxOf(ent);
                    double[] want = BBoxListToArray(ev);
                    if (!BBoxEq(actual, want, tol)) bad("bbox", ev, BBoxList(actual), true);
                }
            }
            // layer safety for the target layer of update/move
            if (fail == null && op == "update")
            {
                JMap set = it.Map("set");
                string newLayer = set == null ? null : set.Str("layer", null);
                if (!string.IsNullOrEmpty(newLayer) && layers != null)
                {
                    string why = LayerProblem(layers, newLayer);
                    if (why != null) bad("layer", newLayer, why, false);
                }
            }
            return fail;
        }

        /// <summary>Find exactly one model-space entity matching an expect map (type/layer/text),
        /// used to re-anchor a disappearing handle when the drawing itself is unchanged.</summary>
        internal static object RelocateByExpect(object doc, JMap expect)
        {
            string wantType = expect == null ? null : expect.Str("type", null);
            string wantLayer = expect == null ? null : expect.Str("layer", null);
            string wantText = expect == null ? null : expect.Str("text", null);
            if (string.IsNullOrEmpty(wantType) && string.IsNullOrEmpty(wantLayer) && string.IsNullOrEmpty(wantText)) return null;
            object ms = Com.TryGet(doc, "ModelSpace");
            if (ms == null) return null;
            int n = Com.I32(Com.TryGet(ms, "Count"));
            int b = Coll.DetectBase(ms, n);
            object found = null;
            int hits = 0;
            for (int k = 0; k < n; k++)
            {
                object e = Coll.At(ms, b, k, n);
                if (e == null) continue;
                string type = Com.Str(Com.TryGet(e, "ObjectName"));
                string kind = Commands.KindOf(type);
                if (!string.IsNullOrEmpty(wantType) && type != wantType && kind != Commands.KindOf(wantType)) continue;
                string layer = Com.Str(Com.TryGet(e, "Layer"));
                if (!string.IsNullOrEmpty(wantLayer) && !string.Equals(layer, wantLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(wantText))
                {
                    object txt = Com.TryGet(e, "TextString");
                    if (txt == null) txt = Com.TryGet(e, "Text");
                    if (!string.Equals(Com.Str(txt), wantText, StringComparison.Ordinal)) continue;
                }
                hits++;
                found = e;
                if (hits > 1) return null;      // ambiguous: never guess
            }
            return hits == 1 ? found : null;
        }
        private static double[] BBoxListToArray(object o)
        {
            List<object> l = o as List<object>;
            if (l != null && l.Count >= 4) return new double[] { Com.Dbl(l[0]), Com.Dbl(l[1]), Com.Dbl(l[2]), Com.Dbl(l[3]) };
            JMap m = o as JMap;
            if (m != null)
            {
                List<object> mn = m.List("min"), mx = m.List("max");
                if (mn != null && mx != null && mn.Count >= 2 && mx.Count >= 2)
                    return new double[] { Com.Dbl(mn[0]), Com.Dbl(mn[1]), Com.Dbl(mx[0]), Com.Dbl(mx[1]) };
            }
            return null;
        }

        /// <summary>Returns a reason string when the layer cannot be written to.</summary>
        internal static string LayerProblem(object layers, string name)
        {
            object lyr = null;
            try { lyr = Com.Call(layers, "Item", name); }
            catch (Exception) { }
            if (lyr == null) return null;                     // caller may create it (add) or it is inherited
            object lk = Com.TryGet(lyr, "Lock");
            if (lk != null && Com.Bool(lk)) return "layer is locked";
            object fr = Com.TryGet(lyr, "Freeze");
            if (fr != null && Com.Bool(fr)) return "layer is frozen";
            object off = Com.TryGet(lyr, "LayerOn");
            if (off != null && !Com.Bool(off)) return "layer is off";
            return null;
        }

        private static JMap PlanOf(object doc, JMap it, int i)
        {
            JMap m = new JMap();
            m.Add("i", i);
            m.Add("op", it.Str("op", ""));
            m.Add("handle", it.Str("handle", null));
            if (it.Has("set")) m.Add("set", it.Get("set"));
            if (it.Has("delta")) m.Add("delta", it.Get("delta"));
            if (it.Has("xdata")) m.Add("xdata", it.Get("xdata"));
            return m;
        }

        private static JMap ApplyItem(object doc, JMap it, int i, Ctx ctx, List<object> touched)
        {
            JMap outMap = new JMap();
            outMap.Add("i", i);
            string op = (it.Str("op", "") ?? "").ToLowerInvariant();
            outMap.Add("op", op);
            try
            {
                if (op == "add")
                {
                    object ms = Com.Get(doc, "ModelSpace");
                    object ent = CreateForAdd(ms, it, ctx);
                    if (ent == null) throw new ZwcError("ADD_FAILED", "CAD returned no object for add " + i, null, null);
                    string wantLayerAdd = it.Str("layer", null);
                    if (!string.IsNullOrEmpty(wantLayerAdd))
                    {
                        // setting Layer to an unknown name silently leaves the entity on layer 0
                        EnsureLayerInDoc(doc, wantLayerAdd);
                        Com.TrySet(ent, "Layer", wantLayerAdd);
                    }
                    string h = Com.Str(Com.TryGet(ent, "Handle"));
                    outMap.Add("handle", h);
                    if (it.Has("xdata")) outMap.Add("xdata", it.Get("xdata"));
                outMap.Add("ok", true);
                    ApplyXData(doc, ent, it, ctx);
                    JMap after = new JMap();
                    after.Add("handle", h);
                    after.Add("type", Com.Str(Com.TryGet(ent, "ObjectName")));
                    after.Add("layer", Com.Str(Com.TryGet(ent, "Layer")));
                    outMap.Add("after", after);
                    JMap t = new JMap();
                    t.Add("handle", h);
                    t.Add("op", "add");
                    touched.Add(t);
                    return outMap;
                }

                string handle = it.Str("handle", null);
                object e = Com.EntityByHandle(doc, handle);
                if (e == null) throw new ZwcError("ENTITY_NOT_FOUND", "handle " + handle + " disappeared before apply", null, null);
                outMap.Add("handle", handle);
                if (it.Bool("_relocated", false)) outMap.Add("relocated", true);
                JMap before = DescribeForResult(e);
                outMap.Add("before", before);

                if (op == "update")
                {
                    JMap set = it.Map("set");
                    if (set != null) foreach (string k in set.Keys) SetField(doc, e, k, set.Get(k), ctx);
                    ApplyXData(doc, e, it, ctx);
                    if (it.Has("xdata")) outMap.Add("xdata", it.Get("xdata"));
                }
                else if (op == "delete")
                {
                    Com.Call(e, "Delete");
                    XdRegistryRemoveHandle(doc, handle);
                }
                else if (op == "move" || op == "copy")
                {
                    double[] delta = Pair(it.Get("delta"));
                    if (delta == null) throw new ZwcError("BAD_ARGS", "op " + op + " needs delta [dx,dy]", null, null);
                    int count = op == "copy" ? Math.Max(1, it.Int("count", 1)) : 1;
                    object target = e;
                    for (int c = 0; c < count; c++)
                    {
                        if (op == "copy")
                        {
                            object cp = Com.Call(e, "Copy");
                            if (cp == null) throw new ZwcError("COPY_FAILED", "Entity.Copy returned nothing", null, null);
                            target = cp;
                            string hc = Com.Str(Com.TryGet(cp, "Handle"));
                            JMap t = new JMap();
                            t.Add("handle", hc);
                            t.Add("op", "add");
                            touched.Add(t);
                        }
                        Com.Call(target, "Move", (object)new double[] { 0.0, 0.0, 0.0 },
                            (object)new double[] { delta[0], delta[1], 0.0 });
                        JMap t2 = new JMap();
                        t2.Add("handle", Com.Str(Com.TryGet(target, "Handle")));
                        t2.Add("op", op);
                        touched.Add(t2);
                    }
                    if (op == "move") outMap.Add("handle", handle);
                }
                if (it.Has("xdata")) outMap.Add("xdata", it.Get("xdata"));
                outMap.Add("ok", true);
                JMap after2 = DescribeForResult(Com.EntityByHandle(doc, outMap.Str("handle", handle)) ?? e);
                outMap.Add("after", after2);
                return outMap;
            }
            catch (Exception ex)
            {
                outMap.Add("ok", false);
                outMap.Add("error", Com.Hr(ex));
                return outMap;
            }
        }

        private static JMap DescribeForResult(object e)
        {
            JMap m = new JMap();
            m.Add("handle", Com.Str(Com.TryGet(e, "Handle")));
            m.Add("type", Com.Str(Com.TryGet(e, "ObjectName")));
            m.Add("layer", Com.Str(Com.TryGet(e, "Layer")));
            string txt = TextOf(e);
            if (txt != null) m.Add("text", txt);
            object h = Com.TryGet(e, "Height");
            if (h != null) m.Add("height", Com.Dbl(h));
            // bbox intentionally omitted here: GetBoundingBox per item dominated entity time on 2025
            return m;
        }

        private static object CreateForAdd(object ms, JMap it, Ctx ctx)
        {
            string type = (it.Str("type", "") ?? "").ToLowerInvariant();
            JMap geom = it.Map("geom");
            if (geom == null) geom = new JMap();
            string layer = it.Str("layer", null);
            object ent = null;
            if (type == "line")
            {
                double[] p1 = Pair(geom.Get("start")); if (p1 == null) p1 = Pair(geom.Get("p1"));
                double[] p2 = Pair(geom.Get("end")); if (p2 == null) p2 = Pair(geom.Get("p2"));
                if (p1 == null || p2 == null) throw new ZwcError("BAD_ARGS", "add line needs geom.start and geom.end", null, null);
                ent = Com.Call(ms, "AddLine", (object)new double[] { p1[0], p1[1], 0.0 }, (object)new double[] { p2[0], p2[1], 0.0 });
            }
            else if (type == "polyline" || type == "lwpolyline")
            {
                List<object> pts = geom.List("points");
                if (pts == null || pts.Count < 2) throw new ZwcError("BAD_ARGS", "add polyline needs geom.points [[x,y],...]", null, null);
                List<double> flat = new List<double>();
                foreach (object o in pts) { double[] p = Pair(o); if (p == null) throw new ZwcError("BAD_ARGS", "polyline points must be [x,y]", null, null); flat.Add(p[0]); flat.Add(p[1]); }
                ent = Com.Call(ms, "AddLightWeightPolyline", (object)flat.ToArray());
                Com.TrySet(ent, "Closed", geom.Bool("closed", false));
            }
            else if (type == "text" || type == "mtext")
            {
                string text = geom.Str("text", null);
                if (text == null) text = it.Str("text", "");
                double[] ins = Pair(geom.Get("insert"));
                if (ins == null) throw new ZwcError("BAD_ARGS", "add " + type + " needs geom.insert [x,y]", null, null);
                double height = geom.Dbl("height", 2.5);
                if (type == "mtext") ent = Com.Call(ms, "AddMText", (object)new double[] { ins[0], ins[1], 0.0 }, (object)geom.Dbl("width", 0.0), (object)text);
                else ent = Com.Call(ms, "AddText", (object)text, (object)new double[] { ins[0], ins[1], 0.0 }, (object)height);
                if (type == "mtext") Com.TrySet(ent, "Height", height);
            }
            else if (type == "circle")
            {
                double[] c = Pair(geom.Get("center"));
                if (c == null) throw new ZwcError("BAD_ARGS", "add circle needs geom.center [x,y]", null, null);
                ent = Com.Call(ms, "AddCircle", (object)new double[] { c[0], c[1], 0.0 }, (object)geom.Dbl("radius", 1.0));
            }
            if (ent == null) return null;
            if (!string.IsNullOrEmpty(layer))
            {
                object layers = Com.TryGet(ms, "Document");   // not available on block; layer set by caller
                Com.TrySet(ent, "Layer", layer);
            }
            JMap props = it.Map("props");
            if (props != null) foreach (string k in props.Keys) SetField(null, ent, k, props.Get(k), ctx);
            return ent;
        }

        private static void SetField(object doc, object e, string field, object value, Ctx ctx)
        {
            string f = (field ?? "").ToLowerInvariant();
            switch (f)
            {
                case "text":
                case "textstring":
                    if (!Com.TrySet(e, "TextString", Com.Str(value)))
                        Com.TrySet(e, "Text", Com.Str(value));
                    break;
                case "height": Com.TrySet(e, "Height", Com.Dbl(value)); break;
                case "layer": EnsureLayerInDoc(doc, Com.Str(value)); Com.TrySet(e, "Layer", Com.Str(value)); break;
                case "color": Com.TrySet(e, "Color", Com.I32(value)); break;
                case "linetype": Com.TrySet(e, "Linetype", Com.Str(value)); break;
                case "style": Com.TrySet(e, "StyleName", Com.Str(value)); break;
                case "rotation": Com.TrySet(e, "Rotation", Com.Dbl(value)); break;
                case "width": Com.TrySet(e, "Width", Com.Dbl(value)); break;
                case "radius": Com.TrySet(e, "Radius", Com.Dbl(value)); break;
                default:
                    ctx.Warn("UNKNOWN_FIELD", "unknown set field '" + field + "' was ignored");
                    break;
            }
        }

        /// <summary>Make sure a layer exists before assigning it: setting Layer to an unknown
        /// name silently fails in both CADs, which used to make verification compare an unchanged
        /// entity against its own unchanged snapshot and report success.</summary>
        internal static void EnsureLayerInDoc(object doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (_layerCache.Contains(name)) return;
            _layerCache.Add(name);
            object layers = Com.TryGet(doc, "Layers");
            if (layers == null) return;
            object lyr = null;
            try { lyr = Com.Call(layers, "Item", name); }
            catch (Exception) { }
            if (lyr != null) return;
            try { Com.Call(layers, "Add", name); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ xdata
        private static readonly HashSet<string> _layerCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static object SafeDoc = null;   // doc used by the xdata registry mirror

        internal static bool ApplyXData(object doc, object ent, JMap it, Ctx ctx)
        {
            JMap x = it.Map("xdata");
            if (x == null) return true;
            string app = x.Str("app", "AI_MARK");
            string val = x.Str("value", "");
            string handle = Com.Str(Com.TryGet(ent, "Handle"));
            // (1) the app name must be registered before SetXData will accept it
            try
            {
                object apps = Com.TryGet(doc, "RegisteredApplications");
                if (apps != null)
                {
                    object reg = null;
                    try { reg = Com.Call(apps, "Item", app); } catch (Exception) { }
                    if (reg == null) Com.Call(apps, "Add", app);
                }
            }
            catch (Exception ex) { ctx.Warn("XDATA_REGAPP_FAILED", "RegisteredApplications.Add(" + app + ") failed: " + Com.Hr(ex)); }
            // (2) real SetXData
            bool setOk = false;
            try
            {
                short[] types = new short[] { 1001, 1000 };
                object[] vals = new object[] { app, val };
                Com.Call(ent, "SetXData", (object)types, (object)vals);
                setOk = true;
            }
            catch (Exception ex) { ctx.Warn("XDATA_SET_FAILED", "SetXData failed: " + Com.Hr(ex)); }
            // (3) read back: the CLR late binder cannot marshal GetXData out-parameters on ZWCAD,
            //     so an unreadable value is mirrored in the tool registry (%TEMP%) and reported.
            string got = TryGetXData(ent, app);
            bool realRead = got != null && got.IndexOf(val, StringComparison.Ordinal) >= 0;
            if (!realRead)
            {
                XdRegistrySet(doc, handle, app, val);
                ctx.Warn("XDATA_READ_FALLBACK", "GetXData out-parameters cannot be read late-bound on this CAD build; xdata is served from the tool registry mirror");
            }
            return setOk || realRead;
        }

        internal static void XdRegistrySet(object doc, string handle, string app, string value)
        {
            if (string.IsNullOrEmpty(handle)) return;
            JMap st = State.Load();
            JMap idx = st.Map("xdataIndex");
            if (idx == null) { idx = new JMap(); st.Add("xdataIndex", idx); }
            idx.Add(DocKey(doc) + "|" + handle + "|" + app, value);
            State.Save(st);
        }

        internal static string XdRegistryGet(object doc, string handle, string app)
        {
            JMap st = State.Load();
            JMap idx = st.Map("xdataIndex");
            if (idx == null) return null;
            return idx.Str(DocKey(doc) + "|" + handle + "|" + app, null);
        }

        internal static void XdRegistryRemove(object doc, string handle, string app)
        {
            JMap st = State.Load();
            JMap idx = st.Map("xdataIndex");
            if (idx == null) return;
            string key = DocKey(doc) + "|" + handle + "|" + app;
            if (!idx.Has(key)) return;
            JMap fresh = new JMap();
            foreach (string k in idx.Keys) if (k != key) fresh.Add(k, idx.Get(k));
            st.Add("xdataIndex", fresh);
            State.Save(st);
        }

        internal static void XdRegistryRemoveHandle(object doc, string handle)
        {
            if (string.IsNullOrEmpty(handle)) return;
            JMap st = State.Load();
            JMap idx = st.Map("xdataIndex");
            if (idx == null) return;
            JMap fresh = new JMap();
            string prefix = DocKey(doc) + "|" + handle + "|";
            bool changed = false;
            foreach (string k in idx.Keys) { if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { changed = true; continue; } fresh.Add(k, idx.Get(k)); }
            if (changed) { st.Add("xdataIndex", fresh); State.Save(st); }
        }

        private static string DocKey(object doc)
        {
            string f = Com.Str(Com.TryGet(doc, "FullName"));
            if (string.IsNullOrEmpty(f)) f = Com.Str(Com.TryGet(doc, "Name"));
            return f ?? "active";
        }

        internal static string TryGetXData(object ent, string app)
        {
            try
            {
                object[] parms = new object[] { app, null, null };
                ent.GetType().InvokeMember("GetXData",
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance, null, ent, parms);
                Array types = parms[1] as Array;
                Array vals = parms[2] as Array;
                List<string> parts = new List<string>();
                if (types != null && vals != null)
                {
                    List<object> tv = new List<object>(); foreach (object o in types) tv.Add(o);
                    List<object> vv = new List<object>(); foreach (object o in vals) vv.Add(o);
                    int n = Math.Min(tv.Count, vv.Count);
                    for (int i = 0; i < n; i++)
                    {
                        int code = Com.I32(tv[i]);
                        if (code == 1000 || code == 1001)
                        {
                            string v = Com.Str(vv[i]);
                            if (!string.IsNullOrEmpty(v)) parts.Add(v);
                        }
                    }
                }
                if (parts.Count > 0) return string.Join("|", parts.ToArray());
            }
            catch (Exception) { }
            // fallback: registry mirror (documented, %TEMP% same-machine; see ApplyXData)
            string handle = Com.Str(Com.TryGet(ent, "Handle"));
            return XdRegistryGet(SafeDoc, handle, app);
        }

        // ------------------------------------------------------------------ verify + rollback
        private static JMap VerifyItems(object doc, JMap args, List<JMap> sourceItems, List<object> itemResults, double tol, Ctx ctx)
        {
            List<object> issues = new List<object>();
            int checkedCount = 0;
            foreach (object o in itemResults)
            {
                JMap ir = o as JMap;
                if (ir == null) continue;
                if (!ir.Bool("ok", false)) continue;
                string handle = ir.Str("handle", null);
                string op = ir.Str("op", "");
                if (op == "delete") { checkedCount++; continue; }
                if (string.IsNullOrEmpty(handle)) { continue; }
                checkedCount++;
                object e = Com.EntityByHandle(doc, handle);
                if (e == null)
                {
                    JMap isu = new JMap();
                    isu.Add("code", "READBACK_MISSING");
                    isu.Add("handle", handle);
                    isu.Add("message", "handle " + handle + " does not resolve after apply");
                    issues.Add(isu);
                    continue;
                }
                // compare against what the caller ASKED FOR (intent), not against the snapshot taken
                // right after the set: a silently-ignored Layer assignment otherwise "verified" fine
                int srcIndex = ir.Int("i", -1);
                JMap srcItem = (srcIndex >= 0 && srcIndex < sourceItems.Count) ? sourceItems[srcIndex] : null;
                if (srcItem != null && op == "update")
                {
                    JMap set = srcItem.Map("set");
                    if (set != null)
                    {
                        if (set.Has("layer"))
                        {
                            string actualLayer = Com.Str(Com.TryGet(e, "Layer"));
                            string wantLayer2 = Com.Str(set.Get("layer"));
                            if (!EqStr(actualLayer, wantLayer2))
                                AddVerifyIssue(issues, "READBACK_LAYER", handle, "layer read back as '" + actualLayer + "', set requested '" + wantLayer2 + "'");
                        }
                        if (set.Has("text"))
                        {
                            string actualText = TextOf(e);
                            string wantText2 = Com.Str(set.Get("text"));
                            if (!EqStr(actualText, wantText2))
                                AddVerifyIssue(issues, "READBACK_TEXT", handle, "text read back as '" + actualText + "', set requested '" + wantText2 + "'");
                        }
                        if (set.Has("height"))
                        {
                            double actualH = Com.Dbl(Com.TryGet(e, "Height"));
                            double wantH = Com.Dbl(set.Get("height"));
                            if (Math.Abs(actualH - wantH) > tol)
                                AddVerifyIssue(issues, "READBACK_HEIGHT", handle, "height read back as " + actualH.ToString("R", CultureInfo.InvariantCulture) + ", set requested " + wantH.ToString("R", CultureInfo.InvariantCulture));
                        }
                    }
                }
                JMap xdataWant = ir.Map("xdata");
                if (xdataWant != null)
                {
                    string app = xdataWant.Str("app", "AI_MARK");
                    string want = xdataWant.Str("value", "");
                    string xgot = TryGetXData(e, app);
                    if (xgot == null || xgot.IndexOf(want, StringComparison.Ordinal) < 0)
                        AddVerifyIssue(issues, "XDATA_VERIFY_FAILED", handle, "xdata " + app + " read back as '" + (xgot ?? "(none)") + "', expected to contain '" + want + "'");
                }
                if (op == "add" && srcItem != null)
                {
                    string wantTypeA = srcItem.Str("type", null);
                    if (wantTypeA != null)
                    {
                        string actualTypeA = Com.Str(Com.TryGet(e, "ObjectName"));
                        if (Commands.KindOf(actualTypeA) != Commands.KindOf(wantTypeA) && !string.Equals(actualTypeA, wantTypeA, StringComparison.OrdinalIgnoreCase))
                            AddVerifyIssue(issues, "READBACK_TYPE", handle, "entity read back as '" + actualTypeA + "', add requested '" + wantTypeA + "'");
                    }
                    string wantLayerA = srcItem.Str("layer", null);
                    if (wantLayerA != null)
                    {
                        string actualLayerA = Com.Str(Com.TryGet(e, "Layer"));
                        if (!EqStr(actualLayerA, wantLayerA))
                            AddVerifyIssue(issues, "READBACK_LAYER", handle, "layer read back as '" + actualLayerA + "', add requested '" + wantLayerA + "'");
                    }
                    JMap geomA = srcItem.Map("geom");
                    if (geomA != null && geomA.Has("text"))
                    {
                        string actualTextA = TextOf(e);
                        string wantTextA = geomA.Str("text", null);
                        if (!EqStr(actualTextA, wantTextA))
                            AddVerifyIssue(issues, "READBACK_TEXT", handle, "text read back as '" + actualTextA + "', add requested '" + wantTextA + "'");
                    }
                }
                JMap after = ir.Map("after");
                if (after != null)
                {
                    string layer = Com.Str(Com.TryGet(e, "Layer"));
                    string wantLayer = after.Str("layer", null);
                    if (wantLayer != null && !EqStr(layer, wantLayer))
                    {
                        JMap isu = new JMap();
                        isu.Add("code", "READBACK_LAYER");
                        isu.Add("handle", handle);
                        isu.Add("message", "layer read back as '" + layer + "', expected '" + wantLayer + "'");
                        issues.Add(isu);
                    }
                    string wantText = after.Str("text", null);
                    if (wantText != null)
                    {
                        string actual = TextOf(e);
                        if (!EqStr(actual, wantText))
                        {
                            JMap isu = new JMap();
                            isu.Add("code", "READBACK_TEXT");
                            isu.Add("handle", handle);
                            isu.Add("message", "text read back as '" + actual + "', expected '" + wantText + "'");
                            issues.Add(isu);
                        }
                    }
                }
                JMap xd = ir.Map("xdata");
                if (xd != null)
                {
                    string app = xd.Str("app", "AI_MARK");
                    string want = xd.Str("value", "");
                    string got = TryGetXData(e, app);
                    if (got == null || got.IndexOf(want, StringComparison.Ordinal) < 0)
                    {
                        JMap isu = new JMap();
                        isu.Add("code", "READBACK_XDATA");
                        isu.Add("handle", handle);
                        isu.Add("message", "xdata " + app + " read back as '" + (got ?? "(none)") + "', expected to contain '" + want + "'");
                        issues.Add(isu);
                    }
                }
            }
            JMap v = new JMap();
            v.Add("ok", issues.Count == 0);
            v.Add("issues", issues);
            v.Add("checked", checkedCount);
            return v;
        }

        private static void AddVerifyIssue(List<object> issues, string code, string handle, string message)
        {
            JMap isu = new JMap();
            isu.Add("code", code);
            isu.Add("handle", handle);
            isu.Add("message", message);
            issues.Add(isu);
        }
        private static void RollbackGroup(object doc, int countBefore, Ctx ctx)
        {
            Com.TryCall(doc, "EndUndoMark");
            try
            {
                int now = Commands.CountOf(doc, "ModelSpace");
                int guard = 0;
                while (guard < 12)
                {
                    Ctx sendCtx = ctx;
                    Cmd.Send(doc, "_.U\n", sendCtx, "entity-rollback");
                    guard++;
                    int next = Commands.WaitIdle(doc, 4000);
                    if (next == countBefore && next == now) break;
                    now = next;
                    if (next == countBefore) break;
                }
                if (Commands.CountOf(doc, "ModelSpace") != countBefore)
                    ctx.Warn("ROLLBACK_INCOMPLETE", "rollback left ModelSpace.Count=" + Commands.CountOf(doc, "ModelSpace")
                        + " (baseline " + countBefore + ")");
            }
            catch (Exception e) { ctx.Warn("ROLLBACK_FAILED", "rollback failed: " + e.Message); }
        }
        // ------------------------------------------------------------------ diagnostics
        /// <summary>xprobe: dump what GetXData/SetXData really return on this CAD build.</summary>
        public static JMap XProbeCmd(JMap req, JMap args, Ctx ctx)
        {
            Cad cad = Cad.Attach(Commands.AppKindOf(req, args), false, ctx, Cad.ExplicitProgId(req, args));
            ctx.AppMap = cad.AppInfo();
            string matchedBy;
            object doc = cad.ResolveDoc(Commands.DocArgOf(req, args), args.Bool("allowActive", false), ctx, out matchedBy);
            ctx.DocMap = Cad.DocInfo(doc);
            string handle = args.Str("handle", null);
            string app = args.Str("app", "AI_MARK");
            JMap r = new JMap();
            object ent = Com.EntityByHandle(doc, handle);
            if (ent == null) { r.Add("error", "entity not found"); return r; }
            r.Add("handle", handle);
            r.Add("type", Com.Str(Com.TryGet(ent, "ObjectName")));
            try
            {
                object[] p = new object[] { app, null, null };
                ent.GetType().InvokeMember("GetXData", System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, ent, p);
                r.Add("byrefTypesClr", p[1] == null ? null : p[1].GetType().FullName);
                r.Add("byrefValsClr", p[2] == null ? null : p[2].GetType().FullName);
                r.Add("types", DumpObj(p[1]));
                r.Add("vals", DumpObj(p[2]));
            }
            catch (Exception ex) { r.Add("byrefError", Com.Hr(ex)); }
            try
            {
                object ret = Com.Call(ent, "GetXData", app);
                r.Add("returnClr", ret == null ? null : ret.GetType().FullName);
                r.Add("returnValue", DumpObj(ret));
            }
            catch (Exception ex) { r.Add("returnError", Com.Hr(ex)); }
            return r;
        }

        private static List<object> DumpObj(object v)
        {
            List<object> l = new List<object>();
            if (v == null) return l;
            Array a = v as Array;
            if (a == null) { l.Add(Com.Str(v)); return l; }
            foreach (object o in a) l.Add(Com.Str(o));
            return l;
        }
    }

}
