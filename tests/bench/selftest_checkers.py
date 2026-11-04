# -*- coding: utf-8 -*-
"""Offline self-test for the E2 checkers: fabricate step outputs, run check.py, assert verdicts.

This validates the *judging layer* (check.py + gen_entity_request.py) without CAD/entity
implementation, so that when core-cad says "primitives ready" we already know the checker is
correct. Two groups:
  good  -> verdict.ok == True for W1/W2/W3/W5/W6/N1..N4
  bad   -> verdict.ok == False (tampered result) for the same tasks
"""
import json
import os
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CHECK = os.path.join(ROOT, "tests", "bench", "checkers", "check.py")
TASKS = os.path.join(ROOT, "tests", "bench", "tasks.json")
NEG = os.path.join(ROOT, "tests", "bench", "negative.json")
EXPECTED = os.path.join(ROOT, "tests", "bench", "fixtures", "expected.json")
OUT = os.path.join(ROOT, "tmp", "bench", "e2", "checker-selftest")

TEXT = "设计水位（复核）"
ANNO_TEXTS = ["设计水位 42.30 m", "设计水位 41.85 m", "设计水位 40.20 m",
              "校核水位 44.10 m", "河底高程 5.70 m", "断面 1-1", "水尺 3 号"]
RIVER = [
    dict(handle="R1", kind="LWPOLYLINE", layer="RIVER", closed=False,
         points=[[0, 0], [10, 0], [20, -2], [30, -1]]),
    dict(handle="R2", kind="LWPOLYLINE", layer="RIVER", closed=False,
         points=[[0, 5], [15, 6], [30, 5.5]]),
    dict(handle="R3", kind="LWPOLYLINE", layer="RIVER", closed=False,
         points=[[40, 0], [50, 0], [50, 10], [40, 10], [40.05, 0.05]]),
    dict(handle="R4", kind="LWPOLYLINE", layer="RIVER", closed=True,
         points=[[60, 0], [70, 0], [70, 8], [60, 8]]),
]


def bbox(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return {"min": [min(xs), min(ys)], "max": [max(xs), max(ys)]}


def read_entities():
    ents = []
    for i, t in enumerate(ANNO_TEXTS):
        ents.append(dict(handle="T%d" % (i + 1), type="AcDbText", kind="TEXT", layer="ANNO",
                         text=t, height=2.5, insertion=[0, 15 + i],
                         bbox={"min": [0, 15], "max": [20, 17.5]}))
    for e in RIVER:
        ents.append(dict(e, type="AcDbPolyline", bbox=bbox(e["points"])))
    # 6 normal lines + 2 zero-length
    for i in range(6):
        ents.append(dict(handle="L%d" % (i + 1), type="AcDbLine", kind="LINE", layer="LINES",
                         start=[i * 10, -5], end=[(i + 1) * 10, -5],
                         points=[[i * 10, -5], [(i + 1) * 10, -5]],
                         bbox={"min": [i * 10, -5], "max": [(i + 1) * 10, -5]}))
    for i, x in enumerate((20, 35)):
        ents.append(dict(handle="Z%d" % (i + 1), type="AcDbLine", kind="LINE", layer="ZERO",
                         start=[x, 30], end=[x, 30], points=[[x, 30], [x, 30]],
                         bbox={"min": [x, 30], "max": [x, 30]}))
    return ents


def write_step(dirpath, name, payload, exit_code=0):
    os.makedirs(dirpath, exist_ok=True)
    raw = json.dumps(payload, ensure_ascii=False)
    with open(os.path.join(dirpath, name + ".json"), "w", encoding="utf-8") as fh:
        fh.write(raw)
    with open(os.path.join(dirpath, name + ".raw.txt"), "w", encoding="utf-8") as fh:
        fh.write(raw)
    meta = {"step": name, "kind": "cad" if not payload.get("cmd") == "py" else "py",
            "exit": exit_code, "wallMs": 100, "bytes": len(raw.encode("utf-8"))}
    with open(os.path.join(dirpath, name + ".meta.json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh)


def query_resp(items, cmd="query"):
    return {"ok": True, "cmd": cmd, "result": {"items": items, "total": len(items),
                                               "nextCursor": None, "truncated": False,
                                               "mode": "summary"}}


def entity_resp(applied, rolled=False, ok_verify=True, undo="u-test"):
    return {"ok": True, "cmd": "entity", "undoToken": undo,
            "result": {"applied": applied, "rolledBack": rolled, "failed": [],
                       "items": [{"i": 0, "op": "update", "ok": True}],
                       "verify": {"ok": ok_verify, "issues": []}}}


def base_dir(task_id, variant):
    d = os.path.join(OUT, variant, task_id, "rep1")
    os.makedirs(d, exist_ok=True)
    return d


def write_common(d, ents=None, extra=None):
    ents = ents if ents is not None else read_entities()
    write_step(d, "open1", {"ok": True, "cmd": "open", "result": {"doc": {"name": "work-design-water.dxf"}}})
    write_step(d, "read1", {"ok": True, "cmd": "read", "result": {
        "entities": ents, "counts": {"model": len(ents), "returned": len(ents)}}})
    if extra:
        extra(d, ents)


def build(task_id, variant):
    d = base_dir(task_id, variant)
    tamper = variant == "bad"
    if task_id == "W1-edit-text":
        items = [{"handle": "T%d" % (i + 1), "type": "AcDbText", "layer": "ANNO", "text": ANNO_TEXTS[i]}
                 for i in range(3)]
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "query1", query_resp(items))
        write_step(d, "gen1", {"ok": True, "mode": "update-text", "items": 3})
        write_step(d, "entity1", entity_resp(3))
        ents = read_entities()
        for e in ents:
            if e["handle"] in ("T1", "T2", "T3"):
                e["text"] = ("改错了" if tamper else TEXT)
        write_step(d, "read1", {"ok": True, "cmd": "read", "result": {
            "entities": ents, "counts": {"model": len(ents)}}})
    elif task_id == "W2-change-layer":
        items = [{"handle": e["handle"], "type": "AcDbPolyline", "layer": "RIVER"} for e in RIVER]
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "query1", query_resp(items))
        write_step(d, "gen1", {"ok": True, "mode": "update-layer", "items": 4})
        write_step(d, "entity1", entity_resp(4))
        ents = read_entities()
        for e in ents:
            if e.get("layer") == "RIVER":
                e["layer"] = ("WRONG-LAYER" if tamper else "RIVER-2")
        write_step(d, "read1", {"ok": True, "cmd": "read", "result": {"entities": ents}})
    elif task_id == "W3-unify-text-height":
        items = [{"handle": "T%d" % (i + 1), "type": "AcDbText", "layer": "ANNO",
                  "text": ANNO_TEXTS[i]} for i in range(7)]
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "query1", query_resp(items))
        write_step(d, "gen1", {"ok": True, "mode": "update-height", "items": 7})
        write_step(d, "entity1", entity_resp(7))
        ents = read_entities()
        for e in ents:
            if e.get("layer") == "ANNO":
                e["height"] = (3.0 if tamper else 2.5)
        write_step(d, "read1", {"ok": True, "cmd": "read", "result": {"entities": ents}})
    elif task_id == "W5-delete-zero-lines":
        items = [{"handle": "L1", "type": "AcDbLine", "layer": "LINES",
                  "bbox": {"min": [0, -5], "max": [10, -5]}},
                 {"handle": "Z1", "type": "AcDbLine", "layer": "ZERO",
                  "bbox": {"min": [20, 30], "max": [20, 30]}},
                 {"handle": "Z2", "type": "AcDbLine", "layer": "ZERO",
                  "bbox": {"min": [35, 30], "max": [35, 30]}}]
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "query1", query_resp(items))
        write_step(d, "gen1", {"ok": True, "mode": "delete-zero-length", "items": 2})
        write_step(d, "entity1", entity_resp(2))
        ents = read_entities()
        if tamper:
            ents = [e for e in ents]  # zero lines still present
        else:
            ents = [e for e in ents if e["handle"] not in ("Z1", "Z2")]
        write_step(d, "read1", {"ok": True, "cmd": "read", "result": {"entities": ents}})
    elif task_id == "W6-xdata-mark":
        items = [{"handle": "T1", "type": "AcDbText", "layer": "ANNO", "text": ANNO_TEXTS[0]},
                 {"handle": "T2", "type": "AcDbText", "layer": "ANNO", "text": ANNO_TEXTS[1]}]
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "query1", query_resp(items))
        write_step(d, "gen1", {"ok": True, "mode": "mark-xdata", "items": 2})
        write_step(d, "entity1", entity_resp(2))
        write_step(d, "query2", query_resp(items))
        write_step(d, "entity2", entity_resp(2))
        write_step(d, "query3", query_resp([] if not tamper else items))
        ents = read_entities()
        if not tamper:
            ents = [e for e in ents if e["handle"] not in ("T1", "T2")]
        write_step(d, "read1", {"ok": True, "cmd": "read", "result": {"entities": ents}})
    elif task_id in ("N1-bad-anchor", "N2-rollback", "N3-undo-restores", "N4-diff"):
        before = read_entities()
        write_step(d, "open1", {"ok": True, "cmd": "open"})
        write_step(d, "read_before", {"ok": True, "cmd": "read", "result": {"entities": before}})
        write_step(d, "query1", query_resp([{"handle": "T%d" % (i + 1), "type": "AcDbText",
                                             "layer": "ANNO", "text": ANNO_TEXTS[i]} for i in range(3)]))
        write_step(d, "gen1", {"ok": True, "items": 3})
        if task_id == "N1-bad-anchor":
            write_step(d, "corrupt1", {"ok": True, "mode": "bad-handle-index"})
            write_step(d, "entity1", {"ok": False, "cmd": "entity",
                                      "error": {"code": "ANCHOR_MISSING", "message": "bad anchor"}},
                       exit_code=2)
            n1_after = read_entities()
            if tamper:
                n1_after[0]["text"] = "被误改"
            write_step(d, "read_after", {"ok": True, "cmd": "read", "result": {"entities": n1_after}})
        elif task_id == "N2-rollback":
            write_step(d, "entity1", {"ok": False, "cmd": "entity",
                                      "error": {"code": "VERIFY_FAILED", "message": "read-back failed"},
                                      "result": {"applied": 0, "rolledBack": True, "failed": []}},
                       exit_code=3)
            n2_after = read_entities()
            if tamper:
                n2_after[0]["height"] = -5.0
            write_step(d, "read_after", {"ok": True, "cmd": "read", "result": {"entities": n2_after}})
        elif task_id == "N3-undo-restores":
            write_step(d, "entity1", entity_resp(3))
            write_step(d, "undo1", {"ok": True, "cmd": "undo",
                                    "result": {"removed": 0, "countBefore": len(before), "countAfter": len(before)}})
            n3_after = read_entities()
            if tamper:
                n3_after[0]["text"] = "临时改"   # undo did not restore -> must be rejected
            write_step(d, "read_after", {"ok": True, "cmd": "read", "result": {"entities": n3_after}})
        else:  # N4-diff
            write_step(d, "snapshot1", {"ok": True, "cmd": "snapshot", "result": {"out": "snap.json"}})
            write_step(d, "entity1", entity_resp(3))
            changed = [] if tamper else [
                {"handle": "T%d" % (i + 1),
                 "fields": {"text": {"before": ANNO_TEXTS[i], "after": "临时改"}}} for i in range(3)]
            write_step(d, "diff1", {"ok": True, "cmd": "diff",
                                    "result": {"added": [], "removed": [], "changed": changed, "same": 17}})
            after = read_entities()
            for e in after:
                if e["handle"] in ("T1", "T2", "T3"):
                    e["text"] = "临时改"
            write_step(d, "read_after", {"ok": True, "cmd": "read", "result": {"entities": after}})


def run_check(task_id, tasks_file):
    d = os.path.join(OUT, "good", task_id, "rep1")
    ctx = os.path.join(d, "context.json")
    with open(ctx, "w", encoding="utf-8") as fh:
        json.dump({"taskId": task_id}, fh)
    proc = subprocess.run([sys.executable, CHECK, "--task", task_id, "--dir", d,
                           "--expected", EXPECTED, "--tasks", tasks_file, "--context", ctx],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    try:
        return json.loads(proc.stdout)
    except Exception:
        return {"ok": False, "reasons": ["unparsable verdict: " + proc.stdout[:200] + proc.stderr[:200]]}


def main():
    ids_good = ["W1-edit-text", "W2-change-layer", "W3-unify-text-height", "W5-delete-zero-lines",
                "W6-xdata-mark", "N1-bad-anchor", "N2-rollback", "N3-undo-restores", "N4-diff"]
    for variant in ("good", "bad"):
        for tid in ids_good:
            build(tid, variant)
    fails = []
    for tid in ids_good:
        tf = NEG if tid.startswith("N") else TASKS
        v = run_check(tid, tf)
        want = tid != "" and (v.get("ok") is True)
        # 'bad' variant must be rejected
        bad_dir = os.path.join(OUT, "bad", tid, "rep1")
        ctx = os.path.join(bad_dir, "context.json")
        with open(ctx, "w", encoding="utf-8") as fh:
            json.dump({"taskId": tid}, fh)
        proc = subprocess.run([sys.executable, CHECK, "--task", tid, "--dir", bad_dir,
                               "--expected", EXPECTED, "--tasks", tf, "--context", ctx],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        try:
            vbad = json.loads(proc.stdout)
        except Exception:
            vbad = {"ok": True, "reasons": ["unparsable"]}
        status = "PASS" if want else "FAIL"
        tamper_caught = vbad.get("ok") is False
        print("%-22s good_ok=%s (%s)  bad_rejected=%s" % (tid, v.get("ok"), status, tamper_caught))
        if not want:
            fails.append((tid, v.get("reasons")))
        if not tamper_caught:
            fails.append((tid + " (tamper not caught)", vbad.get("reasons")))
    print("\nSELFTEST", "PASS" if not fails else "FAIL")
    for f in fails:
        print("  -", f[0], f[1])
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
