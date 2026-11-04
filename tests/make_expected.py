#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_expected.py -- 独立计算 tests/fixtures/section-30.expected.json（验收基准）。

**刻意不 import prepare.py**：断面真值（30 点）在这里硬编码一份，
公式按 DESIGN-M1.md §4.2/§4.3 独立写一遍。prepare.py 的输出与它任何不一致
都会在 tests/verify_section.py 里暴露 —— 这是「两份独立实现互相抓错」的设计。

真值（与 tests/make_fixtures.py 各存一份）：
  起点距 0,10,...,290 m；高程见 ELEVS（38.55..44.30，深泓点第 15 点 / 140 m）；
  设计水位 42.30 m。

运行：python tests\\make_expected.py
输出：tests\\fixtures\\section-30.expected.json
"""

from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "fixtures")
OUT = os.path.join(FIX, "section-30.expected.json")

# ---- 真值（硬编码，独立于 prepare.py 的解析逻辑） ------------------------------
OFFSETS = [i * 10.0 for i in range(30)]
ELEVS = [
    44.20, 44.00, 43.70, 43.35, 43.00, 42.60, 42.20, 41.80, 41.30, 40.80,
    40.30, 39.80, 39.30, 38.85, 38.55, 38.70, 38.95, 39.30, 39.70, 40.10,
    40.60, 41.10, 41.60, 42.10, 42.50, 42.90, 43.30, 43.70, 44.00, 44.30,
]
WATER_VALUE = 42.30
WATER_LABEL = "设计水位"

# ---- 参数（DESIGN §4.2 默认值 + A3 可用区保守值 400x277） ----------------------
H_SCALE_REQ = 500.0
V_SCALE = 50.0
PAPER = "A3"
PAPER_W, PAPER_H = 420.0, 297.0
USABLE_W, USABLE_H = 400.0, 277.0      # 保守可用区（幅面 - 20 mm）
LADDER = (500.0, 1000.0, 2000.0)
PREFIX = "AI-SEC"
ORIGIN = [0.0, 0.0]
LABEL_EVERY = 1
SHOW_AXIS = True
SHOW_POINT_NO = True
ELEV_HEIGHT = 2.0
POINT_NO_HEIGHT = 2.0
WATER_HEIGHT = 2.5
WATER_EXTEND = 5.0        # 水位线左右各外延 5 mm
WATER_TEXT_DY = 2.0       # 水位文字贴在右端上方 2 mm
ELEV_DY = 2.0             # 点高程：点上方 2 mm
POINT_NO_DY = 3.0         # 点号：点下方 3 mm
AXIS_LEFT_H = 10.0        # 左竖线 0 -> 10 mm
THAL_R = 0.6              # 深泓圆半径
THAL_DY = 7.0             # 深泓文字：最低点下方 7 mm（避开点号文字 3+2 mm）
DEC = 2


def r9(v: float) -> float:
    return round(v, 9)


def main() -> int:
    n = len(OFFSETS)
    off_min, off_max = OFFSETS[0], OFFSETS[-1]
    elev_min, elev_max = min(ELEVS), max(ELEVS)

    # --- §4.2 自动升档：取第一个使 bbox 宽 <= 可用宽的 hScale（阶梯 500->1000->2000）
    cands = [H_SCALE_REQ] + [v for v in LADDER if v > H_SCALE_REQ]
    h_scale = None
    for c in cands:
        width = (off_max - off_min) * 1000.0 / c
        if width <= USABLE_W + 1e-9:
            h_scale = c
            break
    if h_scale is None:
        raise SystemExit("expected.json: 升档阶梯用尽，数据本身超宽")
    auto_scaled = h_scale != H_SCALE_REQ

    # --- §4.2 坐标映射（逐字实现）
    elev_base = elev_min - (10.0 * V_SCALE / 1000.0)
    mm = []
    for off, elev in zip(OFFSETS, ELEVS):
        x = (off - off_min) * 1000.0 / h_scale
        y = (elev - elev_base) * 1000.0 / V_SCALE
        mm.append((x, y))

    bbox = {
        "x0": min(p[0] for p in mm),
        "y0": min(p[1] for p in mm),
        "x1": max(p[0] for p in mm),
        "y1": max(p[1] for p in mm),
    }
    width_mm = bbox["x1"] - bbox["x0"]
    height_mm = bbox["y1"] - bbox["y0"]

    layers = {
        "ground": PREFIX + "-GROUND",
        "water": PREFIX + "-WATER",
        "anno": PREFIX + "-ANNO",
        "axis": PREFIX + "-AXIS",
        "text": PREFIX + "-TEXT",
    }

    ents = []

    # 1) 地面线
    ents.append({
        "role": "ground",
        "kind": "POLYLINE",
        "type": "LWPOLYLINE",
        "layer": layers["ground"],
        "closed": False,
        "color": "BYLAYER",
        "lineWeight": 0.3,
        "points": [[r9(x), r9(y)] for x, y in mm],
    })

    # 2)+3) 水位线 + 水位文字
    wy = (WATER_VALUE - elev_base) * 1000.0 / V_SCALE
    wx0 = bbox["x0"] - WATER_EXTEND
    wx1 = bbox["x1"] + WATER_EXTEND
    water_text = "%s %.*f m" % (WATER_LABEL, DEC, WATER_VALUE)
    ents.append({
        "role": "water-line:1",
        "kind": "LINE",
        "type": "LINE",
        "layer": layers["water"],
        "dash": "DASHED",
        "points": [[r9(wx0), r9(wy)], [r9(wx1), r9(wy)]],
    })
    ents.append({
        "role": "water-text:1",
        "kind": "TEXT",
        "type": "TEXT",
        "layer": layers["anno"],
        "text": water_text,
        "pos": [r9(wx1), r9(wy + WATER_TEXT_DY)],
        "height": WATER_HEIGHT,
        "style": layers["text"],
        "rotation": 0.0,
        "align": "left",
    })

    # 4) 点高程（每 labelEvery 个点一个，从第 1 点起）
    for i, ((x, y), elev) in enumerate(zip(mm, ELEVS)):
        if i % LABEL_EVERY != 0:
            continue
        ents.append({
            "role": "elev-text:%d" % (i + 1),
            "kind": "TEXT",
            "type": "TEXT",
            "layer": layers["anno"],
            "text": "%.*f" % (DEC, elev),
            "pos": [r9(x), r9(y + ELEV_DY)],
            "height": ELEV_HEIGHT,
            "style": layers["text"],
            "rotation": 0.0,
            "align": "left",
        })

    # 5) 点号（点下方 3 mm）
    if SHOW_POINT_NO:
        for i, (x, y) in enumerate(mm):
            ents.append({
                "role": "point-no:%d" % (i + 1),
                "kind": "TEXT",
                "type": "TEXT",
                "layer": layers["anno"],
                "text": str(i + 1),
                "pos": [r9(x), r9(y - POINT_NO_DY)],
                "height": POINT_NO_HEIGHT,
                "style": layers["text"],
                "rotation": 0.0,
                "align": "left",
            })

    # 6) 基线 + 左竖线
    if SHOW_AXIS:
        ents.append({
            "role": "axis-base",
            "kind": "LINE",
            "type": "LINE",
            "layer": layers["axis"],
            "dash": None,
            "points": [[r9(bbox["x0"]), 0.0], [r9(bbox["x1"]), 0.0]],
        })
        ents.append({
            "role": "axis-left",
            "kind": "LINE",
            "type": "LINE",
            "layer": layers["axis"],
            "dash": None,
            "points": [[r9(bbox["x0"]), 0.0], [r9(bbox["x0"]), AXIS_LEFT_H]],
        })

    # 7) 深泓点：圆 + 文字（最低点，首个出现者）
    thal_i = ELEVS.index(min(ELEVS))
    tx, ty = mm[thal_i]
    ents.append({
        "role": "thalweg-circle",
        "kind": "CIRCLE",
        "type": "CIRCLE",
        "layer": layers["anno"],
        "center": [r9(tx), r9(ty)],
        "radius": THAL_R,
    })
    ents.append({
        "role": "thalweg-text",
        "kind": "TEXT",
        "type": "TEXT",
        "layer": layers["anno"],
        "text": "河底 %.*f m" % (DEC, ELEVS[thal_i]),
        "pos": [r9(tx), r9(ty - THAL_DY)],
        "height": ELEV_HEIGHT,
        "style": layers["text"],
        "rotation": 0.0,
        "align": "left",
    })

    # --- 期望计数（独立数，不从 prepare.py 的结果抄）
    by_type = {}
    by_layer = {}
    by_kind = {}
    for e in ents:
        by_type[e["type"]] = by_type.get(e["type"], 0) + 1
        by_layer[e["layer"]] = by_layer.get(e["layer"], 0) + 1
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1

    elev_text_n = sum(1 for i in range(n) if i % LABEL_EVERY == 0)
    pnt_no_n = n if SHOW_POINT_NO else 0
    axis_n = 2 if SHOW_AXIS else 0
    water_n = 1
    hand_total = (1                    # 地面线
                  + water_n            # 水位线
                  + water_n            # 水位文字
                  + elev_text_n        # 点高程
                  + pnt_no_n           # 点号
                  + axis_n             # 基线 + 左竖线
                  + 1                  # 深泓圆
                  + 1)                 # 深泓文字
    assert hand_total == len(ents), (hand_total, len(ents))

    expected = {
        "ok": True,
        "schema": "zwcad-section-expected/1",
        "fixture": "tests/fixtures/section-30.xlsx",
        "note": "由 tests/make_expected.py 独立按 DESIGN-M1 4.2/4.3 算出，勿从 prepare.py 输出复制",
        "points": [[float(o), float(e)] for o, e in zip(OFFSETS, ELEVS)],
        "pointCount": n,
        "params": {
            "hScale": h_scale,
            "hScaleRequested": H_SCALE_REQ,
            "vScale": V_SCALE,
            "paper": PAPER,
            "origin": ORIGIN,
            "labelEvery": LABEL_EVERY,
            "noAxis": not SHOW_AXIS,
            "noPointNo": not SHOW_POINT_NO,
            "layerPrefix": PREFIX,
            "textHeight": ELEV_HEIGHT,
            "waterTextHeight": WATER_HEIGHT,
        },
        "scale": {"horizontal": h_scale, "vertical": V_SCALE},
        "waterLevels": [{
            "value": WATER_VALUE,
            "label": WATER_LABEL,
            "text": water_text,
            "y": r9(wy),
            "x0": r9(wx0),
            "x1": r9(wx1),
        }],
        "paper": PAPER,
        "paperSize": {
            "name": PAPER,
            "widthMm": PAPER_W,
            "heightMm": PAPER_H,
            "usableWidthMm": USABLE_W,
            "usableHeightMm": USABLE_H,
            "marginMm": PAPER_W - USABLE_W,
        },
        "layerPrefix": PREFIX,
        "layers": layers,
        "labels": {
            "elevHeightMm": ELEV_HEIGHT,
            "pointNoHeightMm": POINT_NO_HEIGHT,
            "waterHeightMm": WATER_HEIGHT,
            "labelEvery": LABEL_EVERY,
            "showPointNo": SHOW_POINT_NO,
            "showAxis": SHOW_AXIS,
            "elevDecimals": DEC,
        },
        "style": {
            "textStyle": layers["text"],
            "groundLineWeight": 0.3,
            "waterDash": "DASHED",
            "waterExtendLeftMm": WATER_EXTEND,
            "waterExtendRightMm": WATER_EXTEND,
            "waterTextDyMm": WATER_TEXT_DY,
            "elevTextDyMm": ELEV_DY,
            "pointNoDyMm": POINT_NO_DY,
            "axisLeftHeightMm": AXIS_LEFT_H,
            "thalwegCircleRadiusMm": THAL_R,
            "thalwegTextDyMm": THAL_DY,
        },
        "bbox": {k: r9(v) for k, v in bbox.items()},
        "computed": {
            "widthMm": r9(width_mm),
            "heightMm": r9(height_mm),
            "bbox": {k: r9(v) for k, v in bbox.items()},
            "elevBase": r9(elev_base),
            "elevMin": elev_min,
            "elevMax": elev_max,
            "offsetMin": off_min,
            "offsetMax": off_max,
            "spanM": off_max - off_min,
            "autoScaled": auto_scaled,
            "autoScaledFrom": H_SCALE_REQ if auto_scaled else None,
            "autoScaledTo": h_scale if auto_scaled else None,
            "orderReversed": False,
            "thalwegIndex": thal_i + 1,
            "thalwegElev": ELEVS[thal_i],
            "fitWidth": width_mm <= USABLE_W + 1e-9,
            "fitHeight": height_mm <= USABLE_H + 1e-9,
        },
        "expected": {
            "total": hand_total,
            "byType": by_type,
            "byKind": by_kind,
            "byLayer": by_layer,
            "textCount": sum(1 for e in ents if e["kind"] == "TEXT"),
            "breakdown": {
                "ground": 1,
                "waterLine": water_n,
                "waterText": water_n,
                "elevText": elev_text_n,
                "pointNo": pnt_no_n,
                "axisLine": axis_n,
                "thalwegCircle": 1,
                "thalwegText": 1,
            },
        },
        "entities": ents,
        "warnings": [{
            "code": "AUTO_SCALED",
            "message": "断面超宽，hScale 自动升档 %g -> %g" % (H_SCALE_REQ, h_scale),
            "from": "%g" % H_SCALE_REQ,
            "to": "%g" % h_scale,
        }] if auto_scaled else [],
    }

    os.makedirs(FIX, exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(expected, fh, ensure_ascii=False, indent=2)
        fh.write("\n")

    print("WROTE %s" % OUT)
    print("total=%d byType=%s byLayer=%s" % (hand_total, json.dumps(by_type), json.dumps(by_layer)))
    print("scale=%g/%g widthMm=%.6f heightMm=%.6f elevBase=%.6f autoScaled=%s"
          % (h_scale, V_SCALE, width_mm, height_mm, elev_base, auto_scaled))
    print("bbox=%s" % json.dumps(expected["bbox"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
