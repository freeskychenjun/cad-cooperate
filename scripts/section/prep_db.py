#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""prep_db.py -- prepare.py 的「hydro_data.db 数据源 + schema v2 多序列」层（2026-11-05 搬出）。

职责：只读 URI 打开 hydro_data.db、站信息/施测日期列表、按 --date/--years/--last 选日期、
逐序列取点与日均水位、序列统计、v2 多序列模型装配（共用 prep_model 的坐标映射）、
DB vs 源 .xls 逐点比对、v1 --summary 用的 _series_summary。

**纯搬移**：除 import/模块头与「坐标映射改调 prep_model 单一实现」外，函数体逐字来自 prepare.py。
`DEFAULT_DB_PATH` 已改为**调用期求值**（`default_db_path()` + PEP 562 `__getattr__` 兼容访问）：
旧实现在导入期快照 `HYDRO_DB`，导致「先 `import prepare` 再设环境变量」不生效。
"""

from __future__ import annotations

import argparse
import os
import re
import statistics
from typing import Any, Dict, List, Optional, Sequence, Tuple

from prep_io import SectionError, warn, warn_exc
from prep_model import (TEXT_HEIGHT_MM, WATER_TEXT_HEIGHT_MM, CoordMapper,
                        append_too_tall_warning, choose_h_scale, measure_bbox,
                        resolve_layer_prefix, resolve_paper)
from prep_yssd import (DUPLICATE_OFFSET_TOL, ELEV_TRUNC_RE, SUSPECT_OUT_OF_RANGE_M,
                       _norm_date_key)

# ----------------------------------------------------------------------------- 数据库（hydro_data.db）
# 水情数据库路径不写进仓库：优先读环境变量 HYDRO_DB，或用 --db 显式指定。
# 注意 HYDRO_DB 必须**调用期**求值：导入期快照会让「import 之后才设置的环境变量」失效。


def default_db_path() -> str:
    """环境变量 HYDRO_DB 的**调用期**值（导入期求值会让 main 里才设的 env 失效）。"""
    return os.environ.get("HYDRO_DB", "")


def __getattr__(name: str) -> Any:
    """PEP 562：保住 `prep_db.DEFAULT_DB_PATH`（含 `from prep_db import DEFAULT_DB_PATH`）兼容。

    返回的是**取属性那一刻**的 env 值（不再是导入期快照），因此旧调用点无需改造；
    其它名字照旧抛 AttributeError，不改变探测语义。
    """
    if name == "DEFAULT_DB_PATH":
        return default_db_path()
    raise AttributeError(name)


DB_WATER_LABEL = "日均水位"
# 序列配色（ZWCAD ACI）：1红 3绿 5蓝 2黄 4青 6品红，避开 7=白（黑底/打印不可见）
SERIES_PALETTE: Tuple[int, ...] = (1, 3, 5, 2, 4, 6)
SCHEMA_MODEL_V2 = "zwcad-section-model/2"


def open_db(path: str) -> Any:
    """只读 URI 打开 hydro_data.db（库可能很大，绝不写）。"""
    if not path:
        raise SectionError("NO_DB", "未指定数据库路径：用 --db 或环境变量 HYDRO_DB 指定 hydro_data.db")
    try:
        import sqlite3
    except ImportError:  # pragma: no cover
        raise SectionError("NO_SQLITE3", "缺少 sqlite3 模块")
    if not os.path.isfile(path):
        raise SectionError("NO_DB", "数据库文件不存在", path=path)
    uri = "file:%s?mode=ro" % os.path.abspath(path).replace("\\", "/")
    try:
        con = sqlite3.connect(uri, uri=True, timeout=10)
        con.row_factory = sqlite3.Row
        con.execute("SELECT 1 FROM cross_section LIMIT 1")
    except Exception as exc:
        raise SectionError("BAD_DB", "打开/读取 cross_section 失败", path=path, reason=str(exc))
    return con


def db_station_info(con: Any, code: str) -> Dict[str, Any]:
    name = river = None
    try:
        row = con.execute("SELECT station_code, station_name, river_name FROM stations"
                          " WHERE station_code=?", (code,)).fetchone()
        if row is not None:
            name = row["station_name"]
            river = row["river_name"]
    except Exception as exc:
        # stations 表缺失/损坏 vs 站码不存在：下游（label/图例）都需要知道，别静默降级
        warn_exc("DB_STATION_INFO_FAILED", exc, station=str(code))
    label = str(code) + ((" " + str(name)) if name else "")
    return {"code": str(code), "name": name, "river": river, "label": label}


def db_list_dates(con: Any, code: str) -> List[Dict[str, Any]]:
    """列出该站全部施测日期（含点数/范围）。cross_section 无索引但单站全扫 ~100 ms。"""
    sql = ("SELECT meas_date, COUNT(*) AS n, MIN(distance) AS dmin, MAX(distance) AS dmax,"
           " MIN(elevation) AS emin, MAX(elevation) AS emax FROM cross_section"
           " WHERE station_code=? GROUP BY meas_date ORDER BY meas_date")
    out: List[Dict[str, Any]] = []
    try:
        rows = con.execute(sql, (code,)).fetchall()
    except Exception as exc:
        raise SectionError("BAD_DB", "查询 cross_section 失败", reason=str(exc))
    for r in rows:
        out.append({"date": str(r["meas_date"]), "points": int(r["n"]),
                    "offsetMin": r["dmin"], "offsetMax": r["dmax"],
                    "elevMin": r["emin"], "elevMax": r["emax"]})
    return out


def parse_years(text: str) -> set:
    out = set()
    for part in re.split(r"[,;，；\s]+", str(text).strip()):
        if not part:
            continue
        m = re.fullmatch(r"(\d{4})\s*[-~至]\s*(\d{4})", part)
        if m:
            a, b = int(m.group(1)), int(m.group(2))
            if a > b:
                a, b = b, a
            out.update(range(a, b + 1))
            continue
        m = re.fullmatch(r"(\d{4})", part)
        if m:
            out.add(int(m.group(1)))
            continue
        raise SectionError("BAD_ARGS", "--years 需形如 2024-2025 或 2024", years=text)
    if not out:
        raise SectionError("BAD_ARGS", "--years 为空", years=text)
    return out


def select_db_dates(dates: List[Dict[str, Any]], date: Optional[str], years: Optional[str],
                    last: Optional[int]) -> List[Dict[str, Any]]:
    """按 --date / --years / --last 选施测日期；默认取最近 1 次。兼容 'YYYY' 形态。"""
    if not dates:
        raise SectionError("BAD_DATA", "该站没有任何大断面施测记录")
    if date:
        want = str(date).strip().replace("/", "-")
        if re.fullmatch(r"\d{4}", want):   # 只给年份 -> 该年全部
            sel = [d for d in dates if d["date"].startswith(want)]
        else:
            sel = [d for d in dates if d["date"] == want]
            if not sel:   # 兼容 'YYYY-M-D' / 只有年份的老记录前缀匹配
                sel = [d for d in dates if d["date"].startswith(want)]
            if not sel:
                sel = [d for d in dates if want.startswith(d["date"])]
        if not sel:
            raise SectionError("BAD_DATA", "找不到指定施测日期", date=date,
                               available=[d["date"] for d in dates][-12:])
        return sel
    if years:
        ys = parse_years(years)
        sel = [d for d in dates if str(d["date"])[:4].isdigit()
               and int(str(d["date"])[:4]) in ys]
        if not sel:
            raise SectionError("BAD_DATA", "--years 无匹配施测日期", years=years,
                               available=[d["date"] for d in dates][-12:])
        return sel
    if last is not None:
        n = int(last)
        if n <= 0:
            raise SectionError("BAD_ARGS", "--last 必须 >= 1", last=last)
        return dates[-n:]
    return dates[-1:]


def db_fetch_series(con: Any, code: str, date: str) -> Dict[str, Any]:
    """取一次施测的全部测点，**保持 vert_num 顺序**（不按起点距重排）。"""
    sql = ("SELECT vert_num, distance, elevation, source_file, section_name, conflict_flag"
           " FROM cross_section WHERE station_code=? AND meas_date=? ORDER BY vert_num")
    rows = con.execute(sql, (code, date)).fetchall()
    pts: List[List[float]] = []
    verts: List[Optional[int]] = []
    files, names = set(), set()
    conflicts = 0
    skipped = 0
    bad_flags = 0
    for r in rows:
        if r["distance"] is None or r["elevation"] is None:
            skipped += 1
            continue
        pts.append([float(r["distance"]), float(r["elevation"])])
        verts.append(int(r["vert_num"]) if r["vert_num"] is not None else None)
        if r["source_file"]:
            files.add(str(r["source_file"]))
        if r["section_name"]:
            names.add(str(r["section_name"]))
        try:
            conflicts += int(r["conflict_flag"] or 0)
        except (TypeError, ValueError):
            bad_flags += 1
    if bad_flags:
        # 逐行打会刷屏：聚合成一行（conflict_flag 非整数按 0 计，如实报告行数）
        warn("CONFLICT_FLAG_INVALID", "conflict_flag 不是整数，已按 0 计", rows=bad_flags)
    return {"date": str(date), "points": pts, "verts": verts,
            "sourceFiles": sorted(files), "sectionNames": sorted(names),
            "conflictCount": conflicts, "rowCount": len(rows), "skippedRows": skipped}


def db_water_level(con: Any, code: str, date: str) -> Optional[float]:
    try:
        row = con.execute("SELECT value FROM daily_water_level"
                          " WHERE station_code=? AND date=?", (code, date)).fetchone()
    except Exception as exc:
        # daily_water_level 缺失/损坏 与「该日无记录」必须可区分（前者告警，后者静默返回 None）
        warn_exc("DB_WATER_LEVEL_FAILED", exc, station=str(code), date=str(date))
        return None
    if row is None or row["value"] is None:
        return None
    return float(row["value"])


def _series_id(date_str: Any, idx: int) -> str:
    s = re.sub(r"[^A-Za-z0-9_-]+", "-", str(date_str)).strip("-")
    return s or ("s%d" % (idx + 1))


def _series_stats(points: List[List[float]],
                  verts: List[Optional[int]]) -> Dict[str, Any]:
    offs = [p[0] for p in points]
    elevs = [p[1] for p in points]
    mono = all(offs[k] <= offs[k + 1] + 1e-12 for k in range(len(offs) - 1))
    dups = sum(1 for k in range(len(offs) - 1)
               if abs(offs[k + 1] - offs[k]) <= DUPLICATE_OFFSET_TOL)
    med = statistics.median(elevs) if elevs else 0.0
    outliers = []
    for k, e in enumerate(elevs):
        if abs(e - med) > SUSPECT_OUT_OF_RANGE_M:
            outliers.append({"vert": verts[k] if k < len(verts) else None,
                             "offset": offs[k], "elev": e,
                             "reasons": ["ELEV_OUT_OF_RANGE"]})
    return {"pointCount": len(points),
            "offsetMin": min(offs) if offs else None,
            "offsetMax": max(offs) if offs else None,
            "elevMin": min(elevs) if elevs else None,
            "elevMax": max(elevs) if elevs else None,
            "medianElev": med, "vertOrderMonotonic": mono,
            "duplicateOffsets": dups, "outliers": outliers}


def build_db_model(args: argparse.Namespace,
                   water_levels: List[Tuple[float, str]]) -> Dict[str, Any]:
    """从 hydro_data.db cross_section 出 schema v2 多序列模型（多序列共用坐标映射）。"""
    con = open_db(args.db)
    try:
        info = db_station_info(con, args.from_db)
        dates = db_list_dates(con, args.from_db)
        chosen = select_db_dates(dates, args.date, args.years, args.last)
        raw = [db_fetch_series(con, args.from_db, d["date"]) for d in chosen]
        waters: Dict[str, Optional[float]] = {}
        if not args.no_water_from_db and not water_levels:
            for s in raw:
                waters[s["date"]] = db_water_level(con, args.from_db, s["date"])
    finally:
        try:
            con.close()
        except Exception as exc:
            warn_exc("DB_CLOSE_FAILED", exc, db=os.path.abspath(args.db))

    for s in raw:
        if len(s["points"]) < 3:
            raise SectionError("BAD_DATA", "某个施测序列有效点不足 3 个",
                               date=s["date"], pointCount=len(s["points"]))
    all_pts = [p for s in raw for p in s["points"]]
    offs = [p[0] for p in all_pts]
    elevs = [p[1] for p in all_pts]
    offset_min, offset_max = min(offs), max(offs)
    elev_min, elev_max = min(elevs), max(elevs)
    if elev_max - elev_min <= 0.0:
        raise SectionError("BAD_DATA", "高程全等，无法成图",
                           elevMin=elev_min, elevMax=elev_max)

    paper = resolve_paper(args.paper)
    h_scale, auto_scaled, warns = choose_h_scale(
        float(args.h_scale), offset_max - offset_min, paper["usableWidthMm"],
        bool(args.no_auto_scale))
    v_scale = float(args.v_scale)
    # §4.2 坐标映射：与 v1 build_model 共用的单一实现
    mapper = CoordMapper(offset_min, elev_min, h_scale, v_scale, args.origin_xy)
    elev_base = mapper.elevBase
    ox, oy = mapper.ox, mapper.oy
    bbox, width_mm, height_mm = measure_bbox(mapper.map_all(all_pts))

    prefix = resolve_layer_prefix(args.layer_prefix)

    warnings: List[Dict[str, Any]] = list(warns)
    series: List[Dict[str, Any]] = []
    dq_series: List[Dict[str, Any]] = []
    any_water = False
    any_non_mono = False
    for i, s in enumerate(raw):
        sid = _series_id(s["date"], i)
        color = SERIES_PALETTE[i % len(SERIES_PALETTE)]
        if water_levels:
            wl = [{"value": float(v), "label": lb} for v, lb in water_levels]
        elif args.no_water_from_db:
            wl = []
        else:
            wv = waters.get(s["date"])
            wl = [{"value": wv, "label": DB_WATER_LABEL}] if wv is not None else []
        any_water = any_water or bool(wl)
        st = _series_stats(s["points"], s["verts"])
        any_non_mono = any_non_mono or not st["vertOrderMonotonic"]
        series.append({
            "id": sid,
            "label": s["date"],
            "date": s["date"],
            "station": info["label"],
            "points": s["points"],
            "waterLevels": wl,
            "style": {"color": color, "linetype": "CONTINUOUS"},
            "color": color,
            "linetype": "CONTINUOUS",
            "pointCount": len(s["points"]),
        })
        dq_series.append({
            "id": sid, "date": s["date"], "pointCount": st["pointCount"],
            "offsetMin": st["offsetMin"], "offsetMax": st["offsetMax"],
            "elevMin": st["elevMin"], "elevMax": st["elevMax"],
            "medianElev": st["medianElev"],
            "vertOrderMonotonic": st["vertOrderMonotonic"],
            "duplicateOffsets": st["duplicateOffsets"],
            "conflictFlags": s["conflictCount"],
            "outliers": st["outliers"],
            "sourceFiles": s["sourceFiles"][:5],
            "sectionNames": s["sectionNames"][:3],
        })

    suspects = [dict(o, series=d["id"]) for d in dq_series for o in d["outliers"]]
    dropped = 0
    if args.drop_suspect and suspects:
        # 先建索引再剔除：旧实现每个点都 next() 找一次本序列离群集 + any() 扫全部离群点，
        # 复杂度 O(点数 x 离群数 x 序列数)；改成「序列 id -> 离群 vert 集合」后整体 O(点数)。
        # setdefault 与旧 next(...) 语义一致：同 id 只取第一条（id 理论上不重复，防御性保留）。
        outliers_by_id: Dict[str, List[Dict[str, Any]]] = {}
        for d in dq_series:
            outliers_by_id.setdefault(d["id"], d["outliers"])
        for s, ser in zip(raw, series):
            drop_verts = {o["vert"] for o in outliers_by_id.get(ser["id"], [])}
            keep = [(p, v) for p, v in zip(s["points"], s["verts"]) if v not in drop_verts]
            if len(keep) != len(ser["points"]):
                dropped += len(ser["points"]) - len(keep)
                ser["points"] = [p for p, _ in keep]
                ser["pointCount"] = len(ser["points"])
        if dropped:
            warnings.append({"code": "SUSPECT_DROPPED",
                             "message": "已剔除 %d 个高程离群点（DB 源）" % dropped})
    if suspects and not dropped:
        warnings.append({"code": "SUSPECT_DATA",
                         "message": "检出 %d 个高程离群点（|高程-序列中位数|>%g m），"
                                    "已保留在 series 中" % (len(suspects),
                                                            SUSPECT_OUT_OF_RANGE_M),
                         "count": len(suspects)})

    if len(series) > 1:
        warnings.append({
            "code": "MULTIPLE_SERIES",
            "message": "多序列套绘：%d 个施测序列，共用 elevBase=%g / xRange=[%g, %g]"
                       % (len(series), elev_base, offset_min, offset_max),
            "seriesCount": len(series),
            "dates": [s["date"] for s in raw],
        })
    if len(series) > 20:
        warnings.append({"code": "SERIES_MANY",
                         "message": "序列数 %d 较多，图面可能拥挤（建议 --last/--years 收窄）"
                                    % len(series)})
    if any_water:
        if water_levels:
            warnings.append({"code": "WATER_EXPLICIT",
                             "message": "水位线来自 --water（对所有序列同值）"})
        else:
            warnings.append({"code": "WATER_FROM_DB",
                             "message": "水位线取自 daily_water_level 日均水位"
                                        "（非测时水位；--no-water-from-db 可关）"})
    else:
        warnings.append({"code": "NO_WATER", "message": "没有可用水位线（DB 无日均水位记录）"})
    if any_non_mono:
        warnings.append({"code": "SERIES_DIST_NON_MONOTONIC",
                         "message": "有序列的起点距沿 vert_num 非单调；按真实顺序保留、未重排"
                                    "（重排会破坏断面形态）"})
    if any(d["duplicateOffsets"] for d in dq_series):
        warnings.append({"code": "DUPLICATE_OFFSET",
                         "message": "有序列存在重复起点距（已全部保留，制图会形成竖直段）"})
    append_too_tall_warning(warnings, height_mm, paper["usableHeightMm"])

    params = {
        "hScale": h_scale,
        "vScale": v_scale,
        "paper": paper["name"],
        "origin": [ox, oy],
        "labelEvery": None,
        "layerPrefix": prefix,
        "noAxis": bool(args.no_axis),
        "noPointNo": len(series) > 1 or bool(args.no_point_no),
        "pointNo": (len(series) == 1) and not bool(args.no_point_no),
        "scaleBar": False,
        "noLegend": False,
        "textHeight": TEXT_HEIGHT_MM,
        "waterTextHeight": WATER_TEXT_HEIGHT_MM,
    }

    model: Dict[str, Any] = {
        "ok": True,
        "schema": SCHEMA_MODEL_V2,
        "design": "DESIGN-M1.md 4.2/4.3 + SCHEMA-v2（多序列）",
        "source": {
            "mode": "db",
            "db": os.path.abspath(args.db),
            "table": "cross_section",
            "reader": "sqlite3 read-only URI",
            "station": info["code"],
            "stationName": info["name"],
            "river": info["river"],
            "stationLabel": info["label"],
            "selection": {"date": args.date, "years": args.years, "last": args.last},
            "dates": [{"date": s["date"], "pointCount": len(s["points"]),
                       "verts": [s["verts"][0], s["verts"][-1]] if s["verts"] else [],
                       "rowCount": s["rowCount"], "skippedRows": s["skippedRows"],
                       "conflictFlags": s["conflictCount"]} for s in raw],
            "orderedBy": "vert_num（真实测点顺序，未按起点距重排）",
        },
        "params": params,
        "series": series,
        "computed": {
            "bbox": {k: round(v, 9) for k, v in bbox.items()},
            "elevBase": round(elev_base, 9),
            "xRange": [offset_min, offset_max],
            "seriesCount": len(series),
            "pointCounts": [len(s["points"]) for s in series],
            "scale": {"horizontal": h_scale, "vertical": v_scale},
            "widthMm": round(width_mm, 9),
            "heightMm": round(height_mm, 9),
            "width_mm": round(width_mm, 9),
            "height_mm": round(height_mm, 9),
            "offsetMin": offset_min,
            "offsetMax": offset_max,
            "elevMin": elev_min,
            "elevMax": elev_max,
            "spanM": offset_max - offset_min,
            "autoScaled": auto_scaled,
            "autoScaledFrom": float(args.h_scale) if auto_scaled else None,
            "autoScaledTo": h_scale if auto_scaled else None,
            "orderReversed": False,
            "vertOrderPreserved": True,
            "fitWidth": width_mm <= paper["usableWidthMm"] + 1e-9,
            "fitHeight": height_mm <= paper["usableHeightMm"] + 1e-9,
            "paperUsable": {"widthMm": paper["usableWidthMm"],
                            "heightMm": paper["usableHeightMm"]},
        },
        "dataQuality": {
            "profile": "db",
            "source": "hydro_data.db:cross_section（只读）",
            "rules": {
                "ELEV_OUT_OF_RANGE": "|高程 - 本序列中位数| > %g m" % SUSPECT_OUT_OF_RANGE_M,
                "SERIES_DIST_NON_MONOTONIC": "起点距沿 vert_num 非单调（保留真实顺序，不重排）",
            },
            "series": dq_series,
            "suspectCount": len(suspects),
            "dropped": dropped,
            "suspect": suspects,
            "summary": [{"id": d["id"], "date": d["date"], "points": d["pointCount"],
                         "offsetMin": d["offsetMin"], "offsetMax": d["offsetMax"],
                         "elevMin": d["elevMin"], "elevMax": d["elevMax"]}
                        for d in dq_series],
        },
        "warnings": warnings,
    }
    return model


def compare_db_vs_excel(db_series: List[Dict[str, Any]],
                        xls_blocks: List[Dict[str, Any]],
                        tol: float = 0.005) -> Dict[str, Any]:
    """同站同日期：DB（权威值）vs 源 xls（可能被截断）逐测点比对。"""
    by_date: List[Dict[str, Any]] = []
    differing: List[Dict[str, Any]] = []
    compared = same = 0

    def block_key(date: str) -> str:
        s = str(date)
        return _norm_date_key(s[5:]) if len(s) == 10 else _norm_date_key(s)

    for s in db_series:
        key = block_key(s["date"])
        hit = next((b for b in xls_blocks if b["dateKey"] == key), None)
        if hit is None:
            by_date.append({"date": s["date"], "matched": False,
                            "reason": "源 xls 无该日期块"})
            continue
        ents = [e for e in hit["entries"] if e["kind"] in ("number", "label")]
        n = min(len(ents), len(s["points"]))
        trunc = sum(1 for e in ents
                    if isinstance(e.get("elevRaw"), str)
                    and ELEV_TRUNC_RE.match(e["elevRaw"].strip()))
        date_diff: List[Dict[str, Any]] = []
        date_same = 0
        off_mismatch = 0
        for k in range(n):
            e = ents[k]
            db_elev = s["points"][k][1]
            db_off = s["points"][k][0]
            if e.get("elev") is None or e.get("offset") is None:
                continue
            if abs(db_off - e["offset"]) > 1e-6:
                off_mismatch += 1
            delta = db_elev - e["elev"]
            if abs(delta) <= tol:
                date_same += 1
            else:
                date_diff.append({
                    "date": s["date"], "vert": k + 1,
                    "dbOffset": db_off, "xlsOffset": e["offset"],
                    "dbElev": round(db_elev, 4), "xlsElev": round(e["elev"], 4),
                    "delta": round(delta, 4),
                    "xlsRaw": e.get("elevRaw"),
                    "xlsRawIsTruncated": isinstance(e.get("elevRaw"), str)
                    and bool(ELEV_TRUNC_RE.match(str(e["elevRaw"]).strip())),
                })
        compared += n
        same += date_same
        differing.extend(date_diff)
        by_date.append({
            "date": s["date"], "matched": True, "xlsBlock": hit["index"],
            "xlsDate": hit["date"], "xlsWaterLevel": hit.get("waterLevel"),
            "dbPointCount": len(s["points"]),
            "xlsEntryCount": len(ents),
            "xlsNumberedCount": hit.get("numbered"),
            "xlsLabelRows": hit.get("labels"),
            "compared": n, "same": date_same, "differing": len(date_diff),
            "offsetMismatch": off_mismatch,
            "xlsTruncatedCells": trunc,
        })

    return {
        "dbPoints": sum(len(s["points"]) for s in db_series),
        "compared": compared,
        "same": same,
        "differingCount": len(differing),
        "tolerance": tol,
        "byDate": by_date,
        "differing": differing,
        "note": "DB 值取自 cross_section（原始表值）；xls 值取自源 .xls 单元格原文"
                "（部分被截断成无小数点短文本，如 '28' 实为 28.28）。"
                "DB 路径天然包含左岸/右岸行（每序列首末），xls 路径默认过滤标签行，"
                "故按 vert 顺序 1:1 对齐（含标签行）。",
    }


def _series_summary(points: List[List[float]], label: Any) -> Dict[str, Any]:
    offs = [p[0] for p in points]
    elevs = [p[1] for p in points]
    return {"label": label, "points": len(points),
            "offsetMin": min(offs) if offs else None,
            "offsetMax": max(offs) if offs else None,
            "elevMin": min(elevs) if elevs else None,
            "elevMax": max(elevs) if elevs else None}
