# parse_dxf.py - reconcile DXF entity count/type distribution against COM ModelSpace.Count
import sys, os, collections, time

path = sys.argv[1]
t0 = time.time()
with open(path, 'rb') as f:
    head = f.read(64)
print("file=", path)
print("size=", os.path.getsize(path))
print("head_hex=", head[:16].hex())
print("head_ascii=", ''.join(chr(b) if 32 <= b < 127 else '.' for b in head))
is_bin = head[:22] == b'AutoCAD Binary DXF\r\n\x1a\x00'
print("binary_dxf=", is_bin)

if is_bin:
    print("BINARY DXF: skip ezdxf text parse")
    sys.exit(0)

try:
    import ezdxf
except Exception as e:
    print("ezdxf import failed:", e)
    sys.exit(2)

doc = ezdxf.readfile(path)
print("dxfversion=", doc.dxfversion)
print("acad_release=", doc.acad_release)
msp = doc.modelspace()
cnt = collections.Counter(e.dxftype() for e in msp)
total = sum(cnt.values())
print("modelspace_total=", total)
print("types=", dict(cnt.most_common(20)))
layers = collections.Counter(e.dxf.layer for e in msp)
print("layers=", dict(layers.most_common(10)))
# sanity: first LINE coords
for e in msp:
    if e.dxftype() == 'LINE':
        print("first_line_start=", tuple(round(v, 3) for v in e.dxf.start))
        print("first_line_end=", tuple(round(v, 3) for v in e.dxf.end))
        break
print("parse_ms=", int((time.time() - t0) * 1000))
