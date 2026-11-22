# ERUPT Demonstration Plugin

Demonstration collection as an ERUPT `Demonstration` plugin. Records joint-space
demonstrations of the virtual robot, replays and publishes them, and optionally
mirrors tracked hands onto the end effector and a Robotiq 2F gripper. Contract and lifecycle:
`com.erupt.core/Documentation~/plugin-authoring.md`. Topics: `MESSAGES.md`.

## Setup

1. **Scene.** Drop `Prefabs/DemonstrationCollectionPlugin.prefab` next to the `ERUPT` root.
   `PluginHost` finds it on Start and registers it; the robots, ROS bus and UI come from the
   context. One plugin serves every robot in the scene: each robot has its own recorder and
   replay player (`DemonstrationCollectionPlugin.RobotSession`); one demonstration is recorded
   at a time, on the robot it was started for.
2. **Namespace.** Each robot's topics are in that robot's ROS namespace
   (`RobotInstance.Namespace`, e.g. `ur5e`): `/ur5e/joint_trajectory`,
   `/ur5e/virtual_joint_state`, `/ur5e/interaction` and the robot's joint state topic
   (`/ur5e/joint_states`). A robot without a namespace gives un-prefixed topics.
   `/record_start`, `/user_info` and `/point_cloud` are global; `/record_start` toggles a
   recording on the active robot. Mirrored joint states are translated through the robot's
   name map.
3. **Recording.** There is no mode to switch to. The tier 3 **Demonstrations** tab has
   **Record** (which reads **Stop** while a recording runs), **Replay**, **Publish** and the
   two streaming toggles (**Publish state**, **Mirror joint_states**), and shows the last
   `/user_info` message. A selected plan has the verb **Correct** on its menu: it starts a
   recording on the plan's robot and opens the Demonstrations tab, where the recording shows
   and is stopped. Any message on `/record_start` toggles a recording too, at any time. A
   recording ends when it is stopped (the button, the topic) or when its robot leaves the
   scene. What cannot be done (a second recording, publishing or replaying nothing) is a
   refusal with the reason.
4. **Hands (optional).** Add `HandMirror` and/or `Robotiq2fGripperMirror` as children of the
   plugin object, one per hand; the plugin points them at the active robot (and at the new
   one whenever the active robot changes), unless a robot is assigned on the mirror itself,
   which then wins. `HandMirror` needs the
   human and robot reference frames and an indicator transform; the gripper mirror takes the
   finger joint and its open and closed angles from the gripper description in the robot's
   profile.
5. **Point cloud (optional).** Add `PointCloudPublisher` on the plugin object and set the tags
   of the scene objects to sample; the plugin hands it the bus.
6. **Tests.** Test Runner → `Erupt.Plugins.Demonstration.Tests` (PlayMode): recorder, plugin
   and tab against `FakeRosBus` / `FakeRobotModel` / `FakeUiHost`.

## Conventions

- Demonstrations are sampled from `IRobotModel`: ROS-convention radians under the model's
  joint names. The articulation-body controller is the only kinematics path.
- One plugin instance serves every robot; one demonstration is recorded at a time.
- Other plugins depend on this one by the id `demonstration`
  (`DemonstrationCollectionPlugin.PluginId`).

## Layout

```
Runtime/DemonstrationCollectionPlugin.cs  the plugin (Erupt.Plugins.Demonstration): DemonstrationPlugin
Runtime/DemonstrationRecorder.cs          joint-space recorder on IRosBus + IRobotModel (plain C#)
Runtime/DemosTab.cs                       tier 3 tab (5 interactive elements)
Runtime/InfoLog.cs                        /user_info
Runtime/HandMirror.cs                     hand → end effector (IRobotModel.TrySolveToTarget)
Runtime/Robotiq2fGripperMirror.cs         pinch → finger joint (+ pose)
Runtime/PointCloudPublisher.cs            tagged meshes → sensor_msgs/PointCloud2
Runtime/Messages/                         (no generated messages) Erupt.Plugins.Demonstration.Messages
Tests/                                    DemonstrationRecorderTests, DemonstrationCollectionPluginTests, DemosTabTests
Prefabs/DemonstrationCollectionPlugin.prefab   DemonstrationCollectionPlugin + JointTrajectoryPlayer
```
