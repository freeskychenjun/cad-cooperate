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
#
# 本文件是**薄入口**：除“高程来源 = 等高线求交 + 高程点”这一路线标识外，全部实现（几何
# 工具、标签 1:1 关联、网格空间索引、去重/MIN_PTS、Excel 写出、预览 PNG、CLI/错误码）都在
# scripts\section\section_lib.py，与 build_sections_gcd.py 共用同一份。
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import section_lib as lib  # noqa: E402

DEFAULT_NAME = lib.VARIANTS['dmx']['default_name']


def build_arg_parser():
    return lib.build_arg_parser('dmx')


def main(argv=None):
    """入口：BuildError -> stderr 一行 `error <CODE>: <msg>` + 退出码 2（不裸 traceback）。"""
    return lib.main('dmx', argv)


if __name__ == '__main__':
    sys.exit(main())
