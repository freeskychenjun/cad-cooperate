#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""prepare.py -- 断面图「纯计算侧」：xlsx -> section.model.json（零 COM，绝不启动 CAD）。

设计依据：DESIGN-M1.md
  §4.1 输入（Excel：第 1 列起点距 / 第 2 列高程；首行表头自动跳过；>=3 点、单调递增、无空值）
  §4.2 坐标映射（纸面 mm，模型空间 1 单位 = 1 mm）
        x_mm = (offset_m - offset_min) * 1000 / hScale
        y_mm = (elev_m  - elev_base)  * 1000 / vScale
        elev_base = elev_min - (10 * vScale / 1000)          # 最低点上方留 10 mm 底边距
        bbox 宽度 = (offset_max - offset_min) * 1000 / hScale
        A3 可用绘图区 ~ 400x277 mm；超宽把 hScale 按整档放大 500 -> 1000 -> 2000 并告警
  §4.3 绘制元素（地面线/水位线/水位文字/点高程/点号/基线+左竖线/深泓标记）

**输入 profile**（2026-11-04 增补，真实数据接入）：
  - `two-col`（默认）：两列「起点距 / 高程」，行为见 §4.1，保持不变；
  - `yssd`：**实测大断面成果表**（水利行业表式）。`--profile yssd` 或 auto 自动识别
    （表头行里「起点距」「高程」各出现 >=2 次即判为 yssd）。特征：
      * 表头 `垂线号 | 起点距 | 河底高程` **重复 N 组**（3 列一组，N>=2）；
      * 数据**按列块自上而下填充**（第 1 组点号 1..k，第 2 组 k+1..2k…），必须按组
        降维成一维序列后再按起点距排序 —— 逐行读会得到交错乱序；
      * 一个文件可含**多个施测块**（`施测日期： 1月19日 … 测时水位： 17.85`），
        用 `--block N` 或 `--date 9-18` 选（默认第 1 块），全部块清单写进 `source.blocks`；
      * 允许 `左岸`/`右岸` 标签行与负起点距（左岸）；`附注/备注/说明` 行忽略；
      * 默认把该块「测时水位」自动作为水位线（`--no-auto-water` 关）；
      * **数据质量闸门**：高程单元格被截断成无小数点短文本（如 `5.73` 变成 `73`、
        `28.24` 变成 `28`）等质量缺陷 -> `dataQuality.suspect[]` 如实报告，默认保留
        并告警，`--drop-suspect` 剔除，`--fail-on-suspect` 直接失败；
      * 起点距重复 -> `warnings:DUPLICATE_OFFSET`；原始顺序非单调 -> `warnings:NON_MONOTONIC`。
  `.xls` 用 xlrd（`tools\\py-libs` 加进 PYTHONPATH），`.xlsx/.xlsm` 用 openpyxl。

**数据源三选一**（2026-11-04 增补，task-11）：
  - `--data/--from-excel` + two-col / yssd：Excel 路径，输出 **schema v1**（单序列，含 entities[]，行为不变）；
  - `--from-db <站码>`：从 `hydro_data.db` 的 `cross_section` 出断面，输出 **schema v2**（多序列）：
      * `--date 2025-01-19` / `--years 2024-2025` / `--last N` 选施测日期，默认最近 1 次；
      * **按 vert_num 顺序**保留测点（不按起点距重排，否则破坏断面形态）；DB 天然含左岸/右岸行；
      * 多序列**共用** elevBase / xRange（取全体 min/max），否则叠图对不齐；
      * 水位线取 `daily_water_level` 日均水位（label「日均水位」；`--water` 覆盖 / `--no-water-from-db` 关）；
      * `--list-dates` 列可选日期；`--summary` 输出每序列统计；
      * `--compare-source`（配 `--from-excel`）：DB vs 源 .xls 逐点比对 → `sourceCompare{}`，
        用于证明「xls 高程被截断、DB 为正确值」。
  - v2 模型**不含 entities[]/expected**（helper 按 params+series 自行计算绘制）。

输出 section.model.json 的字段（消费侧契约 = core-cad helper，2026-11-04 冻结）：
  v1 单序列（two-col / yssd）：
  ok / schema("zwcad-section-model/1") /
  points[[offset_m, elev_m], ...]（主形态）/ pointsDetail[{i,offset,elev}] /
  waterLevels[{value,label,y,x0,x1,text}] / params{hScale,vScale,paper,origin,labelEvery,
  noAxis,noPointNo,noAutoScale,layerPrefix,textHeight,waterTextHeight} /
  source{file,sheet,rows,profile,...} / scale{horizontal,vertical} / paper / paperSize /
  origin[x,y] / labels{} / style{} / layerPrefix / layers{} /
  entities[]（按 §4.2/§4.3 算好的可直接绘制实体清单，供我侧验收/对账） /
  expected{total,byType,byKind,byLayer,textCount} /
  computed{widthMm,heightMm,bbox{x0,y0,x1,y1},extent,elevBase,autoScaled,orderReversed,...} /
  dataQuality{profile,suspect[],dropped,duplicateOffsets,ignoredRows,blocks,selectedBlock}（yssd 才有）/
  warnings[{code,message}]
  v2 多序列（--from-db）：
  ok / schema("zwcad-section-model/2") / source{mode:"db",station,dates,...} /
  params{hScale,vScale,paper,origin,labelEvery:null,layerPrefix,noAxis,noPointNo,pointNo,
         scaleBar,noLegend,textHeight,waterTextHeight} /
  series[{id,label,date,station,points[[off,elev]],waterLevels[{value,label}],
          style{color,linetype},color,linetype,pointCount}] /
  computed{bbox,elevBase,xRange,seriesCount,pointCounts,scale,widthMm,heightMm,...} /
  dataQuality{profile:"db",series[],suspect[],dropped} / sourceCompare{...}（对比模式）/
  warnings[{code,message}]

用法示例：
  python scripts\\section\\prepare.py --data tests\\fixtures\\section-30.xlsx ^
      --out tmp\\section-30.model.json --water "42.30=设计水位"
  python scripts\\section\\prepare.py --profile yssd --data 实测大断面成果表.xls ^
      --out tmp\\real\\x.model.json --paper A1 --h-scale 4000 --v-scale 200
  python scripts\\section\\prepare.py --from-db <站码> --date 2025-01-19 ^
      --out tmp\\real\\db-2025-01-19.model.json --paper A1 --h-scale 4000 --v-scale 200
  python scripts\\section\\prepare.py --from-db <站码> --years 2024-2025 ^
      --out tmp\\real\\db-overlay.<站码>.model.json --paper A1 --h-scale 4000 --v-scale 200
  python scripts\\section\\prepare.py --from-db <站码> --date 2025-01-19 --compare-source ^
      --from-excel <xls> --out tmp\\real\\db-vs-xls.model.json
  python scripts\\section\\prepare.py --from-db <站码> --list-dates

退出码：0 成功（写文件）；2 任何失败（stdout 打单段 JSON {"ok":false,...}，**不写输出文件**）。
stdout 始终为单段 ASCII 安全 JSON（非 ASCII 走 \\u 转义），便于 PS 5.1 的 ConvertFrom-Json 解析；
落盘文件为 UTF-8(无 BOM) + 中文原文。
非致命内部失败（DB 表缺失/损坏、工作簿关闭失败、临时文件清理失败、conflict_flag 非法等）走
**stderr** 一行 `[prepare] warn <CODE>: <ExcType>: <msg>`；同一 (code, message) 只打一次，
stdout 的 JSON 协议完全不变（stdout/stderr 分离，PS 侧仍可安全 ConvertFrom-Json）。

**代码布局**（2026-11-05 模块化：纯行为保持重构，本文件只剩 CLI 薄壳）：
  `prep_io.py`    Excel/xls 读取、单元格数值/表头解析、SectionError、stderr 告警、原子写
  `prep_yssd.py`  实测大断面成果表（yssd）表头/施测块/点列/数据质量闸门
  `prep_db.py`    hydro_data.db 只读访问 + schema v2 多序列模型（含序列统计与对比）
  `prep_model.py` §4.2 坐标映射（v1/v2 单一实现）、点列校验、图幅/比例、schema v1 模型
本文件保留 emit / SCHEMA_MODEL* / build_arg_parser / _run_db / main，并把上述模块的
公开名显式 re-export（`qa_yssd.py` 依赖 `import prepare` 后直接调用 `prepare.xxx`）。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Dict, List, Optional, Sequence, Tuple

# ----------------------------------------------------------------------------- 依赖（拆分后从各模块导入）
# 这里显式列出名字（不用 `from prep_io import *`）：
#   1) prepare.py 仍是 CLI 入口；2) qa_yssd.py 里的 `import prepare` + prepare.xxx 仍可用。
from prep_db import (DB_WATER_LABEL, SCHEMA_MODEL_V2, SERIES_PALETTE,
                     _series_id, _series_stats, _series_summary, build_db_model,
                     compare_db_vs_excel, db_fetch_series, db_list_dates,
                     db_station_info, db_water_level, default_db_path, open_db,
                     parse_years, select_db_dates)
from prep_io import (ELEV_ALIASES, OFFSET_ALIASES, SectionError, _WARNED, _find_header_row,
                     _header_matches, _is_blank, _load_rows_openpyxl, _load_rows_xlrd,
                     _match_alias, _norm_header, _soft_number, _to_number, load_rows,
                     parse_points, warn, warn_exc, write_json_atomic)
from prep_model import (H_SCALE_LADDER, PAPER_MARGIN_MM, PAPERS, SCHEMA_MODEL,
                        TEXT_HEIGHT_MM, WATER_TEXT_HEIGHT_MM, CoordMapper,
                        append_too_tall_warning, build_model, choose_h_scale,
                        measure_bbox, parse_origin, parse_water, resolve_layer_prefix,
                        resolve_paper, validate_points)
from prep_yssd import (DUPLICATE_OFFSET_TOL, ELEV_TRUNC_RE, PROFILE_AUTO,
                       PROFILE_TWO_COL, PROFILE_YSSD, PROFILES, SUSPECT_CROSS_BLOCK_M,
                       SUSPECT_OUT_OF_RANGE_M, YSSD_ELEV_KEYS, YSSD_META_MARKERS,
                       YSSD_OFFSET_KEYS, YSSD_POINT_KEYS, _block_meta,
                       _elev_looks_reliable, _norm_date_key, assess_data_quality,
                       build_yssd_model, detect_yssd_profile, entries_to_points,
                       find_yssd_header, parse_yssd_blocks, select_yssd_block)


def __getattr__(name: str) -> Any:
    """PEP 562：`prepare.DEFAULT_DB_PATH` 兼容访问 —— 委托 prep_db，**调用期**读 env。

    拆分前的 `DEFAULT_DB_PATH` 是导入期快照；现在从 prep_db 的 import 清单里去掉该名字，
    需要用旧名字的脚本仍可 `prepare.DEFAULT_DB_PATH`，但拿到的是**访问那一刻**的值。
    """
    if name == "DEFAULT_DB_PATH":
        return default_db_path()
    raise AttributeError(name)


# ----------------------------------------------------------------------------- 输出
def emit(payload: Dict[str, Any]) -> None:
    """stdout 单段 JSON（ASCII 安全，中文走 \\u 转义；PS 5.1 可安全 ConvertFrom-Json）。"""
    sys.stdout.write(json.dumps(payload, ensure_ascii=True, sort_keys=False,
                                separators=(",", ":")) + "\n")
    sys.stdout.flush()


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="prepare.py",
        description="Excel/hydro_data.db -> section.model.json (DESIGN-M1 4.2/4.3; schema v1 单序列 / v2 多序列)")
    p.add_argument("--data", default=None,
                   help="Excel 文件路径（.xls 走 xlrd / .xlsx 走 openpyxl）；等价 --from-excel")
    p.add_argument("--from-excel", dest="from_excel", default=None,
                   help="同 --data（显式写法；与 --from-db 组合时用于 --compare-source）")
    p.add_argument("--out", default=None, help="输出 section.model.json 路径（--list-dates 时可省）")
    p.add_argument("--sheet", default=None, help="工作表名或 1-based 序号（默认第一个）")
    p.add_argument("--cols", default=None, help="列名映射，形如 \"起点距=高程\"（two-col 专用）")
    p.add_argument("--profile", default=PROFILE_AUTO, choices=PROFILES,
                   help="输入格式：auto（默认，自动识别）/ two-col / yssd（实测大断面成果表）")
    # ---- 数据库（schema v2 多序列）
    p.add_argument("--from-db", dest="from_db", default=None, metavar="STATION",
                   help="从 hydro_data.db 的 cross_section 出断面（站码，如 <站码>）")
    p.add_argument("--db", default=None,
                   help="hydro_data.db 路径（默认取环境变量 HYDRO_DB）")
    p.add_argument("--date", default=None,
                   help="选施测日期：DB 用 2025-01-19 / 2025；yssd 用 9-18 / 9月18日（选源表块）")
    p.add_argument("--years", default=None,
                   help="DB：按年份范围选，如 2024-2025 或 2024")
    p.add_argument("--last", type=int, default=None,
                   help="DB：最近 N 次施测（如 --last 3）")
    p.add_argument("--compare-source", dest="compare_source", action="store_true",
                   help="DB vs 源 xls 逐点比对（需同时给 --from-db 与 --from-excel）")
    p.add_argument("--list-dates", dest="list_dates", action="store_true",
                   help="只列出该站可选施测日期（不产出模型）")
    p.add_argument("--no-water-from-db", dest="no_water_from_db", action="store_true",
                   help="DB：不从 daily_water_level 取日均水位")
    p.add_argument("--summary", action="store_true",
                   help="输出每序列的点数/起点距范围/高程范围（stdout + 模型 summary 字段）")
    # ---- yssd
    p.add_argument("--block", type=int, default=None,
                   help="yssd：选第 N 个施测块（1-based，默认第 1 块）")
    p.add_argument("--drop-suspect", dest="drop_suspect", action="store_true",
                   help="剔除可疑/离群高程点（默认保留并告警）")
    p.add_argument("--fail-on-suspect", dest="fail_on_suspect", action="store_true",
                   help="yssd：检出可疑点即失败（BAD_DATA，退出码 2，不产出文件）")
    p.add_argument("--keep-label-rows", dest="drop_label_rows", action="store_false",
                   help="yssd：保留 左岸/右岸 等标签行（默认过滤；这些行通常带岸顶坐标）")
    p.add_argument("--no-auto-water", dest="no_auto_water", action="store_true",
                   help="yssd：不从源表「测时水位」自动生成水位线")
    p.add_argument("--water-label", dest="water_label", default=None,
                   help="yssd 自动水位线的文字标签（默认 测时水位）")
    # ---- 通用
    p.add_argument("--h-scale", dest="h_scale", type=float, default=500.0,
                   help="横向比例 1:hScale（默认 500）")
    p.add_argument("--v-scale", dest="v_scale", type=float, default=50.0,
                   help="纵向比例 1:vScale（默认 50，即垂直放大 10 倍）")
    p.add_argument("--paper", default="A3", help="图幅 A0..A4 或 WxH（默认 A3）")
    p.add_argument("--origin", default="0,0", help="模型空间平移 x,y（mm，默认 0,0）")
    p.add_argument("--water", action="append", default=None,
                   help="水位 \"42.30=设计水位\"（可重复）")
    p.add_argument("--label-every", dest="label_every", type=int, default=1,
                   help="每 N 个点标一次高程（默认 1）")
    p.add_argument("--no-axis", dest="no_axis", action="store_true",
                   help="不画基线+左竖线")
    p.add_argument("--no-point-no", dest="no_point_no", action="store_true",
                   help="不画点号")
    p.add_argument("--no-auto-scale", dest="no_auto_scale", action="store_true",
                   help="禁用超宽自动升档（超宽则 TOO_WIDE）")
    p.add_argument("--layer-prefix", dest="layer_prefix", default="AI-SEC",
                   help="图层前缀（默认 AI-SEC）")
    p.set_defaults(drop_label_rows=True)
    return p


def _db_compare_series(args: argparse.Namespace) -> List[Dict[str, Any]]:
    """--compare-source 用的 DB 序列：取**删点前**的原始测点（与源表条目按点身份 1:1 对齐）。

    为什么单独取一次：`--drop-suspect` 会让 build_db_model 从 model["series"] 里删掉离群点，
    若比对仍用删点后的序列按下标对齐，删点位置之后的下标整体前移 -> 比对静默错位
    （compared/same/differing/byDate 全部失真）。比对是「数据审计」，应以原始数据为准；
    这里复用同一套选日期逻辑重取一次只读查询（仅在本组合下多一次 DB 打开，通常 <0.3 s）。
    """
    con = open_db(args.db)
    try:
        dates = select_db_dates(db_list_dates(con, args.from_db),
                                args.date, args.years, args.last)
        return [{"date": s["date"], "points": s["points"]}
                for s in (db_fetch_series(con, args.from_db, d["date"]) for d in dates)]
    finally:
        try:
            con.close()
        except Exception as exc:
            warn_exc("DB_CLOSE_FAILED", exc, db=os.path.abspath(args.db))


def _run_db(args: argparse.Namespace, water_levels: List[Tuple[float, str]],
            xls_path: Optional[str]) -> int:
    """DB 路径：schema v2 多序列（--list-dates / --compare-source / --summary）。"""
    if args.list_dates:
        con = open_db(args.db)
        try:
            info = db_station_info(con, args.from_db)
            dates = db_list_dates(con, args.from_db)
        finally:
            try:
                con.close()
            except Exception as exc:
                warn_exc("DB_CLOSE_FAILED", exc, db=os.path.abspath(args.db))
        payload = {"ok": True, "cmd": "list-dates",
                   "station": info["code"], "stationName": info["name"],
                   "river": info["river"], "db": os.path.abspath(args.db),
                   "count": len(dates), "dates": dates}
        if args.out:
            write_json_atomic(args.out, payload)
            payload["out"] = os.path.abspath(args.out)
        emit(payload)
        return 0

    if not args.out:
        raise SectionError("BAD_ARGS", "缺 --out（输出 section.model.json）")
    model = build_db_model(args, water_levels)

    if args.compare_source:
        if not xls_path:
            raise SectionError("BAD_ARGS",
                               "--compare-source 需同时给 --from-excel <源表路径>")
        rows, _meta = load_rows(xls_path, args.sheet)
        header = find_yssd_header(rows)
        if header is None:
            raise SectionError("BAD_DATA", "比对源表未识别到实测大断面表头", path=xls_path)
        blocks = parse_yssd_blocks(rows, header[0], header[1])
        # --drop-suspect 会从 series 里删点；比对必须用删点前的原始序列（否则下标前移、静默错位）。
        # 不带 --drop-suspect 时保持原路径（模型 series 即原始序列，行为零变化）。
        if args.drop_suspect:
            db_series = _db_compare_series(args)
        else:
            db_series = [{"date": s["date"], "points": s["points"]} for s in model["series"]]
        cmp = compare_db_vs_excel(db_series, blocks)
        for row in cmp["byDate"]:
            ser = next((s for s in model["series"] if s["date"] == row.get("date")), None)
            if ser is not None and ser.get("waterLevels"):
                row["dbWaterLevel"] = ser["waterLevels"][0]["value"]
        cmp["xlsPath"] = os.path.abspath(xls_path)
        model["sourceCompare"] = cmp
        # 源表里找不到的施测日期（byDate[].matched=false）不能计入「两者一致」：修前这种
        # 情形会误报 SOURCE_COMPARE_SAME（比对 0 点却称「两者一致」），现单独给 UNMATCHED。
        unmatched = sum(1 for row in cmp["byDate"] if not row.get("matched"))
        if cmp["differingCount"]:
            code = "SOURCE_COMPARE_DIFF"
            msg = ("DB vs 源 .xls：比对 %d 点，%d 处不同（源表高程被截断成无小数点短文本，"
                   "DB 为正确值）" % (cmp["compared"], cmp["differingCount"]))
        elif unmatched:
            code = "SOURCE_COMPARE_UNMATCHED"
            msg = ("DB vs 源 .xls：%d 个施测日期在源表里没有对应施测块，未做比对"
                   "（见 sourceCompare.byDate[].matched=false）" % unmatched)
        else:
            code = "SOURCE_COMPARE_SAME"
            msg = "DB vs 源 .xls：比对 %d 点，0 处不同（两者一致）" % cmp["compared"]
        model["warnings"].append({"code": code, "message": msg,
                                  "compared": cmp["compared"], "same": cmp["same"],
                                  "differingCount": cmp["differingCount"]})

    if args.summary:
        model["summary"] = model["dataQuality"]["summary"]

    write_json_atomic(args.out, model)
    comp = model["computed"]
    dq = model["dataQuality"]
    emit({
        "ok": True, "out": os.path.abspath(args.out),
        "schema": model["schema"], "mode": "db",
        "station": model["source"]["station"],
        "stationName": model["source"]["stationName"],
        "seriesCount": comp["seriesCount"],
        "pointCounts": comp["pointCounts"],
        "dates": [s["date"] for s in model["series"]],
        "scale": comp["scale"], "bbox": comp["bbox"],
        "widthMm": comp["widthMm"], "heightMm": comp["heightMm"],
        "elevBase": comp["elevBase"], "xRange": comp["xRange"],
        "autoScaled": comp["autoScaled"],
        "suspectCount": dq.get("suspectCount"), "suspectDropped": dq.get("dropped"),
        "sourceCompare": {"compared": model["sourceCompare"]["compared"],
                          "same": model["sourceCompare"]["same"],
                          "differingCount": model["sourceCompare"]["differingCount"]}
        if model.get("sourceCompare") else None,
        "summary": model.get("summary"),
        "warnings": [w["code"] for w in model["warnings"]],
    })
    return 0


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_arg_parser()
    args = parser.parse_args(argv)
    try:
        # --db 的默认值不再在 argparse 定义期快照 env（argparse 默认 None）：这里统一解析一次。
        # 必须用 `is not None` 判定 —— 显式 `--db ""` 的旧行为是 NO_DB，不能被当成「回退 env」。
        args.db = args.db if args.db is not None else default_db_path()
        args.origin_xy = parse_origin(args.origin)
        water_levels = parse_water(args.water)
        if float(args.h_scale) <= 0 or float(args.v_scale) <= 0:
            raise SectionError("BAD_ARGS", "比例必须为正数",
                               hScale=args.h_scale, vScale=args.v_scale)

        data_path = args.from_excel or args.data
        if args.from_db:
            if data_path and not args.compare_source:
                raise SectionError(
                    "BAD_ARGS",
                    "--from-db 与 --data/--from-excel 二选一（要比对请加 --compare-source）")
            return _run_db(args, water_levels, data_path)
        if args.list_dates:
            raise SectionError("BAD_ARGS", "--list-dates 需配 --from-db <站码>")
        if not data_path:
            raise SectionError("BAD_ARGS", "缺输入：给 --from-db <站码> 或 --data/--from-excel <文件>")
        if not args.out:
            raise SectionError("BAD_ARGS", "缺 --out（输出 section.model.json）")
        args.data = data_path

        rows, meta = load_rows(args.data, args.sheet)
        profile = args.profile
        auto_profile = False
        if profile == PROFILE_AUTO:
            profile = PROFILE_YSSD if detect_yssd_profile(rows) else PROFILE_TWO_COL
            auto_profile = True
        meta["profile"] = profile
        meta["profileAuto"] = auto_profile

        if profile == PROFILE_YSSD:
            model = build_yssd_model(rows, meta, args, water_levels)
        else:
            points = parse_points(rows, meta, args.cols)
            points, order_reversed = validate_points(points)
            model = build_model(points, order_reversed, args, meta, water_levels)

        if args.summary:
            model["summary"] = [_series_summary(model["points"],
                                                model["source"].get("date") or "series")]

        write_json_atomic(args.out, model)
        dq = model.get("dataQuality") or {}
        emit({
            "ok": True,
            "out": os.path.abspath(args.out),
            "schema": model["schema"],
            "profile": model["source"].get("profile", PROFILE_TWO_COL),
            "pointCount": len(model["points"]),
            "total": model["expected"]["total"],
            "byType": model["expected"]["byType"],
            "byLayer": model["expected"]["byLayer"],
            "scale": model["scale"],
            "bbox": model["computed"]["bbox"],
            "widthMm": model["computed"]["widthMm"],
            "heightMm": model["computed"]["heightMm"],
            "elevBase": model["computed"]["elevBase"],
            "autoScaled": model["computed"]["autoScaled"],
            "orderReversed": model["computed"]["orderReversed"],
            "block": model["source"].get("block"),
            "date": model["source"].get("date"),
            "suspectCount": dq.get("suspectCount"),
            "suspectDropped": dq.get("dropped"),
            "summary": model.get("summary"),
            "warnings": [w["code"] for w in model["warnings"]],
        })
        return 0
    except SectionError as exc:
        emit(exc.to_json())
        return 2
    except Exception as exc:  # pragma: no cover - 兜底，绝不静默
        emit({"ok": False, "error": {"code": "INTERNAL", "message": str(exc),
                                     "type": type(exc).__name__}})
        return 2


if __name__ == "__main__":
    sys.exit(main())
