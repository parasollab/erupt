# ERUPT Robots: Universal Robots

UR5e and UR10e, each with a Robotiq 2F-85 gripper for ERUPT. Assets only: no scripts.

| Prefab | Robot | ROS side | Joints |
|---|---|---|---|
| `Robots/UR5e.prefab` | UR5e with a Robotiq 2F-85 | `ROBOT=ur5e` | 6 revolute joints (±2π, elbow ±π); one driven gripper joint and five that follow it |
| `Robots/UR10e.prefab` | UR10e with a Robotiq 2F-85 | `ROBOT=ur10e` | 6 revolute joints (±2π, elbow ±π); one driven gripper joint and five that follow it |

The ROS side is the ERUPT ROS workspace (`erupt_ws`): `ROBOT=ur5e docker compose -f docker/compose.yaml up erupt`
starts that robot simulated, with MoveIt and pick-and-place, behind one ROS-TCP endpoint.
The Unity robot and the ROS robot agree on joint and link names, limits, planning groups,
named poses and controller names because both come from the same description.

## Use

1. The scene needs the ERUPT root (`EruptCore.prefab`), an XR rig with ERUPT's input backends
   and the plugins you want; the **Starter Scene** sample of `com.erupt.core` has all of that
   and no robot.
2. Drag `Robots/UR5e.prefab` into the scene. That is all the wiring there is: the robot
   registers itself, finds the interaction router and the selection service, and every plugin
   gives it a session.
3. Put it where you want it. The prefab carries its own planning frame (the child
   `PlanningFrame`), so the ROS side needs no pose: it believes the robot stands at the origin
   of its own `world` frame, wherever the robot is in Unity.
4. For several robots, give each `RobotInstance` its own **Id** and **ROS Namespace**
   (`left`, `right`) and start the ROS side with the same names:
   `ARMS="left=ur5e right=ur10e" docker compose -f docker/compose.yaml --profile arms up arms`.
   One robot alone keeps both fields empty (the root namespace).

## What is in it

In `Robots/`:

- `UR5e.prefab`: the robot, wired (`RobotInstance`, IK controller, end-effector handle, router binding, trajectory player, ghosts, planning-frame anchor)
- `UR5e.asset`: its `RobotProfile` (joints, limits, named poses, gripper, ROS names)
- `UR5eGhost.prefab`: the bare model shown translucent as the start and goal previews
- `ur5e.urdf`, `ur5e.srdf`, `ur5e.ros.json`: the description it was made from
- `UR5e.setup.md`: what Robot Setup found when it made the robot
- `UR10e.prefab`: the robot, wired (`RobotInstance`, IK controller, end-effector handle, router binding, trajectory player, ghosts, planning-frame anchor)
- `UR10e.asset`: its `RobotProfile` (joints, limits, named poses, gripper, ROS names)
- `UR10eGhost.prefab`: the bare model shown translucent as the start and goal previews
- `ur10e.urdf`, `ur10e.srdf`, `ur10e.ros.json`: the description it was made from
- `UR10e.setup.md`: what Robot Setup found when it made the robot
- `ur_description/`, `robotiq_description/`: the meshes the URDF names, with the Unity assets the URDF Importer made from them
- `Materials/`: materials the URDF Importer made for colours the URDF defines

The two robots share the gripper meshes.

## How it was made

Nothing here was made by hand. `Tools/robot-descriptions/generate.sh` (in the ERUPT
repository) exports the description from the robot registry of the ERUPT ROS workspace, and
ERUPT's Robot Setup (`ERUPT > Robots > Robot Setup…`, or from the command line
`Tools/robot-descriptions/setup-robots.sh`) turns it into the prefabs and the profile.
Running it again updates the assets in place and keeps their GUIDs. To change a robot, change
its description or the settings in `Tools/robot-descriptions/packages.json` and run it again;
keep hand tuning in a prefab variant, or in the profile fields Robot Setup leaves alone (limit
overrides and margins, the velocity switch, the ROS name rule, the gripper speed).

## Licences

ERUPT's own files in this package (the prefabs, the profiles, the SRDF and ROS-names files,
this documentation) are under the BSD 3-Clause licence in `LICENSE.md`. The robot description
files (the URDF and the meshes) stay under their makers' licences: see
`THIRD-PARTY-NOTICES.md` and `Licenses/`.
