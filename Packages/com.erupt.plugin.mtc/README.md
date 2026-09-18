# ERUPT MTC Plugin

MoveIt Task Constructor solutions, execution and pick/place study tooling for ERUPT — a
`Planning` plugin that depends on `com.erupt.plugin.moveit` (planning scene, `moveit_msgs`).
Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`. Generated
bindings: `MESSAGES.md`. ROS interface reference: `Documentation~/ros-interface.md`.

## Setup

1. **Scene.** Drop `Prefabs/MtcPlugin.prefab` next to the `ERUPT` root, after or alongside
   `MoveItPlugin.prefab` (`DependsOn = ["moveit"]`; `PluginHost` orders them).
2. **ROS host.** `mtc_pick_place_server` must be running behind the `action-support`
   ROS-TCP-Endpoint: it serves the `/pick_place` and `/execute_solution` actions and the
   `/get_solution` service, and publishes `/pick_place/description` and
   `/pick_place/statistics`. Launch order and the server's rules: `Documentation~/ros-interface.md` §0.
3. **Use.** MTC plans on the ROS side, so `MtcPlugin.AcceptsGoals` is false and the end-effector
   goal verbs stay with MoveIt. The tier 3 **MTC** tab shows the stage tree with per-stage
   solution counts, the current task's solutions (the server lists them by ascending cost)
   and the steps of the selected one; selecting a solution fetches it (`/get_solution`) and
   gives it `Preview` (on `MtcSolutionPlayer`, mirroring `scene_diff` attachments through the
   `EnvironmentRegistry`) and `Execute` (the `/execute_solution` action, by id). While it
   executes, the running step and its stage are highlighted from the action feedback, and
   **Cancel** stops planning or execution (shown as cancelled, not as an error). In Teach mode
   the tab also starts the pick/place recorder, which is the plan button: grab an object,
   release it where it should go, and the task is sent as a plan-only `/pick_place` goal
   (`executeOnServer` on the recorder plans and executes in one goal, skipping the browser).
   A new plan clears the browser: the server invalidates every earlier task and solution id.
4. **Tests.** Test Runner → `Erupt.Plugins.Mtc.Tests` (PlayMode) against `FakeRosBus`.

## Layout

```
Runtime/MtcPlugin.cs             the plugin (Erupt.Plugins.Mtc): PlanningPlugin, AcceptsGoals = false
Runtime/PickPlaceClient.cs       mtc_pick_place_server client: plan, solution ids, /get_solution, execute, cancel
Runtime/MtcSolutionPlayer.cs     solution preview on the robot + attachment mirroring
Runtime/SolutionsTab.cs          tier 3 tab: solution browser + execution panel, recorder entry
Runtime/MtcClient.cs             LEGACY (pick_place_dynamic_demo): unused by the plugin and the prefab
Runtime/PickPlaceTaskRecorder.cs grab → release capture for the study task
Runtime/Messages/                moveit_task_constructor_msgs + study_interfaces (Erupt.Plugins.Mtc.Messages)
Tests/                           PickPlaceClientTests, SolutionsTabTests, MTCExecutionTests (legacy)
Prefabs/MtcPlugin.prefab
Documentation~/ros-interface.md  topics, services, actions, message fields, node start-up order
```
