# Changelog

All notable changes to `com.erupt.core` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with Unity's `-preview.N` pre-release tag.

## [1.0.0-preview.1] - 2026-09-14

First release of ERUPT (Extended Reality Universal Programming Toolkit) as a core package with
a plugin contract. Extracted from the single `xrviz` assembly of the former XRViz project over
phases 0–5 of the plugin refactor (`refactor/plugin/*-report.md`).

### Added

- Plugin contract (`Erupt.Plugins`): `IEruptPlugin`, `EruptPluginBehaviour`, `PluginHost`
  (scene discovery, dependency ordering, mode fan-out), `IEruptContext`, the `PlanningPlugin`
  and `DemonstrationPlugin` family bases, `PlanResult`, `PlanPreferences`, `TrajectorySelectable`.
- Robot model seam (`Erupt.Robot`): `IRobotModel` over `DirectArticulationIKController`,
  `JointTrajectoryPlayer`, `SpawnGhosts`, `RobotInteractionRouterBinding`.
- Environment mirror (`Erupt.Environment`): `EnvironmentRegistry`, `EnvironmentObject`,
  `IEnvironmentSync`, `ObstacleFactory` and undoable obstacle commands with no planner coupling.
- ROS transport seam (`Erupt.Ros`): `IRosBus`, `RosBus`, `LiveRosBus` (publish, subscribe,
  services, ROS 2 actions), `FakeRosBus` in `Erupt.TestSupport`.
- Tier UI (`Erupt.Ui`): `VerbRegistry` seeded from the guideline verb table, `IUiHost`
  implemented by `TierUiRig`, `IWorldWidget`, `ScenePlacementTab`, XR raycast installer.
- One selection system: `SelectionService` with `SelectionHighlighter`; robot-link and
  end-effector selection published through it.
- Haptics as a capability: `CollisionHaptics` in the OpenXR backend (from RADER).
- Editor: `ERUPT > Plugins > Create Plugin...` generator with Blank / Planning /
  Demonstration templates, prefab creation after compilation, "Add to open scene".
- `EruptCore.prefab` (the `ERUPT` root) and the `Documentation~` set: `architecture.md`,
  `plugin-authoring.md`, `design-guidelines.md`.
- Tests: per-assembly PlayMode suites, `Erupt.TestSupport` fakes, EditMode
  `AssemblyBoundaryTests` (core never references plugins or the app) and `PluginGeneratorTests`.

### Removed

- The legacy wrist menu, UI Toolkit panels and the dual selection system; AR / ArUco scenes;
  RADER's transform-based kinematics layer. All recoverable from the `pre-plugin-refactor` tag.
