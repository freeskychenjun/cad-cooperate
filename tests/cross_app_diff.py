#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""cross_app_diff.py - compare the same section drawn by several CAD products.

Why this exists: the per-app acceptance runner checks "did we draw what the model asked for".
For a cross-product comparison we need the other direction: "did all products draw the same
thing". Each app's `read` JSON is normalised (handles dropped, coordinates rounded, types and
layers folded) and the result is diffed pairwise, so a product-specific quirk shows up as a
concrete mismatch instead of a number that happens to be equal.

Usage:
    python tests/cross_app_diff.py --read a=tmp/compare/zwcad16/read.json
                                   --read b=tmp/compare/zwcad2025/read.json
                                   --read c=tmp/compare/autocad2007/read.json
                                   [--tol 1e-6] [--json out.json]
Exit code 0 when every pair matches, 1 otherwise.
"""
import argparse
import json
import sys


def load_entities(path):
    with open(path, 'r', encoding='utf-8-sig') as f:
        doc = json.load(f)
    if not doc.get('ok'):
        raise SystemExit('read json not ok: %s -> %s' % (path, doc.get('error')))
    ents = doc['result']['entities']
    out = []
    for e in ents:
        kind = (e.get('kind') or e.get('type') or '').upper()
        # fold product-specific ObjectName spellings into one vocabulary
        if 'POLYLINE' in kind:
            kind = 'LWPOLYLINE'
        elif 'LINE' in kind:
            kind = 'LINE'
        elif 'TEXT' in kind:
            kind = 'TEXT'
        elif 'CIRCLE' in kind:
            kind = 'CIRCLE'
        rec = {
            'kind': kind,
            'layer': e.get('layer'),
            'space': e.get('space'),
            'linetype': (e.get('linetype') or '').upper() or None,
        }
        if kind == 'LWPOLYLINE':
            rec['points'] = e.get('points') or e.get('vertices') or []
        elif kind == 'LINE':
            rec['start'] = e.get('start')
            rec['end'] = e.get('end')
        elif kind == 'TEXT':
            rec['text'] = e.get('text')
            rec['insertion'] = e.get('insertion') or e.get('pos')
            rec['height'] = e.get('height')
        elif kind == 'CIRCLE':
            rec['center'] = e.get('center')
            rec['radius'] = e.get('radius')
        out.append(rec)
    # deterministic order that does not depend on each CAD's entity order
    out.sort(key=lambda r: json.dumps(r, sort_keys=True, ensure_ascii=False))
    return out


def norm(value, tol):
    if isinstance(value, float):
        return round(value / tol) * tol if tol else round(value, 9)
    if isinstance(value, list):
        return [norm(v, tol) for v in value]
    if isinstance(value, dict):
        return {k: norm(v, tol) for k, v in sorted(value.items())}
    return value


def diff(a, b, tol):
    """Return (mismatch_count, first_n_differences)."""
    diffs = []
    na, nb = norm(a, tol), norm(b, tol)
    if len(na) != len(nb):
        diffs.append('entity count differs: %d vs %d' % (len(na), len(nb)))
    for i, (x, y) in enumerate(zip(na, nb)):
        if x != y:
            diffs.append('entity[%d] differs:\n  a=%s\n  b=%s' % (i, json.dumps(x, ensure_ascii=False), json.dumps(y, ensure_ascii=False)))
            if len(diffs) >= 8:
                break
    return len(diffs), diffs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--read', action='append', required=True, metavar='NAME=PATH')
    ap.add_argument('--tol', type=float, default=1e-6)
    ap.add_argument('--json', default=None)
    args = ap.parse_args()

    sets = {}
    for spec in args.read:
        name, _, path = spec.partition('=')
        if not path:
            raise SystemExit('bad --read spec: %s (want NAME=PATH)' % spec)
        sets[name] = load_entities(path)

    names = list(sets.keys())
    print('cross-app diff (%d products, %d entities each target)' % (len(names), len(next(iter(sets.values())))))
    pairs = []
    ok = True
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            a, b = names[i], names[j]
            count, diffs = diff(sets[a], sets[b], args.tol)
            pairs.append({'a': a, 'b': b, 'mismatches': count, 'ok': count == 0, 'details': diffs})
            status = 'MATCH' if count == 0 else 'DIFF(%d)' % count
            print('  %-12s vs %-12s : %s' % (a, b, status))
            if count:
                ok = False
                for d in diffs:
                    print('    ' + d.replace('\n', '\n    '))
    if args.json:
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump({'ok': ok, 'pairs': pairs}, f, ensure_ascii=False, indent=2)
    print('RESULT:', 'ALL MATCH' if ok else 'MISMATCH')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
