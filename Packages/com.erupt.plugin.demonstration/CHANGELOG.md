# Changelog

All notable changes to `com.erupt.plugin.demonstration` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed (no modes)

- **Breaking, with core.** There is no Teach mode. **Record** on the Demonstrations tab is
  available whenever the robot can record (it reads **Stop** while a recording runs; the
  status reads "Ready to record"), and `/record_start` toggles a recording at any time. A recording ends when it
  is stopped (the button, the topic, `StopDemonstration`) or when its robot leaves; it no
  longer ends because a mode was left. `StartDemonstration` is no longer refused with
  "Demonstrations are recorded in Teach mode only."; `OnModeChanged` is gone.
- The Demonstrations tab is removed when the plugin is unregistered.

### Changed (first run)

- **Correct** is not on a plan's menu. It only started an ordinary recording on the plan's
  robot (from where the robot was, without using the plan), which was confusing; the verb
  is pending in core until correcting a plan is designed. Record on the Demonstrations tab
  and `/record_start` start a recording as before.

### Fixed (buttons that did nothing)

- `StartDemonstration` while another recording runs or for a robot
  without a session, `Publish` with nothing recorded, and `Replay` with nothing to replay,
  without a player or during a recording are refusals on the router with the reason. They
  were a warning in the log or silent.

### Fixed (found against a real ROS 2 stack)

- A published demonstration and the virtual joint state name the joints as the ROS side does
  (the robot's name rule); they carried the Unity robot's names.

### Added (any number of robots)

- `DemonstrationCollectionPlugin.RobotSession`: per robot, a `DemonstrationRecorder` in the
  robot's namespace and a `JointTrajectoryPlayer` for replay (the plugin's own player serves
  the first robot, the others get a copy). `Sessions`, `SessionFor(robot)`, `ActiveSession`,
  `RecordingRobot`; `StartDemonstration(robot)`, `StopDemonstration(robot)`, `Publish(robot)`,
  `Replay(robot)`, `StopReplay(robot)`. One demonstration is recorded at a time; the members
  without a robot, the record-start topic and the tab address the active robot.
- The hand and gripper mirrors without a robot of their own follow the active robot.

### Fixed

- Mirrored joint states (`MirrorInput`) are translated through the robot's name map
  (`RobotInstance.Names`) before they are applied; ROS joint names were applied as they came.

### Added

- Joint limits. A demonstration is checked against the robot's limits when it is finished
  (`DemonstrationRecorder.LastDemonstrationViolations`, `LimitTolerance`); replaying one that
  leaves them plays clamped and tells the user which joint. Joint states mirrored onto the
  robot (`MirrorInput`) that are outside the model's limits are shown clamped and raise one
  `LimitMismatch` refusal per joint (`DemonstrationRecorder.Refused`), which the plugin
  forwards to the interaction router.

### Changed

- The plugin records the registry's active robot and publishes in that robot's ROS namespace;
  `robotNamespace` is gone from `DemonstrationCollectionPlugin`. `DemonstrationRecorder` takes
  the robot's namespaced bus (and its joint state topic) instead of a namespace string.
- `Robotiq2fGripperMirror` takes the driven joint and its open / closed angles from the
  gripper description in the robot's profile (`masterJoint` and `maxJointAngleDegrees` are gone).

### Fixed

- `HandMirror` and `Robotiq2fGripperMirror`: a robot assigned on the mirror (`robotOverride`,
  now a `RobotInstance`) is no longer replaced by the plugin's robot at registration.

## [1.0.0-preview.1] - 2026-09-14

First release of demonstration collection as an ERUPT plugin (Demonstration family).

### Added

- `DemonstrationCollectionPlugin : DemonstrationPlugin` (id `demonstration`) — Teach-mode
  demonstration record / replay / publish through the core contract; `/record_start` toggles
  recording; tier 3 tab `DemosTab`.
- `DemonstrationRecorder` — plain C# joint-space recorder on `IRosBus` + `IRobotModel`
  (`/{ns}/joint_trajectory`, `/{ns}/virtual_joint_state`, `/{ns}/interaction`, `/{ns}/joint_states`).
- `InfoLog` and `PointCloudPublisher` on `IRosBus`.
- `HandMirror` and `Robotiq2fGripperMirror` on `IRobotModel`.
- Tests against `FakeRosBus` / `FakeRobotModel` / `FakeUiHost`.
