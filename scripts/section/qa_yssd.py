#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""qa_yssd.py -- 实测大断面成果表（yssd）解析 / 数据质量 / 两次施测对比 的 QA 报告生成器。

复用 prepare.py 的解析与质量闸门（同一套代码路径，保证报告与模型一致），输出：
  - JSON（机器可读，含全部可疑点、两次施测对比明细）
  - Markdown（人读）

用法（.xls 需要 xlrd，加 PYTHONPATH=tools\\py-libs）：
  python scripts\\section\\qa_yssd.py --data <xls> --out-json tmp\\real\\x.qa.json ^
      --out-md tmp\\real\\x.qa.md [--model name=path ...] [--block 1] [--paper A1]
      [--h-scale 4000] [--v-scale 200]
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
from typing import Any, Dict, List, Optional, Sequence, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prepare  # noqa: E402


def _pct(a: int, b: int) -> str:
    return "%.1f%%" % (100.0 * a / b) if b else "-"


def build_report(data_path: str, sheet: Optional[str], block_sel: Optional[int],
                 models: Sequence[Tuple[str, str]]) -> Dict[str, Any]:
    rows, meta = prepare.load_rows(data_path, sheet)
    header = prepare.find_yssd_header(rows)
    if header is None:
        raise SystemExit("qa: 未识别到实测大断面表头")
    header_idx, groups = header
    blocks = prepare.parse_yssd_blocks(rows, header_idx, groups)

    report: Dict[str, Any] = {
        "generatedAt": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "source": {
            "path": os.path.abspath(data_path),
            "sizeBytes": os.path.getsize(data_path),
            "sheet": meta.get("sheet"),
            "reader": meta.get("reader"),
            "totalRows": meta.get("totalRows"),
        },
        "table": {
            "headerRow": header_idx + 1,
            "groupCount": len(groups),
            "columns": [{"point": (g[0] + 1) if g[0] is not None else None,
                         "offset": g[1] + 1, "elev": g[2] + 1} for g in groups],
        },
        "blocks": [],
        "crossBlock": {},
        "models": [],
        "conclusion": [],
    }

    # ---- 逐块统计 + 质量闸门
    for b in blocks:
        pts, pinfo = prepare.entries_to_points(b, drop_label_rows=True)
        pts_kept_labels, _ = prepare.entries_to_points(b, drop_label_rows=False)
        dq = prepare.assess_data_quality(pts, b, blocks)
        dq.update({"duplicateOffsets": pinfo["duplicates"],
                   "nonMonotonic": pinfo["nonMonotonic"],
                   "labelRowsDropped": pinfo["labelRowsDropped"]})
        offsets = [p["offset"] for p in pts]
        elevs = [p["elev"] for p in pts]
        reliable = [p for p in pts if not p.get("_suspect")]
        report["blocks"].append({
            "index": b["index"],
            "date": b["date"],
            "dateKey": b["dateKey"],
            "waterLevel": b["waterLevel"],
            "sectionName": b["sectionName"],
            "metaRow": b["metaRow"],
            "entries": len(b["entries"]),
            "numbered": b["numbered"],
            "labels": b["labels"],
            "meta": b["metaCount"],
            "invalid": b["invalid"],
            "pointsAfterLabelFilter": len(pts),
            "pointsWithLabelsKept": len(pts_kept_labels),
            "labelRowsDropped": pinfo["labelRowsDropped"],
            "offsetMin": min(offsets) if offsets else None,
            "offsetMax": max(offsets) if offsets else None,
            "elevMin": min(elevs) if elevs else None,
            "elevMax": max(elevs) if elevs else None,
            "medianElev": dq["medianElev"],
            "suspectCount": dq["suspectCount"],
            "suspectShare": _pct(dq["suspectCount"], len(pts)),
            "pointsAfterDropSuspect": len(reliable),
            "duplicateOffsets": pinfo["duplicates"],
            "nonMonotonic": pinfo["nonMonotonic"],
            "ignoredRows": pinfo["ignored"],
            "suspects": dq["suspect"],
            "reliableElevMin": min([p["elev"] for p in reliable]) if reliable else None,
            "reliableElevMax": max([p["elev"] for p in reliable]) if reliable else None,
        })

    # ---- 两次施测对比（按起点距匹配；点号在两次施测里不复用同间距，故不能只按点号）
    by_offset: Dict[float, Dict[int, Dict[str, Any]]] = {}
    for b in blocks:
        for e in b["entries"]:
            if e["kind"] in ("number", "label") and e["offset"] is not None and e["elev"] is not None:
                by_offset.setdefault(round(e["offset"], 6), {})[b["index"]] = {
                    "elev": e["elev"], "elevRaw": e["elevRaw"],
                    "point": e.get("pointRaw"),
                    "reliable": prepare._elev_looks_reliable(e["elevRaw"]),
                }
    pairs = []
    for off, per_block in sorted(by_offset.items()):
        idx = sorted(per_block)
        if len(idx) < 2:
            continue
        a, b = idx[0], idx[-1]
        ea, eb = per_block[a], per_block[b]
        pairs.append({
            "offset": off,
            "blockA": a, "pointA": ea["point"], "elevA": ea["elev"], "elevRawA": ea["elevRaw"],
            "blockB": b, "pointB": eb["point"], "elevB": eb["elev"], "elevRawB": eb["elevRaw"],
            "diff": eb["elev"] - ea["elev"],
            "bothReliable": bool(ea["reliable"] and eb["reliable"]),
        })
    diff_big = [p for p in pairs if abs(p["diff"]) > 1.0]
    diff_huge = [p for p in pairs if abs(p["diff"]) > prepare.SUSPECT_CROSS_BLOCK_M]
    diff_big_reliable = [p for p in diff_big if p["bothReliable"]]
    diff_huge_reliable = [p for p in diff_huge if p["bothReliable"]]
    point_align = 0
    point_total = 0
    for off, per_block in by_offset.items():
        if len(per_block) < 2:
            continue
        point_total += 1
        pa = per_block[sorted(per_block)[0]]["point"]
        pb = per_block[sorted(per_block)[-1]]["point"]
        if str(pa) == str(pb):
            point_align += 1
    report["crossBlock"] = {
        "matchedOffsets": len(pairs),
        "diffGt1m": len(diff_big),
        "diffGt10m": len(diff_huge),
        "diffGt1mBothReliable": len(diff_big_reliable),
        "diffGt10mBothReliable": len(diff_huge_reliable),
        "pointNumberAligned": point_align,
        "pointNumberCompared": point_total,
        "diffTable": diff_big[:80],
        "hugeDiffTable": diff_huge[:40],
        "note": "两次施测点号编号不同（第 2 次更密，111 点 vs 68 点），"
                "因此按起点距数值匹配；diff = 第 2 块 − 第 1 块。"
                "「两侧可信」= 两个块的原始高程文本都带小数（未被截断），"
                "只有这种差异才能当作真实河床变化。",
    }

    # ---- 模型摘要（可选）
    for name, path in models:
        try:
            with open(path, "r", encoding="utf-8-sig") as fh:
                m = json.load(fh)
        except Exception as exc:
            report["models"].append({"name": name, "path": path, "error": str(exc)})
            continue
        dq = m.get("dataQuality") or {}
        report["models"].append({
            "name": name,
            "path": os.path.abspath(path),
            "profile": m.get("source", {}).get("profile"),
            "block": m.get("source", {}).get("block"),
            "date": m.get("source", {}).get("date"),
            "pointCount": len(m.get("points") or []),
            "entityTotal": (m.get("expected") or {}).get("total"),
            "byType": (m.get("expected") or {}).get("byType"),
            "scale": m.get("scale"),
            "paper": m.get("paper"),
            "bbox": (m.get("computed") or {}).get("bbox"),
            "widthMm": (m.get("computed") or {}).get("widthMm"),
            "heightMm": (m.get("computed") or {}).get("heightMm"),
            "elevBase": (m.get("computed") or {}).get("elevBase"),
            "suspectCount": dq.get("suspectCount"),
            "suspectDropped": dq.get("dropped"),
            "suspectKept": (m.get("computed") or {}).get("suspectKept"),
            "waterLevels": m.get("waterLevels"),
            "warnings": [w.get("code") for w in (m.get("warnings") or [])],
        })

    # ---- 结论
    b1 = report["blocks"][0] if report["blocks"] else {}
    total_pts = sum(b.get("pointsAfterLabelFilter", 0) for b in report["blocks"])
    total_sus = sum(b.get("suspectCount", 0) for b in report["blocks"])
    report["conclusion"] = [
        "表结构：表头在第 %d 行，%d 组「点号/起点距/高程」；数据按列块填充，"
        "解析时按组降维后按起点距排序。" % (header_idx + 1, len(groups)),
        "共 %d 个施测块：%s。" % (len(blocks),
                                  "；".join("第%d块 %s（测时水位 %s，%d 编号点）"
                                            % (b["index"], b["date"], b["waterLevel"], b["numbered"])
                                            for b in blocks)),
        "选定块（默认第 1 块）：%d 点（已过滤左岸/右岸标签行 %d 行），起点距 %.1f..%.1f m，"
        "高程 %.2f..%.2f m，中位数 %.2f m。"
        % (b1.get("pointsAfterLabelFilter", 0), b1.get("labelRowsDropped", 0),
           b1.get("offsetMin") or 0, b1.get("offsetMax") or 0,
           b1.get("elevMin") or 0, b1.get("elevMax") or 0, b1.get("medianElev") or 0),
        "数据质量：全部块共 %d 个可疑高程（%d/%d，占 %s）。主因是源表高程单元格被截断成"
        "无小数点短文本（如 5.73 变成 73、28.24 变成 28），文件内无法恢复原值。"
        % (total_sus, total_sus, total_pts, _pct(total_sus, total_pts)),
        "两次施测按起点距匹配到 %d 个同位置点；高程差 >1 m 的 %d 个，但**两侧值都可信**的只有 %d 个"
        "（其余差异来自对方块高程被截断，不能当作真实河床变化）。"
        % (report["crossBlock"]["matchedOffsets"], report["crossBlock"]["diffGt1m"],
           report["crossBlock"]["diffGt1mBothReliable"]),
        "建议：制图用 --drop-suspect 版（剩余点仍能反映断面形态）；同时建议回源核对高程列，"
        "或改用第二次施测（点数更多）。",
    ]
    return report


def render_markdown(r: Dict[str, Any]) -> str:
    L: List[str] = []
    L.append("# 示例站 2025 年实测大断面成果表 · 解析与数据质量报告")
    L.append("")
    L.append("> 自动生成于 %s；解析器：`scripts/section/prepare.py`（profile=yssd）。"
             "本报告只读源文件，未改动任何原始数据。" % r["generatedAt"])
    L.append("")
    L.append("## 1. 数据源")
    L.append("")
    L.append("| 项 | 值 |")
    L.append("|---|---|")
    src = r["source"]
    L.append("| 文件 | `%s` |" % src["path"])
    L.append("| 大小 / sheet / 读取器 | %d B / %s / %s |"
             % (src["sizeBytes"], src["sheet"], src["reader"]))
    L.append("| 总行数 | %d |" % src["totalRows"])
    t = r["table"]
    L.append("| 表头行 / 列组数 | 第 %d 行 / %d 组 |" % (t["headerRow"], t["groupCount"]))
    L.append("| 组列映射(点号/起点距/高程) | %s |"
             % ", ".join("(%s,%d,%d)" % (c["point"], c["offset"], c["elev"])
                         for c in t["columns"]))
    L.append("")
    L.append("## 2. 施测块")
    L.append("")
    L.append("| 块 | 施测日期 | 测时水位(m) | 元信息行 | 条目 | 编号点 | 标签行 | 备注行 |")
    L.append("|---|---|---|---|---|---|---|---|")
    for b in r["blocks"]:
        L.append("| %d | %s | %s | %d | %d | %d | %d | %d |"
                 % (b["index"], b["date"], b["waterLevel"], b["metaRow"], b["entries"],
                    b["numbered"], b["labels"], b["meta"]))
    L.append("")
    L.append("## 3. 选定块统计（默认第 1 块）")
    L.append("")
    b1 = r["blocks"][0]
    L.append("| 项 | 值 |")
    L.append("|---|---|")
    L.append("| 有效点（过滤左岸/右岸标签行后） | %d |" % b1["pointsAfterLabelFilter"])
    L.append("| 保留标签行时点数 | %d |" % b1["pointsWithLabelsKept"])
    L.append("| 起点距范围 | %.1f .. %.1f m |" % (b1["offsetMin"], b1["offsetMax"]))
    L.append("| 高程范围（含可疑） | %.2f .. %.2f m（中位数 %.2f） |"
             % (b1["elevMin"], b1["elevMax"], b1["medianElev"]))
    L.append("| 可疑点 / 占比 | %d / %s |" % (b1["suspectCount"], b1["suspectShare"]))
    L.append("| 剔除可疑点后点数 | %d |" % b1["pointsAfterDropSuspect"])
    L.append("| 剔除后高程范围 | %.2f .. %.2f m |"
             % ((b1["reliableElevMin"] or 0), (b1["reliableElevMax"] or 0)))
    L.append("| 起点距重复 | %s |"
             % ("无" if not b1["duplicateOffsets"] else json.dumps(b1["duplicateOffsets"], ensure_ascii=False)))
    L.append("")
    L.append("## 4. 数据质量问题")
    L.append("")
    L.append("### 4.1 判据")
    L.append("")
    L.append("- `ELEV_TEXT_TRUNCATED`：高程单元格是**文本**且无小数点、长度 ≤2 —— "
             "源表导出/录入被截断（实证：`73`←`5.73`、`03`←`6.03`、`28`←`28.24`）；")
    L.append("- `ELEV_OUT_OF_RANGE`：|高程 − 本块中位数| > %g m；" % prepare.SUSPECT_OUT_OF_RANGE_M)
    L.append("- `CROSS_BLOCK_DIFF`：与另一次施测同起点距的高程差 > %g m"
             "（仅当对方值也带小数点/为真数值时才用作判据）；" % prepare.SUSPECT_CROSS_BLOCK_M)
    L.append("")
    L.append("### 4.2 可疑点清单")
    L.append("")
    for b in r["blocks"]:
        L.append("**第 %d 块（%s）%d 个可疑点 / %d 点**" % (b["index"], b["date"], b["suspectCount"],
                                                         b["pointsAfterLabelFilter"]))
        L.append("")
        L.append("| 点号 | 起点距(m) | 高程(m) | 高程原文 | 源行 | 列组 | 判据 |")
        L.append("|---|---|---|---|---|---|---|")
        for s in b["suspects"]:
            L.append("| %s | %.1f | %.2f | `%s` | %d | %d | %s |"
                     % (s["point"], s["offset"], s["elev"], s["elevRaw"], s["row"],
                        s["group"], ", ".join(s["reasons"])))
        L.append("")
    L.append("### 4.3 被忽略的行（非数据行）")
    L.append("")
    L.append("| 源行 | 原因 | 内容/坐标 |")
    L.append("|---|---|---|")
    for i in b1["ignoredRows"]:
        if i["reason"] == "META_ROW":
            desc = i.get("text", "")
        else:
            desc = "%s @ %s m, %s m" % (i.get("text"), i.get("offset"), i.get("elev"))
        L.append("| %d | %s | %s |" % (i["row"], i["reason"], desc))
    L.append("")
    L.append("## 5. 两次施测对比（按起点距匹配）")
    L.append("")
    cb = r["crossBlock"]
    L.append("- 匹配到同起点距的点：**%d** 个" % cb["matchedOffsets"])
    L.append("- 高程差 > 1 m：**%d** 个；> %g m：**%d** 个" % (cb["diffGt1m"],
                                                            prepare.SUSPECT_CROSS_BLOCK_M,
                                                            cb["diffGt10m"]))
    L.append("- 其中**两侧原始值都可信**（都带小数点）：>1 m 的 **%d** 个，>%g m 的 **%d** 个 "
             "—— 只有这类差异才能当作真实河床变化；其余差异来自对方块高程被截断。"
             % (cb["diffGt1mBothReliable"], prepare.SUSPECT_CROSS_BLOCK_M,
                cb["diffGt10mBothReliable"]))
    L.append("- 同起点距处点号相同的：%d / %d（说明两次施测点号编号不同，第 2 次更密，"
             "**不能只按点号对齐**）" % (cb["pointNumberAligned"], cb["pointNumberCompared"]))
    L.append("- 其余 %d 个匹配点高程差 <= 1 m（两次施测在同位置基本吻合）。"
             % (cb["matchedOffsets"] - cb["diffGt1m"]))
    L.append("")
    L.append("| 起点距(m) | 第1块 点号/高程 | 第2块 点号/高程 | 差(m) | 两侧值可信 |")
    L.append("|---|---|---|---|---|")
    L.append("> 「两侧值可信=否」表示差异来自被截断的高程文本，不能当作真实河床变化。")
    for p in cb["diffTable"]:
        L.append("| %.1f | %s / %.2f (%s) | %s / %.2f (%s) | %+.2f | %s |"
                 % (p["offset"], p["pointA"], p["elevA"], p["elevRawA"],
                    p["pointB"], p["elevB"], p["elevRawB"], p["diff"],
                    "是" if p["bothReliable"] else "否"))
    L.append("")
    L.append("## 6. 产出模型")
    L.append("")
    if not r["models"]:
        L.append("（本次未附模型摘要）")
    else:
        L.append("| 模型 | 块 | 点数 | 实体数 | 比例 | 图幅 | bbox(mm) | 宽×高(mm) | 可疑点(留/剔) | 警告 |")
        L.append("|---|---|---|---|---|---|---|---|---|---|")
        for m in r["models"]:
            if m.get("error"):
                L.append("| %s | - | - | - | - | - | - | - | - | %s |" % (m["name"], m["error"]))
                continue
            bb = m["bbox"] or {}
            L.append("| `%s` | %s | %d | %d | %g/%g | %s | (%.1f,%.1f)-(%.1f,%.1f) | %.1f×%.1f | %s/%s | %s |"
                     % (m["name"], m["block"], m["pointCount"], m["entityTotal"],
                        m["scale"]["horizontal"], m["scale"]["vertical"], m["paper"],
                        bb.get("x0", 0), bb.get("y0", 0), bb.get("x1", 0), bb.get("y1", 0),
                        m["widthMm"], m["heightMm"], m["suspectKept"], m["suspectDropped"],
                        ", ".join(m["warnings"])))
        L.append("")
    L.append("## 7. 结论与建议")
    L.append("")
    for c in r["conclusion"]:
        L.append("- " + c)
    L.append("")
    L.append("> 复现命令见 `tmp/real/README.md`；模型自洽检查用 "
             "`python tests/verify_model_self.py --model <model.json>`。")
    return "\n".join(L) + "\n"


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description="yssd 数据质量 QA 报告生成器")
    ap.add_argument("--data", required=True, help="实测大断面成果表（.xls/.xlsx）")
    ap.add_argument("--sheet", default=None)
    ap.add_argument("--block", type=int, default=1)
    ap.add_argument("--out-json", dest="out_json", default=None)
    ap.add_argument("--out-md", dest="out_md", default=None)
    ap.add_argument("--model", action="append", default=None, metavar="NAME=PATH",
                    help="附模型摘要（可重复），如 base=tmp\\real\\x.model.json")
    args = ap.parse_args(argv)

    models: List[Tuple[str, str]] = []
    for item in args.model or []:
        if "=" in item:
            name, path = item.split("=", 1)
        else:
            name, path = os.path.basename(item), item
        models.append((name, path))

    report = build_report(args.data, args.sheet, args.block, models)

    if args.out_json:
        os.makedirs(os.path.dirname(os.path.abspath(args.out_json)), exist_ok=True)
        with open(args.out_json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print("WROTE %s" % args.out_json)
    if args.out_md:
        os.makedirs(os.path.dirname(os.path.abspath(args.out_md)), exist_ok=True)
        with open(args.out_md, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(render_markdown(report))
        print("WROTE %s" % args.out_md)

    print("\n===== 摘要 =====")
    for b in report["blocks"]:
        print("block %d %s: 水位=%s 编号点=%d 标签行=%d 过滤后=%d 可疑=%d(%s) 剔除后=%d "
              "起点距 %.1f..%.1f 高程 %.2f..%.2f (中位 %.2f)"
              % (b["index"], b["date"], b["waterLevel"], b["numbered"], b["labels"],
                 b["pointsAfterLabelFilter"], b["suspectCount"], b["suspectShare"],
                 b["pointsAfterDropSuspect"], b["offsetMin"], b["offsetMax"],
                 b["elevMin"], b["elevMax"], b["medianElev"]))
    cb = report["crossBlock"]
    print("cross-block: matched=%d diff>1m=%d(两侧可信 %d) diff>%gm=%d(两侧可信 %d) pointNoAligned=%d/%d"
          % (cb["matchedOffsets"], cb["diffGt1m"], cb["diffGt1mBothReliable"],
             prepare.SUSPECT_CROSS_BLOCK_M, cb["diffGt10m"], cb["diffGt10mBothReliable"],
             cb["pointNumberAligned"], cb["pointNumberCompared"]))
    for m in report["models"]:
        if m.get("error"):
            print("model %s: ERROR %s" % (m["name"], m["error"]))
            continue
        print("model %-18s block=%s points=%-4d entities=%-4d %s w×h=%.1f×%.1f 可疑留/剔=%s/%s"
              % (m["name"], m["block"], m["pointCount"], m["entityTotal"],
                 m["scale"], m["widthMm"], m["heightMm"], m["suspectKept"], m["suspectDropped"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
