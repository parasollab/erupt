# Collecting demonstrations

The demonstration plugin (`com.erupt.plugin.demonstration`) records what you make the virtual
robot do, replays it, and publishes it to ROS as a joint trajectory. Use it to collect
demonstrations for learning from demonstration.

## Set up

1. Put `DemonstrationCollectionPlugin.prefab` (in the package's `Prefabs` folder) next to the
   `ERUPT` root object in your scene. The reference scenes already have it.
2. Nothing else is required. The plugin finds the robots in the scene and keeps a recorder for
   each one.

## Record, replay, publish

1. Open the panel and its **Demonstrations** tab.
2. Select a part of the robot you want to demonstrate with, so that it is the active robot.
3. Press **Record**. Move the robot: drag the end-effector handle, drag a link, or jog a joint.
4. Press **Stop**.
5. Press **Replay** to watch the demonstration on the robot.
6. Press **Publish** to send it to ROS.

One demonstration is recorded at a time, on the robot it was started for. The robot cannot
leave its joint limits while you record, and a replay names any joint that would.

Correcting a plan the robot made, from the plan's own menu, is not available yet. See
[Coming soon](coming-soon.md).

## What goes to ROS

The names are relative to the robot's namespace: for a robot in namespace `left`,
`joint_trajectory` is `/left/joint_trajectory`.

| Name | Type | Direction (seen from the app) | When |
| --- | --- | --- | --- |
| `joint_trajectory` | `trajectory_msgs/JointTrajectory` | out | **Publish**: the recorded demonstration |
| `virtual_joint_state` | `sensor_msgs/JointState` | out | Periodically, while **Publish state** is switched on in the tab |
| `interaction` | `std_msgs/Bool` | out | `false` when a recording starts, `true` when it stops |
| `joint_states` | `sensor_msgs/JointState` | in | Shown on the virtual robot while **Mirror joint_states** is switched on |
| `/record_start` | `std_msgs/Bool` | in | Any message starts or stops a recording on the active robot |
| `/user_info` | `std_msgs/String` | in | Text shown in the tab, for messages from your own nodes |

The last two are global, not per robot.

A recorded trajectory holds the joint positions of the virtual robot over time, in radians
(metres for prismatic joints), under the joint names of the robot's ROS description.

## Optional: move the robot with your hand

Two components mirror a tracked hand onto the robot. Add them as children of the plugin
object, one per hand:

- `HandMirror` makes the end effector follow your hand. It needs a reference frame for the
  hand and one for the robot.
- `Robotiq2fGripperMirror` opens and closes a two-finger gripper with your pinch. It reads the
  finger joint and its open and closed positions from the robot's profile.

Both follow the active robot unless you assign a robot on the component.

## Optional: publish a point cloud

Add `PointCloudPublisher` to the plugin object and list the tags of the scene objects to
sample. It publishes `sensor_msgs/PointCloud2` on `/point_cloud`.
