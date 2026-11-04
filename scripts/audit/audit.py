#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""audit.py - drawing health audit rule engine for zwcad-cooperate.

READ-ONLY LAYER. This tool never talks to CAD/COM. It consumes the JSON that
scripts/zwc.ps1 already produces and turns it into a problem list:

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\zwc.ps1 info -ProgId X > info.json
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\zwc.ps1 read -ProgId X -Max 20000 > read.json
    python scripts\\audit\\audit.py --info info.json --read read.json --md report.md --json report.json

Usage:
    audit.py --read read.json [--info info.json] [--rules r1,r2] [--config c.json]
             [--json out.json] [--md out.md] [--exit-code] [--list-rules] [--quiet]

Design notes
------------
* Everything is tolerant: a missing info file downgrades the drawing-level rules
  to "not_measured" instead of crashing, unknown entity kinds are ignored by the
  rules that need geometry (and never break the run).
* Every skipped check is reported in ``skipped[]`` with reason ``not_measured``
  or ``no_input`` so the report never silently looks greener than it is.
* Chinese text lives in the .json/.md payload only; stdout is kept ASCII so a
  PowerShell 5.1 console (GBK code page) never mangles it.

Exit codes: 0 = analysis done (blockers only fail the run with --exit-code),
3 = blockers found and --exit-code was given, 2 = bad input/usage, 1 = internal.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import os
import re
import sys
import unicodedata
from collections import Counter, OrderedDict, defaultdict

TOOL = "zwcad-cooperate/audit"
SCHEMA = "zwcad-audit/1"
SEVERITIES = ("blocker", "major", "minor")
SEV_RANK = {"blocker": 0, "major": 1, "minor": 2}

TEXT_KINDS = ("TEXT", "MTEXT")
POLY_KINDS = ("LWPOLYLINE", "POLYLINE")

# --------------------------------------------------------------------------- rules
# id -> (default severity, short ascii description used by --list-rules)
RULE_SPECS = OrderedDict()

def _rule(rule_id, severity, desc):
    RULE_SPECS[rule_id] = (severity, desc)


_rule("layer_off_but_used", "blocker", "frozen/off layer still carries entities")
_rule("units_missing", "major", "INSUNITS is 0 / undefined")
_rule("duplicate_entity", "major", "identical geometry repeated")
_rule("zero_length_line", "major", "line or polyline with zero length")
_rule("unclosed_polyline", "major", "polyline that almost closes but is not closed")
_rule("linetype_missing", "major", "entity/layer linetype not loaded")
_rule("linetype_scale", "major", "LTSCALE is 0 or absurd")
_rule("text_empty", "major", "empty text entity")
_rule("text_overlap", "major", "text boxes overlap on the same layer")
_rule("outside_extents", "major", "entity outside EXTMIN/EXTMAX")
_rule("dim_no_measurement", "major", "dimension without a readable measurement")
_rule("dim_scale_invalid", "minor", "DIMSCALE is 0 or negative")
_rule("text_height_outlier", "minor", "text height far from its layer median")
_rule("layer_empty", "minor", "layer without any entity")
_rule("layer_naming", "minor", "layer name outside the naming convention")
_rule("layer_unknown", "minor", "entity refers to a layer missing from info")
_rule("text_style_missing", "minor", "text refers to an unloaded text style")

DEFAULT_CONFIG = {
    # global
    "max_issues_per_rule": 200,
    "rules_disabled": [],
    "severity": {rule_id: sev for rule_id, (sev, _d) in RULE_SPECS.items()},

    "layer_empty": {
        "ignore": ["0", "Defpoints", "DEFPOINTS"],
        "ignore_prefix": ["$0$", "*"],
    },    "layer_naming": {
        "max_len": 31,
        "allow_cjk": True,          # site convention: Chinese layer names are normal here
        "forbid_space": True,
        "forbidden_chars": '<>/\\":;?*|=`,',
        "exempt": ["0", "Defpoints"],
    },
    "layer_off_but_used": {
        "report_entities_per_layer": 3,
    },
    "linetype_missing": {
        "loaded": None,             # explicit list; info has no linetypes array today
        "heuristic": True,          # when loaded is None, fall back to a standard-name table
        "standard": [
            "BYLAYER", "BYBLOCK", "CONTINUOUS",
            "DASHED", "DASHED2", "DASHEDX2", "HIDDEN", "HIDDEN2", "HIDDENX2",
            "CENTER", "CENTER2", "CENTERX2", "DASHDOT", "DASHDOT2", "DASHDOTX2",
            "DOT", "DOT2", "DOTX2", "BORDER", "BORDER2", "BORDERX2",
            "DIVIDE", "DIVIDE2", "DIVIDEX2", "PHANTOM", "PHANTOM2", "PHANTOMX2",
            "ACAD_ISO02W100", "ACAD_ISO03W100", "ACAD_ISO04W100",
            "ACAD_ISO05W100", "ACAD_ISO06W100", "ACAD_ISO07W100",
            "ACAD_ISO08W100", "ACAD_ISO09W100", "ACAD_ISO10W100",
            "ACAD_ISO11W100", "ACAD_ISO12W100", "ACAD_ISO13W100",
            "ACAD_ISO14W100", "ACAD_ISO15W100",
        ],
    },
    "linetype_scale": {"min": 1e-6, "max": 1000.0},
    "text_height_outlier": {
        "ratio_low": 0.4,
        "ratio_high": 3.0,
        "min_samples_global": 10,
        "min_samples_per_layer": 8,
        # A drawing's title / 图名 is normally the single largest text and is not a defect.
        # The N largest distinct text heights are exempt from the HIGH side only, up to
        # title_max_ratio; above that a size is treated as a real outlier.
        "title_skip_largest": 1,
        "title_max_ratio": 8.0,
    },
    "text_overlap": {
        "overlap_ratio": 0.30,      # of the smaller box
        "major_ratio": 0.60,
        "cjk_width": 1.0,
        "latin_width": 0.62,
        "max_pairs": 400000,
    },
    "text_empty": {},
    "zero_length_line": {"tol": 1e-6},
    "duplicate_entity": {"tol": 1e-6},
    "unclosed_polyline": {
        "tol": 1e-6,
        "mode": "near_miss",        # near_miss (default, low FP) | any
        "max_gap_ratio": 0.02,      # gap / open-polyline length
        "min_perimeter": 0.0,
    },
    "outside_extents": {
        "tol_ratio": 0.02,          # tolerated overshoot as a ratio of the extents diagonal
        "far_factor": 2.0,          # beyond 2x the extents size from the centre -> major
        "flood_threshold": 20,      # more hits than this -> one aggregate issue instead
        "fallback": "auto",         # auto = when EXTMIN/EXTMAX is stale, use 1%/99% percentiles
    },
    "dim_no_measurement": {
        "measurement_keys": ["measurement", "value", "dimensionValue", "measurementValue"],
    },
    "text_style_missing": {},
    "layer_unknown": {},
}

# --------------------------------------------------------------------------- small utils


def deep_merge(base, override):
    out = dict(base)
    for k, v in (override or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def load_json(path):
    """Load a JSON file written by zwc.ps1 (UTF-8, may carry a BOM)."""
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    text = raw.decode("utf-8", errors="replace")
    if not text.strip():
        raise ValueError("empty file: " + path)
    return json.loads(text)


def unwrap(obj):
    """Accept a full zwc response ({ok,result,...}) or a bare result object."""
    if not isinstance(obj, dict):
        return {}, {}
    meta = {}
    for key in ("cmd", "app", "doc", "warnings", "elapsedMs", "wallMs", "undoToken"):
        if key in obj:
            meta[key] = obj[key]
    if obj.get("ok") is False:
        err = obj.get("error") or {}
        raise ValueError("zwc response says ok:false (%s: %s)"
                         % (err.get("code"), err.get("message")))
    result = obj.get("result")
    if isinstance(result, dict):
        return result, meta
    return obj, meta


def _as_float(v):
    if isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        if isinstance(v, float) and (math.isnan(v) or math.isinf(v)):
            return None
        return float(v)
    if isinstance(v, str):
        try:
            return float(v.strip())
        except ValueError:
            return None
    return None


def _as_xy(v):
    if v is None:
        return None
    if isinstance(v, (list, tuple)) and len(v) >= 2:
        x, y = _as_float(v[0]), _as_float(v[1])
        if x is None or y is None:
            return None
        return (x, y)
    if isinstance(v, dict):
        return _as_xy([v.get("x"), v.get("y")])
    if isinstance(v, str):
        parts = v.replace(",", " ").split()
        if len(parts) >= 2:
            x, y = _as_float(parts[0]), _as_float(parts[1])
            if x is not None and y is not None:
                return (x, y)
    return None


def points_of(entity):
    """List of (x, y) for any entity shape that carries vertices."""
    for key in ("points", "vertices", "coords", "coordinates"):
        raw = entity.get(key)
        if isinstance(raw, list) and raw:
            pts = [_as_xy(p) for p in raw]
            pts = [p for p in pts if p is not None]
            if pts:
                return pts
    return []


def bbox_of_raw(entity):
    bb = entity.get("bbox")
    if isinstance(bb, dict):
        mn, mx = _as_xy(bb.get("min")), _as_xy(bb.get("max"))
        if mn and mx:
            return (mn[0], mn[1], mx[0], mx[1])
    if isinstance(bb, list) and len(bb) == 4:
        vals = [_as_float(v) for v in bb]
        if all(v is not None for v in vals):
            return tuple(vals)  # type: ignore[return-value]
    return None


def bbox_from_points(pts):
    if not pts:
        return None
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def bbox_union(a, b):
    if a is None:
        return b
    if b is None:
        return a
    return (min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3]))


def bbox_area(b):
    if b is None:
        return 0.0
    return max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])


def bbox_overlap(a, b):
    """Intersection area of two axis-aligned boxes."""
    if a is None or b is None:
        return 0.0
    w = min(a[2], b[2]) - max(a[0], b[0])
    h = min(a[3], b[3]) - max(a[1], b[1])
    if w <= 0 or h <= 0:
        return 0.0
    return w * h


def fmt_num(v):
    if v is None:
        return "?"
    if abs(v) >= 1e5 or (v != 0 and abs(v) < 1e-4):
        return "%.6g" % v
    return ("%.6f" % v).rstrip("0").rstrip(".")


def fmt_pt(p):
    if p is None:
        return "?"
    return "%s,%s" % (fmt_num(p[0]), fmt_num(p[1]))


def shorten(text, limit=160):
    if text is None:
        return ""
    text = str(text)
    if len(text) <= limit:
        return text
    return text[: limit - 1] + "..."


_MT_FORMAT = re.compile(r"\\[A-Za-z][^;\\]*;")
_MT_BRACE = re.compile(r"[{}]")


def clean_text(raw):
    """Strip MTEXT formatting so that width estimation and emptiness work."""
    if raw is None:
        return ""
    text = str(raw)
    text = text.replace("\\P", "\n").replace("\\p", "\n")
    text = text.replace("\\~", " ")
    text = _MT_FORMAT.sub("", text)
    text = _MT_BRACE.sub("", text)
    text = text.replace("\\", "")
    return text


def text_lines(raw):
    cleaned = clean_text(raw)
    if cleaned == "":
        return []
    return cleaned.split("\n")


def char_width(ch, cfg):
    if ch in (" ", "\t"):
        return cfg["text_overlap"]["latin_width"] * 0.8
    if unicodedata.east_asian_width(ch) in ("W", "F"):
        return cfg["text_overlap"]["cjk_width"]
    return cfg["text_overlap"]["latin_width"]


def text_box(raw, insertion, height, cfg):
    """Approximate the text box: insertion point is the baseline-left corner."""
    if insertion is None or not height or height <= 0:
        return None
    lines = text_lines(raw) or [""]
    widest = 0.0
    for line in lines:
        widest = max(widest, sum(char_width(ch, cfg) for ch in line) * height)
    if widest <= 0:
        widest = height * 0.5  # unknown glyphs: still reserve something
    box_h = max(1, len(lines)) * height * 1.2
    x, y = insertion
    return (x, y, x + widest, y + box_h)


# --------------------------------------------------------------------------- entities


class Entity(object):
    __slots__ = ("handle", "kind", "type", "layer", "space", "linetype", "bbox",
                 "text", "height", "height_src", "style", "insertion", "points",
                 "closed", "radius", "center", "raw")

    def __init__(self, raw):
        self.raw = raw
        self.handle = str(raw.get("handle") or "")
        self.type = str(raw.get("type") or "")
        self.kind = str(raw.get("kind") or self.type or "UNKNOWN").upper()
        self.layer = str(raw.get("layer") or "")
        self.space = str(raw.get("space") or "model")
        self.linetype = raw.get("linetype")
        self.bbox = bbox_of_raw(raw)
        self.text = raw.get("text")
        self.height = _as_float(raw.get("height"))
        self.height_src = "entity" if self.height and self.height > 0 else None
        self.style = raw.get("style")
        self.insertion = _as_xy(raw.get("insertion")) or _as_xy(raw.get("pos"))
        self.points = points_of(raw)
        closed = raw.get("closed")
        self.closed = bool(closed) if closed is not None else None
        self.radius = _as_float(raw.get("radius"))
        self.center = _as_xy(raw.get("center"))

    # ---- lazily derived geometry
    def ensure_geometry(self, style_heights, cfg):
        if self.kind in TEXT_KINDS:
            if self.bbox is None:
                h = self.effective_height(style_heights)
                self.bbox = text_box(self.text, self.insertion, h, cfg)
            return
        if self.bbox is not None:
            return
        if self.kind == "LINE":
            pts = self.line_points()
            self.bbox = bbox_from_points(pts)
        elif self.kind in POLY_KINDS:
            self.bbox = bbox_from_points(self.points)
        elif self.kind in ("CIRCLE", "ARC") and self.center and self.radius:
            r = abs(self.radius)
            self.bbox = (self.center[0] - r, self.center[1] - r,
                         self.center[0] + r, self.center[1] + r)
        elif self.points:
            self.bbox = bbox_from_points(self.points)

    def effective_height(self, style_heights):
        if self.height and self.height > 0:
            return self.height
        h = style_heights.get((self.style or "").upper())
        if h and h > 0:
            self.height = h
            self.height_src = "style"
            return h
        return 0.0

    def line_points(self):
        if len(self.points) >= 2:
            return [self.points[0], self.points[-1]]
        a = _as_xy(self.raw.get("start"))
        b = _as_xy(self.raw.get("end"))
        if a and b:
            return [a, b]
        return []

    def is_dimension(self):
        return "DIM" in self.kind or "DIMENSION" in self.type.upper()


def norm_entities(raw_entities, style_heights, cfg):
    out = []
    for raw in raw_entities or []:
        if not isinstance(raw, dict):
            continue
        ent = Entity(raw)
        ent.ensure_geometry(style_heights, cfg)
        out.append(ent)
    return out


# --------------------------------------------------------------------------- context


class Audit(object):
    def __init__(self, info, read, config):
        self.cfg = config
        self.info = info or {}
        self.read = read or {}
        self.raw_entities = [e for e in (self.read.get("entities") or []) if isinstance(e, dict)]
        self.text_styles = self.info.get("textStyles") or []
        self.style_heights = {}
        for st in self.text_styles:
            if not isinstance(st, dict):
                continue
            name = str(st.get("name") or "").upper()
            h = _as_float(st.get("height"))
            if name and h is not None:
                self.style_heights[name] = h
        self.entities = norm_entities(self.raw_entities, self.style_heights, self.cfg)
        self.layers = [l for l in (self.info.get("layers") or []) if isinstance(l, dict)]
        self.layer_by_name = {}
        for layer in self.layers:
            name = layer.get("name")
            if name:
                self.layer_by_name[str(name)] = layer
        self.issues = []
        self.counts = Counter()
        self.suppressed = Counter()
        self.skipped = []
        self.warnings = []
        self.enabled = self._enabled_rules()
        self.max_per_rule = max(1, int(self.cfg.get("max_issues_per_rule") or 200))

    # ---- config plumbing
    def _enabled_rules(self):
        disabled = set(self.cfg.get("rules_disabled") or [])
        return [r for r in RULE_SPECS if r not in disabled]

    def severity(self, rule_id):
        sev = (self.cfg.get("severity") or {}).get(rule_id) or RULE_SPECS.get(rule_id, ("minor",))[0]
        return sev if sev in SEVERITIES else "minor"

    def warn(self, code, message):
        self.warnings.append({"code": code, "message": message})

    def skip(self, rule_id, reason, detail=""):
        if rule_id in self.enabled:
            self.skipped.append({"rule": rule_id, "reason": reason, "detail": detail})

    def add(self, rule_id, sample, suggest, handle=None, layer=None, space=None,
            kind=None, severity=None):
        if rule_id not in self.enabled:
            return False
        if self.counts[rule_id] >= self.max_per_rule:
            self.suppressed[rule_id] += 1
            self.counts[rule_id] += 1
            return False
        self.counts[rule_id] += 1
        sev = severity or self.severity(rule_id)
        if sev not in SEVERITIES:
            sev = self.severity(rule_id)
        self.issues.append(OrderedDict([
            ("rule", rule_id),
            ("severity", sev),
            ("handle", handle),
            ("layer", layer),
            ("space", space),
            ("kind", kind),
            ("sample", shorten(sample, 300)),
            ("suggest", suggest),
        ]))
        return True

    # ---- shortcuts
    @property
    def units(self):
        u = self.info.get("units")
        return u if isinstance(u, dict) else {}

    def cfg_for(self, rule_id):
        section = self.cfg.get(rule_id)
        return section if isinstance(section, dict) else {}


# --------------------------------------------------------------------------- rules


def r_units_missing(a):
    units = a.units
    if not a.info.get("units"):
        a.skip("units_missing", "no_input", "info 未提供 units 段（没有 --info 或该 CAD 读不到 INSUNITS）")
        return
    ins = _as_float(units.get("insunits"))
    if ins is None or ins == 0:
        a.add("units_missing",
              "INSUNITS=%s（图纸未定义单位）" % ("缺失" if ins is None else "0"),
              "用 UNITS 命令设置单位（4=毫米、5=厘米、6=米），否则导入/外部参照与出图比例无依据；"
              "本院单比例图纸常见 INSUNITS=0，若要当门禁请把 config.severity.units_missing 改成 blocker",
              handle=None, layer=None)
    elif ins < 0 or ins > 20:
        a.add("units_missing", "INSUNITS=%s 不在 1~20 的标准取值内" % fmt_num(ins),
              "检查 INSUNITS 是否被异常值污染", handle=None, layer=None,
              severity="minor")


def r_layer_empty(a):
    if not a.layers:
        a.skip("layer_empty", "no_input", "info 未提供 layers 段")
        return
    counts = a.info.get("counts") or {}
    paper = _as_float(counts.get("paper"))
    if paper and paper > 0:
        a.warn("PARTIAL_SCOPE",
               "info 报告图纸空间有 %d 个实体，而 read 只读模型空间："
               "只在布局里用到的图层会被误判为空" % int(paper))
    cfg = a.cfg_for("layer_empty")
    ignore = {str(x).upper() for x in (cfg.get("ignore") or [])}
    prefixes = tuple(str(x) for x in (cfg.get("ignore_prefix") or []))
    used = Counter(e.layer for e in a.entities)
    empty = []
    for layer in a.layers:
        name = str(layer.get("name") or "")
        if not name or name.upper() in ignore:
            continue
        if prefixes and name.startswith(prefixes):
            continue
        if used.get(name):
            continue
        empty.append(name)
    if empty:
        blocks = _as_float(counts.get("blocks"))
        if blocks and blocks > 2:
            a.warn("BLOCKS_NOT_READ",
                   "read 只覆盖模型空间，info 报告 %d 个块定义：只被块参照使用的图层会被误判为空图层"
                   "（本报告的空图层命中请先核对块内使用情况）" % int(blocks))
        for name in empty:
            a.add("layer_empty", "图层 %s 在 read 范围内没有任何实体" % name,
                  "删除无用图层；若该层只放图框/外部参照/块内实体，可在 config.layer_empty.ignore 里豁免",
                  layer=name)


def r_layer_naming(a):
    if not a.layers:
        a.skip("layer_naming", "no_input", "info 未提供 layers 段")
        return
    cfg = a.cfg_for("layer_naming")
    max_len = int(cfg.get("max_len") or 31)
    allow_cjk = bool(cfg.get("allow_cjk"))
    forbid_space = bool(cfg.get("forbid_space"))
    forbidden = str(cfg.get("forbidden_chars") or "")
    exempt = {str(x).upper() for x in (cfg.get("exempt") or [])}
    seen = {}
    for layer in a.layers:
        name = str(layer.get("name") or "")
        if not name or name.upper() in exempt:
            continue
        problems = []
        if forbid_space and any(ch.isspace() for ch in name):
            problems.append("含空格")
        if forbidden:
            hits = sorted({ch for ch in name if ch in forbidden})
            if hits:
                problems.append("含非法字符 %s" % "".join(hits))
        if not allow_cjk and any(ord(ch) > 127 for ch in name):
            problems.append("含非 ASCII 字符")
        if len(name) > max_len:
            problems.append("名称长度 %d > %d" % (len(name), max_len))
        key = name.upper()
        if key in seen:
            problems.append("与图层 %s 仅大小写不同" % seen[key])
        else:
            seen[key] = name
        if problems:
            a.add("layer_naming", "图层名 %s：%s" % (name, "、".join(problems)),
                  "统一图层命名规范（ASCII、无空格、<=%d 字符）；院内中文图层名可置 "
                  "layer_naming.allow_cjk=true 豁免" % max_len,
                  layer=name)


def r_layer_off_but_used(a):
    if not a.layers:
        a.skip("layer_off_but_used", "no_input", "info 未提供 layers 段")
        return
    cfg = a.cfg_for("layer_off_but_used")
    show = max(1, int(cfg.get("report_entities_per_layer") or 3))
    frozen_layers = {}
    for layer in a.layers:
        name = str(layer.get("name") or "")
        if not name:
            continue
        frozen = layer.get("frozen")
        if frozen is True:
            frozen_layers[name] = []
    if not frozen_layers:
        return
    for ent in a.entities:
        if ent.layer in frozen_layers:
            frozen_layers[ent.layer].append(ent)
    for name, ents in frozen_layers.items():
        if not ents:
            continue
        handles = ", ".join(e.handle for e in ents[:show] if e.handle)
        more = "" if len(ents) <= show else " 等 %d 个" % len(ents)
        a.add("layer_off_but_used",
              "图层 %s 已关闭/冻结，但仍有 %d 个实体（handle: %s%s）"
              % (name, len(ents), handles, more),
              "解冻该层或把实体移到可见层：冻结层上的内容不会被打印，交付时等于丢失",
              handle=ents[0].handle, layer=name, space=ents[0].space, kind=ents[0].kind)


def r_linetype_missing(a):
    cfg = a.cfg_for("linetype_missing")
    loaded_raw = a.info.get("linetypes")
    loaded = None
    if isinstance(loaded_raw, list) and loaded_raw:
        loaded = set()
        for item in loaded_raw:
            if isinstance(item, dict):
                item = item.get("name")
            if item:
                loaded.add(str(item).upper())
    elif isinstance(cfg.get("loaded"), list) and cfg.get("loaded"):
        loaded = {str(x).upper() for x in cfg["loaded"]}
    heuristic = bool(cfg.get("heuristic", True))
    if loaded is None and not heuristic:
        a.skip("linetype_missing", "not_measured",
               "info 不提供 linetypes 列表（本机 helper 的 info 没有该字段），且未配置 linetype_missing.loaded："
               "无法判定线型是否已加载")
        return
    skip_names = {"", "BYLAYER", "BYBLOCK", "CONTINUOUS"}
    standard = {str(x).upper() for x in (cfg.get("standard") or [])}
    layer_lt = {}
    for layer in a.layers:
        name = str(layer.get("name") or "")
        lt = str(layer.get("linetype") or "").upper()
        if name and lt:
            layer_lt[name] = lt

    refs = defaultdict(list)          # linetype -> [handles]
    layer_refs = defaultdict(list)    # linetype -> [layer names]
    for name, lt in layer_lt.items():
        if lt not in skip_names:
            layer_refs[lt].append(name)
    for ent in a.entities:
        lt = str(ent.linetype or "").upper()
        if lt in skip_names or lt == "BYLAYER":
            lt = layer_lt.get(ent.layer, "")
        if lt in skip_names:
            continue
        refs[lt].append(ent.handle)

    if loaded is None:
        a.warn("LINETYPE_SET_UNKNOWN",
               "info 未返回已加载线型表，linetype_missing 退化为标准线型名启发式判断"
               "（自定义线型可能被误报，可用 --config 覆盖 linetype_missing.standard 或 loaded）")
    bad = []
    for lt in sorted(refs):
        handles = [h for h in refs[lt] if h]
        if loaded is not None:
            if lt in loaded:
                continue
            bad.append((lt, handles, "major"))
        else:
            if lt in standard:
                continue
            bad.append((lt, handles, "minor"))
    for lt in sorted(layer_refs):
        if loaded is not None:
            if lt in loaded:
                continue
            bad.append((lt, [], "major"))
        elif lt not in standard:
            bad.append((lt, [], "minor"))
    if not bad:
        return
    if loaded is None:
        for lt, handles, sev in bad:
            where = ("图层 " + "、".join(layer_refs.get(lt, []))) if layer_refs.get(lt) else ""
            used = ("%d 个实体使用" % len(handles)) if handles else where
            a.add("linetype_missing",
                  "线型 %s 不在标准线型表中，且无法确认已加载（%s）" % (lt, used),
                  "确认该线型已在 LINETYPE 中加载；自定义线型请加入 config.linetype_missing.standard",
                  handle=(handles[0] if handles else None),
                  layer=(layer_refs.get(lt) or [None])[0], severity=sev)
    else:
        for lt, handles, sev in bad:
            where = ("图层 " + "、".join(layer_refs.get(lt, []))) if layer_refs.get(lt) else ""
            used = ("%d 个实体使用" % len(handles)) if handles else where
            a.add("linetype_missing",
                  "线型 %s 未加载却被引用（%s），实际显示会退回 CONTINUOUS" % (lt, used),
                  "用 LINETYPE 加载该线型，或把实体/图层线型改成 BYLAYER/CONTINUOUS",
                  handle=(handles[0] if handles else None),
                  layer=(layer_refs.get(lt) or [None])[0], severity=sev)


def r_linetype_scale(a):
    units = a.units
    if not units:
        a.skip("linetype_scale", "no_input", "info 未提供 units 段")
        return
    cfg = a.cfg_for("linetype_scale")
    lo = _as_float(cfg.get("min")) or 1e-6
    hi = _as_float(cfg.get("max")) or 1000.0
    lts = _as_float(units.get("ltscale"))
    if lts is None:
        a.skip("linetype_scale", "not_measured", "info.units 里没有 ltscale")
        return
    if lts <= 0:
        a.add("linetype_scale", "LTSCALE=%s（全局线型比例无效，虚线会退化成实线）" % fmt_num(lts),
              "设 LTSCALE 为出图比例相关值（如 1:1000 用 10~100），或按图幅统一取值")
    elif lts < lo or lts > hi:
        a.add("linetype_scale", "LTSCALE=%s 超出合理区间 [%s, %s]" % (fmt_num(lts), fmt_num(lo), fmt_num(hi)),
              "核对图纸比例与 LTSCALE 是否匹配（1:1000 图纸常见 10~100）")


def r_dim_scale_invalid(a):
    units = a.units
    if not units:
        a.skip("dim_scale_invalid", "no_input", "info 未提供 units 段")
        return
    dim = _as_float(units.get("dimscale"))
    if dim is None:
        a.skip("dim_scale_invalid", "not_measured", "info.units 里没有 dimscale")
        return
    if dim <= 0:
        a.add("dim_scale_invalid", "DIMSCALE=%s（标注整体比例为 0/负值，非注释性标注会失真）" % fmt_num(dim),
              "非注释性图纸请把 DIMSTYLE 的 Overall scale 设为出图比例（1:1000 用 1000）；"
              "注释性（annotative）图纸 DIMSCALE=0 是合法写法，本项默认只给 minor")


def r_text_height_outlier(a):
    cfg = a.cfg_for("text_height_outlier")
    low = _as_float(cfg.get("ratio_low")) or 0.4
    high = _as_float(cfg.get("ratio_high")) or 3.0
    min_global = int(cfg.get("min_samples_global") or 10)
    min_layer = int(cfg.get("min_samples_per_layer") or 8)

    texts = []
    unknown = 0
    for ent in a.entities:
        if ent.kind not in TEXT_KINDS:
            continue
        h = ent.effective_height(a.style_heights)
        if h <= 0:
            unknown += 1
            continue
        texts.append((ent, h))
    if unknown:
        a.warn("TEXT_HEIGHT_UNKNOWN",
               "%d 个文字取不到有效字高（height=0 且文字样式高度为 0），已排除在字高统计外" % unknown)
    if len(texts) < min_global:
        a.skip("text_height_outlier", "not_measured",
               "可量测字高的文字只有 %d 个（< %d），中位数不可信，跳过字高离群检查"
               % (len(texts), min_global))
        return

    def median(vals):
        s = sorted(vals)
        n = len(s)
        if n == 0:
            return 0.0
        mid = n // 2
        return s[mid] if n % 2 else (s[mid - 1] + s[mid]) / 2.0

    global_med = median([h for _e, h in texts])
    by_layer = defaultdict(list)
    for ent, h in texts:
        by_layer[ent.layer].append(h)
    layer_med = {name: median(vals) for name, vals in by_layer.items() if len(vals) >= min_layer}

    # 图名/标题 exemption (high side only): the N largest distinct heights are treated as
    # intentional title sizes unless they are absurdly large.
    title_skip = int(cfg.get("title_skip_largest") or 0)
    title_max = _as_float(cfg.get("title_max_ratio"))
    if title_max is None:
        title_max = 8.0
    exempt = set()
    if title_skip > 0:
        distinct = []
        for _ent, h in sorted(texts, key=lambda t: t[1], reverse=True):
            if not distinct or h < distinct[-1]:
                distinct.append(h)
            if len(distinct) >= title_skip:
                break
        if len(distinct) >= title_skip:
            cutoff = distinct[title_skip - 1]
            exempt = {id(ent) for ent, h in texts if h >= cutoff}
    exempted = 0
    for ent, h in texts:
        med = layer_med.get(ent.layer, global_med)
        if med <= 0:
            continue
        ratio = h / med
        if ratio > high and id(ent) in exempt and ratio <= title_max:
            exempted += 1
            continue
        if ratio < low or ratio > high:
            base = "本层中位数" if ent.layer in layer_med else "全图中位数"
            a.add("text_height_outlier",
                  "文字「%s」字高 %s，是%s %s 的 %.2f 倍"
                  % (shorten(clean_text(ent.text), 30), fmt_num(h), base, fmt_num(med), ratio),
                  "统一同层文字高度（常用 2.0/2.5/3.5 mm）；离群字高往往会让出图时字大小突兀",
                  handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)
    if exempted:
        a.warn("TEXT_TITLE_EXEMPTED",
               "%d 个字号被视为图名/标题（全图最大的 %d 个字号，<= 中位数 %.1f 倍），未按字高离群报告；"
               "可用 config.text_height_outlier.title_skip_largest 调整"
               % (exempted, title_skip, title_max))


def r_text_overlap(a):
    cfg = a.cfg_for("text_overlap")
    threshold = _as_float(cfg.get("overlap_ratio")) or 0.30
    major_ratio = _as_float(cfg.get("major_ratio")) or 0.60
    max_pairs = int(cfg.get("max_pairs") or 400000)

    groups = defaultdict(list)
    for ent in a.entities:
        if ent.kind not in TEXT_KINDS or ent.bbox is None:
            continue
        groups[(ent.layer, ent.space)].append(ent)
    pairs = 0
    truncated = False
    for (layer, space), items in groups.items():
        if len(items) < 2:
            continue
        items.sort(key=lambda e: (e.bbox[0], e.bbox[1]))
        for i in range(len(items)):
            bi = items[i].bbox
            for j in range(i + 1, len(items)):
                bj = items[j].bbox
                if bj[0] >= bi[2]:
                    break  # sorted by minx: no later box can overlap horizontally
                pairs += 1
                if pairs > max_pairs:
                    truncated = True
                    break
                area = bbox_overlap(bi, bj)
                if area <= 0:
                    continue
                smaller = min(bbox_area(bi), bbox_area(bj))
                if smaller <= 0:
                    continue
                ratio = area / smaller
                if ratio < threshold:
                    continue
                sev = "major" if ratio >= major_ratio else "minor"
                left, right = items[i], items[j]
                if (left.text == right.text and left.insertion == right.insertion
                        and left.height == right.height):
                    continue  # byte-identical text: duplicate_entity reports that, not this
                a.add("text_overlap",
                      "文字「%s」与「%s」重叠 %.0f%%（%s）"
                      % (shorten(clean_text(left.text), 24), shorten(clean_text(right.text), 24),
                         ratio * 100.0, layer),
                      "调整字高/位置或改用引线标注，避免出图压字",
                      handle=right.handle, layer=layer, space=space, kind=right.kind,
                      severity=sev)
            if truncated:
                break
        if truncated:
            break
    if truncated:
        a.warn("TEXT_OVERLAP_TRUNCATED",
               "文字重叠候选对超过 %d 对，已提前停止（结果可能不完整）" % max_pairs)


def r_text_empty(a):
    for ent in a.entities:
        if ent.kind not in TEXT_KINDS:
            continue
        if ent.text is None:
            continue
        if clean_text(ent.text).strip() == "":
            a.add("text_empty",
                  "文字实体内容为空%s" % ("（原始值：%r）" % str(ent.text) if str(ent.text).strip() else ""),
                  "删除空文字实体；空文字会留下不可见的定义点，后续用 QSELECT/统计时会虚增数量",
                  handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


def r_zero_length_line(a):
    tol = _as_float(a.cfg_for("zero_length_line").get("tol")) or 1e-6
    for ent in a.entities:
        if ent.kind == "LINE":
            pts = ent.line_points()
            if len(pts) == 2 and dist(pts[0], pts[1]) <= tol:
                a.add("zero_length_line",
                      "零长度直线（起点=终点 %s）" % fmt_pt(pts[0]),
                      "删除该实体：零长度线不参与出图，却会污染图元统计与后续选择集",
                      handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)
        elif ent.kind in POLY_KINDS and len(ent.points) >= 2:
            total = sum(dist(ent.points[k], ent.points[k + 1]) for k in range(len(ent.points) - 1))
            if total <= tol:
                a.add("zero_length_line",
                      "零长度多段线（%d 个顶点全部重合于 %s）" % (len(ent.points), fmt_pt(ent.points[0])),
                      "删除该实体：顶点全部重合，出图不可见",
                      handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


def _round_key(v, nd):
    if v is None:
        return None
    return round(float(v), nd)


def _geometry_key(ent, nd):
    """Stable signature used by duplicate_entity; None means 'not comparable'."""
    kind = ent.kind
    if kind == "LINE":
        pts = ent.line_points()
        if len(pts) != 2:
            return None
        a = (_round_key(pts[0][0], nd), _round_key(pts[0][1], nd))
        b = (_round_key(pts[1][0], nd), _round_key(pts[1][1], nd))
        pair = tuple(sorted((a, b)))
        return ("LINE", ent.layer, ent.space, pair)
    if kind in POLY_KINDS:
        if len(ent.points) < 2:
            return None
        pts = [(_round_key(p[0], nd), _round_key(p[1], nd)) for p in ent.points]
        fwd = tuple(pts)
        rev = tuple(reversed(pts))
        return ("POLY", ent.layer, ent.space, bool(ent.closed), min(fwd, rev))
    if kind in ("CIRCLE", "ARC"):
        if ent.center is None or ent.radius is None:
            return None
        return (kind, ent.layer, ent.space,
                (_round_key(ent.center[0], nd), _round_key(ent.center[1], nd),
                 _round_key(ent.radius, nd)))
    if kind in TEXT_KINDS:
        if ent.insertion is None:
            return None
        return ("TEXT", ent.layer, ent.space, ent.text,
                (_round_key(ent.insertion[0], nd), _round_key(ent.insertion[1], nd)),
                _round_key(ent.height, nd))
    if ent.bbox is not None:
        return ("BBOX", kind, ent.layer, ent.space,
                tuple(_round_key(v, nd) for v in ent.bbox))
    return None


def r_duplicate_entity(a):
    tol = _as_float(a.cfg_for("duplicate_entity").get("tol")) or 1e-6
    nd = max(0, int(math.ceil(-math.log10(tol)))) if tol < 1 else 0
    seen = {}
    groups = 0
    for ent in a.entities:
        key = _geometry_key(ent, nd)
        if key is None:
            continue
        first = seen.get(key)
        if first is None:
            seen[key] = ent
            continue
        groups += 1
        a.add("duplicate_entity",
              "%s 与 %s 几何完全重复（图层 %s，容差 %s）"
              % (ent.kind, first.handle, ent.layer, fmt_num(tol)),
              "删除重复实体（OVERKILL 命令可批量清理）：重复线会让打印叠色，重复文字会显得发黑",
              handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


def r_unclosed_polyline(a):
    cfg = a.cfg_for("unclosed_polyline")
    tol = _as_float(cfg.get("tol")) or 1e-6
    mode = str(cfg.get("mode") or "near_miss").lower()
    max_ratio = _as_float(cfg.get("max_gap_ratio"))
    if max_ratio is None:
        max_ratio = 0.02
    min_perimeter = _as_float(cfg.get("min_perimeter")) or 0.0
    for ent in a.entities:
        if ent.kind not in POLY_KINDS or ent.closed is True:
            continue
        if len(ent.points) < 3:
            continue
        pts = ent.points
        perimeter = sum(dist(pts[k], pts[k + 1]) for k in range(len(pts) - 1))
        gap = dist(pts[0], pts[-1])
        if gap <= tol:
            continue
        if perimeter < min_perimeter:
            continue
        ratio = gap / perimeter if perimeter > 0 else 1.0
        if mode == "any":
            a.add("unclosed_polyline",
                  "多段线未闭合（首末点相距 %s，周长 %s，缺口 %.1f%%）"
                  % (fmt_num(gap), fmt_num(perimeter), ratio * 100.0),
                  "用 PEDIT/Close 闭合，或确认它本来就不是闭合图形（mode=any 下会覆盖所有开放折线）",
                  handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind,
                  severity="minor")
            continue
        if ratio <= max_ratio:
            a.add("unclosed_polyline",
                  "多段线几乎闭合但首末点差 %s（周长 %s 的 %.2f%%，起点 %s 终点 %s）"
                  % (fmt_num(gap), fmt_num(perimeter), ratio * 100.0,
                     fmt_pt(pts[0]), fmt_pt(pts[-1])),
                  "用 PEDIT/Close 闭合：缺口很小说明是漏点闭合，出图时边界会缺一小段",
                  handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


def _percentile(values, p):
    if not values:
        return None
    vals = sorted(values)
    idx = int(round(p * (len(vals) - 1)))
    idx = max(0, min(len(vals) - 1, idx))
    return vals[idx]


def _robust_bounds(a, pad_ratio=0.10, low=0.05, high=0.95):
    """Fallback bounds for drawings whose EXTMIN/EXTMAX is stale (very common: CAD does
    not recalculate the stored extents on open, it returns whatever was last saved).
    Uses 5%/95% percentiles of every entity bbox corner so a couple of stray entities
    cannot define the whole drawing box."""
    boxes = [e.bbox for e in a.entities if e.space == "model" and e.bbox is not None]
    if len(boxes) < 5:
        return None
    xs = [b[0] for b in boxes] + [b[2] for b in boxes]
    ys = [b[1] for b in boxes] + [b[3] for b in boxes]
    mnx, mxx = _percentile(xs, low), _percentile(xs, high)
    mny, mxy = _percentile(ys, low), _percentile(ys, high)
    if mnx is None or mxx is None or mny is None or mxy is None:
        return None
    if mxx <= mnx or mxy <= mny:
        return None
    padx = pad_ratio * (mxx - mnx)
    pady = pad_ratio * (mxy - mny)
    return (mnx - padx, mny - pady, mxx + padx, mxy + pady)


def r_outside_extents(a):
    ext = a.info.get("extents")
    cfg = a.cfg_for("outside_extents")
    tol_ratio = _as_float(cfg.get("tol_ratio"))
    if tol_ratio is None:
        tol_ratio = 0.02
    far_factor = _as_float(cfg.get("far_factor")) or 2.0
    flood = int(cfg.get("flood_threshold") or 20)
    fallback = str(cfg.get("fallback") or "auto").lower()

    approx = False
    bounds = None
    if isinstance(ext, dict):
        mn = _as_xy(ext.get("min"))
        mx = _as_xy(ext.get("max"))
        if mn and mx and not (mx[0] <= mn[0] and mx[1] <= mn[1]):
            bounds = (mn[0], mn[1], mx[0], mx[1])
    if bounds is None:
        if not a.info.get("extents"):
            reason = "info 未提供 extents 段"
        else:
            reason = "EXTMIN/EXTMAX 缺失或退化（min>=max，CAD 打开后不重算，只返回上次保存值）"
        if fallback == "off":
            a.skip("outside_extents", "not_measured",
                   reason + "；已按 outside_extents.fallback=off 关闭近似判定")
            return
        robust = _robust_bounds(a)
        if robust is None:
            a.skip("outside_extents", "not_measured",
                   reason + "，且实体数不足以做分布近似判定（需要 >=5 个带包围盒的模型空间实体）")
            return
        bounds = robust
        approx = True
        a.warn("EXTENTS_STALE",
               reason + "；outside_extents 改用实体分布 5%/95% 分位数近似判定（结果标注为近似）")
    mnx, mny, mxx, mxy = bounds
    span_x = mxx - mnx
    span_y = mxy - mny
    diag = math.hypot(span_x, span_y)
    if diag <= 0:
        a.skip("outside_extents", "not_measured", "图形范围对角线长度为 0")
        return
    tol = tol_ratio * diag
    cx = (mnx + mxx) / 2.0
    cy = (mny + mxy) / 2.0
    near_hits = []
    far_hits = []
    for ent in a.entities:
        if ent.space != "model" or ent.bbox is None:
            continue
        b = ent.bbox
        if approx:
            # percentile bounds: only entities completely outside count, otherwise the
            # percentile edge itself would flag the legitimate border content
            outside = b[2] < mnx or b[0] > mxx or b[3] < mny or b[1] > mxy
        else:
            outside = (b[0] < mnx - tol or b[1] < mny - tol
                       or b[2] > mxx + tol or b[3] > mxy + tol)
        if not outside:
            continue
        far = (abs(b[0] - cx) > far_factor * span_x
               or abs(b[2] - cx) > far_factor * span_x
               or abs(b[1] - cy) > far_factor * span_y
               or abs(b[3] - cy) > far_factor * span_y)
        (far_hits if far else near_hits).append(ent)
    total = len(near_hits) + len(far_hits)
    if not total:
        return
    how = "（EXTMIN/EXTMAX 不可用，按实体分布分位数近似）" if approx else ""
    if total > flood:
        sample_handles = ", ".join(e.handle for e in (far_hits + near_hits)[:8] if e.handle)
        a.add("outside_extents",
              "%s%d 个实体落在图形范围之外（示例 handle: %s）" % (how, total, sample_handles),
              "多半是 EXTMIN/EXTMAX 过时（删除实体后未重算）：先在 CAD 里 ZOOM Extents / 重新生成边界，"
              "再复查是否真有游离图元",
              handle=None, layer=None, severity="major")
        return
    for ent in near_hits:
        b = ent.bbox
        a.add("outside_extents",
              "%s%s 的包围盒 %s~%s 超出图形边界 %s~%s（容差 %s）"
              % (how, ent.kind, fmt_pt((b[0], b[1])), fmt_pt((b[2], b[3])),
                 fmt_pt((mnx, mny)), fmt_pt((mxx, mxy)), fmt_num(tol)),
              "把游离图元移回图面，或在 CAD 里重算 EXTMIN/EXTMAX",
              handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind,
              severity="minor")
    for ent in far_hits:
        b = ent.bbox
        a.add("outside_extents",
              "%s%s 远离图面（包围盒 %s~%s，约为图幅的 %.1f 倍之外）"
              % (how, ent.kind, fmt_pt((b[0], b[1])), fmt_pt((b[2], b[3])), far_factor),
              "删除或移回：远离图面的图元会让 ZOOM Extents/出图范围失控",
              handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind,
              severity="major")


def r_dim_no_measurement(a):
    cfg = a.cfg_for("dim_no_measurement")
    keys = [str(k) for k in (cfg.get("measurement_keys") or ["measurement", "value"])]
    dims = [e for e in a.entities if e.is_dimension()]
    if not dims:
        a.skip("dim_no_measurement", "no_dimensions", "read 结果里没有标注实体（DIMENSION），本项无需检查")
        return
    measured = 0
    for ent in dims:
        for key in keys:
            if key in ent.raw and ent.raw.get(key) is not None:
                measured += 1
                break
    if measured == 0:
        a.skip("dim_no_measurement", "not_measured",
               "图上有 %d 个标注实体，但 read 不返回标注测量值（Measurement/TextOverride），"
               "本版无法校验标注数值是否与几何一致" % len(dims))
        return
    for ent in dims:
        val = None
        for key in keys:
            if key in ent.raw:
                val = ent.raw.get(key)
                break
        text = clean_text(ent.raw.get("text")).strip() if ent.raw.get("text") else ""
        if val is None and text == "":
            a.add("dim_no_measurement",
                  "标注没有可读测量值，且文字被覆盖为空",
                  "检查标注是否被分解/覆盖，或恢复测量文字",
                  handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


def r_layer_unknown(a):
    if not a.layers:
        a.skip("layer_unknown", "no_input", "info 未提供 layers 段")
        return
    known = set(a.layer_by_name.keys())
    missing = Counter()
    example = {}
    for ent in a.entities:
        if ent.layer and ent.layer not in known:
            missing[ent.layer] += 1
            example.setdefault(ent.layer, ent.handle)
    if not missing:
        return
    if len(missing) > 5:
        sample = "、".join("%s(%d)" % (name, n) for name, n in missing.most_common(10))
        a.add("layer_unknown", "read 里有 %d 个图层在 info.layers 中不存在：%s"
              % (len(missing), sample),
              "info 与 read 不是同一张图纸/同一时刻；重跑 info 与 read 后再审计",
              handle=None, layer=None)
    else:
        for name, n in missing.items():
            a.add("layer_unknown",
                  "read 里有 %d 个实体在图层 %s 上，但 info.layers 里没有这个图层" % (n, name),
                  "info 与 read 不是同一张图纸/同一时刻；重跑 info 与 read 后再审计",
                  handle=example.get(name), layer=name)


def r_text_style_missing(a):
    if not a.text_styles:
        a.skip("text_style_missing", "no_input", "info 未提供 textStyles 段")
        return
    known = {str(s.get("name") or "").upper() for s in a.text_styles if isinstance(s, dict)}
    missing = Counter()
    example = {}
    for ent in a.entities:
        if ent.kind not in TEXT_KINDS:
            continue
        style = str(ent.style or "").upper()
        if not style:
            continue
        if style not in known:
            missing[str(ent.style)] += 1
            example.setdefault(str(ent.style), ent)
    if not missing:
        return
    for style, n in missing.items():
        ent = example[style]
        a.add("text_style_missing",
              "文字样式 %s 不在 textStyles 中（%d 个文字使用）" % (style, n),
              "CAD 会用默认样式替换，字宽/字体可能变化：确认样式已加载（STYLE 命令）",
              handle=ent.handle, layer=ent.layer, space=ent.space, kind=ent.kind)


RULES = [
    ("units_missing", r_units_missing),
    ("layer_off_but_used", r_layer_off_but_used),
    ("duplicate_entity", r_duplicate_entity),
    ("zero_length_line", r_zero_length_line),
    ("unclosed_polyline", r_unclosed_polyline),
    ("linetype_missing", r_linetype_missing),
    ("linetype_scale", r_linetype_scale),
    ("dim_scale_invalid", r_dim_scale_invalid),
    ("text_empty", r_text_empty),
    ("text_overlap", r_text_overlap),
    ("outside_extents", r_outside_extents),
    ("dim_no_measurement", r_dim_no_measurement),
    ("text_height_outlier", r_text_height_outlier),
    ("layer_empty", r_layer_empty),
    ("layer_naming", r_layer_naming),
    ("layer_unknown", r_layer_unknown),
    ("text_style_missing", r_text_style_missing),
]


# --------------------------------------------------------------------------- report


def rule_title(rule_id):
    return RULE_LABELS.get(rule_id, rule_id)


RULE_LABELS = {
    "units_missing": "图纸未定义单位",
    "layer_off_but_used": "关闭/冻结图层上仍有实体",
    "duplicate_entity": "重复实体",
    "zero_length_line": "零长度线",
    "unclosed_polyline": "多段线未闭合",
    "linetype_missing": "线型未加载",
    "linetype_scale": "线型比例异常",
    "dim_scale_invalid": "标注比例异常",
    "text_empty": "空文字",
    "text_overlap": "文字重叠",
    "outside_extents": "实体超出图形范围",
    "dim_no_measurement": "标注缺测量值",
    "text_height_outlier": "文字高度离群",
    "layer_empty": "空图层",
    "layer_naming": "图层命名不规范",
    "layer_unknown": "图层不在 info 中",
    "text_style_missing": "文字样式未加载",
}


def run_audit(info_path, read_path, config, only_rules=None):
    info, info_meta = unwrap(load_json(info_path)) if info_path else ({}, {})
    read, read_meta = unwrap(load_json(read_path))
    audit = Audit(info, read, config)
    if only_rules:
        wanted = set(only_rules)
        audit.enabled = [r for r in audit.enabled if r in wanted]
    for rule_id, func in RULES:
        if rule_id not in audit.enabled:
            continue
        try:
            func(audit)
        except Exception as exc:  # a broken rule must never kill the whole audit
            audit.warn("RULE_ERROR", "规则 %s 执行失败：%s: %s"
                       % (rule_id, type(exc).__name__, exc))
    report = build_report(audit, info_path, read_path, info_meta, read_meta)
    return report, audit


def build_report(audit, info_path, read_path, info_meta, read_meta):
    issues = sorted(audit.issues,
                    key=lambda i: (SEV_RANK.get(i["severity"], 9), i["rule"], str(i["handle"])))
    by_sev = Counter(i["severity"] for i in issues)
    for sev in SEVERITIES:
        by_sev.setdefault(sev, 0)
    by_rule = OrderedDict()
    rule_sev = {}
    for item in issues:
        cur = rule_sev.get(item["rule"])
        if cur is None or SEV_RANK[item["severity"]] < SEV_RANK[cur]:
            rule_sev[item["rule"]] = item["severity"]
    for rule_id in RULE_SPECS:
        if rule_id not in audit.enabled:
            continue
        total = audit.counts.get(rule_id, 0)
        if not total:
            continue
        by_rule[rule_id] = {
            "count": total,
            "severity": rule_sev.get(rule_id, audit.severity(rule_id)),
            "defaultSeverity": audit.severity(rule_id),
            "reported": total - audit.suppressed.get(rule_id, 0),
            "suppressed": audit.suppressed.get(rule_id, 0),
            "label": rule_title(rule_id),
        }
    read_counts = audit.read.get("counts") or {}
    entities = audit.entities
    counts = OrderedDict([
        ("entities", len(entities)),
        ("layers", len(audit.layers) if audit.layers else None),
        ("texts", sum(1 for e in entities if e.kind in TEXT_KINDS)),
        ("polylines", sum(1 for e in entities if e.kind in POLY_KINDS)),
        ("lines", sum(1 for e in entities if e.kind == "LINE")),
        ("dimensions", sum(1 for e in entities if e.is_dimension())),
        ("other", sum(1 for e in entities
                      if e.kind not in TEXT_KINDS and e.kind not in POLY_KINDS
                      and e.kind != "LINE" and not e.is_dimension())),
    ])
    app = (info_meta.get("app") or read_meta.get("app") or {}) if isinstance(info_meta, dict) else {}
    doc = (info_meta.get("doc") or read_meta.get("doc") or {}) if isinstance(info_meta, dict) else {}
    source = OrderedDict([
        ("progId", app.get("progId") if isinstance(app, dict) else None),
        ("product", app.get("product") if isinstance(app, dict) else None),
        ("pid", app.get("pid") if isinstance(app, dict) else None),
        ("doc", doc.get("fullName") or doc.get("name") if isinstance(doc, dict) else None),
        ("scope", audit.read.get("scope")),
        ("readPath", audit.read.get("readPath")),
        ("matchedBy", audit.read.get("matchedBy")),
        ("truncated", bool(audit.read.get("truncated"))),
        ("returned", read_counts.get("returned")),
        ("model", read_counts.get("model")),
        ("paper", read_counts.get("paper")),
    ])
    blockers = by_sev.get("blocker", 0)
    report = OrderedDict([
        ("ok", True),
        ("tool", TOOL),
        ("schema", SCHEMA),
        ("generatedAt", datetime.datetime.now().astimezone().isoformat(timespec="seconds")),
        ("inputs", OrderedDict([("info", os.path.abspath(info_path) if info_path else None),
                                ("read", os.path.abspath(read_path))])),
        ("source", source),
        ("counts", counts),
        ("summary", OrderedDict([
            ("issues", len(issues)),
            ("bySeverity", OrderedDict((sev, by_sev.get(sev, 0)) for sev in SEVERITIES)),
            ("byRule", by_rule),
            ("rulesEnabled", list(audit.enabled)),
            ("rulesWithHits", len(by_rule)),
            ("skipped", audit.skipped),
            ("warnings", audit.warnings),
        ])),
        ("gate", OrderedDict([
            ("blockers", blockers),
            ("pass", blockers == 0),
            ("exitCode", 3 if blockers else 0),
        ])),
        ("issues", issues),
        ("config", effective_config_summary(audit.cfg)),
    ])
    if source.get("truncated"):
        audit.warn("READ_TRUNCATED", "read 返回结果被 -Max 截断，审计只覆盖前 %s 个实体"
                   % (source.get("returned") or "?"))
        report["summary"]["warnings"] = audit.warnings
    return report


def effective_config_summary(cfg):
    keys = ["layer_naming", "linetype_missing", "linetype_scale", "text_height_outlier",
            "text_overlap", "zero_length_line", "duplicate_entity", "unclosed_polyline",
            "outside_extents", "dim_no_measurement"]
    out = OrderedDict()
    for key in keys:
        value = cfg.get(key)
        if isinstance(value, dict):
            if key == "linetype_missing":
                value = {k: v for k, v in value.items() if k != "standard"}
            out[key] = value
    out["max_issues_per_rule"] = cfg.get("max_issues_per_rule")
    out["severity"] = cfg.get("severity")
    return out


# --------------------------------------------------------------------------- markdown

_SEV_LABEL = {"blocker": "阻断", "major": "重要", "minor": "提示"}


def _md_escape(text):
    if text is None:
        return ""
    return str(text).replace("|", "\\|").replace("\r", " ").replace("\n", " ")


def render_markdown(report):
    src = report["source"]
    counts = report["counts"]
    summary = report["summary"]
    sev = summary["bySeverity"]
    lines = []
    lines.append("# 图纸体检报告")
    lines.append("")
    lines.append("> 由 `scripts/audit/audit.py` 生成（只读审计层，不修改图纸）。")
    lines.append("> 数据源：`info` + `read` 的 JSON 输出；报告中的 handle 可直接在 CAD 里用 `(handent \"...\")` 定位。")
    lines.append("")
    lines.append("| 项 | 值 |")
    lines.append("|---|---|")
    lines.append("| 生成时间 | %s |" % report["generatedAt"])
    if src.get("progId"):
        lines.append("| CAD 产品 | %s（%s，pid %s） |"
                     % (src.get("product") or "?", src.get("progId"), src.get("pid")))
    if src.get("doc"):
        lines.append("| 图纸 | `%s` |" % _md_escape(src.get("doc")))
    lines.append("| 读取范围 | %s%s |"
                 % (src.get("scope") or "?",
                    "（**已截断**，只覆盖前 %s 个实体）" % src.get("returned") if src.get("truncated") else ""))
    lines.append("| 实体数 | %s（文字 %s / 多段线 %s / 线 %s / 标注 %s / 其他 %s） |"
                 % (counts["entities"], counts["texts"], counts["polylines"],
                    counts["lines"], counts["dimensions"], counts["other"]))
    lines.append("| 图层数 | %s |" % ("—" if counts["layers"] is None else counts["layers"]))
    lines.append("")
    verdict = "**通过**" if summary["issues"] == 0 and not sev["blocker"] else \
        ("**不通过**（存在 %d 个阻断问题）" % sev["blocker"] if sev["blocker"] else "**有告警**（无阻断项）")
    lines.append("## 一、结论")
    lines.append("")
    lines.append("| 指标 | 数量 |")
    lines.append("|---|---|")
    lines.append("| 命中问题总数 | %d |" % summary["issues"])
    lines.append("| 阻断 blocker | %d |" % sev["blocker"])
    lines.append("| 重要 major | %d |" % sev["major"])
    lines.append("| 提示 minor | %d |" % sev["minor"])
    lines.append("")
    lines.append("结论：%s（门禁：blocker==0 时通过）" % verdict)
    missing_checks = [s for s in summary["skipped"] if s["reason"] in ("not_measured", "no_input")]
    if missing_checks:
        lines.append("")
        lines.append("另有 %d 项检查因数据不足未执行，见第四节（**不要把它们当作已通过**）。"
                     % len(missing_checks))
    lines.append("")
    lines.append("## 二、按规则统计")
    lines.append("")
    if not summary["byRule"]:
        lines.append("（无命中）")
    else:
        lines.append("| 规则 | 说明 | 级别 | 命中 | 其中已列出 |")
        lines.append("|---|---|---|---|---|")
        for rule_id, info in summary["byRule"].items():
            lines.append("| `%s` | %s | %s | %d | %d |"
                         % (rule_id, info.get("label") or rule_title(rule_id),
                            _SEV_LABEL.get(info["severity"], info["severity"]),
                            info["count"], info["reported"]))
    lines.append("")
    lines.append("## 三、问题清单")
    lines.append("")
    if not report["issues"]:
        lines.append("无。")
        lines.append("")
    else:
        for sev_key in SEVERITIES:
            group = [i for i in report["issues"] if i["severity"] == sev_key]
            if not group:
                continue
            lines.append("### %s（%s，%d 条）" % (_SEV_LABEL.get(sev_key, sev_key), sev_key, len(group)))
            lines.append("")
            lines.append("| # | 规则 | handle | 图层 | 问题 | 建议 |")
            lines.append("|---|---|---|---|---|---|")
            for idx, item in enumerate(group, 1):
                lines.append("| %d | `%s` | %s | %s | %s | %s |"
                             % (idx, item["rule"],
                                item["handle"] or "-",
                                _md_escape(item["layer"] or "-"),
                                _md_escape(item["sample"]),
                                _md_escape(item["suggest"])))
            lines.append("")
        truncated_rules = [(r, i) for r, i in summary["byRule"].items() if i.get("suppressed")]
        if truncated_rules:
            lines.append("> 以下规则命中数超过 `max_issues_per_rule`，清单已截断：%s"
                         % "、".join("`%s`（另有 %d 条未列出）" % (r, i["suppressed"])
                                    for r, i in truncated_rules))
            lines.append("")
    lines.append("## 四、未执行的检查（数据不足，不等于通过）")
    lines.append("")
    if not summary["skipped"]:
        lines.append("无。")
    else:
        lines.append("| 规则 | 原因 | 说明 |")
        lines.append("|---|---|---|")
        for item in summary["skipped"]:
            lines.append("| `%s` | %s | %s |"
                         % (item["rule"], item["reason"], _md_escape(item["detail"])))
    lines.append("")
    lines.append("## 五、数据源警告")
    lines.append("")
    if not summary["warnings"]:
        lines.append("无。")
    else:
        for item in summary["warnings"]:
            lines.append("- **%s**：%s" % (item["code"], item["message"]))
    lines.append("")
    lines.append("## 六、生效阈值")
    lines.append("")
    cfg = report.get("config") or {}
    lines.append("```json")
    lines.append(json.dumps(cfg, ensure_ascii=False, indent=2))
    lines.append("```")
    lines.append("")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- console


def print_console(report, quiet=False):
    sev = report["summary"]["bySeverity"]
    gate = report["gate"]
    layers = report["counts"]["layers"]
    head = "AUDIT %s: issues=%d blocker=%d major=%d minor=%d entities=%d layers=%s"
    sys.stdout.write(head % ("PASS" if gate["pass"] else "BLOCKED", report["summary"]["issues"],
                             sev["blocker"], sev["major"], sev["minor"],
                             report["counts"]["entities"],
                             "-" if layers is None else layers) + "\n")
    if quiet:
        return
    for rule_id, info in report["summary"]["byRule"].items():
        sys.stdout.write("  %-7s %-24s %4d\n" % (info["severity"], rule_id, info["count"]))
    for item in report["summary"]["skipped"]:
        sys.stdout.write("  SKIP    %-24s (%s)\n" % (item["rule"], item["reason"]))
    for item in report["summary"]["warnings"]:
        sys.stdout.write("  WARN    %s\n" % item["code"])


# --------------------------------------------------------------------------- cli


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="audit.py",
        description="Drawing health audit for zwcad-cooperate (read-only, consumes zwc.ps1 JSON).")
    parser.add_argument("--info", help="zwc.ps1 info response JSON (optional but recommended)")
    parser.add_argument("--read", required=False, help="zwc.ps1 read response JSON")
    parser.add_argument("--rules", help="comma separated rule ids (default: all)")
    parser.add_argument("--config", help="JSON config file (or inline JSON string)")
    parser.add_argument("--json", dest="json_out", help="write machine report here")
    parser.add_argument("--md", dest="md_out", help="write human report (markdown) here")
    parser.add_argument("--exit-code", action="store_true",
                        help="exit 3 when blockers exist (gate mode)")
    parser.add_argument("--list-rules", action="store_true", help="print rule ids and exit")
    parser.add_argument("--quiet", action="store_true", help="only print the one line summary")
    args = parser.parse_args(argv)
    if not args.list_rules and not args.read:
        parser.error("--read is required (unless --list-rules)")
    return args


def load_config(spec):
    cfg = deep_merge(DEFAULT_CONFIG, {})
    if not spec:
        return cfg
    if spec.strip().startswith("{"):
        override = json.loads(spec)
    else:
        override = load_json(spec)
    return deep_merge(cfg, override)


def main(argv=None):
    try:
        sys.stdout.reconfigure(errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass
    args = parse_args(argv if argv is not None else sys.argv[1:])
    if args.list_rules:
        for rule_id, (sev, desc) in RULE_SPECS.items():
            sys.stdout.write("%-22s %-8s %s\n" % (rule_id, sev, desc))
        return 0
    try:
        config = load_config(args.config)
    except Exception as exc:
        sys.stderr.write("bad --config: %s\n" % exc)
        return 2
    only = None
    if args.rules:
        only = [r.strip() for r in args.rules.split(",") if r.strip()]
        unknown = [r for r in only if r not in RULE_SPECS]
        if unknown:
            sys.stderr.write("unknown rule(s): %s\nknown: %s\n"
                             % (", ".join(unknown), ", ".join(RULE_SPECS)))
            return 2
    try:
        report, _audit = run_audit(args.info, args.read, config, only)
    except FileNotFoundError as exc:
        sys.stderr.write("input file not found: %s\n" % exc)
        return 2
    except ValueError as exc:
        sys.stderr.write("bad input: %s\n" % exc)
        return 2
    except json.JSONDecodeError as exc:
        sys.stderr.write("bad JSON: %s\n" % exc)
        return 2
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps(report, ensure_ascii=False, indent=2))
            fh.write("\n")
    if args.md_out:
        with open(args.md_out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(render_markdown(report))
    print_console(report, quiet=args.quiet)
    if args.exit_code and report["gate"]["blockers"]:
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
