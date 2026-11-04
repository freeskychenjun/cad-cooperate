#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""corrupt_request.py - deliberately damage an `entity -Items` payload (negative tests).

Modes:
  bad-handle-index  --index N   replace items[N].handle with a handle that cannot exist
  bad-expect-index  --index N   make items[N].expect wrong (text -> "does-not-match")
  bad-height-index  --index N   set items[N].set.height to an illegal negative value

The payload shape is preserved so the helper parses it; only the semantics are broken.
Prints one JSON line: {"ok":true,"mode":...,"index":N,"handle":...}
"""

from __future__ import annotations

import argparse
import json
import sys


def read_json(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return json.loads(raw.decode("utf-8", "replace"))


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mode", required=True,
                    choices=["bad-handle-index", "bad-expect-index", "bad-height-index"])
    ap.add_argument("--index", type=int, default=0)
    args = ap.parse_args(argv)

    payload = read_json(args.inp)
    items = payload.get("items") or []
    if not items:
        print(json.dumps({"ok": False, "error": "no items in " + args.inp}, ensure_ascii=False))
        return 2
    idx = max(0, min(args.index, len(items) - 1))
    item = items[idx]
    old_handle = item.get("handle")
    if args.mode == "bad-handle-index":
        item["handle"] = "DEAD" + str(idx + 1)
    elif args.mode == "bad-expect-index":
        exp = item.setdefault("expect", {})
        exp["text"] = "不存在的文字-" + str(idx)
    elif args.mode == "bad-height-index":
        st = item.setdefault("set", {})
        st["height"] = -5.0
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print(json.dumps({"ok": True, "mode": args.mode, "index": idx,
                      "oldHandle": old_handle, "newHandle": item.get("handle")},
                     ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
