# ERUPT MTC Plugin

MoveIt Task Constructor solutions, execution and pick/place task recording for ERUPT — a
`Planning` plugin that depends on `com.erupt.plugin.moveit` (planning scene, `moveit_msgs`).
Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`. Generated
bindings: `MESSAGES.md`. ROS interface reference: `Documentation~/ros-interface.md`.

## Setup

1. **Scene.** Drop `Prefabs/MtcPlugin.prefab` next to the `ERUPT` root, after or alongside
   `MoveItPlugin.prefab` (`DependsOn = ["moveit"]`; `PluginHost` orders them). One plugin
   serves every robot in the scene: each robot has a session (`MtcPlugin.RobotSession`) with
   its own `PickPlaceClient`, `MtcSolutionPlayer`, `PickPlaceTaskRecorder` and solutions. The
   components on the prefab serve the first robot; further robots get copies with the same
   settings. The tab shows the active robot's session ("MTC · left" with several robots).
2. **ROS host.** Each robot has its own `mtc_pick_place_server` in its namespace, behind the
   `action-support` ROS-TCP-Endpoint: it serves the `pick_place` and `execute_solution`
   actions and the `get_solution` service, and publishes `pick_place/description` and
   `pick_place/statistics`. The names are relative to the robot's namespace: `/pick_place`,
   ... for a robot without a namespace and `/left/pick_place`, ... for one in `left`. A robot
   whose ROS side runs no such server keeps working otherwise: its tab reads "Pick-and-place
   not available for this robot" and the other robots are unaffected. Launch order and the
   server's rules: `Documentation~/ros-interface.md` §0.
3. **Use.** MTC plans on the ROS side, so `MtcPlugin.AcceptsGoals` is false and the end-effector
   goal verbs stay with MoveIt. The tier 3 **MTC** tab shows the stage tree with per-stage
   solution counts, the current task's solutions (the server lists them by ascending cost)
   and the steps of the selected one; selecting a solution fetches it (`/get_solution`) and
   gives it `Preview` (on `MtcSolutionPlayer`, mirroring `scene_diff` attachments through the
   `EnvironmentRegistry`) and `Execute` (the `/execute_solution` action, by id). **Preview**
   cycles the preview verb's scope: the whole solution, then each stage that has steps in it
   (a container stage covers its children), so one stage can be watched on its own — earlier
   steps are applied instantly so it starts where it really begins. **Attempt** cycles the
   scoped stage's partial and failed solutions (from the statistics); each one is fetched with
   its `start_scene` and previewed from there, a failed one shows the planner's reason instead.
   Attempts are never executable and never replace the selected solution. The player maps ROS
   joint and link names to the Unity robot through the robot profile's name rule and skips
   gripper-only segments. While a solution executes, the running step and its stage are
   highlighted from the action feedback, and
   **Cancel** stops planning or execution (shown as cancelled, not as an error).
   **Record pick & place** on the tab starts the pick/place recorder, which is the plan
   button (it reads **Stop recording** while it runs; there is no mode to switch to): grab an object,
   release it where it should go, and the task is sent as a plan-only `/pick_place` goal
   (`executeOnServer` on the recorder plans and executes in one goal, skipping the browser).
   A new plan clears the browser: the server invalidates every earlier task and solution id.
4. **Tests.** Test Runner → `Erupt.Plugins.Mtc.Tests` (PlayMode) against `FakeRosBus`.

## Layout

```
Runtime/MtcPlugin.cs             the plugin (Erupt.Plugins.Mtc): PlanningPlugin, AcceptsGoals = false
Runtime/PickPlaceClient.cs       mtc_pick_place_server client: plan, solution ids, /get_solution, execute, cancel
Runtime/MtcSolutionPlayer.cs     solution preview on the robot: whole / stage range / from start_scene, attachment mirroring, name mapping; during execution, live joint states and attach/detach from step feedback
Runtime/SolutionsTab.cs          tier 3 tab: solution browser, preview scope, stage attempts, execution panel, recorder entry
Runtime/PickPlaceTaskRecorder.cs grab → release capture of a pick/place task
Runtime/Messages/                moveit_task_constructor_msgs + erupt_interfaces (Erupt.Plugins.Mtc.Messages)
Tests/                           PickPlaceClientTests, SolutionsTabTests, MtcSolutionPlayerTests, MtcExecutionStepTests, PickPlaceTaskRecorderTests
Prefabs/MtcPlugin.prefab
Documentation~/ros-interface.md  topics, services, actions, message fields, node start-up order
```
