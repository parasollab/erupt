# Third-party notices

This package redistributes robot description files that are not ERUPT's. They stay under the
licences of their makers; ERUPT's BSD 3-Clause licence (`LICENSE.md`) covers only the files
ERUPT adds.

| Description package | Upstream | Version used | Licence | Licence text |
|---|---|---|---|---|
| `ur_description` | https://github.com/UniversalRobots/Universal_Robots_ROS2_Description | ROS Jazzy binary 3.6.1 | BSD-3-Clause | `Licenses/ur_description/LICENSE.txt` |
| `robotiq_description` | https://github.com/robotiq/robotiq-ros (`grippers/robotiq_description`) | commit `49167c4f44a1da2b564715fba3e729c8ce958b63` | BSD-3-Clause | `Licenses/robotiq_description/LICENSE.txt`, `Licenses/robotiq_description/LICENSE.grippers.txt` |

- `ur_description`: Only the UR5e and UR10e meshes are included. The newer UR models and the Smart Panel carry a different, more restrictive licence and are not part of this package.
- `robotiq_description`: `LICENSE.txt` is Robotiq's, `LICENSE.grippers.txt` PickNik Robotics'.

## What these licences cover here

- The mesh files under `ur_description/`, `robotiq_description/` in `Robots/`, unchanged, and the Unity assets the URDF
  Importer derived from them (the `.prefab` and `.asset` files next to the STL meshes).
- `Robots/ur5e.urdf`, `Robots/ur10e.urdf`: generated from the makers' xacro files by expanding them for the
  configuration the ERUPT ROS workspace uses.

## Changes made to the upstream files

- The URDF is a generated file: the xacro of the description packages was expanded into one
  URDF per robot, with the arm and the gripper combined as the ERUPT ROS workspace combines them.
- Elements that only matter to ROS drivers and simulators (`ros2_control`, `gazebo`,
  `transmission`) were removed from it. Nothing else was changed.
- Only the mesh files the URDF names were copied; the rest of each description package is not included.
- The meshes themselves are not modified.

## ERUPT's own files

The SRDF and ROS-names files in `Robots/` are ERUPT's (BSD 3-Clause), except for the parts of
the SRDF taken from the robot's MoveIt configuration package, which keep that package's
licence. The prefabs and profiles were made by ERUPT's Robot Setup and are ERUPT's.
