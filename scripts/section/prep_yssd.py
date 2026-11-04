#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""prep_yssd.py -- prepare.py 的「实测大断面成果表（yssd）」层（2026-11-05 原样搬出）。

职责：表头识别（重复的 垂线号|起点距|河底高程 列组）、施测块元信息与块内条目解析、
选块、点列生成与排序、数据质量闸门（截断/离群/跨块差）、yssd 模型装配（复用 §4.2 建模）。

**纯搬移**：除 import/模块头外，函数体与常量逐字来自 prepare.py；本文件不改变任何行为。
"""

from __future__ import annotations

import argparse
import re
import statistics
from typing import Any, Dict, List, Optional, Sequence, Tuple

from prep_io import SectionError, _is_blank, _norm_header, _soft_number
from prep_model import build_model

# ---- 输入 profile ----
PROFILE_AUTO = "auto"
PROFILE_TWO_COL = "two-col"
PROFILE_YSSD = "yssd"
PROFILES = (PROFILE_AUTO, PROFILE_TWO_COL, PROFILE_YSSD)

# yssd（实测大断面成果表）表头关键字：注意真实表头是「垂    线     号 / 起    点    距 / 河底    高程」
YSSD_POINT_KEYS = ("点号", "垂线号", "垂线", "编号", "测点", "point")
YSSD_OFFSET_KEYS = ("起点距", "起点距离", "距起点", "桩号", "距离", "offset")
YSSD_ELEV_KEYS = ("河底高程", "高程", "河底", "elevation", "elev")
# 元信息/备注行（不是数据点）
YSSD_META_MARKERS = ("施测日期", "附注", "备注", "说明", "单位", "页码", "第 ", "断面名称",
                     "测时水位", "年份", "测站编码", "校核", "填表", "负责人")
# 数据质量：高程单元格被导出/录入截断（无小数点且长度 <= 2 的纯数字文本）
ELEV_TRUNC_RE = re.compile(r"^[+-]?\d{1,2}$")
SUSPECT_OUT_OF_RANGE_M = 40.0   # 相对中位数偏离超过该值 -> 可疑（任务约定 ±40 m）
SUSPECT_CROSS_BLOCK_M = 10.0    # 与另一次施测同起点距差值超过该值 -> 可疑（任务约定 10 m）
DUPLICATE_OFFSET_TOL = 1e-9     # 起点距重复判定容差（m）


def _norm_date_key(text: Any) -> str:
    """把 '1月19日' / '2025-09-18' / '9-18' 归一到 'M-D'。"""
    s = str(text or "")
    m = re.search(r"(\d{1,2})\s*月\s*(\d{1,2})\s*日", s)
    if m:
        return "%d-%d" % (int(m.group(1)), int(m.group(2)))
    m = re.search(r"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})", s)
    if m:
        return "%d-%d" % (int(m.group(2)), int(m.group(3)))
    m = re.search(r"(\d{1,2})\D+(\d{1,2})", s)
    if m:
        return "%d-%d" % (int(m.group(1)), int(m.group(2)))
    return s.strip()


def find_yssd_header(rows: Sequence[Sequence[Any]],
                     max_scan: Optional[int] = None) -> Optional[Tuple[int, List[Tuple[Optional[int], int, int]]]]:
    """找实测大断面表头行：同一行里「起点距」>=2 次且「高程」>=2 次。

    返回 (表头行下标, [(点号列, 起点距列, 高程列), ...] 0-based)；找不到返回 None。
    """
    limit = len(rows) if max_scan is None else min(max_scan, len(rows))
    for r_idx in range(limit):
        row = rows[r_idx]
        heads = [_norm_header(v) for v in row]
        n_off = sum(1 for h in heads if any(k in h for k in ("起点距", "桩号")))
        n_el = sum(1 for h in heads if "高程" in h)
        if n_off < 2 or n_el < 2:
            continue
        groups: List[Tuple[Optional[int], int, int]] = []
        for c, h in enumerate(heads):
            if not any(k in h for k in ("起点距", "桩号")):
                continue
            if c + 1 >= len(heads) or "高程" not in heads[c + 1]:
                continue
            pcol: Optional[int] = None
            if c - 1 >= 0 and any(k in heads[c - 1] for k in YSSD_POINT_KEYS):
                pcol = c - 1
            groups.append((pcol, c, c + 1))
        if groups:
            return r_idx, groups
    return None


def detect_yssd_profile(rows: Sequence[Sequence[Any]]) -> bool:
    return find_yssd_header(rows) is not None


def _block_meta(row: Sequence[Any]) -> Dict[str, Any]:
    """从「施测日期： 1月19日 … 测时水位： 17.85」这一行抽元信息。"""
    date = ""
    water: Optional[float] = None
    section = ""
    row_text = ""
    for c, v in enumerate(row):
        h = _norm_header(v)
        raw = "" if v is None else str(v)
        row_text += raw + " "
        if "施测日期" in h:
            for c2 in range(c + 1, len(row)):
                if not _is_blank(row[c2]):
                    date = str(row[c2]).strip()
                    break
        if "测时水位" in h:
            m = re.search(r"([-+]?\d+(?:\.\d+)?)", raw)
            if m:
                water = float(m.group(1))
        if "断面名称" in h:
            m = re.search(r"[：:]\s*(.+)$", raw)
            section = (m.group(1) if m else raw).strip()
    date_key = _norm_date_key(date) if date else _norm_date_key(row_text)
    if date and not date_key:
        date_key = date.strip()
    return {"date": date, "dateKey": date_key, "waterLevel": water, "sectionName": section}


def parse_yssd_blocks(rows: Sequence[Sequence[Any]], header_idx: int,
                      groups: Sequence[Tuple[Optional[int], int, int]]) -> List[Dict[str, Any]]:
    """把一个 yssd 表解析成若干「施测块」。数据按列块自上而下收集后拼成一维序列。"""
    meta_rows = [r for r in range(header_idx + 1, len(rows))
                 if any("施测日期" in _norm_header(v) for v in rows[r])]
    if not meta_rows:
        raise SectionError("BAD_DATA", "实测大断面表里找不到「施测日期」行",
                           headerRow=header_idx + 1)

    blocks: List[Dict[str, Any]] = []
    for bi, mrow in enumerate(meta_rows):
        start = mrow + 1
        end = meta_rows[bi + 1] if bi + 1 < len(meta_rows) else len(rows)
        entries: List[Dict[str, Any]] = []
        for gi, (pcol, ocol, ecol) in enumerate(groups):
            for r in range(start, end):
                row = rows[r]
                pv = row[pcol] if pcol is not None and pcol < len(row) else None
                ov = row[ocol] if ocol < len(row) else None
                ev = row[ecol] if ecol < len(row) else None
                if _is_blank(pv) and _is_blank(ov) and _is_blank(ev):
                    continue
                base = {"row": r + 1, "group": gi + 1}
                joined = "".join(_norm_header(x) for x in row)
                if any(mk in joined for mk in ("附注", "备注", "说明")):
                    base.update({"kind": "meta", "pointRaw": pv, "offset": None, "elev": None,
                                 "offsetRaw": ov, "elevRaw": ev})
                    entries.append(base)
                    continue
                pf, pok = _soft_number(pv)
                of, ook = _soft_number(ov)
                ef, eok = _soft_number(ev)
                if of is None or ef is None or not ook or not eok:
                    reasons = []
                    if of is None:
                        reasons.append("BAD_OFFSET")
                    if ef is None:
                        reasons.append("BAD_ELEV")
                    base.update({"kind": "invalid", "pointRaw": pv, "point": pf,
                                 "offset": of, "elev": ef, "offsetRaw": ov, "elevRaw": ev,
                                 "reason": "+".join(reasons) or "INVALID_ROW"})
                    entries.append(base)
                    continue
                is_label = not (pf is not None and pok)
                base.update({
                    "kind": "label" if is_label else "number",
                    "pointRaw": pv,
                    "point": None if is_label else pf,
                    "offset": of, "elev": ef,
                    "offsetRaw": ov, "elevRaw": ev,
                })
                entries.append(base)

        meta = _block_meta(rows[mrow])
        numbered = sum(1 for e in entries if e["kind"] == "number")
        labels = sum(1 for e in entries if e["kind"] == "label")
        meta_count = sum(1 for e in entries if e["kind"] == "meta")
        invalid = sum(1 for e in entries if e["kind"] == "invalid")
        blocks.append({
            "index": bi + 1,
            "metaRow": mrow + 1,
            "entries": entries,
            "numbered": numbered,
            "labels": labels,
            "metaCount": meta_count,
            "invalid": invalid,
            **meta,
        })
    return blocks


def select_yssd_block(blocks: Sequence[Dict[str, Any]], block_no: Optional[int],
                      date: Optional[str]) -> Dict[str, Any]:
    available = [{"index": b["index"], "date": b["date"], "dateKey": b["dateKey"],
                  "waterLevel": b["waterLevel"], "entries": len(b["entries"]),
                  "numbered": b["numbered"], "labels": b["labels"]} for b in blocks]
    if block_no is not None:
        if not (1 <= block_no <= len(blocks)):
            raise SectionError("BAD_DATA", "--block 越界", block=block_no, available=available)
        return blocks[block_no - 1]
    if date is not None:
        want = _norm_date_key(date)
        for b in blocks:
            if b["dateKey"] == want or (want and want in _norm_date_key(b["date"])):
                return b
        raise SectionError("BAD_DATA", "找不到指定施测日期", date=date, available=available)
    return blocks[0]


def entries_to_points(block: Dict[str, Any], drop_label_rows: bool = True
                      ) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    """把块的条目转成点列（按起点距排序），返回 (points, info)。"""
    points: List[Dict[str, Any]] = []
    ignored: List[Dict[str, Any]] = []
    for e in block["entries"]:
        if e["kind"] == "meta":
            ignored.append({"row": e["row"], "reason": "META_ROW",
                            "text": str(e.get("pointRaw") or "")})
            continue
        if e["kind"] == "invalid":
            ignored.append({"row": e["row"], "reason": e.get("reason", "INVALID_ROW"),
                            "offsetRaw": str(e.get("offsetRaw") or ""),
                            "elevRaw": str(e.get("elevRaw") or "")})
            continue
        if e["kind"] == "label":
            if drop_label_rows:
                ignored.append({"row": e["row"], "reason": "LABEL_ROW",
                                "text": str(e.get("pointRaw") or ""),
                                "offset": e["offset"], "elev": e["elev"]})
                continue
            points.append({"point": None, "tag": str(e["pointRaw"]).strip(),
                           "offset": e["offset"], "elev": e["elev"],
                           "row": e["row"], "group": e["group"],
                           "offsetRaw": e["offsetRaw"], "elevRaw": e["elevRaw"]})
            continue
        pn = int(round(e["point"])) if e["point"] is not None else None
        points.append({"point": pn, "tag": None,
                       "offset": e["offset"], "elev": e["elev"],
                       "row": e["row"], "group": e["group"],
                       "offsetRaw": e["offsetRaw"], "elevRaw": e["elevRaw"]})

    seq = [p["offset"] for p in points]
    non_monotonic = any(seq[i] > seq[i + 1] + 1e-9 for i in range(len(seq) - 1))
    points.sort(key=lambda p: (p["offset"],
                               p["point"] if p["point"] is not None else 10 ** 9))
    duplicates: List[Dict[str, Any]] = []
    for a, b in zip(points, points[1:]):
        if abs(a["offset"] - b["offset"]) <= DUPLICATE_OFFSET_TOL:
            duplicates.append({"offset": a["offset"],
                               "rows": [a["row"], b["row"]],
                               "points": [a.get("point"), b.get("point")],
                               "elevs": [a["elev"], b["elev"]]})
    return points, {"duplicates": duplicates, "ignored": ignored,
                    "nonMonotonic": non_monotonic,
                    "labelRowsDropped": sum(1 for i in ignored if i["reason"] == "LABEL_ROW")}


def _elev_looks_reliable(raw: Any) -> bool:
    """源表高程原文是否「看起来可信」：真数值，或文本里带小数点（未被截断）。"""
    if raw is None:
        return False
    if isinstance(raw, bool):
        return False
    if isinstance(raw, (int, float)):
        return True
    t = str(raw).strip()
    return ("." in t) and re.search(r"\d", t) is not None


def assess_data_quality(points: List[Dict[str, Any]], block: Dict[str, Any],
                        blocks: Sequence[Dict[str, Any]]) -> Dict[str, Any]:
    """数据质量闸门：逐点判定可疑高程（多判据、如实报告，绝不静默丢弃）。"""
    elevs = [p["elev"] for p in points]
    median_elev = statistics.median(elevs) if elevs else 0.0

    # 另一次施测（同起点距）索引，用于交叉核对
    other_index: Dict[float, List[Dict[str, Any]]] = {}
    for b in blocks:
        if b["index"] == block["index"]:
            continue
        for e in b["entries"]:
            if e["kind"] in ("number", "label") and e["offset"] is not None and e["elev"] is not None:
                other_index.setdefault(round(e["offset"], 6), []).append(
                    {"elev": e["elev"], "block": b["index"], "point": e.get("pointRaw"),
                     "elevRaw": e.get("elevRaw")})

    suspects: List[Dict[str, Any]] = []
    for p in points:
        reasons: List[str] = []
        raw = p.get("elevRaw")
        if isinstance(raw, str) and ELEV_TRUNC_RE.match(raw.strip()):
            reasons.append("ELEV_TEXT_TRUNCATED")
        if abs(p["elev"] - median_elev) > SUSPECT_OUT_OF_RANGE_M:
            reasons.append("ELEV_OUT_OF_RANGE")
        # 交叉核对：只用「对方块里看起来可信」的高程来比（对方自己也是截断文本时不能作证）
        for o in other_index.get(round(p["offset"], 6), []):
            if not _elev_looks_reliable(o.get("elevRaw")):
                continue
            if abs(p["elev"] - o["elev"]) > SUSPECT_CROSS_BLOCK_M:
                reasons.append("CROSS_BLOCK_DIFF")
                p["crossBlock"] = {"block": o["block"], "elev": o["elev"],
                                   "point": o.get("point")}
                break
        if reasons:
            p["_suspect"] = True
            p["_reasons"] = reasons
            suspects.append({
                "point": p.get("point"), "tag": p.get("tag"),
                "offset": p["offset"], "elev": p["elev"],
                "elevRaw": p.get("elevRaw"), "row": p.get("row"), "group": p.get("group"),
                "reasons": reasons,
            })

    return {
        "profile": PROFILE_YSSD,
        "rules": {
            "ELEV_TEXT_TRUNCATED": "高程单元格是文本且无小数点、长度<=2（源表导出/录入截断，"
                                   "如 5.73 变 73、28.24 变 28）",
            "ELEV_OUT_OF_RANGE": "|高程 - 本块中位数| > %g m" % SUSPECT_OUT_OF_RANGE_M,
            "CROSS_BLOCK_DIFF": "与另一次施测同起点距的高程差 > %g m" % SUSPECT_CROSS_BLOCK_M,
        },
        "suspectCount": len(suspects),
        "dropped": 0,
        "suspect": suspects,
        "medianElev": median_elev,
        "blocks": [{"index": b["index"], "date": b["date"], "dateKey": b["dateKey"],
                    "waterLevel": b["waterLevel"], "sectionName": b["sectionName"],
                    "metaRow": b["metaRow"], "entries": len(b["entries"]),
                    "numbered": b["numbered"], "labels": b["labels"],
                    "meta": b["metaCount"], "invalid": b["invalid"]} for b in blocks],
        "selectedBlock": block["index"],
    }


def build_yssd_model(rows: Sequence[Sequence[Any]], meta: Dict[str, Any],
                     args: argparse.Namespace,
                     water_levels: List[Tuple[float, str]]) -> Dict[str, Any]:
    """yssd profile 主流程：表头 -> 块 -> 点列 -> 质量闸门 -> 复用 §4.2/§4.3 建模。"""
    if args.cols:
        raise SectionError("BAD_ARGS", "yssd profile 不接受 --cols（列结构由表头自动识别）")
    header = find_yssd_header(rows)
    if header is None:
        raise SectionError(
            "BAD_DATA", "未识别到实测大断面表头（同一行需「起点距」「高程」各出现 >=2 次）",
            scannedRows=len(rows))
    header_idx, groups = header
    blocks = parse_yssd_blocks(rows, header_idx, groups)
    block = select_yssd_block(blocks, args.block, args.date)

    points, pinfo = entries_to_points(block, drop_label_rows=bool(args.drop_label_rows))
    if len(points) < 3:
        raise SectionError("BAD_DATA", "有效点不足 3 个",
                           pointCount=len(points), block=block["index"],
                           entries=len(block["entries"]))
    elevs = [p["elev"] for p in points]
    if max(elevs) - min(elevs) <= 0.0:
        raise SectionError("BAD_DATA", "高程全等，无法成图",
                           elevMin=min(elevs), elevMax=max(elevs))

    dq = assess_data_quality(points, block, blocks)
    dq.update({
        "duplicateOffsets": pinfo["duplicates"],
        "ignoredRows": pinfo["ignored"],
        "nonMonotonic": pinfo["nonMonotonic"],
        "labelRowsDropped": pinfo["labelRowsDropped"],
    })
    if dq["suspectCount"] and args.fail_on_suspect:
        raise SectionError("BAD_DATA", "检出可疑数据点（--fail-on-suspect）",
                           suspectCount=dq["suspectCount"],
                           sample=dq["suspect"][:5],
                           hint="去掉 --fail-on-suspect，或用 --drop-suspect 剔除后出图")
    if args.drop_suspect:
        before = len(points)
        points = [p for p in points if not p.get("_suspect")]
        dq["dropped"] = before - len(points)
        if len(points) < 3:
            raise SectionError("BAD_DATA", "剔除可疑点后有效点不足 3 个",
                               pointCount=len(points), dropped=dq["dropped"])

    water_auto: Optional[float] = None
    if not water_levels and not args.no_auto_water and block.get("waterLevel") is not None:
        label = args.water_label or "测时水位"
        water_levels = [(float(block["waterLevel"]), label)]
        water_auto = float(block["waterLevel"])

    model = build_model(points, False, args, meta, water_levels)

    suspect_ids = {id(p) for p in points if p.get("_suspect")}
    detail: List[Dict[str, Any]] = []
    for i, p in enumerate(points, 1):
        detail.append({
            "i": i, "point": p.get("point"), "tag": p.get("tag"),
            "offset": p["offset"], "elev": p["elev"],
            "offsetRaw": p.get("offsetRaw"), "elevRaw": p.get("elevRaw"),
            "row": p.get("row"), "group": p.get("group"),
            "suspect": id(p) in suspect_ids,
            "reasons": p.get("_reasons") or [],
        })
    model["pointsDetail"] = detail
    model["source"].update({
        "profile": PROFILE_YSSD,
        "reader": meta.get("reader"),
        "headerRow": header_idx + 1,
        "groupCount": len(groups),
        "columns": [{"point": (g[0] + 1) if g[0] is not None else None,
                     "offset": g[1] + 1, "elev": g[2] + 1} for g in groups],
        "block": block["index"],
        "date": block["date"],
        "dateKey": block["dateKey"],
        "sectionName": block["sectionName"],
        "waterLevelFromFile": block["waterLevel"],
        "blocks": dq["blocks"],
        "labelRows": block["labels"],
        "labelRowsDropped": pinfo["labelRowsDropped"],
        "ignoredRows": pinfo["ignored"],
    })
    model["dataQuality"] = dq
    kept_suspects = sum(1 for p in points if p.get("_suspect"))
    model["computed"].update({
        "profile": PROFILE_YSSD,
        "block": block["index"],
        "pointCount": len(points),
        "suspectKept": kept_suspects,
        "suspectDropped": dq["dropped"],
        "labelRowsDropped": pinfo["labelRowsDropped"],
    })

    warns = model["warnings"]
    if len(blocks) > 1:
        warns.append({
            "code": "MULTIPLE_BLOCKS",
            "message": "源表含 %d 个施测块，已选第 %d 块（%s，测时水位 %s）；可用 --block/--date 切换"
                       % (len(blocks), block["index"], block["date"],
                          block.get("waterLevel")),
            "available": [{"index": b["index"], "date": b["date"],
                           "waterLevel": b["waterLevel"]} for b in blocks],
        })
    if water_auto is not None:
        warns.append({
            "code": "WATER_FROM_FILE",
            "message": "水位线取自源表测时水位 %s m（--no-auto-water 可关）" % water_auto,
        })
    if pinfo["duplicates"]:
        warns.append({"code": "DUPLICATE_OFFSET",
                      "message": "起点距重复 %d 处（已全部保留，制图会形成竖直段）"
                                 % len(pinfo["duplicates"]),
                      "duplicates": pinfo["duplicates"]})
    if pinfo["nonMonotonic"]:
        warns.append({"code": "NON_MONOTONIC",
                      "message": "源表按列块填充，原始顺序非单调递增；已按起点距排序"})
    if pinfo["labelRowsDropped"]:
        warns.append({"code": "LABEL_ROWS_DROPPED",
                      "message": "已过滤 %d 个 左岸/右岸 等标签行（其带岸顶坐标，"
                                 "--keep-label-rows 可保留）" % pinfo["labelRowsDropped"]})
    if kept_suspects:
        warns.append({"code": "SUSPECT_DATA",
                      "message": "检出 %d 个可疑高程（默认保留并在 dataQuality 中列明，"
                                 "--drop-suspect 可剔除）" % kept_suspects})
    if dq["dropped"]:
        warns.append({"code": "SUSPECT_DROPPED",
                      "message": "已剔除 %d 个可疑高程点" % dq["dropped"]})
    return model
