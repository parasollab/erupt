# STEP -> OBJ (+MTL) with assembly names and face colours, via OpenCASCADE (cadquery-ocp).
#   python step_to_obj.py <in.step> <out.obj> [--lin 1.0] [--ang 0.35]
# Each leaf solid becomes one OBJ object "<parent assembly>|<leaf>#<n>", materials are named
# "Opaque(r,g,b)" (like the Fusion FBX export) so bl_assemble's glass_materials keep working.
# Output is in metres, in the STEP's own axes (print the bbox; pass --zup/--yup to bl_prepare).
import sys, time, collections, argparse
from OCP.STEPCAFControl import STEPCAFControl_Reader
from OCP.TDocStd import TDocStd_Document
from OCP.XCAFApp import XCAFApp_Application
from OCP.XCAFDoc import XCAFDoc_DocumentTool, XCAFDoc_ColorSurf, XCAFDoc_ColorGen
from OCP.TCollection import TCollection_ExtendedString, TCollection_AsciiString
from OCP.TDF import TDF_LabelSequence, TDF_Label
from OCP.TDataStd import TDataStd_Name
from OCP.TopLoc import TopLoc_Location
from OCP.TopoDS import TopoDS
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.BRep import BRep_Tool
from OCP.Quantity import Quantity_Color
from OCP.IFSelect import IFSelect_RetDone
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib

ap = argparse.ArgumentParser()
ap.add_argument('step'); ap.add_argument('obj')
ap.add_argument('--lin', type=float, default=1.0, help='linear deflection, mm')
ap.add_argument('--ang', type=float, default=0.35, help='angular deflection, rad')
args = ap.parse_args()
t0 = time.time()
def log(*a): print(f"[step2obj {time.time()-t0:6.0f}s]", *a, flush=True)

app = XCAFApp_Application.GetApplication_s()
doc = TDocStd_Document(TCollection_ExtendedString("MDTV-XCAF"))
app.NewDocument(TCollection_ExtendedString("MDTV-XCAF"), doc)
reader = STEPCAFControl_Reader()
reader.SetColorMode(True); reader.SetNameMode(True); reader.SetLayerMode(False)
if reader.ReadFile(args.step) != IFSelect_RetDone:
    sys.exit("STEP read failed")
log("read")
reader.Transfer(doc)
log("transferred")
shape_tool = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
color_tool = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())

def label_name(label):
    n = TDataStd_Name()
    if label.FindAttribute(TDataStd_Name.GetID_s(), n):
        return TCollection_AsciiString(n.Get()).ToCString().strip()
    return ""

leaves = []
def walk(label, loc, path):
    name = label_name(label) or f"L{label.Tag()}"
    if shape_tool.IsReference_s(label):
        ref = TDF_Label()
        shape_tool.GetReferredShape_s(label, ref)
        walk(ref, loc.Multiplied(shape_tool.GetLocation_s(label)), path + [name])
        return
    if shape_tool.IsAssembly_s(label):
        comps = TDF_LabelSequence()
        shape_tool.GetComponents_s(label, comps)
        for i in range(1, comps.Length() + 1):
            walk(comps.Value(i), loc, path + [name])
        return
    leaves.append((path + [name], shape_tool.GetShape_s(label), loc, label))

roots = TDF_LabelSequence()
shape_tool.GetFreeShapes(roots)
for i in range(1, roots.Length() + 1):
    walk(roots.Value(i), TopLoc_Location(), [])
log(f"{len(leaves)} leaf shapes")

def color_of(shape, label):
    c = Quantity_Color()
    for typ in (XCAFDoc_ColorSurf, XCAFDoc_ColorGen):
        if color_tool.GetColor(shape, typ, c): return c
    for typ in (XCAFDoc_ColorSurf, XCAFDoc_ColorGen):
        if color_tool.GetColor(label, typ, c): return c
    return None

def mat_name(c):
    if c is None: return "Opaque(128,128,128)"
    return "Opaque(%d,%d,%d)" % tuple(int(round(v * 255)) for v in (c.Red(), c.Green(), c.Blue()))

# Mesh every leaf (shared TShapes are meshed once).
for path, shape, loc, label in leaves:
    BRepMesh_IncrementalMesh(shape, args.lin, False, args.ang, True)
log("meshed")

mats = {}
out = open(args.obj, 'w')
mtl_path = args.obj[:-4] + '.mtl'
out.write(f"mtllib {mtl_path.split('/')[-1]}\n")
voff = 0
total_tris = 0
counts = collections.Counter()
bb_min = [1e9] * 3; bb_max = [-1e9] * 3
used = collections.Counter()
for path, shape, loc, label in leaves:
    # path alternates assembly product / NAUO occurrence / product; name by the enclosing product.
    leaf = path[-1]
    parent = next((n for n in reversed(path[:-1]) if not n.startswith("NAUO")), "root")
    base = f"{parent}|{leaf}"
    used[base] += 1
    name = f"{base}#{used[base]}" if used[base] > 1 else base
    # Instances of one product: suffix so names stay unique (the OBJ importer would merge equal names).
    out.write(f"o {name}\n")
    solid_color = color_of(shape, label)
    exp = TopExp_Explorer(shape, TopAbs_FACE)
    ntri = 0
    cur_mat = None
    while exp.More():
        face = TopoDS.Face_s(exp.Current())
        floc = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, floc)
        exp.Next()
        if tri is None: continue
        trsf = loc.Multiplied(floc).Transformation()
        reversed_ = face.Orientation() == TopAbs_REVERSED
        fc = Quantity_Color()
        col = fc if color_tool.GetColor(face, XCAFDoc_ColorSurf, fc) else solid_color
        m = mat_name(col)
        if m not in mats: mats[m] = col
        if m != cur_mat:
            out.write(f"usemtl {m}\n"); cur_mat = m
        n = tri.NbNodes()
        for i in range(1, n + 1):
            p = tri.Node(i).Transformed(trsf)
            x, y, z = p.X() / 1000.0, p.Y() / 1000.0, p.Z() / 1000.0
            out.write(f"v {x:.5f} {y:.5f} {z:.5f}\n")
            for k, v in enumerate((x, y, z)):
                if v < bb_min[k]: bb_min[k] = v
                if v > bb_max[k]: bb_max[k] = v
        for i in range(1, tri.NbTriangles() + 1):
            t = tri.Triangle(i)
            a, b, c = t.Value(1), t.Value(2), t.Value(3)
            if reversed_: b, c = c, b
            out.write(f"f {voff + a} {voff + b} {voff + c}\n")
        voff += n
        ntri += tri.NbTriangles()
    counts[name] = ntri
    total_tris += ntri
out.close()
with open(mtl_path, 'w') as fh:
    for m, c in mats.items():
        r, g, b = (c.Red(), c.Green(), c.Blue()) if c is not None else (0.5, 0.5, 0.5)
        fh.write(f"newmtl {m}\nKd {r:.4f} {g:.4f} {b:.4f}\nKa 0 0 0\nKs 0.2 0.2 0.2\nNs 30\nd 1\n\n")
log(f"wrote {args.obj}: {total_tris} triangles, {len(mats)} materials, bbox(m) {[round(v,3) for v in bb_min]} .. {[round(v,3) for v in bb_max]}")
for name, n in counts.most_common():
    print(f"  {n:8d}  {name}")
log("DONE")
