# Build the cross-section workbook from the CORRECT source: GCD blocks whose insertion Z is the
# ground elevation on the section line (the ZDH layer holds unrelated terrain spot heights).
#
# Input : <tmp>\zwcad-cooperate\sections-src.json  (tmp\extract-sections-src.ps1)
#         <tmp> 默认 = tempfile.gettempdir()，可用 --tmp-dir 覆盖（离线复现/测试用）
# Output: <outdir>\<name>.xlsx (+ csv, preview png)（默认 <repo>\output，
#         name 默认 断面数据-东总干渠-修正版，可用 --outdir/--name 覆盖）
#
# Conventions
#   * 断面线 = layer dmx (304 lines);  桩号 = KZD text "\d+\+\d+" (8 m match)
#   * 高程点 = layer GCD block gc200 (2,938), insertion Z = elevation, also in attribute 'height'
#   * 起点距 = distance from the LEFT end, left = left-hand side facing downstream
#              (downstream = increasing chainage); 反向起点距 column provided as well
#   * 渠底注记 = KZD text "\d+\.\d+" (one per section, e.g. 21.84 == the gc257 block Z)
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

BLOCK_TOL = 0.50     # block-to-line tolerance (m); in practice the blocks sit exactly on the line
LABEL_R = 8.0
DEDUP_D = 0.05
MIN_PTS = 3

DEFAULT_NAME = '断面数据-东总干渠-修正版'
DEFAULT_CHECK_CHAINAGE = '16+475'


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


def _validate_sections_src(d):
    """sections-src.json：doc / lines[] / blocks[] / labels[]，逐项校验必需键。"""
    _need(d, 'doc', 'sections-src.json')
    lines = _need(d, 'lines', 'sections-src.json')
    blocks = _need(d, 'blocks', 'sections-src.json')
    labels = _need(d, 'labels', 'sections-src.json')
    for name, arr in (('lines', lines), ('blocks', blocks), ('labels', labels)):
        if not isinstance(arr, list):
            raise BuildError('BAD_INPUT', 'sections-src.json: %s 必须是数组' % name)
    for i, e in enumerate(lines):
        for key in ('h', 'ax', 'ay', 'bx', 'by'):
            _need(e, key, 'sections-src.json: lines', '[%d]' % i)
    for i, b in enumerate(blocks):
        for key in ('x', 'y', 'z', 'h'):
            _need(b, key, 'sections-src.json: blocks', '[%d]' % i)
    for i, t in enumerate(labels):
        for key in ('x', 'y', 'text'):
            _need(t, key, 'sections-src.json: labels', '[%d]' % i)
    return d


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


def chainage_to_m(s):
    m = re.match(r'^(\d+)\+(\d+(?:\.\d+)?)$', s)
    return float(m.group(1)) * 1000 + float(m.group(2)) if m else None


def build_arg_parser():
    ap = argparse.ArgumentParser(
        description='从 CAD 导出的 sections-src.json 生成断面数据工作簿（GCD 块插入点高程）')
    ap.add_argument('--tmp-dir', dest='tmp_dir', default=None,
                    help='CAD 导出 JSON 的临时根目录（默认 tempfile.gettempdir()，'
                         '输入读 <tmp-dir>\\zwcad-cooperate\\sections-src.json）')
    ap.add_argument('--outdir', default=None, help='输出目录（默认 <repo>\\output）')
    ap.add_argument('--name', default=None,
                    help='输出文件基名（默认 %s）' % DEFAULT_NAME)
    ap.add_argument('--preview-chainage', dest='preview_chainage', default=DEFAULT_CHECK_CHAINAGE,
                    help='预览图要额外强调的桩号（默认 %s；传空串则只出 首/中/末 三张）'
                         % DEFAULT_CHECK_CHAINAGE)
    ap.add_argument('--check-chainage', dest='check_chainage', default=DEFAULT_CHECK_CHAINAGE,
                    help='末尾逐点自检的桩号（默认 %s；传空串则跳过）' % DEFAULT_CHECK_CHAINAGE)
    return ap


def _run(argv=None):
    args = build_arg_parser().parse_args(argv)
    tmp = args.tmp_dir or tempfile.gettempdir()
    src = os.path.join(tmp, 'zwcad-cooperate', 'sections-src.json')
    if not os.path.exists(src):
        raise BuildError('NO_INPUT', 'missing input: %s' % src)
    d = _validate_sections_src(_load_json(src, 'sections-src.json'))
    lines = d['lines']
    blocks = d['blocks']
    labels = d['labels']
    print('drawing:', d['doc'])
    print('sections:', len(lines), '| elevation blocks:', len(blocks), '| KZD labels:', len(labels))
    if not lines:
        raise BuildError('BAD_INPUT', 'sections-src.json: lines 为空（没有断面线实体）')
    if not blocks:
        raise BuildError('BAD_INPUT', 'sections-src.json: blocks 为空（没有高程块）')

    chain_labels = [t for t in labels if re.match(r'^\d+\+\d+(?:\.\d+)?$', t['text'])]
    elev_labels = [t for t in labels if re.match(r'^\d+\.\d+$', t['text'])]
    print('chainage labels:', len(chain_labels), '| elevation labels:', len(elev_labels))

    # ---- associate labels to section lines
    def associate(lab, key='text'):
        out = {}
        for t in lab:
            best = (1e9, None)
            for e in lines:
                dd, _ = seg_dist_t(t['x'], t['y'], e['ax'], e['ay'], e['bx'], e['by'])
                if dd < best[0]:
                    best = (dd, e['h'])
            if best[0] <= LABEL_R:
                out.setdefault(best[1], []).append((best[0], t[key]))
        return dict((h, sorted(v)[0][1]) for h, v in out.items())

    chain_of = associate(chain_labels)
    elev_of = associate(elev_labels)
    print('sections with chainage:', len(chain_of), '| with elevation note:', len(elev_of))
    if not chain_of:
        raise BuildError('BAD_INPUT',
                         'sections-src.json: 没有桩号文字能匹配到断面线（%.0f m 内），无法定下游方向'
                         % LABEL_R)

    # ---- downstream direction from chainage order
    mids = [((e['ax'] + e['bx']) / 2, (e['ay'] + e['by']) / 2) for e in lines]
    wc = sorted((chainage_to_m(chain_of[e['h']]), i) for i, e in enumerate(lines) if e['h'] in chain_of)
    p0 = mids[wc[0][1]]
    p1 = mids[wc[-1][1]]
    ux, uy = p1[0] - p0[0], p1[1] - p0[1]
    n = math.hypot(ux, uy) or 1.0
    ux, uy = ux / n, uy / n
    lx, ly = -uy, ux

    # ---- assign blocks to lines (each block to its nearest line within tolerance)
    by_line = dict((e['h'], []) for e in lines)
    unassigned = 0
    for b in blocks:
        best = (1e9, None, 0.0)
        for e in lines:
            dd, tc = seg_dist_t(b['x'], b['y'], e['ax'], e['ay'], e['bx'], e['by'])
            if dd < best[0]:
                best = (dd, e['h'], tc)
        if best[0] <= BLOCK_TOL:
            by_line[best[1]].append((b, best[2]))
        else:
            unassigned += 1
    print('blocks assigned:', len(blocks) - unassigned, '| unassigned:', unassigned)

    rows = []
    for e in lines:
        ax, ay, bx, by = e['ax'], e['ay'], e['bx'], e['by']
        L = math.hypot(bx - ax, by - ay)
        if L == 0:
            continue
        dx, dy = (bx - ax) / L, (by - ay) / L
        left_is_a = (dx * lx + dy * ly) >= 0
        pts = []
        for b, tc in by_line[e['h']]:
            off = (tc * L) if left_is_a else (L - tc * L)
            pts.append((off, b['z'], b['h'], b.get('attr')))
        pts.sort(key=lambda r: r[0])
        dedup = []
        for off, z, h, at in pts:
            if dedup and abs(dedup[-1][0] - off) < DEDUP_D:
                continue
            dedup.append((off, z, h, at))
        ch = chain_of.get(e['h'], '')
        rows.append({
            'handle': e['h'], 'chain': ch,
            'chain_m': chainage_to_m(ch) if ch else None,
            'a': (ax, ay), 'b': (bx, by), 'len': L, 'left_is_a': left_is_a,
            'mid': ((ax + bx) / 2, (ay + by) / 2),
            'pts': dedup, 'elev_note': elev_of.get(e['h'], ''),
        })

    rows.sort(key=lambda r: (r['chain_m'] if r['chain_m'] is not None else 1e12,
                             r['mid'][0] * ux + r['mid'][1] * uy))
    for i, r in enumerate(rows, 1):
        r['idx'] = i

    usable = [r for r in rows if len(r['pts']) >= MIN_PTS]
    print('sections: %d | usable(>=%d pts): %d' % (len(rows), MIN_PTS, len(usable)))

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
            c = ws.cell(row=1, column=i, value=h)
            c.font = bold
            c.fill = fill
            c.alignment = Alignment(horizontal='center')
        ws.freeze_panes = 'A2'
        for i in range(1, len(headers) + 1):
            ws.column_dimensions[get_column_letter(i)].width = (widths[i - 1] if widths else 13)

    ws = wb.active
    ws.title = '断面汇总'
    head(ws, ['序号', '桩号', '桩号(m)', '实体句柄', '起点X', '起点Y', '终点X', '终点Y', '长度(m)',
              '起点端', '高程点数', '起点距范围(m)', '高程范围(m)', '最低高程', '渠底注记', '状态'],
         [6, 10, 9, 10, 13, 14, 13, 14, 9, 8, 9, 14, 14, 10, 9, 8])
    for r in rows:
        pts = r['pts']
        ws.append([r['idx'], r['chain'], round(r['chain_m'], 2) if r['chain_m'] is not None else '',
                   r['handle'], round(r['a'][0], 3), round(r['a'][1], 3),
                   round(r['b'][0], 3), round(r['b'][1], 3), round(r['len'], 2),
                   'A端' if r['left_is_a'] else 'B端', len(pts),
                   ('%.2f~%.2f' % (pts[0][0], pts[-1][0])) if pts else '',
                   ('%.2f~%.2f' % (min(p[1] for p in pts), max(p[1] for p in pts))) if pts else '',
                   min((p[1] for p in pts), default=''), r['elev_note'],
                   'OK' if len(pts) >= MIN_PTS else '点数不足'])

    ws2 = wb.create_sheet('断面数据_长表')
    head(ws2, ['序号', '桩号', '起点距(m)', '反向起点距(m)', '高程(m)', '块句柄', '块属性height'],
         [6, 10, 12, 14, 10, 10, 13])
    for r in rows:
        for off, z, h, at in r['pts']:
            ws2.append([r['idx'], r['chain'], round(off, 3), round(r['len'] - off, 3),
                        round(z, 3), h, at])

    ws3 = wb.create_sheet('断面数据_宽表')
    head(ws3, ['序号'] + [(r['chain'] or ('#' + str(r['idx']))) for r in rows])
    ws3.cell(row=2, column=1, value='起点距(m)').font = bold
    maxn = max((len(r['pts']) for r in rows), default=0)
    for k in range(maxn):
        for r in rows:
            if k < len(r['pts']):
                off = r['pts'][k][0]
                ws3.cell(row=3 + k, column=1 + r['idx'], value=round(off, 2))
    # a second block with elevations
    base = 3 + maxn + 2
    ws3.cell(row=base, column=1, value='高程(m)').font = bold
    for k in range(maxn):
        for r in rows:
            if k < len(r['pts']):
                ws3.cell(row=base + 1 + k, column=1 + r['idx'], value=r['pts'][k][1])

    ws4 = wb.create_sheet('说明')
    lens = sorted(r['len'] for r in rows)
    total = sum(len(r['pts']) for r in rows)
    notes = [
        ('项目', '说明'),
        ('数据来源', '图纸：' + d['doc'] + '（只读读取，未修改图纸）'),
        ('断面线', '图层 dmx，共 %d 条；长 %.1f~%.1f m，中位 %.1f m' % (len(rows), lens[0], lens[-1], lens[len(lens) // 2])),
        ('断面编号', '图层 KZD 文字桩号（如 %s），%.0f m 内最近匹配：%d/%d'
         % (args.preview_chainage or DEFAULT_CHECK_CHAINAGE, LABEL_R, len(chain_of), len(lines))),
        ('高程点', '图层 GCD 的 gc200 块，共 %d 个（已分配 %d）；高程 = 块插入点 Z（同名属性 height 同值）' % (len(blocks), len(blocks) - unassigned)),
        ('起点距', '从断面线"左端"起算（面朝下游、桩号增大方向的左手侧）；长表另给"反向起点距"便于对照'),
        ('渠底注记', 'KZD 图层每断面一个高程注记（如 21.84），与 KZD 的 gc257 块 Z 值一致，作参考列'),
        ('单位', '坐标与高程均为图纸单位（insunits=0 未定义，实测为米）'),
        ('合计', '断面 %d 条，高程点 %d 个' % (len(rows), total)),
        ('生成时间', datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')),
    ]
    for a, b in notes:
        ws4.append([a, b])
    ws4.column_dimensions['A'].width = 13
    ws4.column_dimensions['B'].width = 120

    wb.save(xlsx)
    with open(csv, 'w', encoding='utf-8-sig', newline='') as f:
        f.write('序号,桩号,起点距(m),反向起点距(m),高程(m),块句柄\n')
        for r in rows:
            for off, z, h, at in r['pts']:
                f.write('%d,%s,%.3f,%.3f,%.3f,%s\n' % (r['idx'], r['chain'], off, r['len'] - off, z, h))
    print('written:', xlsx)
    print('written:', csv)

    # preview: --preview-chainage 指定的断面 + 两个代表性断面
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        picks = [r for r in rows if args.preview_chainage and r['chain'] == args.preview_chainage]
        picks += [rows[0], rows[len(rows) // 2]]
        for r in picks:
            if not r['pts']:
                continue
            fig, ax = plt.subplots(figsize=(10, 3.4), dpi=130)
            ax.plot([p[0] for p in r['pts']], [p[1] for p in r['pts']], '-o', ms=5, lw=1.3, color='#1f77b4')
            for p in r['pts']:
                ax.annotate('%.2f' % p[1], (p[0], p[1]), textcoords='offset points', xytext=(0, 6),
                            ha='center', fontsize=7, color='#333')
            ax.set_xlabel('offset from left end (m)')
            ax.set_ylabel('elevation (m)')
            ax.grid(alpha=.3, ls='--')
            ax.set_title('section %s  (handle %s, L=%.1f m, %d points)' % (r['chain'] or r['idx'], r['handle'], r['len'], len(r['pts'])), fontsize=10)
            fig.tight_layout()
            safe = (r['chain'] or ('idx%d' % r['idx'])).replace('+', '_')
            png = os.path.join(outdir, 'preview-%s.png' % safe)
            fig.savefig(png)
            plt.close(fig)
            print('written:', png)
    except Exception as exc:
        print('plot skipped:', exc)

    # quick self-check of the section the user picked（--check-chainage，默认 16+475）
    if args.check_chainage:
        for r in rows:
            if r['chain'] == args.check_chainage:
                print('\n%s check: handle=%s A=(%.3f,%.3f) B=(%.3f,%.3f) L=%.3f npts=%d' % (
                    args.check_chainage, r['handle'], r['a'][0], r['a'][1], r['b'][0], r['b'][1],
                    r['len'], len(r['pts'])))
                for off, z, h, at in r['pts']:
                    print('   起点距 %7.2f m   高程 %7.2f m   (%s)' % (off, z, h))
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
