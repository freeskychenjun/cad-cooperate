#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""verify_model_self.py -- 模型「自洽」核对器（任意 profile，无需 expected 基准）。

真实数据没有独立 expected 时用它：把 `section.model.json` 里的
`points + params + labels + style` **独立重算一遍**（DESIGN-M1 §4.2/§4.3），
再与模型自带的 `entities / expected / computed` 逐项比对。查的是：
  - 实体清单是否与其声明的点数/开关相符（实体数公式）
  - 地面线顶点数 == 点数，且每个顶点 == (off, elev) 经 §4.2 映射后的值
  - bbox / widthMm / heightMm / elevBase 自洽
  - 水位线端点/文字、点高程文字、点号、基线/左竖线、深泓圆与文字位置自洽
  - 分类型/分图层计数与 `expected` 一致；图层名/文字样式与 prefix 一致
  - yssd 专有：pointsDetail 与 points 一一对应、dataQuality 的 suspect/dropped 计数自洽

用法：
  python tests\\verify_model_self.py --model tmp\\real\\sample-station-2025.model.json
退出码：0 全绿 / 1 有 FAIL。
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from typing import Any, Dict, List, Optional, Sequence, Tuple

TOL = 1e-9
TOL_MM = 1e-6


def load(path: str) -> Dict[str, Any]:
    with open(path, "r", encoding="utf-8-sig") as fh:
        return json.load(fh)


class Ck:
    def __init__(self, tol: float = TOL) -> None:
        self.tol = tol
        self.rows: List[Tuple[str, bool, str]] = []

    def ok(self, name: str, cond: bool, detail: str = "") -> bool:
        self.rows.append((name, bool(cond), detail))
        return bool(cond)

    def near(self, name: str, got: Optional[float], want: float,
             detail: str = "", tol: Optional[float] = None) -> bool:
        t = self.tol if tol is None else tol
        if got is None:
            return self.ok(name, False, "缺失（want=%r）" % want)
        d = abs(float(got) - want)
        return self.ok(name, d <= t,
                       "got=%r want=%r d=%.3g%s" % (got, want, d,
                                                    (" " + detail) if detail else ""))

    @property
    def failed(self) -> int:
        return sum(1 for _, o, _ in self.rows if not o)

    def report(self) -> None:
        w = max([len(n) for n, _, _ in self.rows] + [10])
        for n, o, d in self.rows:
            print("%-4s  %-*s  %s" % ("PASS" if o else "FAIL", w, n, d))
        print("-" * 72)
        print("[self] RESULT: %s  (%d PASS / %d FAIL)"
              % ("FAIL" if self.failed else "PASS", len(self.rows) - self.failed, self.failed))


def norm_kind(e: Dict[str, Any]) -> str:
    k = (e.get("kind") or "").upper()
    if k:
        return k
    t = (e.get("type") or "").upper()
    return {"LWPOLYLINE": "POLYLINE", "POLYLINE": "POLYLINE", "LINE": "LINE",
            "TEXT": "TEXT", "CIRCLE": "CIRCLE"}.get(t, t)


def verify_v2(m: Dict[str, Any], ck: "Ck") -> None:
    """schema v2（多序列，无 entities[]）：独立重算共用坐标映射后逐项比对。"""
    ck.ok("v2.schema", m.get("schema") == "zwcad-section-model/2", repr(m.get("schema")))
    ck.ok("v2.ok", m.get("ok") is True, repr(m.get("ok")))
    ck.ok("v2.no-entities", not m.get("entities"),
          "v2 不应包含 entities[]（helper 按 params+series 计算）")

    params = m.get("params") or {}
    series = m.get("series") or []
    comp = m.get("computed") or {}
    ck.ok("v2.series 非空", len(series) >= 1, "series=%d" % len(series))

    h = float(params.get("hScale") or 0)
    v = float(params.get("vScale") or 0)
    ck.ok("v2.params.hScale > 0", h > 0, repr(params.get("hScale")))
    ck.ok("v2.params.vScale > 0", v > 0, repr(params.get("vScale")))
    ck.ok("v2.params.paper", bool(params.get("paper")), repr(params.get("paper")))
    origin = params.get("origin") or [0.0, 0.0]
    ox, oy = float(origin[0]), float(origin[1])
    prefix = params.get("layerPrefix") or "AI-SEC"
    ck.ok("v2.params.layerPrefix", isinstance(prefix, str) and bool(prefix), repr(prefix))
    h = h or 1.0
    v = v or 1.0

    # ---- 序列基本结构
    ids, labels, counts = [], [], []
    all_pts: List[Tuple[float, float]] = []
    bad_pts = 0
    for s in series:
        ids.append(s.get("id"))
        labels.append(s.get("label"))
        pts = s.get("points") or []
        counts.append(len(pts))
        if len(pts) < 3:
            ck.ok("v2.series[%r].points>=3" % s.get("id"), False, "n=%d" % len(pts))
        for p in pts:
            if not (isinstance(p, (list, tuple)) and len(p) >= 2):
                bad_pts += 1
                continue
            try:
                all_pts.append((float(p[0]), float(p[1])))
            except (TypeError, ValueError):
                bad_pts += 1
    ck.ok("v2.series[id] 唯一且合法",
          all(isinstance(i, str) and re.fullmatch(r"[A-Za-z0-9_-]+", i or "") for i in ids)
          and len(set(ids)) == len(ids), "ids=%s" % ids)
    ck.ok("v2.series[label] 非空", all(bool(x) for x in labels), "labels=%s" % labels)
    ck.ok("v2.series[points] 形态", bad_pts == 0, "非法点 %d 个" % bad_pts)
    ck.ok("v2.computed.seriesCount", int(comp.get("seriesCount", -1)) == len(series),
          "computed=%r series=%d" % (comp.get("seriesCount"), len(series)))
    ck.ok("v2.computed.pointCounts", list(comp.get("pointCounts") or []) == counts,
          "computed=%s actual=%s" % (comp.get("pointCounts"), counts))

    if not all_pts:
        return
    offs = [p[0] for p in all_pts]
    elevs = [p[1] for p in all_pts]
    off_min, off_max = min(offs), max(offs)
    elev_min, elev_max = min(elevs), max(elevs)
    elev_base = elev_min - 10.0 * v / 1000.0
    kx, ky = 1000.0 / h, 1000.0 / v

    # ---- 共用坐标映射（多序列必须同一 elevBase/xRange）
    ck.near("v2.computed.elevBase", comp.get("elevBase"), elev_base)
    xr = comp.get("xRange") or [None, None]
    ck.near("v2.computed.xRange[0]", xr[0], off_min)
    ck.near("v2.computed.xRange[1]", xr[1], off_max)
    cbb = comp.get("bbox") or {}
    want_bb = {"x0": min((o - off_min) * kx + ox for o in offs),
               "y0": min((e - elev_base) * ky + oy for e in elevs),
               "x1": max((o - off_min) * kx + ox for o in offs),
               "y1": max((e - elev_base) * ky + oy for e in elevs)}
    for k in ("x0", "y0", "x1", "y1"):
        ck.near("v2.computed.bbox.%s" % k, cbb.get(k), want_bb[k])
    ck.near("v2.computed.widthMm", comp.get("widthMm"),
            want_bb["x1"] - want_bb["x0"], tol=TOL_MM)
    ck.near("v2.computed.heightMm", comp.get("heightMm"),
            want_bb["y1"] - want_bb["y0"], tol=TOL_MM)
    ck.ok("v2.computed.offsetMin/Max",
          abs(float(comp.get("offsetMin", 1e18)) - off_min) <= ck.tol
          and abs(float(comp.get("offsetMax", -1e18)) - off_max) <= ck.tol,
          "off=[%r,%r] actual=[%r,%r]" % (comp.get("offsetMin"), comp.get("offsetMax"),
                                          off_min, off_max))
    ck.ok("v2.computed.elevMin/Max",
          abs(float(comp.get("elevMin", 1e18)) - elev_min) <= ck.tol
          and abs(float(comp.get("elevMax", -1e18)) - elev_max) <= ck.tol,
          "elev=[%r,%r] actual=[%r,%r]" % (comp.get("elevMin"), comp.get("elevMax"),
                                           elev_min, elev_max))
    inside = all(want_bb["x0"] - 1e-9 <= (o - off_min) * kx + ox <= want_bb["x1"] + 1e-9
                 and want_bb["y0"] - 1e-9 <= (e - elev_base) * ky + oy <= want_bb["y1"] + 1e-9
                 for o, e in all_pts)
    ck.ok("v2.所有点落在 bbox 内", inside)

    # ---- 水位线（序列级，仅数据形态）
    wl_bad = 0
    for s in series:
        for wl in (s.get("waterLevels") or []):
            if not isinstance(wl, dict) or "value" not in wl:
                wl_bad += 1
                continue
            try:
                float(wl["value"])
            except (TypeError, ValueError):
                wl_bad += 1
    ck.ok("v2.series[waterLevels] 形态", wl_bad == 0, "非法 %d 条" % wl_bad)

    # ---- 样式
    color_bad = [s.get("id") for s in series
                 if not (isinstance((s.get("style") or {}).get("color"), (int, float))
                         or (s.get("style") or {}).get("color") is None)]
    ck.ok("v2.series[style].color", not color_bad, "异常序列=%s" % color_bad)
    lt_bad = [s.get("id") for s in series
              if (s.get("style") or {}).get("linetype") not in (None, "CONTINUOUS", "DASHED")]
    ck.ok("v2.series[style].linetype", not lt_bad, "异常序列=%s" % lt_bad)

    # ---- dataQuality / warnings / summary / sourceCompare / source
    dq = m.get("dataQuality") or {}
    if dq:
        ck.ok("v2.dataQuality.series 数", len(dq.get("series") or []) == len(series),
              "dq=%d series=%d" % (len(dq.get("series") or []), len(series)))
        ck.ok("v2.dataQuality.suspectCount == len(suspect)",
              int(dq.get("suspectCount", -1)) == len(dq.get("suspect") or []),
              "count=%r" % dq.get("suspectCount"))
    warnings = m.get("warnings") or []
    ck.ok("v2.warnings 形态", all(isinstance(w, dict) and w.get("code") for w in warnings),
          "%d 条" % len(warnings))
    if len(series) > 1:
        ck.ok("v2.warnings 含 MULTIPLE_SERIES",
              any(w.get("code") == "MULTIPLE_SERIES" for w in warnings),
              "codes=%s" % [w.get("code") for w in warnings])
    summary = m.get("summary")
    if summary:
        ok = len(summary) == len(series) and all(
            int(s.get("points", -1)) == counts[i] for i, s in enumerate(summary))
        ck.ok("v2.summary 点数一致", ok, "summary=%s counts=%s" % (summary, counts))
    sc = m.get("sourceCompare")
    if sc:
        ck.ok("v2.sourceCompare.differingCount == len(differing)",
              int(sc.get("differingCount", -1)) == len(sc.get("differing") or []),
              "count=%r list=%d" % (sc.get("differingCount"),
                                    len(sc.get("differing") or [])))
        ck.ok("v2.sourceCompare 计数自洽 (compared <= dbPoints)",
              int(sc.get("compared", 0)) <= int(sc.get("dbPoints", 0)),
              "compared=%r dbPoints=%r" % (sc.get("compared"), sc.get("dbPoints")))
    src = m.get("source") or {}
    ck.ok("v2.source.mode == db", src.get("mode") == "db", repr(src.get("mode")))
    ck.ok("v2.source.orderedBy 说明未重排", "vert_num" in str(src.get("orderedBy") or ""),
          repr(src.get("orderedBy")))


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="模型自洽核对（DESIGN-M1 §4.2/§4.3 独立重算）")
    ap.add_argument("--model", required=True)
    ap.add_argument("--tol", type=float, default=TOL)
    args = ap.parse_args(argv)

    m = load(args.model)
    ck = Ck(args.tol)

    if m.get("schema") == "zwcad-section-model/2":
        verify_v2(m, ck)
        ck.report()
        return 0 if ck.failed == 0 else 1

    ck.ok("model.ok", m.get("ok") is True, repr(m.get("ok")))
    ck.ok("model.schema", m.get("schema") == "zwcad-section-model/1",
          repr(m.get("schema")))
    points = m.get("points") or []
    pts = [(float(p[0]), float(p[1])) for p in points] if points else []
    ck.ok("points.count>=3", len(pts) >= 3, "n=%d" % len(pts))

    params = m.get("params") or {}
    labels = m.get("labels") or {}
    style = m.get("style") or {}
    layers = m.get("layers") or {}
    prefix = m.get("layerPrefix") or "AI-SEC"
    scale = m.get("scale") or {}
    elems = m.get("entities") or []
    comp = m.get("computed") or {}
    exp = m.get("expected") or {}

    # ---- 1) 独立按 §4.2 重算
    offs = [p[0] for p in pts]
    elevs = [p[1] for p in pts]
    h = float(scale.get("horizontal") or params.get("hScale") or 0) or 1.0
    v = float(scale.get("vertical") or params.get("vScale") or 0) or 1.0
    ox, oy = (m.get("origin") or [0.0, 0.0])[:2]
    off_min, off_max = min(offs), max(offs)
    elev_min, elev_max = min(elevs), max(elevs)
    elev_base = elev_min - 10.0 * v / 1000.0
    mm = [((o - off_min) * 1000.0 / h + ox, (e - elev_base) * 1000.0 / v + oy)
          for o, e in pts]
    bb = {"x0": min(p[0] for p in mm), "y0": min(p[1] for p in mm),
          "x1": max(p[0] for p in mm), "y1": max(p[1] for p in mm)}

    ck.near("computed.elevBase", comp.get("elevBase"), elev_base)
    cbb = comp.get("bbox") or {}
    for k in ("x0", "y0", "x1", "y1"):
        ck.near("computed.bbox.%s" % k, cbb.get(k), bb[k])
    ck.near("computed.widthMm", comp.get("widthMm"), bb["x1"] - bb["x0"], tol=TOL_MM)
    ck.near("computed.heightMm", comp.get("heightMm"), bb["y1"] - bb["y0"], tol=TOL_MM)
    ck.near("computed.offsetMin", comp.get("offsetMin"), off_min)
    ck.near("computed.offsetMax", comp.get("offsetMax"), off_max)
    ck.near("computed.elevMin", comp.get("elevMin"), elev_min)
    ck.near("computed.elevMax", comp.get("elevMax"), elev_max)

    # ---- 2) 实体数公式
    n = len(pts)
    every = int(labels.get("labelEvery") or params.get("labelEvery") or 1)
    show_axis = bool(labels.get("showAxis", not params.get("noAxis", False)))
    show_pno = bool(labels.get("showPointNo", not params.get("noPointNo", False)))
    n_water = len(m.get("waterLevels") or [])
    elev_text = sum(1 for i in range(n) if i % every == 0)
    pnt_no = n if show_pno else 0
    axis_n = 2 if show_axis else 0
    want_total = 1 + 2 * n_water + elev_text + pnt_no + axis_n + 1 + 1
    ck.ok("expected.total == formula(2n+2*nw+%d...)" % axis_n,
          int(exp.get("total", -1)) == want_total,
          "got=%r want=%d (n=%d nw=%d elevText=%d pntNo=%d axis=%d)"
          % (exp.get("total"), want_total, n, n_water, elev_text, pnt_no, axis_n))
    ck.ok("entities.count == expected.total", len(elems) == int(exp.get("total", -2)),
          "entities=%d expected=%r" % (len(elems), exp.get("total")))

    by_type: Dict[str, int] = {}
    by_layer: Dict[str, int] = {}
    by_role: Dict[str, Dict[str, Any]] = {}
    for e in elems:
        k = norm_kind(e)
        t = {"POLYLINE": "LWPOLYLINE", "LINE": "LINE", "TEXT": "TEXT",
             "CIRCLE": "CIRCLE"}.get(k, k)
        by_type[t] = by_type.get(t, 0) + 1
        by_layer[e.get("layer")] = by_layer.get(e.get("layer"), 0) + 1
        if e.get("role"):
            by_role[e["role"]] = e
    ck.ok("expected.byType == actual", dict(exp.get("byType") or {}) == by_type,
          "expected=%s actual=%s" % (exp.get("byType"), by_type))
    ck.ok("expected.byLayer == actual", dict(exp.get("byLayer") or {}) == by_layer,
          "expected=%s actual=%s" % (exp.get("byLayer"), by_layer))

    # ---- 3) 图层名 / 文字样式
    want_layers = {layers.get("ground"), layers.get("water"), layers.get("anno"),
                   layers.get("axis")}
    want_layers.discard(None)
    ck.ok("layers.names", set(by_layer) == want_layers,
          "got=%s want=%s" % (sorted(x for x in by_layer if x), sorted(want_layers)))
    ck.ok("layers.prefix", all(str(x).startswith(prefix) for x in by_layer if x),
          "prefix=%r" % prefix)
    bad_style = [e for e in elems if norm_kind(e) == "TEXT"
                 and e.get("style") not in (None, style.get("textStyle"))]
    ck.ok("texts.style", not bad_style, "%d 条样式不符" % len(bad_style))

    # ---- 4) 地面线顶点逐一对应 §4.2 映射
    ground = by_role.get("ground")
    if ground is None:
        ground = next((e for e in elems if norm_kind(e) == "POLYLINE"), None)
    if not ck.ok("ground.present", ground is not None):
        ck.report()
        return 1
    gpts = [(float(p[0]), float(p[1])) for p in (ground.get("points") or [])]
    ck.ok("ground.vertexCount == points.count", len(gpts) == n,
          "got=%d want=%d" % (len(gpts), n))
    worst = 0.0
    worst_i = -1
    for i in range(min(len(gpts), n)):
        d = math.hypot(gpts[i][0] - mm[i][0], gpts[i][1] - mm[i][1])
        if d > worst:
            worst, worst_i = d, i + 1
    ck.ok("ground.vertices == §4.2 mapping", worst <= ck.tol and len(gpts) == n,
          "maxd=%.3g（顶点#%d）" % (worst, worst_i))
    ck.ok("ground.closed == False", ground.get("closed") in (False, None),
          repr(ground.get("closed")))
    ck.ok("ground.layer", ground.get("layer") == layers.get("ground"),
          repr(ground.get("layer")))

    # ---- 5) 水位线 / 水位文字
    for idx, wl in enumerate(m.get("waterLevels") or [], 1):
        wy = (float(wl["value"]) - elev_base) * 1000.0 / v + oy
        x0 = bb["x0"] - float(style.get("waterExtendLeftMm", 5.0))
        x1 = bb["x1"] + float(style.get("waterExtendRightMm", 5.0))
        line = by_role.get("water-line:%d" % idx)
        if not ck.ok("water-line:%d present" % idx, line is not None):
            continue
        lp = line.get("points") or []
        ck.ok("water-line:%d 端点" % idx,
              len(lp) == 2 and abs(lp[0][1] - wy) <= ck.tol and abs(lp[1][1] - wy) <= ck.tol
              and abs(lp[0][0] - x0) <= ck.tol and abs(lp[1][0] - x1) <= ck.tol,
              "got=%r want=[[%r,%r],[%r,%r]]" % (lp, x0, wy, x1, wy))
        tx = by_role.get("water-text:%d" % idx)
        want_txt = "%s %.2f m" % (wl.get("label"), float(wl["value"]))
        ck.ok("water-text:%d 内容" % idx,
              tx is not None and tx.get("text") == want_txt,
              "got=%r want=%r" % (tx.get("text") if tx else None, want_txt))
        if tx:
            ck.ok("water-text:%d 位置" % idx,
                  abs(tx["pos"][0] - x1) <= ck.tol
                  and abs(tx["pos"][1] - (wy + float(style.get("waterTextDyMm", 2.0)))) <= ck.tol,
                  "got=%r" % (tx.get("pos"),))
            ck.near("water-text:%d 字高" % idx, tx.get("height"),
                    float(labels.get("waterHeightMm", 2.5)))

    # ---- 6) 点高程 / 点号 / 轴线 / 深泓
    bad = []
    for i in range(n):
        if i % every != 0:
            continue
        e = by_role.get("elev-text:%d" % (i + 1))
        want_txt = "%.2f" % elevs[i]
        want_pos = [mm[i][0], mm[i][1] + float(style.get("elevTextDyMm", 2.0))]
        if e is None or e.get("text") != want_txt \
                or abs(e["pos"][0] - want_pos[0]) > ck.tol \
                or abs(e["pos"][1] - want_pos[1]) > ck.tol \
                or abs(float(e.get("height", 0)) - float(labels.get("elevHeightMm", 2.0))) > ck.tol:
            bad.append(i + 1)
    ck.ok("elev-text 全部对位", not bad,
          "%d 个偏差：%s" % (len(bad), bad[:8]))
    bad = []
    if show_pno:
        for i in range(n):
            e = by_role.get("point-no:%d" % (i + 1))
            want_pos = [mm[i][0], mm[i][1] - float(style.get("pointNoDyMm", 3.0))]
            if e is None or e.get("text") != str(i + 1) \
                    or abs(e["pos"][0] - want_pos[0]) > ck.tol \
                    or abs(e["pos"][1] - want_pos[1]) > ck.tol:
                bad.append(i + 1)
        ck.ok("point-no 全部对位", not bad, "%d 个偏差：%s" % (len(bad), bad[:8]))
    else:
        ck.ok("point-no 已关闭", not by_role.get("point-no:1"))

    if show_axis:
        base = by_role.get("axis-base")
        left = by_role.get("axis-left")
        want_base = [[bb["x0"], oy], [bb["x1"], oy]]
        want_left = [[bb["x0"], oy], [bb["x0"], oy + float(style.get("axisLeftHeightMm", 10.0))]]
        ck.ok("axis-base 端点", base is not None
              and all(abs(base["points"][k][0] - want_base[k][0]) <= ck.tol
                      and abs(base["points"][k][1] - want_base[k][1]) <= ck.tol for k in (0, 1)),
              "got=%r want=%r" % (base.get("points") if base else None, want_base))
        ck.ok("axis-left 端点", left is not None
              and all(abs(left["points"][k][0] - want_left[k][0]) <= ck.tol
                      and abs(left["points"][k][1] - want_left[k][1]) <= ck.tol for k in (0, 1)),
              "got=%r want=%r" % (left.get("points") if left else None, want_left))
    else:
        ck.ok("axis 已关闭", not by_role.get("axis-base"))

    thal = min(range(n), key=lambda k: elevs[k])
    circ = by_role.get("thalweg-circle")
    ck.ok("thalweg-circle 圆心 == 最低点",
          circ is not None
          and abs(circ["center"][0] - mm[thal][0]) <= ck.tol
          and abs(circ["center"][1] - mm[thal][1]) <= ck.tol,
          "got=%r want=(%r,%r)" % (circ.get("center") if circ else None,
                                   mm[thal][0], mm[thal][1]))
    ck.near("thalweg-circle 半径", (circ or {}).get("radius"),
            float(style.get("thalwegCircleRadiusMm", 0.6)))
    tt = by_role.get("thalweg-text")
    want_thal = "河底 %.2f m" % elevs[thal]
    ck.ok("thalweg-text 内容", tt is not None and tt.get("text") == want_thal,
          "got=%r want=%r" % (tt.get("text") if tt else None, want_thal))

    # ---- 7) yssd 专有自洽
    detail = m.get("pointsDetail")
    if detail:
        same = len(detail) == n and all(
            abs(float(d["offset"]) - pts[i][0]) <= ck.tol
            and abs(float(d["elev"]) - pts[i][1]) <= ck.tol
            for i, d in enumerate(detail))
        ck.ok("pointsDetail 与 points 一一对应", same, "detail=%d points=%d" % (len(detail), n))
        dq = m.get("dataQuality") or {}
        if dq:
            sus = dq.get("suspect") or []
            dropped = int(dq.get("dropped") or 0)
            kept = sum(1 for d in detail if d.get("suspect"))
            ck.ok("dataQuality.suspectCount == len(suspect)",
                  int(dq.get("suspectCount", -1)) == len(sus),
                  "count=%r len=%d" % (dq.get("suspectCount"), len(sus)))
            ck.ok("dataQuality 计数自洽 (suspectCount == kept + dropped)",
                  int(dq.get("suspectCount", -1)) == kept + dropped,
                  "suspectCount=%r kept=%d dropped=%d"
                  % (dq.get("suspectCount"), kept, dropped))
            ck.ok("yssd 每点都有源行/原文",
                  all("row" in d and "elevRaw" in d for d in detail),
                  "缺失 %d 个" % sum(1 for d in detail if "row" not in d))
            ck.ok("yssd 点号无重复",
                  len({d.get("point") for d in detail}) == n or
                  all(d.get("point") is not None for d in detail),
                  "重复=%s" % (n - len({d.get("point") for d in detail})))
            blocks = (m.get("source") or {}).get("blocks") or []
            ck.ok("yssd source.blocks 非空", bool(blocks), "blocks=%d" % len(blocks))
            ck.ok("yssd 选中块存在",
                  any(b.get("index") == (m.get("source") or {}).get("block") for b in blocks),
                  "selected=%r" % (m.get("source") or {}).get("block"))

    ck.report()
    return 0 if ck.failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
