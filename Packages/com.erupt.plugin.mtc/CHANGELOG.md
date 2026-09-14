# Changelog

All notable changes to `com.erupt.plugin.mtc` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
