# Third-party notices

This package redistributes robot description files that are not ERUPT's. They stay under the
licences of their makers; ERUPT's BSD 3-Clause licence (`LICENSE.md`) covers only the files
ERUPT adds.

| Description package | Upstream | Version used | Licence | Licence text |
|---|---|---|---|---|
| `xarm_description` | https://github.com/xArm-Developer/xarm_ros2 | commit `d837f2e17f7f36043103b367fca3af8172626245` (branch `jazzy`) | BSD-3-Clause | `Licenses/xarm_description/LICENSE.txt` |

- `xarm_description`: The licence is the one of the repository; the package manifest does not declare one.

## What these licences cover here

- The mesh files under `xarm_description/` in `Robots/`, unchanged, and the Unity assets the URDF
  Importer derived from them (the `.prefab` and `.asset` files next to the STL meshes).
- `Robots/xarm7.urdf`: generated from the makers' xacro files by expanding them for the
  configuration the ERUPT ROS workspace uses.

## Changes made to the upstream files

- The URDF is a generated file: the xacro of the description package was expanded into one
  URDF, with the arm and the gripper combined as the ERUPT ROS workspace combines them.
- Elements that only matter to ROS drivers and simulators (`ros2_control`, `gazebo`,
  `transmission`) were removed from it. Nothing else was changed.
- Only the mesh files the URDF names were copied; the rest of each description package is not included.
- The meshes themselves are not modified.

## ERUPT's own files

The SRDF and ROS-names files in `Robots/` are ERUPT's (BSD 3-Clause), except for the parts of
the SRDF taken from the robot's MoveIt configuration package, which keep that package's
licence. The prefabs and profiles were made by ERUPT's Robot Setup and are ERUPT's.
