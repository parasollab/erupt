# Changelog

All notable changes to `com.erupt.plugin.mtc` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed (no modes)

- **Breaking, with core.** There is no Teach mode. **Record pick & place** on the MTC tab is
  available whenever the robot's pick-and-place server is there and idle (it used to read
  "Record (Teach mode)" outside Teach). A recording ends with **Stop recording**; leaving a
  mode no longer stops it. `MtcPlugin.InTeachMode` and `TeachModeChanged` are gone. The
  refusals that named the mode say "record a pick & place on the MTC tab".
- The MTC tab is removed when the plugin is unregistered (core).

### Fixed (buttons that did nothing)

- A failed Execute of a solution reaches the user at the solution's handle (core's execute
  verb now reports the status): "This solution is not part of the current plan. Plan
  again.", "The server is busy with another goal.", and the server's own words when it
  rejects or aborts.
- `RequestPlan` with no pick-and-place client or no solution yet is a refusal; it returned
  without a word.
- A pick-and-place recording that is stopped with nothing to send (no object selected, or the
  object never put down) is a refusal at the robot's end effector that says what to do; it
  was a warning in the log, and the tab went back to "Idle".

### Added (a robot's base is moved)

- When a robot's base is put somewhere else (`RobotInstance.PlaceBase`), its pick-and-place
  task is forgotten (`PickPlaceClient.ForgetTask`): the solutions were found for the base
  where it was. The move is refused while a task is being planned or a solution executed
  for that robot.

### Fixed (found against a real ROS 2 stack)

- A placed object ended 12.5 cm above where it was demonstrated: the MTC task raised every
  place pose by half the height of its demo object. `mtc_pick_place_server` now places the
  object's own frame at the pose it is given, which is what the app sends
  (`placeHeightOffsetMeters` stays 0).
- `MtcSolutionPlayer` announces a preview (`IRobotModel.TakeControl`), so a joint-state mirror
  does not overwrite it. The stale `attachedListener` key is gone from the prefab.

### Added (any number of robots)

- `MtcPlugin.RobotSession`: per robot, the `PickPlaceClient`, the `MtcSolutionPlayer`, the
  `PickPlaceTaskRecorder` and the solutions with their preview scope. The components on the
  plugin's object serve the first robot; every further robot gets copies with the same
  settings. `MtcPlugin.Sessions`, `SessionFor(robot)`, `ActiveSession`,
  `SelectSolution(robot, id)`; the members without a robot address the active robot, which is
  also the one the tab shows. A new plan on one robot clears that robot's solutions only.
- A robot whose ROS side has no pick-and-place server keeps its session:
  `RobotSession.Available` is false while its action is not ready, the tab reads
  "Pick-and-place not available for this robot" (`SolutionsTab.UnavailableText`) and offers no
  recording, and the other robots are unaffected.
- `PickPlaceClient.SetRobot` (place poses are stamped with the robot's planning frame id) and
  `Release`; `PickPlaceTaskRecorder.SetRobot` (the place pose is expressed in the robot's
  planning frame), `SetPickPlaceClient`, `Release`.

### Changed (any number of robots)

- `MtcSolutionPlayer` applies the attach / detach steps of an executing solution through the
  attach mirror of the robot being executed (`AttachedCollisionObjectListener.For(robot)`),
  and reads start-scene poses in that robot's planning frame.
- The legacy task topic of `PickPlaceTaskRecorder` is the relative name `pick_place_task` (it
  lands in the robot's namespace; `/pick_place_task` for a robot without one).
- A solution fetched for a robot that is not the active one no longer takes the selection.

Client for `mtc_pick_place_server` (replaces the `pick_place_dynamic_demo` protocol).

### Added

- Joint limits. Every step of a solution is checked against the robot's limits when the
  solution is fetched (`PlanResult.LimitViolations`, with the step in
  `JointLimitViolation.Segment`); the user is told which joint leaves them, the preview plays
  clamped, and `MtcPlugin.Execute` refuses the solution (`ExecutionPhase.Refused`) unless the
  plugin's `allowExecutionOutsideLimits` is on. `PickPlaceClient.ExecutionGuard` is asked before
  a solution id is sent to `execute_solution`, so the client refuses it as well
  (`REFUSED: ...`, outcome `Rejected`). A stage's partial or failed solution reports its
  violations when it is previewed.
- `MtcSolutionPlayer.OnRefusal`: joint states mirrored during execution that are outside the
  robot model's limits are shown clamped and raise one `LimitMismatch` refusal per joint.
- `MtcSolutionPlayer` during execution: while the `PickPlaceClient` executes a solution the
  robot follows `/joint_states` (`IsMirroring`, `StartLiveMirror` / `StopLiveMirror`, any
  preview stopped first), and each attach/detach step of the solution is applied through
  `AttachedCollisionObjectListener.ApplyExpected` the moment the server's step feedback reports
  it (`ExecutionStepApplied`; `ForgetExpected` when an execution starts). `MtcPlugin` hands the
  player its client and bus (`SetPickPlaceClient`, `SetRos`).
- `PickPlaceClient.ForgetTask()` drops the current task without planning again (refused while a
  goal is active); `placeHeightOffsetMeters` is added to the ROS z of every `PlacePose`.
- `PickPlaceTaskRecorder`: `CancelRecording`, `NotifyObjectReleased` (a release that did not
  come from an XRI grab), `OnTaskCaptured`, `OnRecordingDiscarded`.
- Stage previews (ported from the `mtc` branch's dashboard). `MtcPlugin.PreviewStageId` scopes
  the preview verb to one stage of the selected solution (`SetPreviewStage`,
  `PreviewableStages`, `TryGetStageSteps`; a container stage covers its children's steps);
  `MtcSolutionPlayer.PlaySolution(solution, firstStep, lastStep)` fast-forwards the earlier
  steps and holds the end state for `stagePreviewHoldSeconds`.
- Stage attempts: `MtcPlugin.AttemptsOf(stage)` lists a stage's partial and failed solutions
  from the statistics; `PreviewStageSolution(id, failed)` fetches one with `include_start_scene`
  and previews it from that state (`MtcSolutionPlayer.PlaySolution(solution, useStartScene)`:
  robot joints, world object poses and attachments applied, then restored). A failed attempt
  reports the planner's comment. Refused while a solution executes; never executable.
- `SolutionsTab`: **Preview** (scope) and **Attempt** cycle buttons; `MaxSolutionButtons` is 3
  (a default plan asks for `max_solutions` 3) so the tab stays at seven interactive elements.
  `MtcPlugin.PreviewStatus` lands on the status line.
- `MtcSolutionPlayer`: ROS → Unity joint and link name mapping (`rosNamePrefix` /
  `unityNamePrefix`, `panda_` → `fr3_`), gripper-only segments skipped, `LastProblem` /
  `OnProblem` when nothing can play, `IsPlaying`; attachments are placed at the message's
  link-relative pose (`AttachedObjectPlacement`, exact link only) and objects the live robot is
  carrying (`EnvironmentObject.AttachedTo`) are left alone.
- `PickPlaceClient`: `FetchSolution(..., includeStartScene)` with a separate cache,
  `IsKnownSolutionId` (any stage's `solved[]` / `failed[]`), and `getSolutionTimeoutSeconds`
  so a response that never deserialises is reported instead of waited for forever.
- `PickPlaceClient` — plan (`/pick_place`, plan-only or plan-and-execute), solution ids from
  `/pick_place/statistics` stage 1, `/get_solution`, `/execute_solution` by id, cancel while
  planning or executing, cancel on destroy / pause / quit.
- `erupt_interfaces` bindings: `ExecuteSolution` action, `GetSolution` service.
- `SolutionsTab`: fetch on selection, step and stage highlight from execution feedback,
  cancel for planning as well as execution, plan button disabled while a goal is active.

### Changed

- The plugin works on the registry's active robot and uses its namespaced bus: the client's
  names are relative (`pick_place`, `execute_solution`, `get_solution`,
  `pick_place/description`, `pick_place/statistics`). `MtcSolutionPlayer.SetRobot` takes a
  `RobotInstance` (`SetRos`, the serialised controller, the `panda_` / `fr3_` prefixes and the
  joint state topic are gone: the robot supplies its bus, its name rule and its joint state
  topic). `MtcPlugin.prefab` carries the player.

- Every script is in the `Erupt.Plugins.Mtc` namespace (script GUIDs unchanged).
- `PickPlaceClient`, `PickPlaceTaskRecorder` and `MtcSolutionPlayer` no longer fall back to the
  static ROS bus: `MtcPlugin` hands them the context bus at registration and they stay idle
  until then. `PickPlaceClient.Initialise` may be called after `Start`.
- `MtcSolutionPlayer` plays gripper-only segments and applies gripper joints from a start
  scene when the robot has them (joint names are matched against the whole joint state, not
  the arm chain only); the preview restores the fingers with the arm.
- `PickPlaceTaskRecorder`: selecting an object again during a recording keeps the pick pose it
  was first watched at instead of re-capturing it where it is.
- `PickPlaceActionClient` → `PickPlaceClient` (same script GUID). `PickPlace` bindings regenerated:
  `start_scene_diff`, `max_solutions`, `task_id`, `solution_id`, `error_code`, richer feedback.
- `MtcPlugin` executes by solution id through `PickPlaceClient`; `RankedSolutions` and the
  `SolutionMsg` overload of `SelectSolution` are gone.
- `PickPlaceTaskRecorder` uses the sibling `PickPlaceClient` by default and plans only
  (`executeOnServer` now defaults to false, also in the prefab).

### Removed

- `MtcClient` (`/description`, `/statistics`, `/solution`, `/get_solution_<id>`,
  `/execute_task_solution`) and its `MTCExecutionTests`; the plugin talks to
  `mtc_pick_place_server` through `PickPlaceClient`.

## [1.0.0-preview.1] - 2026-09-14

First release as an ERUPT plugin (Planning family, depends on `com.erupt.plugin.moveit`).

### Added

- `MtcPlugin : PlanningPlugin` — MTC plans on the ROS side; "plan" takes the best solution,
  preview plays it on `MtcSolutionPlayer`, execute sends it to `/execute_task_solution`.
  `AcceptsGoals` is false, so end-effector goal verbs stay with MoveIt.
- `MtcClient` — introspection (`/description`, `/statistics`, `/solution`), solution cache and
  the native `/execute_task_solution` action client, bus injected.
- `SolutionsTab` — tier 3 tab: stage tree, ranked solutions, cost breakdown, pick/place
  recorder entry (Teach mode).
- `PickPlaceActionClient` (`/pick_place` action) and `PickPlaceTaskRecorder`.
- Generated `moveit_task_constructor_msgs` and `erupt_interfaces` bindings (see `MESSAGES.md`).
- `Documentation~/ros-interface.md` — the MTC ROS interface reference (topics, services,
  actions, message fields, node start-up order).

### Removed

- `MTCDashboardPanel` (UI Toolkit) and the `MTCDataManager` singleton.
