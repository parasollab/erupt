# Robot descriptions

The robots ERUPT ships are not described by hand. `generate.sh` exports them from the robot
registry of the ERUPT ROS workspace (`erupt_ws`, `src/erupt_bringup/config/robots`), so the Unity
robots and the ROS robots agree on joint names, link names, limits, planning groups, named poses
and controller names.

```bash
Tools/robot-descriptions/generate.sh Tools/robot-descriptions/out            # the shipped robots
Tools/robot-descriptions/generate.sh Tools/robot-descriptions/out my_robot   # any robot of the registry
```

It needs Docker and the ERUPT ROS image (`docker compose -f docker/compose.yaml build` in the
`erupt_ws` repository). To export a robot file you are still working on, point
`ERUPT_BRINGUP_CONFIG` at your copy of `erupt_ws/src/erupt_bringup/config`.

For each robot it writes a folder `<robot>/` with:

| File | Content |
|---|---|
| `<robot>.urdf` | The URDF the ROS side uses. Elements that only matter to ROS drivers and simulators (`ros2_control`, `gazebo`, `transmission`) are removed; nothing else is changed |
| `<robot>.srdf` | The SRDF MoveIt uses: planning groups, named poses, end effector, passive joints |
| `<robot>.ros.json` | The ROS names a Unity robot profile needs: planning frame, arm group and joints, base and end-effector links, `joint_states` topic, the arm controller's trajectory topic, the gripper controller and group, and which named poses are the initial, gripper-open and gripper-closed ones |
| `<package>/...` | One folder per description package with only the mesh files the URDF uses, which is the layout the Unity URDF importer resolves `package://` paths against |

These are the inputs of `ERUPT > Robots > Robot Setup…`, which turns them into a robot prefab
and profile (`Packages/com.erupt.core/Documentation~/robots.md`). The shipped robot packages
(`Packages/com.erupt.robot.*`) are made from `out/` in one go:

```bash
Tools/robot-descriptions/setup-robots.sh            # every robot in packages.json
Tools/robot-descriptions/setup-robots.sh UR5e,FR3   # only these
```

`packages.json` says which description goes into which package and under which name, and the
few values that are typed in on top of the files (for the FR3 the end-effector link
`fr3_hand_tcp`: the handle sits between the fingertips, where the SRDF's arm group ends,
while the ROS-names file gives `fr3_link8`, the frame grasps are defined in). Running it
again updates the prefabs and profiles in place and keeps their GUIDs. The editor has to be
closed.

| Output | Robot | ROS-side definition |
|---|---|---|
| `fr3` | Franka Research 3 with the Franka hand | `config/robots/fr3.yaml` |
| `ur5e`, `ur10e` | Universal Robots UR5e / UR10e with a Robotiq 2F-85 | `config/robots/ur5e.yaml`, `ur10e.yaml` |
| `kinova_gen3` | Kinova Gen3 (7 joints) with a Robotiq 2F-85 | `config/robots/kinova_gen3.yaml` |
| `xarm7` | UFactory xArm7 with the xArm gripper | `config/robots/xarm7.yaml` |
| `iiwa14` | KUKA LBR iiwa 14 R820 with a Robotiq 2F-85 | `config/robots/iiwa14.yaml` |

## Sources and licences

The generated files are redistributed under their upstream licences, not under ERUPT's. Each
robot package carries the licence files below (copies are in `licenses/`) and states that the
URDF was generated and stripped as described above. The SRDF and ROS-names files are ERUPT's own
(BSD-3-Clause), except for the parts of each SRDF taken from the robot's MoveIt configuration
package, which keep that package's licence.

| Description package | Upstream | Version used | Licence |
|---|---|---|---|
| `franka_description` | https://github.com/frankarobotics/franka_description | commit `7aeeddc449edf8d62b594f9e36a81da53e7796f9` (2.9.0) | Apache-2.0 (`LICENSE`, `NOTICE`) |
| `ur_description` | https://github.com/UniversalRobots/Universal_Robots_ROS2_Description | ROS Jazzy binary 3.6.1 | BSD-3-Clause. Only the UR5e and UR10e meshes are used; the newer UR models and the Smart Panel carry a different, more restrictive licence and are not included. |
| `robotiq_description` | https://github.com/robotiq/robotiq-ros (`grippers/robotiq_description`) | commit `49167c4f44a1da2b564715fba3e729c8ce958b63` | BSD-3-Clause (`LICENSE` Robotiq, `LICENSE.grippers` PickNik Robotics) |
| `kortex_description` | https://github.com/Kinovarobotics/ros2_kortex | ROS Jazzy binary 0.2.6 | BSD-3-Clause |
| `xarm_description` | https://github.com/xArm-Developer/xarm_ros2 | commit `d837f2e17f7f36043103b367fca3af8172626245` (branch `jazzy`) | BSD-3-Clause (repository `LICENSE`; the package manifest does not declare one) |
| `lbr_iiwa14_r820_description` | https://github.com/lbr-stack/lbr_iiwa14_r820_description | commit `86ac0532841a90694afe6a65c300f08b55eb1296` (v2.5.0) | Apache-2.0. The description repository declares the licence in its package manifest only; the licence text is the one from https://github.com/lbr-stack/lbr_fri_ros2_stack, which carries the same meshes. |

The pinned commits are the ones in the `robots` stage of the ROS container's `docker/Dockerfile`.
