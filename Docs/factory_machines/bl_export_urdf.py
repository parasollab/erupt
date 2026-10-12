# Blender headless: assembled machine .blend + joint spec -> URDF package files.
#   Blender -b --python bl_export_urdf.py -- urdf_spec_<m>.json <package_dir>
# Writes <package_dir>/urdf/<machine>.urdf, meshes/<machine>/visual/<link>.dae (existing decimated
# mesh, per-face CAD colours) and meshes/<machine>/collision/<link>.stl (further decimated).
# Each mesh is exported in its link (pivot-local) frame; joint origins are pivot differences.
import bpy, sys, json, os, math
import xml.etree.ElementTree as ET
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index('--') + 1:]
spec_path, pkg_dir = os.path.abspath(argv[0]), os.path.abspath(argv[1])
spec = json.load(open(spec_path))
spec_dir = os.path.dirname(spec_path)
machine = spec['machine']
PKG = os.path.basename(pkg_dir)
vis_dir = os.path.join(pkg_dir, 'meshes', machine, 'visual')
col_dir = os.path.join(pkg_dir, 'meshes', machine, 'collision')
urdf_dir = os.path.join(pkg_dir, 'urdf')
for d in (vis_dir, col_dir, urdf_dir):
    os.makedirs(d, exist_ok=True)

def log(*a): print("URDF", *a, flush=True)

# ---- validate spec ---------------------------------------------------------------------------
links, joints = spec['links'], spec['joints']
assert 'base_link' in links, "spec needs a base_link"
children = {}
for j in joints:
    assert j['parent'] in links and j['child'] in links, f"joint {j['name']} references unknown link"
    assert j['child'] not in children, f"link {j['child']} has two parent joints"
    children[j['child']] = j
    assert j['type'] in ('prismatic', 'revolute', 'continuous', 'fixed'), j['type']
    ax = Vector(j.get('axis', (0, 0, 1)))
    assert ax.length > 1e-6, f"joint {j['name']} has a zero axis"
    j['axis'] = list(ax.normalized())
    if j['type'] in ('prismatic', 'revolute'):
        assert j['lower'] <= j['upper'], f"joint {j['name']} lower > upper"
    if 'mimic' in j:
        assert any(k['name'] == j['mimic']['joint'] for k in joints), f"mimic target missing for {j['name']}"
for l in links:
    if l != 'base_link':
        assert l in children, f"link {l} has no parent joint"
        # cycle check: walk to root
        seen, cur = set(), l
        while cur != 'base_link':
            assert cur not in seen, f"cycle at {cur}"
            seen.add(cur); cur = children[cur]['parent']
names = [j['name'] for j in joints] + list(links)
assert len(names) == len(set(names)), "duplicate link/joint names"

# ---- load blend ------------------------------------------------------------------------------
bpy.ops.wm.open_mainfile(filepath=os.path.join(spec_dir, spec['blend']))
assert abs(bpy.context.scene.unit_settings.scale_length - 1.0) < 1e-6, "scene must be in metres"

# Collada export reads the Principled BSDF when the material uses nodes; make that path explicit.
for m in bpy.data.materials:
    col = tuple(m.diffuse_color)
    rough, metal = m.roughness, m.metallic
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get('Principled BSDF')
    if bsdf is None:
        bsdf = m.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        out = m.node_tree.nodes.get('Material Output') or m.node_tree.nodes.new('ShaderNodeOutputMaterial')
        m.node_tree.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    bsdf.inputs['Base Color'].default_value = col
    bsdf.inputs['Alpha'].default_value = col[3]
    bsdf.inputs['Roughness'].default_value = rough
    bsdf.inputs['Metallic'].default_value = metal
    m.diffuse_color = col

def select_only(obj):
    for o in bpy.data.objects:
        o.select_set(False)
    obj.hide_set(False); obj.hide_viewport = False
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

def box_inertia(obj, mass):
    bb = [Vector(c) for c in obj.bound_box]
    mn = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
    mx = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
    d, c = mx - mn, (mx + mn) / 2
    return c, (mass / 12 * (d.y ** 2 + d.z ** 2), mass / 12 * (d.x ** 2 + d.z ** 2), mass / 12 * (d.x ** 2 + d.y ** 2)), (mn, mx)

# ---- export meshes ---------------------------------------------------------------------------
pivots = {'base_link': Vector((0, 0, 0))}
info = {}
depsgraph = bpy.context.evaluated_depsgraph_get()
for lname, ldef in links.items():
    mesh_name = ldef.get('mesh')
    if mesh_name is None:
        src = bpy.data.objects[ldef['pivot_of']]
        pivots[lname] = src.matrix_world.translation.copy()
        info[lname] = {'virtual': True}
        continue
    obj = bpy.data.objects[mesh_name]
    assert obj.type == 'MESH', mesh_name
    eul = obj.matrix_world.to_euler()
    assert max(abs(eul.x), abs(eul.y), abs(eul.z)) < 1e-4, f"{mesh_name} is rotated; pivot-only transforms expected"
    pivots[lname] = obj.matrix_world.translation.copy()
    # link frame = pivot frame: drop the world transform so exporters write mesh-local coordinates
    obj.parent = None
    obj.matrix_world = Matrix.Identity(4)
    bpy.context.view_layer.update()
    select_only(obj)
    dae = os.path.join(vis_dir, lname + '.dae')
    bpy.ops.wm.collada_export(filepath=dae, selected=True, include_children=False, apply_modifiers=False,
                              export_mesh_type_selection='view', export_global_forward_selection='Y',
                              export_global_up_selection='Z', apply_global_orientation=False, triangulate=True,
                              use_object_instantiation=True, use_blender_profile=False, include_animations=False,
                              include_armatures=False, include_shapekeys=False, limit_precision=True,
                              sort_by_name=True, use_texture_copies=False)
    n_vis = len(obj.data.polygons)
    budget = int(ldef.get('collision_faces', spec.get('collision_faces', 3000)))
    method = ldef.get('collision_method', 'collapse')
    if method == 'voxel':
        # Sheet-metal enclosures collapse into spikes under plain edge-collapse decimation. Thicken the
        # shells (solidify), voxel-remesh into one manifold surface, then decimate that.
        vox = float(ldef.get('voxel_size', 0.03))
        col_obj = obj.copy(); col_obj.data = obj.data.copy(); bpy.context.scene.collection.objects.link(col_obj)
        so = col_obj.modifiers.new('urdf_sol', 'SOLIDIFY'); so.thickness = 2 * vox; so.offset = 0.0
        rm = col_obj.modifiers.new('urdf_rm', 'REMESH'); rm.mode = 'VOXEL'; rm.voxel_size = vox; rm.use_remove_disconnected = False
        tri = col_obj.modifiers.new('urdf_tri', 'TRIANGULATE')
        for mname in ('urdf_sol', 'urdf_rm', 'urdf_tri'):
            with bpy.context.temp_override(object=col_obj, active_object=col_obj, selected_objects=[col_obj], selected_editable_objects=[col_obj]):
                bpy.ops.object.modifier_apply(modifier=mname)
        n_rm = len(col_obj.data.polygons)
        dec = col_obj.modifiers.new('urdf_col', 'DECIMATE')
        dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1.0, budget / max(1, n_rm)); dec.use_collapse_triangulate = True
        export_obj = col_obj
    else:
        dec = obj.modifiers.new('urdf_col', 'DECIMATE')
        dec.decimate_type = 'COLLAPSE'; dec.ratio = min(1.0, budget / max(1, n_vis)); dec.use_collapse_triangulate = True
        export_obj = obj
    select_only(export_obj)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    n_col = len(export_obj.evaluated_get(depsgraph).data.polygons)
    stl = os.path.join(col_dir, lname + '.stl')
    bpy.ops.wm.stl_export(filepath=stl, export_selected_objects=True, apply_modifiers=True, ascii_format=False,
                          global_scale=1.0, use_scene_unit=False, forward_axis='Y', up_axis='Z', use_batch=False)
    if export_obj is obj:
        obj.modifiers.remove(dec)
    else:
        bpy.data.objects.remove(export_obj)
    select_only(obj)
    c, I, (mn, mx) = box_inertia(obj, float(ldef.get('mass', 1.0)))
    info[lname] = {'mesh': mesh_name, 'visual_faces': n_vis, 'collision_faces': n_col,
                   'dae_bytes': os.path.getsize(dae), 'stl_bytes': os.path.getsize(stl),
                   'pivot': list(pivots[lname]), 'bbox_min': list(mn), 'bbox_max': list(mx),
                   'com': c, 'inertia': I, 'mass': float(ldef.get('mass', 1.0))}
    log(f"{lname:18s} vis={n_vis:7d} col={n_col:6d} ({method}) dae={info[lname]['dae_bytes']/1e6:6.2f}MB stl={info[lname]['stl_bytes']/1e6:5.2f}MB pivot={[round(x,3) for x in pivots[lname]]}")

# ---- write URDF ------------------------------------------------------------------------------
def f3(v): return ' '.join(f"{x:.6g}" for x in v)
robot = ET.Element('robot', name=machine)
robot.append(ET.Comment(f" Generated by Docs/factory_machines/bl_export_urdf.py from {os.path.basename(spec_path)}. "
                        "Masses and inertias are nominal box estimates; joint limits follow the FactoryDemo prefab animation. "))
for lname, ldef in links.items():
    L = ET.SubElement(robot, 'link', name=lname)
    inf = info[lname]
    inertial = ET.SubElement(L, 'inertial')
    if inf.get('virtual'):
        ET.SubElement(inertial, 'mass', value=f"{float(ldef.get('mass', 1.0)):.6g}")
        ET.SubElement(inertial, 'inertia', ixx='0.001', ixy='0', ixz='0', iyy='0.001', iyz='0', izz='0.001')
        continue
    ET.SubElement(inertial, 'origin', xyz=f3(inf['com']), rpy='0 0 0')
    ET.SubElement(inertial, 'mass', value=f"{inf['mass']:.6g}")
    ixx, iyy, izz = inf['inertia']
    ET.SubElement(inertial, 'inertia', ixx=f"{ixx:.6g}", ixy='0', ixz='0', iyy=f"{iyy:.6g}", iyz='0', izz=f"{izz:.6g}")
    vis = ET.SubElement(L, 'visual')
    ET.SubElement(vis, 'origin', xyz='0 0 0', rpy='0 0 0')
    ET.SubElement(ET.SubElement(vis, 'geometry'), 'mesh', filename=f"package://{PKG}/meshes/{machine}/visual/{lname}.dae")
    col = ET.SubElement(L, 'collision')
    ET.SubElement(col, 'origin', xyz='0 0 0', rpy='0 0 0')
    ET.SubElement(ET.SubElement(col, 'geometry'), 'mesh', filename=f"package://{PKG}/meshes/{machine}/collision/{lname}.stl")
for j in joints:
    J = ET.SubElement(robot, 'joint', name=j['name'], type=j['type'])
    xyz = Vector(j['origin_xyz']) if 'origin_xyz' in j else pivots[j['child']] - pivots[j['parent']]
    rpy = j.get('origin_rpy', (0, 0, 0))
    ET.SubElement(J, 'origin', xyz=f3(xyz), rpy=f3(rpy))
    ET.SubElement(J, 'parent', link=j['parent'])
    ET.SubElement(J, 'child', link=j['child'])
    if j['type'] != 'fixed':
        ET.SubElement(J, 'axis', xyz=f3(j['axis']))
        lim = {'effort': f"{float(j.get('effort', 100)):.6g}", 'velocity': f"{float(j.get('velocity', 1.0)):.6g}"}
        if j['type'] in ('prismatic', 'revolute'):
            lim['lower'] = f"{float(j['lower']):.6g}"; lim['upper'] = f"{float(j['upper']):.6g}"
        ET.SubElement(J, 'limit', **lim)
        if 'mimic' in j:
            m = j['mimic']
            ET.SubElement(J, 'mimic', joint=m['joint'], multiplier=f"{float(m.get('multiplier', 1.0)):.6g}", offset=f"{float(m.get('offset', 0.0)):.6g}")
ET.indent(robot, space='  ')
urdf_path = os.path.join(urdf_dir, machine + '.urdf')
with open(urdf_path, 'w') as fh:
    fh.write('<?xml version="1.0"?>\n')
    fh.write(ET.tostring(robot, encoding='unicode'))
    fh.write('\n')

total_dae = sum(i.get('dae_bytes', 0) for i in info.values()); total_stl = sum(i.get('stl_bytes', 0) for i in info.values())
log(f"wrote {urdf_path}: {len(links)} links, {len(joints)} joints, dae total {total_dae/1e6:.1f}MB, stl total {total_stl/1e6:.1f}MB")
json.dump({'machine': machine, 'links': {k: {kk: (list(vv) if isinstance(vv, (Vector, tuple)) else vv) for kk, vv in v.items()} for k, v in info.items()},
           'pivots': {k: list(v) for k, v in pivots.items()}},
          open(os.path.join(spec_dir, 'prep', machine + '.export.json'), 'w'), indent=1)
log("DONE", machine)
