# ERUPT MTC Plugin

MoveIt Task Constructor solutions, execution and pick/place study tooling for ERUPT — a
`Planning` plugin that depends on `com.erupt.plugin.moveit` (planning scene, `moveit_msgs`).
Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`. Generated
bindings: `MESSAGES.md`. ROS interface reference: `Documentation~/ros-interface.md`.

## Setup

1. **Scene.** Drop `Prefabs/MtcPlugin.prefab` next to the `ERUPT` root, after or alongside
   `MoveItPlugin.prefab` (`DependsOn = ["moveit"]`; `PluginHost` orders them).
2. **ROS host.** `pick_place_dynamic_demo` must be running: it serves `/pick_place`, republishes
   MTC introspection (`/description`, `/statistics`, `/solution`) at 1 Hz because the connector
   has no transient-local QoS, and exposes `/execute_task_solution`.
3. **Use.** MTC plans on the ROS side, so `MtcPlugin.AcceptsGoals` is false and the end-effector
   goal verbs stay with MoveIt. The tier 3 **MTC** tab shows the stage tree with per-stage
   solution counts, the solutions ranked by cost and the cost breakdown of the selected one;
   selecting a solution gives it `Preview` (on `MtcSolutionPlayer`, mirroring `scene_diff`
   attachments through the `EnvironmentRegistry`) and `Execute` (the
   `/execute_task_solution` action). In Teach mode the tab also starts the pick/place
   recorder: grab an object, release it where it should go, and the task is sent as a
   `/pick_place` goal.
4. **Tests.** Test Runner → `Erupt.Plugins.Mtc.Tests` (PlayMode) against `FakeRosBus`.

## Layout

```
Runtime/MtcPlugin.cs             the plugin (Erupt.Plugins.Mtc): PlanningPlugin, AcceptsGoals = false
Runtime/MtcClient.cs             introspection topics, solution cache, /execute_task_solution action client
Runtime/MtcSolutionPlayer.cs     solution preview on the robot + attachment mirroring
Runtime/SolutionsTab.cs          tier 3 tab: stage tree, ranked solutions, costs, recorder entry
Runtime/PickPlaceActionClient.cs /pick_place action (study_interfaces)
Runtime/PickPlaceTaskRecorder.cs grab → release capture for the study task
Runtime/Messages/                moveit_task_constructor_msgs + study_interfaces (Erupt.Plugins.Mtc.Messages)
Tests/                           MTCExecutionTests, PickPlaceActionTests, SolutionsTabTests
Prefabs/MtcPlugin.prefab
Documentation~/ros-interface.md  topics, services, actions, message fields, node start-up order
```
