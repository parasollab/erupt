# Third-party notices

This package redistributes robot description files that are not ERUPT's. They stay under the
licences of their makers; ERUPT's BSD 3-Clause licence (`LICENSE.md`) covers only the files
ERUPT adds.

| Description package | Upstream | Version used | Licence | Licence text |
|---|---|---|---|---|
| `lbr_iiwa14_r820_description` | https://github.com/lbr-stack/lbr_iiwa14_r820_description | commit `86ac0532841a90694afe6a65c300f08b55eb1296` (v2.5.0) | Apache-2.0 | `Licenses/lbr_iiwa14_r820_description/LICENSE.txt` |
| `robotiq_description` | https://github.com/robotiq/robotiq-ros (`grippers/robotiq_description`) | commit `49167c4f44a1da2b564715fba3e729c8ce958b63` | BSD-3-Clause | `Licenses/robotiq_description/LICENSE.txt`, `Licenses/robotiq_description/LICENSE.grippers.txt` |

- `lbr_iiwa14_r820_description`: The description repository declares the licence in its package manifest only; the text is the one from https://github.com/lbr-stack/lbr_fri_ros2_stack, which carries the same meshes.
- `robotiq_description`: `LICENSE.txt` is Robotiq's, `LICENSE.grippers.txt` PickNik Robotics'.

## What these licences cover here

- The mesh files under `lbr_iiwa14_r820_description/`, `robotiq_description/` in `Robots/`, unchanged, and the Unity assets the URDF
  Importer derived from them (the `.prefab` and `.asset` files next to the STL meshes).
- `Robots/iiwa14.urdf`: generated from the makers' xacro files by expanding them for the
  configuration the ERUPT ROS workspace uses.

## Changes made to the upstream files

- The URDF is a generated file: the xacro of the description packages was expanded into one
  URDF, with the arm and the gripper combined as the ERUPT ROS workspace combines them.
- Elements that only matter to ROS drivers and simulators (`ros2_control`, `gazebo`,
  `transmission`) were removed from it. Nothing else was changed.
- Only the mesh files the URDF names were copied; the rest of each description package is not included.
- The meshes themselves are not modified.

## ERUPT's own files

The SRDF and ROS-names files in `Robots/` are ERUPT's (BSD 3-Clause), except for the parts of
the SRDF taken from the robot's MoveIt configuration package, which keep that package's
licence. The prefabs and profiles were made by ERUPT's Robot Setup and are ERUPT's.
