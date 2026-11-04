#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_fixtures.py - build the M3-E4 benchmark fixtures.

Outputs (tests/bench/fixtures/):
  design-water.dxf    deterministic "design data" drawing used by the read/query tasks:
                      4 RIVER polylines (2 open, 1 almost-closed, 1 closed), 6 lines,
                      2 zero-length lines, 1 ground polyline, 7 texts (3 with "设计水位")
  expected.json       ground truth computed from the same numbers (no CAD needed)

The section drawings are produced at benchmark time by prepare.py (see tasks.json) so
that the benchmark measures the real agent workflow; only the static drawings live here.

Run:  set PYTHONPATH=<repo>/tools/py-libs && python make_fixtures.py
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
FIXTURES = os.path.join(HERE, "fixtures")
sys.path.insert(0, os.path.join(REPO, "tools", "py-libs"))

import ezdxf  # noqa: E402

# ---- the drawing content (single source of truth: coordinates are shared by the DXF
# ---- builder and the expected.json computation, so they cannot drift apart)

RIVER_POLYLINES = [
    dict(name="p-open-1", points=[(0.0, 0.0), (10.0, 0.0), (20.0, -2.0), (30.0, -1.0)], closed=False),
    dict(name="p-open-2", points=[(0.0, 5.0), (15.0, 6.0), (30.0, 5.5)], closed=False),
    dict(name="p-almost-closed", points=[(40.0, 0.0), (50.0, 0.0), (50.0, 10.0),
                                         (40.0, 10.0), (40.05, 0.05)], closed=False),
    dict(name="p-closed", points=[(60.0, 0.0), (70.0, 0.0), (70.0, 8.0), (60.0, 8.0)], closed=True),
]
LINES = [
    [(0.0, -5.0), (10.0, -5.0)],
    [(10.0, -5.0), (20.0, -5.0)],
    [(20.0, -5.0), (30.0, -5.0)],
    [(80.0, 0.0), (80.0, 10.0)],
    [(90.0, 0.0), (90.0, 10.0)],
    [(85.0, 5.0), (95.0, 5.0)],
]
ZERO_LINES = [
    [(20.0, 30.0), (20.0, 30.0)],
    [(35.0, 30.0), (35.0, 30.0)],
]
GROUND = [(0.0, -8.0), (30.0, -8.0), (30.0, 12.0), (0.0, 12.0)]
TEXTS = [
    ("设计水位 42.30 m", (0.0, 15.0)),
    ("设计水位 41.85 m", (20.0, 15.0)),
    ("设计水位 40.20 m", (40.0, 15.0)),
    ("校核水位 44.10 m", (60.0, 15.0)),
    ("河底高程 5.70 m", (0.0, 20.0)),
    ("断面 1-1", (20.0, 20.0)),
    ("水尺 3 号", (40.0, 20.0)),
]
TEXT_HEIGHT = 2.5


def poly_length(points, closed):
    total = 0.0
    for i in range(len(points) - 1):
        total += math.dist(points[i], points[i + 1])
    if closed and len(points) > 2:
        total += math.dist(points[-1], points[0])
    return total


def build_blank_dxf(path):
    """Empty R2007 drawing used as the per-rep writable work drawing for write/section tasks
    (path-backed so E3's strict -Doc resolution can always find it)."""
    doc = ezdxf.new("R2007", setup=False)
    doc.header["$INSUNITS"] = 4
    doc.header["$LTSCALE"] = 1.0
    doc.header["$DIMSCALE"] = 1.0
    doc.header["$TEXTSIZE"] = 2.5
    doc.layers.add("CHAOS", color=2)
    doc.saveas(path)
    return doc


def expected_document():
    """Ground truth for design-water.dxf, computed independently of CAD."""
    river_items = []
    for p in RIVER_POLYLINES:
        pts = [(round(x, 6), round(y, 6)) for x, y in p["points"]]
        gap = math.dist(pts[0], pts[-1])
        length = poly_length(pts, p["closed"])
        perimeter = poly_length(pts, False)
        river_items.append(dict(name=p["name"], closed=p["closed"], vertices=len(pts),
                                length=round(length, 6),
                                gap=round(gap, 6),
                                gapRatio=round((gap / perimeter) if perimeter else 0.0, 6)))
    river_total = round(sum(i["length"] for i in river_items), 6)
    unclosed = [i for i in river_items if not i["closed"]]
    almost = [i for i in unclosed if 0 < i["gapRatio"] <= 0.02]
    design_water = [t for t, _pos in TEXTS if "设计水位" in t]
    return {
        "schema": "zwcad-bench-expected/1",
        "fixture": "design-water.dxf",
        "totals": {
            # 4 river polylines + 6 lines + 2 zero-length + 1 ground polyline + 7 texts
            "entities": len(RIVER_POLYLINES) + len(LINES) + len(ZERO_LINES) + 1 + len(TEXTS),
            "layers": 5,
        },
        "byLayer": {
            "RIVER": len(RIVER_POLYLINES),
            "LINES": len(LINES),
            "ZERO": len(ZERO_LINES),
            "GROUND": 1,
            "ANNO": len(TEXTS),
        },
        "byKind": {
            "LWPOLYLINE": len(RIVER_POLYLINES) + len(GROUND),
            "LINE": len(LINES) + len(ZERO_LINES),
            "TEXT": len(TEXTS),
        },
        "river": {
            "count": len(RIVER_POLYLINES),
            "totalLength": river_total,
            "items": river_items,
            "closed": [i["name"] for i in river_items if i["closed"]],
            "open": [i["name"] for i in unclosed],
        },
        "unclosedPolylines": {
            "count": len(unclosed),
            "almostClosedCount": len(almost),
            "openCount": len(unclosed) - len(almost),
            "minGapRatio": min((i["gapRatio"] for i in unclosed), default=0.0),
            "maxGapRatio": max((i["gapRatio"] for i in unclosed), default=0.0),
        },
        "designWaterTexts": {"count": len(design_water), "texts": design_water},
        # real-drawing (624-entity water-system map) ground truth, captured from a
        # ZWCAD 2025 read; the snapshot is not shipped (regenerate with
        # scripts/audit/run-real-validation.ps1 on your own machine)
        "bigRead": {
            "fixture": "real-map",
            "modelCount": 624,
            "layerCount": 9,
            "byLayer": {"分水界": 2, "公路": 26, "河流": 312, "水库": 79, "县界": 12, "图层1": 191, "0": 2},
            "byKind": {"LINE": 321, "MTEXT": 128, "CIRCLE": 100, "LWPOLYLINE": 59, "ACDBHATCH": 16},
            "emptyLayers": ["电站", "补充"],
        },
    }


def build_dxf(path):
    doc = ezdxf.new("R2007", setup=False)  # R2007 = AC1021: readable by AutoCAD 2007 AND ZWCAD/2025; R2007+ DXF is UTF-8 so Chinese survives
    doc.header["$INSUNITS"] = 4          # millimetres
    doc.header["$LTSCALE"] = 1.0
    doc.header["$TEXTSIZE"] = TEXT_HEIGHT
    # a CJK capable style so the Chinese texts survive the round trip in every CAD
    style = doc.styles.add("BENCH-TEXT", font="simsun.ttc")
    for name, color in (("RIVER", 5), ("ANNO", 3), ("GROUND", 7), ("LINES", 2), ("ZERO", 6)):
        doc.layers.add(name, color=color)

    msp = doc.modelspace()
    for p in RIVER_POLYLINES:
        msp.add_lwpolyline(p["points"], close=p["closed"], dxfattribs={"layer": "RIVER"})
    for line in LINES:
        msp.add_line(line[0], line[1], dxfattribs={"layer": "LINES"})
    for line in ZERO_LINES:
        msp.add_line(line[0], line[1], dxfattribs={"layer": "ZERO"})
    msp.add_lwpolyline(GROUND, close=True, dxfattribs={"layer": "GROUND"})
    for text, (x, y) in TEXTS:
        msp.add_text(text, height=TEXT_HEIGHT,
                     dxfattribs={"layer": "ANNO", "insert": (x, y), "style": "BENCH-TEXT"})

    doc.saveas(path)
    return doc


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isdir(FIXTURES):
        os.makedirs(FIXTURES)
    dxf_path = os.path.join(FIXTURES, "design-water.dxf")
    build_dxf(dxf_path)
    blank_path = os.path.join(FIXTURES, "blank.dxf")
    build_blank_dxf(blank_path)
    print("wrote", blank_path, "(empty work drawing)")
    expected = expected_document()
    expected["designWater.dxf.sha256"] = sha256_of(dxf_path)
    with open(os.path.join(FIXTURES, "expected.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(expected, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print("wrote", dxf_path, "entities:", expected["totals"]["entities"])
    print("wrote", os.path.join(FIXTURES, "expected.json"))
    print("river total length:", expected["river"]["totalLength"],
          "unclosed:", expected["unclosedPolylines"]["count"],
          "design-water texts:", expected["designWaterTexts"]["count"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
