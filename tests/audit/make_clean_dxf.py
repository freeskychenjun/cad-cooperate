#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make_clean_dxf.py - tiny helper: write tests/audit/fixtures/clean.dxf.

The clean DXF is the "no problem here" end-to-end case: it is opened in CAD, read
through zwc.ps1 and audited; the expected result is zero issues. The same drawing
exists as clean.info.json / clean.read.json for the offline regression test.
"""

from __future__ import annotations

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools", "py-libs"))

import ezdxf  # noqa: E402


def main():
    doc = ezdxf.new("R2000", setup=False)
    doc.header["$INSUNITS"] = 4          # millimetres
    doc.header["$LTSCALE"] = 1.0
    doc.header["$DIMSCALE"] = 1.0
    doc.header["$TEXTSIZE"] = 2.5
    doc.header["$EXTMIN"] = (0.0, 0.0, 0.0)
    doc.header["$EXTMAX"] = (100.0, 50.0, 0.0)
    doc.layers.add("BODY", color=7)
    doc.layers.add("ANNO", color=3)

    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (100, 0), (100, 50), (0, 50)], close=True,
                       dxfattribs={"layer": "BODY"})
    msp.add_line((0, 25), (100, 25), dxfattribs={"layer": "BODY"})
    for i in range(12):
        x = 4.0 + i * 8.0
        msp.add_text("L%d" % (i + 1), height=2.5,
                     dxfattribs={"layer": "ANNO", "insert": (x, 30.0)})

    path = os.path.join(HERE, "fixtures", "clean.dxf")
    doc.saveas(path)
    print("wrote", path, len(list(msp)), "entities")


if __name__ == "__main__":
    main()
