#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""prep_io.py -- prepare.py 的「IO / 单元格解析」层（2026-11-05 从 prepare.py 原样搬出）。

职责：
  * SectionError（带机器可读 code 的失败）、stderr 告警 warn/warn_exc、原子写 write_json_atomic
  * 单元格数值解析：_is_blank / _to_number（严格）/ _soft_number（宽松）/ 表头归一化与别名匹配
  * Excel 读取：load_rows（.xls -> xlrd / .xlsx/.xlsm -> openpyxl）、_find_header_row
  * 两列（two-col）行 -> 点列：parse_points

**纯搬移**：除 import/模块头外，函数体与常量逐字来自 prepare.py；本文件不改变任何行为。
告警去重状态 _WARNED 是模块级单例：prepare.py 与其它模块必须引用**同一个对象**
（`from prep_io import _WARNED`），绝不能在别处再 `_WARNED = set()`。
"""

from __future__ import annotations

import json
import math
import os
import re
import sys
import tempfile
from typing import Any, Dict, List, Optional, Sequence, Tuple

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


# ----------------------------------------------------------------------------- 告警（stderr）
# stdout 恒为单段 JSON 协议，所以「不影响结果、但下游必须知道」的信息一律走 stderr。
# 同一 (code, message) 只打一行，正常路径不刷屏；异常对象本身绝不进 stdout。
_WARNED: set = set()


def warn(code: str, message: str, **details: Any) -> None:
    """往 stderr 打一行：`[prepare] warn <CODE>: <message> [k=v ...]`。"""
    key = (code, message)
    if key in _WARNED:
        return
    _WARNED.add(key)
    tail = "".join(" %s=%s" % (k, v) for k, v in details.items()) if details else ""
    sys.stderr.write("[prepare] warn %s: %s%s\n" % (code, message, tail))
    sys.stderr.flush()


def warn_exc(code: str, exc: BaseException, **details: Any) -> None:
    """吞错点统一格式：`<ExcType>: <msg>`（DB 表损坏与「确实无记录」因此可区分）。"""
    warn(code, "%s: %s" % (type(exc).__name__, exc), **details)


def write_json_atomic(path: str, payload: Dict[str, Any]) -> None:
    """先写同目录临时文件再 os.replace：失败时绝不留下半截/残留输出文件。"""
    out_dir = os.path.dirname(os.path.abspath(path)) or "."
    os.makedirs(out_dir, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".section-model-", suffix=".tmp", dir=out_dir)
    replaced = False
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        os.replace(tmp, path)
        replaced = True
    finally:
        # 刻意不用 except BaseException：KeyboardInterrupt/SystemExit 必须照常向上传播
        # （Ctrl+C 不能被静默），这里只负责清掉半截临时文件。
        if not replaced:
            try:
                if os.path.exists(tmp):
                    os.remove(tmp)
            except OSError as exc:
                warn_exc("TMP_CLEANUP_FAILED", exc, path=path)


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


# §4.1 兼容列名
OFFSET_ALIASES = ("起点距", "桩号", "距离", "offset", "distance", "station")
ELEV_ALIASES = ("高程", "河底高程", "elevation", "elev", "height")


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
        except Exception as exc:
            # 关工作簿失败不影响已读出的行，但必须留痕（否则「读取失败」会被当成「表里没数据」）
            warn_exc("WORKBOOK_CLOSE_FAILED", exc, path=path)
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
