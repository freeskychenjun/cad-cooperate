#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""selftest.py -- 计算侧全量自测（零 COM，绝不启动 CAD）。

覆盖：
  1  基准：prepare.py(section-30.xlsx) -> verify_section.py 对 expected 全绿
  2  --tamper 负例：改一个顶点 y 必须被抓到
  3  反序输入：section-30-reversed.xlsx 自动反转 + ORDER_REVERSED，几何与正序完全一致
  4  三个 bad 用例（<3 点 / 非单调 / 空值）：BAD_DATA + 退出码 2 + **不产出文件**
  5  --no-auto-scale：超宽 -> TOO_WIDE + 不产出文件
  6  A3 自动升档：默认 500 -> 1000（widthMm 290）；A1 幅面不升档；显式 1000 不升档
  7  变体计数：--no-axis / --no-point-no / --label-every 2 / 多水位 / --v-scale / --origin
  8  --cols 表头匹配（含别名 桩号/河底高程）与 --sheet
  9  产物形态：stdout 全 ASCII、模型文件 UTF-8 无 BOM、中文原文可反解
 10  CAD 回读 schema 交叉验证：把模型转成 core-cad 约定的 read 响应（AcDb*/vertices/
     start-end/insertion/center，无 role）-> verify 必须全绿；再故意改坏 -> 必须 FAIL

运行：python tests\\selftest.py
退出码：0 全绿；1 有 FAIL
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from typing import Any, Dict, List, Optional, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FIX = os.path.join(HERE, "fixtures")
TMP = os.path.join(ROOT, "tmp")
PY = sys.executable or r"python"
PREPARE = os.path.join(ROOT, "scripts", "section", "prepare.py")
VERIFY = os.path.join(HERE, "verify_section.py")
FIXTURE = os.path.join(FIX, "section-30.xlsx")
EXPECTED = os.path.join(FIX, "section-30.expected.json")
WATER = "42.30=设计水位"

# CI runners default the console code page to cp1252 while the details printed below contain
# Chinese: force UTF-8 with replacement instead of dying with UnicodeEncodeError (the same guard
# tests\bench\selftest_checkers.py already had). Harmless on a real console.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

RESULTS: List[Tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append((name, bool(ok), detail))
    return bool(ok)


def env() -> Dict[str, str]:
    e = os.environ.copy()
    e["PYTHONUTF8"] = "1"
    e["PYTHONIOENCODING"] = "utf-8"
    return e


def run(script: str, args: List[str]) -> Tuple[int, str, str]:
    p = subprocess.run([PY, script] + args, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=ROOT, env=env())
    return p.returncode, p.stdout, p.stderr


def out_json(stdout: str) -> Dict[str, Any]:
    for line in (stdout or "").splitlines():
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
    return {}


def prepare(args: List[str], out_name: str) -> Tuple[int, Dict[str, Any], str]:
    out_path = os.path.join(TMP, out_name)
    if os.path.exists(out_path):
        os.remove(out_path)
    code, so, se = run(PREPARE, args + ["--out", out_path])
    return code, out_json(so), out_path


def verify(model: str, extra: Optional[List[str]] = None) -> Tuple[int, str]:
    code, so, _ = run(VERIFY, ["--model", model, "--expected", EXPECTED] + (extra or []))
    return code, so


def model_of(path: str) -> Dict[str, Any]:
    with open(path, "r", encoding="utf-8-sig") as fh:
        return json.load(fh)


def scenario_main() -> None:
    os.makedirs(TMP, exist_ok=True)

    # ---- 1 基准
    code, j, model_path = prepare(["--data", FIXTURE, "--water", WATER], "selftest-base.model.json")
    ok = check("1.0 prepare exit=0", code == 0, "exit=%d" % code)
    check("1.1 stdout ok:true", j.get("ok") is True, json.dumps(j.get("error") or {}, ensure_ascii=False))
    check("1.2 total=67", j.get("total") == 67, "got=%r" % j.get("total"))
    check("1.3 byType", j.get("byType") == {"LWPOLYLINE": 1, "LINE": 3, "TEXT": 62, "CIRCLE": 1},
          json.dumps(j.get("byType")))
    check("1.4 autoScaled 500->1000", j.get("autoScaled") is True
          and j.get("scale", {}).get("horizontal") == 1000.0,
          "scale=%r" % (j.get("scale"),))
    check("1.5 widthMm=290 heightMm=115", j.get("widthMm") == 290.0 and j.get("heightMm") == 115.0,
          "w=%r h=%r" % (j.get("widthMm"), j.get("heightMm")))
    if ok:
        code, so = verify(model_path)
        passed = "RESULT: PASS" in so
        check("1.6 verify 全绿", code == 0 and passed, "exit=%d" % code)
        check("1.7 verify 断言数 >= 200", so.count("PASS") >= 200,
              "PASS 行数=%d" % so.count("PASS"))

    # ---- 2 tamper
    code, so = verify(model_path, ["--tamper"])
    check("2.0 tamper 自证（必须抓到）", code == 0 and "TAMPER: PASS" in so, "exit=%d" % code)

    # ---- 3 反序
    code, jr, rev_path = prepare(["--data", os.path.join(FIX, "section-30-reversed.xlsx"),
                                  "--water", WATER], "selftest-rev.model.json")
    check("3.0 reversed prepare exit=0", code == 0, "exit=%d" % code)
    check("3.1 reversed orderReversed=true", jr.get("orderReversed") is True, repr(jr.get("orderReversed")))
    check("3.2 reversed warnings 含 ORDER_REVERSED", "ORDER_REVERSED" in (jr.get("warnings") or []),
          repr(jr.get("warnings")))
    if code == 0:
        with open(rev_path, "r", encoding="utf-8") as fh:
            rev = json.load(fh)
        base = model_of(model_path)
        same_geom = (rev["points"] == base["points"]
                     and json.dumps(rev["entities"], sort_keys=True)
                     == json.dumps(base["entities"], sort_keys=True))
        check("3.3 reversed 几何/实体与正序完全一致", same_geom)
        code_v, so = verify(rev_path)
        fails = [ln for ln in so.splitlines() if ln.startswith("FAIL")]
        check("3.4 reversed verify 只差 orderReversed 一项",
              len(fails) == 1 and "computed.orderReversed" in fails[0],
              "fails=%s" % fails)

    # ---- 4 bad 用例
    bad_cases = [("bad-2points.xlsx", "BAD_DATA"), ("bad-nonmono.xlsx", "BAD_DATA"),
                 ("bad-empty.xlsx", "BAD_DATA")]
    for fn, want_code in bad_cases:
        code, jb, out_path = prepare(["--data", os.path.join(FIX, fn), "--water", WATER],
                                     "selftest-%s.model.json" % fn.replace(".", "-"))
        err = (jb.get("error") or {})
        check("4.%s 拒绝(%s)" % (fn, want_code),
              code == 2 and jb.get("ok") is False and err.get("code") == want_code,
              "exit=%d code=%r msg=%r" % (code, err.get("code"), err.get("message")))
        check("4.%s 不产出文件" % fn, not os.path.exists(out_path), out_path)

    # ---- 5 --no-auto-scale -> TOO_WIDE
    code, jt, out_path = prepare(["--data", FIXTURE, "--water", WATER, "--no-auto-scale"],
                                 "selftest-toowide.model.json")
    check("5.0 --no-auto-scale -> TOO_WIDE",
          code == 2 and (jt.get("error") or {}).get("code") == "TOO_WIDE",
          "exit=%d code=%r" % (code, (jt.get("error") or {}).get("code")))
    check("5.1 TOO_WIDE 不产出文件", not os.path.exists(out_path))

    # ---- 6 幅面/比例分支
    code, ja, _ = prepare(["--data", FIXTURE, "--water", WATER, "--paper", "A1"],
                          "selftest-a1.model.json")
    check("6.0 A1 不触发升档", code == 0 and ja.get("autoScaled") is False
          and ja.get("scale", {}).get("horizontal") == 500.0
          and ja.get("widthMm") == 580.0,
          "scale=%r width=%r" % (ja.get("scale"), ja.get("widthMm")))
    code, j1k, _ = prepare(["--data", FIXTURE, "--water", WATER, "--h-scale", "1000"],
                           "selftest-h1000.model.json")
    check("6.1 显式 1000 不升档", code == 0 and j1k.get("autoScaled") is False
          and j1k.get("widthMm") == 290.0, "w=%r" % j1k.get("widthMm"))
    code, j2k, _ = prepare(["--data", FIXTURE, "--water", WATER, "--h-scale", "200"],
                           "selftest-h200.model.json")
    check("6.2 200 -> 升档到 1000", code == 0 and j2k.get("autoScaled") is True
          and j2k.get("scale", {}).get("horizontal") == 1000.0,
          "scale=%r" % (j2k.get("scale"),))
    code, jv, _ = prepare(["--data", FIXTURE, "--water", WATER, "--v-scale", "100"],
                          "selftest-v100.model.json")
    check("6.3 --v-scale 100: elevBase=37.55 heightMm=57.5",
          code == 0 and abs(jv.get("elevBase", 0) - 37.55) < 1e-9
          and abs(jv.get("heightMm", 0) - 57.5) < 1e-9,
          "elevBase=%r height=%r" % (jv.get("elevBase"), jv.get("heightMm")))

    # ---- 7 变体计数
    variants = [
        ("--no-axis", ["--no-axis"], 65),
        ("--no-point-no", ["--no-point-no"], 37),
        ("--label-every 2", ["--label-every", "2"], 52),
        ("--no-axis --no-point-no --label-every 2",
         ["--no-axis", "--no-point-no", "--label-every", "2"], 20),
        ("多水位(2 条)", ["--water", "42.30=设计水位", "--water", "40.00=警戒水位"], 69),
    ]
    for idx, (label, extra, want) in enumerate(variants):
        args = ["--data", FIXTURE] + (["--water", WATER] if "水位(2" not in label else []) + extra
        code, jx, _ = prepare(args, "selftest-var%d.model.json" % idx)
        check("7.%d %s -> total=%d" % (idx, label, want),
              code == 0 and jx.get("total") == want,
              "exit=%d total=%r" % (code, jx.get("total")))

    # ---- 7b --origin
    code, jo, _ = prepare(["--data", FIXTURE, "--water", WATER, "--origin", "100,50"],
                          "selftest-origin.model.json")
    bb = jo.get("bbox") or {}
    check("7.5 --origin 100,50 平移 bbox",
          code == 0 and bb.get("x0") == 100.0 and bb.get("y0") == 60.0
          and bb.get("x1") == 390.0 and bb.get("y1") == 175.0, json.dumps(bb))

    # ---- 8 --cols / --sheet（用别名表头另造一份 xlsx）
    try:
        from openpyxl import Workbook
        alias_xlsx = os.path.join(TMP, "selftest-alias.xlsx")
        wb = Workbook()
        ws = wb.active
        ws.title = "SEC"
        ws["A1"] = "桩号(m)"
        ws["B1"] = "河底高程(m)"
        # 从 expected 取真值写两列（不依赖 prepare 的解析）
        exp = model_of(EXPECTED)
        for r, (off, elev) in enumerate(exp["points"], start=2):
            ws.cell(row=r, column=1, value=off)
            ws.cell(row=r, column=2, value=elev)
        wb.save(alias_xlsx)
        code, jc, _ = prepare(["--data", alias_xlsx, "--cols", "桩号=河底高程",
                               "--sheet", "SEC", "--water", WATER],
                              "selftest-cols.model.json")
        check("8.0 --cols 别名 + --sheet 名称", code == 0 and jc.get("total") == 67,
              "exit=%d total=%r" % (code, jc.get("total")))
        code, jc2, _ = prepare(["--data", alias_xlsx, "--sheet", "1", "--water", WATER],
                               "selftest-sheet1.model.json")
        check("8.1 --sheet 序号 1（无 --cols，走默认列）", code == 0 and jc2.get("total") == 67,
              "exit=%d total=%r" % (code, jc2.get("total")))
    except ImportError as exc:  # pragma: no cover
        check("8.0 --cols 别名", False, "openpyxl 缺失：%s" % exc)

    # ---- 9 产物形态
    code, so, _ = run(PREPARE, ["--data", FIXTURE, "--water", WATER,
                                "--out", os.path.join(TMP, "selftest-ascii.model.json")])
    check("9.0 prepare stdout 全 ASCII", all(ord(c) < 128 for c in so),
          "首个非 ASCII 位置=%r" % next((i for i, c in enumerate(so) if ord(c) >= 128), None))
    raw = open(model_path, "rb").read()
    check("9.1 模型文件 UTF-8 无 BOM", not raw.startswith(b"\xef\xbb\xbf"))
    m = model_of(model_path)
    wt = [e for e in m["entities"] if e.get("role") == "water-text:1"]
    check("9.2 中文原文可反解", bool(wt) and wt[0]["text"] == "设计水位 42.30 m",
          repr(wt[0]["text"]) if wt else "missing")
    check("9.3 图上无 AI/工具名残留",
          not any(any(t in (e.get("text") or "") for t in ("AI", "DSH", "zwcad", "cooperate"))
                  for e in m["entities"] if e["kind"] == "TEXT"),
          "图层名是唯一痕迹")

    # ---- 10 CAD 回读 schema 交叉验证
    read_resp = to_read_response(m)
    read_path = os.path.join(TMP, "selftest-read.json")
    with open(read_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(read_resp, fh, ensure_ascii=False, indent=2)
    code, so = verify(read_path)
    check("10.0 模拟 CAD read 响应 verify 全绿", code == 0, "exit=%d" % code)
    # 混入 1 个非本图层实体，必须被过滤掉
    read_resp2 = json.loads(json.dumps(read_resp))
    read_resp2["result"]["entities"].append({
        "handle": "9FF", "type": "AcDbLine", "kind": "LINE", "layer": "0",
        "start": [0, 0], "end": [10, 10]})
    p2 = os.path.join(TMP, "selftest-read-extra.json")
    with open(p2, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(read_resp2, fh, ensure_ascii=False, indent=2)
    code, so = verify(p2)
    check("10.1 read 混入他图层实体被过滤", code == 0, "exit=%d" % code)
    # 改坏回读 -> 必须 FAIL
    read_resp3 = json.loads(json.dumps(read_resp))
    for e in read_resp3["result"]["entities"]:
        if e["kind"] == "LWPOLYLINE":
            e["vertices"][4][1] = e["vertices"][4][1] + 0.5
            break
    p3 = os.path.join(TMP, "selftest-read-bad.json")
    with open(p3, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(read_resp3, fh, ensure_ascii=False, indent=2)
    code, so = verify(p3)
    check("10.2 read 几何被改坏 -> FAIL", code == 1 and "FAIL  ground.polyline.vertices" in so,
          "exit=%d" % code)


def to_read_response(model: Dict[str, Any]) -> Dict[str, Any]:
    """把 prepare 的模型转成 core-cad 约定的 `cad.ps1 read` 响应形态（无 role）。"""
    ents = []
    for i, e in enumerate(model["entities"]):
        handle = "%X" % (0x100 + i)
        if e["kind"] == "POLYLINE":
            pts = e["points"]
            ents.append({
                "handle": handle, "type": "AcDbPolyline", "kind": "LWPOLYLINE",
                "layer": e["layer"], "space": "model", "closed": e["closed"],
                "vertices": [[p[0], p[1]] for p in pts],
                "bbox": {"min": [min(p[0] for p in pts), min(p[1] for p in pts)],
                         "max": [max(p[0] for p in pts), max(p[1] for p in pts)]},
            })
        elif e["kind"] == "LINE":
            pts = e["points"]
            ents.append({
                "handle": handle, "type": "AcDbLine", "kind": "LINE",
                "layer": e["layer"], "space": "model",
                "start": [pts[0][0], pts[0][1]], "end": [pts[1][0], pts[1][1]],
                "bbox": {"min": [min(pts[0][0], pts[1][0]), min(pts[0][1], pts[1][1])],
                         "max": [max(pts[0][0], pts[1][0]), max(pts[0][1], pts[1][1])]},
            })
        elif e["kind"] == "TEXT":
            ents.append({
                "handle": handle, "type": "AcDbText", "kind": "TEXT",
                "layer": e["layer"], "space": "model", "text": e["text"],
                "insertion": [e["pos"][0], e["pos"][1]], "height": e["height"],
                "style": e["style"],
            })
        elif e["kind"] == "CIRCLE":
            ents.append({
                "handle": handle, "type": "AcDbCircle", "kind": "CIRCLE",
                "layer": e["layer"], "space": "model",
                "center": [e["center"][0], e["center"][1]], "radius": e["radius"],
            })
    return {"ok": True, "cmd": "read",
            "result": {"readPath": "com",
                       "counts": {"model": len(ents), "paper": 0, "returned": len(ents)},
                       "truncated": False, "entities": ents}}


def main() -> int:
    scenario_main()
    width = max(len(n) for n, _, _ in RESULTS) if RESULTS else 10
    failed = 0
    for name, ok, detail in RESULTS:
        print("%-4s  %-*s  %s" % ("PASS" if ok else "FAIL", width, name, detail))
        if not ok:
            failed += 1
    print("-" * 72)
    print("[selftest] RESULT: %s  (%d PASS / %d FAIL)"
          % ("FAIL" if failed else "PASS", len(RESULTS) - failed, failed))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
