#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""prep_model.py -- prepare.py 的「建模 / 图幅 / 比例」层（2026-11-05 从 prepare.py 搬出）。

职责：
  * §4.2 坐标映射的**单一实现**（CoordMapper / measure_bbox / resolve_layer_prefix /
    append_too_tall_warning），供 v1 build_model 与 v2 build_db_model 共用
  * 点列校验 validate_points、图幅 resolve_paper、自动升档 choose_h_scale
  * schema v1 单序列模型 build_model、CLI 取值解析 parse_origin / parse_water

**纯搬移 + 一处合并**：除 import/模块头与上面列出的重复段合并外，函数体逐字来自 prepare.py。
"""

from __future__ import annotations

import argparse
import math
import os
import re
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from prep_io import SectionError

# 消费侧契约（与 core-cad 的 helper 对齐，2026-11-04 冻结）：helper 只吃 JSON、不解析 xlsx。
SCHEMA_MODEL = "zwcad-section-model/1"

# 图纸：可用绘图区 = 幅面 - 20 mm（DESIGN §4.2：A3 可用约 400x277，420-20 / 297-20）
PAPERS: Dict[str, Tuple[float, float]] = {
    "A0": (1189.0, 841.0),
    "A1": (841.0, 594.0),
    "A2": (594.0, 420.0),
    "A3": (420.0, 297.0),
    "A4": (210.0, 297.0),
}
PAPER_MARGIN_MM = 20.0

# 超宽自动升档阶梯（DESIGN §4.2：500 -> 1000 -> 2000）
H_SCALE_LADDER: Tuple[float, ...] = (500.0, 1000.0, 2000.0)


# ----------------------------------------------------------------------------- 坐标映射（v1/v2 单一实现）
# 2026-11-05 模块化：build_model 与 build_db_model 原来各抄一遍 §4.2 的坐标映射、bbox 计算、
# TOO_TALL 告警、图层前缀解析；现在合并为下面这一份实现，两个调用点只差「传入谁的 min/max」。
TEXT_HEIGHT_MM = 2.0        # 高程/点号文字高度（params.textHeight / labels.elevHeightMm）
WATER_TEXT_HEIGHT_MM = 2.5  # 水位文字高度（params.waterTextHeight / labels.waterHeightMm）


class CoordMapper:
    """§4.2 坐标映射（纸面 mm）：x=(off-offMin)*1000/h + ox，y=(elev-elevBase)*1000/v + oy。

    elevBase = elevMin - 10*vScale/1000（最低点上方留 10 mm 底边距）。
    算式与运算顺序逐字保持原实现，v1 build_model 与 v2 build_db_model 共用。
    """

    __slots__ = ("offMin", "elevBase", "kx", "ky", "ox", "oy")

    def __init__(self, off_min: float, elev_min: float, h_scale: float, v_scale: float,
                 origin_xy: Tuple[float, float]) -> None:
        self.offMin = off_min
        self.elevBase = elev_min - (10.0 * v_scale / 1000.0)
        self.kx = 1000.0 / h_scale          # mm per m（横向）
        self.ky = 1000.0 / v_scale          # mm per m（纵向）
        self.ox, self.oy = origin_xy

    def to_mm(self, off: float, elev: float) -> Tuple[float, float]:
        return ((off - self.offMin) * self.kx + self.ox,
                (elev - self.elevBase) * self.ky + self.oy)

    def map_all(self, pts: Iterable[Tuple[float, float]]) -> List[Tuple[float, float]]:
        return [self.to_mm(off, elev) for off, elev in pts]


def measure_bbox(pts_mm: Sequence[Tuple[float, float]]) -> Tuple[Dict[str, float], float, float]:
    """bbox{x0,y0,x1,y1} + 宽/高（mm）；v1 build_model 与 v2 build_db_model 共用。"""
    bbox = {
        "x0": min(p[0] for p in pts_mm),
        "y0": min(p[1] for p in pts_mm),
        "x1": max(p[0] for p in pts_mm),
        "y1": max(p[1] for p in pts_mm),
    }
    return bbox, bbox["x1"] - bbox["x0"], bbox["y1"] - bbox["y0"]


def resolve_layer_prefix(text: Optional[str]) -> str:
    """--layer-prefix 解析（默认 AI-SEC；空串 -> BAD_ARGS）；v1/v2 共用。"""
    prefix = (text or "AI-SEC").strip()
    if not prefix:
        raise SectionError("BAD_ARGS", "--layer-prefix 不能为空")
    return prefix


def append_too_tall_warning(warnings: List[Dict[str, Any]], height_mm: float,
                            usable_height_mm: float) -> None:
    """TOO_TALL 告警（高度超出图幅可用区）；v1/v2 共用同一文案与字段。"""
    if height_mm > usable_height_mm + 1e-9:
        warnings.append({
            "code": "TOO_TALL",
            "message": "断面高度超出图幅可用区（hScale 升档不影响纵向，需调小 vScale）",
            "heightMm": round(height_mm, 3),
            "usableHeightMm": usable_height_mm,
        })


# ----------------------------------------------------------------------------- 校验
def validate_points(points: List[Dict[str, float]]) -> Tuple[List[Dict[str, float]], bool]:
    """校验并（必要时）反转点序；返回 (反序后点列, orderReversed)。"""
    if len(points) < 3:
        raise SectionError("BAD_DATA", "有效点不足 3 个", pointCount=len(points))

    offs = [p["offset"] for p in points]
    elevs = [p["elev"] for p in points]

    if max(elevs) - min(elevs) <= 0.0:
        raise SectionError("BAD_DATA", "高程全等，无法成图",
                           elevMin=min(elevs), elevMax=max(elevs))

    diffs = [offs[i + 1] - offs[i] for i in range(len(offs) - 1)]
    if all(d > 0.0 for d in diffs):
        return points, False
    if all(d < 0.0 for d in diffs):
        return list(reversed(points)), True
    bad_at = next(i for i, d in enumerate(diffs) if d == 0.0) if any(d == 0.0 for d in diffs) \
        else next(i for i, d in enumerate(diffs) if d < 0.0)
    raise SectionError(
        "BAD_DATA", "起点距非单调递增（容差 0）",
        atRowIndex=bad_at + 1,
        offsets=[offs[bad_at], offs[bad_at + 1]],
        pointCount=len(points))


# ----------------------------------------------------------------------------- 图纸与比例
def resolve_paper(paper: str) -> Dict[str, Any]:
    key = (paper or "A3").strip().upper()
    if key in PAPERS:
        w, h = PAPERS[key]
    else:
        m = re.fullmatch(r"(\d+(?:\.\d+)?)\s*[xX*×]\s*(\d+(?:\.\d+)?)", key)
        if not m:
            raise SectionError("BAD_ARGS", "未知图幅",
                               paper=paper, supported=list(PAPERS) + ["WxH"])
        w, h = float(m.group(1)), float(m.group(2))
        key = "%gx%g" % (w, h)
    usable_w = w - PAPER_MARGIN_MM
    usable_h = h - PAPER_MARGIN_MM
    if usable_w <= 0 or usable_h <= 0:
        raise SectionError("BAD_ARGS", "图幅可用区非正", paper=paper, width=w, height=h)
    return {"name": key, "widthMm": w, "heightMm": h,
            "usableWidthMm": usable_w, "usableHeightMm": usable_h,
            "marginMm": PAPER_MARGIN_MM}


def choose_h_scale(h_scale: float, span_m: float, usable_w: float,
                   no_auto_scale: bool) -> Tuple[float, bool, List[Dict[str, str]]]:
    """按 §4.2 决定实际 hScale：先试请求值，再按 500->1000->2000 整档放大。"""
    warnings: List[Dict[str, str]] = []

    def width_at(s: float) -> float:
        return span_m * 1000.0 / s

    if width_at(h_scale) <= usable_w + 1e-9:
        return h_scale, False, warnings

    if no_auto_scale:
        raise SectionError(
            "TOO_WIDE", "断面宽度超出图幅可用区，且已禁用自动升档",
            requestedHScale=h_scale, widthMm=round(width_at(h_scale), 3),
            usableWidthMm=usable_w, spanM=span_m)

    candidates = [h_scale] + [v for v in H_SCALE_LADDER if v > h_scale]
    for cand in candidates[1:]:
        if width_at(cand) <= usable_w + 1e-9:
            warnings.append({
                "code": "AUTO_SCALED",
                "message": "断面超宽，hScale 自动升档 %g -> %g" % (h_scale, cand),
                "from": "%g" % h_scale,
                "to": "%g" % cand,
            })
            return cand, True, warnings

    raise SectionError(
        "TOO_WIDE", "断面宽度超出图幅可用区，自动升档阶梯已用尽",
        requestedHScale=h_scale, maxHScale=candidates[-1],
        widthMm=round(width_at(candidates[-1]), 3), usableWidthMm=usable_w)


# ----------------------------------------------------------------------------- 模型构建
def build_model(points: List[Dict[str, float]], order_reversed: bool, args: argparse.Namespace,
                meta: Dict[str, Any], water_levels: List[Tuple[float, str]]) -> Dict[str, Any]:
    paper = resolve_paper(args.paper)
    h_scale = float(args.h_scale)
    v_scale = float(args.v_scale)
    if h_scale <= 0 or v_scale <= 0:
        raise SectionError("BAD_ARGS", "比例必须为正数", hScale=h_scale, vScale=v_scale)
    if args.label_every < 1:
        raise SectionError("BAD_ARGS", "--label-every 必须 >= 1", labelEvery=args.label_every)

    offs = [p["offset"] for p in points]
    elevs = [p["elev"] for p in points]
    off_min, off_max = offs[0], offs[-1]
    elev_min, elev_max = min(elevs), max(elevs)
    span_m = off_max - off_min

    warnings: List[Dict[str, str]] = []
    if order_reversed:
        warnings.append({
            "code": "ORDER_REVERSED",
            "message": "起点距递减，已自动反转为上游->下游（偏移量最小者在前）",
        })

    used_h, auto_scaled, w2 = choose_h_scale(h_scale, span_m, paper["usableWidthMm"],
                                             bool(args.no_auto_scale))
    warnings.extend(w2)

    # §4.2 坐标映射：单一实现 CoordMapper（v2 build_db_model 共用同一份算式）
    mapper = CoordMapper(off_min, elev_min, used_h, v_scale, args.origin_xy)
    elev_base, ky = mapper.elevBase, mapper.ky     # 水位线 y 仍用同一 ky
    ox, oy = mapper.ox, mapper.oy
    origin = [float(ox), float(oy)]
    pts_mm = mapper.map_all(zip(offs, elevs))
    bbox, width_mm, height_mm = measure_bbox(pts_mm)

    if paper["usableHeightMm"] <= 0:
        raise SectionError("BAD_ARGS", "图幅可用高度非正")

    prefix = resolve_layer_prefix(args.layer_prefix)
    layers = {
        "ground": prefix + "-GROUND",
        "water": prefix + "-WATER",
        "anno": prefix + "-ANNO",
        "axis": prefix + "-AXIS",
        "text": prefix + "-TEXT",
    }

    labels = {
        "elevHeightMm": TEXT_HEIGHT_MM,
        "pointNoHeightMm": TEXT_HEIGHT_MM,
        "waterHeightMm": WATER_TEXT_HEIGHT_MM,
        "labelEvery": int(args.label_every),
        "showPointNo": not bool(args.no_point_no),
        "showAxis": not bool(args.no_axis),
        "elevDecimals": 2,
        "waterTextSuffix": " m",
        "thalwegPrefix": "河底",
    }
    style = {
        "textStyle": layers["text"],
        "textWidthFactor": 1.0,
        "groundColor": "BYLAYER",
        "groundLineWeight": 0.3,
        "waterDash": "DASHED",
        "waterExtendLeftMm": 5.0,
        "waterExtendRightMm": 5.0,
        "waterTextDyMm": 2.0,
        "elevTextDyMm": 2.0,
        "pointNoDyMm": 3.0,
        "axisLeftHeightMm": 10.0,
        "thalwegCircleRadiusMm": 0.6,
        "thalwegTextDyMm": 7.0,
    }

    entities: List[Dict[str, Any]] = []

    def add(ent: Dict[str, Any]) -> None:
        entities.append(ent)

    # 1) 地面线（LWPOLYLINE，闭合=false）
    add({
        "role": "ground",
        "kind": "POLYLINE",
        "type": "LWPOLYLINE",
        "layer": layers["ground"],
        "closed": False,
        "color": style["groundColor"],
        "lineWeight": style["groundLineWeight"],
        "points": [[round(x, 9), round(y, 9)] for x, y in pts_mm],
    })

    # 2) 水位线 + 3) 水位文字
    water_out: List[Dict[str, Any]] = []
    for idx, (wlv, wlabel) in enumerate(water_levels):
        wy = (wlv - elev_base) * ky + oy
        x0 = bbox["x0"] - style["waterExtendLeftMm"]
        x1 = bbox["x1"] + style["waterExtendRightMm"]
        text = "%s %.*f%s" % (wlabel, labels["elevDecimals"], wlv,
                              labels["waterTextSuffix"])
        add({
            "role": "water-line:%d" % (idx + 1),
            "kind": "LINE",
            "type": "LINE",
            "layer": layers["water"],
            "dash": style["waterDash"],
            "points": [[round(x0, 9), round(wy, 9)], [round(x1, 9), round(wy, 9)]],
        })
        add({
            "role": "water-text:%d" % (idx + 1),
            "kind": "TEXT",
            "type": "TEXT",
            "layer": layers["anno"],
            "text": text,
            "pos": [round(x1, 9), round(wy + style["waterTextDyMm"], 9)],
            "height": labels["waterHeightMm"],
            "style": style["textStyle"],
            "rotation": 0.0,
            "align": "left",
        })
        water_out.append({
            "value": wlv,
            "label": wlabel,
            "text": text,
            "y": round(wy, 9),
            "x0": round(x0, 9),
            "x1": round(x1, 9),
        })

    # 4) 点高程（每 labelEvery 个点一个）
    every = int(args.label_every)
    elev_text_count = 0
    for i, ((x, y), elev) in enumerate(zip(pts_mm, elevs)):
        if i % every != 0:
            continue
        add({
            "role": "elev-text:%d" % (i + 1),
            "kind": "TEXT",
            "type": "TEXT",
            "layer": layers["anno"],
            "text": "%.*f" % (labels["elevDecimals"], elev),
            "pos": [round(x, 9), round(y + style["elevTextDyMm"], 9)],
            "height": labels["elevHeightMm"],
            "style": style["textStyle"],
            "rotation": 0.0,
            "align": "left",
        })
        elev_text_count += 1

    # 5) 点号（点下方 3 mm）
    pnt_no_count = 0
    if labels["showPointNo"]:
        for i, (x, y) in enumerate(pts_mm):
            add({
                "role": "point-no:%d" % (i + 1),
                "kind": "TEXT",
                "type": "TEXT",
                "layer": layers["anno"],
                "text": str(i + 1),
                "pos": [round(x, 9), round(y - style["pointNoDyMm"], 9)],
                "height": labels["pointNoHeightMm"],
                "style": style["textStyle"],
                "rotation": 0.0,
                "align": "left",
            })
            pnt_no_count += 1

    # 6) 基线 + 左竖线（§4.3 #6：底部 y=0 横线 x=0..W；左端竖线 0..10 mm）
    axis_count = 0
    if labels["showAxis"]:
        y_base = oy + 0.0
        add({
            "role": "axis-base",
            "kind": "LINE",
            "type": "LINE",
            "layer": layers["axis"],
            "dash": None,
            "points": [[round(bbox["x0"], 9), round(y_base, 9)],
                       [round(bbox["x1"], 9), round(y_base, 9)]],
        })
        add({
            "role": "axis-left",
            "kind": "LINE",
            "type": "LINE",
            "layer": layers["axis"],
            "dash": None,
            "points": [[round(bbox["x0"], 9), round(y_base, 9)],
                       [round(bbox["x0"], 9),
                        round(y_base + style["axisLeftHeightMm"], 9)]],
        })
        axis_count = 2

    # 7) 深泓点：圆 + 文字（最低点，第一个出现者）
    thal_idx = min(range(len(elevs)), key=lambda k: elevs[k])
    tx, ty = pts_mm[thal_idx]
    add({
        "role": "thalweg-circle",
        "kind": "CIRCLE",
        "type": "CIRCLE",
        "layer": layers["anno"],
        "center": [round(tx, 9), round(ty, 9)],
        "radius": style["thalwegCircleRadiusMm"],
    })
    thal_text = "%s %.*f%s" % (labels["thalwegPrefix"], labels["elevDecimals"], elevs[thal_idx],
                               labels["waterTextSuffix"])
    add({
        "role": "thalweg-text",
        "kind": "TEXT",
        "type": "TEXT",
        "layer": layers["anno"],
        "text": thal_text,
        "pos": [round(tx, 9), round(ty - style["thalwegTextDyMm"], 9)],
        "height": labels["elevHeightMm"],
        "style": style["textStyle"],
        "rotation": 0.0,
        "align": "left",
    })

    by_type: Dict[str, int] = {}
    by_layer: Dict[str, int] = {}
    by_kind: Dict[str, int] = {}
    for ent in entities:
        by_type[ent["type"]] = by_type.get(ent["type"], 0) + 1
        by_layer[ent["layer"]] = by_layer.get(ent["layer"], 0) + 1
        by_kind[ent["kind"]] = by_kind.get(ent["kind"], 0) + 1

    extent = {
        "x0": bbox["x0"],
        "y0": min(bbox["y0"], oy if labels["showAxis"] else bbox["y0"]),
        "x1": bbox["x1"],
        "y1": max(bbox["y1"], oy + style["axisLeftHeightMm"]) if labels["showAxis"] else bbox["y1"],
    }

    model: Dict[str, Any] = {
        "ok": True,
        "schema": SCHEMA_MODEL,
        "design": "DESIGN-M1.md 4.2/4.3",
        "source": {
            "file": os.path.abspath(args.data),
            "path": os.path.abspath(args.data),
            "sheet": meta.get("sheet"),
            "rows": meta.get("totalRows"),
            "headerRow": meta.get("headerRow"),
            "firstDataRow": meta.get("firstDataRow"),
            "lastDataRow": meta.get("lastDataRow"),
            "offsetColumn": meta.get("offsetColumn"),
            "elevColumn": meta.get("elevColumn"),
            "pointCount": len(points),
            "cols": args.cols,
        },
        # 主形态（helper 必填）：[[offset_m, elev_m], ...]，按行序
        "points": [[float(p["offset"]), float(p["elev"])] for p in points],
        # 附加：带 1-based 点号，便于索引核对/排错
        "pointsDetail": [{"i": i + 1, "offset": float(p["offset"]), "elev": float(p["elev"])}
                         for i, p in enumerate(points)],
        "params": {
            "hScale": used_h,
            "vScale": v_scale,
            "paper": paper["name"],
            "origin": origin,
            "labelEvery": int(args.label_every),
            "noAxis": bool(args.no_axis),
            "noPointNo": bool(args.no_point_no),
            "noAutoScale": bool(args.no_auto_scale),
            "layerPrefix": prefix,
            "textHeight": TEXT_HEIGHT_MM,
            "waterTextHeight": WATER_TEXT_HEIGHT_MM,
        },
        "scale": {"horizontal": used_h, "vertical": v_scale},
        "waterLevels": water_out,
        "paper": paper["name"],
        "paperSize": paper,
        "origin": origin,
        "labels": labels,
        "style": style,
        "layerPrefix": prefix,
        "layers": layers,
        "entities": entities,
        "expected": {
            "total": len(entities),
            "byType": by_type,
            "byKind": by_kind,
            "byLayer": by_layer,
            "textCount": sum(1 for e in entities if e["kind"] == "TEXT"),
        },
        "computed": {
            "scale": {"horizontal": used_h, "vertical": v_scale},
            "widthMm": round(width_mm, 9),
            "heightMm": round(height_mm, 9),
            "width_mm": round(width_mm, 9),
            "height_mm": round(height_mm, 9),
            "bbox": {k: round(v, 9) for k, v in bbox.items()},
            "extent": {k: round(v, 9) for k, v in extent.items()},
            "elevBase": round(elev_base, 9),
            "elevMin": elev_min,
            "elevMax": elev_max,
            "offsetMin": off_min,
            "offsetMax": off_max,
            "spanM": span_m,
            "autoScaled": auto_scaled,
            "autoScaledFrom": h_scale if auto_scaled else None,
            "autoScaledTo": used_h if auto_scaled else None,
            "orderReversed": order_reversed,
            "thalwegIndex": thal_idx + 1,
            "thalwegElev": elevs[thal_idx],
            "fitWidth": width_mm <= paper["usableWidthMm"] + 1e-9,
            "fitHeight": height_mm <= paper["usableHeightMm"] + 1e-9,
            "textCounts": {
                "elev": elev_text_count,
                "pointNo": pnt_no_count,
                "water": len(water_out),
                "thalweg": 1,
            },
            "axisLines": axis_count,
        },
        "warnings": warnings,
    }

    append_too_tall_warning(warnings, height_mm, paper["usableHeightMm"])

    return model


# ----------------------------------------------------------------------------- CLI
def parse_origin(text: str) -> Tuple[float, float]:
    try:
        parts = [float(p) for p in str(text).split(",")]
    except ValueError:
        raise SectionError("BAD_ARGS", "--origin 需形如 x,y", origin=text)
    if len(parts) != 2:
        raise SectionError("BAD_ARGS", "--origin 需形如 x,y", origin=text)
    return parts[0], parts[1]


def parse_water(items: Optional[List[str]]) -> List[Tuple[float, str]]:
    out: List[Tuple[float, str]] = []
    for raw in items or []:
        text = str(raw).strip()
        if not text:
            raise SectionError("BAD_ARGS", "--water 不能为空")
        if "=" in text:
            vpart, label = text.split("=", 1)
            label = label.strip() or "水位"
        else:
            vpart, label = text, "水位"
        try:
            value = float(vpart.strip())
        except ValueError:
            raise SectionError("BAD_ARGS", "--water 水位值不是数字", water=raw)
        if not math.isfinite(value):
            raise SectionError("BAD_ARGS", "--water 水位值非有限", water=raw)
        out.append((value, label))
    return out
