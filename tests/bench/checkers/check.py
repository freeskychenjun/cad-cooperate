#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""check.py - verdict logic for the M3-E4 CAD task benchmark.

One verdict per task: reads the captured step outputs (see run_bench.ps1), applies the
task's success criteria and prints ONE json object:

    {"ok": bool, "kind": "success|capability_gap|setup_unavailable", "reasons": [...],
     "metrics": {...}}

Kept in Python (not PowerShell) on purpose: the assertions are geometry/JSON arithmetic
(vertex lengths, bbox shifts, per-layer counts) and Python is the readable home for that;
the .ps1 runner stays a pure orchestrator (timing, process hygiene, JSON collection).

Usage (called by the runner):
    python check.py --task Q1-layer-stats --dir <task run dir> --expected <expected.json>
                    --tasks <tasks.json> --context <context.json>
"""

from __future__ import annotations

import json
import math
import os
import sys

PASS = "PASS"


def read_json(path):
    try:
        with open(path, "rb") as fh:
            raw = fh.read()
        if raw.startswith(b"\xef\xbb\xbf"):
            raw = raw[3:]
        return json.loads(raw.decode("utf-8", errors="replace"))
    except Exception:
        return None


def read_text(path):
    try:
        with open(path, "rb") as fh:
            return fh.read().decode("utf-8", errors="replace")
    except Exception:
        return ""


class Steps(object):
    def __init__(self, rundir):
        self.dir = rundir

    def meta(self, name):
        return read_json(os.path.join(self.dir, name + ".meta.json")) or {}

    def json(self, name):
        return read_json(os.path.join(self.dir, name + ".json"))

    def raw(self, name):
        return read_text(os.path.join(self.dir, name + ".raw.txt"))

    def exit_code(self, name):
        return self.meta(name).get("exit")

    def wall(self, name):
        return self.meta(name).get("wallMs")

    def result(self, name):
        payload = self.json(name) or {}
        return payload.get("result") or {}

    def error_code(self, name):
        payload = self.json(name) or {}
        return (payload.get("error") or {}).get("code")


def ok_step(st, name, reasons):
    meta = st.meta(name)
    if not meta:
        reasons.append("缺少步骤输出: " + name)
        return False
    if meta.get("parseError"):
        reasons.append("步骤 %s 输出不是合法 JSON: %s" % (name, meta.get("parseError")))
        return False
    if meta.get("exit") != 0:
        reasons.append("步骤 %s 退出码 %s（期望 0）%s" % (name, meta.get("exit"), _err_hint(st, name)))
        return False
    return True


def report_file(st, filename):
    """audit.py writes its machine report to a file (stdout stays ASCII), so checkers that
    need the issue list must load <rundir>/<filename> instead of the step's stdout."""
    return read_json(os.path.join(st.dir, filename))


def _err_hint(st, name):
    payload = st.json(name) or {}
    err = payload.get("error") or {}
    if err:
        return " [" + str(err.get("code")) + ": " + str(err.get("message"))[:120] + "]"
    return ""


def points_of(entity):
    pts = entity.get("points") or entity.get("vertices") or []
    out = []
    for p in pts:
        if isinstance(p, (list, tuple)) and len(p) >= 2:
            try:
                out.append((float(p[0]), float(p[1])))
            except (TypeError, ValueError):
                pass
    return out


def poly_length(pts, closed):
    total = 0.0
    for i in range(len(pts) - 1):
        total += math.dist(pts[i], pts[i + 1])
    if closed and len(pts) > 2:
        total += math.dist(pts[-1], pts[0])
    return total


def bbox_of(entities):
    xs, ys = [], []
    for e in entities:
        bb = e.get("bbox") or {}
        mn = bb.get("min") or e.get("start")
        mx = bb.get("max") or e.get("end")
        if isinstance(mn, (list, tuple)) and len(mn) >= 2:
            xs.append(float(mn[0]))
            ys.append(float(mn[1]))
        if isinstance(mx, (list, tuple)) and len(mx) >= 2:
            xs.append(float(mx[0]))
            ys.append(float(mx[1]))
        for p in points_of(e):
            xs.append(p[0])
            ys.append(p[1])
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys))


def close(a, b, tol=1e-4):
    return abs(float(a) - float(b)) <= tol


# --------------------------------------------------------------------------- checks


def chk_Q1(st, exp, ctx, reasons):
    if not ok_step(st, "read1", reasons):
        return False, {}
    ents = st.result("read1").get("entities") or []
    river = [e for e in ents if e.get("layer") == "RIVER"]
    want = exp["river"]
    if len(river) != want["count"]:
        reasons.append("RIVER 实体数 %d != 期望 %d" % (len(river), want["count"]))
    kinds = sorted({e.get("kind") for e in river})
    if kinds != ["LWPOLYLINE"]:
        reasons.append("RIVER 实体类型 %s，期望全部 LWPOLYLINE" % kinds)
    total = 0.0
    for e in river:
        total += poly_length(points_of(e), bool(e.get("closed")))
    if not close(total, want["totalLength"], 0.01):
        reasons.append("RIVER 总长度 %.4f != 期望 %.4f" % (total, want["totalLength"]))
    also = [e for e in ents if e.get("layer") == "ANNO"]
    return not reasons, {"riverCount": len(river), "riverLength": round(total, 4),
                         "annoCount": len(also)}


def chk_Q2(st, exp, ctx, reasons):
    if not ok_step(st, "read1", reasons):
        return False, {}
    ents = st.result("read1").get("entities") or []
    want = exp["designWaterTexts"]
    hits = [e for e in ents if "设计水位" in str(e.get("text") or "")]
    if len(hits) != want["count"]:
        reasons.append("含「设计水位」的文字 %d 条 != 期望 %d 条" % (len(hits), want["count"]))
    no_handle = [e for e in hits if not e.get("handle")]
    if no_handle:
        reasons.append("%d 条命中没有句柄" % len(no_handle))
    bad_text = [e for e in hits if "m" not in str(e.get("text") or "")]
    if bad_text:
        reasons.append("命中文字缺少数值单位 m: %r" % [e.get("text") for e in bad_text[:2]])
    return not reasons, {"hits": len(hits),
                         "handles": [e.get("handle") for e in hits],
                         "texts": [e.get("text") for e in hits]}


def chk_Q3(st, exp, ctx, reasons):
    if not ok_step(st, "read1", reasons):
        return False, {}
    ents = st.result("read1").get("entities") or []
    polys = [e for e in ents if e.get("kind") in ("LWPOLYLINE", "POLYLINE")]
    unclosed = [e for e in polys if not e.get("closed")]
    want = exp["unclosedPolylines"]
    if len(unclosed) != want["count"]:
        reasons.append("未闭合多段线 %d 条 != 期望 %d 条" % (len(unclosed), want["count"]))
    ratios = []
    for e in unclosed:
        pts = points_of(e)
        if len(pts) < 2:
            ratios.append((e.get("handle"), None))
            continue
        gap = math.dist(pts[0], pts[-1])
        per = poly_length(pts, False)
        ratios.append((e.get("handle"), gap / per if per else 1.0))
    almost = [r for _h, r in ratios if r is not None and 0 < r <= 0.02]
    openish = [r for _h, r in ratios if r is not None and r > 0.5]
    if len(almost) != want["almostClosedCount"]:
        reasons.append("“差一点闭合”的 %d 条 != 期望 %d 条" % (len(almost), want["almostClosedCount"]))
    if len(openish) != want["openCount"]:
        reasons.append("真正开放的 %d 条 != 期望 %d 条" % (len(openish), want["openCount"]))
    return not reasons, {"unclosed": len(unclosed), "almostClosed": len(almost),
                         "open": len(openish), "handles": [e.get("handle") for e in unclosed]}


def chk_Q4(st, exp, ctx, reasons):
    if not ok_step(st, "open1", reasons) or not ok_step(st, "read1", reasons):
        return False, {}
    want = exp["bigRead"]
    res = st.result("read1")
    ents = res.get("entities") or []
    got_total = res.get("counts", {}).get("model")
    if got_total != want["modelCount"] and len(ents) != want["modelCount"]:
        reasons.append("实体总数 %s/%d != 期望 %d" % (got_total, len(ents), want["modelCount"]))
    if res.get("truncated"):
        reasons.append("read 结果被截断（truncated=true），任务目标无法确认")
    got_layers = {}
    for e in ents:
        got_layers[e.get("layer")] = got_layers.get(e.get("layer"), 0) + 1
    for layer, n in want["byLayer"].items():
        if got_layers.get(layer) != n:
            reasons.append("图层 %s 实体数 %s != 期望 %d" % (layer, got_layers.get(layer), n))
    extra = sorted(set(got_layers) - set(want["byLayer"]))
    if extra:
        reasons.append("出现预期外图层: %s" % extra)
    return not reasons, {"entities": len(ents), "layers": len(got_layers),
                         "readWallMs": st.wall("read1")}


def chk_gap(st, exp, ctx, reasons):
    """Capability-gap probe: the planned primitive must NOT exist today, and the command
    surface must reject it cleanly (BAD_ARGS exit 2, not a hang / silent success)."""
    meta = st.meta("probe1")
    if not meta:
        reasons.append("缺少探针步骤输出")
        return False, {}
    code = st.error_code("probe1")
    expect = exp.get("expect") or {}
    if meta.get("exit") != 2:
        reasons.append("缺能力探针退出码 %s，期望 2（命令应被干净拒绝）" % meta.get("exit"))
    if code != "BAD_ARGS":
        reasons.append("缺能力探针错误码 %s，期望 BAD_ARGS" % code)
    raw = st.raw("probe1").lower()
    if "unknown command" not in raw and "bad_args" not in raw:
        reasons.append("拒绝信息里没有可读原因（应说明命令不存在）")
    return not reasons, {"missing": expect.get("missing"), "plannedIn": expect.get("plannedIn")}


def chk_W4(st, exp, ctx, reasons):
    for name in ("open1", "prep1", "sec1", "read1", "undo1", "prep2", "sec2", "read2"):
        if name.endswith("1") and name.startswith("prep"):
            pass
    if not ok_step(st, "prep1", reasons) or not ok_step(st, "sec1", reasons):
        return False, {}
    if not (st.result("sec1").get("verify") or {}).get("ok"):
        reasons.append("第一次出图 verify.ok=false")
    if not ok_step(st, "read1", reasons) or not ok_step(st, "undo1", reasons):
        return False, {}
    if not ok_step(st, "prep2", reasons):
        return False, {}
    sec2 = st.meta("sec2") or {}
    if sec2.get("exit") != 0:
        payload = st.json("sec2") or {}
        err = payload.get("error") or {}
        detail = str(err.get("detail") or "") + " " + str(err.get("message") or "")
        if err.get("code") == "VERIFY_FAILED" and "MODEL_BBOX_INTERNAL" in detail:
            # product defect, not a benchmark failure: section's own read-back check does not
            # subtract a non-zero origin when comparing the ground bbox with W/H, so the whole
            # batch is rolled back. Reported to the CAD side; kept as a first-class finding.
            reasons.append("产品缺陷：section 在 origin != [0,0] 时自检失败（MODEL_BBOX_INTERNAL，已整体回滚）")
            return False, {"productBug": "MODEL_BBOX_INTERNAL", "origin": "50,0",
                           "sec2Exit": sec2.get("exit")}
        reasons.append("第二次出图退出码 %s（期望 0）%s" % (sec2.get("exit"), _err_hint(st, "sec2")))
        return False, {}
    if not (st.result("sec2").get("verify") or {}).get("ok"):
        reasons.append("第二次出图 verify.ok=false")
    if not ok_step(st, "read2", reasons):
        return False, {}
    b1 = bbox_of(st.result("read1").get("entities") or [])
    b2 = bbox_of(st.result("read2").get("entities") or [])
    if not b1 or not b2:
        reasons.append("读不到平移前后的 bbox")
        return False, {}
    shift = b2[0] - b1[0]
    if not close(shift, 50.0, 0.01):
        reasons.append("平移量 x=%.4f != 50（±0.01）" % shift)
    c1 = st.result("read1").get("counts", {}).get("model")
    c2 = st.result("read2").get("counts", {}).get("model")
    if c1 != c2:
        reasons.append("重画后实体数 %s != 原图 %s" % (c2, c1))
    undo = st.result("undo1")
    if undo.get("countAfter") not in (0, None):
        reasons.append("撤销后实体数 %s（期望 0）" % undo.get("countAfter"))
    return not reasons, {"shiftX": round(shift, 4), "entities": c2,
                         "undoCountAfter": undo.get("countAfter")}


def chk_S1(st, exp, ctx, reasons):
    for name in ("prep1", "sec1", "read1", "verify1"):
        if not ok_step(st, name, reasons):
            return False, {}
    verify = st.result("sec1").get("verify") or {}
    if not verify.get("ok"):
        reasons.append("section.verify.ok=false: %s" % (verify.get("issues") or [])[:3])
    expected = st.result("sec1").get("expected") or {}
    got = st.result("read1").get("counts", {}).get("model")
    if expected.get("total") and got != expected.get("total"):
        reasons.append("回读实体数 %s != 出图 expected.total %s" % (got, expected.get("total")))
    if not _verify_passed(st, "verify1", reasons):
        return False, {}
    return not reasons, {"entities": got, "assertions": _verify_assertions(st.raw("verify1"))}


def chk_S2(st, exp, ctx, reasons):
    for name in ("prep1", "sec1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    res = st.result("sec1")
    verify = res.get("verify") or {}
    if not verify.get("ok"):
        reasons.append("套绘 verify.ok=false: %s" % (verify.get("issues") or [])[:3])
    series = res.get("series") or []
    expected = res.get("expected") or {}
    ground_layers = [k for k in (expected.get("byLayer") or {}) if "GROUND" in k.upper()]
    if len(series) < 4 and len(ground_layers) < 4:
        reasons.append("套绘序列数 %d（series）/%d（ground 层）< 4" % (len(series), len(ground_layers)))
    total = expected.get("total") or 0
    if total < 40:
        reasons.append("套绘实体数 %s 偏少（期望 >=40）" % total)
    read_ents = st.result("read1").get("entities") or []
    polys = [e for e in read_ents if e.get("kind") in ("LWPOLYLINE", "POLYLINE")]
    if len(polys) < 4:
        reasons.append("回读到 %d 条多段线（期望 >=4 条序列线）" % len(polys))
    return not reasons, {"series": len(series), "groundLayers": len(ground_layers),
                         "entities": total, "polylines": len(polys)}


def chk_S3(st, exp, ctx, reasons):
    for name in ("prep1", "sec1", "read1", "audit1", "undo1", "prep2", "sec2", "read2", "audit2"):
        if not ok_step(st, name, reasons):
            return False, {}
    undo = st.result("undo1")
    if undo.get("countAfter") not in (0, None):
        reasons.append("撤销旧图后实体数 %s（期望 0）" % undo.get("countAfter"))
    verify = st.result("sec2").get("verify") or {}
    if not verify.get("ok"):
        reasons.append("改水位后出图 verify.ok=false: %s" % (verify.get("issues") or [])[:3])
    # the water line must sit at the NEW level: y = (43.00 - elevBase) * 1000 / vScale
    model_path = os.path.join(st.dir, "s3-w4300.model.json")
    model = read_json(model_path) or {}
    elev_base = (model.get("computed") or {}).get("elevBase")
    scale = model.get("scale") or {}
    vscale = scale.get("vertical") or 50.0
    if elev_base is None:
        elev_base = (model.get("bbox") or {}).get("elevBase")
    if elev_base is None:
        reasons.append("读不到改水位模型的 elevBase，无法核对水位线位置")
    else:
        want_y = (43.0 - elev_base) * 1000.0 / vscale
        water = [e for e in (st.result("read2").get("entities") or [])
                 if str(e.get("layer") or "").upper().endswith("WATER")
                 or "WATER" in str(e.get("layer") or "").upper()]
        ys = []
        for e in water:
            if e.get("kind") == "LINE":
                pts = points_of(e)
                ys.extend([p[1] for p in pts])
            else:
                bb = e.get("bbox") or {}
                mn = bb.get("min") or []
                if mn:
                    ys.append(float(mn[1]))
        if not ys:
            reasons.append("回读里找不到水位线实体")
        elif not any(close(y, want_y, 0.02) for y in ys):
            reasons.append("水位线 y=%s 都不在期望 %.4f（43.00m）" % ([round(y, 3) for y in ys][:4], want_y))
    for name, fname in (("audit1", "s3-audit1.json"), ("audit2", "s3-audit2.json")):
        rep = report_file(st, fname) or {}
        if not rep.get("ok"):
            reasons.append("%s 没有产出可用报告（%s）" % (name, fname))
    return not reasons, {"undoCountAfter": undo.get("countAfter"),
                         "audit1Issues": (report_file(st, "s3-audit1.json") or {}).get("summary", {}).get("issues"),
                         "audit2Issues": (report_file(st, "s3-audit2.json") or {}).get("summary", {}).get("issues")}


def chk_S4(st, exp, ctx, reasons):
    for name in ("prep1", "sec1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    prep = _first_json_block(st.raw("prep1")) or {}
    verify = st.result("sec1").get("verify") or {}
    if not verify.get("ok"):
        reasons.append("出图 verify.ok=false: %s" % (verify.get("issues") or [])[:3])
    point_count = prep.get("pointCount") or (prep.get("pointCounts") or [0])[0]
    expected = st.result("sec1").get("expected") or {}
    if expected.get("total", 0) < 40:
        reasons.append("出图实体数 %s 偏少（113 点断面期望 >=40）" % expected.get("total"))
    got = st.result("read1").get("counts", {}).get("model")
    if got != expected.get("total"):
        reasons.append("回读实体数 %s != expected.total %s" % (got, expected.get("total")))
    return not reasons, {"points": point_count, "entities": got}


def chk_C1(st, exp, ctx, reasons):
    for name in ("open1", "read1", "info1"):
        if not ok_step(st, name, reasons):
            return False, {}
    meta = st.meta("audit1")
    if meta.get("exit") != 0:
        reasons.append("audit 在干净图上退出码 %s（期望 0）" % meta.get("exit"))
    report = report_file(st, "audit-clean.json") or {}
    issues = (report.get("summary") or {}).get("issues")
    if issues != 0:
        reasons.append("干净图体检命中 %s 条（期望 0，属于误报）" % issues)
    if not (report.get("gate") or {}).get("pass"):
        reasons.append("干净图门禁未通过")
    return not reasons, {"issues": issues}


def chk_C2(st, exp, ctx, reasons):
    for name in ("open1", "read1", "info1"):
        if not ok_step(st, name, reasons):
            return False, {}
    meta = st.meta("audit1")
    if meta.get("exit") != 3:
        reasons.append("问题图 audit 退出码 %s（期望 3 = 门禁拦截）" % meta.get("exit"))
    report = report_file(st, "audit-problem.json") or {}
    summary = report.get("summary") or {}
    issues = summary.get("issues") or 0
    blockers = (summary.get("bySeverity") or {}).get("blocker") or 0
    if issues < 15:
        reasons.append("问题图命中 %d 条（期望 >=15）" % issues)
    if blockers < 1:
        reasons.append("问题图 blocker=%d（期望 >=1）" % blockers)
    md = os.path.join(st.dir, "audit-problem.md")
    if not os.path.exists(md) or os.path.getsize(md) < 2000:
        reasons.append("人读报告 audit-problem.md 缺失或过短")
    return not reasons, {"issues": issues, "blockers": blockers,
                         "topRule": _top_rule(report)}


def chk_C3(st, exp, ctx, reasons):
    for name in ("open1", "prep1", "precheck1", "sec1", "read1", "verify1"):
        if not ok_step(st, name, reasons):
            return False, {}
    if not _verify_passed(st, "precheck1", reasons):
        return False, {}
    if not _verify_passed(st, "verify1", reasons):
        return False, {}
    expected = st.result("sec1").get("expected") or {}
    got = st.result("read1").get("counts", {}).get("model")
    if got != expected.get("total"):
        reasons.append("回读实体数 %s != expected.total %s" % (got, expected.get("total")))
    if not (st.result("sec1").get("verify") or {}).get("ok"):
        reasons.append("section.verify.ok=false")
    return not reasons, {"entities": got, "assertions": _verify_assertions(st.raw("verify1"))}


def chk_X1(st, exp, ctx, reasons):
    meta = st.meta("status1")
    if not meta:
        reasons.append("缺少 status 步骤输出")
        return False, {}
    if meta.get("exit") != 2:
        reasons.append("无 CAD 时 status 退出码 %s（期望 2，不是 0 也不是崩）" % meta.get("exit"))
    code = st.error_code("status1")
    if code != "NO_INSTANCE":
        reasons.append("无 CAD 时错误码 %s（期望 NO_INSTANCE）" % code)
    payload = st.json("status1") or {}
    if payload.get("ok") is not False:
        reasons.append("无 CAD 时 ok 字段应为 false")
    wall = meta.get("wallMs") or 0
    if wall > 30000:
        reasons.append("无 CAD 时耗时 %.0f ms（>30 s，说明在死等）" % wall)
    return not reasons, {"errorCode": code, "wallMs": wall}


def chk_X2(st, exp, ctx, reasons):
    products = ctx.get("zwcadProducts") or []
    meta = st.meta("status1") or {}
    payload = st.json("status1") or {}
    code = (payload.get("error") or {}).get("code")
    if len(products) >= 2:
        if meta.get("exit") != 2 or code != "MULTI_PRODUCT":
            reasons.append("双产品同开时未返回 MULTI_PRODUCT（exit=%s code=%s）"
                           % (meta.get("exit"), code))
        return not reasons, {"branch": "multi-product", "errorCode": code}
    # single product branch: MUST attach to the running product, never guess/launch another
    if meta.get("exit") != 0:
        reasons.append("单产品时 status 退出码 %s（期望 0）%s" % (meta.get("exit"), _err_hint(st, "status1")))
    app = payload.get("app") or {}
    body = json.dumps(payload, ensure_ascii=False)
    expected_product = (ctx.get("expectedProduct") or "").lower()
    if expected_product and expected_product not in body.lower():
        reasons.append("单产品时未附着到 %s（app.product=%s）" % (expected_product, app.get("product")))
    if payload.get("ok") is not True:
        reasons.append("单产品时 status 应 ok:true")
    return not reasons, {"branch": "single-product", "product": app.get("product"),
                         "progId": app.get("progId")}


def chk_X3(st, exp, ctx, reasons):
    for name in ("bad1", "bad2", "bad3"):
        meta = st.meta(name)
        if not meta:
            reasons.append("缺少步骤 " + name)
            continue
        if meta.get("exit") != 2:
            reasons.append("%s 退出码 %s（期望 2 = 前置拒绝）" % (name, meta.get("exit")))
        payload = st.json(name) or {}
        code = (payload.get("error") or {}).get("code")
        if code not in ("BAD_DATA", "TOO_WIDE", "BAD_ARGS"):
            reasons.append("%s 错误码 %s（期望 BAD_DATA 类）" % (name, code))
    leftover = [n for n in ("bad-2points.model.json", "bad-nonmono.model.json", "bad-empty.model.json")
                if os.path.exists(os.path.join(st.dir, n))]
    if leftover:
        reasons.append("脏数据仍然产出了模型文件: %s" % leftover)
    return not reasons, {}



# --------------------------------------------------------------------------- E2 write tasks
# These assert the RESULT (re-read geometry/text after the write), not just exit codes:
# the entity response is only trusted as a claim, every task ends with a fresh `read -Mode geom`
# and the checker recomputes the expected state from that independent snapshot.

def _res(st, name):
    return st.result(name) or {}


def _applied_of(rep):
    applied = rep.get("applied")
    if isinstance(applied, list):
        return len(applied)
    try:
        return int(applied)
    except (TypeError, ValueError):
        return -1


def _verify_ok(rep, name, reasons):
    verify = rep.get("verify") or {}
    if not verify.get("ok"):
        issues = verify.get("issues")
        reasons.append("%s verify.ok=false: %s" % (name, str(issues)[:300]))
        return False
    if rep.get("rolledBack"):
        reasons.append("%s rolledBack=true（回读校验失败并整体回滚）" % name)
        return False
    failed = rep.get("failed")
    if isinstance(failed, list) and failed:
        reasons.append("%s failed 非空: %s" % (name, str(failed)[:300]))
        return False
    return True


def _entities(st, name):
    res = _res(st, name)
    ents = res.get("entities")
    if ents is None:
        ents = res.get("items")
    return [e for e in (ents or []) if isinstance(e, dict)]


def _query_items(st, name):
    return [e for e in (_res(st, name).get("items") or []) if isinstance(e, dict)]


def _query_total(st, name):
    res = _res(st, name)
    total = res.get("total")
    if total is None:
        total = len(res.get("items") or [])
    try:
        return int(total)
    except (TypeError, ValueError):
        return -1


def _text(e):
    return str(e.get("text") if e.get("text") is not None else "")


def _height(e):
    try:
        return float(e.get("height"))
    except (TypeError, ValueError):
        return None


def _layer(e):
    return str(e.get("layer") or "")


def _kind(e):
    return str(e.get("kind") or e.get("type") or "").upper()


def _is_line(e):
    return _kind(e) == "LINE" or "LINE" in _kind(e) and "POLY" not in _kind(e)


def _pts(e):
    pts = e.get("points") or e.get("vertices") or []
    out = []
    for p in pts:
        if isinstance(p, (list, tuple)) and len(p) >= 2:
            try:
                out.append((float(p[0]), float(p[1])))
            except (TypeError, ValueError):
                pass
    a = e.get("start")
    b = e.get("end")
    if isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)):
        try:
            out = [(float(a[0]), float(a[1])), (float(b[0]), float(b[1]))]
        except (TypeError, ValueError, IndexError):
            pass
    return out


def _degenerate(e, tol=1e-6):
    pts = _pts(e)
    if len(pts) >= 2:
        x0, y0 = pts[0]
        return all(abs(x - x0) <= tol and abs(y - y0) <= tol for x, y in pts)
    bb = e.get("bbox") or {}
    mn = bb.get("min")
    mx = bb.get("max")
    if not (isinstance(mn, (list, tuple)) and isinstance(mx, (list, tuple))):
        return False
    try:
        return abs(float(mx[0]) - float(mn[0])) <= tol and abs(float(mx[1]) - float(mn[1])) <= tol
    except (TypeError, ValueError, IndexError):
        return False


def chk_W1(st, exp, ctx, reasons):
    for name in ("open1", "query1", "gen1", "entity1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    q = _query_items(st, "query1")
    rep = _res(st, "entity1")
    if len(q) < 3:
        reasons.append("query 命中 %d 条「设计水位」文字（夹具应有 3 条）" % len(q))
    if not _verify_ok(rep, "entity1", reasons):
        return False, {}
    applied = _applied_of(rep)
    if applied != len(q):
        reasons.append("entity applied=%s != query 命中 %s" % (applied, len(q)))
    ents = _entities(st, "read1")
    by_handle = {e.get("handle"): e for e in ents}
    new_text = "设计水位（复核）"
    wrong = []
    for it in q:
        h = it.get("handle")
        e = by_handle.get(h)
        if e is None:
            wrong.append((h, "实体消失"))
        elif _text(e) != new_text:
            wrong.append((h, _text(e)))
    if wrong:
        reasons.append("回读文字与预期不符: %s" % wrong[:3])
    stale = [(_text(e), e.get("handle")) for e in ents
             if "设计水位" in _text(e) and not _text(e).startswith("设计水位（复核）")]
    if stale:
        reasons.append("仍有未改的「设计水位」文字: %s" % stale[:3])
    return not reasons, {"queryHits": len(q), "applied": applied,
                         "newTexts": sum(1 for e in ents if _text(e) == new_text)}


def chk_W2(st, exp, ctx, reasons):
    for name in ("open1", "query1", "gen1", "entity1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    q = _query_items(st, "query1")
    rep = _res(st, "entity1")
    if len(q) < 4:
        reasons.append("query -Layer RIVER 命中 %d 条（夹具应有 4 条多段线）" % len(q))
    if not _verify_ok(rep, "entity1", reasons):
        return False, {}
    applied = _applied_of(rep)
    if applied != len(q):
        reasons.append("entity applied=%s != query 命中 %s" % (applied, len(q)))
    ents = _entities(st, "read1")
    old = [e for e in ents if _layer(e) == "RIVER"]
    new = [e for e in ents if _layer(e) == "RIVER-2"]
    if old:
        reasons.append("RIVER 图层仍有 %d 个实体（应已全部移走）" % len(old))
    if len(new) != len(q):
        reasons.append("RIVER-2 图层实体数 %d != 期望 %d" % (len(new), len(q)))
    return not reasons, {"queryHits": len(q), "applied": applied, "moved": len(new)}


def chk_W3(st, exp, ctx, reasons):
    for name in ("open1", "query1", "gen1", "entity1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    q = _query_items(st, "query1")
    rep = _res(st, "entity1")
    if len(q) < 7:
        reasons.append("query -Layer ANNO -Type text 命中 %d 条（夹具应有 7 条）" % len(q))
    if not _verify_ok(rep, "entity1", reasons):
        return False, {}
    applied = _applied_of(rep)
    if applied != len(q):
        reasons.append("entity applied=%s != query 命中 %s" % (applied, len(q)))
    ents = _entities(st, "read1")
    anno = [e for e in ents if _layer(e) == "ANNO" and _kind(e) in ("TEXT", "MTEXT", "ACDBTEXT")]
    bad = [(e.get("handle"), _height(e)) for e in anno if _height(e) is None or abs(_height(e) - 2.5) > 1e-6]
    if bad:
        reasons.append("ANNO 层仍有非 2.5 字高: %s" % bad[:3])
    if len(anno) < len(q):
        reasons.append("ANNO 层文字数 %d < query 命中 %d" % (len(anno), len(q)))
    return not reasons, {"queryHits": len(q), "applied": applied,
                         "heights": sorted({_height(e) for e in anno})}


def chk_W5(st, exp, ctx, reasons):
    for name in ("open1", "query1", "gen1", "entity1", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    q = _query_items(st, "query1")
    zero_q = [it for it in q if _degenerate(it)]
    rep = _res(st, "entity1")
    if len(zero_q) != 2:
        reasons.append("query 里零长线 %d 条（夹具应有 2 条）" % len(zero_q))
    if not _verify_ok(rep, "entity1", reasons):
        return False, {}
    applied = _applied_of(rep)
    if applied != len(zero_q):
        reasons.append("entity applied=%s != 待删零长线 %s" % (applied, len(zero_q)))
    ents = _entities(st, "read1")
    zero_left = [(e.get("handle"), _kind(e)) for e in ents
                 if _kind(e) in ("LINE", "LWPOLYLINE", "POLYLINE") and _degenerate(e)]
    if zero_left:
        reasons.append("回读里仍有零长度实体: %s" % zero_left[:3])
    gone = {it.get("handle") for it in zero_q}
    still = [e.get("handle") for e in ents if e.get("handle") in gone]
    if still:
        reasons.append("已删的 handle 仍在回读里: %s" % still[:3])
    return not reasons, {"queryLines": len(q), "zeroFound": len(zero_q), "applied": applied}


def chk_W6(st, exp, ctx, reasons):
    for name in ("open1", "query1", "gen1", "entity1", "query2", "entity2", "query3", "read1"):
        if not ok_step(st, name, reasons):
            return False, {}
    q1 = _query_items(st, "query1")
    rep1 = _res(st, "entity1")
    if len(q1) != 2:
        reasons.append("query1 命中 %d 条（应正好 2 条待标记）" % len(q1))
    if not _verify_ok(rep1, "entity1", reasons):
        return False, {}
    if _applied_of(rep1) != len(q1):
        reasons.append("打标 applied=%s != %d" % (_applied_of(rep1), len(q1)))
    marked = _query_total(st, "query2")
    if marked != len(q1):
        reasons.append("query -XData 筛出 %d 条（期望 %d）—— 打标没生效" % (marked, len(q1)))
    rep2 = _res(st, "entity2")
    if not _verify_ok(rep2, "entity2", reasons):
        return False, {}
    if _applied_of(rep2) != len(q1):
        reasons.append("删除 applied=%s != %d" % (_applied_of(rep2), len(q1)))
    left = _query_total(st, "query3")
    if left != 0:
        reasons.append("删除后 query -XData 仍有 %d 条" % left)
    gone = {it.get("handle") for it in q1}
    still = [e.get("handle") for e in _entities(st, "read1") if e.get("handle") in gone]
    if still:
        reasons.append("按标记删除后实体仍在: %s" % still[:3])
    return not reasons, {"marked": marked, "appliedMark": _applied_of(rep1),
                         "appliedDelete": _applied_of(rep2), "leftAfterDelete": left}



# --------------------------------------------------------------------------- E2 negative tests
# Each negative task asserts the EXPECTED FAILURE semantics: a bad anchor must leave the
# drawing untouched, a bad write must roll back, and a successful batch must be undoable.

def _fingerprint(st, name):
    fp = {}
    for e in _entities(st, name):
        h = e.get("handle")
        if not h:
            continue
        fp[h] = (_kind(e), _layer(e), _text(e), _height(e),
                 tuple((round(x, 6), round(y, 6)) for x, y in _pts(e)))
    return fp


def _fp_diff(before, after):
    keys = set(before) | set(after)
    return [h for h in keys if before.get(h) != after.get(h)]


def chk_N1(st, exp, ctx, reasons):
    for name in ("open1", "read_before", "gen1", "corrupt1", "read_after"):
        if not ok_step(st, name, reasons):
            return False, {}
    meta = st.meta("entity1") or {}
    code = st.error_code("entity1")
    if meta.get("exit") != 2:
        reasons.append("坏锚点 entity 退出码 %s（期望 2 = 非门禁拒绝）" % meta.get("exit"))
    if code != "ANCHOR_MISSING":
        reasons.append("坏锚点错误码 %s（期望 ANCHOR_MISSING）" % code)
    payload = st.json("entity1") or {}
    applied = (payload.get("result") or {}).get("applied")
    if applied not in (0, None):
        reasons.append("坏锚点批次仍 applied=%s（必须一条都不改）" % applied)
    before = _fingerprint(st, "read_before")
    after = _fingerprint(st, "read_after")
    changed = _fp_diff(before, after)
    if changed:
        reasons.append("坏锚点后文档指纹变化（应完全不变）: %s" % changed[:5])
    return not reasons, {"errorCode": code, "entities": len(before), "changed": len(changed)}


def chk_N2(st, exp, ctx, reasons):
    for name in ("open1", "read_before", "query1", "gen1", "read_after"):
        if not ok_step(st, name, reasons):
            return False, {}
    meta = st.meta("entity1") or {}
    code = st.error_code("entity1")
    if meta.get("exit") not in (2, 3):
        reasons.append("非法 set 的 entity 退出码 %s（期望 2/3 = 失败，不能 ok:true）" % meta.get("exit"))
    if code not in ("VERIFY_FAILED", "BAD_DATA", "BAD_ARGS", "ROLLBACK_FAILED"):
        reasons.append("非法 set 错误码 %s（期望 VERIFY_FAILED 类）" % code)
    payload = st.json("entity1") or {}
    res = payload.get("result") or {}
    if res.get("rolledBack") is not True:
        reasons.append("result.rolledBack 不是 true（回滚未发生）")
    before = _fingerprint(st, "read_before")
    after = _fingerprint(st, "read_after")
    changed = _fp_diff(before, after)
    if changed:
        reasons.append("回滚后文档指纹仍有变化: %s" % changed[:5])
    return not reasons, {"errorCode": code, "rolledBack": res.get("rolledBack"),
                         "entities": len(before), "changed": len(changed)}


def chk_N3(st, exp, ctx, reasons):
    for name in ("open1", "read_before", "query1", "gen1", "entity1", "undo1", "read_after"):
        if not ok_step(st, name, reasons):
            return False, {}
    if not _verify_ok(_res(st, "entity1"), "entity1", reasons):
        return False, {}
    before = _fingerprint(st, "read_before")
    after = _fingerprint(st, "read_after")
    changed = _fp_diff(before, after)
    if changed:
        reasons.append("undo 后指纹未恢复: %s" % changed[:5])
    undo = _res(st, "undo1")
    count_after = undo.get("countAfter")
    if count_after is not None and int(count_after) != len(after):
        reasons.append("undo 报 countAfter=%s，但回读实体数 %d" % (count_after, len(after)))
    return not reasons, {"entities": len(before), "changedAfterUndo": len(changed)}



def chk_N4(st, exp, ctx, reasons):
    for name in ("open1", "read_before", "snapshot1", "query1", "gen1", "entity1", "diff1", "read_after"):
        if not ok_step(st, name, reasons):
            return False, {}
    rep = _res(st, "entity1")
    if not _verify_ok(rep, "entity1", reasons):
        return False, {}
    diff = _res(st, "diff1")
    added = diff.get("added") or []
    removed = diff.get("removed") or []
    changed = diff.get("changed") or []
    if added:
        reasons.append("diff.added 非空（只应改文字）: %s" % [a.get("handle") for a in added][:3])
    if removed:
        reasons.append("diff.removed 非空（只应改文字）: %s" % [r.get("handle") for r in removed][:3])
    q = _query_items(st, "query1")
    want_handles = {it.get("handle") for it in q}
    got_handles = {c.get("handle") for c in changed}
    if len(changed) != len(want_handles):
        reasons.append("diff.changed %d 条 != 期望 %d 条" % (len(changed), len(want_handles)))
    missing = want_handles - got_handles
    if missing:
        reasons.append("diff 漏报改动 handle: %s" % sorted(missing)[:5])
    bad_fields = []
    for c in changed:
        fields = c.get("fields") or {}
        text_delta = fields.get("text") or fields.get("Text")
        if not isinstance(text_delta, dict):
            bad_fields.append((c.get("handle"), "no text before/after"))
            continue
        before = str(text_delta.get("before", ""))
        after = str(text_delta.get("after", ""))
        if "设计水位" not in before or after != "临时改":
            bad_fields.append((c.get("handle"), before, after))
    if bad_fields:
        reasons.append("diff 的 text before/after 不符: %s" % bad_fields[:3])
    after_ents = {e.get("handle"): e for e in _entities(st, "read_after")}
    stale = [h for h in want_handles if _text(after_ents.get(h) or {}) != "临时改"]
    if stale:
        reasons.append("回读里这些 handle 的文字不是「临时改」: %s" % stale[:5])
    return not reasons, {"changed": len(changed), "added": len(added), "removed": len(removed),
                         "queries": len(q)}


CHECKS = {
    "Q1-layer-stats": chk_Q1,
    "Q2-text-search": chk_Q2,
    "Q3-open-polylines": chk_Q3,
    "Q4-big-read": chk_Q4,
    "W1-edit-text": chk_W1,
    "W2-change-layer": chk_W2,
    "W3-unify-text-height": chk_W3,
    "W4-move-to-origin": chk_W4,
    "W5-delete-zero-lines": chk_W5,
    "W6-xdata-mark": chk_W6,
    "S1-single-section": chk_S1,
    "S2-overlay-4series": chk_S2,
    "S3-water-edit-composite": chk_S3,
    "S4-from-db-station": chk_S4,
    "C1-audit-clean": chk_C1,
    "C2-audit-problem": chk_C2,
    "C3-draw-readback": chk_C3,
    "X1-no-instance": chk_X1,
    "X2-product-ambiguity": chk_X2,
    "X3-bad-data-gate": chk_X3,
    "N1-bad-anchor": chk_N1,
    "N2-rollback": chk_N2,
    "N3-undo-restores": chk_N3,
    "N4-diff": chk_N4,
}


# --------------------------------------------------------------------------- utils


def _verify_passed(st, name, reasons):
    raw = st.raw(name)
    code = st.exit_code(name)
    if code != 0:
        reasons.append("%s 退出码 %s（期望 0）" % (name, code))
        return False
    if "RESULT: PASS" not in raw:
        tail = [ln for ln in raw.splitlines() if ln.strip()][-3:]
        reasons.append("%s 未打印 RESULT: PASS（尾部: %s）" % (name, " | ".join(tail)))
        return False
    return True


def _verify_assertions(raw):
    return len([ln for ln in raw.splitlines() if ln.startswith("PASS")])


def _first_json_block(raw):
    start = raw.find("{")
    if start < 0:
        return None
    depth = 0
    for i in range(start, len(raw)):
        ch = raw[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                try:
                    return json.loads(raw[start:i + 1])
                except Exception:
                    return None
    return None


def _top_rule(report):
    by_rule = ((report.get("summary") or {}).get("byRule") or {})
    if not by_rule:
        return None
    return max(by_rule.items(), key=lambda kv: kv[1].get("count", 0))[0]


def main(argv):
    args = {"task": None, "dir": None, "expected": None, "tasks": None, "context": None}
    i = 0
    while i < len(argv):
        tok = argv[i]
        if tok in ("--task", "--dir", "--expected", "--tasks", "--context") and i + 1 < len(argv):
            args[tok.lstrip("-")] = argv[i + 1]
            i += 2
        else:
            i += 1
    task_id = args["task"]
    rundir = args["dir"]
    verdict = {"ok": False, "kind": "error", "reasons": [], "metrics": {}}
    if not task_id or not rundir:
        verdict["reasons"].append("usage: check.py --task <id> --dir <dir>")
        print(json.dumps(verdict, ensure_ascii=False))
        return 2
    func = CHECKS.get(task_id)
    if not func:
        verdict["reasons"].append("no checker for task " + task_id)
        print(json.dumps(verdict, ensure_ascii=False))
        return 2

    expected = read_json(args["expected"]) or {}
    tasks_doc = read_json(args["tasks"]) or {}
    ctx = read_json(args["context"]) or {}
    task = next((t for t in (tasks_doc.get("tasks") or []) if t.get("id") == task_id), {})
    exp = dict(expected)
    exp["expect"] = task.get("expect") or {}
    expected_kind = (task.get("expect") or {}).get("kind")

    st = Steps(rundir)
    reasons = []
    try:
        ok, metrics = func(st, exp, ctx, reasons)
    except Exception as exc:  # a checker bug must not masquerade as a product failure
        ok, metrics = False, {}
        reasons.append("checker error: %s: %s" % (type(exc).__name__, exc))
    verdict["ok"] = bool(ok)
    verdict["metrics"] = metrics
    verdict["reasons"] = reasons
    if ok and expected_kind == "capability_gap":
        verdict["kind"] = "capability_gap"
    elif ok:
        verdict["kind"] = "success"
    elif metrics.get("productBug"):
        verdict["kind"] = "product_bug"
    else:
        verdict["kind"] = "failure"
    print(json.dumps(verdict, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
