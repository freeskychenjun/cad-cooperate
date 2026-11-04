#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""check_dxf.py -- 用 ezdxf（真 DXF 内核）对 section.model.json 做独立往返复核。

DESIGN-M1 §6 把「ezdxf 独立复核脚本」划给计算侧；这是**第三份实现**：
  prepare.py（计算侧） / make_expected.py（基准侧） / 本脚本（DXF 内核往返）
三方若一致，几何/文字/图层基本不可能同时错。

做法：把模型里的实体写进一个真 DXF（%TEMP%\\zwcad-cooperate\\<hash>\\，避开 DGS 对
工作区 .dxf 的透明加密）→ ezdxf 重新读回 → 逐项与 expected.json 比对。

用法：
  set PYTHONPATH=<repo>\\tools\\py-libs
  python tests\\check_dxf.py --model tmp\\section-30.model.json ^
         --expected tests\\fixtures\\section-30.expected.json [--keep]

退出码：0 全绿；1 有 FAIL；3 ezdxf 不可用（SKIP，不算失败）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
from typing import Any, Dict, List, Optional, Sequence, Tuple

TOL = 1e-9
RESULTS: List[Tuple[str, Optional[bool], str]] = []


def check(name: str, ok: Optional[bool], detail: str = "") -> bool:
    RESULTS.append((name, ok, detail))
    return bool(ok)


def near(name: str, got: float, want: float, tol: float = TOL) -> bool:
    return check(name, abs(got - want) <= tol,
                 "got=%r want=%r d=%r" % (got, want, abs(got - want)))


def load(path: str) -> Dict[str, Any]:
    with open(path, "r", encoding="utf-8-sig") as fh:
        return json.load(fh)


def model_entities(model: Dict[str, Any]) -> List[Dict[str, Any]]:
    return model.get("entities") or []


def build_dxf(model: Dict[str, Any], out_path: str) -> None:
    import ezdxf

    doc = ezdxf.new("R2010")
    msp = doc.modelspace()
    layers = model.get("layers") or {}
    style_name = (model.get("style") or {}).get("textStyle") or "AI-SEC-TEXT"

    for name in set(v for v in layers.values() if v):
        if name not in doc.layers:
            doc.layers.add(name, color=7)
    if style_name not in doc.styles:
        doc.styles.add(style_name, font="txt")
    has_dashed = "DASHED" in doc.linetypes

    for ent in model_entities(model):
        kind = ent.get("kind")
        layer = ent.get("layer")
        attribs: Dict[str, Any] = {"layer": layer}
        if kind == "POLYLINE":
            msp.add_lwpolyline([(float(x), float(y)) for x, y in ent["points"]],
                               close=bool(ent.get("closed", False)), dxfattribs=attribs)
        elif kind == "LINE":
            if ent.get("dash") and has_dashed:
                attribs["linetype"] = ent["dash"]
            (x0, y0), (x1, y1) = ent["points"]
            msp.add_line((float(x0), float(y0)), (float(x1), float(y1)), dxfattribs=attribs)
        elif kind == "TEXT":
            attribs["style"] = ent.get("style") or style_name
            attribs["insert"] = (float(ent["pos"][0]), float(ent["pos"][1]))
            msp.add_text(ent["text"], height=float(ent["height"]), dxfattribs=attribs)
        elif kind == "CIRCLE":
            msp.add_circle((float(ent["center"][0]), float(ent["center"][1])),
                           float(ent["radius"]), dxfattribs=attribs)
        else:
            raise SystemExit("unknown entity kind: %r" % kind)

    doc.saveas(out_path)


def audit_dxf(path: str) -> Tuple[bool, str]:
    import ezdxf

    doc = ezdxf.readfile(path)
    auditor = doc.audit()
    nerr = len(auditor.errors)
    nfix = len(auditor.fixes)
    return nerr == 0, "audit errors=%d fixes=%d" % (nerr, nfix)


def read_back(path: str) -> List[Dict[str, Any]]:
    import ezdxf

    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    out: List[Dict[str, Any]] = []
    for e in msp:
        t = e.dxftype()
        rec: Dict[str, Any] = {"type": t, "layer": e.dxf.layer}
        if t == "LWPOLYLINE":
            rec["points"] = [(float(p[0]), float(p[1])) for p in e.get_points("xy")]
            rec["closed"] = bool(e.closed)
        elif t == "LINE":
            rec["points"] = [(float(e.dxf.start.x), float(e.dxf.start.y)),
                             (float(e.dxf.end.x), float(e.dxf.end.y))]
        elif t == "TEXT":
            rec["text"] = e.dxf.text
            rec["pos"] = (float(e.dxf.insert.x), float(e.dxf.insert.y))
            rec["height"] = float(e.dxf.height)
            rec["style"] = e.dxf.style
        elif t == "CIRCLE":
            rec["center"] = (float(e.dxf.center.x), float(e.dxf.center.y))
            rec["radius"] = float(e.dxf.radius)
        out.append(rec)
    return out


def count_by(ents: Sequence[Dict[str, Any]], key) -> Dict[str, int]:
    out: Dict[str, int] = {}
    for e in ents:
        k = key(e)
        out[k] = out.get(k, 0) + 1
    return out


def dist(a: Sequence[float], b: Sequence[float]) -> float:
    return ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="ezdxf 独立复核（模型 -> DXF -> 回读 -> 比对基准）")
    ap.add_argument("--model", required=True)
    ap.add_argument("--expected", required=True)
    ap.add_argument("--keep", action="store_true", help="保留临时 DXF")
    args = ap.parse_args(argv)

    try:
        import ezdxf  # noqa: F401
    except ImportError:
        print("SKIP  ezdxf 不可用（把 tools\\py-libs 加进 PYTHONPATH 再跑）")
        print("[check_dxf] RESULT: SKIP")
        return 3

    model = load(args.model)
    expected = load(args.expected)

    tmp_root = os.path.join(tempfile.gettempdir(), "zwcad-cooperate")
    os.makedirs(tmp_root, exist_ok=True)
    digest = hashlib.sha1(os.path.abspath(args.model).encode("utf-8")).hexdigest()[:12]
    dxf_path = os.path.join(tmp_root, "section-%s.dxf" % digest)

    print("[check_dxf] model   = %s" % os.path.abspath(args.model))
    print("[check_dxf] dxf     = %s" % dxf_path)

    build_dxf(model, dxf_path)
    check("dxf.written", os.path.getsize(dxf_path) > 0,
          "size=%d bytes" % os.path.getsize(dxf_path))

    ok_audit, detail = audit_dxf(dxf_path)
    check("dxf.audit", ok_audit, detail)

    back = read_back(dxf_path)
    exp = expected["entities"]

    # 1) 总数 / 分类型 / 分图层
    check("dxf.entities.total", len(back) == expected["expected"]["total"],
          "got=%d want=%d" % (len(back), expected["expected"]["total"]))
    got_type = count_by(back, lambda e: e["type"])
    want_type = expected["expected"]["byType"]
    check("dxf.entities.byType", got_type == want_type,
          "got=%s want=%s" % (json.dumps(got_type, sort_keys=True),
                              json.dumps(want_type, sort_keys=True)))
    got_layer = count_by(back, lambda e: e["layer"])
    want_layer = expected["expected"]["byLayer"]
    check("dxf.entities.byLayer", got_layer == want_layer,
          "got=%s want=%s" % (json.dumps(got_layer, sort_keys=True),
                              json.dumps(want_layer, sort_keys=True)))

    # 2) 地面线顶点（DXF 往返后必须逐点相同）
    exp_ground = next(e for e in exp if e["role"] == "ground")
    got_ground = [e for e in back if e["type"] == "LWPOLYLINE"]
    check("dxf.ground.count", len(got_ground) == 1, "got=%d" % len(got_ground))
    if got_ground:
        g = got_ground[0]
        pts = g["points"]
        exp_pts = exp_ground["points"]
        check("dxf.ground.vertexCount", len(pts) == len(exp_pts),
              "got=%d want=%d" % (len(pts), len(exp_pts)))
        worst = max((dist(pts[i], exp_pts[i]) for i in range(min(len(pts), len(exp_pts)))),
                    default=9e9)
        check("dxf.ground.vertices", worst <= TOL, "maxd=%r" % worst)
        check("dxf.ground.closed", g["closed"] is False, "got=%r" % g["closed"])

    # 3) 文字（内容 + 插入点 + 字高 + 样式）
    exp_texts = [e for e in exp if e["kind"] == "TEXT"]
    got_texts = [e for e in back if e["type"] == "TEXT"]
    check("dxf.text.count", len(got_texts) == len(exp_texts),
          "got=%d want=%d" % (len(got_texts), len(exp_texts)))
    pool = list(got_texts)
    bad = []
    for e in exp_texts:
        hit = None
        best = 9e9
        for cand in pool:
            if cand["text"] != e["text"]:
                continue
            d = dist(cand["pos"], e["pos"])
            if d < best:
                hit, best = cand, d
        if hit is None or best > TOL:
            bad.append((e["role"], e["text"], best))
            continue
        pool.remove(hit)
        if abs(hit["height"] - e["height"]) > TOL:
            bad.append((e["role"], "height", hit["height"], e["height"]))
    check("dxf.text.match", not bad, "全部匹配" if not bad else "首个不符=%r" % (bad[0],))

    # 4) 线段（水位线 + 轴线）端点集合
    exp_lines = [e for e in exp if e["kind"] == "LINE"]
    got_lines = [e for e in back if e["type"] == "LINE"]
    check("dxf.line.count", len(got_lines) == len(exp_lines),
          "got=%d want=%d" % (len(got_lines), len(exp_lines)))
    key = lambda pts: tuple(sorted((round(p[0], 6), round(p[1], 6)) for p in pts))
    want_keys = set(key(e["points"]) for e in exp_lines)
    got_keys = set(key(e["points"]) for e in got_lines)
    check("dxf.line.endpoints", want_keys == got_keys,
          "全部端点一致" if want_keys == got_keys else
          "got=%s want=%s" % (sorted(got_keys), sorted(want_keys)))

    # 5) 圆
    exp_circle = next(e for e in exp if e["kind"] == "CIRCLE")
    got_circles = [e for e in back if e["type"] == "CIRCLE"]
    check("dxf.circle.count", len(got_circles) == 1, "got=%d" % len(got_circles))
    if got_circles:
        c = got_circles[0]
        check("dxf.circle.center",
              dist(c["center"], exp_circle["center"]) <= TOL,
              "got=%r want=%r" % (c["center"], exp_circle["center"]))
        near("dxf.circle.radius", c["radius"], exp_circle["radius"])

    if not args.keep:
        try:
            os.remove(dxf_path)
        except OSError:
            pass

    failed = sum(1 for _, ok, _ in RESULTS if ok is False)
    width = max(len(n) for n, _, _ in RESULTS)
    for name, ok, detail in RESULTS:
        tag = "PASS" if ok is True else ("FAIL" if ok is False else "SKIP")
        print("%-4s  %-*s  %s" % (tag, width, name, detail))
    print("-" * 72)
    print("[check_dxf] RESULT: %s  (%d PASS / %d FAIL)"
          % ("FAIL" if failed else "PASS", len(RESULTS) - failed, failed))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
