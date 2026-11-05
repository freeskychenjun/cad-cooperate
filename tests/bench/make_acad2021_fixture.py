r"""Generate an R2007 DXF fixture with OLD-STYLE 2D POLYLINEs, a LWPOLYLINE, an attributed
block reference and a TEXT - the shapes the AutoCAD 2021 compatibility work has to cover.

Why a fixture: the real user drawing (388 AcDb2dPolyline contours + 8,123 attributed blocks) must
not be used for write/regression tests, and its LISP dump takes minutes. This fixture is tiny and
has KNOWN coordinates, so the expected values can be asserted exactly.

Old-style POLYLINE is created explicitly (ezdxf's add_polyline2d would produce LWPOLYLINE): the
POLYLINE header carries the elevation (group 10 z / group 30) and every VERTEX is a sub-entity -
exactly the shape that made the LISP (entnext) walk cross SEQEND on AutoCAD 2021.

Run:
    set PYTHONPATH=<repo>/tools/py-libs
    python tests\bench\make_acad2021_fixture.py
"""
import os

import ezdxf


def old_polyline(msp, points, elevation, closed, layer, color):
    """Classic POLYLINE + VERTEX sub-entities (what AcadPolyline.Coordinates sees as triples)."""
    flags = 1 if closed else 0
    poly = msp.new_entity("POLYLINE", dxfattribs={
        "flags": flags,
        "elevation": (0.0, 0.0, elevation),
        "layer": layer,
        "color": color,
    })
    for x, y in points:
        poly.append_vertex((x, y, 0.0))
    return poly


def main():
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "acad-oldpoly.dxf")
    doc = ezdxf.new("R2007", setup=False)
    msp = doc.modelspace()

    # 1) closed 5-vertex classic polyline, elevation 10.0 (contour-like)
    p1 = [(0, 0), (40, 0), (40, 30), (0, 30), (0, 0)]
    old_polyline(msp, p1, 10.0, True, "DGX", 1)

    # 2) open 8-vertex classic polyline, elevation 12.5
    p2 = [(100, 0), (120, 10), (140, 5), (160, 25), (180, 15), (200, 35), (220, 20), (240, 40)]
    old_polyline(msp, p2, 12.5, False, "DGX", 2)

    # 3) LWPOLYLINE control case: vertices are inline (group 10 repeated), no sub-entities
    msp.add_lwpolyline([(300, 0), (340, 0), (340, 20), (300, 20)], close=True,
                       dxfattribs={"layer": "DSX", "elevation": 7.25})

    # 4) block with one attribute, referenced twice - elevation labels live in block attributes
    blk = doc.blocks.new("ELEVPT")
    blk.add_attdef("height", (0, 0), dxfattribs={"height": 0.5, "layer": "GCD"})
    for i, (ins, value) in enumerate((((400, 100), "11.11"), ((420, 120), "22.22"))):
        ref = msp.add_blockref("ELEVPT", ins, dxfattribs={"layer": "GCD"})
        ref.add_attrib("height", value, ins, dxfattribs={"height": 0.5, "layer": "GCD"})

    # 5) plain text (no attribute indirection)
    msp.add_text("ACAD-OLDPOLY", height=2.5, dxfattribs={"layer": "ANNO"}).set_placement((0, 200))

    doc.saveas(out)
    print("written:", out)
    print("entities:", len(msp))


if __name__ == "__main__":
    main()
