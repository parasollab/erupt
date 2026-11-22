# Changelog

All notable changes to `com.erupt.core` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/) with Unity's `-preview.N` pre-release tag.

## [Unreleased]

### Added (previews on a copy of the robot)

- `RobotGhosts.PlayPreview(trajectory)` / `StopPreview()` / `IsPreviewPlaying`: plays a
  trajectory, in a loop, on a copy of the robot in `previewGhostColor` (blue) while the robot
  stays where it is; the copy is posed through the robot's joint limits like every ghost.
- `JointTrajectoryPlayer.StartReplay` / `RestartReplay` take an optional `IJointPoseTarget`
  to play onto instead of the robot (the robot is then not touched), and `IsReplaying`.
- `PlanningPlugin.IsPreviewPlaying(plan)` and the shared verb `stop-preview` (**Stop
  Preview**), shown on a plan's menu in the place of **Preview** while a planner says that
  plan's preview is playing. The default is no, so a planner whose preview ends by itself
  is unchanged.

### Removed (the modes: Build, Plan, Teach)

- **Breaking change to the plugin contract.** ERUPT has no application modes any more.
  `AppMode`, `ModeManager`, `IEruptContext.Modes` (and `EruptContext.Modes`),
  `IEruptPlugin.OnModeChanged` and `EruptPluginBehaviour.OnModeChanged` are gone, and with
  them the host's fan-out of mode changes, tier 1's mode button (`ModeIndicatorControl`),
  the `ModeManager` component on `EruptCore.prefab`, the `modes` fields of `PluginHost` and
  `ScenePlacementTab`, and `DemonstrationPlugin.InTeachMode`. The modes gated three things
  (the Scene tab's Add buttons, the two Record buttons and the `correct` verb) and cost a
  first-time user a "why is this greyed out"; each of those actions is already an explicit
  button press. To migrate a plugin: delete its `OnModeChanged` override and whatever it
  read from `Modes`, and let the control that was gated start the thing. The plugin
  templates (`ERUPT > New Plugin…`) no longer generate a mode hook.
- What the modes gated is always available: **Add Cube / Add Sphere / Add Cylinder** on the
  Scene tab (its hint is gone; there is nothing to explain), **Record** on a demonstration
  tab, and **Correct** on a plan's menu whenever a demonstration plugin is registered.
  `DemonstrationPlugin` feeds samples while a demonstration is being recorded, and a
  recording ends when it is stopped: leaving a mode no longer ends it.
- The floating "≡ Scene Menu" label next to the left controller (`MenuLabel` in the app's
  XR rig prefab and in the rigs of the Starter Scene and the AR scene). Tier 1's Menu
  button replaces it.

### Changed (tier 1: Menu, Undo, Redo, on the wrist like a watch)

- **Tier 1 holds Menu, Undo, Redo.** `MenuControl` (`TierOneBar.Menu`) raises `Pressed`,
  and the rig toggles tier 3: the same toggle as the controller's Menu button, the hand
  menu gesture and the desktop panel key, which all keep working. The bar's canvas is sized
  to its content (`TierOneBar.SizeFor`: three 140 × 72 buttons, 8 between, 12 round them =
  460 × 96; it was a fixed 560 × 96).
- **Where the bar sits on the controller.** `WristAnchor` has serialized `controllerPosition`
  (0, 0.05, −0.12) m, `controllerEuler` (45°, −20°, 0°) and `controllerScale` (0.4), next to
  the hand offsets: behind the controller's origin toward the wrist, lifted, leaned back and
  turned a little toward the body's midline, about 18 × 4 cm. It used to stand at the
  origin with no rotation and at full size (56 cm wide), across the path of the left
  controller's own ray. All of it is now behind the origin, so that ray cannot cross it; it
  is pressed with the other hand's ray or a poke. The offsets are applied every frame, so
  they can be tuned while the app runs (the yaw's sign is for a left controller). With
  tracked hands the wrist-joint placement and size are unchanged. `ControllerPose`,
  `ControllerScale`, `Configure(anchor, controller)`.
- On the desktop the bar still rides the top of the view; the key hint reads "Tab or the
  Menu button at the top: panel".
- **A module's buttons leave with the module.** `IUiHost.RemoveTab(id)` (new member; an
  implementation of `IUiHost` has to add it) and `TabbedPanelView.RemoveTab`. The planning
  and demonstration base classes remove their tab when they are unregistered, and the
  shared planner verbs (`set-goal`, `plan`, `preview`, `execute`) carry a condition, so they
  are on the menus only while a planner is registered; before, a plugin unregistered while
  the app ran left its tab and those verbs behind. `PluginHost.Unregister` rebuilds an open
  menu. (A plugin whose prefab is not in the scene never showed anything; that is now
  covered by `Assets/Erupt.App/Tests/Modules`.)
- Tab titles shrink to fit their header when the strip is crowded (five or six tabs share
  the panel's width; "Demonstrations" ran over its neighbours).
- `Erupt.Interaction.Backends.OpenXR` references `UnityEngine.UI` and `Unity.TextMeshPro`
  (the collision haptics build their controls).

### Added (settings the user changes while using the app)

- **The Settings tab** (`SettingsTab`, `TierUiRig.Settings`): the last tab of tier 3
  (`PanelTab.Order` 1000), made of sections that components contribute with
  `AddSection(id, build)` and remove with `RemoveSection(id)`. It exists only while it has
  a section, so a scene shows no dead controls, and no scene wires anything.
- **Collision haptics: switch, strength, distance.** `CollisionHaptics.HapticsEnabled`,
  `Strength` (0 to 1) and `Distance` (0.05 to 0.5 m), `SettingsChanged`, `IntensityAt`,
  `LastIntensity`. The component adds "Collision haptics: on / off", a "Strength" slider
  (0 to 100 %, steps of 5) and a "Distance" slider (5 to 50 cm, steps of 1) to the Settings
  tab of the tier UI it finds. The serialized values (`hapticsOn`, `maxIntensity`,
  `minDistance`) are the defaults; a change applies from the next frame and is kept between
  sessions (`rememberSettings`; `showInSettings` leaves the controls out).
- `UiBuilder.CreateSlider(name, parent, label, min, max, initial, size, onChanged, format, step)`:
  a uGUI slider on the plate of the other controls, showing "label: value", one interactive
  element for the tier budget, worked by the XR ray with the trigger, a poke and the desktop
  mouse (a press puts the handle under the pointer, a drag moves it).
  `UiBuilder.SetInteractable(Slider, bool)`, `UiBuilder.ControlColor`.
- `UserSettings` (`Erupt.Interaction`): the user's settings between sessions, in
  `PlayerPrefs` behind `IUserSettingsStore`; `MemoryUserSettingsStore` for tests.

### Fixed

- `DesktopMouseBackendTests.APressOnAMenu_IsNotAWorldSelect` aimed "beside the menu" at a
  point behind the menu's collider and passed only while no physics step had run since the
  menu was placed; the menu now leaves part of the cube in the clear.

### Added (first run: the editor without a headset)

- **Desktop controls.** `DesktopControls` and `DesktopMouseBackend` are on the ERUPT root
  (`EruptCore.prefab`), so every scene has them and no scene wires anything. With no XR head
  device present they drive the main camera (a view of the robots at the start, chosen once
  the robots stand still, from the side the scene's rig stands on or round from there to
  where the end effectors are not hidden; then `W A S D`, `Q`/`E`, the arrow keys or a
  middle-drag, `F` to view the robots again), put the tier 1 bar at the top of the view,
  open tier 3 square in front of the camera (`Tab`), keep tier 2 menus readable and in
  front of whatever would hide them, and show the keys in a corner of the Game view and
  once in the console (`showHint`). Left click selects, the right button drags, the wheel
  turns the selected joint or, during a drag, moves the dragged point along the ray. They
  stand down whenever an XR head device is present (`DesktopControls.XrHeadPresent`: an XR
  display is running, or the Input System has an `XRHMD`), which is always the case in a
  headset build: the mouse backend is suppressed, the camera's tracked pose driver is back
  on, tier 1 is on the wrist. The assembly `Erupt.Interaction.Backends.Desktop` is now built
  for every platform (the root prefab carries its components) and references `Erupt.Ui`.
  See the README for the keys.
- `ObstacleDragBinding` (ERUPT root): a drag intent moves the selected obstacle, for an input
  source without an XR interactor of its own. Same rule as a grab (only the selection, only
  while its grab interactable is enabled); it lets go the moment an XR interactor holds the
  obstacle, so a headset session is unchanged. The move is one undo step.
- `TierUiRig.ShowUnimplementedVerbs` (serialized, off) and
  `ContextualMenuModel.ListsUnavailableVerbs`: the tier 2 menus hold only the verbs that can
  be pressed. The verbs the guidelines name and nothing implements yet (Reshape, Paint Cost,
  Lock, Preview Grasp, Scrub, Compare, Lock Joint, and Set Joint Angle, which nothing binds)
  are left out unless the switch is on; they stay in `VerbTable`. A selection with nothing
  to offer has no menu.
- `TierUiRig.UseSummonPlacement`: a backend may say where tier 3 opens.
  `ContextualMenuView.ButtonFor(verbId)`, `ReadableDistance`, `ClearsLineOfSight`.
- `PlanningPlugin.ExecuteAndReport(plan, pointedAt, status)` and `RefusalCode.ExecutionFailed`:
  see *Fixed*.
- `RobotInteractionController.PointsAtHandle(ray, hit)`: pointing through the body of the
  last link at an end-effector handle that is sunk into it (a robot without a gripper, a
  handle between flange and gripper) selects and drags the handle, not the link.
- `ColladaPipelineMaterials` (`Erupt.Setup.Editor`): Collada (`.dae`) models get materials
  with the lit shader of the project's render pipeline at import. The Universal Render
  Pipeline converts FBX and OBJ materials and leaves Collada's on the built-in shader, so
  every robot made from Collada meshes (FR3, UR5e, UR10e, Gen3, iiwa 14) was drawn magenta.
- `UiBuilder.ResizeWorldCanvas`.

### Changed

- **The tier 3 tabs have a fixed order.** `PanelTab.Order` (default 100; lower is further
  left, ties stand in the order they were added) decides where a tab stands, and until the
  user picks a tab the panel shows the leftmost one. Before, both followed the order the
  plugins happened to start in, which changed between sessions. The Scene tab is 0;
  `PlanningPlugin` and `DemonstrationPlugin` take theirs from the new `TabOrder`.
- **The tier 2 menu** is as tall as its header and buttons, stands on its lower edge just
  above the selected object (it used to be a fixed 320 × 480 canvas centred there, whose
  lower half and whose press-consuming collider hung over the object), has a dark plate
  behind header and buttons, and is pulled up to 20 cm toward the viewer so the robot's own
  links do not come in front of it. `verticalOffset` is now the gap above the object (5 cm).
- **Dragging the end effector selects it**, as dragging a joint selects the joint; it used
  to clear the selection, so the menu with Set Goal was gone after every drag.
- **"Correct"** on a plan is available (it was marked pending in the table and could never
  be pressed, although the demonstration family binds it). `DemonstrationPlugin` starts a
  demonstration on the plan's robot and opens the demos tab, where the recording shows and
  is stopped.
- **A new shape** from the Scene tab rests on the first surface the gaze meets within the
  placement distance (not a robot, not a menu), or is put that distance ahead when it meets
  none; it used to be put 2 m ahead whatever was there. The free-spot search steps to both
  sides, in smaller steps, and no longer takes the surface a shape rests on for an obstacle.
- The mouse wheel's axis value is per notch (`scrollScale`), not the raw device value.

### Fixed (buttons that did nothing)

- **Execute.** The `execute` verb dropped the planner's status, so "MoveIt is not connected.",
  "The robot's profile names no trajectory command topic.", "No planned trajectory to
  execute.", an aborted pick-and-place solution or a robot that never arrived were silent.
  Every outcome that keeps the robot from arriving is now a refusal on the router, at the
  plan's handle, in the planner's words (`PlannerUnavailable` / `ExecutionFailed`).
- `preview` / `execute` on something that is not a plan, `snap` with no surface below the
  object: a refusal with the reason.
- The tier 2 menu left the header of every earlier menu under the canvas, one on top of
  the other; a rebuild now replaces the whole column.
- A menu refresh while the selected object is already destroyed (a scene reset with
  something selected, found against the real stack) closes the menu instead of throwing.


### Added (moving a robot's base; the optional AR module builds on it)

- `RobotInstance.PlaceBase(pose)` / `PlaceBase(pose, out reason)`: puts the robot's base at a
  world pose by moving its articulation root (`ArticulationRootMover`, ported from the lab's
  demo: PhysX undoes a plain Transform write on an articulation root), and raises
  `BaseMoved(robot, previousBase)`. `BasePose`. `AddBaseGuard` / `RemoveBaseGuard` /
  `CanPlaceBase(out reason)`: whatever needs the base to stay says so, and the move is refused
  with its reason. A robot whose planning frame is not part of the robot is moved with a
  warning (its ROS side still has it at the old pose in that frame).
- `PlaceBaseCommand`: the undoable form.
- `RobotGhosts.ShowPlacementGhost(pose)` / `ClearPlacementGhost()` / `PlacementGhost`: a
  translucent copy of the robot where its base would go.
- `PlanningPlugin`: on `BaseMoved` the robot's preview is stopped and its plans are dropped,
  then `OnRobotBaseMoved(robot, previousBase)`; `WhyBaseMustStay(robot)` registers the
  planner's reason for refusing a move.
- `VerbRegistry.ShowWhen(kind, verbId, condition)`, `For(kind, target)`, `IsShownFor`,
  `Refresh()`: a verb listed only for the selections its condition accepts. The contextual
  menu applies it.
- `RefusalCode.BaseNotPlaced`.
- Boundary tests and CI: nothing references the AR module; only the AR module references
  AprilTag and the MR Utility Kit; what every ERUPT package asks for under `erupt.requires`
  is what the project manifest has.

### Added (robots, installing as packages)

- **Robot Setup** (`ERUPT > Robots > Robot Setup…`, `Erupt.Editor.RobotSetup`, batch entry
  `RunFromCommandLine`): turns a URDF, with an optional SRDF and ROS-names file or with the
  same values typed in, into a wired robot prefab, a ghost prefab, a `RobotProfile` and a
  report of what it found and what is wrong. Joint kinds, limits, velocity limits and mimic
  rules come from the URDF itself; groups, named poses, the end effector and passive joints
  from the SRDF; topics, links, the initial pose and the gripper's open and closed positions
  from the ROS-names file. Running it again updates the assets in place and keeps GUIDs and
  file IDs. See `Documentation~/robots.md`.
- **Planning-frame anchor**: every robot prefab carries a `PlanningFrame` child that stands
  for the origin of its own ROS stack's planning frame, and `RobotInstance` points at it. A
  robot can stand anywhere in a scene with nothing configured on the ROS side; a scene can
  still give several robots one shared frame.
- `RobotProfile.rosRobotName`: what the ROS side calls the robot model (informational).
- **Robot packages** `com.erupt.robot.franka` (FR3), `.universal-robots` (UR5e, UR10e),
  `.kinova` (Gen3), `.ufactory` (xArm7), `.kuka` (LBR iiwa 14), all made by Robot Setup from
  `Tools/robot-descriptions`. The FR3 moved there from `Assets/fr3` (same GUIDs).
- **Installing by git URL**: `Erupt.Setup.Editor`, an editor assembly that references no
  other package, reads what each ERUPT package asks for (`"erupt": { "requires": [...] }` in
  `package.json`) and offers to add what is missing to the project manifest: the
  ROS-TCP-Connector and URDF-Importer forks, which a git package cannot declare as
  dependencies. The assemblies that need the connector carry the define constraints
  `ERUPT_ROS_TCP_CONNECTOR` and `ROS2`, so a project without the connector, or with it but
  without the `ROS2` scripting define, compiles and the offer appears.
- **Project Setup** (`ERUPT > Project Setup`, batch entry
  `Erupt.Editor.Setup.ProjectSetup.RunFromCommandLine`): checks and fixes the packages, the
  tags (`Selectable`, `SnapSurface`, `robot`), the `ROS2` scripting define for Standalone and
  Android, TextMesh Pro's essential resources, the input handling and the OpenXR settings;
  each item ends up fine, fixed or needing the user.
- **Starter Scene** sample: the ERUPT root, an XR rig with the controller and hand backends,
  the three plugins, a floor and a table, and no robot. See `Documentation~/installation.md`.

### Changed

- `MultiArmFR3` gives each robot its own planning frame; its ROS side is started with plain
  `ARMS="left=fr3 right=fr3 third=fr3"`.
- The tests that need the project's robots and scenes (`MultiRobotInteractionTests`,
  `MultiArmSceneTests`, the FR3 prefab tests) moved out of the package to
  `Assets/Erupt.App/Tests/Robots`, where every shipped robot prefab is now tested
  (`RobotPrefabTests`, `ShippedRobotAssetTests`).
- `Tools/live-tests.sh` runs under the editor's own Standalone build target by default
  (`ERUPT_BUILD_TARGET` overrides) and takes any shipped robot (`ERUPT_LIVE_ROBOT`,
  `ERUPT_LIVE_ARMS`).

### Fixed

- `DirectArticulationIKController` no longer throws when a robot is destroyed while a
  trajectory preview is playing (a scene unloading mid-preview).

### Fixed (found against a real ROS 2 stack)

- `LiveRosBus` keeps one connection across scene loads; publishing or unsubscribing no longer
  brings a new `ROSConnection` to life during a scene unload or while quitting, publishers are
  registered once per connection, and the connector's TF listener is dropped with the scene
  its frame objects lived in.
- `RosUnityConversion`: the quaternion conversion is the closed form (−x, −z, −y, w) instead of
  a round trip through Euler angles (same rotation, exact at a 90° pitch).
- `SelectionService` clears a selection or hover whose object was destroyed (the tier 2 menu
  threw every frame after a selected plan handle was destroyed).
- `IRobotModel.ControlTaken` / `TakeControl()`: raised by `BeginInteraction` and by
  `JointTrajectoryPlayer`, so a joint-state mirror lets go when the user or a playback takes
  the robot's pose. `IRobotGripper.GripperCommanded`: the gripper verb can be carried to the
  real gripper by a plugin.
- `RefusalCode.PlanFailed`; `PlanningPlugin.ReleaseHandles` (plan handles no longer die with
  the goal ghost they follow).

### Added

- `Assets/Erupt.App/Tests/Live` and `Tools/live-tests.sh`: opt-in tests of the reference scenes
  against a real ROS 2 stack (single arm, three arms, ROS away).

### Added (any number of robots)

- `RobotSessions<T>` (Erupt.Plugins): one session per robot for a plugin that stays a single
  instance. Sessions are created for the robots present at registration and for robots that
  join later, and disposed when a robot leaves; `For(robot)`, `For(selectable)` (the owner of
  the selected object, else the active robot), `Active`, `Robots`, enumeration.
- `ActiveRobotTab`: a tier 3 tab that shows the active robot. It is rebuilt on
  `IRobotRegistry.ActiveChanged` and its title names the robot while the scene has several
  ("MoveIt · left"). `PanelTab.SetLabel` / `PanelTab.Rebuild`. The Planning and Demonstration
  family tabs use it.
- `SessionComponents`: per-robot copies (same serialized settings) of the helper components a
  plugin prefab carries once.
- `PlanResult.RobotId`, `TrajectorySelectable.RobotId`, `PlanningPlugin.RobotOf(plan)`,
  `LastResultFor(robot)`, `ClearResults(robot)`, `Serves(robot)`, `PlannerFor(host, robot)`.
- `RobotIntentDispatcher`: the one place robot intents are dispatched, for every robot in the
  scene. An intent goes to the controller of the robot its target belongs to; drags are per
  input source (two hands can drag two robots); the axis jogs the selected joint's owner; the
  owner of the most recent robot-owned selection becomes the active robot (the only caller of
  `IRobotRegistry.SetActive`).
- The active robot is shown while the scene has several robots: the tier 2 header and the
  plan labels name the robot ("End Effector · left"), and its links carry a light tint
  (`RobotInteractionController.SetActiveHighlight`, `SetLabelSuffix`).
- `RendererTint`: layered colour overrides as property blocks (base, active robot, selected,
  joint limit), used by the handles, the selected-joint highlight, the active-robot tint and
  `JointLimitIndicator`.
- `IRobotGripper` and `RobotInstance.Gripper` (null for a robot without a gripper).
- `IProximitySource` and `InteractionRouter.ProximitySources`: the robots offer their tips and
  bodies to proximity feedback; `CollisionHaptics` reads them, so it needs no per-scene setup
  and considers every robot.
- `ContextualMenuView.Invoke` / `SetRouter`: a refusal from a tier 2 verb is reported on the
  interaction router like any other.
- `FakeRobots.Three` (robots `left`, `right`, `third`), `FakeRobots.EndEffectorOf`,
  `FakeRobotModel` implements `IRobotGripper`; `FakeUiHost.TabsById`.
- Sample scene `Assets/Scenes/MultiArmFR3.unity` in the reference app: three FR3 robots in
  the namespaces `left`, `right`, `third`, one shared planning frame.

### Changed (any number of robots)

- `PlanningPlugin` and `DemonstrationPlugin` name the robot in their template methods:
  `SetGoal(robot, endEffector)`, `RequestPlan(robot, preferences, done)`, `StopPreview(robot)`,
  `StartDemonstration(robot)`, `StopDemonstration(robot)`, `Publish(robot)`. The overloads
  without a robot address the active robot. The shared verbs act on the robot that owns the
  selected object; `set-goal` / `plan` go to the first planner that accepts goals and serves
  that robot, and a robot no planner serves gets a refusal.
- The gripper verb is bound once and toggles the gripper of the robot that owns the selected
  object; a robot without a gripper answers "This robot has no gripper." (it used to act on
  the robot that was active when the host started, and stayed unbound without a gripper).
- `Quest3RobotInteractionController` is now `RobotInteractionController` and `SpawnGhosts` is
  `RobotGhosts` (script GUIDs unchanged). A controller treats a selection as its own only when
  the selected object belongs to its robot.
- `RobotInteractionRouterBinding` hands its controller to the router's
  `RobotIntentDispatcher` instead of subscribing to the router itself, and no longer falls
  back on another robot's controller.
- The plugin generator's Planning and Demonstration templates start from the per-robot
  session pattern, and their smoke tests register three fake robots.
- Only `PluginHost` may read `RosBus.Instance` (the boundary test and CI allowlist is that
  one file).
- A robot's registry is set before `IRobotRegistry.Added` is raised, so a handler of that
  event already finds `RobotInstance.Ros`.

### Removed

- `IEruptContext.Robot` (obsolete since the robot registry): use `Robots`.

### Fixed

- The selected-joint highlight no longer instantiates materials (it wrote
  `renderer.material`, which leaked an instance per selection) and no longer tints every
  link below the selected one.
- The joint handles no longer create a material each.
- With several robots, one robot's controller no longer clears or keeps another robot's
  selection, and a joint selected earlier on another robot is no longer jogged.

### Added

- Joint limits are enforced on every joint write. `JointLimitModel` (`IJointLimits`,
  `IRobotModel.Limits`) is the one description of a robot's limits: per joint whether it has
  position limits (a continuous joint, or one without a range, has none and is neither clamped
  nor wrapped), the limits after the profile's overrides and safety margin
  (`RobotProfile.limitMarginRadians` / `limitMarginMetres`), the velocity limit and the kind.
  It is built from the `RobotProfile` (`BuildLimits()`), or for a robot without one from the
  drive limits the robot was imported with. `Clamp`, `StepToward` (a step under a velocity
  budget) and `Validate` (a joint state or a trajectory: which joints leave their limits, by
  how much, at which point; `JointLimitViolation`, `JointLimitTolerance`).
- `JointWriter.ApplyJointPosition`: the single place a joint position is written, always
  clamped. IK, jogging, joint states, the initial pose, the held pose, the gripper and its
  mimic followers, trajectory playback and the ghost robots all go through it. A value that is
  not a finite number is not written. A boundary test and a CI step allow `jointPosition` to
  be assigned in that file only.
- `JointWriteReport`: what `IRobotModel.ApplyJointState` returns — the joints that were clamped
  (requested and applied value) and the names the robot does not have.
- `IRobotModel.TryGetJointLink`: the link a joint moves, for feedback that points at a joint.
- `RobotProfile.limitUserVelocity` (off by default): motion the user drives directly (dragging
  a handle, jogging) moves each joint at most `maxVelocity x deltaTime` per frame. Trajectory
  playback and mirrored joint states are not slowed. A drag that falls short only because of
  the velocity budget is not reported as out of reach.
- `InteractionRefusal.Code` (`RefusalCode`: `JointLimit`, `OutOfReach`, `LimitMismatch`,
  `PlanOutsideLimits`, `NotControllable`, `NothingSelected`, `VerbUnavailable`,
  `PlannerUnavailable`, `Unspecified`), `Subject` (the link a joint limit is about) and
  `SourceId` (the input source that caused it; `From(sourceId)`).
- Refusals reach the user. `RefusalToast` (created by `TierUiRig`, `TierUiRig.Refusals`) shows
  the reason as world-space text at the refusal's point; `JointLimitIndicator` (added to each
  robot by its `RobotInteractionRouterBinding`) tints the link whose joint is at its limit;
  `RefusalHaptics` (added by each `XriControllerBackend`) answers with a double pulse on the
  controller that caused the refusal. All three listen to `InteractionRouter.Refused` only.
- `IHapticOutput`, implemented by `XriControllerBackend` (`SendHapticImpulse`, `SourceId`; the
  haptic action is found next to the select action's map unless `hapticAction` is assigned).
- `LimitMismatchMonitor`: turns clamped writes of mirrored joint states into one
  `LimitMismatch` refusal per joint, repeated at most every `RepeatSeconds`.
- `PlanResult.LimitViolations` / `HasLimitViolations`, `ExecutionPhase.Refused`, and on
  `PlanningPlugin`: `CheckLimits`, `FindLimitViolations`, `ReportLimitViolations`,
  `MayExecute`, the serialized `allowExecutionOutsideLimits` (off) and `limitTolerance`
  (1e-3 rad, 1e-4 m). `Report(refusal)` is now protected.
- `UiBuilder.CreateWorldReadout`: a world canvas without a raycaster or a collider.
- `MaterialColor`: the base-colour swap the robot's highlights share.
- `FakeRobotModel.WithLimits` / `Limits` / `JointLinks`: a fake robot that clamps and reports
  like the real controller.
- `RobotProfile` (ScriptableObject): the description of a robot model — links, planning frame,
  SRDF groups, joints (`JointSpec`: role, kind, limits, velocity limit, mimic), named poses and
  the initial one, `GripperSpec`, the robot's relative ROS names, the ROS ↔ Unity name rule
  (`RobotNameMap`) and the ghost prefab. `Validate()` lists its problems.
  `ERUPT > Robots > Create Profile From Selected Robot` writes one from an imported robot.
- `RobotInstance` (on the robot's root): id, ROS namespace, profile, model, the per-robot
  components, the planning frame and `Ros`, the bus scoped to the robot's namespace.
- `IRobotRegistry` / `RobotRegistry` and `IEruptContext.Robots`: the robots in the scene, the
  active one, `OwnerOf(gameObject)` (with the `RobotOwned` marker for handles and ghosts).
- `IRosBus.Scoped(ns)`: like `InNamespace`, but an empty namespace still resolves relative
  names to `/name`; `IRosBus.ResolveName(name)`.
- Test support: `FakeRobots` (registries of fake robots with ids and namespaces) and
  `FakeRosBus.Root`.
- `RosName` (`Resolve`, `Normalize`, `Combine`, `IsAbsolute`): ROS name resolution — a leading
  `/` is absolute, any other name lands under the namespace. `NamespacedRosBus` and
  `IRosBus.InNamespace(rosNamespace)` apply it to every topic, service and action name on a bus.
- Boundary tests: every type in an `Erupt.*` assembly is in an `Erupt.*` namespace, and
  `RosBus.Instance` is read only by `PluginHost` and a short allowlist (also a CI step).
- `GrabbableObjectSetup.Configure`: the grab stack (kinematic rigidbody, collider, XRI grab
  interactable gated by `SelectableGrabController`, grab transformers, "Selectable" tag) in one
  place. `ObstacleFactory` builds its obstacles with it and planner plugins use it for the
  objects they mirror.
- `SelectableGrabController.SetLocked`: while locked the object cannot be grabbed whatever the
  selection says (the robot is carrying it).
- `DirectArticulationIKController.ignorePhysicsContacts` (default on): the robot's colliders
  are excluded from physics contacts, which could only shove the kinematically driven arm; they
  stay raycastable. `ClearLinkCache` forgets resolved link transforms.
- `FakeRobotModel.WithGripper`: gripper joints that are part of the joint state but not of
  `JointNames`, as on the real controller.
- `IRobotModel.TryFindLinkTransform`: the exact link by URDF name, with no end-effector
  fallback, for callers that place objects in a link's own frame (the MTC preview mirrors
  `start_scene` attachments). `DirectArticulationIKController.FindLinkTransform` delegates to
  it and no longer caches the fallback under the missing name; `FakeRobotModel` gains `Links`.
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

- `IRobotModel.ApplyJointState` (both overloads) returns a `JointWriteReport` instead of
  `void`, and `IRobotModel` gains `Limits` and `TryGetJointLink`. Implementations outside this
  package need the three members.
- `InteractionRouter.ReportRefusal` drops a repeat of a refusal with the same code and subject
  raised within `RefusalCoalesceSeconds` (0.5 s) and returns whether it raised; a caller may
  report on every frame of a drag. Refusals without a code are told apart by their text.
- `DirectArticulationIKController`: IK reports `JointLimit` (naming the joint, with its link as
  the subject) when it falls short with a joint at a limit and `OutOfReach` otherwise; jogging
  into a limit reports `JointLimit`. With a profile, the profile's limits apply to every joint
  (the gripper included) instead of the limits on the articulation drive.
  `LogJointDriveLimits` is replaced by `LogJointLimits`, which prints the limit model.
- `SpawnGhosts` poses a ghost through `JointWriter` with the robot's limits instead of copying
  raw joint positions.
- `RobotInteractionRouterBinding` attributes the refusals it reports to the input source that
  caused them.
- `DirectArticulationIKController` takes joint roles, the initial pose and the gripper from
  the robot's profile: a revolute gripper is no longer part of the IK chain, mimic joints
  follow their leader, and there is no built-in pose. Without a profile it guesses as before.
  `Configure(robotRoot, tool)` now stores the root it is given.
- `PluginHost` no longer holds a `DirectArticulationIKController`; it puts the robot registry
  in the context and gives it the bus. `IEruptContext.Robot` is obsolete (`Robots.Active.Model`).
- `SpawnGhosts` uses the profile's ghost prefab unless one is assigned, and marks its ghosts
  with their robot.
- Every script is in its assembly's root namespace. `DirectArticulationIKController`,
  `Quest3RobotInteractionController`, `RobotInteractionRouterBinding`, `JointTrajectoryPlayer`,
  `SpawnGhosts` and `TranslucentOverride` are in `Erupt.Robot`; `SelectionHighlighter`,
  `SelectableGrabController` and the grab transformers (`XRGrabTransformerLockPose`,
  `XRGrabTransformerScaleAxisLock`, `XRTwoHandedScaleTransformer`, `XRUIScaleTransformer`) in
  `Erupt.Environment`; `Billboard` in `Erupt.Ui`; `RosUnityConversion` in `Erupt.Ros`. Script
  GUIDs are unchanged, so scenes and prefabs keep their references.
- `DirectArticulationIKController.ApplyJointState`: a joint state that names only some of the
  finger joints (MoveIt leaves out the mimic finger) moves the fingers it does not name along
  with the first one it does, so the hand stays symmetric.
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

- Joints could be moved past their limits: only IK dragging and jogging were clamped, while
  joint states (plan and solution previews, mirrored `joint_states`, demonstration replay, the
  pose restored after a preview), the initial pose, the held pose and the ghost robots wrote
  joint positions as given. PhysX does not apply joint limits to a teleported joint, and the
  joint locks are freed on purpose, so nothing else stopped them.
- The arm clamp had no guard for a joint without a limit range: a continuous joint (drive
  limits 0/0) was frozen at zero by jogging and IK.
- `GetJointStatePositions` / `TryGetJointAngle` went through a check that negated the drive
  limits (wrong for this importer: the joint position is the ROS angle, same sign); the check
  and `LogJointDriveLimits`' negated ROS column are gone.
- `Quest3RobotInteractionController.JogSelectedJoint` left an earlier drag's refusal standing
  when it had nothing to jog, and the binding reported it again.
- The FR3's prismatic finger joints were not registered with the controller, so nothing held
  them and finger positions in joint states and trajectories were dropped with a warning. They
  are now held at their commanded travel every physics step and follow `ApplyJointState`.

### Removed

- `FPSPublisher` (frame-rate publishing on `/fps`): measurement tooling is not part of core.
- `Erupt.Interaction.Backends.VisionOS` (the gaze-pinch stub backend). ERUPT targets Meta Quest
  controllers and hands, with the desktop backend for the editor.

## [1.0.0-preview.1] - 2026-09-14

First release of ERUPT (Extended Reality Universal Programming Toolkit) as a core package with
a plugin contract. Extracted from the single `xrviz` assembly of the former XRViz project.

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
- Haptics as a capability: `CollisionHaptics` in the OpenXR backend.
- Editor: `ERUPT > Plugins > Create Plugin...` generator with Blank / Planning /
  Demonstration templates, prefab creation after compilation, "Add to open scene".
- `EruptCore.prefab` (the `ERUPT` root) and the `Documentation~` set: `architecture.md`,
  `plugin-authoring.md`, `design-guidelines.md`.
- Tests: per-assembly PlayMode suites, `Erupt.TestSupport` fakes, EditMode
  `AssemblyBoundaryTests` (core never references plugins or the app) and `PluginGeneratorTests`.

### Removed

- The legacy wrist menu, UI Toolkit panels and the dual selection system; AR / ArUco scenes;
  the transform-based kinematics layer.
