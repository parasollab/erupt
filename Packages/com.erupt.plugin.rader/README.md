# ERUPT RADER Plugin

RADER (the Parasol Lab demonstration-collection interface) as an ERUPT `Demonstration`
plugin. Records joint-space demonstrations of the virtual robot in Teach mode, replays and
publishes them, answers FERL feedback requests, and optionally mirrors tracked hands onto the
end effector and a Robotiq 2F gripper. Contract and lifecycle:
`com.erupt.core/Documentation~/plugin-authoring.md`. Topics: `MESSAGES.md`.

## Setup

1. **Scene.** Drop `Prefabs/RaderPlugin.prefab` next to the `ERUPT` root. `PluginHost` finds
   it on Start and registers it; the robot, ROS bus and UI come from the context.
2. **Namespace.** Set `robotNamespace` on `RaderPlugin` (e.g. `ur5e`) so the topics become
   `/ur5e/joint_trajectory`, `/ur5e/virtual_joint_state`, `/ur5e/interaction`,
   `/ur5e/joint_states`. Leave it empty for un-prefixed topics.
3. **Teach mode.** Switch the app to Teach (tier 1 mode control). The tier 3 **RADER** tab has
   Record / Stop, Replay, Publish, the two streaming toggles and FERL's Resume. The
   trajectory verb `correct` also starts a recording, and any message on `/record_start`
   toggles one.
4. **Hands (optional).** Add `HandMirror` and/or `Robotiq2fGripperMirror` as children of the
   plugin object, one per hand; the plugin points them at the context's robot on
   registration. `HandMirror` needs the human and robot reference frames and an indicator
   transform; the gripper mirror needs the finger joint's URDF name.
5. **Point cloud (optional).** Add `PointCloudPublisher` on the plugin object and set the tags
   of the scene objects to sample; the plugin hands it the bus.
6. **Tests.** Test Runner → `Erupt.Plugins.Rader.Tests` (PlayMode): recorder, plugin and tab
   against `FakeRosBus` / `FakeRobotModel` / `FakeUiHost`.

## What changed from the RADER submodule

- Demonstrations are sampled from `IRobotModel` (ROS-convention radians, the model's joint
  names); RADER's UR5e-specific index remap, degree negation and transform-based IK
  (`IKSolver`, `CCDIK`, `UR5eAnalyticalIK`, `SetupIK`, `RobotManager`, `ProcessUrdf`,
  `TargetSphere`) are gone — the articulation-body controller is the only kinematics path.
- One robot per plugin instance (the context's); RADER's multi-robot dropdown, joint
  slider, set-home / go-home and the `FullMenu` / `FERLPopup` prefabs are replaced by the
  tier 3 tab. FERL requests summon the tab instead of opening a popup.
- `CollisionHaptics` moved into core (`Erupt.Interaction.Backends.OpenXR`).
- ArUco / AR scripts, camera permission helpers, `XRKnobAlt`, `XRPokeFollowAffordanceFill`,
  the UR5e / gripper prefabs and the `hri/JointQuery` message were dropped; all of it is
  recoverable from the `pre-plugin-refactor` tag or parasollab/RADER.

## Layout

```
Runtime/RaderPlugin.cs               the plugin (Erupt.Plugins.Rader): DemonstrationPlugin
Runtime/DemonstrationRecorder.cs     joint-space recorder on IRosBus + IRobotModel (plain C#)
Runtime/DemosTab.cs                  tier 3 tab (6 interactive elements)
Runtime/FerlFeedback.cs              /feedback_request → /req_satisfied → /feedback_response
Runtime/InfoLog.cs                   /user_info
Runtime/HandMirror.cs                hand → end effector (IRobotModel.TrySolveToTarget)
Runtime/Robotiq2fGripperMirror.cs    pinch → finger joint (+ pose)
Runtime/PointCloudPublisher.cs       tagged meshes → sensor_msgs/PointCloud2
Runtime/Messages/                    (no generated messages) Erupt.Plugins.Rader.Messages
Tests/                               DemonstrationRecorderTests, RaderPluginTests, DemosTabTests
Prefabs/RaderPlugin.prefab           RaderPlugin + JointTrajectoryPlayer
```
