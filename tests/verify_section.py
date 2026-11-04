#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""verify_section.py -- 断面图独立核对器（零 COM）。

吃两种输入：
  1) `prepare.py` 产出的 section.model.json（schema: zwcad-section-model/1）
  2) `scripts\\zwc.ps1 read` 的响应 JSON（{"ok":true,"cmd":"read","result":{...,"entities":[...]}}）

对 `tests\\fixtures\\section-30.expected.json`（由 make_expected.py 独立算出）逐项断言：
  实体总数 / 分类型 & 分图层计数 / 图层名 / 地面线顶点坐标 / 水位线端点与 y /
  水位文字 / 点高程文字 / 点号 / 基线+左竖线 / 深泓圆与文字 / bbox / computed 字段。
坐标容差默认 1e-6（DESIGN §4.4），bbox 容差 0.01 mm（DESIGN §4.4）。

`--tamper`：故意把地面线一个顶点的 y 改 1.0 mm，然后跑同一套断言；
必须报出 FAIL 才算「校验有效」（此时退出码 0 = 负例自证成功；抓不到则退出码 1）。

退出码：0 全绿（或 --tamper 成功抓到改动）；1 有 FAIL（或 --tamper 没抓到）。

用法：
  python tests\\verify_section.py --model tmp\\section-30.model.json ^
         --expected tests\\fixtures\\section-30.expected.json
  python tests\\verify_section.py --model tmp\\read.json --expected ...  # 吃 CAD 回读
  python tests\\verify_section.py --model tmp\\section-30.model.json --expected ... --tamper
"""

from __future__ import annotations

import argparse
import copy
import json
import math
import os
import sys
from typing import Any, Dict, List, Optional, Sequence, Tuple

TOL_DEFAULT = 1e-6
TOL_BBOX = 0.01  # DESIGN §4.4：bbox 宽高容差 0.01 mm
TAMPER_DY = 1.0

# 实体类型归一（CAD ObjectName / 我的 type 名 -> 统一 kind）
TYPE_TO_KIND = {
    "LWPOLYLINE": "POLYLINE", "POLYLINE": "POLYLINE", "ACDBPOLYLINE": "POLYLINE",
    "LINE": "LINE", "ACDBLINE": "LINE",
    "TEXT": "TEXT", "ACDBTEXT": "TEXT", "MTEXT": "TEXT", "ACDBMTEXT": "TEXT",
    "CIRCLE": "CIRCLE", "ACDBCIRCLE": "CIRCLE",
    "ARC": "ARC", "ACDBARC": "ARC",
}


# --------------------------------------------------------------------------- 基础工具
def load_json(path: str) -> Any:
    with open(path, "r", encoding="utf-8-sig") as fh:
        return json.load(fh)


def kind_of(typ: str) -> str:
    return TYPE_TO_KIND.get((typ or "").strip().upper(), (typ or "").strip().upper())


def _xy(v: Any) -> Optional[List[float]]:
    """把 [x,y] / [x,y,z] / {"x":..,"y":..} 归一成 [x, y]。"""
    if v is None:
        return None
    if isinstance(v, dict):
        if "x" in v and "y" in v:
            return [float(v["x"]), float(v["y"])]
        return None
    if isinstance(v, (list, tuple)) and len(v) >= 2:
        return [float(v[0]), float(v[1])]
    return None


def fmt(v: float) -> str:
    return ("%.9g" % v)


def dist(a: Sequence[float], b: Sequence[float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


# --------------------------------------------------------------------------- 归一化
def normalize_entity(ent: Dict[str, Any]) -> Dict[str, Any]:
    typ = str(ent.get("type") or "").strip()
    kind = ent.get("kind")
    kind = kind_of(kind or typ)
    if kind not in ("POLYLINE", "LINE", "TEXT", "CIRCLE"):
        kind = kind_of(typ)
    out: Dict[str, Any] = {
        "kind": kind,
        "type": typ or kind,
        "canonical": {"POLYLINE": "LWPOLYLINE", "LINE": "LINE", "TEXT": "TEXT",
                      "CIRCLE": "CIRCLE"}.get(kind, kind),
        "layer": ent.get("layer"),
        "role": ent.get("role"),
        "handle": ent.get("handle"),
    }
    if kind == "POLYLINE":
        raw = ent.get("points") or ent.get("vertices") or ent.get("coordinates") or []
        out["points"] = [p for p in (_xy(v) for v in raw) if p is not None]
        out["closed"] = bool(ent.get("closed", False))
    elif kind == "LINE":
        pts = ent.get("points")
        if pts:
            out["points"] = [p for p in (_xy(v) for v in pts) if p is not None]
        else:
            a = _xy(ent.get("start") or ent.get("startPoint") or ent.get("startpoint"))
            b = _xy(ent.get("end") or ent.get("endPoint") or ent.get("endpoint"))
            if a is not None and b is not None:
                out["points"] = [a, b]
        out["dash"] = ent.get("dash") or ent.get("linetype") or ent.get("lineType")
    elif kind == "TEXT":
        out["text"] = ent.get("text")
        if out["text"] is None:
            out["text"] = ent.get("content")
        if out["text"] is None:
            out["text"] = ent.get("value")
        out["pos"] = _xy(ent.get("pos") or ent.get("insertion") or ent.get("position")
                         or ent.get("insert") or ent.get("insertionPoint"))
        h = ent.get("height")
        if h is None:
            h = ent.get("textHeight")
        out["height"] = None if h is None else float(h)
        out["style"] = ent.get("style") or ent.get("textStyle")
    elif kind == "CIRCLE":
        out["center"] = _xy(ent.get("center") or ent.get("centre") or ent.get("centerPoint"))
        r = ent.get("radius")
        out["radius"] = None if r is None else float(r)
    # bbox（read 侧 min/max 数组或 x0/y0/x1/y1 都接受）
    bb = ent.get("bbox")
    if isinstance(bb, dict):
        if "min" in bb and "max" in bb:
            lo, hi = _xy(bb["min"]), _xy(bb["max"])
            if lo and hi:
                out["bbox"] = {"x0": lo[0], "y0": lo[1], "x1": hi[0], "y1": hi[1]}
        elif all(k in bb for k in ("x0", "y0", "x1", "y1")):
            out["bbox"] = {k: float(bb[k]) for k in ("x0", "y0", "x1", "y1")}
    return out


def detect_payload(payload: Dict[str, Any]) -> Tuple[str, List[Dict[str, Any]], Dict[str, Any]]:
    """返回 (mode, 原始实体列表, 额外元信息)。mode ∈ model / read。"""
    if not isinstance(payload, dict):
        raise SystemExit("verify: 输入 JSON 顶层不是对象")
    result = payload.get("result")
    if isinstance(result, dict) and isinstance(result.get("entities"), list):
        return "read", result["entities"], {"counts": result.get("counts"),
                                            "readPath": result.get("readPath"),
                                            "truncated": result.get("truncated")}
    if isinstance(payload.get("entities"), list):
        return "model", payload["entities"], {"schema": payload.get("schema")}
    raise SystemExit("verify: 找不到 entities（既不是 model 也不是 read 响应）")


# --------------------------------------------------------------------------- 校验器
class Checker:
    def __init__(self, tol: float = TOL_DEFAULT) -> None:
        self.tol = tol
        self.results: List[Tuple[str, Optional[bool], str]] = []

    def check(self, name: str, ok: Optional[bool], detail: str = "") -> bool:
        self.results.append((name, ok, detail))
        return bool(ok)

    def near(self, name: str, got: Optional[float], want: float, detail: str = "") -> bool:
        if got is None:
            return self.check(name, False, "缺失（got=None, want=%s）" % fmt(want))
        d = abs(got - want)
        return self.check(name, d <= self.tol,
                          "got=%s want=%s d=%s%s" % (fmt(got), fmt(want), fmt(d),
                                                     (" " + detail) if detail else ""))

    def near_xy(self, name: str, got: Optional[Sequence[float]],
                want: Sequence[float]) -> bool:
        if got is None:
            return self.check(name, False, "缺失（want=[%s, %s]）" % (fmt(want[0]), fmt(want[1])))
        d = dist(got, want)
        return self.check(name, d <= self.tol,
                          "got=[%s, %s] want=[%s, %s] d=%s"
                          % (fmt(got[0]), fmt(got[1]), fmt(want[0]), fmt(want[1]), fmt(d)))

    @property
    def failed(self) -> int:
        return sum(1 for _, ok, _ in self.results if ok is False)

    @property
    def passed(self) -> int:
        return sum(1 for _, ok, _ in self.results if ok is True)

    @property
    def skipped(self) -> int:
        return sum(1 for _, ok, _ in self.results if ok is None)

    def report(self) -> None:
        width = max([len(n) for n, _, _ in self.results] + [10])
        for name, ok, detail in self.results:
            tag = "PASS" if ok is True else ("FAIL" if ok is False else "SKIP")
            print("%-4s  %-*s  %s" % (tag, width, name, detail))
        print("-" * 72)
        print("[verify] RESULT: %s  (%d PASS / %d FAIL / %d SKIP)"
              % ("FAIL" if self.failed else "PASS", self.passed, self.failed, self.skipped))


def count_by(ents: Sequence[Dict[str, Any]], getter) -> Dict[str, int]:
    out: Dict[str, int] = {}
    for e in ents:
        k = getter(e)
        if k is None:
            k = "<none>"
        out[k] = out.get(k, 0) + 1
    return out


def match_texts(ck: Checker, actual: List[Dict[str, Any]],
                expected: List[Dict[str, Any]], label: str) -> Dict[str, Dict[str, Any]]:
    """按 text 内容 + 最近位置做贪婪匹配（CAD 回读无 role 时用）。"""
    used = set()
    mapping: Dict[str, Dict[str, Any]] = {}
    for exp in expected:
        role = exp.get("role") or label
        want_pos = exp.get("pos")
        best = None
        best_d = float("inf")
        for idx, act in enumerate(actual):
            if idx in used or act["kind"] != "TEXT":
                continue
            if (act.get("text") or "") != (exp.get("text") or ""):
                continue
            if want_pos and act.get("pos"):
                d = dist(act["pos"], want_pos)
            else:
                d = 0.0
            if d < best_d:
                best, best_d = idx, d
        if best is None:
            ck.check("%s[%s]" % (label, role), False,
                     "回读里找不到文字 %r" % exp.get("text"))
        else:
            used.add(best)
            mapping[role] = actual[best]
    return mapping


def check_entities(ck: Checker, mode: str, raw: List[Dict[str, Any]],
                   expected: Dict[str, Any], layer_prefix: str,
                   meta: Dict[str, Any]) -> None:
    want = expected["entities"]
    exp_prefix = expected.get("layerPrefix") or layer_prefix
    layers = expected.get("layers") or {}
    tol_bbox = TOL_BBOX

    # --- 只算我们自己图层的实体
    acts = [normalize_entity(e) for e in raw]
    ours = [e for e in acts if str(e.get("layer") or "").startswith(exp_prefix)]

    # 1) 总数
    ck.check("entities.total", len(ours) == expected["expected"]["total"],
             "got=%d want=%d" % (len(ours), expected["expected"]["total"]))

    # 2) 分类型（归一到 LWPOLYLINE/LINE/TEXT/CIRCLE，AcDb* 也认）
    want_by_type = expected["expected"]["byType"]
    got_by_type = count_by(ours, lambda e: e["canonical"])
    ck.check("entities.byType", got_by_type == want_by_type,
             "got=%s want=%s" % (json.dumps(got_by_type, sort_keys=True),
                                 json.dumps(want_by_type, sort_keys=True)))

    # 3) 分图层
    want_by_layer = expected["expected"]["byLayer"]
    got_by_layer = count_by(ours, lambda e: e.get("layer"))
    ck.check("entities.byLayer", got_by_layer == want_by_layer,
             "got=%s want=%s" % (json.dumps(got_by_layer, sort_keys=True),
                                 json.dumps(want_by_layer, sort_keys=True)))

    # 4) 图层名齐备（前缀 + 名称）
    want_layers = set(want_by_layer) | {layers.get("ground"), layers.get("water"),
                                       layers.get("anno"), layers.get("axis")}
    want_layers.discard(None)
    got_layers = set(got_by_layer)
    ck.check("layers.names", got_layers == want_layers,
             "got=%s want=%s" % (sorted(got_layers), sorted(want_layers)))

    # --- 按 role 索引（model 有 role；read 无 role -> 下面逐个匹配）
    by_role: Dict[str, Dict[str, Any]] = {}
    for e in ours:
        if e.get("role"):
            by_role.setdefault(e["role"], e)
    if mode == "read" or len(by_role) < len(want):
        by_role = {}

    def role_expected(role: str) -> Optional[Dict[str, Any]]:
        for e in want:
            if e.get("role") == role:
                return e
        return None

    # 5) 地面线
    exp_ground = role_expected("ground") or next(e for e in want if e["role"] == "ground")
    got_ground = by_role.get("ground")
    if got_ground is None:
        cands = [e for e in ours if e["kind"] == "POLYLINE"
                 and e.get("layer") == layers.get("ground")]
        got_ground = cands[0] if cands else None
    if got_ground is None:
        ck.check("ground.polyline.present", False, "回读/模型里没有 GROUND 图层折线")
    else:
        ck.check("ground.polyline.layer", got_ground.get("layer") == layers.get("ground"),
                 "got=%r want=%r" % (got_ground.get("layer"), layers.get("ground")))
        ck.check("ground.polyline.closed", got_ground.get("closed") in (False, None),
                 "got=%r want=False" % (got_ground.get("closed"),))
        exp_pts = exp_ground["points"]
        got_pts = got_ground.get("points") or []
        ck.check("ground.polyline.vertexCount", len(got_pts) == len(exp_pts),
                 "got=%d want=%d" % (len(got_pts), len(exp_pts)))
        bad = []
        for i in range(min(len(got_pts), len(exp_pts))):
            if dist(got_pts[i], exp_pts[i]) > ck.tol:
                bad.append((i + 1, got_pts[i], exp_pts[i]))
        ck.check("ground.polyline.vertices", not bad,
                 "全部 %d 个顶点误差 <= %s" % (len(exp_pts), fmt(ck.tol))
                 if not bad else
                 "首个不符 顶点#%d got=[%s,%s] want=[%s,%s]"
                 % (bad[0][0], fmt(bad[0][1][0]), fmt(bad[0][1][1]),
                    fmt(bad[0][2][0]), fmt(bad[0][2][1])))
        # bbox（DESIGN §4.4 容差 0.01 mm）
        if got_pts:
            bb = {"x0": min(p[0] for p in got_pts), "y0": min(p[1] for p in got_pts),
                  "x1": max(p[0] for p in got_pts), "y1": max(p[1] for p in got_pts)}
            exp_bb = expected["bbox"]
            worst = max(abs(bb[k] - exp_bb[k]) for k in ("x0", "y0", "x1", "y1"))
            ck.check("ground.polyline.bbox", worst <= tol_bbox,
                     "got=%s want=%s maxd=%s"
                     % (json.dumps(bb), json.dumps(exp_bb), fmt(worst)))

    # 6) 水位线
    exp_waters = [e for e in want if e["role"].startswith("water-line:")]
    if by_role:
        got_waters = [by_role.get(e["role"]) for e in exp_waters]
    else:
        got_waters = []
        cands = [e for e in ours if e["kind"] == "LINE"
                 and e.get("layer") == layers.get("water")]
        used = set()
        for exp in exp_waters:
            want_y = exp["points"][0][1]
            best = None
            best_d = float("inf")
            for idx, act in enumerate(cands):
                if idx in used:
                    continue
                d = abs((act["points"] or [[0, 0]])[0][1] - want_y)
                if d < best_d:
                    best, best_d = idx, d
            if best is not None:
                used.add(best)
                got_waters.append(cands[best])
            else:
                got_waters.append(None)
    ck.check("water.lines.count", len(got_waters) == len(exp_waters),
             "got=%d want=%d" % (len(got_waters), len(exp_waters)))
    for exp, got in zip(exp_waters, got_waters):
        role = exp["role"]
        if got is None:
            ck.check("water.line[%s]" % role, False, "未找到水位线")
            continue
        pts = got.get("points") or []
        if len(pts) != 2:
            ck.check("water.line[%s].points" % role, False,
                     "端点数 != 2（got=%d）" % len(pts))
            continue
        ck.near("water.line[%s].y" % role, pts[0][1], exp["points"][0][1])
        ck.near("water.line[%s].x0" % role, pts[0][0], exp["points"][0][0])
        ck.near("water.line[%s].x1" % role, pts[1][0], exp["points"][1][0])
        if got.get("dash") is not None and exp.get("dash") is not None:
            ck.check("water.line[%s].dash" % role,
                     str(got["dash"]).upper() == str(exp["dash"]).upper(),
                     "got=%r want=%r" % (got.get("dash"), exp.get("dash")))

    # 7) 文字（水位文字 / 点高程 / 点号 / 深泓文字）
    exp_texts = [e for e in want if e["kind"] == "TEXT"]
    if by_role:
        got_texts = {e["role"]: by_role.get(e["role"]) for e in exp_texts}
    else:
        got_texts = match_texts(ck, ours, exp_texts, "text")
    mismatch = 0
    checked = 0
    for exp in exp_texts:
        role = exp["role"]
        got = got_texts.get(role)
        if got is None:
            if by_role:
                ck.check("text[%s]" % role, False, "模型/回读里缺少该文字")
            continue
        checked += 1
        ok_pos = ck.near_xy("text[%s].pos" % role, got.get("pos"), exp["pos"])
        ok_txt = ck.check("text[%s].content" % role, got.get("text") == exp["text"],
                          "got=%r want=%r" % (got.get("text"), exp["text"]))
        ok_h = ck.near("text[%s].height" % role,
                       got.get("height"), exp["height"]) if exp.get("height") is not None \
            else True
        ok_layer = ck.check("text[%s].layer" % role,
                            got.get("layer") == exp["layer"],
                            "got=%r want=%r" % (got.get("layer"), exp["layer"]))
        if got.get("style") is not None and exp.get("style") is not None:
            if str(got["style"]) != str(exp["style"]):
                ck.check("text[%s].style" % role, False,
                         "got=%r want=%r" % (got.get("style"), exp.get("style")))
        if not (ok_pos and ok_txt and ok_h and ok_layer):
            mismatch += 1
    ck.check("text.summary", mismatch == 0,
             "%d 条文字全部匹配（内容/位置/字高/图层）" % checked
             if mismatch == 0 else "%d 条文字不匹配" % mismatch)

    # 8) 基线 + 左竖线
    for role in ("axis-base", "axis-left"):
        exp = role_expected(role)
        if exp is None:
            continue
        got = by_role.get(role)
        if got is None:
            cands = [e for e in ours if e["kind"] == "LINE"
                     and e.get("layer") == layers.get("axis")]
            got = None
            for c in cands:
                pts = c.get("points") or []
                if len(pts) != 2:
                    continue
                same_x = abs(pts[0][0] - pts[1][0]) <= ck.tol
                same_y = abs(pts[0][1] - pts[1][1]) <= ck.tol
                if role == "axis-base" and same_y and abs(pts[0][1]) <= ck.tol:
                    got = c
                    break
                if role == "axis-left" and same_x:
                    got = c
                    break
        if got is None:
            ck.check("axis[%s]" % role, False, "未找到该轴线")
            continue
        pts = got.get("points") or []
        ok = len(pts) == 2 and all(
            dist(pts[k], exp["points"][k]) <= ck.tol for k in (0, 1))
        ck.check("axis[%s]" % role, ok,
                 "got=%s want=%s" % (json.dumps(pts), json.dumps(exp["points"])))

    # 9) 深泓圆 + 深泓文字
    exp_circle = role_expected("thalweg-circle")
    if exp_circle is not None:
        got = by_role.get("thalweg-circle")
        if got is None:
            cands = [e for e in ours if e["kind"] == "CIRCLE"]
            got = cands[0] if cands else None
        if got is None:
            ck.check("thalweg.circle", False, "未找到深泓圆")
        else:
            ck.near_xy("thalweg.circle.center", got.get("center"), exp_circle["center"])
            ck.near("thalweg.circle.radius", got.get("radius"), exp_circle["radius"])
            ck.check("thalweg.circle.layer", got.get("layer") == exp_circle["layer"],
                     "got=%r want=%r" % (got.get("layer"), exp_circle["layer"]))

    # 10) 模型自身字段（computed / points / warnings）—— 仅 model 模式
    payload = meta.get("payload")
    if mode == "model" and isinstance(payload, dict):
        comp = payload.get("computed") or {}
        expc = expected["computed"]
        if "widthMm" in comp:
            ck.near("computed.widthMm", comp.get("widthMm"), expc["widthMm"])
        if "heightMm" in comp:
            ck.near("computed.heightMm", comp.get("heightMm"), expc["heightMm"])
        if "elevBase" in comp:
            ck.near("computed.elevBase", comp.get("elevBase"), expc["elevBase"])
        bb = comp.get("bbox")
        if isinstance(bb, dict):
            exp_bb = expected["bbox"]
            worst = max(abs(float(bb.get(k, 1e18)) - exp_bb[k])
                        for k in ("x0", "y0", "x1", "y1"))
            ck.check("computed.bbox", worst <= tol_bbox,
                     "got=%s want=%s maxd=%s"
                     % (json.dumps(bb), json.dumps(exp_bb), fmt(worst)))
        if "autoScaled" in comp:
            ck.check("computed.autoScaled", bool(comp.get("autoScaled")) ==
                     bool(expc["autoScaled"]),
                     "got=%r want=%r" % (comp.get("autoScaled"), expc["autoScaled"]))
        if "orderReversed" in comp:
            ck.check("computed.orderReversed", bool(comp.get("orderReversed")) ==
                     bool(expc["orderReversed"]),
                     "got=%r want=%r" % (comp.get("orderReversed"),
                                         expc["orderReversed"]))
        scale = payload.get("scale") or comp.get("scale") or {}
        if scale.get("horizontal"):
            ck.near("scale.horizontal", scale.get("horizontal"),
                    expected["scale"]["horizontal"])
        if scale.get("vertical"):
            ck.near("scale.vertical", scale.get("vertical"),
                    expected["scale"]["vertical"])
        if payload.get("expected", {}).get("total") is not None:
            ck.check("model.expected.total",
                     payload["expected"]["total"] == expected["expected"]["total"],
                     "got=%r want=%r" % (payload["expected"]["total"],
                                         expected["expected"]["total"]))
        warn_codes = [w.get("code") for w in payload.get("warnings") or []]
        exp_warn = [w.get("code") for w in expected.get("warnings") or []]
        ck.check("model.warnings", set(exp_warn) <= set(warn_codes),
                 "got=%s want(superset of)=%s" % (warn_codes, exp_warn))
        # 点列（主形态 [[off,elev],...]；也接受 pointsDetail）
        pts = payload.get("points")
        if isinstance(pts, list) and pts and isinstance(pts[0], (list, tuple)):
            ok = len(pts) == len(expected["points"]) and all(
                abs(float(a[0]) - b[0]) <= ck.tol and abs(float(a[1]) - b[1]) <= ck.tol
                for a, b in zip(pts, expected["points"]))
            ck.check("model.points", ok,
                     "%d 点，起点距/高程与基准一致" % len(pts) if ok else
                     "点列不一致（got=%d want=%d）"
                     % (len(pts), len(expected["points"])))
        detail = payload.get("pointsDetail")
        if isinstance(detail, list) and detail:
            ok = len(detail) == len(expected["points"]) and all(
                abs(float(a["offset"]) - b[0]) <= ck.tol
                and abs(float(a["elev"]) - b[1]) <= ck.tol
                for a, b in zip(detail, expected["points"]))
            ck.check("model.pointsDetail", ok,
                     "%d 点，i/offset/elev 与基准一致" % len(detail) if ok else "不一致")
        # 参数
        prm = payload.get("params") or {}
        if prm:
            ck.near("params.hScale", prm.get("hScale"), expected["params"]["hScale"])
            ck.near("params.vScale", prm.get("vScale"), expected["params"]["vScale"])


# --------------------------------------------------------------------------- 入口
def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="断面图独立核对器（DESIGN-M1 §4.4/§5）")
    ap.add_argument("--model", required=True,
                    help="section.model.json 或 zwc.ps1 read 的响应 JSON")
    ap.add_argument("--expected", required=True, help="section-30.expected.json")
    ap.add_argument("--tamper", action="store_true",
                    help="负例自证：改一个地面线顶点 y，必须抓到 FAIL")
    ap.add_argument("--tol", type=float, default=TOL_DEFAULT, help="坐标容差（默认 1e-6）")
    ap.add_argument("--layer-prefix", default=None, help="只核对此前缀图层（默认取 expected 里的）")
    args = ap.parse_args(argv)

    expected = load_json(args.expected)
    payload = load_json(args.model)
    mode, raw, meta = detect_payload(payload)
    meta = dict(meta)
    meta["payload"] = payload if mode == "model" else None
    prefix = args.layer_prefix or expected.get("layerPrefix") or "AI-SEC"

    print("[verify] model   = %s" % os.path.abspath(args.model))
    print("[verify] expected= %s" % os.path.abspath(args.expected))
    print("[verify] mode=%s entities=%d layerPrefix=%s tol=%s%s"
          % (mode, len(raw), prefix, fmt(args.tol), "  TAMPER" if args.tamper else ""))

    if args.tamper:
        tampered = copy.deepcopy(payload)
        if mode == "read":
            ents = tampered["result"]["entities"]
        else:
            ents = tampered["entities"]
        target = None
        for e in ents:
            if kind_of(str(e.get("type") or "")) == "POLYLINE" or \
                    str(e.get("role") or "") == "ground":
                target = e
                break
        if target is None:
            print("[verify] TAMPER: FAIL（没有地面线可改，负例无效）")
            return 1
        pts = target.get("points") or target.get("vertices")
        if not pts:
            print("[verify] TAMPER: FAIL（地面线没有顶点，负例无效）")
            return 1
        p = pts[0]
        old = (p[0], p[1])
        p[1] = float(p[1]) + TAMPER_DY
        print("[verify] TAMPER: 地面线顶点#1 y %s -> %s (+%g mm)"
              % (fmt(old[1]), fmt(float(p[1])), TAMPER_DY))

        ck = Checker(max(args.tol, 1e-9))
        check_entities(ck, mode, ents, expected, prefix, meta)
        ck.report()
        if ck.failed > 0:
            print("[verify] TAMPER: PASS（改动被 %d 条断言抓到，校验有效）" % ck.failed)
            return 0
        print("[verify] TAMPER: FAIL（改动没被任何断言抓到，校验无效！）")
        return 1

    ck = Checker(args.tol)
    check_entities(ck, mode, raw, expected, prefix, meta)
    ck.report()
    return 0 if ck.failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
