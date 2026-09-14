# Plugin refactor — Phase 5 plan (RADER absorb, LfD template proven, rader plugin)

Copied verbatim from `refactor_plan.md` § Phase 5 at the start of the phase.

### Phase 5 — RADER absorb, LfD template proven, rader plugin

- Absorb into core: `CollisionHaptics` → `Erupt.Interaction.Backends.OpenXR` (haptics are a capability), with an `Erupt.*` namespace. Nothing else from RADER moves into core.
- Drop RADER's transform-based kinematics layer outright: `IKSolver`, `CCDIK`, `CCDIKJoint`, `UR5eAnalyticalIK` + `Runtime/Plugins/*` native libs, `SetupIK`, `RobotManager`, `ProcessUrdf`, `TargetSphere`. The project moved to URDF-Importer articulation bodies; `DirectArticulationIKController` (`IRobotModel`) already does what these scripts did by hand, so no `IIkSolver` abstraction is added. Decision recorded 2026-09-11.
- `com.erupt.plugin.rader`: `RaderPlugin : DemonstrationPlugin`; demonstration record/replay/publish ported from `SetupUI` onto `IRosBus` (`/{ns}/joint_trajectory`, `/{ns}/virtual_joint_state`, `/{ns}/interaction`, `/record_start`); `FERL`, `InfoLog`, `PointCloudPublisher` moved as-is onto `IRosBus`; `HandMirror`, `Robotiq2fGripperMirror` kept in the plugin, re-pointed from `RobotManager` (`SetTargetEEPose`, `SetGripperByJointName`, `GetJointAngles`) onto `IRobotModel` (`TrySolveToTarget`, `ApplyJointState`, `GetJointStatePositions`). ArUco/AR scripts, `SetupUI` menus, ur5e prefabs, `RosMessageTypes/Hri` dropped along with the kinematics layer above (all recoverable from the tag and from parasollab/RADER).
- Remove `Packages/RADER` from `.gitmodules`; remove `CollisionHaptics` reference on the rig instance and re-add the core version.
- Gate: compile; `DemonstrationPluginTests` with a fake recorder + `RaderPluginTests` against `FakeRosBus`; boundary test; Quest smoke: Teach mode records a demonstration and publishes.

