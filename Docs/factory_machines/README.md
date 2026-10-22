# Factory machine models (FactoryDemo scene)

`Assets/Scenes/Demo Scenes/FactoryDemo.unity` is the ReachabilityVR demo rig (FR3 on a pedestal, VR camera
rig, wrist menus, MTC pick/place stack) laid out as a one-machine work cell around a Haas VF-2TR (VF-2 with
the TRT-160 trunnion):

| Prefab (`Assets/Prefabs/Factory`) | Source (Desktop) | Parts |
|---|---|---|
| HaasVF2TR | `vf-2_solid_models_1_2023/VF-2TR_STEP__1_2023.STEP` (SolidWorks STEP AP214, inches, named assemblies) | DoorLeft/DoorRight (slide 0.48 m each, driven together by `Factory.MachineDoors`); Trunnion and Platter are separate static parts (the fixture block is placed on the platter); everything else is `Static` |

The prefab also carries renderer-less collision meshes under each part (from `HaasVF2TR_collision.fbx`) with
`CollisionObjectPublisher`s `vf2_body`, `vf2_door_left`, `vf2_door_right`, `vf2_trunnion`, `vf2_platter`, so
MoveIt plans around the enclosure and the doors follow in the planning scene as they slide. The doors are
opened and closed from the `MachineDoorMenu` world-space panel (`MachineDoorPanel.uxml` +
`Factory.MachineDoorPanel`, an instance of the planning-menu prefab like the MTC dashboard) standing just
outside the machine's front-right corner.

The four machines of the earlier hall (HaasVF1, HaasST10, HaasEC500, InjectionMolder, with their
`Factory.*Cycle` components) remain as prefabs and in the URDF package but are no longer in the scene.
`FactorySceneBuilder.cs.txt` is the archived one-shot script that built them. The plain VF-2 (built from a
Fusion FBX export, `spec_vf2.json` / `urdf_spec_vf2.json`) was replaced by the TR on 2026-10-14; its URDF
stays in the package, its Unity assets were removed.

## Rebuilding the VF-2TR (OpenCASCADE + Blender 4.3, headless)

STEP goes through `step_to_obj.py` (needs the `cadquery-ocp` wheel: `python3.10 -m venv occ &&
occ/bin/pip install cadquery-ocp`). It reads the STEP with the XCAF document reader, so assembly names and
face colours survive, tessellates every solid at a fixed tolerance (2 mm / 0.5 rad gives ~450k triangles for
the whole machine, clean enough that no decimation is needed) and writes one OBJ object per leaf solid named
`<enclosing product>|<leaf product>` with materials `Opaque(r,g,b)`, in metres, Y-up as SolidWorks stores it.

```
B=/Applications/Blender.app/Contents/MacOS/Blender
PKG=~/VR/erupt_ws/src/factory_machines_description
occ/bin/python step_to_obj.py ~/Desktop/vf-2_solid_models_1_2023/VF-2TR_STEP__1_2023.STEP /tmp/tr/vf2tr.obj --lin 2.0 --ang 0.5
$B -b --python bl_prepare.py  -- /tmp/tr/vf2tr.obj prep vf2tr 500000 --scale 1.0
$B -b --python bl_assemble.py -- spec_vf2tr.json /tmp/asm/HaasVF2TR.fbx
cp /tmp/asm/HaasVF2TR.fbx "../../Assets/Models/Factory Machines/HaasVF2TR.fbx"
$B -b /tmp/asm/HaasVF2TR.blend --python-expr "import bpy; bpy.ops.wm.save_as_mainfile(filepath='$PWD/prep/vf2tr.blend', compress=True)"
$B -b --python bl_export_urdf.py -- urdf_spec_vf2tr.json $PKG   # URDF + meshes + HaasVF2TR_collision.fbx
$B -b --python bl_check_urdf.py  -- $PKG haas_vf2tr /tmp/chk urdf_spec_vf2tr.json
```

`spec_vf2tr.json`: `origin` puts the machine origin at the main enclosure's footprint centre on the floor
(chip chute and pendant stick out of it); `glass_materials` turns the door and side-door window materials into
`Glass` (remapped to `MachineGlass` in Unity); `drop` leaves out the chip-conveyor holder/shelf brackets
(0.5 m out of the front-left at head height) and the 160 mm platter the CAD keeps inside the 250 mm one;
`adjust` lowers the side doors 0.62 m (the CAD stores them raised, glass above the roof and the 0.3-1.1 m side
openings exposed) and jogs the saddle, table, trunnion and platter 0.25 m toward the doors (the machine's Y
travel) so the platter is within the FR3's reach. `prep/vf2tr.blend` is the assembled output (parts +
`Static`), the input of the URDF exporter; `prep/vf2tr_objects.json` are the per-body bounds from the prepare
step. Rerun `bl_prepare.py` before `bl_assemble.py` (the assembled blend no longer holds the raw bodies; the
script says so). `bl_prepare.py` also accepts the Fusion FBX route used for the plain VF-2 (assembly tree baked
into world space, bodies renamed `<assembly>|<body>`).

Then delete `Assets/Prefabs/Factory/HaasVF2TR.prefab` (if it exists) and run *ERUPT > Scenes > Relayout
FactoryDemo (VF-2 cell)* (`MtcSceneBuilder.RelayoutFactoryBatch` headless): it re-imports both FBX files,
extracts the materials double-sided into `Assets/Materials/Factory/Machines/HaasVF2TR_*`, bakes the
`SlidingPart`s, collision publishers and `MachineDoors` into the prefab, and rebuilds the cell.

## Rebuilding the earlier machines' FBX files (Blender 4.3, headless)

The CAD exports have meaningless body names (`Body1:133`), so parts are picked geometrically.

1. `bl_prepare.py` – import the OBJ (cm → m), planar-dissolve each body with bmesh, save `<m>.blend`
   and `<m>_objects.json` (per-body bounds). `bl_render.py <blend> <dir> <m> overview` renders views
   plus flat-colour ID maps; `idq.py <m> <view> x0 y0 x1 y1` maps image rectangles back to body names,
   and `bl_render.py … highlight <spec.json>` paints candidate groups red for checking.
2. `spec_<m>.json` lists each moving part as explicit names, bounding-box / cylinder selections, box
   cuts out of fused bodies (the VF-1 doors are welded into the front panel) and primitive extras
   (tool, workpiece, bar stock).
3. `bl_assemble.py spec.json out.fbx` rebases the machine to its footprint centre, joins each part with
   its pivot at the given point, exports every part flat under an Empty root (nesting is rebuilt in
   Unity: Blender's FBX exporter mangles child pivots when `bake_space_transform` is on), collapses to
   the triangle budget and quantises the CAD colours. `Axis_X/Y/Z` empties let Unity map Blender-frame
   vectors into the prefab's local frame (X→−X, Y→−Z, Z→+Y).

`FactorySceneBuilder.cs.txt` is the one-shot Unity editor script that imported the FBX files, baked the
components into the prefabs and laid out the scene (copy into `Assets/Editor` to rerun; it overwrites
FactoryDemo.unity from ReachabilityVR.unity).

## URDF package (ROS 2)

`factory_machines_description` (in `erupt_ws/src`, the ROS workspace repo) is an ament_cmake package with one URDF per machine
(`urdf/haas_vf2.urdf` for the current cell; `haas_vf1.urdf`, `haas_st10.urdf`, `haas_ec500.urdf`,
`injection_molder.urdf` for the earlier hall), Collada visual
meshes with the CAD colours and decimated STL collision meshes, plus `display.launch.py`
(robot_state_publisher + joint_state_publisher_gui + RViz). The generated files live in the ROS workspace; the
Blender scripts and specs that produce them stay here.

- `prep/<m>.blend` are the assembled outputs of `bl_assemble.py` (one mesh object per part, pivot at the
  object origin, `Static` for the rest) and are the archived source for the URDF meshes.
- `urdf_spec_<m>.json` maps Blender objects to links and defines the joints: `type`, `axis`
  (Blender/URDF frame: X right, Y back, Z up), `lower`/`upper`, `velocity`, `effort`, optional `mimic`,
  nominal `mass`, per-link `collision_faces`; virtual links use `"mesh": null, "pivot_of"`. Joint origins
  are computed as child pivot minus parent pivot, so re-pivoting a part in the spec moves the joint.
  Values mirror the Unity prefab motion (Unity→Blender: `(x,y,z) → (−x,−z,y)`).
- `bl_export_urdf.py <spec> <package_dir>` – headless Blender: converts materials to Principled nodes
  (the Collada exporter reads those), exports each part in its pivot-local frame (`.dae` visual; STL
  collision by edge-collapse decimation, or for `"collision_method": "voxel"` links by solidify → voxel
  remesh → decimate, which keeps thin sheet-metal walls that plain collapse turns into spikes), computes box inertia and writes the URDF.
  With `"unity_collision_fbx"` in the spec it also writes the same collision meshes as one FBX for Unity
  (`<link>_col` objects at their link pivots plus `Axis_*` markers), which the scene builder nests under the
  visual parts as mesh collision objects.
- `bl_check_urdf.py <package_dir> <machine> <out_dir> [spec]` – verifies the URDF without ROS: XML/tree/
  limit checks, mesh paths, DAE `Z_UP`/metre headers, binary STL, then re-imports every mesh, asserts the
  imported transforms are identity, builds the kinematic tree and renders zero/lower/mid/upper poses
  and the spec's `check_poses` (plus a flat-coloured collision pass) as PNGs.

```
B=/Applications/Blender.app/Contents/MacOS/Blender
PKG=~/VR/erupt_ws/src/factory_machines_description
$B -b --python bl_export_urdf.py -- urdf_spec_vf1.json $PKG
$B -b --python bl_check_urdf.py  -- $PKG haas_vf1 /tmp/chk urdf_spec_vf1.json
```

## FR3 work cell (MTC)

`Assets/Editor/MtcSceneBuilder.cs` builds the cell. *Build FactoryDemo MTC station* moves the MTC stack from
`Old Scenes/KitchenFR3.unity` into a FactoryDemo that still has the UR5e; *Relayout FactoryDemo (VF-2 cell)*
rebuilds the cell in a scene that already has the fr3 (`RelayoutFactoryBatch` headless). Either way the
layout is (world metres, floor y = 0, user at the origin facing +z, fr3 base at (0, 0.75, 1)):

- `Machines/Haas VF-2TR`: behind the robot, facing it, door front at z = 1.25 (6 cm behind the 0.6 x 0.44 m
  `RobotPedestal`), shifted 0.2 m to the right so the trunnion platter (left of the spindle centreline) is only
  0.18 m off the robot's axis. The robot reaches over the door sill onto the platter (top 1.10 m).
- `MachineDoorMenu`: Open / Close doors panel at the machine's front-right corner, facing the user.
- `FR3 Station/RawStockStand`: 0.3 x 0.7 x 0.3 m stand to the robot's right with a stack of four 8 x 6 x 5 cm
  billets (`GrabbablePartSet`, ids `blank_1..4`, 5 mm rest gaps). The billets are baked into the scene
  (`prePlaced`, visible in the editor) and registered at Start instead of spawned. The stand publishes as collision object
  `table` = the MTC server's `surface_link`, with the conveyor and the machine fixture as further primitives
  (`compoundRoots`): the arm avoids all of them, the carried part may touch any of them.
- `FR3 Station/MachineFixture`: 6 cm fixture block on the trunnion platter (0.53 m from the base, placed from
  the prefab's Platter part at build time); its `DropAnchor` is where a billet goes for machining.
- `FR3 Station/Conveyor`: belt from the robot's left (x -0.45, `DropAnchor` at x -0.6) to the parts table.
- `FR3 Station/PartsTable`: 1.6 x 0.9 m table at (-3.7, 0.8), 3.7 m from the robot (phase 2).

`ValidateFactoryBatch` checks the wiring, the door travel/opening, the reach of every anchor and the
clearances; `RenderFactoryShotsBatch` (`FACTORY_SHOT_DIR`) renders check views with the doors closed and open.

User flow: open the doors from the door menu (closed doors are collision objects, so a load plan fails
against them); wrist menu "Record Pick Place" (or the MTC dashboard's Plan tab), point at the top billet,
grab it and drop it on the fixture in the machine, stop recording. The place pose goes to the `/pick_place`
action; the dashboard lists stages and solutions, Preview animates the FR3, Execute runs it. The unload is
the same recording with the billet picked off the fixture and dropped on the conveyor.
`PickPlaceClient.placeHeightOffsetMeters` (-0.1251) cancels the server's surface offset so the part lands
where it was released.

ROS side (erupt_ws, branch study_full), in order:
```
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=<ip>
ros2 launch moveit_task_constructor_demo demo.launch.py
ros2 launch mtc_pick_place_server pick_place_server.launch.py spawn_demo_scene:=false
ros2 run planning_scene_utils planning_scene_watcher
```
The server plans with the Panda demo config; the FR3 shares its kinematics and joint names are remapped
`panda_*` <-> `fr3_*` (RobotReachProfile prefixes, MTCTrajectoryPlayer, AttachedCollisionObjectListener).
