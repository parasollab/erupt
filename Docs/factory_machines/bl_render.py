# Render views of a prepared .blend. Modes:
#   overview : material colours, all standard views (+ ID maps as BMP)
#   highlight <tag> <view,view,..> <name1> <name2> ... : selected objects red, rest grey
import bpy, sys, json, os, math
from mathutils import Vector
argv = sys.argv[sys.argv.index('--')+1:]
blend, out_dir, name, mode = argv[0], argv[1], argv[2], argv[3]
bpy.ops.wm.open_mainfile(filepath=blend)
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
gmin = Vector((1e9,)*3); gmax = Vector((-1e9,)*3)
for ob in meshes:
    for c in ob.bound_box:
        v = ob.matrix_world @ Vector(c)
        gmin = Vector(map(min, gmin, v)); gmax = Vector(map(max, gmax, v))
scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.render.resolution_x = 1600; scene.render.resolution_y = 1200; scene.render.resolution_percentage = 100
sh = scene.display.shading
sh.light = 'STUDIO'; sh.color_type = 'MATERIAL'; sh.show_cavity = True; sh.show_shadows = False; sh.show_object_outline = True
scene.view_settings.view_transform = 'Standard'; scene.view_settings.look = 'None'
scene.render.dither_intensity = 0.0
scene.world = bpy.data.worlds.new('W'); scene.world.color = (0.85, 0.85, 0.85)
cam_data = bpy.data.cameras.new('cam'); cam = bpy.data.objects.new('cam', cam_data); scene.collection.objects.link(cam); scene.camera = cam
center = (gmin + gmax) / 2; size = gmax - gmin; diag = size.length
def look_at(obj, target):
    obj.rotation_euler = (target - obj.location).to_track_quat('-Z', 'Y').to_euler()
views = {
    'front': (Vector((0, -1, 0)), 'ORTHO'), 'back': (Vector((0, 1, 0)), 'ORTHO'),
    'left': (Vector((-1, 0, 0)), 'ORTHO'), 'right': (Vector((1, 0, 0)), 'ORTHO'),
    'top': (Vector((0, -0.0001, 1)), 'ORTHO'),
    'persp_fl': (Vector((-1, -1.2, 0.7)), 'PERSP'), 'persp_fr': (Vector((1, -1.2, 0.7)), 'PERSP'),
    'persp_bl': (Vector((-1, 1.2, 0.7)), 'PERSP'), 'persp_br': (Vector((1, 1.2, 0.7)), 'PERSP'),
}
def setup_view(v, zoom=None):
    d, typ = views[v]
    cam_data.type = typ
    c = center if zoom is None else Vector(zoom[:3])
    sc = max(size.x, size.y, size.z) * 1.1 if zoom is None else zoom[3]
    if typ == 'ORTHO':
        cam_data.ortho_scale = sc
        cam.location = c + d.normalized() * diag * 2
    else:
        cam_data.lens = 32
        cam.location = c + d.normalized() * (diag * 1.5 if zoom is None else sc * 1.4)
    look_at(cam, c); cam_data.clip_end = diag * 10; cam_data.clip_start = 0.01
def render(path):
    scene.render.filepath = path; bpy.ops.render.render(write_still=True)
scene.render.image_settings.file_format = 'PNG'; scene.render.image_settings.color_mode = 'RGB'

if mode == 'overview':
    for v in views:
        setup_view(v); render(os.path.join(out_dir, f'{name}_{v}.png'))
if mode in ('overview', 'ids'):
    idmap = {}
    for i, ob in enumerate(meshes):
        h = (i + 1) * 2654435761 % (1 << 24)
        r, g, b = (h >> 16) & 255, (h >> 8) & 255, h & 255
        if r + g + b < 90: r, g, b = min(255, r + 90), min(255, g + 90), min(255, b + 90)
        ob.color = (r / 255, g / 255, b / 255, 1.0); idmap[f'{r},{g},{b}'] = ob.name
    json.dump(idmap, open(os.path.join(out_dir, f'{name}_idmap.json'), 'w'))
    sh.light = 'FLAT'; sh.color_type = 'OBJECT'; sh.show_cavity = False; sh.show_object_outline = False
    scene.world.color = (0, 0, 0)
    scene.render.image_settings.file_format = 'BMP'; scene.render.filter_size = 0.0
    scene.view_settings.view_transform = 'Raw'
    for v in views:
        setup_view(v); render(os.path.join(out_dir, f'{name}_{v}_id.bmp'))
elif mode == 'highlight':
    spec = json.load(open(argv[4]))
    idmap = {}
    for i, ob in enumerate(meshes):
        h = (i + 1) * 2654435761 % (1 << 24)
        r, g, b = (h >> 16) & 255, (h >> 8) & 255, h & 255
        if r + g + b < 90: r, g, b = min(255, r + 90), min(255, g + 90), min(255, b + 90)
        idmap[f'{r},{g},{b}'] = ob.name
    json.dump(idmap, open(os.path.join(out_dir, f'{name}_idmap.json'), 'w'))
    for tag, item in spec.items():
        names = set(item['names']); hide = set(item.get('hide', [])); zoom = item.get('zoom')
        for ob in meshes:
            ob.hide_render = ob.name in hide
        # highlight pass
        sh.light = 'STUDIO'; sh.color_type = 'MATERIAL' if item.get('material') else 'OBJECT'; sh.show_cavity = True; sh.show_object_outline = True
        scene.view_settings.view_transform = 'Standard'; scene.world.color = (0.85, 0.85, 0.85)
        scene.render.image_settings.file_format = 'PNG'; scene.render.filter_size = 1.5
        for ob in meshes:
            ob.color = (0.9, 0.1, 0.1, 1) if ob.name in names else (0.82, 0.82, 0.82, 1)
        for v in item['views']:
            setup_view(v, zoom); render(os.path.join(out_dir, f'{name}_H_{tag}_{v}.png'))
        # id pass (same hide/zoom)
        if item.get('ids', True):
            sh.light = 'FLAT'; sh.show_cavity = False; sh.show_object_outline = False
            scene.view_settings.view_transform = 'Raw'; scene.world.color = (0, 0, 0)
            scene.render.image_settings.file_format = 'BMP'; scene.render.filter_size = 0.0
            for k, n in idmap.items():
                r, g, b = map(int, k.split(',')); bpy.data.objects[n].color = (r / 255, g / 255, b / 255, 1)
            for v in item['views']:
                setup_view(v, zoom); render(os.path.join(out_dir, f'{name}_H_{tag}_{v}_id.bmp'))
print("RENDER DONE", name, mode)
