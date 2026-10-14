# Factory machine models (FactoryDemo scene)

`Assets/Scenes/Demo Scenes/FactoryDemo.unity` is the ReachabilityVR demo rig (FR3 on a pedestal, VR camera
rig, wrist menus, MTC pick/place stack) laid out as a one-machine work cell around a Haas VF-2:

| Prefab (`Assets/Prefabs/Factory`) | Source (Desktop) | Animated parts |
|---|---|---|
| HaasVF2 | `vf-2_solid_models_1_2023/VF-2_STEP__1_2023.fbx` (Fusion export of the STEP, named assemblies) | DoorLeft/DoorRight (slide 0.48 m each, driven together by `Factory.MachineDoors`); everything else is static for now |

The VF-2 prefab also carries three renderer-less collision meshes (`Static/Collision`, `DoorLeft/Collision`,
`DoorRight/Collision`, from `HaasVF2_collision.fbx`) with `CollisionObjectPublisher`s `vf2_body`,
`vf2_door_left`, `vf2_door_right`, so MoveIt plans around the enclosure and the doors follow in the planning
scene as they slide. The doors are opened and closed from the `MachineDoorMenu` world-space panel
(`MachineDoorPanel.uxml` + `Factory.MachineDoorPanel`, an instance of the planning-menu prefab like the MTC
dashboard) standing just outside the machine's front-right corner.

The four machines of the earlier hall (HaasVF1, HaasST10, HaasEC500, InjectionMolder, with their
`Factory.*Cycle` components) remain as prefabs and in the URDF package but are no longer in the scene.
`FactorySceneBuilder.cs.txt` is the archived one-shot script that built them.

## Rebuilding the VF-2 (Blender 4.3, headless)

```
B=/Applications/Blender.app/Contents/MacOS/Blender
$B -b --python bl_prepare.py  -- ~/Desktop/vf-2_solid_models_1_2023/VF-2_STEP__1_2023.fbx prep vf2
$B -b --python bl_assemble.py -- spec_vf2.json /tmp/asm/HaasVF2.fbx
cp /tmp/asm/HaasVF2.fbx "../../Assets/Models/Factory Machines/HaasVF2.fbx"
$B -b /tmp/asm/HaasVF2.blend --python-expr "import bpy; bpy.ops.wm.save_as_mainfile(filepath='$PWD/prep/vf2.blend', compress=True)"
PKG=~/VR/erupt_ws/src/factory_machines_description
$B -b --python bl_export_urdf.py -- urdf_spec_vf2.json $PKG   # URDF + meshes + HaasVF2_collision.fbx
$B -b --python bl_check_urdf.py  -- $PKG haas_vf2 /tmp/chk urdf_spec_vf2.json
```

`bl_prepare.py` accepts an FBX as well as an OBJ: the FBX assembly tree is baked into world space (instanced
bodies such as the leveling pads get their own mesh data first) and every body is renamed
`<assembly>|<body>`, so `spec_vf2.json` selects the doors by name (`37-0400-LEFT DOOR|Body1.010`). The spec's
`origin` puts the machine origin at the main enclosure's footprint centre on the floor (the chip conveyor and
pendant stick out of it), and `glass_materials` turns the door-window material into `Glass` (remapped to
`MachineGlass` in Unity). `drop` leaves out the chip-conveyor holder/shelf brackets (they stick 0.5 m out of the
front-left at head height), and `adjust` lowers the two side doors by 0.62 m: the CAD stores them raised, with
their glass above the roof line and the 0.3-1.1 m side openings exposed. `prep/vf2.blend` is the assembled
output (parts + `Static`), the input of the URDF exporter; `prep/vf2_objects.json` are the per-body bounds from
the prepare step. Rerun `bl_prepare.py` before `bl_assemble.py` (the assembled blend no longer holds the raw
bodies; the script says so).

Then delete `Assets/Prefabs/Factory/HaasVF2.prefab` (if it exists) and run *ERUPT > Scenes > Relayout
FactoryDemo (VF-2 cell)* (`MtcSceneBuilder.RelayoutFactoryBatch` headless): it re-imports both FBX files,
extracts the materials double-sided into `Assets/Materials/Factory/Machines/HaasVF2_*`, bakes the
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
layout is (world metres, floor y = 0, visitor at the origin facing +z, fr3 base at (0, 0.75, 1)):

- `Machines/Haas VF-2`: behind the robot, facing it, door front at z = 1.25 (3 cm behind the 0.6 x 0.44 m
  `RobotPedestal`). The robot reaches over the door sill onto the machine table (0.85 m).
- `MachineDoorMenu`: Open / Close doors panel at the machine's front-right corner, facing the visitor.
- `FR3 Station/RawStockStand`: 0.3 x 0.7 x 0.3 m stand to the robot's right with a stack of four 8 x 6 x 5 cm
  billets (`GrabbablePartSet`, ids `blank_1..4`, 5 mm rest gaps). The stand publishes as collision object
  `table` = the MTC server's `surface_link`, with the conveyor and the machine fixture as further primitives
  (`compoundRoots`): the arm avoids all of them, the carried part may touch any of them.
- `FR3 Station/MachineFixture`: 6 cm vise block on the VF-2 table, 0.40 m behind the door front (0.65 m
  from the base); its `DropAnchor` is where a billet goes for machining.
- `FR3 Station/Conveyor`: belt from the robot's left (x -0.45, `DropAnchor` at x -0.6) to the parts table.
- `FR3 Station/PartsTable`: 1.6 x 0.9 m table at (-3.7, 0.8), 3.7 m from the robot (phase 2).

`ValidateFactoryBatch` checks the wiring, the door travel/opening, the reach of every anchor and the
clearances; `RenderFactoryShotsBatch` (`FACTORY_SHOT_DIR`) renders check views with the doors closed and open.

Visitor flow: open the doors from the door menu (closed doors are collision objects, so a load plan fails
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
