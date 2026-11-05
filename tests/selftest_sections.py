#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""selftest_sections.py -- 断面构建双脚本（scripts/section/section_lib.py）纯离线自测。

零 COM、零 CAD、零真实图纸数据：夹具在脚本内合成到 ``tmp/selftest-sections/``
（``.gitignore`` 只忽略仓库 ``/tmp/``，故夹具永不入库），每条断言都对着可手算的
几何值。

覆盖：
  1  几何工具：seg_dist_t（垂足/端点外/零长）· intersect（相交/平行/无交点）·
     chainage_to_m 与桩号/高程注记正则
  2  去重与过滤：DEDUP_D 聚类、prefer_spot 替换规则、MIN_PTS 可用断面过滤
  3  网格空间索引：最近段（含等距时取低序号的原语义）、超距返回哨兵、
     bbox 查询是暴力 bbox 过滤的超集、半径查询完备性、自适应桶尺寸
  4  CLI 契约：两入口参数/默认值（--tmp-dir/--outdir/--name；gcd 额外
     --preview-chainage/--check-chainage）与 DEFAULT_NAME 常量
  5  gcd 合成图正例：退出码 0、stdout 关键行、5 张表（汇总/长表/宽表/说明）
     逐格值、CSV、产物清单、确定性（两次运行逐格相同）、--check-chainage 自检块
  6  dmx 合成图正例：退出码 0、stdout 关键行、5 张表（多"仅高程点"）、
     等高线交点/高程点计数、"同一位置优先高程点"在产物里的体现
  7  负例：NO_INPUT / BAD_INPUT（坏 JSON、空 lines、等高线顶点不足、无桩号文字）
     -> stderr 一行 + 退出码 2 + **不产出任何文件**；NO_OPENPYXL（桩模块）
     -> 退出码 2 且不产生产物

运行：python tests\\selftest_sections.py
退出码：0 全绿；1 有 FAIL
"""

import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# CI runners default the console code page to cp1252 while fixture details below are Chinese:
# force UTF-8 with replacement instead of dying with UnicodeEncodeError.
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

SEC = os.path.join(ROOT, 'scripts', 'section')
GCD = os.path.join(SEC, 'build_sections_gcd.py')
DMX = os.path.join(SEC, 'build_sections_from_dmx.py')
TMP = os.path.join(ROOT, 'tmp', 'selftest-sections')
PY = sys.executable or 'python'

sys.path.insert(0, SEC)
import section_lib as lib  # noqa: E402

RESULTS = []

# ---- synthetic drawing: a straight channel running north (chainage grows with +y);
# every section is perpendicular, A=(+40, y) is the left end (offset 0) and
# B=(-40, y) the right end (offset 80 m) -> off = 40 - x.  Chainage = 1+000 + 100*i.
YS = [0.0, 100.0, 200.0, 300.0, 400.0]
CHAINS = ['1+000', '1+100', '1+200', '1+300', '1+400']
ELEV_NOTES = ['11.10', '11.20', '11.30', '11.40', '11.50']
BLOCK_Z = [12.0, 11.5, 11.0, 11.5, 12.0]      # per-section channel shape (+i per section)
BLOCK_X = [40.0, 20.0, 0.0, -20.0, -40.0]     # -> offsets 0/20/40/60/80
SPOT = [(35.0, 11.0), (10.0, 13.5), (-25.0, 11.5), (-40.0, 11.8)]   # off 5/30/65/80
CONTOURS = [
    ('C1', 12.0, [[30.0, -50.0], [30.0, 450.0]]),      # crosses all sections at off 10
    ('C2', 13.0, [[10.0, -50.0], [10.0, 450.0]]),      # off 30 (a spot height sits there too)
    ('C3', 14.0, [[-10.0, -50.0], [-10.0, 450.0]]),    # off 50
    ('C4', 15.0, [[-30.0, -50.0], [-30.0, 450.0]]),    # off 70
    ('C5', 16.0, [[-40.0, 100.0], [40.0, 100.0]]),     # collinear with section 1+100 -> None
    ('C6', 20.0, [[100.0, -50.0], [100.0, 450.0]]),    # bbox never overlaps the sections
]
DOC = 'synthetic-channel.dwg'


def check(name, ok, detail=''):
    RESULTS.append((name, bool(ok), detail))
    return bool(ok)


def env(extra=None):
    e = os.environ.copy()
    e['PYTHONUTF8'] = '1'
    e['PYTHONIOENCODING'] = 'utf-8'
    e.pop('ZWC_SECTION_TIMING', None)
    if extra:
        e.update(extra)
    return e


def run(script, args, extra_env=None):
    p = subprocess.run([PY, script] + args, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', cwd=ROOT, env=env(extra_env))
    return p.returncode, p.stdout, p.stderr


# ---------------------------------------------------------------------------
# fixture builders
# ---------------------------------------------------------------------------
def section_lines():
    lines = []
    for i, y in enumerate(YS):
        lines.append({'h': 'S%d' % (i + 1), 'layer': 'dmx',
                      'ax': 40.0, 'ay': y, 'bx': -40.0, 'by': y})
    # a degenerate (zero-length) line far away: must be skipped, never break the run
    lines.append({'h': 'ZL', 'layer': 'dmx', 'ax': 500.0, 'ay': 500.0,
                  'bx': 500.0, 'by': 500.0})
    return lines


def label_list():
    labels = []
    for i, y in enumerate(YS):
        labels.append({'h': 'T%d' % (i + 1), 'text': CHAINS[i], 'x': 0.0, 'y': y + 2.0})
        labels.append({'h': 'E%d' % (i + 1), 'text': ELEV_NOTES[i], 'x': 30.0, 'y': y - 2.0})
    return labels


def block_list():
    blocks = []
    for i, y in enumerate(YS[:4]):
        for j, x in enumerate(BLOCK_X):
            z = BLOCK_Z[j] + i
            blocks.append({'h': 'B%d%d' % (i, j), 'name': 'gc200', 'x': x, 'y': y,
                           'z': z, 'attr': '%.2f' % z})
    # section 1+400 gets only 2 blocks -> usable(<3) false but still listed in the workbook
    for j, x in ((0, 40.0), (4, -40.0)):
        z = BLOCK_Z[j] + 4
        blocks.append({'h': 'B4%d' % j, 'name': 'gc200', 'x': x, 'y': 400.0,
                       'z': z, 'attr': '%.2f' % z})
    # one block 200 m off every line -> unassigned counter
    blocks.append({'h': 'BFAR', 'name': 'gc200', 'x': 0.0, 'y': 600.0, 'z': 99.0,
                   'attr': '99.00'})
    return blocks


def geom_com():
    ents = []
    for ln in section_lines():
        ents.append({'h': ln['h'], 'l': 'dmx', 'o': 'AcDbLine',
                     'p': [ln['ax'], ln['ay']], 'p2': [ln['bx'], ln['by']]})
    for i, y in enumerate(YS):
        for j, (x, z) in enumerate(SPOT):
            ents.append({'h': 'P%d%d' % (i, j), 'l': 'ZDH', 'o': 'AcDbPoint',
                         'p': [x, y, z + i]})
    for lab in label_list():
        ents.append({'h': lab['h'], 'l': 'KZD', 'o': 'AcDbText', 'x': lab['text'],
                     'p': [lab['x'], lab['y'], 0.0]})
    return {'doc': DOC, 'entities': ents}


def contours_doc():
    return {'doc': DOC, 'count': len(CONTOURS),
            'contours': [{'h': h, 'z': z, 'v': v} for h, z, v in CONTOURS]}


def write_case(name, files):
    """Write a <root>/zwcad-cooperate/ input dir; returns its tmp-dir root."""
    root = os.path.join(TMP, 'cases', name)
    zw = os.path.join(root, 'zwcad-cooperate')
    if os.path.isdir(root):
        shutil.rmtree(root)
    os.makedirs(zw)
    for fname, payload in files.items():
        path = os.path.join(zw, fname)
        if isinstance(payload, str):
            with open(path, 'w', encoding='utf-8') as fh:
                fh.write(payload)
        else:
            with open(path, 'w', encoding='utf-8') as fh:
                json.dump(payload, fh, ensure_ascii=False)
    return root


def happy_root():
    return write_case('happy', {
        'sections-src.json': {'doc': DOC, 'lines': section_lines(),
                              'blocks': block_list(), 'labels': label_list()},
        'geom-com.json': geom_com(),
        'contours.json': contours_doc(),
    })


def fresh_dir(name):
    path = os.path.join(TMP, 'out', name)
    if os.path.isdir(path):
        shutil.rmtree(path)
    return path


def snap(path):
    """Cell matrix of an xlsx, with the 生成时间 timestamp cell masked."""
    from openpyxl import load_workbook
    wb = load_workbook(path, data_only=True)
    sheets = []
    for ws in wb.worksheets:
        rows = [[c.value for c in row] for row in ws.iter_rows()]
        for row in rows:
            if row and row[0] == '生成时间' and len(row) > 1:
                row[1] = '<TIMESTAMP>'
        sheets.append((ws.title, rows))
    return sheets


def lines_of(stdout):
    return [ln.rstrip() for ln in (stdout or '').splitlines()]


def has_line(stdout, text):
    return any(ln.startswith(text) for ln in lines_of(stdout))


# ---------------------------------------------------------------------------
# 1) geometry helpers
# ---------------------------------------------------------------------------
def t_geometry():
    dd, tc = lib.seg_dist_t(0.0, 5.0, -10.0, 0.0, 10.0, 0.0)
    check('seg_dist_t 垂足距离', abs(dd - 5.0) < 1e-12 and abs(tc - 0.5) < 1e-12,
          'dd=%.12f t=%.12f' % (dd, tc))
    dd, tc = lib.seg_dist_t(20.0, 0.0, -10.0, 0.0, 10.0, 0.0)
    check('seg_dist_t 端点外（t 夹到 1）', abs(dd - 10.0) < 1e-12 and tc == 1.0,
          'dd=%.12f t=%.12f' % (dd, tc))
    dd, tc = lib.seg_dist_t(-20.0, 0.0, -10.0, 0.0, 10.0, 0.0)
    check('seg_dist_t 端点外（t 夹到 0）', abs(dd - 10.0) < 1e-12 and tc == 0.0,
          'dd=%.12f t=%.12f' % (dd, tc))
    dd, tc = lib.seg_dist_t(3.0, 4.0, 1.0, 1.0, 1.0, 1.0)
    check('seg_dist_t 零长段',
          abs(dd - ((3 - 1) ** 2 + (4 - 1) ** 2) ** 0.5) < 1e-12 and tc == 0.0,
          'dd=%.12f' % dd)

    p = lib.intersect((0.0, 0.0), (2.0, 2.0), (0.0, 2.0), (2.0, 0.0))
    check('intersect 直接相交', p is not None and abs(p[0] - 1.0) < 1e-12
          and abs(p[1] - 1.0) < 1e-12, 'p=%r' % (p,))
    check('intersect 平行 -> None', lib.intersect((0.0, 0.0), (10.0, 0.0),
                                                  (0.0, 5.0), (10.0, 5.0)) is None)
    check('intersect 线段外 -> None', lib.intersect((0.0, 0.0), (1.0, 0.0),
                                                    (5.0, -1.0), (5.0, 1.0)) is None)

    check('chainage_to_m 16+475', lib.chainage_to_m('16+475') == 16475.0)
    check('chainage_to_m 1+324.5', lib.chainage_to_m('1+324.5') == 1324.5)
    check('chainage_to_m 非法 -> None',
          lib.chainage_to_m('1+3x') is None and lib.chainage_to_m('abc') is None
          and lib.chainage_to_m('1+') is None)
    check('桩号/高程注记正则',
          bool(lib.CHAINAGE_RE.match('1+324')) and not lib.CHAINAGE_RE.match('21.84')
          and bool(lib.ELEV_RE.match('21.84')) and not lib.ELEV_RE.match('1+324'))


# ---------------------------------------------------------------------------
# 2) dedup / MIN_PTS
# ---------------------------------------------------------------------------
def t_dedup():
    pts = [(0.0, 1.0), (0.01, 2.0), (0.02, 3.0), (0.2, 4.0), (0.2001, 5.0)]
    got = lib.dedup_points(pts)
    check('dedup_points 0.05 m 内去重', [p[0] for p in got] == [0.0, 0.2],
          'kept=%r' % ([p[0] for p in got],))
    got = lib.dedup_points([(0.0, 1.0, 'C:a'), (0.01, 2.0, 'P:b')], prefer_spot=True)
    check('dedup_points 同位置优先高程点', got == [(0.01, 2.0, 'P:b')], 'got=%r' % (got,))
    got = lib.dedup_points([(0.0, 1.0, 'P:a'), (0.01, 2.0, 'C:b')], prefer_spot=True)
    check('dedup_points 已在高程点则不改动', got == [(0.0, 1.0, 'P:a')], 'got=%r' % (got,))
    check('usable_rows MIN_PTS=%d 过滤' % lib.MIN_PTS,
          [r['handle'] for r in lib.usable_rows(
              [{'handle': 'a', 'pts': [1, 2]}, {'handle': 'b', 'pts': [1, 2, 3]}])] == ['b'])


# ---------------------------------------------------------------------------
# 3) grid spatial index
# ---------------------------------------------------------------------------
def t_grid():
    segs = [(0.0, 0.0, 10.0, 0.0), (0.0, 1.0, 10.0, 1.0), (100.0, 100.0, 101.0, 100.0)]
    grid = lib.SegmentGrid(segs)
    dd, idx, tc = grid.nearest(5.0, 0.5, 5.0)
    check('grid nearest 等距取低序号（原严格 < 语义）', idx == 0 and abs(dd - 0.5) < 1e-12,
          'dd=%.3f idx=%d' % (dd, idx))
    dd, idx, _tc = grid.nearest(5.0, 0.5, 0.4)
    check('grid nearest 半径外仍给出全局最近 + 空网格哨兵',
          dd == 0.5 and idx == 0
          and lib.SegmentGrid([]).nearest(1.0, 1.0, 1.0) == (1e9, -1, 0.0),
          'dd=%r idx=%d' % (dd, idx))
    fa = lib.nearest_line_points([{'x': 5.0, 'y': 2.0, 'z': 1.0}],
                                 [{'h': 'L', 'ax': 0.0, 'ay': 0.0, 'bx': 10.0, 'by': 0.0}],
                                 grid, 0.5)
    check('nearest_line_points 超出容差计为 unassigned',
          fa[1] == 1 and not any(fa[0]['L']), 'unassigned=%d' % fa[1])
    got = grid.query_bbox(-1.0, -1.0, 11.0, 1.5)
    check('grid query_bbox 命中前两段', got == [0, 1], 'got=%r' % (got,))
    got = grid.query_radius(5.0, 0.5, 0.6)
    check('grid query_radius 完备且按序号升序', [g[0] for g in got] == [0, 1], 'got=%r' % (got,))
    segs_bf = [(0.0, 0.0, 3.0, 1.0), (20.0, 5.0, 26.0, 5.0), (-5.0, -5.0, -1.0, -2.0)]
    g2 = lib.SegmentGrid(segs_bf)
    q = (-1.0, -1.0, 3.0, 2.0)     # bbox query
    brute = [i for i, s in enumerate(segs_bf)
             if not (max(s[0], s[2]) < q[0] or min(s[0], s[2]) > q[2]
                     or max(s[1], s[3]) < q[1] or min(s[1], s[3]) > q[3])]
    check('grid query_bbox 是暴力 bbox 过滤的超集',
          set(brute) <= set(g2.query_bbox(*q)), 'brute=%r grid=%r'
          % (brute, g2.query_bbox(*q)))
    near = [abs(lib.seg_dist_t(1.0, 1.0, *s)[0]) for s in segs_bf]
    want = [i for i, d in enumerate(near) if d <= 2.0]
    check('grid query_radius == 暴力半径过滤',
          [x[0] for x in g2.query_radius(1.0, 1.0, 2.0)] == want,
          'want=%r' % (want,))
    check('auto_cell_size 自适应正数',
          lib.auto_cell_size(segs) > 0 and lib.auto_cell_size([]) > 0,
          'cell=%.3f' % lib.auto_cell_size(segs))


# ---------------------------------------------------------------------------
# 4) CLI contract
# ---------------------------------------------------------------------------
def t_cli():
    pg = lib.build_arg_parser('gcd')
    pd = lib.build_arg_parser('dmx')
    ag, ad = pg.parse_args([]), pd.parse_args([])
    check('CLI 默认值：--tmp-dir/--outdir/--name 均为 None',
          ag.tmp_dir is None and ag.outdir is None and ag.name is None
          and ad.tmp_dir is None and ad.outdir is None and ad.name is None)
    check('gcd 额外参数 --preview-chainage/--check-chainage 默认 16+475',
          ag.preview_chainage == '16+475' and ag.check_chainage == '16+475')
    check('dmx 无 --preview-chainage/--check-chainage',
          not hasattr(ad, 'preview_chainage') and not hasattr(ad, 'check_chainage'))
    check('默认输出名常量', lib.VARIANTS['gcd']['default_name'] == '断面数据-东总干渠-修正版'
          and lib.VARIANTS['dmx']['default_name'] == '断面数据-东总干渠')


# ---------------------------------------------------------------------------
# 5) gcd route: synthetic happy path
# ---------------------------------------------------------------------------
def t_gcd_route():
    root = happy_root()
    outdir = fresh_dir('gcd')
    code, so, se = run(GCD, ['--tmp-dir', root, '--outdir', outdir, '--name', 'syn',
                             '--check-chainage', '1+100', '--preview-chainage', '1+100'])
    check('gcd 正例退出码 0 且 stderr 空', code == 0 and se.strip() == '',
          'rc=%d stderr=%r' % (code, se.strip()[:200]))
    expect = ['drawing: ' + DOC,
              'sections: 6 | elevation blocks: 23 | KZD labels: 10',
              'chainage labels: 5 | elevation labels: 5',
              'sections with chainage: 5 | with elevation note: 5',
              'blocks assigned: 22 | unassigned: 1',
              'sections: 5 | usable(>=3 pts): 4']
    missing = [e for e in expect if e not in lines_of(so)]
    check('gcd stdout 关键行', not missing, 'missing=%r' % (missing,))
    check('gcd --check-chainage 自检块', has_line(so, '1+100 check: handle=S2')
          and has_line(so, '   起点距'), '')

    xlsx = os.path.join(outdir, 'syn.xlsx')
    csv = os.path.join(outdir, 'syn.csv')
    check('gcd 产物 xlsx+csv 落盘', os.path.isfile(xlsx) and os.path.isfile(csv))
    pngs = [f for f in os.listdir(outdir) if f.endswith('.png')]
    check('gcd 预览 PNG（matplotlib 可用时 3 张）',
          len(pngs) in (0, 3) and (('plot skipped' in so) if len(pngs) == 0 else True),
          'pngs=%r' % (pngs,))

    sheets = snap(xlsx)
    names = [s[0] for s in sheets]
    check('gcd sheet 名与顺序', names == ['断面汇总', '断面数据_长表', '断面数据_宽表', '说明'],
          'names=%r' % (names,))
    summary = sheets[0][1]
    check('gcd 汇总行数 = 5 断面 + 表头', len(summary) == 6, 'rows=%d' % len(summary))
    check('gcd 汇总表头', summary[0][:3] == ['序号', '桩号', '桩号(m)']
          and summary[0][10] == '高程点数' and summary[0][14] == '渠底注记', '')
    want_first = [1, '1+000', 1000, 'S1', 40, 0, -40, 0, 80, 'A端', 5, '0.00~80.00',
                  '11.00~12.00', 11, '11.10', 'OK']
    check('gcd 汇总首行逐值', summary[1] == want_first, 'got=%r' % (summary[1],))
    check('gcd 汇总末行桩号/点数/状态',
          summary[5][1] == '1+400' and summary[5][10] == 2 and summary[5][15] == '点数不足',
          'got=%r' % (summary[5],))
    longs = sheets[1][1]
    check('gcd 长表行数 = 22 点 + 表头', len(longs) == 23, 'rows=%d' % len(longs))
    check('gcd 长表表头', longs[0] == ['序号', '桩号', '起点距(m)', '反向起点距(m)',
                                       '高程(m)', '块句柄', '块属性height'])
    check('gcd 长表首行（起点距/反向/高程/句柄/属性）',
          longs[1] == [1, '1+000', 0, 80, 12, 'B00', '12.00'], 'got=%r' % (longs[1],))
    check('gcd 长表起点距升序',
          [r[2] for r in longs[1:6]] == [0, 20, 40, 60, 80],
          'offsets=%r' % ([r[2] for r in longs[1:6]],))
    wide = sheets[2][1]
    check('gcd 宽表表头', wide[0] == ['序号', '1+000', '1+100', '1+200', '1+300', '1+400'],
          'got=%r' % (wide[0],))
    check('gcd 宽表起点距段', wide[1][0] == '起点距(m)' and wide[2][1:] == [0, 0, 0, 0, 0]
          and wide[3][1] == 20 and wide[3][5] == 80, 'row2=%r row3=%r' % (wide[2], wide[3]))
    check('gcd 宽表高程段（base=10）', wide[9][0] == '高程(m)' and wide[10][1] == 12
          and wide[10][2] == 13 and wide[10][5] == 16, 'base=%r' % (wide[9],))
    notes = sheets[3][1]
    check('gcd 说明表行数/项目列', len(notes) == 10 and notes[0] == ['项目', '说明'],
          'rows=%d' % len(notes))
    check('gcd 说明表内容要点',
          any(r[0] == '高程点' and '共 23 个（已分配 22）' in (r[1] or '') for r in notes)
          and any(r[0] == '断面编号' and '如 1+100' in (r[1] or '') for r in notes)
          and any(r[0] == '合计' and '断面 5 条，高程点 22 个' == (r[1] or '') for r in notes),
          'notes=%r' % ([r[0] for r in notes],))
    check('gcd 说明表含生成时间', any(r[0] == '生成时间' and r[1] == '<TIMESTAMP>'
                                       for r in notes))

    with open(csv, encoding='utf-8-sig') as fh:
        csv_lines = fh.read().splitlines()
    check('gcd CSV 表头/首行/行数', csv_lines[0] == '序号,桩号,起点距(m),反向起点距(m),高程(m),块句柄'
          and csv_lines[1] == '1,1+000,0.000,80.000,12.000,B00'
          and len(csv_lines) == 23, 'first=%r n=%d' % (csv_lines[:2], len(csv_lines)))

    # determinism: a second run yields the same cells (timestamp masked)
    outdir2 = fresh_dir('gcd2')
    code2, _so2, _se2 = run(GCD, ['--tmp-dir', root, '--outdir', outdir2, '--name', 'syn',
                                  '--check-chainage', '1+100', '--preview-chainage', '1+100'])
    check('gcd 两次运行逐格一致（时间戳除外）',
          code2 == 0 and snap(os.path.join(outdir2, 'syn.xlsx')) == sheets)


# ---------------------------------------------------------------------------
# 6) dmx route: synthetic happy path
# ---------------------------------------------------------------------------
def t_dmx_route():
    root = happy_root()
    outdir = fresh_dir('dmx')
    code, so, se = run(DMX, ['--tmp-dir', root, '--outdir', outdir, '--name', 'syn'])
    check('dmx 正例退出码 0 且 stderr 空', code == 0 and se.strip() == '',
          'rc=%d stderr=%r' % (code, se.strip()[:200]))
    expect = ['drawing: ' + DOC,
              'entities: 36 | contours: 6',
              'sections: 6 | spot points: 20 | chainage labels: 5 | elevation labels: 5',
              'contour segments: 6',
              'sections built: 5',
              'sections=5 points=35 contour_hits=15 spot=20']
    missing = [e for e in expect if e not in lines_of(so)]
    check('dmx stdout 关键行', not missing, 'missing=%r' % (missing,))

    xlsx = os.path.join(outdir, 'syn.xlsx')
    sheets = snap(xlsx)
    check('dmx sheet 名与顺序',
          [s[0] for s in sheets] == ['断面汇总', '断面数据_长表', '断面数据_宽表',
                                     '仅高程点', '说明'],
          'names=%r' % ([s[0] for s in sheets],))
    summary = sheets[0][1]
    check('dmx 汇总行数/表头列', len(summary) == 6 and summary[0][10] == '等高线交点'
          and summary[0][11] == '高程点')
    check('dmx 汇总首行逐值',
          summary[1] == [1, '1+000', 1000, 'S1', 40, 0, -40, 0, 80, 'A端', 3, 4,
                         '5.00~80.00', '11.00~15.00', '11.10', 'OK'],
          'got=%r' % (summary[1],))
    longs = sheets[1][1]
    check('dmx 长表行数 = 35 点 + 表头', len(longs) == 36, 'rows=%d' % len(longs))
    check('dmx 长表表头', longs[0] == ['序号', '桩号', '起点距(m)', '高程(m)', '来源', '对象'])
    check('dmx 长表来源列/对象列',
          longs[1] == [1, '1+000', 5, 11, '高程点', 'P00']
          and longs[2] == [1, '1+000', 10, 12, '等高线', 'C1'], 'got=%r' % (longs[1:3],))
    # "same offset -> keep the spot height": off 30 has contour 13.0 and spot 13.5
    row30 = [r for r in longs if r[0] == 1 and r[2] == 30]
    check('dmx 同一位置优先高程点（off 30 -> 13.5 / P 源）',
          len(row30) == 1 and row30[0][3] == 13.5 and row30[0][4] == '高程点',
          'rows=%r' % (row30,))
    check('dmx 起点距升序且逐段来源正确',
          [r[2] for r in longs[1:8]] == [5, 10, 30, 50, 65, 70, 80],
          'offsets=%r' % ([r[2] for r in longs[1:8]],))
    wide = sheets[2][1]
    check('dmx 宽表表头/列数', wide[0] == ['起点距(m)', '1+000', '1+100', '1+200', '1+300',
                                          '1+400'] and len(wide) == 8,
          'header=%r rows=%d' % (wide[0], len(wide)))
    check('dmx 宽表逐行高程（列=断面，含原实现的列偏移）',
          wide[1][:5] == [11, 12, 13, 14, 15], 'got=%r' % (wide[1][:5],))
    extra = sheets[3][1]
    check('dmx 仅高程点表行数=20+1 且全为高程点',
          len(extra) == 21 and extra[0] == ['序号', '桩号', '起点距(m)', '高程(m)', '点句柄']
          and [r[4][:1] for r in extra[1:]] == ['P'] * 20,
          'rows=%d' % len(extra))
    notes = sheets[4][1]
    check('dmx 说明表行数/来源要点', len(notes) == 12
          and any(r[0] == '地形来源①' and '得 15 个剖面高程点' in (r[1] or '') for r in notes)
          and any(r[0] == '地形来源②' and '得 20 个点' in (r[1] or '') for r in notes)
          and any(r[0] == '合计'
                  and (r[1] or '') == '断面 5 条，剖面点 35 个（等高线 15 + 高程点 20）'
                  for r in notes),
          'notes=%r' % ([r[0] for r in notes],))


# ---------------------------------------------------------------------------
# 7) bad inputs + NO_OPENPYXL
# ---------------------------------------------------------------------------
def assert_no_output(outdir, label):
    produced = sorted(os.listdir(outdir)) if os.path.isdir(outdir) else []
    check(label, not produced, 'produced=%r' % (produced,))


def t_bad_inputs():
    empty = os.path.join(TMP, 'cases', 'empty')
    os.makedirs(os.path.join(empty, 'zwcad-cooperate'), exist_ok=True)

    # --- gcd: no input file
    outdir = fresh_dir('bad-gcd-noinput')
    code, so, se = run(GCD, ['--tmp-dir', empty, '--outdir', outdir, '--name', 'x'])
    check('gcd NO_INPUT -> exit 2 + stderr 一行',
          code == 2 and se.strip().startswith('error NO_INPUT: missing input:')
          and len(se.strip().splitlines()) == 1, 'rc=%d stderr=%r' % (code, se.strip()))
    check('gcd NO_INPUT stdout 无 written:', 'written:' not in so)
    assert_no_output(outdir, 'gcd NO_INPUT 不产出文件')

    # --- gcd: broken JSON
    bad = write_case('gcd-badjson', {'sections-src.json': '{"doc": "x", '})
    outdir = fresh_dir('bad-gcd-badjson')
    code, so, se = run(GCD, ['--tmp-dir', bad, '--outdir', outdir, '--name', 'x'])
    check('gcd BAD_INPUT(坏 JSON) -> exit 2 + BAD_INPUT',
          code == 2 and se.strip().startswith('error BAD_INPUT:') and 'written:' not in so,
          'rc=%d stderr=%r' % (code, se.strip()[:120]))
    assert_no_output(outdir, 'gcd 坏 JSON 不产出文件')

    # --- gcd: missing key
    bad = write_case('gcd-missingkey', {'sections-src.json': {'doc': 'x', 'lines': [],
                                                               'blocks': [], 'labels': []}})
    outdir = fresh_dir('bad-gcd-missingkey')
    code, so, se = run(GCD, ['--tmp-dir', bad, '--outdir', outdir, '--name', 'x'])
    check('gcd 空 lines -> BAD_INPUT + exit 2',
          code == 2 and se.strip().startswith('error BAD_INPUT:') and 'lines 为空' in se,
          'rc=%d stderr=%r' % (code, se.strip()[:120]))
    assert_no_output(outdir, 'gcd 空 lines 不产出文件')

    # --- dmx: no input files
    outdir = fresh_dir('bad-dmx-noinput')
    code, so, se = run(DMX, ['--tmp-dir', empty, '--outdir', outdir, '--name', 'x'])
    check('dmx NO_INPUT -> exit 2 + stderr 一行',
          code == 2 and se.strip().startswith('error NO_INPUT:') and 'written:' not in so,
          'rc=%d stderr=%r' % (code, se.strip()[:120]))
    assert_no_output(outdir, 'dmx NO_INPUT 不产出文件')

    # --- dmx: contour with a single vertex
    bad = write_case('dmx-badcontour', {
        'geom-com.json': {'doc': DOC, 'entities': []},
        'contours.json': {'doc': DOC, 'count': 1,
                          'contours': [{'h': 'C1', 'z': 1.0, 'v': [[0.0, 0.0]]}]},
    })
    outdir = fresh_dir('bad-dmx-badcontour')
    code, so, se = run(DMX, ['--tmp-dir', bad, '--outdir', outdir, '--name', 'x'])
    check('dmx 等高线顶点不足 -> BAD_INPUT + exit 2',
          code == 2 and se.strip().startswith('error BAD_INPUT:')
          and '至少需要 2 个顶点' in se, 'rc=%d stderr=%r' % (code, se.strip()[:120]))
    assert_no_output(outdir, 'dmx 坏等高线不产出文件')

    # --- dmx: no chainage text -> cannot sort sections
    nochain = geom_com()
    nochain['entities'] = [e for e in nochain['entities']
                           if not (e['l'] == 'KZD' and lib.CHAINAGE_RE.match(e['x']))]
    bad = write_case('dmx-nochain', {'geom-com.json': nochain,
                                     'contours.json': contours_doc()})
    outdir = fresh_dir('bad-dmx-nochain')
    code, so, se = run(DMX, ['--tmp-dir', bad, '--outdir', outdir, '--name', 'x'])
    check('dmx 无桩号文字 -> BAD_INPUT + exit 2',
          code == 2 and se.strip().startswith('error BAD_INPUT:')
          and '没有 KZD 桩号文字' in se, 'rc=%d stderr=%r' % (code, se.strip()[:120]))
    assert_no_output(outdir, 'dmx 无桩号不产出文件')


def t_no_openpyxl():
    stub = os.path.join(TMP, 'stub')
    os.makedirs(stub, exist_ok=True)
    with open(os.path.join(stub, 'openpyxl.py'), 'w', encoding='utf-8') as fh:
        fh.write('raise ImportError("openpyxl stubbed out by selftest_sections.py")\n')
    root = happy_root()
    for label, script, name in (('gcd', GCD, 'x'), ('dmx', DMX, 'x')):
        outdir = fresh_dir('noopenpyxl-' + label)
        code, so, se = run(script, ['--tmp-dir', root, '--outdir', outdir, '--name', name],
                           extra_env={'PYTHONPATH': stub})
        check('%s NO_OPENPYXL（桩模块）-> exit 2 + 明确错误码' % label,
              code == 2 and se.strip().startswith('error NO_OPENPYXL:')
              and 'pip install openpyxl' in se and 'written:' not in so,
              'rc=%d stderr=%r' % (code, se.strip()[:160]))
        assert_no_output(outdir, '%s NO_OPENPYXL 不产出文件' % label)


def t_optional_deps():
    """matplotlib 缺失 -> 'plot skipped' 但仍成功；空桩号参数 -> 缩水的预览/自检。"""
    stub = os.path.join(TMP, 'stub-mpl')
    os.makedirs(stub, exist_ok=True)
    with open(os.path.join(stub, 'matplotlib.py'), 'w', encoding='utf-8') as fh:
        fh.write('raise ImportError("matplotlib stubbed out by selftest_sections.py")\n')
    root = happy_root()
    outdir = fresh_dir('nompl-gcd')
    code, so, se = run(GCD, ['--tmp-dir', root, '--outdir', outdir, '--name', 'syn'],
                       extra_env={'PYTHONPATH': stub})
    check('matplotlib 缺失 -> exit 0 + plot skipped + 仍落盘',
          code == 0 and se.strip() == '' and has_line(so, 'plot skipped:')
          and os.path.isfile(os.path.join(outdir, 'syn.xlsx'))
          and not [f for f in os.listdir(outdir) if f.endswith('.png')],
          'rc=%d stdout_tail=%r' % (code, lines_of(so)[-2:]))

    outdir = fresh_dir('gcd-emptyopts')
    code, so, se = run(GCD, ['--tmp-dir', root, '--outdir', outdir, '--name', 'syn',
                             '--preview-chainage', '', '--check-chainage', ''])
    pngs = sorted(f for f in os.listdir(outdir) if f.endswith('.png')) \
        if os.path.isdir(outdir) else []
    check('gcd --preview-chainage ""/--check-chainage "" -> 只出 首/中 两张图且无自检块',
          code == 0 and 'check: handle=' not in so
          and (len(pngs) in (0, 2)), 'rc=%d pngs=%r' % (code, pngs))


def main():
    if os.path.isdir(TMP):
        shutil.rmtree(TMP)
    os.makedirs(TMP)
    t_geometry()
    t_dedup()
    t_grid()
    t_cli()
    t_gcd_route()
    t_dmx_route()
    t_bad_inputs()
    t_no_openpyxl()
    t_optional_deps()

    width = max(len(n) for n, _, _ in RESULTS) if RESULTS else 10
    failed = 0
    for name, ok, detail in RESULTS:
        print('%-4s  %-*s  %s' % ('PASS' if ok else 'FAIL', width, name, detail))
        if not ok:
            failed += 1
    print('-' * 72)
    print('[selftest_sections] RESULT: %s  (%d PASS / %d FAIL)'
          % ('FAIL' if failed else 'PASS', len(RESULTS) - failed, failed))
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
