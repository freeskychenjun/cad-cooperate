#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""summarize.py - render the human readable M3-E4 benchmark report from summary.json.

Kept in Python (not PowerShell) because the .ps1 runner must stay pure ASCII while this
report is Chinese. The runner calls it automatically after writing summary.json.

    python summarize.py --summary <out>/summary.json \
        --tasks tests/bench/tasks.json --out <out>/summary.md
"""

from __future__ import annotations

import json
import os
import sys

CAT_LABEL = {
    "read": "读/查",
    "edit": "改图",
    "section": "断面",
    "check": "检核",
    "stability": "异常/稳定性",
}
KIND_LABEL = {
    "success": "成功",
    "capability_gap": "能力缺口",
    "failure": "失败",
    "product_bug": "产品缺陷",
    "mixed": "部分成功",
    "setup_unavailable": "未能测（环境不满足）",
    "error": "错误",
}
CAT_ORDER = ["read", "edit", "section", "check", "stability"]


def read_json(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return json.loads(raw.decode("utf-8", "replace"))


def n(v, digits=0):
    if v is None:
        return "-"
    try:
        if digits == 0:
            return "{:,.0f}".format(float(v))
        return "{:,.{d}f}".format(float(v), d=digits)
    except (TypeError, ValueError):
        return str(v)


def pct(v):
    if v is None:
        return "-"
    return "%.0f%%" % float(v)


def find_task(summary, task_id):
    for t in summary.get("tasks") or []:
        if t.get("id") == task_id:
            return t
    return None


def step_median_wall(task, step_name):
    """median wall of one named step across the task's reps"""
    vals = []
    for rep in task.get("reps") or []:
        for s in rep.get("steps") or []:
            if s.get("step") == step_name:
                vals.append(float(s.get("wallMs") or 0))
    if not vals:
        return None
    vals.sort()
    mid = len(vals) // 2
    return vals[mid] if len(vals) % 2 else (vals[mid - 1] + vals[mid]) / 2.0


def main(argv):
    args = {"summary": None, "tasks": None, "out": None}
    i = 0
    while i < len(argv):
        tok = argv[i]
        if tok in ("--summary", "--tasks", "--out") and i + 1 < len(argv):
            args[tok.lstrip('-')] = argv[i + 1]
            i += 2
        else:
            i += 1
    if not args["summary"]:
        print("usage: summarize.py --summary <summary.json> [--tasks tasks.json] [--out summary.md]")
        return 2
    summary = read_json(args["summary"])
    tasks_def = read_json(args["tasks"]) if args["tasks"] and os.path.exists(args["tasks"]) else {"tasks": []}
    goal_by_id = {t.get("id"): t.get("goal") for t in tasks_def.get("tasks") or []}

    meta = summary.get("meta") or {}
    micro = summary.get("micro") or {}
    agg = summary.get("aggregate") or {}
    tasks = summary.get("tasks") or []
    cat_agg = agg.get("byCategory") or {}
    cmd_rows = agg.get("byCommand") or []

    L = []
    L.append("# M3-E4 度量基准报告（20 条真实 CAD 任务）")
    L.append("")
    L.append("> 由 `scripts/bench/run_bench.ps1` 自动生成；判定逻辑在 `tests/bench/checkers/check.py`，任务定义在 `tests/bench/tasks.json`。")
    L.append("> 每条任务 = 一句用户会说的话 → agent 的**真实命令序列** → 自动判定；度量的是“任务级”成本（命令地板价 × 命令数 + read 等重活），不是单条命令微基准。")
    L.append("")
    L.append("| 项 | 值 |")
    L.append("|---|---|")
    L.append("| 时间戳 | %s |" % meta.get("stamp"))
    L.append("| 产品 | %s（`%s`） |" % (meta.get("product"), meta.get("productArgs")))
    L.append("| 入口 | %s |" % meta.get("entry"))
    L.append("| 重复次数 | 每条 %s 次（Q4 大图读 1 次） |" % meta.get("repeat"))
    L.append("| 被测代码 | %s |" % ("冻结快照（见 summary.json meta.freeze.sha256）" if meta.get("freeze") else "工作区实时文件"))
    L.append("| 总墙钟 | %s s |" % n((summary.get("durationMs") or 0) / 1000.0, 1))
    L.append("")

    # ---------------------------------------------------------------- headline numbers
    L.append("## 一、基线三数字（先看这里）")
    L.append("")
    L.append("| 指标 | 数值 | 说明 |")
    L.append("|---|---|---|")
    floor = micro.get("medianFloorMs")
    helper = micro.get("avgHelperMs")
    avg_wall = micro.get("avgWallMs")
    L.append("| **每命令均值（status）** | **%s ms** | 含 PS 启动 + wrapper 全部开销；helper 本体仅 %s ms ⇒ 地板价 ≈ %s ms |"
             % (n(avg_wall), n(helper), n(floor)))
    q4 = find_task(summary, "Q4-big-read")
    q4_read = step_median_wall(q4, "read1") if q4 else None
    q4_total = (q4 or {}).get("median", {}).get("wallMs")
    L.append("| **大图 read（624 实体真实水系图）** | **%s ms** | 任务总墙钟 %s ms；这是 E1 分级读的主要目标（目标 <10 s） |"
             % (n(q4_read), n(q4_total)))
    s3 = find_task(summary, "S3-water-edit-composite")
    s3_total = (s3 or {}).get("median", {}).get("wallMs")
    L.append("| **10 命令复合任务（S3）** | **%s ms** | %s 条命令；每命令 %s ms |"
             % (n(s3_total), n((s3 or {}).get("median", {}).get("commands")),
                n((s3 or {}).get("median", {}).get("wallPerCommandMs"))))
    L.append("")
    L.append("**地板价证据**：`status` 跑 %s 次 → 平均 %s ms / 最小 %s ms / 中位 %s ms；"
             "对应 helper 自报耗时平均 %s ms，⇒ 每命令约 %s ms 花在“起 PowerShell + wrapper + JSON 往返”上。"
             % (len(micro.get("runs") or []), n(micro.get("avgWallMs")), n(micro.get("minWallMs")),
                n(micro.get("medianWallMs")), n(helper), n(floor)))
    L.append("")

    # ---------------------------------------------------------------- task table
    L.append("## 二、逐任务结果（中位数 = 三次取中位）")
    L.append("")
    L.append("| 任务 | 类别 | 结果 | 命令数 | 中位墙钟 ms | 每命令 ms | 响应字节 | 近似 token | 一键撤销 |")
    L.append("|---|---|---|---|---|---|---|---|---|")
    for t in tasks:
        undo = t.get("undo")
        undo_txt = "-"
        if undo:
            undo_txt = "✅ %s ms" % n(undo.get("medianWallMs")) if undo.get("ok") else "❌"
        L.append("| `%s` | %s | %s | %s | %s | %s | %s | %s | %s |"
                 % (t.get("id"), CAT_LABEL.get(t.get("category"), t.get("category")),
                    KIND_LABEL.get(t.get("kind"), t.get("kind")),
                    n(t.get("median", {}).get("commands")),
                    n(t.get("median", {}).get("wallMs")),
                    n(t.get("median", {}).get("wallPerCommandMs")),
                    n(t.get("median", {}).get("bytes")),
                    n(t.get("median", {}).get("tokensEst")),
                    undo_txt))
    L.append("")
    L.append("> “能力缺口”= 任务本身合理、但当前命令面还没有对应原语（探针证实命令被 `BAD_ARGS` 干净拒绝）。这类任务不计入失败率，单列统计；E2 上通用写原语后应转为“成功”。")
    L.append("")

    # ---------------------------------------------------------------- category table
    L.append("## 三、按类别汇总（哪类任务最贵）")
    L.append("")
    L.append("| 类别 | 任务数 | 平均命令数 | 每任务中位墙钟 ms | 每任务字节 | 成功 | 缺口 | 缺陷 | 失败 | 撤销成功 |")
    L.append("|---|---|---|---|---|---|---|---|---|---|")
    for cat in CAT_ORDER + [c for c in cat_agg if c not in CAT_ORDER]:
        c = cat_agg.get(cat)
        if not c:
            continue
        L.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s/%s |"
                 % (CAT_LABEL.get(cat, cat), n(c.get("tasks")), n(c.get("avgCommands"), 1),
                    n(c.get("medianWallMsPerTask")), n(c.get("avgBytesPerTask")),
                    n(c.get("ok")), n(c.get("gap")), n(c.get("bug")), n(c.get("failed")),
                    n(c.get("undoOk")), n(c.get("undos"))))
    L.append("")

    # ---------------------------------------------------------------- command breakdown
    L.append("## 四、贵在哪一步（按命令类型聚合全部步骤）")
    L.append("")
    L.append("| 命令/步骤 | 次数 | 平均墙钟 ms | 平均 helper ms | 平均地板价 ms | 平均字节 | 总墙钟 ms |")
    L.append("|---|---|---|---|---|---|---|")
    for row in cmd_rows:
        L.append("| `%s` | %s | %s | %s | %s | %s | %s |"
                 % (row.get("label"), n(row.get("count")), n(row.get("avgWallMs")),
                    n(row.get("avgHelperMs")), n(row.get("avgFloorMs")),
                    n(row.get("avgBytes")), n(row.get("totalWallMs"))))
    L.append("")
    L.append("读法：`cad:read` / `cad:open` / `cad:section` 的“平均地板价”就是 E1 daemon 能直接吃掉的收益；"
             "`cad:read` 的 helper 毫秒是 E1 分级读要压缩的部分；`py:*` 是数据侧（不进 daemon，但也在任务总成本里）。")
    L.append("")

    # ---------------------------------------------------------------- top expensive
    ranked = [t for t in tasks if t.get("kind") != "setup_unavailable" and t.get("median")]
    ranked.sort(key=lambda t: t.get("median", {}).get("wallMs") or 0, reverse=True)
    L.append("## 五、最贵任务 TOP5")
    L.append("")
    L.append("| # | 任务 | 类别 | 中位墙钟 ms | 命令数 | 主要成本 |")
    L.append("|---|---|---|---|---|---|")
    for idx, t in enumerate(ranked[:5], 1):
        cmds = (t.get("median", {}).get("commands") or 0)
        per = t.get("median", {}).get("wallPerCommandMs")
        cost = "命令地板价 × %s（%s ms/条）" % (n(cmds), n(per))
        if t.get("id") == "Q4-big-read":
            cost = "单次 read 重活（helper %s ms）" % n(step_median_wall(t, "read1"))
        L.append("| %d | `%s` | %s | %s | %s | %s |"
                 % (idx, t.get("id"), CAT_LABEL.get(t.get("category"), t.get("category")),
                    n(t.get("median", {}).get("wallMs")), n(cmds), cost))
    L.append("")

    # ---------------------------------------------------------------- failures / gaps
    L.append("## 六、失败与能力缺口清单")
    L.append("")
    if not summary.get("failures"):
        L.append("无失败任务。")
    else:
        for f in summary.get("failures") or []:
            L.append("- **%s**（%s）：%s" % (f.get("id"), CAT_LABEL.get(f.get("category"), f.get("category")),
                                          "；".join(str(x) for x in (f.get("reasons") or [])[:3])))
    gaps = [t for t in tasks if t.get("kind") == "capability_gap"]
    if gaps:
        L.append("")
        L.append("能力缺口（合理任务、缺原语；E2 目标）：")
        for t in gaps:
            L.append("- `%s` — %s" % (t.get("id"), goal_by_id.get(t.get("id")) or t.get("goal")))
    bugs = summary.get("productBugs") or [t for t in tasks if t.get("kind") == "product_bug"]
    if bugs:
        L.append("")
        L.append("产品缺陷（任务本身合理，是被测产品当前实现的 bug 卡住）：")
        for b in bugs:
            L.append("- **%s**（%s）：%s" % (b.get("id"), CAT_LABEL.get(b.get("category"), b.get("category")),
                                          "；".join(str(x) for x in (b.get("reasons") or [])[:3])))
    L.append("")

    # ---------------------------------------------------------------- hygiene & notes
    L.append("## 七、进程与稳定性")
    L.append("")
    hyg = summary.get("hygiene") or {}
    L.append("| 项 | 值 |")
    L.append("|---|---|")
    L.append("| 本脚本自起实例 pid | %s |" % ((summary.get("launch") or {}).get("pid")))
    L.append("| finish -Quit 退出码 | %s |" % hyg.get("finishExit"))
    L.append("| finish 后残留 CAD 进程（本产品） | %s |" % hyg.get("processesAfterFinish"))
    foreign = hyg.get("foreignProcesses") or []
    L.append("| 其它产品的 CAD 进程（不归本次运行） | %s |"
             % (("、".join("pid %s %s" % (f.get("pid"), f.get("name")) for f in foreign)) or "无"))
    L.append("| 任务期间本产品最多进程数 | %s |" % (hyg.get("maxTaskProcesses") or "1（仅本实例）"))
    L.append("")
    L.append("## 八、口径与已知限制")
    L.append("")
    L.append("1. **墙钟含 PowerShell 启动**：基线走 `powershell -File scripts\\cad.ps1`，所以每条命令 ≈ 4 s 地板价；E1 的 `CadClient.exe` + daemon 后应大幅下降（同一 runner 用 `-Entry client` 复跑即可对比）。")
    L.append("2. **响应字节 ≈ token**：按 UTF-8 字节 / 3.2 估算（中文为主时的粗口径），用于看“读一次图要灌多少上下文”，不替代真实 tokenizer。")
    L.append("3. **大图读（Q4）只跑 1 次**：单次 ≥200 s，为控制基线总时长；量级与三次中位数同阶（历史实测 209 s / 224 s）。")
    L.append("4. **能力缺口不算失败**：改图类 5 条探针证实命令面缺 `entity/query`（E2 才补），因此单独统计；它们的“命令数/墙钟”只是探针成本，不代表任务成本。")
    L.append("5. **被测代码被冻结**：`-Freeze` 把 `scripts\\cad.ps1` + `scripts\\helper\\*.cs` 复制到本次输出目录再跑，避免队友并行改代码把两个版本混进同一次基线；快照 SHA256 见 `summary.json`。")
    L.append("")
    L.append("---")
    L.append("")
    L.append("*由 tests/bench/checkers/summarize.py 生成*")

    text = "\n".join(L) + "\n"
    if args["out"]:
        with open(args["out"], "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote " + args["out"])
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
