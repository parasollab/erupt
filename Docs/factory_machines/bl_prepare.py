# Blender headless: import a CAD OBJ (cm), planar+collapse decimate per body, save .blend,
# dump per-object stats, render overview + ID images.
import bpy, sys, json, math, os, time
from mathutils import Vector
argv = sys.argv[sys.argv.index('--')+1:]
obj_path, out_dir, name = argv[0], argv[1], argv[2]
target_faces = int(argv[3]) if len(argv) > 3 else 150000
os.makedirs(out_dir, exist_ok=True)
t0 = time.time()
def log(*a): print(f"[prep {name} {time.time()-t0:6.0f}s]", *a, flush=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=obj_path, global_scale=0.01, use_split_objects=True, use_split_groups=True,
                      forward_axis='NEGATIVE_Z', up_axis='Y', validate_meshes=True)
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
for o in [o for o in bpy.data.objects if o.type != 'MESH']:
    bpy.data.objects.remove(o)
total0 = sum(len(o.data.polygons) for o in meshes)
log("imported", len(meshes), "objects", total0, "faces")

import bmesh
for ob in meshes:
    me = ob.data
    bm = bmesh.new(); bm.from_mesh(me)
    if len(bm.faces) > 3:
        bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(3.0), use_dissolve_boundaries=False,
                                 verts=bm.verts[:], edges=bm.edges[:], delimit={'MATERIAL'})
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me); bm.free()
total1 = sum(len(o.data.polygons) for o in meshes)
log("after planar dissolve", total1, "faces (collapse deferred to part assembly)")
for ob in meshes:
    ob.data.shade_smooth()
try:
    with bpy.context.temp_override(selected_objects=meshes, selected_editable_objects=meshes, active_object=meshes[0], object=meshes[0]):
        bpy.ops.object.shade_auto_smooth(angle=math.radians(35.0))
except Exception as e:
    log("auto smooth failed", e)

# stats
stats = []
gmin = Vector((1e9,)*3); gmax = Vector((-1e9,)*3)
for i, ob in enumerate(meshes):
    bb = [ob.matrix_world @ Vector(c) for c in ob.bound_box]
    mn = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
    mx = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
    gmin = Vector(map(min, gmin, mn)); gmax = Vector(map(max, gmax, mx))
    mats = [s.material.name if s.material else None for s in ob.material_slots]
    stats.append({'name': ob.name, 'faces': len(ob.data.polygons), 'min': list(mn), 'max': list(mx),
                  'size': list(mx - mn), 'center': list((mx + mn) / 2), 'mats': mats})
json.dump({'objects': stats, 'bbox_min': list(gmin), 'bbox_max': list(gmax)}, open(os.path.join(out_dir, f'{name}_objects.json'), 'w'))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out_dir, f'{name}.blend'))
log("saved blend; bbox", list(gmin), list(gmax))

# ---- rendering
scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.render.resolution_x = 1600; scene.render.resolution_y = 1200; scene.render.resolution_percentage = 100
scene.display.shading.light = 'STUDIO'; scene.display.shading.color_type = 'MATERIAL'
scene.display.shading.show_cavity = True; scene.display.shading.show_shadows = False
scene.display.shading.show_object_outline = True
scene.display_settings.display_device = 'sRGB'
scene.view_settings.view_transform = 'Standard'; scene.view_settings.look = 'NONE'
scene.render.dither_intensity = 0.0
scene.world = bpy.data.worlds.new('W'); scene.world.color = (0.9, 0.9, 0.9)
cam_data = bpy.data.cameras.new('cam'); cam = bpy.data.objects.new('cam', cam_data); scene.collection.objects.link(cam); scene.camera = cam
center = (gmin + gmax) / 2; size = gmax - gmin; diag = size.length

def look_at(obj, target):
    d = target - obj.location
    obj.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()

views = {
    'front':  (Vector((0, -1, 0)), 'ORTHO'), 'back': (Vector((0, 1, 0)), 'ORTHO'),
    'left':   (Vector((-1, 0, 0)), 'ORTHO'), 'right': (Vector((1, 0, 0)), 'ORTHO'),
    'top':    (Vector((0, -0.0001, 1)), 'ORTHO'),
    'persp_fl': (Vector((-1, -1.2, 0.7)), 'PERSP'), 'persp_fr': (Vector((1, -1.2, 0.7)), 'PERSP'),
    'persp_bl': (Vector((-1, 1.2, 0.7)), 'PERSP'), 'persp_br': (Vector((1, 1.2, 0.7)), 'PERSP'),
}
def setup_view(v):
    d, typ = views[v]
    cam_data.type = typ
    if typ == 'ORTHO':
        cam_data.ortho_scale = max(size.x, size.y, size.z) * 1.15
        cam.location = center + d.normalized() * diag * 2
    else:
        cam_data.lens = 35
        cam.location = center + d.normalized() * diag * 1.6
    look_at(cam, center)
    cam_data.clip_end = diag * 10

def render(path):
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

scene.render.image_settings.file_format = 'PNG'
for v in views:
    setup_view(v); render(os.path.join(out_dir, f'{name}_{v}.png'))
log("rendered material views")

# ID render: unique flat colour per object
idmap = {}
for i, ob in enumerate(meshes):
    h = (i + 1) * 2654435761 % (1 << 24)
    r, g, b = (h >> 16) & 255, (h >> 8) & 255, h & 255
    if r + g + b < 60: r, g, b = r + 80, g + 80, b + 80
    ob.color = (r / 255, g / 255, b / 255, 1.0)
    idmap[f'{r},{g},{b}'] = ob.name
json.dump(idmap, open(os.path.join(out_dir, f'{name}_idmap.json'), 'w'))
scene.display.shading.light = 'FLAT'; scene.display.shading.color_type = 'OBJECT'
scene.display.shading.show_cavity = False; scene.display.shading.show_object_outline = False
scene.world.color = (0, 0, 0)
scene.render.image_settings.file_format = 'BMP'
scene.render.image_settings.color_mode = 'RGB'
scene.render.filter_size = 0.0
for v in views:
    setup_view(v); render(os.path.join(out_dir, f'{name}_{v}_id.bmp'))
log("rendered id views; done")
