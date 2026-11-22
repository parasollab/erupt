# Adding a robot

Robot Setup turns a robot description into an ERUPT robot: a prefab you can drag into a scene
and a profile with its joints, limits, groups and poses. This page is for an arm with one
gripper that ERUPT does not ship.

There are two halves: the robot in Unity (this page, steps 1 to 5) and the robot on the ROS
side (step 6).

## What you need

- **The robot's URDF with its meshes.** A `.xacro` has to be expanded first:
  `xacro robot.urdf.xacro > robot.urdf`. A mesh the URDF names as
  `package://<package>/<path>` is looked for at `<package>/<path>` next to the URDF.
- **Its MoveIt SRDF**, if you have one. It supplies the planning groups, the named poses and
  the end effector, which you otherwise type in.

The files can be anywhere on disk. Robot Setup copies what it needs.

## Steps

1. In Unity, open **ERUPT > Robots > Robot Setup…**
2. Choose the URDF. An SRDF with the same name next to it is filled in for you. Give the
   robot a display name and an output folder under `Assets`.
3. Press **Check**. Nothing is written yet. The window lists what it found, where each answer
   came from (the SRDF, a guess, or typed by you), and what is missing.
4. Under *What the files do not say*, fill in what is missing or wrong:

   | Field | What to enter |
   | --- | --- |
   | Arm joints | The joints of the arm, base to tip |
   | End-effector link | The link the drag handle sits on. Use a link between the fingertips if the URDF has one |
   | Gripper driver | The one joint the gripper controller commands |
   | Gripper open / closed | The driver joint's value with the gripper open and closed |
   | Initial pose | A named pose from the SRDF, or joint values |
   | Trajectory topic | `<controller name>/joint_trajectory` of the arm's trajectory controller |

5. Press **Set Up Robot**, then read the report (`<Name>.setup.md` in the output
   folder). *Problems* have to be fixed before the robot works; *Warnings* are for you to
   confirm. Drag `<Name>.prefab` into a scene to use the robot.

6. On the ROS side, either start your own bringup and MoveIt for the robot and add the pieces
   ERUPT needs (see [The ROS 2 side](ros-setup.md)), or add the robot to the ERUPT workspace
   so that it starts with `ROBOT=<your robot>` like the shipped ones. That takes the robot's
   MoveIt configuration and one YAML file in `erupt_ws/src/erupt_bringup/config/robots/`,
   which names the MoveIt configuration, the arm group, the controllers and the pose the
   simulated robot starts in. *Adding a robot* in the `erupt_ws` README has the details, and
   `fr3.yaml` there is the template. Then press Play.

## Check that it works

- The robot takes the pose of the simulated robot when you press Play.
- Selecting the end effector shows its menu with **Set Start**, **Set Goal** and **Plan**.
- Dragging the end effector moves the arm, and each joint stops at its limit.
- **Set Start**, a drag, **Set Goal**, **Plan** and **Execute** move the simulated robot, and
  the Unity robot follows.
- The gripper opens and closes, with all its fingers moving together.

If the robot does not follow the simulated one, the joint names on the two sides differ: the
Unity robot and the ROS robot must come from the same description.

## When the description changes

Run Robot Setup again with the same output folder. The prefab and the profile are updated in
place, so scenes that use the robot keep working. Joint-limit overrides you set on the profile
are kept. Changes you made to the prefab by hand are not: keep those in a prefab variant.

## What Robot Setup does not handle

- More than one arm or gripper in one description, a mobile base, or a tool changer.
- Joints with more than one degree of freedom.

The reference page [Robots](../../Packages/com.erupt.core/Documentation~/robots.md) has the
details: what is read from which file, what the report checks, and how to run Robot Setup
from the command line.
