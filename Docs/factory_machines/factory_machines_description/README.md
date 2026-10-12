# factory_machines_description

URDF models of the four FactoryDemo machines, generated from the same CAD-derived meshes as the Unity
prefabs (`Assets/Prefabs/Factory`). Each moving part is its own link; joint types, axes and limits
follow the motion baked into the Unity prefabs, so a URDF joint at its limit matches the demo animation.

| URDF | Links | Joints |
|---|---|---|
| `haas_vf1` | base_link, door_left, door_right, head, spindle, saddle, table, carousel | door_left_slide / door_right_slide (prismatic, doors part from the centre, 0–0.55 m), head_z (prismatic Z, −0.40–0), spindle_spin (continuous, child of head), saddle_y (prismatic +Y, 0–0.245), table_x (prismatic +X, child of saddle, 0–0.333), carousel_index (continuous Z, 18° pockets) |
| `haas_st10` | base_link, door, chuck, turret_z_carriage (virtual), turret_slide, turret | door_slide (prismatic −X toward the headstock, 0–0.9), chuck_spin (continuous X), turret_z (prismatic −X toward the chuck, 0–0.17), turret_x (prismatic along the slant bed toward the spindle axis, 0–0.08), turret_index (continuous X, 30° stations) |
| `haas_ec500` | base_link, door, pallet_changer, carousel | door_slide (prismatic −X, 0–0.86), pallet_changer_index (continuous Z, 180° steps), carousel_index (continuous X, 7.2° pockets) |
| `injection_molder` | base_link, moving_platen, toggle_links | moving_platen_slide (prismatic −Y toward the clamp end, 0–0.27), toggle_links_slide (mimic of the platen, ×0.5) |

Conventions: `base_link` is the machine footprint centre on the floor, Z up, metres. Visual meshes are
Collada (`meshes/<machine>/visual/*.dae`, per-face CAD colours; the EC-500 door glass carries alpha 0.3),
collision meshes are binary STL (`meshes/<machine>/collision/*.stl`): the machine body is a solidified,
3 cm voxel-remeshed shell decimated to ~10–12k triangles (enclosure walls and door openings preserved, no
convex hull), moving parts are edge-collapse decimated copies (0.5k–4k triangles). Masses, inertias and efforts are nominal estimates (box inertia around
each link's bounding box); velocities come from the prefab travel times.

## Use

```
ln -s /path/to/erupt/Docs/factory_machines/factory_machines_description ~/erupt_ws/src/
cd ~/erupt_ws && colcon build --packages-select factory_machines_description && source install/setup.bash
check_urdf $(ros2 pkg prefix --share factory_machines_description)/urdf/haas_vf1.urdf
ros2 launch factory_machines_description display.launch.py model:=haas_st10     # RViz + joint sliders
```

`display.launch.py` arguments: `model` (haas_vf1 | haas_st10 | haas_ec500 | injection_molder), `gui`,
`rviz`, `prefix` (TF frame prefix, for running several machines side by side).

Unity: the vendored URDF-Importer resolves `package://factory_machines_description/...` under
`Assets/factory_machines_description/...`, so copying this folder into `Assets/` lets
*Robotics > Import Robot from URDF* rebuild a machine as an articulated GameObject.

## Regenerating

See `Docs/factory_machines/README.md` ("URDF package"). Edit `urdf_spec_<machine>.json` (links, joint
axes/limits, collision budgets) and rerun `bl_export_urdf.py`; the meshes and URDF here are generated
output and should not be edited by hand.
