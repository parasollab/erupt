# ERUPT Robots: Franka

Franka Research 3 with the Franka hand for ERUPT. Assets only: no scripts.

| Prefab | Robot | ROS side | Joints |
|---|---|---|---|
| `Robots/FR3.prefab` | Franka Research 3 with the Franka hand | `ROBOT=fr3` | 7 revolute joints; two prismatic fingers, the second following the first |

The ROS side is the ERUPT ROS workspace (`erupt_ws`): `ROBOT=fr3 docker compose -f docker/compose.yaml up erupt`
starts that robot simulated, with MoveIt and pick-and-place, behind one ROS-TCP endpoint.
The Unity robot and the ROS robot agree on joint and link names, limits, planning groups,
named poses and controller names because both come from the same description.

## Use

1. The scene needs the ERUPT root (`EruptCore.prefab`), an XR rig with ERUPT's input backends
   and the plugins you want; the **Starter Scene** sample of `com.erupt.core` has all of that
   and no robot.
2. Drag `Robots/FR3.prefab` into the scene. That is all the wiring there is: the robot
   registers itself, finds the interaction router and the selection service, and every plugin
   gives it a session.
3. Put it where you want it. The prefab carries its own planning frame (the child
   `PlanningFrame`), so the ROS side needs no pose: it believes the robot stands at the origin
   of its own `world` frame, wherever the robot is in Unity.
4. For several robots, give each `RobotInstance` its own **Id** and **ROS Namespace**
   (`left`, `right`) and start the ROS side with the same names:
   `ARMS="left=fr3 right=fr3" docker compose -f docker/compose.yaml --profile arms up arms`.
   One robot alone keeps both fields empty (the root namespace).

## What is in it

In `Robots/`:

- `FR3.prefab`: the robot, wired (`RobotInstance`, IK controller, end-effector handle, router binding, trajectory player, ghosts, planning-frame anchor)
- `FR3.asset`: its `RobotProfile` (joints, limits, named poses, gripper, ROS names)
- `FR3Ghost.prefab`: the bare model shown translucent as the start and goal previews
- `fr3.urdf`, `fr3.srdf`, `fr3.ros.json`: the description it was made from
- `FR3.setup.md`: what Robot Setup found when it made the robot
- `franka_description/`: the meshes the URDF names, with the Unity assets the URDF Importer made from them
- `Materials/`: materials the URDF Importer made for colours the URDF defines

The end-effector handle sits on `fr3_hand_tcp`, the point between the fingertips (the tip of the SRDF arm group), not on `fr3_link8`, which the ROS-names file gives as the frame grasps are defined in.

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
