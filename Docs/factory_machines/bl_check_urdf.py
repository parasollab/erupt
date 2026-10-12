# Blender headless URDF check (no ROS needed):
#   Blender -b --python bl_check_urdf.py -- <package_dir> <machine> <out_dir> [urdf_spec.json]
# Static checks (XML, mesh paths, tree, limits, DAE/STL headers), then re-imports every link's DAE and
# STL, builds the kinematic tree from the URDF, poses the joints (zero / lower / mid / upper / spec
# check_poses) and renders Workbench views so joint directions and mesh frames can be eyeballed.
import bpy, sys, os, json, math, struct, re
import xml.etree.ElementTree as ET
from mathutils import Vector, Matrix, Euler

argv = sys.argv[sys.argv.index('--') + 1:]
pkg_dir, machine, out_dir = os.path.abspath(argv[0]), argv[1], os.path.abspath(argv[2])
spec = json.load(open(argv[3])) if len(argv) > 3 else {}
os.makedirs(out_dir, exist_ok=True)
PKG = os.path.basename(pkg_dir)
problems = []
def log(*a): print("CHK", *a, flush=True)
def bad(msg): problems.append(msg); log("PROBLEM", msg)

# ---- static checks ---------------------------------------------------------------------------
urdf_path = os.path.join(pkg_dir, 'urdf', machine + '.urdf')
root = ET.parse(urdf_path).getroot()
assert root.tag == 'robot'
links = {l.get('name'): l for l in root.findall('link')}
joints = [j for j in root.findall('joint')]
jnames = [j.get('name') for j in joints]
if len(set(jnames)) != len(jnames): bad("duplicate joint names")
if len(set(links)) != len(links): bad("duplicate link names")
parent_of = {}
for j in joints:
    p, c = j.find('parent').get('link'), j.find('child').get('link')
    if p not in links or c not in links: bad(f"{j.get('name')} references unknown link")
    if c in parent_of: bad(f"{c} has two parent joints")
    parent_of[c] = j
    lim = j.find('limit')
    if j.get('type') in ('prismatic', 'revolute'):
        if lim is None or float(lim.get('lower')) > float(lim.get('upper')): bad(f"{j.get('name')} bad limits")
    mim = j.find('mimic')
    if mim is not None and mim.get('joint') not in jnames: bad(f"{j.get('name')} mimics unknown joint")
roots = [l for l in links if l not in parent_of]
if roots != ['base_link']: bad(f"root links: {roots}")
for l in links:
    seen, cur = set(), l
    while cur in parent_of:
        if cur in seen: bad(f"cycle at {cur}"); break
        seen.add(cur); cur = parent_of[cur].find('parent').get('link')

def resolve(uri):
    assert uri.startswith(f"package://{PKG}/"), uri
    return os.path.join(pkg_dir, uri[len(f"package://{PKG}/"):])
mesh_files = {}
for lname, L in links.items():
    v = L.find('visual/geometry/mesh'); c = L.find('collision/geometry/mesh')
    mesh_files[lname] = (resolve(v.get('filename')) if v is not None else None, resolve(c.get('filename')) if c is not None else None)
    for f in mesh_files[lname]:
        if f and not os.path.exists(f): bad(f"missing mesh {f}")
    if mesh_files[lname][0]:
        head = open(mesh_files[lname][0], 'rb').read(4000).decode('utf-8', 'ignore')
        if '<up_axis>Z_UP</up_axis>' not in head: bad(f"{lname}.dae not Z_UP")
        if 'meter="1"' not in head: bad(f"{lname}.dae unit not metre")
        txt = open(mesh_files[lname][0], 'r', errors='ignore').read(200000)
        tr = re.findall(r'<transparent opaque="A_ONE">\s*<color[^>]*>([^<]+)</color>', txt)
        if tr: log(f"{lname}.dae transparent colours (A_ONE, alpha = opacity): {tr}")
    if mesh_files[lname][1]:
        with open(mesh_files[lname][1], 'rb') as fh:
            fh.seek(80); n = struct.unpack('<I', fh.read(4))[0]
        if os.path.getsize(mesh_files[lname][1]) != 84 + 50 * n: bad(f"{lname}.stl size mismatch (ascii?)")
log(f"static checks done: {len(links)} links, {len(joints)} joints, problems={len(problems)}")

# ---- import meshes ---------------------------------------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
vis_col = bpy.data.collections.new('visual'); col_col = bpy.data.collections.new('collision')
scene.collection.children.link(vis_col); scene.collection.children.link(col_col)
link_empty, vis_objs, col_objs = {}, {}, {}
stats = {}
def import_into(path, kind, lname):
    before = set(bpy.data.objects)
    if kind == 'dae':
        bpy.ops.wm.collada_import(filepath=path, import_units=False, custom_normals=True, fix_orientation=False)
    else:
        bpy.ops.wm.stl_import(filepath=path, global_scale=1.0, use_scene_unit=False, forward_axis='Y', up_axis='Z')
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    for o in new:
        m = o.matrix_world
        if (m - Matrix.Identity(4)).to_3x3().row[0].length > 1e-4 or max(abs(x) for x in m.translation) > 1e-4 or (m.to_3x3() - Matrix.Identity(3)).determinant() > 1e-6:
            if any(abs(m[i][j] - (1 if i == j else 0)) > 1e-4 for i in range(4) for j in range(4)):
                bad(f"{lname} {kind}: imported object {o.name} has non-identity transform {[list(r) for r in m]}")
    for o in new:
        if o.type != 'MESH': bpy.data.objects.remove(o)
    if not meshes: bad(f"{lname} {kind}: nothing imported"); return None
    if len(meshes) > 1:
        with bpy.context.temp_override(active_object=meshes[0], selected_editable_objects=meshes, selected_objects=meshes):
            bpy.ops.object.join()
    ob = meshes[0]; ob.name = f"{'V' if kind == 'dae' else 'C'}_{lname}"
    for c in list(ob.users_collection): c.objects.unlink(ob)
    (vis_col if kind == 'dae' else col_col).objects.link(ob)
    return ob
for lname in links:
    e = bpy.data.objects.new('L_' + lname, None); scene.collection.objects.link(e); e.empty_display_size = 0.15
    link_empty[lname] = e
    dae, stl = mesh_files[lname]
    st = {}
    if dae:
        ob = import_into(dae, 'dae', lname)
        if ob:
            vis_objs[lname] = ob; st['visual_faces'] = len(ob.data.polygons)
            bb = [Vector(c) for c in ob.bound_box]; st['visual_bbox'] = [[round(min(v[i] for v in bb), 3) for i in range(3)], [round(max(v[i] for v in bb), 3) for i in range(3)]]
            for slot in ob.material_slots:
                m = slot.material
                if m and m.use_nodes and m.node_tree.nodes.get('Principled BSDF'):
                    b = m.node_tree.nodes['Principled BSDF']
                    col = list(b.inputs['Base Color'].default_value); col[3] = b.inputs['Alpha'].default_value
                    m.diffuse_color = col
                    if col[3] < 0.999: m.surface_render_method = 'BLENDED'
    if stl:
        ob = import_into(stl, 'stl', lname)
        if ob:
            col_objs[lname] = ob; st['collision_faces'] = len(ob.data.polygons)
            bb = [Vector(c) for c in ob.bound_box]; st['collision_bbox'] = [[round(min(v[i] for v in bb), 3) for i in range(3)], [round(max(v[i] for v in bb), 3) for i in range(3)]]
            if 'visual_bbox' in st:
                dv = max(abs(st['visual_bbox'][k][i] - st['collision_bbox'][k][i]) for k in range(2) for i in range(3))
                st['bbox_delta'] = round(dv, 3)
                if dv > 0.05: bad(f"{lname}: collision bbox differs from visual by {dv:.3f} m")
    for ob in (vis_objs.get(lname), col_objs.get(lname)):
        if ob: ob.parent = link_empty[lname]; ob.matrix_parent_inverse = Matrix.Identity(4); ob.matrix_basis = Matrix.Identity(4)
    stats[lname] = st
    log(lname, st)

# ---- kinematics -------------------------------------------------------------------------------
jdef = {}
for j in joints:
    o = j.find('origin'); ax = j.find('axis'); lim = j.find('limit'); mim = j.find('mimic')
    jdef[j.get('name')] = {
        'type': j.get('type'), 'parent': j.find('parent').get('link'), 'child': j.find('child').get('link'),
        'xyz': Vector([float(x) for x in (o.get('xyz', '0 0 0') if o is not None else '0 0 0').split()]),
        'rpy': [float(x) for x in (o.get('rpy', '0 0 0') if o is not None else '0 0 0').split()],
        'axis': Vector([float(x) for x in ax.get('xyz').split()]) if ax is not None else Vector((0, 0, 1)),
        'lower': float(lim.get('lower')) if lim is not None and lim.get('lower') else None,
        'upper': float(lim.get('upper')) if lim is not None and lim.get('upper') else None,
        'mimic': (mim.get('joint'), float(mim.get('multiplier', 1)), float(mim.get('offset', 0))) if mim is not None else None}
step_deg = {j['name']: j.get('step_deg', 45.0) for j in spec.get('joints', [])}

def pose(q):
    # resolve mimics
    qq = dict(q)
    for n, d in jdef.items():
        if d['mimic']:
            t, k, off = d['mimic']; qq[n] = k * qq.get(t, 0.0) + off
    done = {'base_link': Matrix.Identity(4)}
    pending = dict(jdef)
    while pending:
        progressed = False
        for n, d in list(pending.items()):
            if d['parent'] in done:
                v = qq.get(n, 0.0)
                if d['type'] == 'prismatic': M = Matrix.Translation(d['axis'] * v)
                elif d['type'] in ('revolute', 'continuous'): M = Matrix.Rotation(v, 4, d['axis'])
                else: M = Matrix.Identity(4)
                done[d['child']] = done[d['parent']] @ Matrix.Translation(d['xyz']) @ Euler(d['rpy'], 'XYZ').to_matrix().to_4x4() @ M
                del pending[n]; progressed = True
        assert progressed, "disconnected joints"
    for l, T in done.items():
        link_empty[l].matrix_world = T
    bpy.context.view_layer.update()

poses = {'zero': {}}
lower = {n: d['lower'] for n, d in jdef.items() if d['lower'] is not None and not d['mimic']}
upper = {n: d['upper'] for n, d in jdef.items() if d['upper'] is not None and not d['mimic']}
cont = {n: math.radians(step_deg.get(n, 45.0)) for n, d in jdef.items() if d['type'] == 'continuous'}
poses['lower'] = dict(lower)
poses['upper'] = dict(upper, **cont)
poses['mid'] = {n: (lower[n] + upper[n]) / 2 for n in lower}; poses['mid'].update({n: 2 * v for n, v in cont.items()})
for name, q in spec.get('check_poses', {}).items():
    poses[name] = dict(q)

# ---- rendering (bl_render.py style) ------------------------------------------------------------
scene.render.engine = 'BLENDER_WORKBENCH'
scene.render.resolution_x = 1400; scene.render.resolution_y = 1050; scene.render.resolution_percentage = 100
scene.display.shading.light = 'STUDIO'; scene.display.shading.color_type = 'MATERIAL'
scene.display.shading.show_cavity = True; scene.display.shading.show_shadows = False
scene.display.shading.show_object_outline = True
scene.view_settings.view_transform = 'Standard'
scene.world = bpy.data.worlds.new('W'); scene.world.color = (0.9, 0.9, 0.9)
cam_data = bpy.data.cameras.new('cam'); cam = bpy.data.objects.new('cam', cam_data); scene.collection.objects.link(cam); scene.camera = cam
pose({})
allv = [v for ob in vis_objs.values() for v in [ob.matrix_world @ Vector(c) for c in ob.bound_box]]
gmin = Vector((min(v.x for v in allv), min(v.y for v in allv), min(v.z for v in allv)))
gmax = Vector((max(v.x for v in allv), max(v.y for v in allv), max(v.z for v in allv)))
center = (gmin + gmax) / 2; size = gmax - gmin; diag = size.length
# axis gizmo at the base so X/Y/Z are visible in renders
for nm, v, colr in (('gx', (1, 0, 0), (1, 0, 0, 1)), ('gy', (0, 1, 0), (0, 1, 0, 1)), ('gz', (0, 0, 1), (0, 0, 1, 1))):
    me = bpy.data.meshes.new(nm); import bmesh; bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=8, radius1=0.02, radius2=0.02, depth=0.8)
    bmesh.ops.translate(bm, vec=(0, 0, 0.4), verts=bm.verts)
    if v[0]: bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, 'Y'), verts=bm.verts)
    if v[1]: bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(-90), 3, 'X'), verts=bm.verts)
    bm.to_mesh(me); bm.free(); m = bpy.data.materials.new('M' + nm); m.diffuse_color = colr; me.materials.append(m)
    ob = bpy.data.objects.new(nm, me); scene.collection.objects.link(ob); ob.color = colr
def look_at(obj, target):
    obj.rotation_euler = (target - obj.location).to_track_quat('-Z', 'Y').to_euler()
views = {'front': (Vector((0, -1, 0)), 'ORTHO'), 'top': (Vector((0, -0.0001, 1)), 'ORTHO'),
         'persp_fl': (Vector((-1, -1.2, 0.7)), 'PERSP'), 'persp_fr': (Vector((1, -1.2, 0.7)), 'PERSP')}
def setup_view(v):
    d, typ = views[v]; cam_data.type = typ
    if typ == 'ORTHO':
        cam_data.ortho_scale = max(size.x, size.y, size.z) * 1.3; cam.location = center + d.normalized() * diag * 2
    else:
        cam_data.lens = 35; cam.location = center + d.normalized() * diag * 1.5
    look_at(cam, center); cam_data.clip_end = diag * 10
def render(path):
    scene.render.filepath = path; bpy.ops.render.render(write_still=True)
scene.render.image_settings.file_format = 'PNG'
col_col.hide_render = True
for pname, q in poses.items():
    pose(q)
    for v in views:
        setup_view(v); render(os.path.join(out_dir, f"{machine}_{pname}_{v}.png"))
    log("rendered pose", pname, {k: round(x, 3) for k, x in q.items()})
# collision pass: flat per-link colours
pose(poses.get('open', poses['upper']))
vis_col.hide_render = True; col_col.hide_render = False
scene.display.shading.light = 'FLAT'; scene.display.shading.color_type = 'OBJECT'
for i, ob in enumerate(col_objs.values()):
    h = (i + 1) * 2654435761 % (1 << 24); ob.color = (((h >> 16) & 255) / 255, ((h >> 8) & 255) / 255, (h & 255) / 255, 1)
for v in ('persp_fr', 'front'):
    setup_view(v); render(os.path.join(out_dir, f"{machine}_collision_{v}.png"))
json.dump({'machine': machine, 'problems': problems, 'links': stats, 'poses': {k: {n: round(x, 4) for n, x in q.items()} for k, q in poses.items()}},
          open(os.path.join(out_dir, f"{machine}_check.json"), 'w'), indent=1)
log("DONE", machine, "problems:", len(problems))
