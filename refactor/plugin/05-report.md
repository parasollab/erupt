# Plugin refactor — Phase 5 report (RADER absorb, LfD template proven, rader plugin)

Implements `05-plan.md`. Branch `design_refactor`, on top of Phase 4 (`446587f`).

## What was built

### Absorbed into core

| From RADER | To | Notes |
|---|---|---|
| `CollisionHaptics` | `Packages/com.erupt.core/Runtime/Interaction/Backends/OpenXR/CollisionHaptics.cs` (`Erupt.Interaction.Backends`) | same serialised fields, so the scene component kept its values when its script GUID was retargeted; gained `SetRobot(root, tip)` |

Nothing else from RADER entered core. The transform-based kinematics layer (`IKSolver`,
`CCDIK`, `CCDIKJoint`/`IKJoint`, `UR5eAnalyticalIK` + `Runtime/Plugins/*` native libs, `SetupIK`,
`RobotManager`, `ProcessUrdf`, `TargetSphere`) was dropped as decided on 2026-09-11: the
articulation-body controller behind `IRobotModel` is the only kinematics path.

### `Packages/com.erupt.plugin.rader` (`Erupt.Plugins.Rader`, Demonstration family)

| File | Role |
|---|---|
| `Runtime/RaderPlugin.cs` | `RaderPlugin : DemonstrationPlugin`, id `rader`. Owns the recorder, FERL, info log, replay; re-points `HandMirror` / `Robotiq2fGripperMirror` children and a `PointCloudPublisher` sibling at the context. `/record_start` toggles recording (RADER convention), Teach only |
| `Runtime/DemonstrationRecorder.cs` | plain C#: `SetupUI`'s record / stop / publish / mirror / state-stream on `IRosBus` + `IRobotModel`. Topics `/{ns}/joint_trajectory`, `/{ns}/virtual_joint_state`, `/{ns}/interaction` (false on start, true on stop), `/{ns}/joint_states` (mirror). Time is injected (`Tick(now)`) so it runs against `FakeRosBus` |
| `Runtime/DemosTab.cs` | tier 3 tab: Record/Stop, Replay, Publish, "Publish state", "Mirror joint_states", FERL Resume — 6 interactive elements (Part 8 ≤ 7). Replaces `FullMenu` + `FERLPopup` |
| `Runtime/FerlFeedback.cs` | `/feedback_request` → `/req_satisfied` → `/feedback_response`; a request summons the tab (no popup, Part 4) |
| `Runtime/InfoLog.cs` | `/user_info` → `Debug.Log` + last line in the tab |
| `Runtime/HandMirror.cs`, `Runtime/Robotiq2fGripperMirror.cs` | one instance per hand; `RobotManager.SetTargetEEPose` → `IRobotModel.TrySolveToTarget` (position-only), `SetGripperByJointName` → `ApplyJointState` (radians). Hand subsystem via `SubsystemManager` like `OpenXrHandBackend`; `Mirror(...)` is public so tests can drive it |
| `Runtime/PointCloudPublisher.cs` | as before, on `IRosBus` (`Initialise(bus)`, falls back to `RosBus.Instance`); inert with no tags |
| `Runtime/Messages/` | empty `Erupt.Plugins.Rader.Messages` (template shape); `hri/JointQuery` dropped |
| `Prefabs/RaderPlugin.prefab` | `RaderPlugin` + `JointTrajectoryPlayer` (replay) |
| `Tests/` | `DemonstrationRecorderTests` (9), `RaderPluginTests` (12), `DemosTabTests` (3) — all against `FakeRosBus` / `FakeRobotModel` / `FakeUiHost` |

### Core tests

- `Tests/Support/FakeRobotModel.cs` (`Erupt.TestSupport`, now references `Erupt.Robot`): settable
  positions, records applied joint states and IK targets.
- `Tests/Plugins/DemonstrationPluginTests.cs` + `FakeDemonstrator` in `FakePlugins.cs`: the
  family contract — demos tab and `correct` on register, samples only in Teach + recording,
  leaving Teach stops, unregister stops the feed, no-UI-host still gates.
- `PluginGeneratorTests.ShippedPlugins_HaveTheTemplateShape` now also checks
  `com.erupt.plugin.rader` against the Demonstration shape (`: DemonstrationPlugin`).

### Scene and repo

- `KitchenFR3.unity`: the rig's `CollisionHaptics` component now points at the core script
  (GUID swap; values kept). `RaderPlugin` prefab instance added at the scene root next to
  `MtcPlugin`. `tipLink` / `robotRoot` on the haptics were already unassigned before this
  phase and still are — the component is inert until they are set (or `SetRobot` is called).
- `Packages/RADER` submodule removed (`git rm`, `.gitmodules` entry gone);
  `packages-lock.json` drops `com.parasol.rader` and lists `com.erupt.plugin.rader`.
  `.git/modules/Packages/RADER` was left in place; delete it by hand if wanted (the fork is
  pushed at `4fa1053`).
- Docs: `architecture.md` (package, assembly, Demonstration + Haptics flows), core `README.md`,
  `plugin-authoring.md` (shipped Demonstration example).

## Deviations from RADER, all deliberate

- **Single robot.** `IEruptContext.Robot` is one robot; RADER's multi-robot list, robot
  dropdown, joint dropdown/slider and set-home / go-home are gone (core owns direct joint
  manipulation; homes belong to the robot model if ever needed).
- **No joint remap.** RADER negated degrees and remapped UR5e indices; the recorder writes
  the model's ROS-convention radians under the model's joint names. `/joint_states` mirroring
  applies by name.
- **Mirror decoupled from record.** RADER toggled input mirroring whenever recording toggled;
  here "Mirror joint_states" is its own toggle (off by default) so a demonstration is not
  fought by the real robot's state.
- **Replay once.** `JointTrajectoryPlayer` loops; the plugin stops it after one pass.
- **Late ticks.** A stalled frame yields one late point at its real time rather than a burst.
- **Position-only hand mirroring.** The core IK has no orientation target; the indicator
  carries the hand rotation, the robot follows position.

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS — clean in the open Editor (6000.3.15f1). A headless `-batchmode` run from the agent shell is not usable as a gate: it fails on licensing before any UPM package is registered |
| `DemonstrationPluginTests` (core, fake recorder) | PASS — 6/6 (PlayMode run 2026-09-14) |
| `RaderPluginTests` + `DemonstrationRecorderTests` + `DemosTabTests` (against `FakeRosBus`) | PASS — 24/24 on the re-run. First run was 20/24 (PlayMode 180/184 overall): four recorder tests failed because a `float` interval widened to double (`0.1f` → `0.10000000149`) made an on-schedule tick read as not due; `Tick` now compares with a 1 µs tolerance |
| EditMode `AssemblyBoundaryTests` + `PluginGeneratorTests` (rader shape) | PASS — 12/12 (3 boundary, 9 generator incl. the RADER Demonstration-shape case; read from the Editor's TestResults.xml, 2026-09-14) |
| Quest smoke: Teach mode records a demonstration and publishes | DEFERRED — needs a device and a ROS host (as with the Phase 3 ROS flows); check `/{ns}/interaction` false→true and one `/{ns}/joint_trajectory` when both are available |

## Next

Phase 6 (`refactor_plan.md` § Phase 6): rebrand, docs, release.
