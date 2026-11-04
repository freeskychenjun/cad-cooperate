#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_fixtures.py -- 生成 tests/fixtures 下的测试数据（纯离线，零 COM）。

运行：python tests\\make_fixtures.py

产出（全部由本脚本生成，可重复再生）：
  section-30.xlsx          30 点合成断面（起点距 0..290 步长 10 m，高程 38.55..44.30 m，
                           深泓点在第 15 点 / 起点距 140 m），表头行「起点距/高程」，
                           另在 D1/D2 附 1 条设计水位 42.30（供人读，程序不解析；runner 用
                           --water "42.30=设计水位" 传入，与 DESIGN §4.1「水位由参数指定」一致）。
  section-30-reversed.xlsx 与上表同数据、行序完全反向（上游→下游倒置），用于验证
                           prepare.py 的「自动反转 + ORDER_REVERSED 告警」。
  bad-2points.xlsx         只有 2 个有效点  -> 期望 BAD_DATA
  bad-nonmono.xlsx         起点距非单调    -> 期望 BAD_DATA
  bad-empty.xlsx           数据行中高程空值 -> 期望 BAD_DATA

设计依据：DESIGN-M1.md §4.1 / §5.B。
高程真值同时被 tests/make_expected.py 独立硬编码一份（刻意不共享常量，
两份若不一致会在 verify_section.py 里暴露出来）。
"""

from __future__ import annotations

import os
import sys

from openpyxl import Workbook

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "fixtures")

# ---- 断面真值（30 点；与 make_expected.py 中的常量刻意各写一份） ----------------
OFFSETS = [i * 10.0 for i in range(30)]          # 0, 10, ..., 290 m
ELEVS = [
    44.20, 44.00, 43.70, 43.35, 43.00, 42.60, 42.20, 41.80, 41.30, 40.80,
    40.30, 39.80, 39.30, 38.85, 38.55, 38.70, 38.95, 39.30, 39.70, 40.10,
    40.60, 41.10, 41.60, 42.10, 42.50, 42.90, 43.30, 43.70, 44.00, 44.30,
]
WATER_LEVEL = 42.30
WATER_LABEL = "设计水位"


def _write_section(path: str, rows, extra_water: bool) -> None:
    wb = Workbook()
    ws = wb.active
    ws.title = "断面"
    ws["A1"] = "起点距"
    ws["B1"] = "高程"
    if extra_water:
        ws["D1"] = WATER_LABEL
        ws["D2"] = WATER_LEVEL
    r = 2
    for off, elev in rows:
        ws.cell(row=r, column=1, value=off)
        ws.cell(row=r, column=2, value=elev)
        r += 1
    wb.save(path)


def main() -> int:
    os.makedirs(FIX, exist_ok=True)

    pairs = list(zip(OFFSETS, ELEVS))
    _write_section(os.path.join(FIX, "section-30.xlsx"), pairs, extra_water=True)
    _write_section(os.path.join(FIX, "section-30-reversed.xlsx"), list(reversed(pairs)),
                   extra_water=False)

    # bad-2points：只有 2 个有效点
    _write_section(os.path.join(FIX, "bad-2points.xlsx"),
                   [(0.0, 44.10), (10.0, 43.90)], extra_water=False)

    # bad-nonmono：起点距 0,10,20,15,40,50 —— 20 之后回落
    _write_section(os.path.join(FIX, "bad-nonmono.xlsx"),
                   [(0.0, 44.10), (10.0, 43.90), (20.0, 43.50),
                    (15.0, 43.20), (40.0, 42.90), (50.0, 42.60)],
                   extra_water=False)

    # bad-empty：第 4 个数据行高程为空（A 有值、B 为空 -> 必须 BAD_DATA，不能被当成表头跳过）
    wb = Workbook()
    ws = wb.active
    ws.title = "断面"
    ws["A1"] = "起点距"
    ws["B1"] = "高程"
    data = [(0.0, 44.10), (10.0, 43.90), (20.0, 43.50), (30.0, None),
            (40.0, 42.90), (50.0, 42.60)]
    r = 2
    for off, elev in data:
        ws.cell(row=r, column=1, value=off)
        if elev is not None:
            ws.cell(row=r, column=2, value=elev)
        r += 1
    wb.save(os.path.join(FIX, "bad-empty.xlsx"))

    for name in ("section-30.xlsx", "section-30-reversed.xlsx", "bad-2points.xlsx",
                 "bad-nonmono.xlsx", "bad-empty.xlsx"):
        p = os.path.join(FIX, name)
        print("WROTE %-28s %7d bytes" % (name, os.path.getsize(p)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
