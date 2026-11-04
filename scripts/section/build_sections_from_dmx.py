# Build dense cross-section profiles for the 东总干渠 drawing.
#
# Inputs (read-only, extracted from the live CAD session):
#   <tmp>\zwcad-cooperate\geom-com.json    section lines (dmx), spot heights (ZDH points), labels (KZD)
#   <tmp>\zwcad-cooperate\contours.json    contours (DGX polylines with their elevation property)
#   <tmp> 默认 = tempfile.gettempdir()，可用 --tmp-dir 覆盖（离线复现/测试用）
#
# Output (默认 <repo>\output，可用 --outdir/--name 覆盖):
#   <outdir>\<name>.xlsx                     5 sheets（默认 name=断面数据-东总干渠）
#   <outdir>\<name>.csv
#   <outdir>\preview-<桩号>.png              3 张代表性断面图
#
# Method
#   * 断面线 = dmx lines (304);  桩号 = KZD text "\d+\+\d+" matched within 8 m (1:1)
#   * 高程点 = ZDH POINT with Z != 0, projected onto the line (tolerance 0.5 m)
#   * 地形剖面 = intersections of the section line with DGX contour polylines
#                (each contour has a uniform elevation property, 1 m interval)
#   * 起点距 = distance from the LEFT end, left = left-hand side facing downstream
#                (downstream = increasing chainage); 0 = left bank
#   * 长表按起点距升序，来源列区分"等高线"与"高程点"
#
# 失败约定：输入缺失/坏 JSON/缺键/缺 openpyxl -> stderr 一行 `error <CODE>: <msg>` + 退出码 2
# （原先是裸 traceback）；成功返回 0。
import argparse
import datetime
import json
import math
import os
import re
import sys
import tempfile

POINT_TOL = 0.50      # spot height considered "on" the section line (m)
DEDUP_D = 0.05        # duplicate offset threshold (m)
MIN_PTS = 3
LABEL_R = 8.0

DEFAULT_NAME = '断面数据-东总干渠'


class BuildError(Exception):
    """带机器可读 code 的失败；main() 统一转成 stderr 一行 + 退出码 2。"""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def _load_json(path, what):
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


def _need(obj, key, what, where=''):
    if not isinstance(obj, dict) or key not in obj:
        raise BuildError('BAD_INPUT', '%s%s 缺键 %r' % (what, where, key))
    return obj[key]


def _validate_geom(g):
    """geom-com.json：必须有 doc / entities[]，且每个实体至少有图层键 'l'。"""
    _need(g, 'doc', 'geom-com.json')
    ents = _need(g, 'entities', 'geom-com.json')
    if not isinstance(ents, list):
        raise BuildError('BAD_INPUT', 'geom-com.json: entities 必须是数组')
    for i, e in enumerate(ents):
        if not isinstance(e, dict) or 'l' not in e:
            raise BuildError('BAD_INPUT', 'geom-com.json: entities[%d] 缺键 "l"（图层）' % i)
    return g


def _validate_contours(c):
    """contours.json：必须有 contours[]，每条等高线有 z / h / v（顶点数组）。"""
    cs = _need(c, 'contours', 'contours.json')
    if not isinstance(cs, list):
        raise BuildError('BAD_INPUT', 'contours.json: contours 必须是数组')
    for i, ct in enumerate(cs):
        for key in ('z', 'h', 'v'):
            _need(ct, key, 'contours.json', '[%d]' % i)
        if not isinstance(ct['v'], list) or len(ct['v']) < 2:
            raise BuildError('BAD_INPUT',
                             'contours.json: contours[%d].v 至少需要 2 个顶点' % i)
    return c


def _openpyxl():
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


def seg_dist_t(px, py, ax, ay, bx, by):
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
    m = re.match(r'^(\d+)\+(\d+(?:\.\d+)?)$', s)
    return float(m.group(1)) * 1000 + float(m.group(2)) if m else None


def build_arg_parser():
    ap = argparse.ArgumentParser(
        description='从 CAD 导出的 geom-com.json + contours.json 生成断面数据工作簿')
    ap.add_argument('--tmp-dir', dest='tmp_dir', default=None,
                    help='CAD 导出 JSON 的临时根目录（默认 tempfile.gettempdir()，'
                         '输入读 <tmp-dir>\\zwcad-cooperate\\*.json）')
    ap.add_argument('--outdir', default=None,
                    help='输出目录（默认 <repo>\\output）')
    ap.add_argument('--name', default=None,
                    help='输出文件基名（默认 %s，生成 <name>.xlsx/.csv）' % DEFAULT_NAME)
    return ap


def _run(argv=None):
    args = build_arg_parser().parse_args(argv)
    tmp = args.tmp_dir or tempfile.gettempdir()
    gj = os.path.join(tmp, 'zwcad-cooperate', 'geom-com.json')
    cj = os.path.join(tmp, 'zwcad-cooperate', 'contours.json')
    for f in (gj, cj):
        if not os.path.exists(f):
            raise BuildError('NO_INPUT', 'missing input: %s' % f)
    g = _validate_geom(_load_json(gj, 'geom-com.json'))
    c = _validate_contours(_load_json(cj, 'contours.json'))
    ents = g['entities']
    print('drawing:', g['doc'])
    print('entities:', len(ents), '| contours:', c['count'] if 'count' in c else len(c['contours']))

    secs = [e for e in ents if e['l'] == 'dmx' and 'p' in e and 'p2' in e]
    spots = [e for e in ents if e.get('o') == 'AcDbPoint' and e['l'] == 'ZDH' and 'p' in e]
    kzd = [e for e in ents if e['l'] == 'KZD' and 'x' in e and 'p' in e]
    chain_labels = [t for t in kzd if re.match(r'^\d+\+\d+(?:\.\d+)?$', t['x'])]
    elev_labels = [t for t in kzd if re.match(r'^\d+\.\d+$', t['x'])]
    print('sections:', len(secs), '| spot points:', len(spots),
          '| chainage labels:', len(chain_labels), '| elevation labels:', len(elev_labels))
    if not secs:
        raise BuildError('BAD_INPUT', 'geom-com.json: 没有图层 dmx 的断面线实体（检查导出/图层名）')
    if not chain_labels:
        raise BuildError('BAD_INPUT', 'geom-com.json: 没有 KZD 桩号文字（如 1+324），无法排序断面')

    # contour segments with bbox
    segs = []
    for ct in c['contours']:
        z = ct['z']
        h = ct['h']
        vs = ct['v']
        for a, b in zip(vs, vs[1:]):
            segs.append((a[0], a[1], b[0], b[1], z, h,
                         min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])))
    print('contour segments:', len(segs))

    def associate(labels):
        out = {}
        for t in labels:
            best = (1e9, None)
            for e in secs:
                dd, _ = seg_dist_t(t['p'][0], t['p'][1], e['p'][0], e['p'][1], e['p2'][0], e['p2'][1])
                if dd < best[0]:
                    best = (dd, e['h'])
            if best[0] <= LABEL_R:
                out.setdefault(best[1], []).append((best[0], t['x']))
        return dict((h, sorted(v)[0][1]) for h, v in out.items())

    chain_of = associate(chain_labels)
    elev_of = associate(elev_labels)
    if not chain_of:
        raise BuildError('BAD_INPUT',
                         'geom-com.json: 没有桩号文字能匹配到断面线（%.0f m 内），无法定下游方向'
                         % LABEL_R)

    # downstream direction
    mids = [((e['p'][0] + e['p2'][0]) / 2, (e['p'][1] + e['p2'][1]) / 2) for e in secs]
    wc = sorted((chainage_to_m(chain_of[e['h']]), i) for i, e in enumerate(secs) if e['h'] in chain_of)
    p0 = mids[wc[0][1]]
    p1 = mids[wc[-1][1]]
    ux, uy = p1[0] - p0[0], p1[1] - p0[1]
    n = math.hypot(ux, uy) or 1.0
    ux, uy = ux / n, uy / n
    lx, ly = -uy, ux

    rows = []
    for e in secs:
        ax, ay = e['p'][0], e['p'][1]
        bx, by = e['p2'][0], e['p2'][1]
        L = math.hypot(bx - ax, by - ay)
        if L == 0:
            continue
        dx, dy = (bx - ax) / L, (by - ay) / L
        left_is_a = (dx * lx + dy * ly) >= 0
        sbb = (min(ax, bx), min(ay, by), max(ax, bx), max(ay, by))

        # 1) contour intersections
        hits = []
        for (x1, y1, x2, y2, z, h, sx1, sy1, sx2, sy2) in segs:
            if sx2 < sbb[0] or sx1 > sbb[2] or sy2 < sbb[1] or sy1 > sbb[3]:
                continue
            p = intersect((ax, ay), (bx, by), (x1, y1), (x2, y2))
            if p is None:
                continue
            off_a = math.hypot(p[0] - ax, p[1] - ay)
            off = off_a if left_is_a else (L - off_a)
            hits.append((off, z, 'C:' + h))
        # 2) spot heights on the line
        for q in spots:
            z = q['p'][2] if len(q['p']) > 2 else 0.0
            if not z:
                continue
            dd, tc = seg_dist_t(q['p'][0], q['p'][1], ax, ay, bx, by)
            if dd > POINT_TOL:
                continue
            off = (tc * L) if left_is_a else (L - tc * L)
            hits.append((off, z, 'P:' + q['h']))
        hits.sort(key=lambda r: r[0])

        merged = []
        for off, z, src in hits:
            if merged and abs(merged[-1][0] - off) < DEDUP_D:
                # prefer a spot height over a contour crossing at the same offset
                if src.startswith('P:') and merged[-1][2].startswith('C:'):
                    merged[-1] = (off, z, src)
                continue
            merged.append((off, z, src))

        ch = chain_of.get(e['h'], '')
        rows.append({
            'handle': e['h'], 'chain': ch,
            'chain_m': chainage_to_m(ch) if ch else None,
            'a': (ax, ay), 'b': (bx, by), 'len': L, 'left_is_a': left_is_a,
            'mid': ((ax + bx) / 2, (ay + by) / 2),
            'pts': merged,
            'n_contour': sum(1 for r in merged if r[2].startswith('C:')),
            'n_spot': sum(1 for r in merged if r[2].startswith('P:')),
            'elev_note': elev_of.get(e['h'], ''),
        })

    rows.sort(key=lambda r: (r['chain_m'] if r['chain_m'] is not None else 1e12,
                             r['mid'][0] * ux + r['mid'][1] * uy))
    for i, r in enumerate(rows, 1):
        r['idx'] = i
    print('sections built:', len(rows))

    # ---------------- Excel（openpyxl 延迟导入：缺库 -> NO_OPENPYXL 退出码 2）
    Workbook, Font, PatternFill, Alignment, get_column_letter = _openpyxl()
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    outdir = args.outdir or os.path.join(repo, 'output')
    os.makedirs(outdir, exist_ok=True)
    name = args.name or DEFAULT_NAME
    xlsx = os.path.join(outdir, name + '.xlsx')
    csv = os.path.join(outdir, name + '.csv')

    wb = Workbook()
    bold = Font(bold=True)
    fill = PatternFill('solid', fgColor='DDEBF7')

    def head(ws, headers, widths=None):
        for i, h in enumerate(headers, 1):
            cell = ws.cell(row=1, column=i, value=h)
            cell.font = bold
            cell.fill = fill
            cell.alignment = Alignment(horizontal='center')
        ws.freeze_panes = 'A2'
        for i in range(1, len(headers) + 1):
            ws.column_dimensions[get_column_letter(i)].width = (widths[i - 1] if widths else 13)

    ws = wb.active
    ws.title = '断面汇总'
    head(ws, ['序号', '桩号', '桩号(m)', '实体句柄', '起点X', '起点Y', '终点X', '终点Y', '长度(m)',
              '起点端', '等高线交点', '高程点', '起点距范围(m)', '高程范围(m)', '渠底注记', '状态'],
         [6, 10, 9, 10, 13, 14, 13, 14, 9, 8, 9, 8, 14, 14, 9, 8])
    for r in rows:
        pts = r['pts']
        ws.append([r['idx'], r['chain'], round(r['chain_m'], 2) if r['chain_m'] is not None else '',
                   r['handle'], round(r['a'][0], 3), round(r['a'][1], 3),
                   round(r['b'][0], 3), round(r['b'][1], 3), round(r['len'], 2),
                   'A端' if r['left_is_a'] else 'B端', r['n_contour'], r['n_spot'],
                   ('%.2f~%.2f' % (pts[0][0], pts[-1][0])) if pts else '',
                   ('%.2f~%.2f' % (min(p[1] for p in pts), max(p[1] for p in pts))) if pts else '',
                   r['elev_note'], 'OK' if len(pts) >= MIN_PTS else '点数不足'])

    ws2 = wb.create_sheet('断面数据_长表')
    head(ws2, ['序号', '桩号', '起点距(m)', '高程(m)', '来源', '对象'], [6, 10, 12, 10, 9, 12])
    for r in rows:
        for off, z, src in r['pts']:
            ws2.append([r['idx'], r['chain'], round(off, 3), round(z, 3),
                        '等高线' if src.startswith('C:') else '高程点', src[2:]])

    ws3 = wb.create_sheet('断面数据_宽表')
    head(ws3, ['起点距(m)'] + [(r['chain'] or ('#' + str(r['idx']))) for r in rows])
    maxn = max((len(r['pts']) for r in rows), default=0)
    for k in range(maxn):
        ws3.append([r['pts'][k][1] if k < len(r['pts']) else None for r in rows])

    ws4 = wb.create_sheet('仅高程点')
    head(ws4, ['序号', '桩号', '起点距(m)', '高程(m)', '点句柄'], [6, 10, 12, 10, 11])
    for r in rows:
        for off, z, src in r['pts']:
            if src.startswith('P:'):
                ws4.append([r['idx'], r['chain'], round(off, 3), round(z, 3), src[2:]])

    ws5 = wb.create_sheet('说明')
    lens = sorted(r['len'] for r in rows)
    ntot = sum(len(r['pts']) for r in rows)
    nc = sum(r['n_contour'] for r in rows)
    nsp = sum(r['n_spot'] for r in rows)
    notes = [
        ('项目', '说明'),
        ('数据来源', '图纸：' + g['doc'] + '（只读读取，未修改图纸）'),
        ('断面线', '图层 dmx，共 %d 条；长 %.1f~%.1f m，中位 %.1f m' % (len(rows), lens[0], lens[-1], lens[len(lens) // 2])),
        ('断面编号', '图层 KZD 文字桩号（如 1+324），8 m 内最近匹配：%d/%d 条已匹配' % (len(chain_of), len(secs))),
        ('地形来源①', 'DGX 图层等高线（%d 条折线，等高距 1 m，高程 18~58 m）：与断面线求交，得 %d 个剖面高程点' % (len(c['contours']), nc)),
        ('地形来源②', 'ZDH 图层高程点（POINT 的 Z 值，仅 Z≠0）：投影到断面线（容差 %.2f m），得 %d 个点' % (POINT_TOL, nsp)),
        ('合并规则', '按起点距排序；同一位置（<%.2f m）重复时优先保留高程点' % DEDUP_D),
        ('起点距', '从断面线"左端"起算：0=左岸，面朝下游（桩号增大方向）的左手侧为左端'),
        ('渠底注记', 'KZD 图层每断面的高程注记（如 22.84），作参考复核列，未参与剖面'),
        ('单位', '坐标与高程均为图纸单位（该图 insunits=0 未定义，实测为米）'),
        ('生成时间', datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')),
        ('合计', '断面 %d 条，剖面点 %d 个（等高线 %d + 高程点 %d）' % (len(rows), ntot, nc, nsp)),
    ]
    for a, b in notes:
        ws5.append([a, b])
    ws5.column_dimensions['A'].width = 13
    ws5.column_dimensions['B'].width = 120

    wb.save(xlsx)
    with open(csv, 'w', encoding='utf-8-sig', newline='') as f:
        f.write('序号,桩号,起点距(m),高程(m),来源,对象\n')
        for r in rows:
            for off, z, src in r['pts']:
                f.write('%d,%s,%.3f,%.3f,%s,%s\n' % (r['idx'], r['chain'], off, z,
                                                     'contour' if src.startswith('C:') else 'spot', src[2:]))
    print('written:', xlsx)
    print('written:', csv)

    # ---------------- preview plots for three representative sections
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt

        picks = [rows[0], rows[len(rows) // 2], rows[-1]]
        for r in picks:
            fig, ax = plt.subplots(figsize=(10, 3.6), dpi=130)
            cc = [(o, z) for o, z, s in r['pts'] if s.startswith('C:')]
            sp = [(o, z) for o, z, s in r['pts'] if s.startswith('P:')]
            if cc:
                ax.plot([p[0] for p in cc], [p[1] for p in cc], '-', lw=1.4, color='#1f77b4', label='contour profile')
            if sp:
                ax.plot([p[0] for p in sp], [p[1] for p in sp], 'o', ms=4, color='#d62728', label='spot height')
            ax.set_xlabel('offset from left end (m)')
            ax.set_ylabel('elevation (m)')
            ax.grid(alpha=.3, ls='--')
            ax.legend(fontsize=8, loc='best')
            ax.set_title('section %s   (handle %s, L=%.1f m)' % (r['chain'] or r['idx'], r['handle'], r['len']), fontsize=10)
            fig.tight_layout()
            safe = (r['chain'] or ('idx%d' % r['idx'])).replace('+', '_')
            png = os.path.join(outdir, 'preview-%s.png' % safe)
            fig.savefig(png)
            plt.close(fig)
            print('written:', png)
    except Exception as exc:
        print('plot skipped:', exc)

    print('sections=%d points=%d contour_hits=%d spot=%d' % (len(rows), ntot, nc, nsp))
    return 0


def main(argv=None):
    """入口：BuildError -> stderr 一行 `error <CODE>: <msg>` + 退出码 2（不裸 traceback）。"""
    try:
        return _run(argv)
    except BuildError as exc:
        sys.stderr.write('error %s: %s\n' % (exc.code, exc.message))
        return 2


if __name__ == '__main__':
    sys.exit(main())
