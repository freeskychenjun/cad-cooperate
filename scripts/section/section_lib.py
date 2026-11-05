# -*- coding: utf-8 -*-
"""Shared core of the two 断面 (cross-section) builders.

Single implementation of everything the two entry points used to duplicate:

    build_sections_gcd.py        elevation source = GCD gc200 block insertion Z
    build_sections_from_dmx.py   elevation source = DGX contour intersections + ZDH POINT Z

Both routes only differ in — and this module declares that difference as data in
``VARIANTS``:

    * elevation source            (GCD block Z  vs  contour×line intersection + spot Z)
    * input JSON schema           (sections-src.json  vs  geom-com.json + contours.json)
    * summary columns             (高程点数  vs  等高线交点/高程点)
    * long/wide/csv layout        (反向起点距+块属性  vs  来源+对象, 宽表+仅高程点)
    * default output name
    * optional CLI extras         (--preview-chainage / --check-chainage: gcd only)

Everything else lives here exactly once: constants, error codes, input
validation, geometry helpers, the grid spatial index, the 1:1 label association
(8 m, chainage regex), downstream-direction resolution, offset ordering,
dedup + MIN_PTS filtering, the workbook writer (styles / widths / 5 sheets),
the preview PNGs and the CLI/stdout contract.

Compatibility (kept byte-identical to the pre-merge scripts): CLI options and
defaults, exit codes (0 / 2), stdout lines, artefact names and contents.

Python 3.9 compatible (no match/walrus/3.10+ syntax), UTF-8 source.
"""

import argparse
import datetime
import json
import math
import os
import re
import sys
import tempfile
import time

# ---------------------------------------------------------------------------
# constants shared by both routes
# ---------------------------------------------------------------------------
LABEL_R = 8.0        # label -> section line association radius (m)
DEDUP_D = 0.05       # duplicate offset threshold (m)
MIN_PTS = 3          # a usable section needs at least this many profile points

DEFAULT_CHECK_CHAINAGE = '16+475'   # gcd preview/self-check section

CHAINAGE_RE = re.compile(r'^\d+\+\d+(?:\.\d+)?$')     # e.g. 16+475 / 1+324.5
ELEV_RE = re.compile(r'^\d+\.\d+$')                   # e.g. 21.84
_CHAINAGE_PARSE_RE = re.compile(r'^(\d+)\+(\d+(?:\.\d+)?)$')


# ---------------------------------------------------------------------------
# errors + input loading / validation
# ---------------------------------------------------------------------------
class BuildError(Exception):
    """带机器可读 code 的失败；main() 统一转成 stderr 一行 + 退出码 2。"""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def load_json(path, what):
    """读输入 JSON：缺失/坏 JSON/不可读 -> BuildError（明确错误码，不裸 traceback）。"""
    try:
        with open(path, 'r', encoding='utf-8') as fh:
            return json.load(fh)
    except FileNotFoundError:
        raise BuildError('NO_INPUT', 'missing input: %s' % path)
    except json.JSONDecodeError as exc:
        raise BuildError('BAD_INPUT', '%s 不是合法 JSON（%s）: %s' % (what, path, exc))
    except OSError as exc:
        raise BuildError('BAD_INPUT', '读取 %s 失败（%s）: %s' % (what, path, exc))


def need(obj, key, what, where=''):
    if not isinstance(obj, dict) or key not in obj:
        raise BuildError('BAD_INPUT', '%s%s 缺键 %r' % (what, where, key))
    return obj[key]


def validate_sections_src(d):
    """sections-src.json：doc / lines[] / blocks[] / labels[]，逐项校验必需键。"""
    need(d, 'doc', 'sections-src.json')
    lines = need(d, 'lines', 'sections-src.json')
    blocks = need(d, 'blocks', 'sections-src.json')
    labels = need(d, 'labels', 'sections-src.json')
    for name, arr in (('lines', lines), ('blocks', blocks), ('labels', labels)):
        if not isinstance(arr, list):
            raise BuildError('BAD_INPUT', 'sections-src.json: %s 必须是数组' % name)
    for i, e in enumerate(lines):
        for key in ('h', 'ax', 'ay', 'bx', 'by'):
            need(e, key, 'sections-src.json: lines', '[%d]' % i)
    for i, b in enumerate(blocks):
        for key in ('x', 'y', 'z', 'h'):
            need(b, key, 'sections-src.json: blocks', '[%d]' % i)
    for i, t in enumerate(labels):
        for key in ('x', 'y', 'text'):
            need(t, key, 'sections-src.json: labels', '[%d]' % i)
    return d


def validate_geom(g):
    """geom-com.json：必须有 doc / entities[]，且每个实体至少有图层键 'l'。"""
    need(g, 'doc', 'geom-com.json')
    ents = need(g, 'entities', 'geom-com.json')
    if not isinstance(ents, list):
        raise BuildError('BAD_INPUT', 'geom-com.json: entities 必须是数组')
    for i, e in enumerate(ents):
        if not isinstance(e, dict) or 'l' not in e:
            raise BuildError('BAD_INPUT', 'geom-com.json: entities[%d] 缺键 "l"（图层）' % i)
    return g


def validate_contours(c):
    """contours.json：必须有 contours[]，每条等高线有 z / h / v（顶点数组）。"""
    cs = need(c, 'contours', 'contours.json')
    if not isinstance(cs, list):
        raise BuildError('BAD_INPUT', 'contours.json: contours 必须是数组')
    for i, ct in enumerate(cs):
        for key in ('z', 'h', 'v'):
            need(ct, key, 'contours.json', '[%d]' % i)
        if not isinstance(ct['v'], list) or len(ct['v']) < 2:
            raise BuildError('BAD_INPUT',
                             'contours.json: contours[%d].v 至少需要 2 个顶点' % i)
    return c


def openpyxl_api():
    """延迟导入 openpyxl：缺库时给 NO_OPENPYXL 明确错误（对齐 prepare.py:243-248）。"""
    try:
        from openpyxl import Workbook
        from openpyxl.styles import Font, PatternFill, Alignment
        from openpyxl.utils import get_column_letter
    except ImportError as exc:
        raise BuildError('NO_OPENPYXL',
                         '缺少 openpyxl：%s（pip install -r requirements.txt 或 '
                         'pip install openpyxl）' % exc)
    return Workbook, Font, PatternFill, Alignment, get_column_letter


# ---------------------------------------------------------------------------
# lightweight timing (opt-in: CAD_SECTION_TIMING=1 -> one stderr line, stdout untouched)
# ---------------------------------------------------------------------------
class Timer(object):
    """阶段计时；默认静默，设 CAD_SECTION_TIMING=1 时在结束时往 stderr 打一行。"""

    def __init__(self, enabled=None):
        if enabled is None:
            enabled = os.environ.get('CAD_SECTION_TIMING') == '1'
        self.enabled = enabled
        self.t0 = time.perf_counter()
        self.marks = []

    def mark(self, label):
        now = time.perf_counter()
        self.marks.append((label, (now - self.t0) * 1000.0))
        self.t0 = now

    def report(self):
        if not self.enabled:
            return
        parts = ['%s=%.1fms' % (label, ms) for label, ms in self.marks]
        sys.stderr.write('timing: ' + ' '.join(parts) + '\n')


# ---------------------------------------------------------------------------
# geometry helpers
# ---------------------------------------------------------------------------
def seg_dist_t(px, py, ax, ay, bx, by):
    """Point -> segment distance and clamped projection parameter t in [0, 1]."""
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    if l2 == 0:
        return math.hypot(px - ax, py - ay), 0.0
    t = ((px - ax) * dx + (py - ay) * dy) / l2
    tc = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + tc * dx), py - (ay + tc * dy)), tc


def intersect(p1, p2, p3, p4):
    """Return (x, y) of segment intersection or None."""
    x1, y1 = p1
    x2, y2 = p2
    x3, y3 = p3
    x4, y4 = p4
    d = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3)
    if abs(d) < 1e-12:
        return None
    t = ((x3 - x1) * (y4 - y3) - (y3 - y1) * (x4 - x3)) / d
    u = ((x3 - x1) * (y2 - y1) - (y3 - y1) * (x2 - x1)) / d
    if -1e-9 <= t <= 1 + 1e-9 and -1e-9 <= u <= 1 + 1e-9:
        return (x1 + t * (x2 - x1), y1 + t * (y2 - y1))
    return None


def chainage_to_m(s):
    m = _CHAINAGE_PARSE_RE.match(s)
    return float(m.group(1)) * 1000 + float(m.group(2)) if m else None


# ---------------------------------------------------------------------------
# grid spatial index (replaces the section × object double loops)
# ---------------------------------------------------------------------------
def auto_cell_size(segs):
    """Adaptive bucket size: 2× median segment length, capped at extent/128 buckets.

    Keeps the grid at O(#objects) occupied cells for both the ~120 m section
    lines and the short contour segments, without a hand-tuned magic number.
    """
    n = len(segs)
    lens = sorted(math.hypot(s[2] - s[0], s[3] - s[1]) for s in segs)
    med = lens[n // 2] if n else 1.0
    xs = [s[0] for s in segs] + [s[2] for s in segs]
    ys = [s[1] for s in segs] + [s[3] for s in segs]
    extent = max(max(xs) - min(xs), max(ys) - min(ys)) if n else 1.0
    return max(med * 2.0, extent / 128.0, 1e-9)


class SegmentGrid(object):
    """Uniform grid over segment bounding boxes.

    Three exact queries, all equivalent to the brute-force loops they replace:

    ``nearest(px, py, max_dist)``
        -> ``(dist, seg_index, t)``; closest segment, ties broken by the lowest
        index (the pre-merge code kept the first line at equal distance because
        it used a strict ``<`` while walking the input order).  ``(1e9, -1, 0.0)``
        when nothing is within ``max_dist``.
    ``query_bbox(xmin, ymin, xmax, ymax)``
        -> ascending segment indices whose *bbox* touches the query bbox — a
        superset of the brute-force bbox prefilter (same final result, since the
        caller still runs the original bbox test and the exact intersection).
    ``query_radius(px, py, radius)``
        -> ``[(seg_index, dist, t)]`` for every segment within ``radius``,
        ascending by index.

    Exactness argument for the ring expansion: a segment is registered in every
    cell its bbox overlaps, so if the true nearest point of segment ``S`` is at
    distance ``d``, ``S`` is registered in a cell whose Chebyshev ring around
    the query cell is ``<= floor(d / cell) + 1``.  The loop therefore keeps
    expanding while ``(r - 1) * cell <= bound`` (``bound`` = min(best, max_dist)),
    which always covers that ring — conservative, never misses, and it stops
    after one or two rings because the tolerances (0.5 m / 8 m) are far below
    the adaptive cell size.
    """

    def __init__(self, segs, cell=None):
        self.segs = segs
        self.cell = cell if cell else auto_cell_size(segs)
        n = len(segs)
        self._mark = [0] * n
        self._stamp = 0
        if not n:
            self.x0 = self.y0 = 0.0
            self.nx = self.ny = 1
            self.buckets = {}
            return
        xs = [s[0] for s in segs] + [s[2] for s in segs]
        ys = [s[1] for s in segs] + [s[3] for s in segs]
        self.x0 = min(xs)
        self.y0 = min(ys)
        self.nx = int((max(xs) - self.x0) / self.cell) + 1
        self.ny = int((max(ys) - self.y0) / self.cell) + 1
        buckets = {}
        for i, (ax, ay, bx, by) in enumerate(segs):
            ix0, iy0 = self._cell_of(min(ax, bx), min(ay, by))
            ix1, iy1 = self._cell_of(max(ax, bx), max(ay, by))
            for ix in range(ix0, ix1 + 1):
                for iy in range(iy0, iy1 + 1):
                    key = (ix, iy)
                    b = buckets.get(key)
                    if b is None:
                        buckets[key] = [i]
                    else:
                        b.append(i)
        self.buckets = buckets

    def _cell_of(self, x, y):
        ix = int((x - self.x0) // self.cell)
        iy = int((y - self.y0) // self.cell)
        if ix < 0:
            ix = 0
        elif ix >= self.nx:
            ix = self.nx - 1
        if iy < 0:
            iy = 0
        elif iy >= self.ny:
            iy = self.ny - 1
        return ix, iy

    @staticmethod
    def _ring_cells(ci, cj, r):
        """Yield the cells of Chebyshev ring ``r`` around ``(ci, cj)`` once each."""
        if r == 0:
            yield ci, cj
            return
        lo, hi = cj - r, cj + r
        for ix in range(ci - r, ci + r + 1):
            yield ix, lo
            yield ix, hi
        for iy in range(lo + 1, hi):
            yield ci - r, iy
            yield ci + r, iy

    def _next_stamp(self):
        self._stamp += 1
        if self._stamp > 1 << 30:          # pragma: no cover - practically unreachable
            self._mark = [0] * len(self.segs)
            self._stamp = 1
        return self._stamp

    def nearest(self, px, py, max_dist):
        """Closest segment to (px, py); see class docstring for the tie-break rule."""
        segs = self.segs
        best_d = 1e9
        best_i = -1
        best_t = 0.0
        if not segs:
            return best_d, best_i, best_t
        stamp = self._next_stamp()
        mark = self._mark
        buckets = self.buckets
        cell = self.cell
        ci, cj = int((px - self.x0) // cell), int((py - self.y0) // cell)
        r = 0
        while True:
            if r > 0:
                bound = best_d if best_d <= max_dist else max_dist
                if (r - 1) * cell > bound:
                    break
            for ix, iy in self._ring_cells(ci, cj, r):
                bucket = buckets.get((ix, iy))
                if not bucket:
                    continue
                for i in bucket:
                    if mark[i] == stamp:
                        continue
                    mark[i] = stamp
                    ax, ay, bx, by = segs[i]
                    dd, tc = seg_dist_t(px, py, ax, ay, bx, by)
                    if dd < best_d or (dd == best_d and i < best_i):
                        best_d, best_i, best_t = dd, i, tc
            r += 1
        return best_d, best_i, best_t

    def query_bbox(self, xmin, ymin, xmax, ymax):
        """Ascending indices of segments whose bbox touches the query bbox."""
        out = []
        if not self.segs:
            return out
        ix0, iy0 = self._cell_of(xmin, ymin)
        ix1, iy1 = self._cell_of(xmax, ymax)
        stamp = self._next_stamp()
        mark = self._mark
        buckets = self.buckets
        for ix in range(ix0, ix1 + 1):
            for iy in range(iy0, iy1 + 1):
                bucket = buckets.get((ix, iy))
                if not bucket:
                    continue
                for i in bucket:
                    if mark[i] != stamp:
                        mark[i] = stamp
                        out.append(i)
        out.sort()
        return out

    def query_radius(self, px, py, radius):
        """All segments within ``radius`` of (px, py), ascending by index."""
        out = []
        if not self.segs:
            return out
        stamp = self._next_stamp()
        mark = self._mark
        buckets = self.buckets
        cell = self.cell
        segs = self.segs
        ci, cj = int((px - self.x0) // cell), int((py - self.y0) // cell)
        r = 0
        while True:
            if r > 0 and (r - 1) * cell > radius:
                break
            for ix, iy in self._ring_cells(ci, cj, r):
                bucket = buckets.get((ix, iy))
                if not bucket:
                    continue
                for i in bucket:
                    if mark[i] == stamp:
                        continue
                    mark[i] = stamp
                    ax, ay, bx, by = segs[i]
                    dd, tc = seg_dist_t(px, py, ax, ay, bx, by)
                    if dd <= radius:
                        out.append((i, dd, tc))
            r += 1
        out.sort(key=lambda item: item[0])
        return out


# ---------------------------------------------------------------------------
# section core: line index, label association, downstream direction, rows
# ---------------------------------------------------------------------------
def line_segments(lines):
    """-> [(ax, ay, bx, by)] in input order (the index is the line index)."""
    return [(e['ax'], e['ay'], e['bx'], e['by']) for e in lines]


def associate_labels(labels, lines, grid, radius=LABEL_R):
    """1:1 label -> nearest section line (within ``radius``), keyed by line handle.

    ``labels``: [(x, y, text)].  Exactly the pre-merge semantics: nearest line
    wins (ties -> first line in input order, via the grid's index tie-break);
    when several labels land on one line the pair with the smallest
    ``(distance, text)`` wins.
    """
    out = {}
    for lx, ly, text in labels:
        dd, idx, _tc = grid.nearest(lx, ly, radius)
        if idx >= 0 and dd <= radius:
            out.setdefault(lines[idx]['h'], []).append((dd, text))
    return dict((h, sorted(v)[0][1]) for h, v in out.items())


def downstream_vector(lines, chain_of):
    """Unit downstream vector + left normal from the chainage order of the lines."""
    mids = [((e['ax'] + e['bx']) / 2, (e['ay'] + e['by']) / 2) for e in lines]
    wc = sorted((chainage_to_m(chain_of[e['h']]), i)
                for i, e in enumerate(lines) if e['h'] in chain_of)
    p0 = mids[wc[0][1]]
    p1 = mids[wc[-1][1]]
    ux, uy = p1[0] - p0[0], p1[1] - p0[1]
    n = math.hypot(ux, uy) or 1.0
    ux, uy = ux / n, uy / n
    return ux, uy, -uy, ux


def left_is_a(e, ux, uy, lx, ly):
    """True when endpoint A is the left end (facing downstream = increasing chainage)."""
    ax, ay, bx, by = e['ax'], e['ay'], e['bx'], e['by']
    L = math.hypot(bx - ax, by - ay)
    if L == 0:
        return None
    dx, dy = (bx - ax) / L, (by - ay) / L
    return (dx * lx + dy * ly) >= 0


def make_row(e, chain, elev_note):
    """Common row skeleton for one section line."""
    ax, ay, bx, by = e['ax'], e['ay'], e['bx'], e['by']
    L = math.hypot(bx - ax, by - ay)
    return {
        'handle': e['h'], 'chain': chain,
        'chain_m': chainage_to_m(chain) if chain else None,
        'a': (ax, ay), 'b': (bx, by), 'len': L,
        'mid': ((ax + bx) / 2, (ay + by) / 2),
        'elev_note': elev_note,
    }


def order_rows(rows, ux, uy):
    """Sort sections by chainage (missing chainage last), then by downstream projection."""
    rows.sort(key=lambda r: (r['chain_m'] if r['chain_m'] is not None else 1e12,
                             r['mid'][0] * ux + r['mid'][1] * uy))
    for i, r in enumerate(rows, 1):
        r['idx'] = i


def nearest_line_points(points, lines, grid, tol):
    """Assign each point to its nearest line within ``tol`` -> ({handle: [(p, t)]}, unassigned).

    Replaces the ``for p: for line`` double loop (2,938 × 304 ≈ 893k distance
    computations) with the grid nearest-neighbour query.
    """
    by_line = dict((e['h'], []) for e in lines)
    unassigned = 0
    for p in points:
        dd, idx, tc = grid.nearest(p['x'], p['y'], tol)
        if idx >= 0 and dd <= tol:
            by_line[lines[idx]['h']].append((p, tc))
        else:
            unassigned += 1
    return by_line, unassigned


def points_within(points, lines, grid, tol):
    """Per-line list of (point, t) for every line the point lies within ``tol`` of."""
    by_line = dict((e['h'], []) for e in lines)
    for p in points:
        for idx, _dd, tc in grid.query_radius(p['x'], p['y'], tol):
            by_line[lines[idx]['h']].append((p, tc))
    return by_line


def dedup_points(pts, prefer_spot=False):
    """Keep the first point of every cluster closer than DEDUP_D (offset order).

    ``prefer_spot``: when a ZDH spot height coincides with a contour crossing,
    the spot height wins (dmx route; same as the pre-merge loop).
    """
    merged = []
    for item in pts:
        off = item[0]
        if merged and abs(merged[-1][0] - off) < DEDUP_D:
            if prefer_spot and len(item) > 2 and item[2].startswith('P:') \
                    and merged[-1][2].startswith('C:'):
                merged[-1] = item
            continue
        merged.append(item)
    return merged


def usable_rows(rows):
    return [r for r in rows if len(r['pts']) >= MIN_PTS]


# ---------------------------------------------------------------------------
# route gcd: GCD gc200 block insertion Z as ground elevation
# ---------------------------------------------------------------------------
BLOCK_TOL = 0.50     # block-to-line tolerance (m); in practice the blocks sit exactly on the line


def _load_gcd(tmp):
    src = os.path.join(tmp, 'cad-cooperate', 'sections-src.json')
    if not os.path.exists(src):
        raise BuildError('NO_INPUT', 'missing input: %s' % src)
    d = validate_sections_src(load_json(src, 'sections-src.json'))
    lines = d['lines']
    blocks = d['blocks']
    labels = d['labels']
    print('drawing:', d['doc'])
    print('sections:', len(lines), '| elevation blocks:', len(blocks),
          '| KZD labels:', len(labels))
    if not lines:
        raise BuildError('BAD_INPUT', 'sections-src.json: lines 为空（没有断面线实体）')
    if not blocks:
        raise BuildError('BAD_INPUT', 'sections-src.json: blocks 为空（没有高程块）')
    return {'d': d, 'lines': lines, 'blocks': blocks, 'labels': labels}


def _build_gcd(ctx, timer):
    d, lines, blocks, labels = ctx['d'], ctx['lines'], ctx['blocks'], ctx['labels']
    chain_labels = [t for t in labels if CHAINAGE_RE.match(t['text'])]
    elev_labels = [t for t in labels if ELEV_RE.match(t['text'])]
    print('chainage labels:', len(chain_labels), '| elevation labels:', len(elev_labels))

    # ---- associate labels to section lines (grid instead of labels × lines)
    grid = SegmentGrid(line_segments(lines))
    chain_of = associate_labels([(t['x'], t['y'], t['text']) for t in chain_labels], lines, grid)
    elev_of = associate_labels([(t['x'], t['y'], t['text']) for t in elev_labels], lines, grid)
    print('sections with chainage:', len(chain_of), '| with elevation note:', len(elev_of))
    if not chain_of:
        raise BuildError('BAD_INPUT',
                         'sections-src.json: 没有桩号文字能匹配到断面线（%.0f m 内），无法定下游方向'
                         % LABEL_R)
    timer.mark('associate')

    # ---- downstream direction from chainage order
    ux, uy, lx, ly = downstream_vector(lines, chain_of)

    # ---- assign blocks to lines (grid nearest instead of blocks × lines)
    by_line, unassigned = nearest_line_points(blocks, lines, grid, BLOCK_TOL)
    print('blocks assigned:', len(blocks) - unassigned, '| unassigned:', unassigned)
    timer.mark('assign')

    rows = []
    for e in lines:
        is_a = left_is_a(e, ux, uy, lx, ly)
        if is_a is None:
            continue
        L = math.hypot(e['bx'] - e['ax'], e['by'] - e['ay'])
        pts = []
        for b, tc in by_line[e['h']]:
            off = (tc * L) if is_a else (L - tc * L)
            pts.append((off, b['z'], b['h'], b.get('attr')))
        pts.sort(key=lambda r: r[0])
        row = make_row(e, chain_of.get(e['h'], ''), elev_of.get(e['h'], ''))
        row['left_is_a'] = is_a
        row['pts'] = dedup_points(pts)
        rows.append(row)

    order_rows(rows, ux, uy)
    usable = usable_rows(rows)
    print('sections: %d | usable(>=%d pts): %d' % (len(rows), MIN_PTS, len(usable)))
    timer.mark('rows')

    ctx.update(unassigned=unassigned, chain_of=chain_of, elev_of=elev_of,
               nlines=len(lines), nblocks=len(blocks), nlabels=len(labels))
    return rows


def _summary_gcd(r):
    pts = r['pts']
    return [r['idx'], r['chain'],
            round(r['chain_m'], 2) if r['chain_m'] is not None else '',
            r['handle'], round(r['a'][0], 3), round(r['a'][1], 3),
            round(r['b'][0], 3), round(r['b'][1], 3), round(r['len'], 2),
            'A端' if r['left_is_a'] else 'B端', len(pts),
            ('%.2f~%.2f' % (pts[0][0], pts[-1][0])) if pts else '',
            ('%.2f~%.2f' % (min(p[1] for p in pts), max(p[1] for p in pts))) if pts else '',
            min((p[1] for p in pts), default=''), r['elev_note'],
            'OK' if len(pts) >= MIN_PTS else '点数不足']


def _long_gcd(r):
    out = []
    for off, z, h, at in r['pts']:
        out.append([r['idx'], r['chain'], round(off, 3), round(r['len'] - off, 3),
                    round(z, 3), h, at])
    return out


def _csv_gcd(r):
    return ['%d,%s,%.3f,%.3f,%.3f,%s\n' % (r['idx'], r['chain'], off, r['len'] - off, z, h)
            for off, z, h, _at in r['pts']]


def _notes_gcd(ctx, args, rows):
    d = ctx['d']
    lines = ctx['lines']
    blocks = ctx['blocks']
    lens = sorted(r['len'] for r in rows)
    total = sum(len(r['pts']) for r in rows)
    return [
        ('项目', '说明'),
        ('数据来源', '图纸：' + d['doc'] + '（只读读取，未修改图纸）'),
        ('断面线', '图层 dmx，共 %d 条；长 %.1f~%.1f m，中位 %.1f m'
         % (len(rows), lens[0], lens[-1], lens[len(lens) // 2])),
        ('断面编号', '图层 KZD 文字桩号（如 %s），%.0f m 内最近匹配：%d/%d'
         % (args.preview_chainage or DEFAULT_CHECK_CHAINAGE, LABEL_R,
            len(ctx['chain_of']), len(lines))),
        ('高程点', '图层 GCD 的 gc200 块，共 %d 个（已分配 %d）；高程 = 块插入点 Z（同名属性 height 同值）'
         % (len(blocks), len(blocks) - ctx['unassigned'])),
        ('起点距', '从断面线"左端"起算（面朝下游、桩号增大方向的左手侧）；长表另给"反向起点距"便于对照'),
        ('渠底注记', 'KZD 图层每断面一个高程注记（如 21.84），与 KZD 的 gc257 块 Z 值一致，作参考列'),
        ('单位', '坐标与高程均为图纸单位（insunits=0 未定义，实测为米）'),
        ('合计', '断面 %d 条，高程点 %d 个' % (len(rows), total)),
        ('生成时间', datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')),
    ]


def _epilogue_gcd(ctx, args, rows):
    """末尾逐点自检（--check-chainage，默认 16+475）。"""
    if not args.check_chainage:
        return
    for r in rows:
        if r['chain'] == args.check_chainage:
            print('\n%s check: handle=%s A=(%.3f,%.3f) B=(%.3f,%.3f) L=%.3f npts=%d' % (
                args.check_chainage, r['handle'], r['a'][0], r['a'][1], r['b'][0], r['b'][1],
                r['len'], len(r['pts'])))
            for off, z, h, at in r['pts']:
                print('   起点距 %7.2f m   高程 %7.2f m   (%s)' % (off, z, h))


# ---------------------------------------------------------------------------
# route dmx: DGX contour × section-line intersections + ZDH spot heights
# ---------------------------------------------------------------------------
POINT_TOL = 0.50     # spot height considered "on" the section line (m)


def _load_dmx(tmp):
    gj = os.path.join(tmp, 'cad-cooperate', 'geom-com.json')
    cj = os.path.join(tmp, 'cad-cooperate', 'contours.json')
    for f in (gj, cj):
        if not os.path.exists(f):
            raise BuildError('NO_INPUT', 'missing input: %s' % f)
    g = validate_geom(load_json(gj, 'geom-com.json'))
    c = validate_contours(load_json(cj, 'contours.json'))
    ents = g['entities']
    print('drawing:', g['doc'])
    print('entities:', len(ents), '| contours:',
          c['count'] if 'count' in c else len(c['contours']))
    secs = [e for e in ents if e['l'] == 'dmx' and 'p' in e and 'p2' in e]
    spots = [e for e in ents if e.get('o') == 'AcDbPoint' and e['l'] == 'ZDH' and 'p' in e]
    kzd = [e for e in ents if e['l'] == 'KZD' and 'x' in e and 'p' in e]
    chain_labels = [t for t in kzd if CHAINAGE_RE.match(t['x'])]
    elev_labels = [t for t in kzd if ELEV_RE.match(t['x'])]
    print('sections:', len(secs), '| spot points:', len(spots),
          '| chainage labels:', len(chain_labels), '| elevation labels:', len(elev_labels))
    if not secs:
        raise BuildError('BAD_INPUT', 'geom-com.json: 没有图层 dmx 的断面线实体（检查导出/图层名）')
    if not chain_labels:
        raise BuildError('BAD_INPUT', 'geom-com.json: 没有 KZD 桩号文字（如 1+324），无法排序断面')
    # normalise the two entity families once: flat section lines / spot heights (Z != 0)
    flat_secs = [{'h': e['h'], 'ax': e['p'][0], 'ay': e['p'][1],
                  'bx': e['p2'][0], 'by': e['p2'][1]} for e in secs]
    flat_spots = [{'x': q['p'][0], 'y': q['p'][1],
                   'z': q['p'][2] if len(q['p']) > 2 else 0.0, 'h': q['h']}
                  for q in spots if (q['p'][2] if len(q['p']) > 2 else 0.0)]
    return {'g': g, 'c': c, 'secs': flat_secs, 'spots': flat_spots,
            'nsecs': len(secs), 'nspots': len(spots),
            'chain_labels': chain_labels, 'elev_labels': elev_labels}


def _contour_segments(contours):
    """Flatten contours into (x1, y1, x2, y2, z, h, sx1, sy1, sx2, sy2) tuples."""
    segs = []
    for ct in contours:
        z = ct['z']
        h = ct['h']
        vs = ct['v']
        for a, b in zip(vs, vs[1:]):
            segs.append((a[0], a[1], b[0], b[1], z, h,
                         min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])))
    return segs


def _build_dmx(ctx, timer):
    c = ctx['c']
    lines, spots = ctx['secs'], ctx['spots']

    # contour segments with bbox
    segs = _contour_segments(c['contours'])
    print('contour segments:', len(segs))
    seg_grid = SegmentGrid([(s[0], s[1], s[2], s[3]) for s in segs])
    timer.mark('segments')

    # grid over section lines: label association + spot membership
    line_grid = SegmentGrid(line_segments(lines))

    chain_of = associate_labels([(t['p'][0], t['p'][1], t['x']) for t in ctx['chain_labels']],
                                lines, line_grid)
    elev_of = associate_labels([(t['p'][0], t['p'][1], t['x']) for t in ctx['elev_labels']],
                               lines, line_grid)
    if not chain_of:
        raise BuildError('BAD_INPUT',
                         'geom-com.json: 没有桩号文字能匹配到断面线（%.0f m 内），无法定下游方向'
                         % LABEL_R)
    timer.mark('associate')

    # downstream direction
    ux, uy, lx, ly = downstream_vector(lines, chain_of)

    # spot heights per line (grid radius instead of spots × lines)
    spot_by_line = points_within(spots, lines, line_grid, POINT_TOL)
    timer.mark('assign')

    rows = []
    for e in lines:
        ax, ay, bx, by = e['ax'], e['ay'], e['bx'], e['by']
        L = math.hypot(bx - ax, by - ay)
        if L == 0:
            continue
        is_a = left_is_a(e, ux, uy, lx, ly)
        sbb = (min(ax, bx), min(ay, by), max(ax, bx), max(ay, by))

        # 1) contour intersections (grid bbox query instead of sections × segments)
        hits = []
        for si in seg_grid.query_bbox(sbb[0], sbb[1], sbb[2], sbb[3]):
            x1, y1, x2, y2, z, h, sx1, sy1, sx2, sy2 = segs[si]
            if sx2 < sbb[0] or sx1 > sbb[2] or sy2 < sbb[1] or sy1 > sbb[3]:
                continue
            p = intersect((ax, ay), (bx, by), (x1, y1), (x2, y2))
            if p is None:
                continue
            off_a = math.hypot(p[0] - ax, p[1] - ay)
            off = off_a if is_a else (L - off_a)
            hits.append((off, z, 'C:' + h, 0, si))
        # 2) spot heights on the line (per-line list, input order preserved)
        for q, tc in spot_by_line[e['h']]:
            off = (tc * L) if is_a else (L - tc * L)
            hits.append((off, q['z'], 'P:' + q['h'], 1, 0))
        # stable-equivalent sort: offset, then source phase (contours before spots),
        # then the source's own input order
        hits.sort(key=lambda r: (r[0], r[3], r[4]))

        merged = dedup_points([(off, z, src) for off, z, src, _ph, _i in hits], prefer_spot=True)
        row = make_row(e, chain_of.get(e['h'], ''), elev_of.get(e['h'], ''))
        row['left_is_a'] = is_a
        row['pts'] = merged
        row['n_contour'] = sum(1 for pt in merged if pt[2].startswith('C:'))
        row['n_spot'] = sum(1 for pt in merged if pt[2].startswith('P:'))
        rows.append(row)
    print('sections built:', len(rows))
    timer.mark('rows')

    order_rows(rows, ux, uy)
    ctx.update(chain_of=chain_of, elev_of=elev_of, nsegs=len(segs),
               ntot=sum(len(r['pts']) for r in rows),
               nc=sum(r['n_contour'] for r in rows),
               nsp=sum(r['n_spot'] for r in rows))
    return rows


def _summary_dmx(r):
    pts = r['pts']
    return [r['idx'], r['chain'],
            round(r['chain_m'], 2) if r['chain_m'] is not None else '',
            r['handle'], round(r['a'][0], 3), round(r['a'][1], 3),
            round(r['b'][0], 3), round(r['b'][1], 3), round(r['len'], 2),
            'A端' if r['left_is_a'] else 'B端', r['n_contour'], r['n_spot'],
            ('%.2f~%.2f' % (pts[0][0], pts[-1][0])) if pts else '',
            ('%.2f~%.2f' % (min(p[1] for p in pts), max(p[1] for p in pts))) if pts else '',
            r['elev_note'], 'OK' if len(pts) >= MIN_PTS else '点数不足']


def _long_dmx(r):
    out = []
    for off, z, src in r['pts']:
        out.append([r['idx'], r['chain'], round(off, 3), round(z, 3),
                    '等高线' if src.startswith('C:') else '高程点', src[2:]])
    return out


def _csv_dmx(r):
    return ['%d,%s,%.3f,%.3f,%s,%s\n' % (r['idx'], r['chain'], off, z,
                                         'contour' if src.startswith('C:') else 'spot', src[2:])
            for off, z, src in r['pts']]


def _extra_dmx(r):
    """仅高程点 sheet: spot-height points only."""
    return [[r['idx'], r['chain'], round(off, 3), round(z, 3), src[2:]]
            for off, z, src in r['pts'] if src.startswith('P:')]


def _notes_dmx(ctx, args, rows):
    g = ctx['g']
    c = ctx['c']
    lens = sorted(r['len'] for r in rows)
    ntot, nc, nsp = ctx['ntot'], ctx['nc'], ctx['nsp']
    return [
        ('项目', '说明'),
        ('数据来源', '图纸：' + g['doc'] + '（只读读取，未修改图纸）'),
        ('断面线', '图层 dmx，共 %d 条；长 %.1f~%.1f m，中位 %.1f m'
         % (len(rows), lens[0], lens[-1], lens[len(lens) // 2])),
        ('断面编号', '图层 KZD 文字桩号（如 1+324），8 m 内最近匹配：%d/%d 条已匹配'
         % (len(ctx['chain_of']), ctx['nsecs'])),
        ('地形来源①', 'DGX 图层等高线（%d 条折线，等高距 1 m，高程 18~58 m）：与断面线求交，得 %d 个剖面高程点'
         % (len(c['contours']), nc)),
        ('地形来源②', 'ZDH 图层高程点（POINT 的 Z 值，仅 Z≠0）：投影到断面线（容差 %.2f m），得 %d 个点'
         % (POINT_TOL, nsp)),
        ('合并规则', '按起点距排序；同一位置（<%.2f m）重复时优先保留高程点' % DEDUP_D),
        ('起点距', '从断面线"左端"起算：0=左岸，面朝下游（桩号增大方向）的左手侧为左端'),
        ('渠底注记', 'KZD 图层每断面的高程注记（如 22.84），作参考复核列，未参与剖面'),
        ('单位', '坐标与高程均为图纸单位（该图 insunits=0 未定义，实测为米）'),
        ('生成时间', datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')),
        ('合计', '断面 %d 条，剖面点 %d 个（等高线 %d + 高程点 %d）'
         % (len(rows), ntot, nc, nsp)),
    ]


def _epilogue_dmx(ctx, args, rows):
    print('sections=%d points=%d contour_hits=%d spot=%d'
          % (len(rows), ctx['ntot'], ctx['nc'], ctx['nsp']))


# ---------------------------------------------------------------------------
# workbook + csv + preview writers (one implementation, per-route layout data)
# ---------------------------------------------------------------------------
def _write_head(ws, headers, widths, style):
    bold, fill, alignment, get_column_letter = style
    for i, h in enumerate(headers, 1):
        c = ws.cell(row=1, column=i, value=h)
        c.font = bold
        c.fill = fill
        c.alignment = alignment
    ws.freeze_panes = 'A2'
    for i in range(1, len(headers) + 1):
        ws.column_dimensions[get_column_letter(i)].width = (widths[i - 1] if widths else 13)


def _write_wide_gcd(ws, rows, style):
    """gcd 宽表：先一段起点距，空两行，再一段高程（原样保留该布局）。"""
    bold = style[0]
    maxn = max((len(r['pts']) for r in rows), default=0)
    ws.cell(row=2, column=1, value='起点距(m)').font = bold
    for k in range(maxn):
        for r in rows:
            if k < len(r['pts']):
                ws.cell(row=3 + k, column=1 + r['idx'], value=round(r['pts'][k][0], 2))
    base = 3 + maxn + 2
    ws.cell(row=base, column=1, value='高程(m)').font = bold
    for k in range(maxn):
        for r in rows:
            if k < len(r['pts']):
                ws.cell(row=base + 1 + k, column=1 + r['idx'], value=r['pts'][k][1])


def _write_wide_dmx(ws, rows, style):
    """dmx 宽表：每列一个断面，逐行写高程（原样保留该布局）。"""
    maxn = max((len(r['pts']) for r in rows), default=0)
    for k in range(maxn):
        ws.append([r['pts'][k][1] if k < len(r['pts']) else None for r in rows])


def write_book(outdir, name, rows, variant, ctx, args):
    """Write <outdir>/<name>.xlsx (+ .csv); returns the list of written paths."""
    spec = VARIANTS[variant]
    Workbook, Font, PatternFill, Alignment, get_column_letter = openpyxl_api()
    os.makedirs(outdir, exist_ok=True)
    xlsx = os.path.join(outdir, name + '.xlsx')
    csv_path = os.path.join(outdir, name + '.csv')

    wb = Workbook()
    style = (Font(bold=True), PatternFill('solid', fgColor='DDEBF7'),
             Alignment(horizontal='center'), get_column_letter)

    ws = wb.active
    ws.title = '断面汇总'
    _write_head(ws, spec['summary_headers'], spec['summary_widths'], style)
    for r in rows:
        ws.append(spec['summary_row'](r))

    ws2 = wb.create_sheet('断面数据_长表')
    _write_head(ws2, spec['long_headers'], spec['long_widths'], style)
    for r in rows:
        for row in spec['long_rows'](r):
            ws2.append(row)

    ws3 = wb.create_sheet('断面数据_宽表')
    _write_head(ws3, spec['wide_headers'](rows), None, style)
    spec['wide_writer'](ws3, rows, style)

    extra = spec.get('extra')
    if extra:
        wsx = wb.create_sheet(extra['title'])
        _write_head(wsx, extra['headers'], extra['widths'], style)
        for r in rows:
            for row in extra['rows'](r):
                wsx.append(row)

    ws4 = wb.create_sheet('说明')
    for a, b in spec['notes'](ctx, args, rows):
        ws4.append([a, b])
    ws4.column_dimensions['A'].width = 13
    ws4.column_dimensions['B'].width = 120

    wb.save(xlsx)
    with open(csv_path, 'w', encoding='utf-8-sig', newline='') as f:
        f.write(spec['csv_header'])
        for r in rows:
            for line in spec['csv_rows'](r):
                f.write(line)
    return [xlsx, csv_path]


def plot_previews(outdir, rows, variant, args):
    """Preview PNGs; never fatal (same try/except contract as before)."""
    if not rows:
        return []
    spec = VARIANTS[variant]
    paths = []
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        picks = spec['preview_picks'](rows, args)
        for r in picks:
            if spec.get('preview_skip_empty') and not r['pts']:
                continue
            fig, ax = plt.subplots(figsize=spec['preview_figsize'], dpi=130)
            spec['preview_plot'](ax, r)
            fig.tight_layout()
            safe = (r['chain'] or ('idx%d' % r['idx'])).replace('+', '_')
            png = os.path.join(outdir, 'preview-%s.png' % safe)
            fig.savefig(png)
            plt.close(fig)
            paths.append(png)
            print('written:', png)
    except Exception as exc:          # pragma: no cover - plotting is optional
        print('plot skipped:', exc)
    return paths


def _preview_picks_gcd(rows, args):
    picks = [r for r in rows if args.preview_chainage and r['chain'] == args.preview_chainage]
    picks += [rows[0], rows[len(rows) // 2]]
    return picks


def _preview_plot_gcd(ax, r):
    ax.plot([p[0] for p in r['pts']], [p[1] for p in r['pts']], '-o', ms=5, lw=1.3,
            color='#1f77b4')
    for p in r['pts']:
        ax.annotate('%.2f' % p[1], (p[0], p[1]), textcoords='offset points', xytext=(0, 6),
                    ha='center', fontsize=7, color='#333')
    ax.set_xlabel('offset from left end (m)')
    ax.set_ylabel('elevation (m)')
    ax.grid(alpha=.3, ls='--')
    ax.set_title('section %s  (handle %s, L=%.1f m, %d points)'
                 % (r['chain'] or r['idx'], r['handle'], r['len'], len(r['pts'])), fontsize=10)


def _preview_picks_dmx(rows, args):
    return [rows[0], rows[len(rows) // 2], rows[-1]]


def _preview_plot_dmx(ax, r):
    cc = [(o, z) for o, z, s in r['pts'] if s.startswith('C:')]
    sp = [(o, z) for o, z, s in r['pts'] if s.startswith('P:')]
    if cc:
        ax.plot([p[0] for p in cc], [p[1] for p in cc], '-', lw=1.4, color='#1f77b4',
                label='contour profile')
    if sp:
        ax.plot([p[0] for p in sp], [p[1] for p in sp], 'o', ms=4, color='#d62728',
                label='spot height')
    ax.set_xlabel('offset from left end (m)')
    ax.set_ylabel('elevation (m)')
    ax.grid(alpha=.3, ls='--')
    ax.legend(fontsize=8, loc='best')
    ax.set_title('section %s   (handle %s, L=%.1f m)'
                 % (r['chain'] or r['idx'], r['handle'], r['len']), fontsize=10)


# ---------------------------------------------------------------------------
# the two routes as data: only the documented differences live here
# ---------------------------------------------------------------------------
VARIANTS = {
    'gcd': {
        'default_name': '断面数据-东总干渠-修正版',
        'description': '从 CAD 导出的 sections-src.json 生成断面数据工作簿（GCD 块插入点高程）',
        'tmp_help': 'CAD 导出 JSON 的临时根目录（默认 tempfile.gettempdir()，'
                    '输入读 <tmp-dir>\\cad-cooperate\\sections-src.json）',
        'name_help': '输出文件基名（默认 %s）' % '断面数据-东总干渠-修正版',
        'load': _load_gcd,
        'build': _build_gcd,
        'epilogue': _epilogue_gcd,
        'extra_args': [
            {'flag': '--preview-chainage', 'dest': 'preview_chainage',
             'default': DEFAULT_CHECK_CHAINAGE,
             'help': '预览图要额外强调的桩号（默认 %s；传空串则只出 首/中/末 三张）'
                     % DEFAULT_CHECK_CHAINAGE},
            {'flag': '--check-chainage', 'dest': 'check_chainage',
             'default': DEFAULT_CHECK_CHAINAGE,
             'help': '末尾逐点自检的桩号（默认 %s；传空串则跳过）' % DEFAULT_CHECK_CHAINAGE},
        ],
        'summary_headers': ['序号', '桩号', '桩号(m)', '实体句柄', '起点X', '起点Y', '终点X',
                            '终点Y', '长度(m)', '起点端', '高程点数', '起点距范围(m)',
                            '高程范围(m)', '最低高程', '渠底注记', '状态'],
        'summary_widths': [6, 10, 9, 10, 13, 14, 13, 14, 9, 8, 9, 14, 14, 10, 9, 8],
        'summary_row': _summary_gcd,
        'long_headers': ['序号', '桩号', '起点距(m)', '反向起点距(m)', '高程(m)', '块句柄',
                         '块属性height'],
        'long_widths': [6, 10, 12, 14, 10, 10, 13],
        'long_rows': _long_gcd,
        'wide_headers': lambda rows: ['序号'] + [(r['chain'] or ('#' + str(r['idx']))) for r in rows],
        'wide_writer': _write_wide_gcd,
        'notes': _notes_gcd,
        'csv_header': '序号,桩号,起点距(m),反向起点距(m),高程(m),块句柄\n',
        'csv_rows': _csv_gcd,
        'preview_picks': _preview_picks_gcd,
        'preview_plot': _preview_plot_gcd,
        'preview_figsize': (10, 3.4),
        'preview_skip_empty': True,
    },
    'dmx': {
        'default_name': '断面数据-东总干渠',
        'description': '从 CAD 导出的 geom-com.json + contours.json 生成断面数据工作簿',
        'tmp_help': 'CAD 导出 JSON 的临时根目录（默认 tempfile.gettempdir()，'
                    '输入读 <tmp-dir>\\cad-cooperate\\*.json）',
        'name_help': '输出文件基名（默认 %s，生成 <name>.xlsx/.csv）' % '断面数据-东总干渠',
        'load': _load_dmx,
        'build': _build_dmx,
        'epilogue': _epilogue_dmx,
        'extra_args': [],
        'summary_headers': ['序号', '桩号', '桩号(m)', '实体句柄', '起点X', '起点Y', '终点X',
                            '终点Y', '长度(m)', '起点端', '等高线交点', '高程点',
                            '起点距范围(m)', '高程范围(m)', '渠底注记', '状态'],
        'summary_widths': [6, 10, 9, 10, 13, 14, 13, 14, 9, 8, 9, 8, 14, 14, 9, 8],
        'summary_row': _summary_dmx,
        'long_headers': ['序号', '桩号', '起点距(m)', '高程(m)', '来源', '对象'],
        'long_widths': [6, 10, 12, 10, 9, 12],
        'long_rows': _long_dmx,
        'wide_headers': lambda rows: (['起点距(m)']
                                      + [(r['chain'] or ('#' + str(r['idx']))) for r in rows]),
        'wide_writer': _write_wide_dmx,
        'extra': {'title': '仅高程点',
                  'headers': ['序号', '桩号', '起点距(m)', '高程(m)', '点句柄'],
                  'widths': [6, 10, 12, 10, 11],
                  'rows': _extra_dmx},
        'notes': _notes_dmx,
        'csv_header': '序号,桩号,起点距(m),高程(m),来源,对象\n',
        'csv_rows': _csv_dmx,
        'preview_picks': _preview_picks_dmx,
        'preview_plot': _preview_plot_dmx,
        'preview_figsize': (10, 3.6),
        'preview_skip_empty': False,
    },
}


# ---------------------------------------------------------------------------
# CLI + top-level runner (identical contract for both routes)
# ---------------------------------------------------------------------------
def build_arg_parser(variant):
    spec = VARIANTS[variant]
    ap = argparse.ArgumentParser(description=spec['description'])
    ap.add_argument('--tmp-dir', dest='tmp_dir', default=None, help=spec['tmp_help'])
    ap.add_argument('--outdir', default=None, help='输出目录（默认 <repo>\\output）')
    ap.add_argument('--name', default=None, help=spec['name_help'])
    for extra in spec['extra_args']:
        kwargs = dict((k, v) for k, v in extra.items() if k != 'flag')
        ap.add_argument(extra['flag'], **kwargs)
    return ap


def run(variant, argv=None):
    """Entry-point body: parse -> load -> build -> write -> preview -> epilogue."""
    timer = Timer()
    spec = VARIANTS[variant]
    args = build_arg_parser(variant).parse_args(argv)
    tmp = args.tmp_dir or tempfile.gettempdir()
    ctx = spec['load'](tmp)
    timer.mark('load')
    rows = spec['build'](ctx, timer)

    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    outdir = args.outdir or os.path.join(repo, 'output')
    name = args.name or spec['default_name']
    for path in write_book(outdir, name, rows, variant, ctx, args):
        print('written:', path)
    timer.mark('excel')

    plot_previews(outdir, rows, variant, args)
    timer.mark('preview')

    spec['epilogue'](ctx, args, rows)
    timer.report()
    return 0


def main(variant, argv=None):
    """入口：BuildError -> stderr 一行 `error <CODE>: <msg>` + 退出码 2（不裸 traceback）。"""
    try:
        return run(variant, argv)
    except BuildError as exc:
        sys.stderr.write('error %s: %s\n' % (exc.code, exc.message))
        return 2
