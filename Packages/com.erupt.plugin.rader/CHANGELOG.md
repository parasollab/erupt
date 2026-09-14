# Changelog

All notable changes to `com.erupt.plugin.rader` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0-preview.1] - 2026-09-14

First release of RADER as an ERUPT plugin (Demonstration family), replacing the RADER
submodule (`parasollab/RADER`, branch `xrviz`).

### Added

- `RaderPlugin : DemonstrationPlugin` — Teach-mode demonstration record / replay / publish
  through the core contract; `/record_start` toggles recording; tier 3 tab `DemosTab`.
- `DemonstrationRecorder` — plain C# joint-space recorder on `IRosBus` + `IRobotModel`
  (`/{ns}/joint_trajectory`, `/{ns}/virtual_joint_state`, `/{ns}/interaction`, `/{ns}/joint_states`).
- `FerlFeedback`, `InfoLog`, `PointCloudPublisher` on `IRosBus`.
- `HandMirror` and `Robotiq2fGripperMirror` re-pointed from `RobotManager` onto `IRobotModel`.
- Tests against `FakeRosBus` / `FakeRobotModel` / `FakeUiHost`.

### Removed

- RADER's transform-based kinematics (`IKSolver`, `CCDIK`, `UR5eAnalyticalIK`, `SetupIK`,
  `RobotManager`, `ProcessUrdf`, `TargetSphere`), the `SetupUI` menus, ArUco / AR scripts,
  UR5e prefabs and the `hri/JointQuery` message. `CollisionHaptics` moved into core.
