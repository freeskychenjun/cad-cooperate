# -*- coding: utf-8 -*-
"""验证：设计中心线 5E078 的顶点顺序是否 = 桩号增大方向（决定"左岸"方向是否成立）。"""
import json, math, os, re

TMP = os.path.join(os.environ["TEMP"], "zwcad-cooperate")
g = json.load(open(os.path.join(TMP, "sections-geom-cache.json"), encoding="utf-8"))
lines, texts, centre = g["lines"], g["texts"], g["centre"]


def sdist_t(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    if l2 == 0:
        return math.hypot(px - ax, py - ay), 0.0
    t = ((px - ax) * dx + (py - ay) * dy) / l2
    tc = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + tc * dx), py - (ay + tc * dy)), tc


# 每个断面的桩号（用中点最近桩号文字的规则，与 builder 一致）
chains = {}
for t in texts:
    if not re.match(r"^\d+\+\d+(\.\d+)?$", t["text"]):
        continue
    best = (1e9, None)
    for ln in lines:
        d, _ = sdist_t(t["x"], t["y"], ln["ax"], ln["ay"], ln["bx"], ln["by"])
        if d < best[0]:
            best = (d, ln["h"])
    if best[0] <= 8.0:
        cur = chains.get(best[1])
        if cur is None or best[0] < cur[0]:
            chains[best[1]] = (best[0], t["text"])


def cm(s):
    m = re.match(r"^(\d+)\+(\d+(?:\.\d+)?)$", s)
    return int(m.group(1)) * 1000 + float(m.group(2))


mid = {ln["h"]: ((ln["ax"] + ln["bx"]) / 2, (ln["ay"] + ln["by"]) / 2) for ln in lines}
sec = [(cm(v[1]), mid[h]) for h, v in chains.items()]
sec.sort()

print("中心线顶点数:", len(centre))
print("顶点 i -> 最近断面桩号 -> 到该断面的距离")
prev = -1
mono = True
for i, (x, y) in enumerate(centre):
    best = (1e9, None)
    for c, p in sec:
        d = math.hypot(x - p[0], y - p[1])
        if d < best[0]:
            best = (d, c)
    if i in (0, 1, 2, 3, 60, 124, 125, 126, 127):
        print("  %3d  %8.0f m   距 %.2f m   %s" % (i, best[1], best[0],
                                                  "倒序!" if best[1] < prev else ""))
    if best[1] < prev - 1:
        mono = False
        print("  !! 非单调 at vertex %d: %s after %s" % (i, best[1], prev))
    prev = best[1]
print("顶点顺序单调递增（= 桩号增大 = 下游方向）:", mono)

# 相邻顶点方向与"从起点到终点"总体方向的一致性
v0, v1 = centre[0], centre[-1]
print("中心线首点 %s  末点 %s" % (v0, v1))
print("首点最近断面桩号: %.0f ；末点最近断面桩号: %.0f"
      % (min(sec, key=lambda s: math.hypot(s[1][0] - v0[0], s[1][1] - v0[1]))[0],
         min(sec, key=lambda s: math.hypot(s[1][0] - v1[0], s[1][1] - v1[1]))[0]))
