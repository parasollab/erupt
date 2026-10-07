# Changelog

All notable changes to `com.erupt.core` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with Unity's `-preview.N` pre-release tag.

## [Unreleased]

### Added

- `ObstacleResizeWidget`: the obstacle row's `resize` verb now opens an in-world widget beside
  the selected shape (−/+ per dimension, hold to repeat, one undo step per adjustment). Rows
  follow the shape: width/height/depth for a cube, height/diameter for a cylinder, size for a
  sphere or an unknown mesh. Replaces the wrist menu's scale sliders; Guidelines Part 1 P3.
- `TierUiRig.PlaceTierThree`: tier 3 opens in front of the user's head (`summonDistance`,
  `summonHeightOffset`), level with the horizon and facing them, instead of wherever its scene
  anchor happens to be. Switching tabs on an open panel does not move it; `placeInFrontOfUser`
  turns the behaviour off.
- Hand tracking as a full stand-in for the controllers. `OpenXrHandBackend` is no longer a
  stub: pinch selects and drags (`PinchTracker`), thumb-middle pinch emits the thumbstick
  axis, and the Meta menu gesture emits `Activate`. Joint poses are converted from XR Origin
  space to world space, which the stub did not do.
- `WristAnchor` (OpenXR backend): keeps the tier 1 anchor on the left controller or the left
  wrist, whichever is in use.
- Gripper support in `DirectArticulationIKController`: `SetGripperWidth`, `OpenGripper`,
  `CloseGripper`, `ToggleGripper`, `GripperWidth`, `GripperOpenWidth`, `HasGripper`. A width is
  split evenly across the finger joints and clamped to their limits; open/close move at
  `gripperMaxSpeed` (0.1 m/s by default).
- The end effector's `gripper` verb (Open/Close Gripper) is available and bound by `PluginHost`
  when the robot has finger joints.

### Changed

- The Scene tab's new shapes are 0.3 m (`defaultSize`) instead of Unity's 1 m primitives.
  Cylinders get half that in Y so they stand as tall as they are wide (Unity's cylinder mesh is
  2 m at scale 1); `ScenePlacementTab.DefaultScaleFor` holds the rule and the free-spot search
  scales with the size.
- Hands keep their XRI far ray on regardless of finger pose: the demo's `PokeGestureDetector`
  is disabled on the rig, since it turned the ray off in the pointing pose users adopt to aim
  at menus while the ERUPT ray kept drawing.
- `XriInteractableAdapter` tags XRI grab samples with the active modality instead of always
  `Controller`.
- `IRobotModel.GetJointStateNames` / `GetJointStatePositions` now end with the gripper's finger
  joints (metres) on `DirectArticulationIKController`; `JointNames` stays the IK chain only.

### Fixed

- The FR3's prismatic finger joints were not registered with the controller, so nothing held
  them and finger positions in joint states and trajectories were dropped with a warning. They
  are now held at their commanded travel every physics step and follow `ApplyJointState`.

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
