# Build the cross-section workbook from the CORRECT source: GCD blocks whose insertion Z is the
# ground elevation on the section line (the ZDH layer holds unrelated terrain spot heights).
#
# Input : <tmp>\cad-cooperate\sections-src.json  (tmp\extract-sections-src.ps1)
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
#
# 本文件是**薄入口**：除“高程来源 = GCD 块 Z”这一路线标识外，全部实现（几何工具、标签
# 1:1 关联、网格空间索引、去重/MIN_PTS、Excel 写出、预览 PNG、CLI/错误码）都在
# scripts\section\section_lib.py，与 build_sections_from_dmx.py 共用同一份。
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import section_lib as lib  # noqa: E402

DEFAULT_NAME = lib.VARIANTS['gcd']['default_name']
DEFAULT_CHECK_CHAINAGE = lib.DEFAULT_CHECK_CHAINAGE


def build_arg_parser():
    return lib.build_arg_parser('gcd')


def main(argv=None):
    """入口：BuildError -> stderr 一行 `error <CODE>: <msg>` + 退出码 2（不裸 traceback）。"""
    return lib.main('gcd', argv)


if __name__ == '__main__':
    sys.exit(main())
