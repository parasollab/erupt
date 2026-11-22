# Third-party notices

This package redistributes robot description files that are not ERUPT's. They stay under the
licences of their makers; ERUPT's BSD 3-Clause licence (`LICENSE.md`) covers only the files
ERUPT adds.

| Description package | Upstream | Version used | Licence | Licence text |
|---|---|---|---|---|
| `kortex_description` | https://github.com/Kinovarobotics/ros2_kortex | ROS Jazzy binary 0.2.6 | BSD-3-Clause | `Licenses/kortex_description/LICENSE.txt` |
| `robotiq_description` | https://github.com/robotiq/robotiq-ros (`grippers/robotiq_description`) | commit `49167c4f44a1da2b564715fba3e729c8ce958b63` | BSD-3-Clause | `Licenses/robotiq_description/LICENSE.txt`, `Licenses/robotiq_description/LICENSE.grippers.txt` |

- `robotiq_description`: `LICENSE.txt` is Robotiq's, `LICENSE.grippers.txt` PickNik Robotics'.

## What these licences cover here

- The mesh files under `kortex_description/`, `robotiq_description/` in `Robots/`, unchanged, and the Unity assets the URDF
  Importer derived from them (the `.prefab` and `.asset` files next to the STL meshes).
- `Robots/kinova_gen3.urdf`: generated from the makers' xacro files by expanding them for the
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
