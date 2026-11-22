# Robots

A robot in ERUPT is a prefab and a `RobotProfile`. The prefab is the articulated model with
ERUPT's components on it; the profile is the description every instance of the model shares.
Drop the prefab into a scene that has the ERUPT root and the plugins, and it works: it
registers itself, finds the interaction router and the selection service, and each plugin
gives it a session.

## Shipped robots

One package per maker, each made by Robot Setup from the description the ERUPT ROS workspace
(`erupt_ws`) uses, so the Unity robot and the ROS robot agree on names, limits, groups, poses
and controllers.

| Package | Prefab (`Robots/`) | ROS side |
|---|---|---|
| `com.erupt.robot.franka` | `FR3` (Franka hand) | `ROBOT=fr3` |
| `com.erupt.robot.universal-robots` | `UR5e`, `UR10e` (Robotiq 2F-85) | `ROBOT=ur5e`, `ROBOT=ur10e` |
| `com.erupt.robot.kinova` | `Gen3` (7 joints, Robotiq 2F-85) | `ROBOT=kinova_gen3` |
| `com.erupt.robot.ufactory` | `xArm7` (xArm gripper) | `ROBOT=xarm7` |
| `com.erupt.robot.kuka` | `iiwa14` (LBR iiwa 14 R820, Robotiq 2F-85) | `ROBOT=iiwa14` |

## Adding a shipped robot to a scene

1. Have its package in the project (it is in this repository; in another project add it by
   git URL, see `installation.md`).
2. Drag `Robots/<Name>.prefab` into the scene and put it where it should stand. It needs the
   ERUPT root (`EruptCore.prefab`), an XR rig with ERUPT's input backends and the plugins you
   want in the same scene; the Starter Scene sample has them.
3. Start the ROS side for that robot (`ROBOT=<name> docker compose -f docker/compose.yaml up erupt`
   in `erupt_ws`) and press Play.

One robot keeps `RobotInstance`'s **Id** and **ROS Namespace** empty: it talks in the root
namespace (`/joint_states`, `/plan_kinematic_path`). For several robots give each its own id
and namespace (`left`, `right`) and start the ROS side with the same names
(`ARMS="left=fr3 right=ur5e" docker compose -f docker/compose.yaml --profile arms up arms`).

### Where the robot stands, for ROS

Each robot prefab has a child `PlanningFrame`: the origin of the `world` frame of that
robot's own ROS stack. `RobotInstance` uses it as its planning frame, so the ROS side always
believes the robot stands at the origin of its own world, wherever you put the robot in the
scene, and no pose has to be given to the ROS side. Obstacles are sent to each robot in that
robot's frame.

When several robots' ROS stacks really share one world (a real cell with calibrated base
poses), assign one scene transform to **Planning Frame** on each `RobotInstance` and start
each stack with its robot's pose in that world. The transform's axes are ERUPT's: ROS x, y, z
= its x, z, y.

## Setting up a robot ERUPT does not ship

`ERUPT > Robots > Robot Setup…` turns a robot description into a prefab and a profile.

**You need** the robot's URDF with its meshes. A mesh the URDF names as
`package://<package>/<path>` is looked for at `<package>/<path>` next to the URDF, which is
the layout `Tools/robot-descriptions/generate.sh` exports; relative paths are taken from the
URDF's folder. A `.xacro` has to be expanded first (`xacro robot.urdf.xacro > robot.urdf`).
The files may be anywhere on disk; Robot Setup copies what it needs into the output folder.

**It helps to have** the MoveIt SRDF (planning groups, named poses, end effector, passive
joints) and a ROS-names file (`<robot>.ros.json`, see `Tools/robot-descriptions/README.md`:
planning frame, arm group and joints, base and end-effector links, topics, the gripper
controller's joint, which named poses are the initial, the gripper-open and the
gripper-closed one). Without them the same things are typed into the window, or guessed
where a guess is sound.

1. Open the window, choose the URDF (the SRDF and ROS-names file next to it are filled in when
   they are named alike), a display name and an output folder under `Assets` or in a package
   of your own under `Packages`.
2. Press **Check**. Nothing is written; the window lists what Robot Setup makes of the files:
   every answer with where it came from (typed, ROS-names file, SRDF, guessed), warnings and
   problems.
3. Fill in what is missing or wrong under *What the files do not say*: the arm's joints, the
   end-effector link (where the handle sits), the gripper's joints, driver joint and open and
   closed positions, the initial pose (a pose name, or joint values), the trajectory topic
   (`<controller>/joint_trajectory`). A typed value wins over the files.
4. Press **Set Up Robot**. In the output folder you get:
   - `<Name>.prefab`, the wired robot: the URDF import with `RobotInstance`, the IK
     controller, the interaction controller with the end-effector handle, the router binding,
     the trajectory player, the ghosts and the planning-frame anchor;
   - `<Name>Ghost.prefab`, the bare model shown translucent as start and goal previews;
   - `<Name>.asset`, the `RobotProfile`;
   - the URDF, SRDF, ROS-names file and meshes it was made from, with the materials and mesh
     assets the URDF Importer made (the materials inside Collada meshes get the lit shader
     of the project's render pipeline when they are imported);
   - `<Name>.setup.md`, the report.
5. Read the report. *Problems* have to be fixed before the robot works; *Warnings* are for you
   to confirm.

### What is taken from where

| From | What |
|---|---|
| URDF | every movable joint: kind (revolute, continuous, prismatic), position limits, velocity limit, mimic rule; link names; the planning-frame link when the URDF has it |
| SRDF | arm and gripper groups, the end effector, every named pose of those two groups, passive joints, the virtual joint that attaches the planning frame |
| ROS-names file | planning frame, arm group and joints, base and end-effector links, joint-states and trajectory topics, the gripper's driver joint (its controller's joint), the initial pose, the gripper's open and closed positions (the driver joint's value in the named open and closed poses), the ROS robot name |
| Guessed from the URDF | the gripper (a joint that other joints mimic drives it; failing that, prismatic joints at the end of the tree are fingers), the arm (the joints up to the gripper), the gripper's open and closed ends (the end of the driver's range that leaves the fingertips further apart is open) and its open width (how far the fingertips move apart) |

Joint roles: the arm's joints are `Arm`; every movable joint of the gripper is `Gripper`,
followers included (the SRDF calls them passive; ERUPT moves them by their mimic rule);
anything else is `Passive` and not driven. A continuous joint has no position limits. A pose
or gripper position outside the limits is stored at the limit and reported.

### What the report checks

The profile (`RobotProfile.Validate()`), the prefab against the profile (every joint and link
it names), references the wiring needs, joints of the URDF the import lacks or has as another
kind, joint limits and link frames that differ between the import and the URDF, visual and
collision geometry that do not overlap (a mesh read with the wrong axes or units), missing
meshes and materials, collision meshes too detailed for one convex collider, a gripper that
could not be identified, an initial pose or a trajectory topic that is missing.

### Running it again

Run Robot Setup again on the same output folder after the description changed. The prefabs
and the profile are updated in place: their GUIDs and the file IDs inside the prefab stay,
so scenes and prefab variants that use the robot keep working. The prefab is rebuilt from the
import, so keep hand tuning in a prefab variant. In the profile, what Robot Setup does not
describe is kept: limit overrides and margins, the velocity switch, the ROS name rule, the
gripper speed.

### From the command line

```bash
Unity -batchmode -quit -projectPath . -executeMethod Erupt.Editor.RobotSetup.RunFromCommandLine \
  -urdf path/robot.urdf -srdf path/robot.srdf -rosNames path/robot.ros.json \
  -out Assets/Robots/MyRobot -name "My Robot" [-assetName MyRobot] [-endEffectorLink tool0] ...
```

Every field of the window has a flag (`-baseLink`, `-planningFrame`, `-armGroup`,
`-armJoints a,b`, `-gripperGroup`, `-gripperJoints a,b`, `-gripperDriver`, `-gripperOpen`,
`-gripperClosed`, `-gripperOpenWidth`, `-noGripper`, `-initialPose`,
`-initialPoseValues j=v,j=v`, `-jointStatesTopic`, `-trajectoryTopic`). `-settings file.json`
reads one set of settings from a file and `-settingsList file.json` several
(`{"robots": [ ... ]}`), which is how the shipped robots are made:
`Tools/robot-descriptions/setup-robots.sh` with `Tools/robot-descriptions/packages.json`.

### Limits of the tool

- One arm and one gripper per robot. A mobile base, a second arm or a tool changer are not
  described by a profile.
- Joints with more than one degree of freedom (floating, planar) are left alone.
- The handle sits on the end-effector link. For a description whose end-effector link is the
  flange, type in a link between the fingertips if the URDF has one.
- The robot's colliders are convex hulls of the collision meshes; they serve selection and
  grabbing, not contact.
- Inside a package, the URDF Importer does not turn STL meshes into prefabs; Robot Setup does
  that itself.
