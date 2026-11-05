# -*- coding: utf-8 -*-
"""通用版：从图纸几何 dump 生成"起点距-高程"断面表（东总干渠各分段通用）。

环境变量
  ZWC_GEOM      几何 dump 的 JSON（ZwcClient read -Mode geom 的输出）
  ZWC_CACHE     解析后的中间缓存路径
  ZWC_TAG       输出文件名标签，如 "16+500~24+700"
  ZWC_UNIT_NOTE 单位说明（可选）

管线与 1+300~16+500 那段完全一致：
  断面线 dmx(LINE) / 高程点 GCD(块插入点Z) / 桩号 KZD(TEXT)
  左岸 = 面向下游左手侧；下游方向 = 设计中心线的局部切向（中心线句柄自动识别为顶点最多的那条）
"""
import datetime
import json
import math
import os
import re

from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.utils import get_column_letter

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
OUTDIR = os.path.join(REPO, "output")
TMP = os.path.join(os.environ["TEMP"], "zwcad-cooperate")

SRC = os.environ.get("ZWC_GEOM") or os.path.join(os.environ["TEMP"], "zwc-geom-all.utf8.json")
CACHE = os.environ.get("ZWC_CACHE") or os.path.join(TMP, "sections-geom-cache.json")
TAG = os.environ.get("ZWC_TAG", "")
UNIT_NOTE = os.environ.get("ZWC_UNIT_NOTE", "")

ON_LINE_TOL = 0.50
LABEL_R = 8.0
DEDUP_D = 0.05
MIN_PTS = 3


def sdist_t(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    if l2 == 0:
        return math.hypot(px - ax, py - ay), 0.0
    t = ((px - ax) * dx + (py - ay) * dy) / l2
    tc = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + tc * dx), py - (ay + tc * dy)), tc


def chain_m(s):
    m = re.match(r"^(\d+)\+(\d+(?:\.\d+)?)$", (s or "").strip())
    return float(m.group(1)) * 1000 + float(m.group(2)) if m else None


def load_geometry():
    if os.path.exists(CACHE):
        return json.load(open(CACHE, encoding="utf-8"))
    d = json.load(open(SRC, encoding="utf-8"))
    ents = d["result"]["entities"]
    seen = set()
    uniq = [e for e in ents if not (e["handle"] in seen or seen.add(e["handle"]))]
    lines, blocks, texts, kzd_blocks = [], [], [], []
    cand_cl = []
    for e in uniq:
        lay, typ, h = e.get("layer") or "", e.get("type"), e["handle"]
        p = e.get("point") or [e.get("x"), e.get("y"), e.get("z")]
        if lay == "dmx" and typ == "AcDbLine":
            b = e.get("p2")
            lines.append({"h": h, "ax": p[0], "ay": p[1], "bx": b[0], "by": b[1]})
        elif lay == "GCD" and typ == "AcDbBlockReference":
            blocks.append({"h": h, "x": p[0], "y": p[1], "z": p[2] if len(p) > 2 else 0.0})
        elif lay == "KZD" and typ == "AcDbText":
            texts.append({"h": h, "text": (e.get("text") or "").strip(), "x": p[0], "y": p[1]})
        elif lay == "KZD" and typ == "AcDbBlockReference":
            kzd_blocks.append({"h": h, "x": p[0], "y": p[1], "z": p[2] if len(p) > 2 else 0.0})
        if "初设干渠治理段" in lay and typ in ("AcDbPolyline", "AcDb2dPolyline"):
            vs = [[v[0], v[1]] for v in (e.get("vertices") or []) if len(v) >= 2]
            if len(vs) >= 2:
                cand_cl.append((len(vs), h, vs))
    centre, clh = None, None
    if cand_cl:
        cand_cl.sort(reverse=True)
        _, clh, centre = cand_cl[0]
    payload = {"doc": d["doc"]["name"], "lines": lines, "blocks": blocks, "texts": texts,
               "kzd_blocks": kzd_blocks, "centre": centre, "centre_handle": clh}
    json.dump(payload, open(CACHE, "w", encoding="utf-8"), ensure_ascii=False)
    return payload


def centre_tangent(centre, px, py):
    best = (1e18, 1.0, 0.0)
    for (x1, y1), (x2, y2) in zip(centre, centre[1:]):
        d, _ = sdist_t(px, py, x1, y1, x2, y2)
        if d < best[0]:
            L = math.hypot(x2 - x1, y2 - y1)
            if L:
                best = (d, (x2 - x1) / L, (y2 - y1) / L)
    return best[1], best[2]


def build():
    g = load_geometry()
    lines, blocks, texts, centre = g["lines"], g["blocks"], g["texts"], g["centre"]
    print("输入：断面线 %d | GCD 高程点块 %d | KZD 文本 %d | 中心线 %s（%d 顶点）"
          % (len(lines), len(blocks), len(texts), g.get("centre_handle"),
             len(centre or [])))

    chains, notes = {}, {}
    for t in texts:
        if re.match(r"^\d+\+\d+(?:\.\d+)?$", t["text"]):
            bucket = chains
        elif re.match(r"^\d+\.\d+$", t["text"]):
            bucket = notes
        else:
            continue
        best = (1e9, None)
        for ln in lines:
            d, _ = sdist_t(t["x"], t["y"], ln["ax"], ln["ay"], ln["bx"], ln["by"])
            if d < best[0]:
                best = (d, ln["h"])
        if best[0] <= LABEL_R:
            cur = bucket.get(best[1])
            if cur is None or best[0] < cur[0]:
                bucket[best[1]] = (best[0], t["text"])
    chains = {k: v[1] for k, v in chains.items()}
    notes = {k: v[1] for k, v in notes.items()}
    print("桩号匹配 %d/%d | 渠底注记匹配 %d" % (len(chains), len(lines), len(notes)))

    kzd_by_line = {}
    for b in g["kzd_blocks"]:
        best = (1e9, None)
        for ln in lines:
            d, _ = sdist_t(b["x"], b["y"], ln["ax"], ln["ay"], ln["bx"], ln["by"])
            if d < best[0]:
                best = (d, ln["h"])
        if best[0] <= ON_LINE_TOL:
            kzd_by_line[best[1]] = b

    by_line = {ln["h"]: [] for ln in lines}
    unassigned = 0
    for b in blocks:
        best = (1e9, None, 0.0)
        for ln in lines:
            d, t = sdist_t(b["x"], b["y"], ln["ax"], ln["ay"], ln["bx"], ln["by"])
            if d < best[0]:
                best = (d, ln["h"], t)
        if best[0] <= ON_LINE_TOL:
            by_line[best[1]].append((b, best[0], best[2]))
        else:
            unassigned += 1
    print("高程点归属 %d 个，未归属 %d 个" % (len(blocks) - unassigned, unassigned))

    # ---- 第一遍：每条断面的中点/桩号（先排好下游→上游的顺序）
    base_rows = []
    for ln in lines:
        ax, ay, bx, by = ln["ax"], ln["ay"], ln["bx"], ln["by"]
        L = math.hypot(bx - ax, by - ay)
        if L == 0:
            continue
        ch = chains.get(ln["h"], "")
        base_rows.append({"handle": ln["h"], "chain": ch,
                          "chain_m": chain_m(ch) if ch else None,
                          "a": (ax, ay), "b": (bx, by), "len": L,
                          "mid": ((ax + bx) / 2, (ay + by) / 2)})
    base_rows.sort(key=lambda r: -(r["chain_m"] if r["chain_m"] is not None else -1e12))

    # ---- 局部下游方向：相邻断面中点的弦（索引 i-1 更下游，故取 mid[i-1]-mid[i+1]）
    n = len(base_rows)
    tangents = []
    for i in range(n):
        if n == 1:
            tangents.append((1.0, 0.0))
            continue
        p_dn = base_rows[max(0, i - 1)]["mid"]
        p_up = base_rows[min(n - 1, i + 1)]["mid"]
        dx, dy = p_dn[0] - p_up[0], p_dn[1] - p_up[1]
        L = math.hypot(dx, dy) or 1.0
        tangents.append((dx / L, dy / L))

    rows, angles, ang_cl, cl_hits = [], [], [], 0
    for i, base in enumerate(base_rows):
        ax, ay = base["a"]
        bx, by = base["b"]
        L = base["len"]
        mx, my = base["mid"]
        tx, ty = tangents[i]
        lx, ly = -ty, tx
        left_is_b = ((bx - ax) * lx + (by - ay) * ly) > 0
        ux, uy = (bx - ax) / L, (by - ay) / L
        ang = math.degrees(math.acos(max(-1.0, min(1.0, abs(ux * tx + uy * ty)))))
        angles.append(ang)
        if centre:
            # 与设计中心线切向对照（仅当该断面靠近中心线时才有意义）
            cx, cy = centre_tangent(centre, mx, my)
            cd, _ = sdist_t(mx, my, centre[0][0], centre[0][1], centre[1][0], centre[1][1])
            ang_cl.append(math.degrees(math.acos(max(-1.0, min(1.0, abs(tx * cx + ty * cy))))))
            cl_hits += 1
        pts = []
        for b, dmin, t in by_line[base["handle"]]:
            off = (L - t * L) if left_is_b else (t * L)
            pts.append((off, b["z"], b["h"], dmin))
        pts.sort(key=lambda r: r[0])
        dedup = []
        for p in pts:
            if dedup and abs(dedup[-1][0] - p[0]) < DEDUP_D:
                continue
            dedup.append(p)
        rows.append({"handle": base["handle"], "chain": base["chain"], "chain_m": base["chain_m"],
                     "a": base["a"], "b": base["b"], "len": L, "left_is_b": left_is_b,
                     "left_end": "B端" if left_is_b else "A端", "mid": base["mid"], "pts": dedup,
                     "note": notes.get(base["handle"], ""),
                     "kzd_z": kzd_by_line.get(base["handle"], {}).get("z"), "angle": ang})
    for i, r in enumerate(rows, 1):
        r["idx"] = i
    angles.sort()
    ang_cl.sort()
    print("断面线与局部下游方向（相邻断面弦）夹角：中位 %.1f°，最小 %.1f°"
          % (angles[len(angles) // 2], angles[0]))
    if ang_cl:
        print("该下游方向与设计中心线切向的夹角：中位 %.1f°，最大 %.1f°（%d 条断面参与对照）"
              % (ang_cl[len(ang_cl) // 2], ang_cl[-1], cl_hits))
    return rows, g, len(blocks), unassigned, len(lines), len(chains)


def write_excel(rows, g, nblocks, unassigned, nlines, nmatch):
    os.makedirs(OUTDIR, exist_ok=True)
    base = "断面数据-东总干渠%s-起点距高程" % (TAG or "")
    xlsx = os.path.join(OUTDIR, base + ".xlsx")
    csv = os.path.join(OUTDIR, base + ".csv")
    wb = Workbook()
    bold, fill = Font(bold=True), PatternFill("solid", fgColor="DDEBF7")
    warn_fill = PatternFill("solid", fgColor="FFF2CC")

    def head(ws, headers, widths):
        for i, h in enumerate(headers, 1):
            c = ws.cell(row=1, column=i, value=h)
            c.font, c.fill = bold, fill
            c.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        ws.freeze_panes = "A2"
        for i, w in enumerate(widths, 1):
            ws.column_dimensions[get_column_letter(i)].width = w
        ws.row_dimensions[1].height = 30

    ws = wb.active
    ws.title = "断面汇总"
    head(ws, ["序号", "桩号", "桩号(m)", "断面线句柄", "左岸端X", "左岸端Y", "右岸端X", "右岸端Y",
              "断面长(m)", "左岸为", "高程点数", "起点距范围(m)", "高程范围(m)", "最低高程(m)",
              "渠底注记(m)", "渠底块Z(m)", "与渠线夹角(°)", "状态"],
         [6, 10, 9, 12, 13, 14, 13, 14, 9, 7, 9, 15, 15, 11, 11, 11, 12, 10])
    for r in rows:
        pts = r["pts"]
        left_pt = r["b"] if r["left_is_b"] else r["a"]
        right_pt = r["a"] if r["left_is_b"] else r["b"]
        ws.append([r["idx"], r["chain"], round(r["chain_m"], 2) if r["chain_m"] is not None else "",
                   r["handle"], round(left_pt[0], 3), round(left_pt[1], 3),
                   round(right_pt[0], 3), round(right_pt[1], 3), round(r["len"], 2),
                   r["left_end"], len(pts),
                   "%.2f~%.2f" % (pts[0][0], pts[-1][0]) if pts else "",
                   "%.2f~%.2f" % (min(p[1] for p in pts), max(p[1] for p in pts)) if pts else "",
                   round(min((p[1] for p in pts), default=0), 2) if pts else "",
                   r["note"], round(r["kzd_z"], 2) if r["kzd_z"] is not None else "",
                   round(r["angle"], 1), "OK" if len(pts) >= MIN_PTS else "点数不足"])
    for row in ws.iter_rows(min_row=2, max_row=ws.max_row, min_col=18, max_col=18):
        for c in row:
            if c.value == "点数不足":
                c.fill = warn_fill

    ws2 = wb.create_sheet("断面数据_长表")
    head(ws2, ["序号", "桩号", "起点距(m)", "反向起点距(m)", "高程(m)", "高程点句柄", "点到断面线距离(m)"],
         [6, 10, 12, 15, 10, 12, 15])
    for r in rows:
        for off, z, h, dmin in r["pts"]:
            ws2.append([r["idx"], r["chain"], round(off, 3), round(r["len"] - off, 3),
                        round(z, 3), h, round(dmin, 4)])

    ws3 = wb.create_sheet("断面数据_宽表")
    c0 = ws3.cell(row=1, column=1, value="起点距(m) ↓ / 高程(m) ↓")
    c0.font, c0.fill = bold, fill
    for j, r in enumerate(rows, 2):
        c = ws3.cell(row=1, column=j, value=r["chain"] or ("#" + str(r["idx"])))
        c.font, c.fill = bold, fill
        c.alignment = Alignment(horizontal="center")
    ws3.freeze_panes = "B2"
    ws3.column_dimensions["A"].width = 15
    maxn = max((len(r["pts"]) for r in rows), default=0)
    for k in range(maxn):
        for j, r in enumerate(rows, 2):
            if k < len(r["pts"]):
                ws3.cell(row=2 + k, column=j, value=round(r["pts"][k][0], 2))
    base_row = 3 + maxn
    c = ws3.cell(row=base_row, column=1, value="高程(m) ↓")
    c.font, c.fill = bold, fill
    for k in range(maxn):
        for j, r in enumerate(rows, 2):
            if k < len(r["pts"]):
                ws3.cell(row=base_row + 1 + k, column=j, value=r["pts"][k][1])

    ws4 = wb.create_sheet("说明")
    lens = sorted(r["len"] for r in rows)
    total = sum(len(r["pts"]) for r in rows)
    notes = [
        ("项目", "说明"),
        ("任务", "读取图纸中每条断面线的坐标，按从左岸到右岸整理起点距-高程，供水面线计算"),
        ("数据来源", "图纸：" + g["doc"] + "（实时读取，未修改图纸）"),
        ("断面线", "图层 dmx 的直线，共 %d 条；长 %.1f~%.1f m（中位 %.1f m）"
                  % (nlines, lens[0], lens[-1], lens[len(lens) // 2])),
        ("高程点", "图层 GCD 的块参照 %d 个；块插入点 Z 即地面高程；落在断面线上（≤%.2f m）的 %d 个全部采用，"
                  "另有 %d 个不靠断面线未采用" % (nblocks, ON_LINE_TOL, nblocks - unassigned, unassigned)),
        ("桩号", "图层 KZD 的文字（如 16+475），8 m 内最近匹配，%d/%d 条断面已匹配" % (nmatch, nlines)),
        ("起点距方向", "0 = 左岸（面向下游的左手侧），向右岸递增；下游方向取设计中心线（句柄 %s）的局部切向；"
                     "另附“反向起点距”一列便于对照" % g.get("centre_handle")),
        ("排序", "整个工作簿按 下游→上游 排列，与水面线从下游起推一致；每条断面内部按左岸→右岸排列"),
        ("渠底注记/渠底块", "图层 KZD 的高程文字与块 Z，仅作复核参考列，未计入剖面点"),
        ("去重", "同一起点距 %.2f m 内的重复高程点只保留最先出现者" % DEDUP_D),
        ("单位", UNIT_NOTE or "坐标、起点距、高程均为图纸单位（实测为米）"),
        ("合计", "断面 %d 条，剖面高程点 %d 个" % (len(rows), total)),
        ("生成时间", datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")),
    ]
    for a, b in notes:
        ws4.append([a, b])
    ws4.column_dimensions["A"].width = 16
    ws4.column_dimensions["B"].width = 120

    wb.save(xlsx)
    with open(csv, "w", encoding="utf-8-sig", newline="") as f:
        f.write("序号,桩号,起点距(m),反向起点距(m),高程(m),高程点句柄\n")
        for r in rows:
            for off, z, h, dmin in r["pts"]:
                f.write('%d,%s,%.3f,%.3f,%.3f,%s\n' % (r["idx"], r["chain"], off, r["len"] - off, z, h))
    print("已写出:", xlsx)
    print("已写出:", csv)
    return xlsx, csv


def preview(rows, picks):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        matplotlib.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "SimSun", "DejaVu Sans"]
        matplotlib.rcParams["axes.unicode_minus"] = False
    except Exception as exc:
        print("跳过预览图:", exc)
        return []
    made = []
    for key in picks:
        r = next((x for x in rows if x["chain"] == key), None)
        if not r or not r["pts"]:
            continue
        fig, ax = plt.subplots(figsize=(10, 3.6), dpi=130)
        ax.plot([p[0] for p in r["pts"]], [p[1] for p in r["pts"]], "-o", ms=4, lw=1.2, color="#1f77b4")
        for off, z, h, dmin in r["pts"]:
            ax.annotate("%.2f" % z, (off, z), textcoords="offset points", xytext=(0, 6),
                        ha="center", fontsize=7, color="#333")
        ax.set_xlabel("起点距 (m)  [左岸→右岸]")
        ax.set_ylabel("高程 (m)")
        ax.grid(alpha=.3, ls="--")
        ax.set_title("断面 %s  (句柄 %s, 长 %.1f m, %d 点)" % (r["chain"], r["handle"], r["len"], len(r["pts"])),
                     fontsize=10)
        fig.tight_layout()
        png = os.path.join(OUTDIR, "断面预览%s-%s.png" % (("(" + TAG + ")") if TAG else "", key.replace("+", "_")))
        fig.savefig(png)
        plt.close(fig)
        made.append(png)
        print("已写出:", png)
    return made


if __name__ == "__main__":
    rows, g, nblocks, unassigned, nlines, nmatch = build()
    total = sum(len(r["pts"]) for r in rows)
    counts = sorted(len(r["pts"]) for r in rows)
    print("每断面点数：min %d / 中位 %d / max %d；合计 %d"
          % (counts[0], counts[len(counts) // 2], counts[-1], total))
    cond = [r for r in rows if len(r["pts"]) < MIN_PTS]
    print("点数不足 %d 条：%s" % (len(cond), ", ".join(r["chain"] or r["handle"] for r in cond)))
    print("桩号范围：%s（第1行）~ %s（最后一行）" % (rows[0]["chain"], rows[-1]["chain"]))
    xlsx, csv = write_excel(rows, g, nblocks, unassigned, nlines, nmatch)
    preview(rows, [rows[0]["chain"], rows[1]["chain"], rows[len(rows) // 2]["chain"], rows[-1]["chain"]])
    r = rows[0]
    print("\n[自检] %s 句柄=%s 长=%.3f m 左岸端=%s 点数=%d" % (r["chain"], r["handle"], r["len"], r["left_end"], len(r["pts"])))
    for off, z, h, dmin in r["pts"]:
        print("   起点距 %7.3f m   高程 %6.2f m   (%s, 距线 %.4f m)" % (off, z, h, dmin))
