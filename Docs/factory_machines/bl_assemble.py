# Assemble a machine: load prepared .blend, (scale), rebase to footprint origin, cut/group parts,
# set pivots, build hierarchy, decimate, quantise materials, add extras, export FBX for Unity.
import bpy, bmesh, sys, json, math, os
from mathutils import Vector, Matrix
argv = sys.argv[sys.argv.index('--')+1:]
spec = json.load(open(argv[0])); out_fbx = argv[1]
bpy.ops.wm.open_mainfile(filepath=spec['blend'])
S = float(spec.get('scale', 1.0))
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
objs = {o['name']: o for o in json.load(open(spec['objects_json']))['objects']}

# ---- bake transforms, scale, rebase ---------------------------------------------------------
for o in meshes:
    o.data.transform(o.matrix_world); o.matrix_world = Matrix.Identity(4)
import numpy as np
gmin = Vector((1e9,)*3); gmax = Vector((-1e9,)*3)
for o in meshes:
    n = len(o.data.vertices)
    if n == 0: continue
    arr = np.empty(n * 3, dtype=np.float32); o.data.vertices.foreach_get('co', arr); arr = arr.reshape(n, 3)
    gmin = Vector(map(min, gmin, Vector(arr.min(axis=0)))); gmax = Vector(map(max, gmax, Vector(arr.max(axis=0))))
rebase = Vector((-(gmin.x + gmax.x) / 2, -(gmin.y + gmax.y) / 2, -gmin.z))
T = Matrix.Translation(rebase * S) @ Matrix.Scale(S, 4)
for o in meshes:
    o.data.transform(T)
def P(v):  # spec coordinate (original frame) -> working frame
    return (Vector(v) * S) + rebase * S
def V(v):  # spec vector -> working (scale only)
    return Vector(v) * S
print("ASM rebase", list(rebase), "scale", S, "footprint", list((gmax - gmin) * S))

# ---- selection helpers -----------------------------------------------------------------------
def sel_box(c0, c1, maxsize=(1e9,)*3):
    return [n for n, o in objs.items() if all(c0[i] <= o['center'][i] <= c1[i] for i in range(3)) and all(o['size'][i] <= maxsize[i] for i in range(3))]
def sel_cyl(axis, a, b, rad, lo, hi, maxsize):
    i = 'xyz'.index(axis); j, k = [t for t in range(3) if t != i]
    return [n for n, o in objs.items() if lo <= o['center'][i] <= hi and math.hypot(o['center'][j]-a, o['center'][k]-b) <= rad and max(o['size']) <= maxsize]
def resolve(sel):
    names = set()
    for item in sel:
        if isinstance(item, str): names.add(item)
        elif item['type'] == 'box': names.update(sel_box(item['min'], item['max'], item.get('maxsize', (1e9,)*3)))
        elif item['type'] == 'cyl': names.update(sel_cyl(item['axis'], item['a'], item['b'], item['rad'], item['lo'], item['hi'], item.get('maxsize', 1e9)))
        elif item['type'] == 'exclude': names -= set(resolve(item['names']))
    return [n for n in names if n in bpy.data.objects]

# ---- cutting --------------------------------------------------------------------------------
def cut_box(src_name, bmin, bmax, new_name):
    src = bpy.data.objects[src_name]
    bm = bmesh.new(); bm.from_mesh(src.data)
    bmin, bmax = P(bmin), P(bmax)
    for axis in range(3):
        for val, sign in ((bmin[axis], 1), (bmax[axis], -1)):
            no = Vector((0, 0, 0)); no[axis] = sign; co = Vector((0, 0, 0)); co[axis] = val
            geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
            bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=no, clear_outer=False, clear_inner=False)
    inside = [f for f in bm.faces if all(bmin[i] - 1e-5 <= f.calc_center_median()[i] <= bmax[i] + 1e-5 for i in range(3))]
    bm_in = bm.copy()
    # delete outside faces from bm_in: rebuild index map
    bm.faces.ensure_lookup_table(); bm_in.faces.ensure_lookup_table()
    inside_idx = set(f.index for f in inside)
    bmesh.ops.delete(bm_in, geom=[f for f in bm_in.faces if f.index not in inside_idx], context='FACES')
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index in inside_idx], context='FACES')
    me = bpy.data.meshes.new(new_name); bm_in.to_mesh(me); bm_in.free()
    for m in src.data.materials: me.materials.append(m)
    bm.to_mesh(src.data); bm.free()
    ob = bpy.data.objects.new(new_name, me); bpy.context.scene.collection.objects.link(ob)
    print(f"ASM cut {new_name} from {src_name}: {len(me.polygons)} faces")
    return ob

# ---- materials: quantise ---------------------------------------------------------------------
qmats = {}
def qmat(mat, override=None):
    if override:
        if override not in bpy.data.materials:
            m = bpy.data.materials.new(override); m.diffuse_color = (0.8, 0.9, 1.0, 0.3); m.blend_method = 'BLEND'
        return bpy.data.materials[override]
    c = mat.diffuse_color if mat else (0.6, 0.6, 0.6, 1)
    key = tuple(min(0.8, round(x * 6) / 6) for x in c[:3])
    if key not in qmats:
        m = bpy.data.materials.new("M_%d_%d_%d" % tuple(int(k * 255) for k in key)); m.diffuse_color = (*key, 1.0)
        m.roughness = 0.45; m.metallic = 0.0
        qmats[key] = m
    return qmats[key]
glass = set(spec.get('glass', []))
for o in meshes:
    for slot in o.material_slots:
        slot.material = qmat(slot.material, 'Glass' if o.name in glass else None)

# ---- extras (primitive meshes) -------------------------------------------------------------
def add_extra(e):
    bm = bmesh.new()
    c = P(e['center'])
    if e['type'] == 'box':
        sz = V(e['size'])
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=sz, verts=bm.verts)
    elif e['type'] in ('cylinder', 'hex'):
        segs = 6 if e['type'] == 'hex' else 24
        r, d = e['radius'] * S, e['depth'] * S
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=d)
        ax = e.get('axis', 'z')
        if ax == 'x': bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, 'Y'), verts=bm.verts)
        if ax == 'y': bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, 'X'), verts=bm.verts)
    bmesh.ops.translate(bm, vec=c, verts=bm.verts)
    me = bpy.data.meshes.new(e['name']); bm.to_mesh(me); bm.free()
    col = e.get('color', (0.7, 0.7, 0.7)); m = bpy.data.materials.new('M_' + e['name']); m.diffuse_color = (*col, 1.0); m.roughness = 0.4
    me.materials.append(m)
    ob = bpy.data.objects.new(e['name'], me); bpy.context.scene.collection.objects.link(ob)
    for p in me.polygons: p.use_smooth = e['type'] != 'box' and e['type'] != 'hex'
    return ob

# ---- parts -----------------------------------------------------------------------------------
root = bpy.data.objects.new(spec['name'], None); bpy.context.scene.collection.objects.link(root)
claimed = set()
part_objs = {}
part_parent = {}
def build_part(pdef, parent):
    names = resolve(pdef.get('select', []))
    # children first so they are not swallowed by this part's join
    children = pdef.get('children', [])
    child_defs = [(c, resolve(c.get('select', []))) for c in children]
    child_names = set(n for _, ns in child_defs for n in ns)
    pieces = [bpy.data.objects[n] for n in names if n not in claimed and n not in child_names]
    claimed.update(o.name for o in pieces)
    for cut in pdef.get('cuts', []):
        pieces.append(cut_box(cut['from'], cut['min'], cut['max'], pdef['name'] + '_cut'))
    for e in pdef.get('extras', []):
        pieces.append(add_extra(e))
    # children first so they are not swallowed by this part's join
    if not pieces:
        print("ASM WARNING empty part", pdef['name']); ob = bpy.data.objects.new(pdef['name'], None); bpy.context.scene.collection.objects.link(ob)
    else:
        with bpy.context.temp_override(active_object=pieces[0], selected_editable_objects=pieces, selected_objects=pieces):
            bpy.ops.object.join()
        ob = pieces[0]; ob.name = pdef['name']; ob.data.name = pdef['name']
    pivot = P(pdef['pivot']) if pdef.get('pivot') else Vector(((ob.bound_box[0][0] + ob.bound_box[6][0]) / 2, (ob.bound_box[0][1] + ob.bound_box[6][1]) / 2, (ob.bound_box[0][2] + ob.bound_box[6][2]) / 2)) if ob.type == 'MESH' else Vector((0, 0, 0))
    if ob.type == 'MESH':
        ob.data.transform(Matrix.Translation(-pivot))
    ob.parent = parent
    ob.matrix_parent_inverse = Matrix.Identity(4)
    ob.matrix_world = Matrix.Translation(pivot)
    part_objs[pdef['name']] = ob
    part_parent[pdef['name']] = pdef.get('_parent')
    print(f"ASM part {pdef['name']}: pieces={len(names)} faces={len(ob.data.polygons) if ob.type=='MESH' else 0} pivot={[round(x,3) for x in pivot]}")
    for cdef, _ in child_defs:
        cdef['_parent'] = pdef['name']
        build_part(cdef, root)

for pdef in spec['parts']:
    build_part(pdef, root)
# static remainder
rest = [o for o in bpy.data.objects if o.type == 'MESH' and o.name not in claimed and o.name not in part_objs and o.parent is None]
with bpy.context.temp_override(active_object=rest[0], selected_editable_objects=rest, selected_objects=rest):
    bpy.ops.object.join()
static = rest[0]; static.name = 'Static'; static.data.name = 'Static'
static.parent = root
print(f"ASM static faces={len(static.data.polygons)}")

# ---- decimate to budget ----------------------------------------------------------------------
all_parts = [o for o in bpy.data.objects if o.type == 'MESH']
total = sum(len(o.data.polygons) for o in all_parts)
budget = spec.get('budget', 150000)
if total > budget:
    ratio = budget / total
    for o in all_parts:
        n = len(o.data.polygons)
        if n < 500: continue
        m = o.modifiers.new('dec', 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = max(ratio, 500.0 / n); m.use_collapse_triangulate = True
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o], selected_editable_objects=[o]):
            bpy.ops.object.modifier_apply(modifier='dec')
total2 = sum(len(o.data.polygons) for o in all_parts)
print(f"ASM faces {total} -> {total2}")
for o in all_parts:
    o.data.shade_smooth()
    with bpy.context.temp_override(selected_objects=[o], selected_editable_objects=[o], active_object=o, object=o):
        try: bpy.ops.object.shade_auto_smooth(angle=math.radians(40))
        except Exception as ex: print("auto smooth fail", ex)

# ---- axis markers ---------------------------------------------------------------------------
for nm, v in (('Axis_X', (1, 0, 0)), ('Axis_Y', (0, 1, 0)), ('Axis_Z', (0, 0, 1))):
    e = bpy.data.objects.new(nm, None); bpy.context.scene.collection.objects.link(e); e.location = v; e.parent = root; e.empty_display_size = 0.1
# remove leftovers (cameras etc.)
for o in list(bpy.data.objects):
    if o.type not in ('MESH', 'EMPTY'): bpy.data.objects.remove(o)
bpy.ops.wm.save_as_mainfile(filepath=out_fbx.replace('.fbx', '.blend'))
bpy.ops.export_scene.fbx(filepath=out_fbx, use_selection=False, object_types={'EMPTY', 'MESH'}, apply_unit_scale=True,
                         apply_scale_options='FBX_SCALE_ALL', bake_space_transform=True, axis_forward='-Z', axis_up='Y',
                         mesh_smooth_type='OFF', use_mesh_modifiers=True, add_leaf_bones=False, path_mode='STRIP',
                         use_custom_props=False, use_triangles=True, use_tspace=False, embed_textures=False)
info = {'name': spec['name'], 'rebase': list(rebase), 'scale': S, 'footprint': list((gmax - gmin) * S),
        'parts': {n: {'pivot': list(o.matrix_world.translation), 'parent': part_parent.get(n), 'faces': len(o.data.polygons) if o.type == 'MESH' else 0} for n, o in part_objs.items()},
        'static_faces': len(static.data.polygons)}
json.dump(info, open(out_fbx.replace('.fbx', '.json'), 'w'), indent=1)
print("ASM DONE", out_fbx)
