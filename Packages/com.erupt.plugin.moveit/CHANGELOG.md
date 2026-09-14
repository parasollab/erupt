# Changelog

All notable changes to `com.erupt.plugin.moveit` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0-preview.1] - 2026-09-14

First release as an ERUPT plugin (Planning family).

### Added

- `MoveItPlugin : PlanningPlugin` — goal capture from the robot, `/plan_kinematic_path`,
  preview on `JointTrajectoryPlayer`, execution by trajectory publish; verbs `set-goal`, `plan`,
  `preview`, `execute` through the core contract.
- `MoveItPlanningClient` — the transport half (joint-state mirroring, `/query_planner_interface`
  discovery, motion-plan service, execution), plain C# over `IRosBus` + `IRobotModel`.
- `PlannerSettingsTab` — tier 3 tab (pipeline, planner, attempts, allowed time, mirror, plan, reset).
- `MoveItPlanningSceneSync` — mirrors the core `EnvironmentRegistry` into the planning scene;
  `CollisionObjectsListenerSimple` and `AttachedCollisionObjectListener` mirror ROS back.
- Generated `moveit_msgs` bindings in `Erupt.Plugins.MoveIt.Messages` (see `MESSAGES.md`).
- Tests against `FakeRosBus`: planning client, collision-object flow, planner settings tab.

### Removed

- `MoveItPlanningRequestMenuUI` (UI Toolkit panel) and the wrist-menu entry point.
