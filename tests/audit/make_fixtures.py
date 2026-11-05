#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_fixtures.py - build the audit demo/regression fixtures.

Writes into tests/audit/fixtures/:

  problem-rich.dxf          a drawing that carries one of every problem the audit knows
  problem-rich.info.json    cad.ps1 info shaped JSON for that drawing
  problem-rich.read.json    cad.ps1 read shaped JSON for that drawing
  clean.info.json/.read    a well-formed drawing (must produce zero issues)
  strict.info.json/.read   layer / style edge cases used by the strict-mode tests
  expected-hits.json       rule -> minimum expected hits (regression contract)

The DXF is the source of truth for the two "real" drawing fixtures: the JSON pair
is exported from the same entity table so an offline run is deterministic and does
not need CAD. The DXF itself is then opened in CAD (tests/audit/run-real-validation.ps1)
to prove that the audit also works on genuine `cad.ps1 read` output.

Run:  set PYTHONPATH=<repo>/tools/py-libs && python make_fixtures.py
"""

from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "fixtures")

REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools", "py-libs"))

import ezdxf  # noqa: E402


def ensure_dir(path):
    if not os.path.isdir(path):
        os.makedirs(path)


# --------------------------------------------------------------------------- dxf


def build_problem_dxf(path):
    doc = ezdxf.new("R2000", setup=False)
    msp = doc.modelspace()

    # ---- header variables: the drawing-level problems
    doc.header["$INSUNITS"] = 0        # unitless      -> units_missing (blocker)
    doc.header["$LTSCALE"] = 0         # broken dashes -> linetype_scale
    doc.header["$DIMSCALE"] = 0        # auto (0)      -> dim_scale_invalid
    doc.header["$TEXTSIZE"] = 2.5
    doc.header["$MEASUREMENT"] = 1
    # a tight box around the real content: the stray line at x=5000 is outside it
    doc.header["$EXTMIN"] = (0.0, 0.0, 0.0)
    doc.header["$EXTMAX"] = (100.0, 60.0, 0.0)

    # ---- layers
    doc.layers.add("BODY", color=7)
    doc.layers.add("ANNO", color=3)
    doc.layers.add("FROZEN-L", color=1).freeze()      # binary 0x01 -> frozen
    doc.layers.add("EMPTY-1", color=5)               # layer_empty
    doc.layers.add(" bad name", color=2)             # layer_naming (space)
    doc.layers.add("LONG_LAYER_NAME_ABCDEFGHIJKLMNOP", color=6)  # 32 chars

    # ---- a custom linetype: the heuristic linetype check will flag it (documented FP)
    doc.linetypes.add(
        "WATER_DASH",
        pattern=[0.6, 0.3, -0.15],
        description="demo custom dash",
    )

    # ---- normal geometry (so most rules have a healthy baseline)
    ground = [(0, 20), (10, 14), (20, 10), (30, 12), (40, 8), (50, 9),
              (60, 11), (70, 10), (80, 12), (90, 15), (100, 20)]
    msp.add_lwpolyline(ground, dxfattribs={"layer": "BODY"})
    for y in (5, 25, 35, 45, 55):
        msp.add_line((0, y), (100, y), dxfattribs={"layer": "BODY"})
    for x in (10, 20, 30, 40, 50, 60, 70, 80, 90):
        msp.add_line((x, 0), (x, 60), dxfattribs={"layer": "BODY"})

    # ---- zero length line
    msp.add_line((10, 10), (10, 10), dxfattribs={"layer": "BODY"})

    # ---- duplicated line + duplicated circle + duplicated text
    msp.add_line((20, 0), (20, 10), dxfattribs={"layer": "BODY"})
    msp.add_line((20, 0), (20, 10), dxfattribs={"layer": "BODY"})
    msp.add_circle((60, 40), 5, dxfattribs={"layer": "BODY"})
    msp.add_circle((60, 40), 5, dxfattribs={"layer": "BODY"})

    # ---- polyline that almost closes (gap 0.08 on a perimeter of ~30)
    msp.add_lwpolyline(
        [(70, 20), (80, 20), (80, 30), (70, 30), (70.08, 20.08)],
        close=False, dxfattribs={"layer": "BODY"},
    )

    # ---- custom linetype used by one line
    msp.add_line((0, 30), (20, 35), dxfattribs={"layer": "BODY", "linetype": "WATER_DASH"})

    # ---- entities on the frozen layer -> layer_off_but_used (blocker)
    msp.add_line((0, 45), (15, 50), dxfattribs={"layer": "FROZEN-L"})
    msp.add_line((15, 50), (30, 45), dxfattribs={"layer": "FROZEN-L"})

    # ---- text: 12 normal labels (median basis), 2 outliers, 1 empty, 2 overlapping
    for i in range(12):
        msp.add_text("P%d" % (i + 1), height=2.5,
                     dxfattribs={"layer": "ANNO", "insert": (2 + i * 7.0, 50)})
    msp.add_text("TOO-SMALL", height=0.3, dxfattribs={"layer": "ANNO", "insert": (5, 30)})
    msp.add_text("TOO-LARGE-MID", height=8.0, dxfattribs={"layer": "ANNO", "insert": (30, 5)})
    # the single largest text is treated as a title / 图名 and exempted by the audit
    msp.add_text("TOO-BIG-TITLE-LIKE", height=12.0, dxfattribs={"layer": "ANNO", "insert": (5, 5)})
    msp.add_text("", height=2.5, dxfattribs={"layer": "ANNO", "insert": (85, 55)})
    msp.add_text("OVERLAP-A", height=2.5, dxfattribs={"layer": "ANNO", "insert": (70, 42)})
    msp.add_text("OVERLAP-B", height=2.5, dxfattribs={"layer": "ANNO", "insert": (70.5, 42.5)})
    msp.add_text("OVERLAP-B", height=2.5, dxfattribs={"layer": "ANNO", "insert": (70.5, 42.5)})

    # ---- dimension: read gives no measurement, so the audit reports not_measured
    dim = msp.add_linear_dim(base=(0, -6), p1=(10, 0), p2=(50, 0),
                             dimstyle="Standard", override={"dimtxt": 2.5})
    dim.render()

    # ---- stray entity far outside the drawing extents
    msp.add_line((5000, 5000), (5010, 5000), dxfattribs={"layer": "BODY"})

    doc.saveas(path)
    return doc


# --------------------------------------------------------------------------- json export


def _bbox_of(entity):
    """Same contract as the helper: bbox only for LINE/CIRCLE/ARC/LWPOLYLINE."""
    kind = entity.dxftype()
    try:
        if kind == "LINE":
            s, e = entity.dxf.start, entity.dxf.end
            return {"min": [min(s.x, e.x), min(s.y, e.y)],
                    "max": [max(s.x, e.x), max(s.y, e.y)]}
        if kind in ("CIRCLE", "ARC"):
            c, r = entity.dxf.center, abs(entity.dxf.radius)
            return {"min": [c.x - r, c.y - r], "max": [c.x + r, c.y + r]}
        if kind in ("LWPOLYLINE", "POLYLINE"):
            pts = [(p[0], p[1]) for p in entity.get_points()] if kind == "LWPOLYLINE" else \
                  [(v.dxf.location.x, v.dxf.location.y) for v in entity.vertices]
            if not pts:
                return None
            xs = [p[0] for p in pts]
            ys = [p[1] for p in pts]
            return {"min": [min(xs), min(ys)], "max": [max(xs), max(ys)]}
    except Exception:
        return None
    return None


def export_entities(doc):
    out = []
    for entity in doc.modelspace():
        kind = entity.dxftype()
        item = {
            "handle": entity.dxf.handle,
            "type": "AcDb" + kind.title().replace("Polyline", "Polyline"),
            "kind": kind,
            "layer": entity.dxf.get("layer", "0"),
            "space": "model",
        }
        bb = _bbox_of(entity)
        if bb:
            item["bbox"] = bb
        if kind == "LINE":
            s, e = entity.dxf.start, entity.dxf.end
            item["start"] = [s.x, s.y]
            item["end"] = [e.x, e.y]
            item["points"] = [[s.x, s.y], [e.x, e.y]]
            lt = entity.dxf.get("linetype", "BYLAYER")
            item["linetype"] = "ByLayer" if str(lt).upper() in ("BYLAYER", "CONTINUOUS") else lt
        elif kind == "LWPOLYLINE":
            pts = [[p[0], p[1]] for p in entity.get_points()]
            item["vertices"] = pts
            item["points"] = pts
            item["closed"] = bool(entity.closed)
        elif kind == "CIRCLE":
            c = entity.dxf.center
            item["center"] = [c.x, c.y]
            item["radius"] = entity.dxf.radius
        elif kind == "TEXT":
            item["text"] = entity.dxf.text
            ins = entity.dxf.insert
            item["insertion"] = [ins.x, ins.y]
            item["pos"] = [ins.x, ins.y]
            item["height"] = entity.dxf.height
            item["style"] = entity.dxf.get("style", "Standard")
        elif kind == "MTEXT":
            item["text"] = entity.text
            ins = entity.dxf.insert
            item["insertion"] = [ins.x, ins.y]
            item["pos"] = [ins.x, ins.y]
            item["height"] = entity.dxf.get("char_height", 2.5)
            item["style"] = entity.dxf.get("style", "Standard")
        elif kind == "DIMENSION":
            item["kind"] = "ACDB" + str(entity.dxf.get("dimtype", 0))
        out.append(item)
    return out


def export_info(doc, entity_count):
    units = {}
    for var in ("$INSUNITS", "$DIMSCALE", "$LTSCALE", "$TEXTSIZE", "$MEASUREMENT", "$ANNOALLVISIBLE"):
        try:
            units[var[1:].lower()] = doc.header.get(var)
        except Exception:
            units[var[1:].lower()] = None
    extents = {"min": None, "max": None}
    try:
        extents["min"] = list(doc.header["$EXTMIN"])
        extents["max"] = list(doc.header["$EXTMAX"])
    except Exception:
        pass
    layers = []
    for layer in doc.layers:
        layers.append({
            "name": layer.dxf.name,
            "color": layer.dxf.get("color", 7),
            "frozen": bool(layer.is_frozen()),
            "locked": bool(layer.is_locked()),
            "linetype": layer.dxf.get("linetype", "CONTINUOUS"),
        })
    styles = []
    for style in doc.styles:
        styles.append({
            "name": style.dxf.name,
            "font": style.dxf.get("font", "txt"),
            "height": style.dxf.get("height", 0),
        })
    return {
        "units": units,
        "extents": extents,
        "layers": layers,
        "textStyles": styles,
        "counts": {"model": entity_count, "paper": 0, "blocks": 4, "layouts": 1, "documents": 1},
        "matchedBy": "new",
    }


def wrap(cmd, result, doc_name):
    return {
        "wallMs": 1,
        "ok": True,
        "cmd": cmd,
        "doc": {"path": "", "fullName": "", "name": doc_name, "saved": True,
                "readOnly": False, "active": True, "count": 0},
        "result": result,
        "warnings": [],
        "undoToken": None,
        "elapsedMs": 1,
        "app": {"progId": "fixture", "product": "offline fixture", "mappedBy": "fixture",
                "version": "0", "hwnd": 0, "found": True, "launched": False, "pid": 0,
                "name": "fixture", "fullName": "fixture"},
    }


# --------------------------------------------------------------------------- clean / strict


def clean_fixture():
    """A tiny well-formed drawing: the audit must report zero issues."""
    info = {
        "units": {"insunits": 4, "dimscale": 1, "ltscale": 1, "textsize": 2.5,
                  "measurement": 1, "annoallvisible": 1},
        "extents": {"min": [0.0, 0.0, 0.0], "max": [100.0, 50.0, 0.0]},
        "layers": [
            {"name": "0", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "BODY", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "ANNO", "color": 3, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
        ],
        "textStyles": [{"name": "Standard", "font": "gbcbig.shx", "height": 0.0}],
        "counts": {"model": 0, "paper": 0, "blocks": 2, "layouts": 1, "documents": 1},
    }
    entities = []
    # one closed rectangle + one normal line
    entities.append({"handle": "100", "type": "AcDbPolyline", "kind": "LWPOLYLINE",
                     "layer": "BODY", "space": "model", "closed": True,
                     "vertices": [[0, 0], [100, 0], [100, 50], [0, 50]],
                     "points": [[0, 0], [100, 0], [100, 50], [0, 50]],
                     "bbox": {"min": [0, 0], "max": [100, 50]}})
    entities.append({"handle": "101", "type": "AcDbLine", "kind": "LINE", "layer": "BODY",
                     "space": "model", "start": [0, 25], "end": [100, 25],
                     "points": [[0, 25], [100, 25]], "linetype": "ByLayer",
                     "bbox": {"min": [0, 25], "max": [100, 25]}})
    # 12 evenly spaced labels, same height, no overlap
    for i in range(12):
        x = 4.0 + i * 8.0
        entities.append({"handle": "%03X" % (0x200 + i), "type": "AcDbText", "kind": "TEXT",
                         "layer": "ANNO", "space": "model", "text": "L%d" % (i + 1),
                         "insertion": [x, 30.0], "pos": [x, 30.0], "height": 2.5,
                         "style": "Standard"})
    info["counts"]["model"] = len(entities)
    return info, entities


def strict_fixture():
    """Layer/style edge cases that a strict site config should catch."""
    info = {
        "units": {"insunits": 4, "dimscale": 100, "ltscale": 100, "textsize": 2.5,
                  "measurement": 1, "annoallvisible": 1},
        "extents": {"min": [0.0, 0.0, 0.0], "max": [50.0, 50.0, 0.0]},
        "layers": [
            {"name": "0", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "水系", "color": 5, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "WALL", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "wall", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
            {"name": "has space", "color": 7, "frozen": False, "locked": False, "linetype": "CONTINUOUS"},
        ],
        "textStyles": [{"name": "Standard", "font": "gbcbig.shx", "height": 0.0}],
        "counts": {"model": 2, "paper": 0, "blocks": 2, "layouts": 1, "documents": 1},
    }
    entities = [
        {"handle": "300", "type": "AcDbLine", "kind": "LINE", "layer": "水系",
         "space": "model", "start": [0, 0], "end": [10, 0], "points": [[0, 0], [10, 0]],
         "linetype": "ByLayer", "bbox": {"min": [0, 0], "max": [10, 0]}},
        {"handle": "301", "type": "AcDbText", "kind": "TEXT", "layer": "GHOST-LAYER",
         "space": "model", "text": "hello", "insertion": [1, 1], "pos": [1, 1],
         "height": 2.5, "style": "MISSING-STYLE"},
    ]
    return info, entities


# --------------------------------------------------------------------------- main


def write_json(path, payload):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
        fh.write("\n")


def main():
    ensure_dir(FIXTURES)
    dxf_path = os.path.join(FIXTURES, "problem-rich.dxf")
    doc = build_problem_dxf(dxf_path)
    entities = export_entities(doc)
    info = export_info(doc, len(entities))
    # ezdxf rewrites $EXTMIN/$EXTMAX to its sentinel values on save; the drawing's
    # *intended* extents are the tight box, so the fixture info carries that box and
    # the audit can demonstrate the outside_extents rule.
    info["extents"] = {"min": [0.0, 0.0, 0.0], "max": [100.0, 60.0, 0.0]}
    write_json(os.path.join(FIXTURES, "problem-rich.info.json"), wrap("info", info, "problem-rich.dxf"))
    read = {"entities": entities,
            "counts": {"model": len(entities), "paper": 0, "returned": len(entities)},
            "readPath": "fixture", "scope": "model", "truncated": False, "matchedBy": "new"}
    write_json(os.path.join(FIXTURES, "problem-rich.read.json"), wrap("read", read, "problem-rich.dxf"))

    cinfo, centities = clean_fixture()
    write_json(os.path.join(FIXTURES, "clean.info.json"), wrap("info", cinfo, "clean.dxf"))
    write_json(os.path.join(FIXTURES, "clean.read.json"),
               wrap("read", {"entities": centities,
                             "counts": {"model": len(centities), "paper": 0,
                                        "returned": len(centities)},
                             "readPath": "fixture", "scope": "model",
                             "truncated": False, "matchedBy": "new"}, "clean.dxf"))

    sinfo, sentities = strict_fixture()
    write_json(os.path.join(FIXTURES, "strict.info.json"), wrap("info", sinfo, "strict.dxf"))
    write_json(os.path.join(FIXTURES, "strict.read.json"),
               wrap("read", {"entities": sentities,
                             "counts": {"model": len(sentities), "paper": 0,
                                        "returned": len(sentities)},
                             "readPath": "fixture", "scope": "model",
                             "truncated": False, "matchedBy": "new"}, "strict.dxf"))

    expected = {
        "problem-rich": {
            "min_hits": {
                "units_missing": 1,
                "layer_off_but_used": 1,
                "duplicate_entity": 3,
                "zero_length_line": 1,
                "unclosed_polyline": 1,
                "linetype_missing": 1,
                "linetype_scale": 1,
                "dim_scale_invalid": 1,
                "text_empty": 1,
                "text_overlap": 1,
                "outside_extents": 1,
                "text_height_outlier": 2,
                "layer_empty": 3,
                "layer_naming": 2,
            },
            "must_skip": ["dim_no_measurement"],
            "blockers_min": 1,
        },
        "clean": {"max_hits": 0, "blockers_max": 0},
        "strict": {"min_hits": {"layer_naming": 2, "layer_unknown": 1,
                                "text_style_missing": 1}},
    }
    write_json(os.path.join(FIXTURES, "expected-hits.json"), expected)

    print("wrote fixtures into", FIXTURES)
    print("  problem-rich.dxf        %d entities" % len(entities))
    print("  problem-rich.info.json  %d layers" % len(info["layers"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
