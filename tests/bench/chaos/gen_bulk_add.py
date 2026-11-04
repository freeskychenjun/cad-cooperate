#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""gen_bulk_add.py - build an `entity -Items` payload that ADDS N distinct text entities.

Used by the E3 chaos/concurrency harness: every session writes its own set of entities
(layer CHAOS, session/tag embedded in the text), so the final drawing can be checked for
"zero lost updates" and "no duplicate handles".

    python gen_bulk_add.py --session S1 --round 1 --count 5 --out items.json
"""

from __future__ import annotations

import argparse
import json
import sys


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--session", required=True)
    ap.add_argument("--round", type=int, default=1)
    ap.add_argument("--count", type=int, default=5)
    ap.add_argument("--layer", default="CHAOS")
    ap.add_argument("--height", type=float, default=2.5)
    ap.add_argument("--out", required=True)
    ap.add_argument("--x0", type=float, default=500.0)
    ap.add_argument("--step", type=float, default=3.0)
    args = ap.parse_args(argv)

    # deterministic geometry: session index gives a distinct column, round/item give the row
    try:
        sidx = int(str(args.session).lstrip("Ss"))
    except ValueError:
        sidx = 0
    items = []
    for i in range(args.count):
        x = args.x0 + sidx * 40.0 + i * args.step
        y = 500.0 + args.round * 20.0 + i * 2.0
        text = "%s-R%d-%02d" % (args.session, args.round, i + 1)
        items.append({
            "op": "add",
            "type": "text",
            "layer": args.layer,
            "geom": {"text": text, "insert": [x, y], "height": args.height},
            "props": {},
        })
    payload = {"items": items}
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print(json.dumps({"ok": True, "session": args.session, "round": args.round,
                      "count": len(items), "out": args.out}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
