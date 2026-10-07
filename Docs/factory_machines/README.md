# Factory machine models (FactoryDemo scene)

`Assets/Scenes/Demo Scenes/FactoryDemo.unity` is the ReachabilityVR demo rig (UR5e on a pedestal,
VR camera rig, wrist menus) surrounded by four animated machines built from CAD OBJ exports:

| Prefab (`Assets/Prefabs/Factory`) | Source OBJ (Desktop) | Animated parts |
|---|---|---|
| HaasVF1 | VF-1_STEP_11_2021 | DoorLeft/DoorRight (slide), Head (Z), Saddle (Y), Table (X), Spindle (spin), Carousel (index) |
| HaasST10 | st-10l_st-15l_solid_models_10_2023 | Door (slide), Chuck (spin, with hex bar), TurretSlide (X/Z), Turret (index) |
| HaasEC500 | EC-500-50T_Solid_Model_STEP_2020-12 | Door (slide), PalletChanger (180° index), Carousel (index) |
| InjectionMolder | Injection_molding_machine_equipment_model (scaled 0.6) | MovingPlaten + ToggleLinks (slide), MoldedPart (ejected) |

Each prefab root carries a `Factory.*Cycle` component (Assets/Scripts/Factory) that sequences the part
components (`SlidingPart`, `RotatingPart`, `IndexingPart`, `LinearAxis`). Offsets are in each part's
parent-local space; tune them in the Inspector.

## Rebuilding the FBX files (Blender 4.3, headless)

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

## FR3 pick-and-place station (MTC)

`Assets/Editor/MtcSceneBuilder.cs` ("ERUPT/Scenes/Build FactoryDemo MTC station") moves the MTC stack
from `Old Scenes/KitchenFR3.unity` into FactoryDemo, swaps the FR3 in for the UR5e on `RobotPedestal`,
and builds the `FR3 Station` root: `StationTable` (published as collision object `table`, the MTC
server's support surface), `PartsTray` with four slots fed by a chute from the injection moulder,
`ToteLeft`/`ToteRight`, `BlanksTray` (three `blank_n` parts from `GrabbablePartSet`) and `FixturePlate`.
`MoldedPartSpawner` turns every moulder ejection into a grabbable `molded_part_n` with a
`CollisionObjectPublisher`; `PickPlaceStationController` (autoSort off) repeats the last demonstrated
place for later parts. Batch entry points: `MtcSceneBuilder.BuildFactoryBatch`, `ValidateFactoryBatch`,
`RenderFactoryShotsBatch` (`FACTORY_SHOT_DIR`). The build is in-place and refuses to run twice; restore
the committed ur5e version of FactoryDemo.unity to rebuild.

Visitor flow: wrist menu "Record Pick Place" (or the MTC dashboard's Plan tab), point at a part, grab it
and drop it where it should go (a tote, the fixture plate), stop recording. The place pose goes to the
`/pick_place` action; the dashboard lists stages and solutions, Preview animates the FR3, Execute runs it.
`PickPlaceClient.placeHeightOffsetMeters` (−0.1251) cancels the server's surface offset so the part lands
where it was released.

ROS side (erupt_ws, branch study_full), in order:
```
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=<ip>
ros2 launch moveit_task_constructor_demo demo.launch.py
ros2 launch mtc_pick_place_server pick_place_server.launch.py spawn_demo_scene:=false
ros2 run planning_scene_utils planning_scene_watcher
```
The server plans with the Panda demo config; the FR3 shares its kinematics and joint names are remapped
`panda_*` ↔ `fr3_*` (RobotReachProfile prefixes, MTCTrajectoryPlayer, AttachedCollisionObjectListener).
