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
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import statistics
import sys
import tempfile
from typing import Any, Dict, List, Optional, Sequence, Tuple

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

# §4.1 兼容列名
OFFSET_ALIASES = ("起点距", "桩号", "距离", "offset", "distance", "station")
ELEV_ALIASES = ("高程", "河底高程", "elevation", "elev", "height")

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


class SectionError(Exception):
    """带机器可读 code 的失败；main() 统一转成 {"ok":false,...} + 退出码 2。"""

    def __init__(self, code: str, message: str, **details: Any) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.details = details

    def to_json(self) -> Dict[str, Any]:
        err: Dict[str, Any] = {"code": self.code, "message": self.message}
        if self.details:
            err["details"] = self.details
        return {"ok": False, "error": err}


# ----------------------------------------------------------------------------- 输出
def emit(payload: Dict[str, Any]) -> None:
    """stdout 单段 JSON（ASCII 安全，中文走 \\u 转义；PS 5.1 可安全 ConvertFrom-Json）。"""
    sys.stdout.write(json.dumps(payload, ensure_ascii=True, sort_keys=False,
                                separators=(",", ":")) + "\n")
    sys.stdout.flush()


def write_json_atomic(path: str, payload: Dict[str, Any]) -> None:
    """先写同目录临时文件再 os.replace：失败时绝不留下半截/残留输出文件。"""
    out_dir = os.path.dirname(os.path.abspath(path)) or "."
    os.makedirs(out_dir, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".section-model-", suffix=".tmp", dir=out_dir)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        os.replace(tmp, path)
    except BaseException:
        try:
            if os.path.exists(tmp):
                os.remove(tmp)
        except OSError:
            pass
        raise


# ----------------------------------------------------------------------------- 数值解析
def _is_blank(v: Any) -> bool:
    if v is None:
        return True
    if isinstance(v, str):
        return v.strip() == ""
    return False


def _to_number(v: Any) -> Optional[float]:
    """把单元格转成有限 float；空值返回 None；不可转 -> SectionError(BAD_DATA)。"""
    if _is_blank(v):
        return None
    if isinstance(v, bool):
        raise SectionError("BAD_DATA", "布尔值不能作为数字", value=str(v))
    if isinstance(v, (int, float)):
        f = float(v)
    elif isinstance(v, str):
        s = v.strip().replace(",", "").replace("，", "")
        try:
            f = float(s)
        except ValueError:
            raise SectionError("BAD_DATA", "单元格不是数字", value=v)
    else:
        raise SectionError("BAD_DATA", "单元格类型不支持", value=repr(v))
    if not math.isfinite(f):
        raise SectionError("BAD_DATA", "数值非有限（NaN/Inf）", value=repr(v))
    return f


def _norm_header(v: Any) -> str:
    s = "" if v is None else str(v)
    s = s.strip().lower()
    s = re.sub(r"[\s\u3000()（）\[\]【】{}<>《》:：,，/\\|_\-]+", "", s)
    return s


def _header_matches(header: Any, token: str) -> bool:
    h = _norm_header(header)
    t = _norm_header(token)
    if not h or not t:
        return False
    return h == t or h.startswith(t) or t.startswith(h)


def _match_alias(header: Any, aliases: Sequence[str]) -> bool:
    return any(_header_matches(header, a) for a in aliases)


# ----------------------------------------------------------------------------- Excel 读取
def load_rows(path: str, sheet: Optional[str]) -> Tuple[List[Tuple[Any, ...]], Dict[str, Any]]:
    """读 Excel 成「行 × 列」原始单元格矩阵（保留原文，供质量判定）。

    `.xls` -> xlrd（老式 BIFF，openpyxl 不支持）；`.xlsx/.xlsm` -> openpyxl。
    """
    if not os.path.isfile(path):
        raise SectionError("NO_FILE", "数据文件不存在", path=path)
    ext = os.path.splitext(path)[1].lower()
    if ext == ".xls":
        return _load_rows_xlrd(path, sheet)
    return _load_rows_openpyxl(path, sheet)


def _load_rows_xlrd(path: str, sheet: Optional[str]) -> Tuple[List[Tuple[Any, ...]], Dict[str, Any]]:
    try:
        import xlrd
    except ImportError:  # pragma: no cover
        raise SectionError(
            "NO_XLRD", "读 .xls 需要 xlrd（把 tools\\py-libs 加进 PYTHONPATH）", path=path)
    try:
        book = xlrd.open_workbook(path, formatting_info=False)
    except Exception as exc:
        raise SectionError("BAD_FILE", "打开 .xls 失败", path=path, reason=str(exc))

    names = book.sheet_names()
    if sheet:
        name = None
        if sheet in names:
            name = sheet
        elif str(sheet).isdigit() and 0 <= int(sheet) - 1 < len(names):
            name = names[int(sheet) - 1]
        if name is None:
            raise SectionError("BAD_SHEET", "找不到工作表", sheet=sheet, available=names)
        ws = book.sheet_by_name(name)
    else:
        ws = book.sheet_by_index(0)

    rows: List[Tuple[Any, ...]] = []
    for r in range(ws.nrows):
        rows.append(tuple(ws.cell_value(r, c) for c in range(ws.ncols)))
    meta = {
        "sheet": ws.name,
        "sheetNames": names,
        "totalRows": len(rows),
        "reader": "xlrd",
        "sourcePath": os.path.abspath(path),
    }
    return rows, meta


def _load_rows_openpyxl(path: str,
                        sheet: Optional[str]) -> Tuple[List[Tuple[Any, ...]], Dict[str, Any]]:
    try:
        from openpyxl import load_workbook
    except ImportError as exc:  # pragma: no cover
        raise SectionError("NO_OPENPYXL", "缺少 openpyxl：%s" % exc)

    try:
        wb = load_workbook(filename=path, data_only=True, read_only=True)
    except Exception as exc:
        raise SectionError("BAD_FILE", "打开 Excel 失败", path=path, reason=str(exc))

    try:
        if sheet:
            name = None
            if sheet in wb.sheetnames:
                name = sheet
            elif sheet.isdigit():
                idx = int(sheet) - 1
                if 0 <= idx < len(wb.sheetnames):
                    name = wb.sheetnames[idx]
            if name is None:
                raise SectionError("BAD_SHEET", "找不到工作表", sheet=sheet,
                                   available=list(wb.sheetnames))
            ws = wb[name]
        else:
            ws = wb[wb.sheetnames[0]]
        rows = [tuple(r) for r in ws.iter_rows(values_only=True)]
        meta = {"sheet": ws.title, "sheetNames": list(wb.sheetnames),
                "totalRows": len(rows), "reader": "openpyxl",
                "sourcePath": os.path.abspath(path)}
    finally:
        try:
            wb.close()
        except Exception:
            pass
    return rows, meta


def _find_header_row(rows: Sequence[Sequence[Any]], left_tok: str, right_tok: str,
                     scan: int = 10) -> Optional[Tuple[int, int, int]]:
    """在前 scan 行里找同时含左右列名的表头行，返回 (row_idx, offset_col, elev_col)。"""
    for r_idx in range(min(scan, len(rows))):
        row = rows[r_idx]
        off_col = elev_col = None
        for c_idx, cell in enumerate(row):
            if off_col is None and _header_matches(cell, left_tok):
                off_col = c_idx
            elif elev_col is None and _header_matches(cell, right_tok):
                elev_col = c_idx
        if off_col is not None and elev_col is not None and off_col != elev_col:
            return r_idx, off_col, elev_col
    return None


def parse_points(rows: List[Tuple[Any, ...]], meta: Dict[str, Any],
                 cols: Optional[str]) -> List[Dict[str, float]]:
    """按 DESIGN §4.1 解析 (offset, elev) 序列；不做排序/校验，只做「表头跳过 + 空值判定」。"""
    if not rows:
        raise SectionError("BAD_DATA", "工作表为空")

    off_col, elev_col = 0, 1
    header_row: Optional[int] = None
    if cols:
        parts = [p.strip() for p in cols.split("=", 1)]
        if len(parts) != 2 or not parts[0] or not parts[1]:
            raise SectionError("BAD_ARGS", "--cols 需形如 \"起点距=高程\"", cols=cols)
        left_tok, right_tok = parts
        found = _find_header_row(rows, left_tok, right_tok)
        if found is None:
            raise SectionError("BAD_DATA", "未找到 --cols 指定的表头行",
                               cols=cols, scannedRows=min(10, len(rows)))
        header_row, off_col, elev_col = found

    points: List[Dict[str, float]] = []
    first_data_row: Optional[int] = None
    last_data_row: Optional[int] = None
    r_idx = 0 if header_row is None else header_row + 1

    if header_row is None:
        # 默认口径：跳过开头非数字行（首行表头）
        while r_idx < len(rows):
            row = rows[r_idx]
            a = row[off_col] if off_col < len(row) else None
            b = row[elev_col] if elev_col < len(row) else None
            if _is_blank(a) and _is_blank(b):
                r_idx += 1
                continue
            try:
                _to_number(a)
                _to_number(b)
            except SectionError:
                r_idx += 1
                continue
            break

    for row_idx in range(r_idx, len(rows)):
        row = rows[row_idx]
        a = row[off_col] if off_col < len(row) else None
        b = row[elev_col] if elev_col < len(row) else None
        blank_a, blank_b = _is_blank(a), _is_blank(b)
        if blank_a and blank_b:
            break  # 数据区结束（尾部空行/注释区忽略）
        if blank_a or blank_b:
            raise SectionError(
                "BAD_DATA", "数据行存在空值",
                row=row_idx + 1, offset=("" if blank_a else str(a)),
                elev=("" if blank_b else str(b)))
        try:
            off = _to_number(a)
            elev = _to_number(b)
        except SectionError as exc:
            exc.details.setdefault("row", row_idx + 1)
            raise
        assert off is not None and elev is not None
        if first_data_row is None:
            first_data_row = row_idx + 1
        last_data_row = row_idx + 1
        points.append({"offset": off, "elev": elev})

    if first_data_row is not None:
        meta["firstDataRow"] = first_data_row
        meta["lastDataRow"] = last_data_row
    if header_row is not None:
        meta["headerRow"] = header_row + 1
        meta["offsetColumn"] = off_col + 1
        meta["elevColumn"] = elev_col + 1
    else:
        meta["offsetColumn"] = off_col + 1
        meta["elevColumn"] = elev_col + 1
    return points


# ----------------------------------------------------------------------------- yssd（实测大断面成果表）
def _soft_number(v: Any) -> Tuple[Optional[float], bool]:
    """宽松数值解析：返回 (数值或 None, ok)。空值 -> (None, True)；非数字 -> (None, False)。"""
    if _is_blank(v):
        return None, True
    if isinstance(v, bool):
        return None, False
    if isinstance(v, (int, float)):
        f = float(v)
        return (f, True) if math.isfinite(f) else (None, False)
    s = str(v).strip().replace(",", "").replace("，", "")
    if not s:
        return None, True
    try:
        f = float(s)
    except ValueError:
        return None, False
    return (f, True) if math.isfinite(f) else (None, False)


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


# ----------------------------------------------------------------------------- 数据库（hydro_data.db）
# 水情数据库路径不写进仓库：优先读环境变量 HYDRO_DB，或用 --db 显式指定
DEFAULT_DB_PATH = os.environ.get("HYDRO_DB", "")
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
    except Exception:
        pass
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
            pass
    return {"date": str(date), "points": pts, "verts": verts,
            "sourceFiles": sorted(files), "sectionNames": sorted(names),
            "conflictCount": conflicts, "rowCount": len(rows), "skippedRows": skipped}


def db_water_level(con: Any, code: str, date: str) -> Optional[float]:
    try:
        row = con.execute("SELECT value FROM daily_water_level"
                          " WHERE station_code=? AND date=?", (code, date)).fetchone()
    except Exception:
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
        except Exception:
            pass

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
    elev_base = elev_min - (10.0 * v_scale / 1000.0)
    kx = 1000.0 / h_scale
    ky = 1000.0 / v_scale
    ox, oy = args.origin_xy

    mapped = [((off - offset_min) * kx + ox, (elev - elev_base) * ky + oy)
              for off, elev in all_pts]
    bbox = {"x0": min(p[0] for p in mapped), "y0": min(p[1] for p in mapped),
            "x1": max(p[0] for p in mapped), "y1": max(p[1] for p in mapped)}
    width_mm = bbox["x1"] - bbox["x0"]
    height_mm = bbox["y1"] - bbox["y0"]

    prefix = (args.layer_prefix or "AI-SEC").strip()
    if not prefix:
        raise SectionError("BAD_ARGS", "--layer-prefix 不能为空")

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
        for s, ser in zip(raw, series):
            keep = [(p, v) for p, v in zip(s["points"], s["verts"])
                    if not any(o["vert"] == v for o in
                               next(d["outliers"] for d in dq_series if d["id"] == ser["id"]))]
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
    if height_mm > paper["usableHeightMm"] + 1e-9:
        warnings.append({"code": "TOO_TALL",
                         "message": "断面高度超出图幅可用区（hScale 升档不影响纵向，需调小 vScale）",
                         "heightMm": round(height_mm, 3),
                         "usableHeightMm": paper["usableHeightMm"]})

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
        "textHeight": 2.0,
        "waterTextHeight": 2.5,
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

    # §4.2 坐标映射（逐字）
    elev_base = elev_min - (10.0 * v_scale / 1000.0)
    kx = 1000.0 / used_h          # mm per m（横向）
    ky = 1000.0 / v_scale         # mm per m（纵向）

    ox, oy = args.origin_xy
    origin = [float(ox), float(oy)]
    pts_mm = [((o - off_min) * kx + ox, (e - elev_base) * ky + oy) for o, e in zip(offs, elevs)]

    bbox = {
        "x0": min(p[0] for p in pts_mm),
        "y0": min(p[1] for p in pts_mm),
        "x1": max(p[0] for p in pts_mm),
        "y1": max(p[1] for p in pts_mm),
    }
    width_mm = bbox["x1"] - bbox["x0"]
    height_mm = bbox["y1"] - bbox["y0"]

    if paper["usableHeightMm"] <= 0:
        raise SectionError("BAD_ARGS", "图幅可用高度非正")

    prefix = (args.layer_prefix or "AI-SEC").strip()
    if not prefix:
        raise SectionError("BAD_ARGS", "--layer-prefix 不能为空")
    layers = {
        "ground": prefix + "-GROUND",
        "water": prefix + "-WATER",
        "anno": prefix + "-ANNO",
        "axis": prefix + "-AXIS",
        "text": prefix + "-TEXT",
    }

    labels = {
        "elevHeightMm": 2.0,
        "pointNoHeightMm": 2.0,
        "waterHeightMm": 2.5,
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
            "textHeight": 2.0,
            "waterTextHeight": 2.5,
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

    if height_mm > paper["usableHeightMm"] + 1e-9:
        warnings.append({
            "code": "TOO_TALL",
            "message": "断面高度超出图幅可用区（hScale 升档不影响纵向，需调小 vScale）",
            "heightMm": round(height_mm, 3),
            "usableHeightMm": paper["usableHeightMm"],
        })

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
    p.add_argument("--db", default=DEFAULT_DB_PATH, help="hydro_data.db 路径（默认取环境变量 HYDRO_DB）")
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


def _series_summary(points: List[List[float]], label: Any) -> Dict[str, Any]:
    offs = [p[0] for p in points]
    elevs = [p[1] for p in points]
    return {"label": label, "points": len(points),
            "offsetMin": min(offs) if offs else None,
            "offsetMax": max(offs) if offs else None,
            "elevMin": min(elevs) if elevs else None,
            "elevMax": max(elevs) if elevs else None}


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
            except Exception:
                pass
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
        db_series = [{"date": s["date"], "points": s["points"]} for s in model["series"]]
        cmp = compare_db_vs_excel(db_series, blocks)
        for row in cmp["byDate"]:
            ser = next((s for s in model["series"] if s["date"] == row.get("date")), None)
            if ser is not None and ser.get("waterLevels"):
                row["dbWaterLevel"] = ser["waterLevels"][0]["value"]
        cmp["xlsPath"] = os.path.abspath(xls_path)
        model["sourceCompare"] = cmp
        code = "SOURCE_COMPARE_DIFF" if cmp["differingCount"] else "SOURCE_COMPARE_SAME"
        msg = ("DB vs 源 .xls：比对 %d 点，%d 处不同%s"
               % (cmp["compared"], cmp["differingCount"],
                  "（源表高程被截断成无小数点短文本，DB 为正确值）"
                  if cmp["differingCount"] else "（两者一致）"))
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
