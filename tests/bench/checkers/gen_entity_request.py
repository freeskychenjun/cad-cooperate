#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""gen_entity_request.py - build an `entity -RequestFile` payload from a prior query/read.

The benchmark tasks are written the way an agent actually works: `query` first to locate
handles, then assemble one batch request for `entity`, then apply it. This script is the
"assemble the JSON" step (local, ~50 ms) - it is NOT a CAD command and the task definitions
count it separately from the CAD command budget.

Modes (all write {"items":[...]} to --out):
  update-text        --query q.json --set-text "新文字"            (expect: type/layer/text)
  update-layer       --query q.json --set-layer "NEW"             (expect: type/layer)
  update-height      --query q.json --set-height 2.5              (expect: type/layer/text)
  delete-zero-length --query q.json                               (lines whose bbox is a point)
  mark-xdata         --query q.json --app AI_MARK --value bench    (set xdata on each item)
  delete-marked      --query q.json                               (delete each item, expect text)

Optional --also-out writes a SECOND file with the same handles (used by W6: it builds the
"mark" request and the "delete after marking" request in one local step, because handles do
not change when xdata is added).

Output: one JSON line on stdout: {"ok":true,"mode":...,"items":N,"out":...,"alsoOut":...}
"""

from __future__ import annotations

import argparse
import json
import os
import sys

TEXT_TYPES = {"TEXT", "MTEXT", "AcDbText", "AcDbMText", "ACDBTEXT", "ACDBMTEXT"}


def read_json(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return json.loads(raw.decode("utf-8", "replace"))


def items_of(payload):
    """query/read responses may carry items[] or entities[]; accept both."""
    if not isinstance(payload, dict):
        return []
    result = payload.get("result") if isinstance(payload.get("result"), dict) else payload
    for key in ("items", "entities"):
        val = result.get(key)
        if isinstance(val, list):
            return val
    return []


def is_text(item):
    kind = str(item.get("type") or item.get("kind") or "").upper()
    return any(t.upper() in kind for t in TEXT_TYPES) or kind == "TEXT" or kind == "MTEXT"


def bbox_of(item):
    bb = item.get("bbox")
    if not isinstance(bb, dict):
        return None
    try:
        mn = [float(v) for v in bb.get("min")[:2]]
        mx = [float(v) for v in bb.get("max")[:2]]
    except (TypeError, ValueError, IndexError):
        return None
    return mn, mx


def is_zero_length(item, tol):
    b = bbox_of(item)
    if b is None:
        return False
    mn, mx = b
    return abs(mx[0] - mn[0]) <= tol and abs(mx[1] - mn[1]) <= tol


def expect_of(item, fields):
    exp = {}
    for f in fields:
        if f == "type":
            val = item.get("type")
        elif f == "layer":
            val = item.get("layer")
        elif f == "text":
            val = item.get("text")
        elif f == "height":
            val = item.get("height")
        else:
            continue
        if val is not None:
            exp[f] = val
    return exp


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", required=True,
                    choices=["update-text", "update-layer", "update-height",
                             "delete-zero-length", "mark-xdata", "delete-marked"])
    ap.add_argument("--query", required=True, help="query/read response JSON that carries the handles")
    ap.add_argument("--out", required=True)
    ap.add_argument("--also-out", default=None)
    ap.add_argument("--set-text", default=None)
    ap.add_argument("--set-layer", default=None)
    ap.add_argument("--set-height", type=float, default=None)
    ap.add_argument("--app", default="AI_MARK")
    ap.add_argument("--value", default="bench")
    ap.add_argument("--tol", type=float, default=1e-6)
    ap.add_argument("--expect-fields", default="")
    args = ap.parse_args(argv)

    payload = read_json(args.query)
    items = [it for it in items_of(payload) if isinstance(it, dict) and it.get("handle")]
    batch = []
    mode = args.mode

    if mode == "update-text":
        for it in items:
            if not is_text(it):
                continue
            exp = expect_of(it, ["type", "layer", "text"])
            st = {"text": args.set_text}
            batch.append({"op": "update", "handle": it["handle"], "expect": exp, "set": st})
    elif mode == "update-layer":
        for it in items:
            batch.append({"op": "update", "handle": it["handle"],
                          "expect": expect_of(it, ["type", "layer"]),
                          "set": {"layer": args.set_layer}})
    elif mode == "update-height":
        for it in items:
            if not is_text(it):
                continue
            batch.append({"op": "update", "handle": it["handle"],
                          "expect": expect_of(it, ["type", "layer", "text"]),
                          "set": {"height": args.set_height}})
    elif mode == "delete-zero-length":
        for it in items:
            if is_zero_length(it, args.tol):
                batch.append({"op": "delete", "handle": it["handle"],
                              "expect": expect_of(it, ["type", "layer"])})
    elif mode == "mark-xdata":
        for it in items:
            entry = {"op": "update", "handle": it["handle"],
                     "expect": expect_of(it, ["type", "layer", "text"]),
                     "xdata": {"app": args.app, "value": args.value}}
            batch.append(entry)
        if args.also_out:
            deletes = [{"op": "delete", "handle": it["handle"],
                        "expect": expect_of(it, ["type", "layer", "text"])} for it in items]
            write_json(args.also_out, {"items": deletes})
    elif mode == "delete-marked":
        for it in items:
            batch.append({"op": "delete", "handle": it["handle"],
                          "expect": expect_of(it, ["type", "layer", "text"])})

    write_json(args.out, {"items": batch})
    print(json.dumps({"ok": True, "mode": mode, "items": len(batch), "out": args.out,
                      "alsoOut": args.also_out, "handles": [i.get("handle") for i in batch][:8]},
                     ensure_ascii=False))
    return 0


def write_json(path, payload):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
        fh.write("\n")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
