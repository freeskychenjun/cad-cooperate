# -*- coding: utf-8 -*-
"""复核产物：xlsx 结构/顺序/数值一致性 + 未归属高程点的偏离分布。"""
import json, math, os, re, sys
from openpyxl import load_workbook

REPO = r"D:\DSH工作区\cad-cooperate"
TMP = os.path.join(os.environ["TEMP"], "cad-cooperate")
XLSX = os.path.join(REPO, "output", "断面数据-东总干渠-起点距高程.xlsx")

ok = True


def check(cond, msg):
    global ok
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond:
        ok = False


wb = load_workbook(XLSX, data_only=True)
print("sheets:", wb.sheetnames)
check(wb.sheetnames == ["断面汇总", "断面数据_长表", "断面数据_宽表", "说明"], "工作表名称与顺序")

ws = wb["断面汇总"]
rows = list(ws.iter_rows(values_only=True))
hdr, data = rows[0], [r for r in rows[1:] if r[0] is not None]
check(len(data) == 304, "断面条数 = 304（实际 %d）" % len(data))
check(data[0][1] == "16+475", "第一行是 16+475（实际 %s）" % data[0][1])
check(data[1][1] == "16+450", "第二行是 16+450（实际 %s）" % data[1][1])
check(data[-1][1] in ("1+324",), "最后一行是最上游断面（实际 %s）" % data[-1][1])
cm = [r[2] for r in data]
check(all(a > b for a, b in zip(cm, cm[1:])), "桩号严格递减（下游→上游）")
check(all(r[17] == "OK" for r in data), "全部断面状态 OK")
check(all((r[10] or 0) >= 3 for r in data), "每断面点数 >= 3")

ws2 = wb["断面数据_长表"]
rows2 = [r for r in list(ws2.iter_rows(values_only=True))[1:] if r[0] is not None]
check(len(rows2) == 2896, "长表行数 = 2896（实际 %d）" % len(rows2))
check(sum(r[10] for r in data) == len(rows2), "汇总点数合计 == 长表行数")
# 每条断面内 起点距 递增，且 == 反向起点距 的镜像
bad = 0
from collections import defaultdict
d = defaultdict(list)
for r in rows2:
    d[r[0]].append(r)
for idx, rs in d.items():
    offs = [r[2] for r in rs]
    if any(a > b for a, b in zip(offs, offs[1:])):
        bad += 1
check(bad == 0, "每条断面内部起点距从左岸到右岸递增")
sample = d[1]
L = data[0][8]
check(all(abs((r[2] + r[3]) - L) < 0.01 for r in sample), "起点距 + 反向起点距 = 断面长（16+475 逐点）")

ws3 = wb["断面数据_宽表"]
check(ws3.max_column == 305, "宽表列数 = 1 + 304 断面（实际 %d）" % ws3.max_column)
check(ws3.cell(row=1, column=2).value == "16+475", "宽表第一列断面 = 16+475")

# ---- 与上一版（修正版）交叉核对：点数与高程集合应一致（起点距互为镜像）
old = os.path.join(REPO, "output", "断面数据-东总干渠-修正版.xlsx")
if os.path.exists(old):
    wbo = load_workbook(old, data_only=True)
    o2 = [r for r in list(wbo["断面数据_长表"].iter_rows(values_only=True))[1:] if r[1]]
    od = defaultdict(list)
    for r in o2:
        od[r[1]].append(r)
    same_cnt = 0
    same_z = 0
    tot = 0
    for r in data:
        ch = r[1]
        tot += 1
        mine = d[r[0]]
        theirs = od.get(ch, [])
        if len(mine) == len(theirs):
            same_cnt += 1
        zs_a = sorted(round(x[4], 2) for x in mine)
        zs_b = sorted(round(x[4], 2) for x in theirs)
        if zs_a == zs_b:
            same_z += 1
    check(same_cnt == tot, "与上一版逐断面点数一致 %d/%d" % (same_cnt, tot))
    check(same_z == tot, "与上一版逐断面高程集合一致 %d/%d" % (same_z, tot))

# ---- 未归属高程点偏离分布
g = json.load(open(os.path.join(TMP, "sections-geom-cache.json"), encoding="utf-8"))
lines, blocks = g["lines"], g["blocks"]


def sdist(px, py, ln):
    ax, ay, bx, by = ln["ax"], ln["ay"], ln["bx"], ln["by"]
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    t = ((px - ax) * dx + (py - ay) * dy) / l2
    tc = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + tc * dx), py - (ay + tc * dy))


far = []
for b in blocks:
    dmin = min(sdist(b["x"], b["y"], ln) for ln in lines)
    if dmin > 0.5:
        far.append(dmin)
far.sort()
print("\n未归属高程点 %d 个，距离分布: %s" % (len(far), [round(x, 2) for x in far[:12]]))
print("  全部 > 0.5 m；最近的 12 个见上（若多为 0.5~0.7 m 可考虑放宽容差）")

print("\nRESULT:", "ALL PASS" if ok else "HAS FAILURES")
sys.exit(0 if ok else 3)
