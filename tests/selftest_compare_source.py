#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""selftest_compare_source.py -- `prepare.py --compare-source`（DB vs 源 .xls 逐点比对）纯离线自测。

零 COM、零 CAD、零真实数据：仓库里没有与真实 `hydro_data.db` 施测日期匹配的源 `.xls`，
所以夹具全部**合成**（到 ``tmp/selftest-compare-source/``，``.gitignore`` 只忽略仓库 ``/tmp/``，
夹具永不入库）：
  * ``fixture.db``：按 ``hydro_data.db`` 的真实表结构（``stations`` / ``cross_section`` /
    ``daily_water_level``）造的小库，站码用**虚构值** ``99000001``；
  * ``source-yssd.xlsx``：实测大断面成果表形态（表头「垂线号|起点距|河底高程」×3 组、
    每块一行 施测日期/测时水位、按列块自上而下填充），含刻意制造的一致点、少量偏差点、
    被截断的高程文本（``9.36`` 之外再放 ``88`` / ``75`` 这种无小数点短文本）。

覆盖：
  1  主体一致：``differingCount=0`` -> ``SOURCE_COMPARE_SAME``，各计数/字段对齐
  2  少量差异 + 截断：``differing[]`` 的 vert/dbElev/xlsElev/delta/xlsRawIsTruncated 逐项
  3  截断 + 起点距不符：``offsetMismatch`` 与 ``differing`` 互不污染（高程相同不算不同）
  4  多序列：``--last 2``（2 个都匹配）与 ``--last 3``（含 1 个源表没有的日期 ->
     ``byDate[].matched=False``）
  5  ``--water-from-db`` 关闭：``dbWaterLevel`` 缺省 + ``NO_WATER`` 告警
  6  负例：缺 ``--from-excel`` -> ``BAD_ARGS``；坏 DB -> ``BAD_DB``；非 yssd 源表 ->
     ``BAD_DATA``；全部 exit 2 且**不产出文件**
  7  纯函数 ``prep_db.compare_db_vs_excel``：容差边界、日期键匹配（``YYYY-MM-DD`` -> ``M-D``）、
     无匹配日期、``min(ents, points)`` 截断
  8  stdout 协议：单段 ASCII JSON、两次运行逐字节相同、落盘 UTF-8 无 BOM

运行：python tests\\selftest_compare_source.py
退出码：0 全绿；1 有 FAIL（缺 openpyxl 时打印 SKIP 并 exit 0，与 run_m1 的 SKIP 口径一致）
"""

from __future__ import annotations

import json
import os
import shutil
import sqlite3
import subprocess
import sys
from typing import Any, Dict, List, Optional, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SEC = os.path.join(ROOT, "scripts", "section")
PREPARE = os.path.join(SEC, "prepare.py")
TMP = os.path.join(ROOT, "tmp", "selftest-compare-source")
PY = sys.executable or "python"

# CI runners default the console code page to cp1252 while fixture details below are Chinese:
# force UTF-8 with replacement instead of dying with UnicodeEncodeError.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 虚构站码/站名（绝不使用真实站码；本文件可入库）
STATION = "99000001"
STATION_NAME = "合成测试站"
RIVER_NAME = "合成河"

D_A = "2022-05-20"   # 主体一致（源表块 5月20日）
D_B = "2024-09-18"   # 少量差异 + 截断（源表块 9月18日）
D_C = "2025-01-19"   # 截断 + 起点距不符（源表块 1月19日）
D_X = "2023-06-01"   # 源表里没有这一批次 -> matched=False
D_S = "2021-03-15"   # 含 2 个高程离群点（|elev-median|>40 m）-> --drop-suspect 会删点

RESULTS: List[Tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append((name, bool(ok), detail))
    return bool(ok)


def env() -> Dict[str, str]:
    e = os.environ.copy()
    e["PYTHONUTF8"] = "1"
    e["PYTHONIOENCODING"] = "utf-8"
    return e


def run(args: List[str], timeout: int = 120) -> Tuple[int, bytes, bytes]:
    """跑 prepare.py；返回**原始字节**（stdout 协议要求 ASCII 单段 JSON，逐字节可断言）。"""
    p = subprocess.run([PY, PREPARE] + args, capture_output=True, cwd=ROOT, env=env(),
                       timeout=timeout)
    return p.returncode, p.stdout, p.stderr


def out_json(raw: bytes) -> Dict[str, Any]:
    text = raw.decode("utf-8", "replace")
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
    return {}


# ---------------------------------------------------------------------------
# fixtures（合成 DB + 合成 yssd 源表；golden 抓取脚本也 import 本函数，保证同一份夹具）
# ---------------------------------------------------------------------------
DB_SCHEMA = """
CREATE TABLE stations (
    station_code TEXT PRIMARY KEY, station_name TEXT, river_name TEXT, station_type TEXT,
    data_category TEXT, catchment_area REAL, unit TEXT, longitude REAL, latitude REAL,
    notes TEXT, location TEXT, water_system TEXT, data_items TEXT, establish_date TEXT
);
CREATE TABLE cross_section (
    station_code TEXT, meas_date TEXT, vert_num INTEGER, distance REAL, elevation REAL,
    source_file TEXT, section_name TEXT DEFAULT NULL, conflict_flag INTEGER DEFAULT 0
);
CREATE TABLE daily_water_level (
    station_code TEXT, date TEXT, value REAL, unit TEXT DEFAULT 'm', source_file TEXT,
    PRIMARY KEY (station_code, date)
);
"""

# 每个 DB 施测日期的测点（vert_num 顺序；与源表块的条目按 1:1 对齐）
DB_POINTS: Dict[str, List[Tuple[float, float]]] = {
    # 与块 A（5月20日）逐点一致
    D_A: [(-8.0, 11.90), (10.0, 10.80), (25.0, 9.95), (40.0, 10.30)],
    # 源表没有这一批次（日期不匹配用例）
    D_X: [(-6.0, 11.50), (8.0, 10.90), (22.0, 10.10), (36.0, 9.85), (50.0, 10.20)],
    # 与块 B（9月18日）比对：vert6 偏差 0.03（DB 9.33 / xls 9.36），vert7 源表截断成 "88"
    D_B: [(-12.5, 12.40), (0.0, 11.05), (15.0, 10.20), (30.0, 9.55), (45.0, 9.10),
          (60.0, 9.33), (75.0, 8.88), (90.0, 10.75)],
    # 与块 C（1月19日）比对：vert4 偏差 0.03，vert5 起点距 50 vs 51（高程一致），vert6 截断 "75"
    D_C: [(-10.0, 12.10), (5.0, 11.30), (20.0, 10.40), (35.0, 9.83), (50.0, 9.20),
          (65.0, 9.75), (80.0, 10.60)],
    # --drop-suspect 用例：vert2=65.0 / vert5=-35.0 相对中位 9.675 均 >40 m -> 会被删点；
    # 源表块（3月15日）与原始值逐点一致（不删点时比对应为 6/6/0）
    D_S: [(-9.0, 10.40), (5.0, 65.00), (20.0, 9.80), (35.0, 9.55), (50.0, -35.00),
          (65.0, 9.30)],
}

DB_WATER: Dict[str, float] = {D_A: 14.85, D_X: 11.10, D_B: 9.62, D_C: 10.05, D_S: 9.10}

# 源表块：三个列组，条目顺序 = 组1 自上而下 -> 组2 -> 组3（parse_yssd_blocks 的收集顺序）
BLOCKS: List[Dict[str, Any]] = [
    {"date_text": "5月20日", "water_text": "测时水位： 15.20",
     "g1": [("左岸", "-8.0", "11.90"), ("2", "10.0", "10.80")],
     "g2": [("3", "25.0", "9.95")],
     "g3": [("右岸", "40.0", "10.30")]},
    {"date_text": "9月18日", "water_text": "测时水位： 17.85",
     "g1": [("左岸", "-12.5", "12.40"), ("2", "0.0", "11.05"), ("3", "15.0", "10.20")],
     "g2": [("4", "30.0", "9.55"), ("5", "45.0", "9.10"), ("6", "60.0", "9.36")],
     "g3": [("7", "75.0", "88"), ("右岸", "90.0", "10.75")]},
    {"date_text": "1月19日", "water_text": "测时水位： 16.40",
     "g1": [("1", "-10.0", "12.10"), ("2", "5.0", "11.30"), ("3", "20.0", "10.40")],
     "g2": [("4", "35.0", "9.80"), ("5", "51.0", "9.20")],
     "g3": [("6", "65.0", "75"), ("7", "80.0", "10.60")]},
    # 追加在最后：已有块的 index（1/2/3）保持不变，既有 golden 的 xlsBlock 值不受影响
    {"date_text": "3月15日", "water_text": "测时水位： 9.10",
     "g1": [("1", "-9.0", "10.40"), ("2", "5.0", "65.00"), ("3", "20.0", "9.80")],
     "g2": [("4", "35.0", "9.55"), ("5", "50.0", "-35.00")],
     "g3": [("6", "65.0", "9.30")]},
]

HEADER = ["垂线号", "起点距", "河底高程"] * 3
PAD = 13


def _blank(n: int = PAD) -> List[Any]:
    return [None] * n


def _data_row(g1, g2, g3, pad: int = PAD) -> List[Any]:
    vals = list(g1) + list(g2) + list(g3)
    return vals + [None] * (pad - len(vals))


def _meta_row(date_text: str, water_text: str, pad: int = PAD) -> List[Any]:
    cells = [None] * pad
    cells[0] = "施测日期："
    cells[2] = date_text
    cells[pad - 1] = water_text
    return cells


def source_rows() -> List[List[Any]]:
    """源 .xlsx 的完整行序列（xlsx writer 与断言共用）。"""
    rows: List[List[Any]] = [
        ["合成站 实测大断面成果表"] + [None] * (PAD - 1),
        _blank(),
        list(HEADER),
        _blank(),
    ]
    for block in BLOCKS:
        rows.append(_meta_row(block["date_text"], block["water_text"]))
        nrows = max(len(block["g1"]), len(block["g2"]), len(block["g3"]))
        for i in range(nrows):
            def _cell(group):
                return group[i] if i < len(group) else (None, None, None)
            rows.append(_data_row(_cell(block["g1"]), _cell(block["g2"]),
                                  _cell(block["g3"])))
        rows.append(_blank())
    return rows


def build_fixtures(base_dir: str) -> Dict[str, str]:
    """在 ``base_dir`` 下生成合成 DB / yssd 源表 / 非 yssd 源表 / 坏 DB，返回路径 dict。"""
    os.makedirs(base_dir, exist_ok=True)
    db_path = os.path.join(base_dir, "fixture.db")
    if os.path.exists(db_path):
        os.remove(db_path)
    con = sqlite3.connect(db_path)
    try:
        con.executescript(DB_SCHEMA)
        con.execute(
            "INSERT INTO stations (station_code, station_name, river_name, water_system)"
            " VALUES (?,?,?,?)", (STATION, STATION_NAME, RIVER_NAME, "合成水系"))
        for date in DB_POINTS:
            for idx, (off, elev) in enumerate(DB_POINTS[date], 1):
                con.execute(
                    "INSERT INTO cross_section (station_code, meas_date, vert_num, distance,"
                    " elevation, source_file, section_name, conflict_flag)"
                    " VALUES (?,?,?,?,?,?,?,?)",
                    (STATION, date, idx, off, elev, "source-yssd.xlsx",
                     "合成断面", 0))
            con.execute(
                "INSERT INTO daily_water_level (station_code, date, value)"
                " VALUES (?,?,?)", (STATION, date, DB_WATER[date]))
        con.commit()
    finally:
        con.close()

    xlsx_path = os.path.join(base_dir, "source-yssd.xlsx")
    from openpyxl import Workbook  # 延迟 import：缺库时由 main() 打 SKIP
    wb = Workbook()
    ws = wb.active
    ws.title = "大断面"
    for row in source_rows():
        ws.append(row)
    wb.save(xlsx_path)

    # 非 yssd 源表（两列），用于 BAD_DATA 负例
    two_col = os.path.join(base_dir, "two-col.xlsx")
    wb2 = Workbook()
    ws2 = wb2.active
    ws2.append(["起点距", "高程"])
    for off, elev in ((0, 12.0), (10, 11.0), (20, 10.0), (30, 10.5)):
        ws2.append([off, elev])
    wb2.save(two_col)

    # 坏 DB（不是 sqlite 文件）用于 BAD_DB 负例
    bad_db = os.path.join(base_dir, "bad.db")
    with open(bad_db, "wb") as fh:
        fh.write(b"this is not a sqlite database\n" * 64)

    return {"base": base_dir, "db": db_path, "xlsx": xlsx_path,
            "two_col": two_col, "bad_db": bad_db}


# ---------------------------------------------------------------------------
# helper：跑 CLI 并读回模型
# ---------------------------------------------------------------------------
def fresh(name: str) -> str:
    out = os.path.join(TMP, "out", name + ".json")
    parent = os.path.dirname(out)
    if os.path.isdir(parent):
        for f in os.listdir(parent):
            if f.startswith(name + "."):
                os.remove(os.path.join(parent, f))
    os.makedirs(parent, exist_ok=True)
    return out


def compare_run(fx: Dict[str, str], date_args: List[str], name: str,
                extra: Optional[List[str]] = None) -> Tuple[int, bytes, bytes, str, Dict[str, Any]]:
    out_path = fresh(name)
    args = ["--from-db", STATION, "--db", fx["db"]] + list(date_args) + \
           ["--compare-source", "--from-excel", fx["xlsx"]] + list(extra or []) + \
           ["--out", out_path]
    code, so, se = run(args)
    model: Dict[str, Any] = {}
    if os.path.isfile(out_path):
        with open(out_path, "rb") as fh:
            raw = fh.read()
        try:
            model = json.loads(raw.decode("utf-8"))
        except Exception:
            model = {}
    return code, so, se, out_path, model


SC = "sourceCompare"


def sc_of(model: Dict[str, Any]) -> Dict[str, Any]:
    return model.get(SC) or {}


# ---------------------------------------------------------------------------
# 1) 主体一致
# ---------------------------------------------------------------------------
def t_same(fx: Dict[str, str]) -> None:
    code, so, se, out_path, model = compare_run(fx, ["--date", D_A], "same")
    payload = out_json(so)
    sc = sc_of(model)
    check("一致例：exit 0 且 stderr 为空", code == 0 and se == b"",
          "rc=%d stderr=%r" % (code, se[:120]))
    check("一致例：stdout 单段 JSON 且 ok=True", payload.get("ok") is True
          and so.count(b"\n") == 1 and so.endswith(b"\n"),
          "stdout=%r" % (so[:120],))
    check("一致例：stdout 是纯 ASCII（协议要求）", so.isascii(), "non-ascii bytes present")
    check("一致例：stdout.sourceCompare = {compared:4, same:4, differingCount:0}",
          payload.get("sourceCompare") == {"compared": 4, "same": 4, "differingCount": 0},
          "got=%r" % (payload.get("sourceCompare"),))
    check("一致例：warnings 码含 SOURCE_COMPARE_SAME 与 WATER_FROM_DB",
          payload.get("warnings") == ["WATER_FROM_DB", "SOURCE_COMPARE_SAME"],
          "warnings=%r" % (payload.get("warnings"),))
    check("一致例：落盘 model schema = v2", model.get("schema") == "zwcad-section-model/2",
          "schema=%r" % (model.get("schema"),))
    check("一致例：sc.dbPoints=4 / compared=4 / same=4 / differingCount=0",
          (sc.get("dbPoints"), sc.get("compared"), sc.get("same"),
           sc.get("differingCount")) == (4, 4, 4, 0),
          "sc=%r" % ({k: sc.get(k) for k in ("dbPoints", "compared", "same",
                                             "differingCount")},))
    check("一致例：tolerance 默认 0.005", sc.get("tolerance") == 0.005,
          "got=%r" % (sc.get("tolerance"),))
    check("一致例：differing[] 为空", sc.get("differing") == [], "got=%r" % (sc.get("differing"),))
    check("一致例：note 说明 1:1 对齐口径（含「1:1 对齐」）",
          "1:1 对齐" in (sc.get("note") or ""), "note=%r" % ((sc.get("note") or "")[:60],))
    check("一致例：xlsPath = 源表绝对路径",
          sc.get("xlsPath") == os.path.abspath(fx["xlsx"]), "got=%r" % (sc.get("xlsPath"),))
    bd = (sc.get("byDate") or [{}])
    check("一致例：byDate 只有 1 条且 matched=True", len(bd) == 1 and bd[0].get("matched") is True,
          "byDate=%r" % (bd,))
    if bd:
        row = bd[0]
        check("一致例：byDate[0] 计数/源表块信息（xlsBlock=1, xlsDate=5月20日,"
              " entries/numbered/labels=4/2/2）",
              (row.get("date"), row.get("xlsBlock"), row.get("xlsDate"),
               row.get("xlsEntryCount"), row.get("xlsNumberedCount"),
               row.get("xlsLabelRows")) == (D_A, 1, "5月20日", 4, 2, 2),
              "row=%r" % ({k: row.get(k) for k in ("date", "xlsBlock", "xlsDate",
                                                   "xlsEntryCount", "xlsNumberedCount",
                                                   "xlsLabelRows")},))
        check("一致例：byDate[0] 的 1:1 对齐字段（compared/same/differing/offsetMismatch/"
              "xlsTruncatedCells）",
              (row.get("compared"), row.get("same"), row.get("differing"),
               row.get("offsetMismatch"), row.get("xlsTruncatedCells")) == (4, 4, 0, 0, 0),
              "row=%r" % ({k: row.get(k) for k in ("compared", "same", "differing",
                                                   "offsetMismatch", "xlsTruncatedCells")},))
        check("一致例：byDate[0].xlsWaterLevel 取自源表测时水位 15.20",
              row.get("xlsWaterLevel") == 15.20, "got=%r" % (row.get("xlsWaterLevel"),))
        check("一致例：byDate[0].dbWaterLevel 回填 DB 日均水位 14.85",
              row.get("dbWaterLevel") == DB_WATER[D_A], "got=%r" % (row.get("dbWaterLevel"),))
    warn_sc = [w for w in (model.get("warnings") or []) if w.get("code") == "SOURCE_COMPARE_SAME"]
    check("一致例：model.warnings 的 SOURCE_COMPARE_* 带 compared/same/differingCount 字段",
          len(warn_sc) == 1 and (warn_sc[0].get("compared"), warn_sc[0].get("same"),
                                 warn_sc[0].get("differingCount")) == (4, 4, 0),
          "warnings=%r" % (warn_sc,))
    check("一致例：模型文件 UTF-8 无 BOM",
          (lambda raw: not raw.startswith(b"\xef\xbb\xbf"))(open(out_path, "rb").read()),
          "bom found")


# ---------------------------------------------------------------------------
# 2) 少量差异 + 截断
# ---------------------------------------------------------------------------
def t_minor_diff(fx: Dict[str, str]) -> None:
    code, so, se, _out, model = compare_run(fx, ["--date", D_B], "diff")
    payload = out_json(so)
    sc = sc_of(model)
    rows = {d.get("vert"): d for d in (sc.get("differing") or [])}
    check("差异例：exit 0 且 stdout sourceCompare 计数（8/6/2）",
          code == 0 and se == b""
          and payload.get("sourceCompare") == {"compared": 8, "same": 6, "differingCount": 2},
          "rc=%d stderr=%r sc=%r" % (code, se[:80], payload.get("sourceCompare")))
    check("差异例：sc.dbPoints=8 / compared=8 / same=6 / differingCount=2",
          (sc.get("dbPoints"), sc.get("compared"), sc.get("same"),
           sc.get("differingCount")) == (8, 8, 6, 2),
          "sc=%r" % ({k: sc.get(k) for k in ("dbPoints", "compared", "same",
                                             "differingCount")},))
    check("差异例：warnings 码含 SOURCE_COMPARE_DIFF",
          payload.get("warnings") == ["WATER_FROM_DB", "SOURCE_COMPARE_DIFF"],
          "warnings=%r" % (payload.get("warnings"),))
    warn = [w for w in (model.get("warnings") or [])
            if w.get("code") == "SOURCE_COMPARE_DIFF"]
    check("差异例：SOURCE_COMPARE_DIFF message 说明「8 点 / 2 处不同 / 截断」",
          len(warn) == 1 and "比对 8 点" in warn[0].get("message", "")
          and "2 处不同" in warn[0].get("message", "")
          and "截断" in warn[0].get("message", ""),
          "warn=%r" % (warn[0].get("message", "") if warn else None,))
    d6 = rows.get(6) or {}
    check("差异例：differing[vert=6] 的字段（dbElev 9.33 / xlsElev 9.36 / delta -0.03 / "
          "xlsRaw 非截断）",
          (d6.get("date"), d6.get("vert"), d6.get("dbOffset"), d6.get("xlsOffset"),
           d6.get("dbElev"), d6.get("xlsElev"), d6.get("delta"),
           d6.get("xlsRaw"), d6.get("xlsRawIsTruncated")) ==
          (D_B, 6, 60.0, 60.0, 9.33, 9.36, -0.03, "9.36", False),
          "d6=%r" % (d6,))
    d7 = rows.get(7) or {}
    check("差异例：differing[vert=7] 是截断高程（xlsRaw='88' -> 88.0，delta -79.12，"
          "xlsRawIsTruncated=True）",
          (d7.get("vert"), d7.get("dbElev"), d7.get("xlsElev"), d7.get("delta"),
           d7.get("xlsRaw"), d7.get("xlsRawIsTruncated")) ==
          (7, 8.88, 88.0, -79.12, "88", True),
          "d7=%r" % (d7,))
    bd = (sc.get("byDate") or [{}])[0]
    check("差异例：byDate[0] 的 xlsBlock=2 / xlsNumberedCount=6 / xlsLabelRows=2 / "
          "xlsTruncatedCells=1",
          (bd.get("xlsBlock"), bd.get("xlsNumberedCount"), bd.get("xlsLabelRows"),
           bd.get("xlsTruncatedCells")) == (2, 6, 2, 1),
          "bd=%r" % ({k: bd.get(k) for k in ("xlsBlock", "xlsNumberedCount",
                                             "xlsLabelRows", "xlsTruncatedCells")},))
    check("差异例：byDate[0].differing=2 且 compared(8) == same(6)+differing(2)",
          bd.get("differing") == 2 and bd.get("compared") == bd.get("same", 0) + bd.get("differing", 0),
          "bd=%r" % ({k: bd.get(k) for k in ("compared", "same", "differing")},))


# ---------------------------------------------------------------------------
# 3) 截断 + 起点距不符
# ---------------------------------------------------------------------------
def t_trunc_offset(fx: Dict[str, str]) -> None:
    code, so, _se, _out, model = compare_run(fx, ["--date", D_C], "trunc")
    payload = out_json(so)
    sc = sc_of(model)
    bd = (sc.get("byDate") or [{}])[0]
    rows = {d.get("vert"): d for d in (sc.get("differing") or [])}
    check("截断+起点距例：exit 0，stdout 计数（7/5/2）",
          code == 0 and payload.get("sourceCompare") ==
          {"compared": 7, "same": 5, "differingCount": 2},
          "rc=%d sc=%r" % (code, payload.get("sourceCompare")))
    check("截断+起点距例：offsetMismatch=1 单独计数，不混入 differing（differing 只有 vert4/6）",
          bd.get("offsetMismatch") == 1
          and sorted(rows) == [4, 6], "bd=%r verts=%r"
          % (bd.get("offsetMismatch"), sorted(rows)))
    check("截断+起点距例：起点距不符但高程一致的点（vert5：50 vs 51）不算不同",
          5 not in rows and (rows.get(4) or {}).get("delta") == 0.03,
          "verts=%r" % (sorted(rows),))
    check("截断+起点距例：vert6 是截断高程（xlsRaw='75' -> 75.0，delta -65.25，"
          "xlsRawIsTruncated=True）",
          (rows.get(6) or {}).get("xlsRaw") == "75"
          and (rows.get(6) or {}).get("xlsElev") == 75.0
          and (rows.get(6) or {}).get("delta") == -65.25
          and (rows.get(6) or {}).get("xlsRawIsTruncated") is True,
          "d6=%r" % (rows.get(6),))
    check("截断+起点距例：byDate[0].xlsTruncatedCells=1 / xlsBlock=3 / xlsLabelRows=0",
          (bd.get("xlsTruncatedCells"), bd.get("xlsBlock"),
           bd.get("xlsLabelRows")) == (1, 3, 0),
          "bd=%r" % ({k: bd.get(k) for k in ("xlsTruncatedCells", "xlsBlock",
                                             "xlsLabelRows")},))
    check("截断+起点距例：byDate[0].dbWaterLevel 回填 10.05",
          bd.get("dbWaterLevel") == DB_WATER[D_C], "got=%r" % (bd.get("dbWaterLevel"),))


# ---------------------------------------------------------------------------
# 4) 多序列：--last 2 / --last 3（含源表没有的日期）
# ---------------------------------------------------------------------------
def t_multi_series(fx: Dict[str, str]) -> None:
    code, so, _se, _out, model = compare_run(fx, ["--last", "2"], "last2")
    sc = sc_of(model)
    payload = out_json(so)
    check("多序列 --last 2：exit 0 + 2 个序列（2024-09-18 / 2025-01-19）",
          code == 0 and payload.get("seriesCount") == 2
          and payload.get("dates") == [D_B, D_C],
          "rc=%d seriesCount=%r dates=%r"
          % (code, payload.get("seriesCount"), payload.get("dates")))
    check("多序列 --last 2：sc 汇总 dbPoints/compared/same/differingCount = 15/15/11/4",
          (sc.get("dbPoints"), sc.get("compared"), sc.get("same"),
           sc.get("differingCount")) == (15, 15, 11, 4),
          "sc=%r" % ({k: sc.get(k) for k in ("dbPoints", "compared", "same",
                                             "differingCount")},))
    by = sc.get("byDate") or []
    check("多序列 --last 2：byDate 两条都 matched，xlsBlock 依次 2/3",
          [r.get("matched") for r in by] == [True, True]
          and [r.get("xlsBlock") for r in by] == [2, 3],
          "byDate=%r" % by)
    check("多序列 --last 2：stdout.sourceCompare.differingCount=4",
          payload.get("sourceCompare", {}).get("differingCount") == 4,
          "got=%r" % (payload.get("sourceCompare"),))

    code, so2, _se2, _out2, model2 = compare_run(fx, ["--last", "3"], "last3")
    sc2 = sc_of(model2)
    by2 = sc2.get("byDate") or []
    unmatched = [r for r in by2 if r.get("matched") is False]
    check("多序列 --last 3：exit 0 + 3 个序列（含源表没有的 2023-06-01）",
          code == 0 and len(by2) == 3, "rc=%d byDate=%r" % (code, [r.get("date") for r in by2]))
    check("多序列 --last 3：2023-06-01 一条 matched=False 且带 reason，不计入 compared",
          len(unmatched) == 1 and unmatched[0].get("date") == D_X
          and bool(unmatched[0].get("reason")) and "compared" not in unmatched[0],
          "unmatched=%r" % (unmatched,))
    check("多序列 --last 3：dbPoints=20，compared 只算匹配上的 15，differingCount=4",
          (sc2.get("dbPoints"), sc2.get("compared"), sc2.get("differingCount")) == (20, 15, 4),
          "sc=%r" % ({k: sc2.get(k) for k in ("dbPoints", "compared", "differingCount")},))

    # 只选源表里没有的日期：sourceCompare 数据照实（compared=0 / matched=False），告警必须是
    # SOURCE_COMPARE_UNMATCHED —— 修前这里会误报 SOURCE_COMPARE_SAME「（两者一致）」。
    code, so3, _se3, _out3, model3 = compare_run(fx, ["--date", D_X], "unmatched")
    payload3 = out_json(so3)
    sc3 = sc_of(model3)
    check("单日期不匹配：exit 0，compared=0 / differingCount=0 / byDate[].matched=False",
          code == 0 and sc3.get("compared") == 0 and sc3.get("differingCount") == 0
          and sc3.get("dbPoints") == 5
          and (sc3.get("byDate") or [{}])[0].get("matched") is False,
          "sc=%r" % ({k: sc3.get(k) for k in ("dbPoints", "compared", "differingCount")},))
    check("单日期不匹配：warnings 码为 SOURCE_COMPARE_UNMATCHED（不再误报 SOURCE_COMPARE_SAME）",
          payload3.get("warnings") == ["WATER_FROM_DB", "SOURCE_COMPARE_UNMATCHED"],
          "warnings=%r" % (payload3.get("warnings"),))
    warn_un = [w for w in (model3.get("warnings") or [])
               if w.get("code") == "SOURCE_COMPARE_UNMATCHED"]
    check("单日期不匹配：UNMATCHED message 说明「1 个施测日期没有对应施测块」且带计数字段",
          len(warn_un) == 1 and "1 个施测日期" in warn_un[0].get("message", "")
          and "没有对应施测块" in warn_un[0].get("message", "")
          and warn_un[0].get("compared") == 0 and warn_un[0].get("differingCount") == 0,
          "warn=%r" % (warn_un[0] if warn_un else None,))


# ---------------------------------------------------------------------------
# 5) 关闭 DB 水位：dbWaterLevel 缺省 + NO_WATER
# ---------------------------------------------------------------------------
def t_no_water(fx: Dict[str, str]) -> None:
    code, so, _se, _out, model = compare_run(fx, ["--date", D_B], "nowater",
                                             ["--no-water-from-db"])
    payload = out_json(so)
    bd = ((sc_of(model).get("byDate")) or [{}])[0]
    check("--no-water-from-db：exit 0 且 byDate 无 dbWaterLevel 键",
          code == 0 and "dbWaterLevel" not in bd, "rc=%d bd=%r" % (code, bd))
    check("--no-water-from-db：warnings 含 NO_WATER（不含 WATER_FROM_DB）",
          payload.get("warnings") == ["NO_WATER", "SOURCE_COMPARE_DIFF"],
          "warnings=%r" % (payload.get("warnings"),))


# ---------------------------------------------------------------------------
# 5b) --drop-suspect × --compare-source：比对必须按「删点前」的原始序列对齐
#     （修前用 model["series"]（已删点）按下标 1:1 对齐 -> 下标前移、比对静默错位）
# ---------------------------------------------------------------------------
def t_drop_suspect(fx: Dict[str, str]) -> None:
    # 参照组：不带 --drop-suspect（块 3月15日 与 DB 原始值逐点一致 -> 6 点全一致）
    code_ref, so_ref, _se_ref, _out_ref, model_ref = compare_run(fx, ["--date", D_S],
                                                                 "drop-ref")
    sc_ref = sc_of(model_ref)

    # 本组：--drop-suspect（模型删 2 个离群点；比对仍须覆盖 6 个原始点且不错位）
    code, so, _se, _out, model = compare_run(fx, ["--date", D_S], "drop-drop",
                                             ["--drop-suspect"])
    payload = out_json(so)
    sc = sc_of(model)
    series = (model.get("series") or [{}])[0]
    dq = model.get("dataQuality") or {}
    bd = (sc.get("byDate") or [{}])[0]

    check("--drop-suspect 参照：不删点时 6 点全一致（dbPoints/compared/same/diff = 6/6/6/0）",
          code_ref == 0
          and (sc_ref.get("dbPoints"), sc_ref.get("compared"), sc_ref.get("same"),
               sc_ref.get("differingCount")) == (6, 6, 6, 0),
          "sc=%r" % ({k: sc_ref.get(k) for k in ("dbPoints", "compared", "same",
                                                 "differingCount")},))
    check("--drop-suspect 例：模型仍按原语义删点（series.pointCount=4 / dropped=2 / "
          "suspectCount=2）",
          code == 0 and series.get("pointCount") == 4 and dq.get("dropped") == 2
          and dq.get("suspectCount") == 2,
          "rc=%d pointCount=%r dropped=%r suspectCount=%r"
          % (code, series.get("pointCount"), dq.get("dropped"), dq.get("suspectCount")))
    check("--drop-suspect 例：比对按删点前原始序列 —— compared=dbPoints=6、differingCount=0"
          "（修前会退化成 compared=4 / differing=3 的错位结果）",
          (sc.get("dbPoints"), sc.get("compared"), sc.get("same"),
           sc.get("differingCount")) == (6, 6, 6, 0),
          "sc=%r" % ({k: sc.get(k) for k in ("dbPoints", "compared", "same",
                                             "differingCount")},))
    check("--drop-suspect 例：byDate 覆盖原始 6 点（compared=6 / dbPointCount=6 / "
          "xlsEntryCount=6），不因删点缩水",
          (bd.get("compared"), bd.get("dbPointCount"), bd.get("xlsEntryCount"),
           bd.get("differing")) == (6, 6, 6, 0),
          "bd=%r" % ({k: bd.get(k) for k in ("compared", "dbPointCount", "xlsEntryCount",
                                             "differing")},))
    check("--drop-suspect 例：sourceCompare 与不删点参照**完全相同**（无下标错位）",
          sc == sc_ref,
          "diff=%r" % ({k: (sc.get(k), sc_ref.get(k)) for k in sc
                        if sc.get(k) != sc_ref.get(k)},))
    check("--drop-suspect 例：stdout.sourceCompare 同参照（6/6/0），告警仍为 SAME",
          payload.get("sourceCompare") == out_json(so_ref).get("sourceCompare")
          and payload.get("warnings") == ["SUSPECT_DROPPED", "WATER_FROM_DB", "TOO_TALL",
                                          "SOURCE_COMPARE_SAME"],
          "stdout_sc=%r warnings=%r"
          % (payload.get("sourceCompare"), payload.get("warnings")))

    # 多序列 + 删点：--last 5 覆盖到含离群点的 D_S；比对总数仍等于原始点数
    # （D_A 4 + D_X 未匹配 0 + D_B 8 + D_C 7 + D_S 6 -> compared=25）
    code5, _so5, _se5, _out5, model5 = compare_run(fx, ["--last", "5"], "drop-last5",
                                                   ["--drop-suspect"])
    _cr, _sr, _er, _outr, model_r = compare_run(fx, ["--last", "5"], "drop-last5-ref")
    sc5, sc_r = sc_of(model5), sc_of(model_r)
    check("--drop-suspect 多序列 --last 5：compared=25（原始匹配点数）且 sourceCompare 与"
          "不删点参照完全相同",
          code5 == 0 and sc5.get("compared") == 25 and sc5 == sc_r,
          "compared=%r ref=%r equal=%s"
          % (sc5.get("compared"), sc_r.get("compared"), sc5 == sc_r))
    check("--drop-suspect 多序列 --last 5：differingCount 与不删点时同一批差异点一致（4）",
          sc5.get("differingCount") == sc_r.get("differingCount") == 4,
          "drop=%r ref=%r" % (sc5.get("differingCount"), sc_r.get("differingCount")))

    # --drop-suspect 但该日期没有离群点（D_B）：比对结果与不带 drop 完全相同（多取一次
    # 原始序列不应有副作用）
    code_n, _so_n, _se_n, _out_n, model_n = compare_run(fx, ["--date", D_B], "drop-nodrop",
                                                        ["--drop-suspect"])
    _cd, _sd, _ed, _outd, model_d = compare_run(fx, ["--date", D_B], "drop-nodrop-ref")
    check("--drop-suspect 但无离群点：sourceCompare 与不带 drop 完全相同（无副作用）",
          code_n == 0 and sc_of(model_n) == sc_of(model_d),
          "equal=%s" % (sc_of(model_n) == sc_of(model_d),))


# ---------------------------------------------------------------------------
# 6) 负例：BAD_ARGS / BAD_DB / BAD_DATA（exit 2 且不产出文件）
# ---------------------------------------------------------------------------
def t_negative(fx: Dict[str, str]) -> None:
    # (a) 缺 --from-excel
    out_path = fresh("neg-noexcel")
    code, so, se = run(["--from-db", STATION, "--db", fx["db"], "--date", D_B,
                        "--compare-source", "--out", out_path])
    payload = out_json(so)
    check("负例 缺 --from-excel -> BAD_ARGS + exit 2 + 不产出文件",
          code == 2 and (payload.get("error") or {}).get("code") == "BAD_ARGS"
          and not os.path.exists(out_path) and se == b"",
          "rc=%d err=%r exists=%s" % (code, payload.get("error"), os.path.exists(out_path)))
    check("负例 缺 --from-excel -> stdout 仍是单段 ASCII JSON（报文未破）",
          so.isascii() and so.count(b"\n") == 1 and payload.get("ok") is False,
          "stdout=%r" % (so[:120],))

    # (b) 坏 DB（文件不是 sqlite）
    out_path = fresh("neg-baddb")
    code, so, se = run(["--from-db", STATION, "--db", fx["bad_db"], "--date", D_B,
                        "--compare-source", "--from-excel", fx["xlsx"],
                        "--out", out_path])
    payload = out_json(so)
    check("负例 坏 DB -> BAD_DB + exit 2 + 不产出文件",
          code == 2 and (payload.get("error") or {}).get("code") == "BAD_DB"
          and not os.path.exists(out_path) and se == b"",
          "rc=%d err=%r" % (code, payload.get("error")))

    # (c) 非 yssd 源表（两列）-> BAD_DATA
    out_path = fresh("neg-badxls")
    code, so, se = run(["--from-db", STATION, "--db", fx["db"], "--date", D_B,
                        "--compare-source", "--from-excel", fx["two_col"],
                        "--out", out_path])
    payload = out_json(so)
    err = payload.get("error") or {}
    check("负例 非 yssd 源表 -> BAD_DATA + exit 2 + 不产出文件",
          code == 2 and err.get("code") == "BAD_DATA"
          and "比对源表未识别到实测大断面表头" in err.get("message", "")
          and not os.path.exists(out_path),
          "rc=%d err=%r exists=%s" % (code, err, os.path.exists(out_path)))

    # (d) --from-db 与 --from-excel 同时给但没 --compare-source -> BAD_ARGS
    out_path = fresh("neg-both")
    code, so, _se = run(["--from-db", STATION, "--db", fx["db"], "--date", D_B,
                         "--from-excel", fx["xlsx"], "--out", out_path])
    payload = out_json(so)
    check("负例 --from-db 与 --from-excel 二选一（无 --compare-source）-> BAD_ARGS",
          code == 2 and (payload.get("error") or {}).get("code") == "BAD_ARGS"
          and not os.path.exists(out_path),
          "rc=%d err=%r" % (code, payload.get("error")))


# ---------------------------------------------------------------------------
# 7) 纯函数 compare_db_vs_excel：容差 / 日期键 / 无匹配 / min 截断
# ---------------------------------------------------------------------------
def _block(date_text: str, date_key: str, entries: List[Dict[str, Any]],
           index: int = 1) -> Dict[str, Any]:
    return {"index": index, "date": date_text, "dateKey": date_key, "entries": entries,
            "numbered": sum(1 for e in entries if e.get("kind") == "number"),
            "labels": sum(1 for e in entries if e.get("kind") == "label"),
            "waterLevel": 1.0}


def _ent(offset: float, elev: float, raw: Any = None, kind: str = "number") -> Dict[str, Any]:
    return {"kind": kind, "offset": offset, "elev": elev,
            "elevRaw": raw if raw is not None else elev}


def t_pure_function() -> None:
    sys.path.insert(0, SEC)
    import prep_db  # noqa: E402

    # 容差边界：默认 0.005 —— 恰好 0.005 算一致，0.006 算不同
    series = [{"date": "2025-01-19", "points": [[0.0, 1.0], [1.0, 2.0], [2.0, 3.0]]}]
    blocks = [_block("1月19日", "1-19",
                     [_ent(0.0, 1.0), _ent(1.0, 2.0 + 0.005), _ent(2.0, 3.0 + 0.006)])]
    cmp = prep_db.compare_db_vs_excel(series, blocks)
    check("纯函数：默认容差 0.005 —— delta=0.005 算一致、0.006 算不同",
          cmp["same"] == 2 and cmp["differingCount"] == 1
          and cmp["differing"][0]["vert"] == 3, "cmp=%r" % (cmp["differing"],))
    check("纯函数：默认 tolerance 字段 = 0.005", cmp["tolerance"] == 0.005,
          "got=%r" % (cmp["tolerance"],))

    cmp2 = prep_db.compare_db_vs_excel(series, blocks, tol=0.01)
    check("纯函数：自定义 tol=0.01 时 delta=0.006 也算一致",
          cmp2["same"] == 3 and cmp2["differingCount"] == 0 and cmp2["tolerance"] == 0.01,
          "cmp2=%r" % ({k: cmp2.get(k) for k in ("same", "differingCount", "tolerance")},))

    # 日期键：DB 的 YYYY-MM-DD 取后 5 字符归一到 M-D 才能匹配源表块的 dateKey
    cmp3 = prep_db.compare_db_vs_excel([{"date": "2025-01-19", "points": [[0.0, 1.0]]}],
                                       [_block("1月19日", "1-19", [_ent(0.0, 1.0)])])
    check("纯函数：DB 日期 2025-01-19 命中源表块 dateKey=1-19",
          len(cmp3["byDate"]) == 1 and cmp3["byDate"][0]["matched"] is True
          and cmp3["compared"] == 1, "byDate=%r" % (cmp3["byDate"],))

    # 无匹配日期：matched=False + reason，compared 计 0
    cmp4 = prep_db.compare_db_vs_excel([{"date": "2023-06-01", "points": [[0.0, 1.0]]}],
                                       [_block("1月19日", "1-19", [_ent(0.0, 1.0)])])
    check("纯函数：源表无对应日期 -> byDate[].matched=False + reason，compared=0",
          len(cmp4["byDate"]) == 1 and cmp4["byDate"][0]["matched"] is False
          and bool(cmp4["byDate"][0].get("reason")) and cmp4["compared"] == 0,
          "byDate=%r" % (cmp4["byDate"],))

    # min(ents, points)：源表条目多于 DB 测点时，只比 DB 测点数且不抛异常
    cmp5 = prep_db.compare_db_vs_excel(
        [{"date": "2025-01-19", "points": [[0.0, 1.0], [1.0, 2.0]]}],
        [_block("1月19日", "1-19", [_ent(0.0, 1.0), _ent(1.0, 2.0), _ent(2.0, 3.0),
                                    _ent(3.0, 4.0)])])
    check("纯函数：源表条目多于 DB 测点 -> compared = min(4,2) = 2 且不抛异常",
          cmp5["compared"] == 2 and cmp5["dbPoints"] == 2
          and cmp5["byDate"][0]["xlsEntryCount"] == 4,
          "cmp5=%r" % ({k: cmp5.get(k) for k in ("compared", "dbPoints")},))

    # 空白/None 条目跳过：不影响 compared 的自洽（compared 只统计 min 覆盖范围）
    cmp6 = prep_db.compare_db_vs_excel(
        [{"date": "2025-01-19", "points": [[0.0, 1.0]]}],
        [_block("1月19日", "1-19", [_ent(0.0, 1.0, kind="label")])])
    check("纯函数：标签行也参与 1:1 对齐（kind=label 不被过滤）",
          cmp6["compared"] == 1 and cmp6["same"] == 1
          and cmp6["byDate"][0]["xlsLabelRows"] == 1,
          "cmp6=%r" % ({k: cmp6.get(k) for k in ("compared", "same")},))


# ---------------------------------------------------------------------------
# 8) 协议：确定性 / 落盘编码
# ---------------------------------------------------------------------------
def t_protocol(fx: Dict[str, str]) -> None:
    args = ["--from-db", STATION, "--db", fx["db"], "--date", D_B,
            "--compare-source", "--from-excel", fx["xlsx"]]
    out1 = fresh("proto-1")
    code1, so1, se1 = run(args + ["--out", out1])
    out2 = fresh("proto-2")
    code2, so2, se2 = run(args + ["--out", out2])
    # 两次运行只有 --out 不同：stdout 里的路径是 \\u 转义形式，直接按 JSON 比较并剔除 out 字段
    p1, p2 = out_json(so1), out_json(so2)
    got_out1 = p1.pop("out", None)
    p2.pop("out", None)
    check("协议：同一输入两次运行 stdout（除 out 路径）完全相同 -- 确定性",
          code1 == code2 == 0 and p1 == p2 and se1 == se2 == b"",
          "equal=%s" % (p1 == p2))
    check("协议：两次运行的落盘模型除路径外逐字节相同（模型内容确定性）",
          open(out1, "rb").read() == open(out2, "rb").read(),
          "model bytes differ")
    check("协议：stdout.out 就是本次 --out 的绝对路径",
          got_out1 == os.path.abspath(out1), "got=%r" % (got_out1,))
    check("协议：stdout 单段 JSON + 纯 ASCII + 以换行结尾",
          so1.count(b"\n") == 1 and so1.isascii() and so1.endswith(b"\n"),
          "stdout=%r" % (so1[-120:],))
    check("协议：落盘 JSON 为 UTF-8 无 BOM 且可 json.loads",
          (lambda raw: (not raw.startswith(b"\xef\xbb\xbf"))
           and isinstance(json.loads(raw.decode("utf-8")), dict))
          (open(out1, "rb").read()), "bom or invalid json")
    model = json.loads(open(out1, "rb").read().decode("utf-8"))
    check("协议：stdout.out 指向落盘绝对路径，且模型含 sourceCompare 与 warnings",
          (out_json(so1).get("out") == os.path.abspath(out1)
           and isinstance(model.get(SC), dict) and isinstance(model.get("warnings"), list)),
          "out=%r" % (out_json(so1).get("out"),))


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def main() -> int:
    if os.path.isdir(TMP):
        shutil.rmtree(TMP)
    os.makedirs(TMP)
    try:
        import openpyxl  # noqa: F401
    except ImportError:
        print("SKIP  openpyxl 不可用：无法生成合成 yssd 源表，跳过 compare-source 自测"
              "（pip install -r requirements.txt 后可跑）")
        return 0
    fx = build_fixtures(TMP)
    t_same(fx)
    t_minor_diff(fx)
    t_trunc_offset(fx)
    t_multi_series(fx)
    t_no_water(fx)
    t_drop_suspect(fx)
    t_negative(fx)
    t_pure_function()
    t_protocol(fx)

    width = max(len(n) for n, _, _ in RESULTS) if RESULTS else 10
    failed = 0
    for name, ok, detail in RESULTS:
        print("%-4s  %-*s  %s" % ("PASS" if ok else "FAIL", width, name, detail))
        if not ok:
            failed += 1
    print("-" * 72)
    print("[selftest_compare_source] RESULT: %s  (%d PASS / %d FAIL)"
          % ("FAIL" if failed else "PASS", len(RESULTS) - failed, failed))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
