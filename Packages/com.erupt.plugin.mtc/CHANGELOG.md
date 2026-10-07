# Changelog

All notable changes to `com.erupt.plugin.mtc` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

Client for `mtc_pick_place_server` (replaces the `pick_place_dynamic_demo` protocol).

### Added

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
- `study_interfaces` bindings: `ExecuteSolution` action, `GetSolution` service.
- `SolutionsTab`: fetch on selection, step and stage highlight from execution feedback,
  cancel for planning as well as execution, plan button disabled while a goal is active.

### Changed

- `PickPlaceActionClient` → `PickPlaceClient` (same script GUID). `PickPlace` bindings regenerated:
  `start_scene_diff`, `max_solutions`, `task_id`, `solution_id`, `error_code`, richer feedback.
- `MtcPlugin` executes by solution id through `PickPlaceClient`; `RankedSolutions` and the
  `SolutionMsg` overload of `SelectSolution` are gone.
- `PickPlaceTaskRecorder` uses the sibling `PickPlaceClient` by default and plans only
  (`executeOnServer` now defaults to false, also in the prefab).

### Deprecated

- `MtcClient` (`/description`, `/statistics`, `/solution`, `/get_solution_<id>`,
  `/execute_task_solution`): no longer used by the plugin and removed from the prefab.

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
- Generated `moveit_task_constructor_msgs` and `study_interfaces` bindings (see `MESSAGES.md`).
- `Documentation~/ros-interface.md` — the MTC ROS interface reference (topics, services,
  actions, message fields, node start-up order).

### Removed

- `MTCDashboardPanel` (UI Toolkit) and the `MTCDataManager` singleton.
