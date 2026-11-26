# ERUPT architecture

## Packages

```
Packages/com.erupt.core                  ERUPT Core — universal features + the plugin contract
Packages/com.erupt.plugin.moveit         MoveIt 2 planning + planning-scene sync (Planning family)
Packages/com.erupt.plugin.mtc            MoveIt Task Constructor solutions + pick/place task recording (Planning family; depends on moveit)
Packages/com.erupt.plugin.demonstration  demonstration recording/replay/publish, hand + gripper mirroring (Demonstration family)
Packages/com.erupt.plugin.ar             optional AR module (Meta Quest): AprilTag tracking, robots placed at markers, room anchors
Packages/com.erupt.robot.<maker>         robots, one package per maker (franka, universal-robots, kinova, ufactory, kuka): assets only
Assets/Erupt.App                         the reference app (KitchenFR3): scene-specific scripts only
ROS-TCP-Connector, URDF-Importer         parasollab forks, git packages pinned in Packages/manifest.json
```

Every package can be added to another project by git URL
(`https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.core`, and so on); see
`installation.md`. The robots are described in `robots.md`.

## Assemblies (arrows = references; core never points right)

```
Erupt.Interaction.Core  ←  Erupt.Interaction.Backends.{OpenXR, Desktop}
Erupt.Interaction.Core  ←  Erupt.Ui  ←  Erupt.Interaction.Backends.OpenXR (raycaster installer), .Desktop (tier placement in the view)
Erupt.Ros               →  ROSTCPConnector, .Messages, .MessageGeneration
Erupt.Robot             →  Erupt.Interaction.Core, Erupt.Ros
Erupt.Environment       →  Erupt.Interaction.Core, Erupt.Ui, XRI
Erupt.Plugins           →  all of the above core assemblies
Erupt.Plugins.MoveIt    →  Erupt.Plugins, Erupt.Plugins.MoveIt.Messages
Erupt.Plugins.Mtc       →  Erupt.Plugins.MoveIt, Erupt.Plugins.Mtc.Messages
Erupt.Plugins.Demonstration  →  Erupt.Plugins, Erupt.Plugins.Demonstration.Messages, XR Hands
Erupt.Plugins.Ar        →  Erupt.Plugins and core; AprilTag, Meta MR Utility Kit (the only assembly that may); nothing references it
Erupt.App               →  everything (the reference app may see plugins)
Erupt.Core.Editor       →  core (plugin generator, templates, Robot Setup); the URDF Importer when it is installed
Erupt.Setup.Editor      →  nothing (the offer to add missing packages, Project Setup, Collada materials for the render pipeline)
Tests: Erupt.TestSupport (FakeRosBus, FakeRobotModel, FakeRobots, TestInteractionSource, IdleRosConnection) ← every *.Tests; Erupt.Boundary.Tests enforces the graph
```

The assemblies that need the ROS-TCP-Connector (`Erupt.Ros`, `Erupt.Robot`, `Erupt.Plugins`,
`Erupt.Core.Editor` and the plugins) carry two define constraints: `ERUPT_ROS_TCP_CONNECTOR`,
set by a version define when the connector is in the project, and `ROS2`, the scripting
define that makes the connector speak ROS 2 (its message types differ without it, and
ERUPT's code is written for ROS 2). Without either they are left out instead of failing to
compile, so `Erupt.Setup.Editor`, which references no package, loads and can offer to add
the packages and set the define (`ERUPT > Project Setup`).

Every type lives in an `Erupt.*` namespace, by default its assembly's root namespace
(`Erupt.Interaction`, `Erupt.Interaction.Backends`, `Erupt.Ui`, `Erupt.Ros`, `Erupt.Robot`,
`Erupt.Environment`, `Erupt.Plugins`, `Erupt.Plugins.MoveIt`, `Erupt.Plugins.Mtc`,
`Erupt.Plugins.Demonstration`, `Erupt.App`). Generated ROS message bindings keep their
`RosMessageTypes.*` namespaces. `Erupt.Boundary.Tests` enforces this too.

The interaction backends target Meta Quest: `OpenXR` holds the controller and hand-tracking
backends, `Desktop` is the mouse and keyboard backend for working in the editor without a
headset (see *Without a headset*). Feature code names neither a device nor the Meta SDK; it
asks the input source for capabilities.

## The ERUPT root (EruptCore.prefab)

`PluginHost` (discovery, dependency order, context with the robot registry, undo stack, the
gripper verb) · `SelectionService` (the one selection) · `SelectionHighlighter` ·
`SelectionRouterBinding` (router select → selection) · `InteractionRouter` (with the
`RobotIntentDispatcher` the robots add to it) ·
`EnvironmentRegistry` (the mirror; `worldOrigin`) · `TierUiRig` (tiers 1–3, the `IUiHost`; it
also creates the `RefusalToast`) · `ObstacleVerbBindings` · `ObstacleDragBinding` ·
`ScenePlacementTab` · `XrUiRaycastInstaller` (OpenXR) · `DesktopMouseBackend` and
`DesktopControls` (Desktop).

There are no application modes (no `ModeManager`, no `AppMode`): every action is a button
that is there whenever it can work. See the design guidelines, *No modes*.

### The tiers

- **Tier 1** (`TierOneBar`) holds **Menu**, **Undo**, **Redo** (and a slot kept for voice).
  Menu raises `MenuControl.Pressed`, which the rig answers with `ToggleTierThree`: the same
  toggle the router's `Activate` intent drives (the controller's Menu button, the hand menu
  gesture, the desktop panel key). The bar's canvas is as wide as its buttons
  (`TierOneBar.SizeFor`).
- **Tier 2** (`ContextualMenuView` over `ContextualMenuModel` and the `VerbRegistry`): see
  *Selection* under *Flows*.
- **Tier 3** (`TabbedPanelView`) is one panel with tabs, in a fixed order (`PanelTab.Order`):
  Scene 0, MoveIt 10, MTC 20, Demonstrations 30, AR 40, Settings 1000. A tab's title is drawn
  smaller when the strip gets crowded. `IUiHost.RemoveTab` takes a tab away again, and a
  plugin does so when it is unregistered.
- **The Settings tab** (`SettingsTab`, `TierUiRig.Settings`) is made of sections that
  components contribute (`AddSection(id, build)`), and exists only while it has one. The
  collision haptics add theirs (below), so a scene without that component has no such
  controls and no scene wires anything. A value the user sets is kept between sessions
  through `UserSettings` (Erupt.Interaction: `PlayerPrefs` behind `IUserSettingsStore`; tests
  put a `MemoryUserSettingsStore` there). It holds numbers, switches and text.
- **The ROS connection section** (`RosConnectionSection`, Erupt.Plugins) is core's own
  section of that tab. `PluginHost` adds it when the bus has an endpoint it can change
  (`bus.Endpoint()`) and the scene has a tier UI. It shows where the app connects and
  whether it is connected, and **Change address** opens a keypad (digits, a dot, **Del**)
  for an IPv4 address. While the keypad is open it has the tab to itself
  (`SettingsTab.ShowOnly`); the button that opened it, **Connect**, leaving the tab and
  closing the panel all put it away. **Connect** checks the entry (`RosAddress.TryParseIPv4`;
  an entry that is no address is a refusal on the router), saves it and calls
  `IRosEndpoint.ConnectTo`.
- **Controls** are built in code by `UiBuilder`: buttons, cycles, toggles, and
  `CreateSlider`, a uGUI `Slider` on one plate that shows "label: value", moves in steps
  when asked and counts as one interactive element. Being uGUI, it is worked by whatever the
  scene's XR UI input module serves: an XR ray with the trigger held, a poke, the mouse.

### Without a headset

The quick start is: open a scene, press Play in the editor, plan a motion with the mouse.
`DesktopControls` on the root makes that work in any scene, with nothing wired per scene:

- **Who drives the view.** Every frame it asks whether an XR head device is present: an XR
  display is running (`XRSettings.isDeviceActive`), or the Input System has a head-mounted
  display (`XRHMD`: a headset, or an XR simulator's). When there is one, the controls stand
  down: `DesktopMouseBackend.Suppressed` (it raises no intent and reports itself inactive, so
  there is no ray next to the controllers'), the camera's `TrackedPoseDriver` enabled, tier 1
  on the wrist anchor, tier 3 opening in front of the head. That is always so in a headset
  build. When there is none, they take over: the tracked pose driver is switched off
  (without a head device it pins the camera to the rig's origin every frame) and the main
  camera is theirs.
- **The view.** Once the robots' end effectors (the router's `IProximitySource`s) have stood
  still for a moment, the camera goes to the side of them the scene's rig stands on, turns
  round them in 45° steps when their own arms or something else hides them from there, and
  does not start within half a metre of anything (a wall, a panel that hangs in the room).
  The end effectors sit below the middle of the view, which leaves room for the menu above
  one. Keys and the middle button move and turn it from there.
- **The tiers.** Tier 1 (Menu, Undo, Redo) rides the camera at the top of the view (it is
  re-parented from the wrist anchor and put back on standing down); its Menu button opens
  and closes the panel like the panel key. Tier 3 opens square in front of the camera
  through `TierUiRig.UseSummonPlacement`, whatever the camera's pitch. Tier 2 stays attached
  to the selection but is drawn so that it reads from any distance
  (`ContextualMenuView.ReadableDistance`) and is brought in front of whatever stands between
  it and the camera (`ClearsLineOfSight`); neither applies in a headset, where a menu is a
  thing in the room.
- **Input.** `DesktopMouseBackend` is an interaction source like the controller backends:
  left press → `Select`, right button → `BeginDrag` / `Drag` / `EndDrag`, wheel → `Axis` (or
  the depth of a drag), one key → `Activate`. A press on a UI graphic is the UI's: the scene's
  event system clicks the button (the XR UI input module handles the mouse) and the backend
  raises no `Select`. Obstacles are XR grab interactables, which a mouse has no interactor
  for; `ObstacleDragBinding` applies the router's drag to the selected obstacle and yields
  to XRI the moment an interactor holds it.

On the XR rig (`KitchenFR3`): an `XriControllerBackend` under each controller, an
`OpenXrHandBackend` per hand under the Camera Offset, and `WristAnchor`, which keeps the tier 1
anchor on the left controller or the left wrist. XRI's `XRInputModalityManager` swaps the
controller and hand objects as the user picks controllers up or puts them down. Each
controller backend adds its own `RefusalHaptics`.

**Where tier 1 sits on the controller.** The controller's transform is its pointing pose (the
ray leaves its origin along +Z, +Y is up with the thumb on top). `WristAnchor` puts the anchor
at `controllerPosition` (0, 0.05, −0.12) m, turned by `controllerEuler` (45°, −20°, 0°) and
scaled by `controllerScale` (0.4): behind the origin, toward the wrist, lifted clear of the
thumb, leaned back and turned a little toward the body's midline, about 18 cm wide. That is
where a watch face would be, it faces the eyes of a user who holds the controller in front
of the body, and because all of it is behind the origin the controller's own ray cannot
cross it (it used to stand at the origin, across that ray). It is pressed with the other
hand's ray or a poke. The three values are serialized because they want tuning on a headset
(the yaw's sign is for a left controller), and they are applied every frame, so they can be
changed while the app runs. With tracked hands the anchor follows the wrist joint
(`handPosition`, `handEuler`) at its own size, as before.

## Robots

A scene holds any number of robots (one, two, three, …), of the same or different models.
Nothing in core or in the plugins assumes a count, a model, seven joints or a gripper kind.
A robot is data plus one object in the scene:

- **`RobotProfile`** (a ScriptableObject, e.g.
  `Packages/com.erupt.robot.franka/Robots/FR3.asset`) describes a robot model: base and
  end-effector links, planning frame, SRDF groups, every joint with its role (`Arm` /
  `Gripper` / `Passive`), kind, limits (URDF or overridden), velocity limit and mimic, the
  limit margin and whether user-driven motion is velocity limited, named poses and the
  initial one, the gripper (`GripperSpec`: driver joint, open / closed positions, open
  width), the robot's own relative ROS names (`jointStatesTopic`, `trajectoryCommandTopic`),
  what the ROS side calls the model (`rosRobotName`, informational), the ROS ↔ Unity name
  rule (`RobotNameMap`: prefix swap plus explicit pairs) and the ghost prefab.
  `Validate()` lists what is wrong with it.
- **`RobotInstance`** sits on the robot's root object. A robot prefab
  (`Packages/com.erupt.robot.franka/Robots/FR3.prefab`) is the whole robot: the articulated
  import, `RobotInstance`, the IK controller, the `RobotInteractionController` with the
  end-effector handle, the router binding, the trajectory player, `RobotGhosts` and the
  planning-frame anchor. `RobotInstance` carries the robot's `Id`, its ROS `Namespace`, the
  `Profile`, the `Model` (`IRobotModel`), the `Gripper` (`IRobotGripper`, null without one),
  the per-robot components plugins use (`TrajectoryPlayer`, `Ghosts`, `Interaction`), `Ros`
  (the context bus seen from the robot's namespace) and the planning frame: `PlanningFrame`
  is the Unity transform that stands for the frame this robot's ROS side plans in, and
  `PlanningFrameId` its frame id. Every pose sent to a robot is expressed in that robot's
  planning frame.
- **The planning-frame anchor.** Every robot prefab has a child `PlanningFrame`: the origin
  of the `world` frame of that robot's own ROS stack, with ERUPT's axes (ROS x, y, z = the
  transform's x, z, y; a link lies along the importer's axes, ROS x = Unity z, hence a
  quarter turn between the two). `RobotInstance.PlanningFrame` points at it by default, so
  each robot's ROS stack believes its robot stands at the origin of its own world: a robot
  can stand anywhere in a Unity scene, or be moved, and nothing is configured on the ROS
  side. A scene may instead assign one shared transform to several robots when their ROS
  stacks really share one world (each stack then has to be started with its robot's pose in
  it). A robot that names no frame falls back to the scene's world origin.
- **Moving the base.** `RobotInstance.PlaceBase(pose)` puts the robot's base (its root
  object) at a world pose: it moves the articulation root (`ArticulationRootMover`; PhysX
  undoes a plain Transform write on an articulation root at the next physics step) and
  raises `BaseMoved` with the pose the base had. The robot's own planning frame moves with
  it, so nothing on the ROS side is reconfigured, but every pose that was expressed in that
  frame is stale, and whoever sent one reacts to `BaseMoved`: the planning family drops the
  robot's plans and stops its preview, the MoveIt plugin drops its start and goal and sends
  its planning scene every object again (`PlanningSceneFanout.FrameMoved`), the MTC plugin
  forgets its task. Whatever needs the base to stay registers a guard (`AddBaseGuard`):
  `PlaceBase` is refused, with the guard's reason, while the robot executes a trajectory or
  a pick-and-place solution, while a pick-and-place task is being planned for it, and while
  it carries an object (`CanPlaceBase(out reason)`). `PlaceBaseCommand` is the undoable
  form. `RobotGhosts.ShowPlacementGhost(pose)` shows a translucent copy of the robot where
  its base would go. The AR module places robots at printed markers this way.
- **Robot Setup** (`ERUPT > Robots > Robot Setup…`, `Erupt.Editor.RobotSetup`) makes a robot
  from a description: it imports the URDF and writes the wired prefab, the ghost prefab, the
  profile and a report. The shipped robots are its output. See `robots.md`.
- **`RobotRegistry`** (`IRobotRegistry`, `IEruptContext.Robots`) holds the robots. A robot
  registers itself when enabled and leaves when disabled (`Added` / `Removed`).
  `OwnerOf(gameObject)` says which robot an object belongs to: the `RobotInstance` above it,
  or the one named by a `RobotOwned` marker (joint handles, ghosts, trajectory handles).
  `Active` is the robot the user is working with (see *Interaction with several robots*).

`DirectArticulationIKController` takes joint roles, the initial pose and the gripper from the
profile; a robot without a profile falls back to guessing (revolute = arm, prismatic = finger).
A gripper is one driver joint and any number of joints that follow it by their mimic rule
(two prismatic fingers, or a linkage of revolute joints): the controller writes the followers
itself, and a joint state that names only the driver (what ROS reports) moves them too.

### One plugin, per-robot sessions

Each robot is an independent stack on the ROS side, in its own namespace: its own
`joint_states`, controllers, `move_group`, planning scene and pick-and-place server, all
reached through one TCP endpoint. A plugin is one instance however many robots there are
(one id, one `DependsOn`, one tier 3 tab); what it holds per robot is a **session**, kept by
`RobotSessions<T>` (Erupt.Plugins): a session is created for every robot present at
registration and for every robot that joins later, and disposed when its robot leaves.

| Plugin | Per-robot session |
|---|---|
| MoveIt (`MoveItRobotSession`) | `MoveItPlanningClient` on `robot.Ros`, captured start / goal, preview on the robot's `JointTrajectoryPlayer`, its `RobotGhosts`, planner discovery, the limit policy, its planning-scene endpoint and attach mirror |
| MTC (`MtcPlugin.RobotSession`) | `PickPlaceClient`, `MtcSolutionPlayer`, `PickPlaceTaskRecorder`, the solutions and the preview scope |
| Demonstration (`DemonstrationCollectionPlugin.RobotSession`) | `DemonstrationRecorder` (with the robot's name map), a `JointTrajectoryPlayer` for replay |

Verbs act on the robot the selected object belongs to (`Robots.OwnerOf(target)`, the active
robot when it belongs to none); a `PlanResult` records its `RobotId`. The members of a plugin
that take no robot address the active robot's session, which is also what the tab shows: the
tab is rebuilt on `ActiveChanged` and, while the scene has several robots, its title names
the robot ("MoveIt · left"). A robot a plugin cannot serve (no bus, no pick-and-place server)
gets no session or an unavailable one; it never blocks the others. The gripper verb is bound
once in `PluginHost` and toggles the gripper of the target's owner; a robot without a gripper
answers with a refusal.

### Interaction with several robots

Each robot has its own `RobotInteractionController` (handle, joint handles, link grabbing,
selection highlight). One `RobotIntentDispatcher`, on the interaction router's object, serves
all of them:

- An intent whose target belongs to robot X goes to X's controller and to no other. A
  controller treats a selection as its own only when the selected object is its robot's.
- Drags are tracked per input source, so two hands can drag two robots at once. A ray that
  hit nothing robot-owned but passes within 8 cm of an end-effector handle drags the nearest
  such handle.
- The axis jogs the selected joint of the robot that owns the current selection.
- **Selection defines the active robot**: the owner of the most recent selection that
  belongs to a robot (a link, an end effector, a plan's handle). Selecting anything else
  leaves it. The dispatcher is the only caller of `IRobotRegistry.SetActive`.
- The active robot is shown without a tier 1 control, and only while there are several
  robots: the tier 2 header names the robot ("End Effector · left"), the tier 3 tab titles
  name it, and its links carry a light tint.
- Highlights are layers of `RendererTint` (base colour, active robot, selected, joint limit):
  property blocks, never material instances, each layer set and cleared on its own.
- The dispatcher offers every robot's tip and body to the router as an `IProximitySource`;
  `CollisionHaptics` reads them there.

### Joint limits

No path moves a joint past its limits, and there is one reason for that:

- **One limit model.** `JointLimitModel` (`IJointLimits`, exposed as `IRobotModel.Limits`) is
  built from the robot's profile: per joint whether it has position limits, the limits after
  overrides and the profile's margin, the velocity limit and the kind. A joint has position
  limits only when it is not continuous and `upper > lower`; a continuous joint passes through
  unclamped and is not wrapped, and a range beyond ±π (±2π on a UR) is kept as it is. A robot
  without a profile uses the drive limits it was imported with, read before the controller
  frees the joint locks (a free lock is a continuous joint).
- **One write point.** `JointWriter.ApplyJointPosition` is the only code that assigns
  `ArticulationBody.jointPosition`, and it clamps to the joint's limit. The controller's own
  `ApplyJointPosition` calls it for everything: IK, jogging, `ApplyJointState` (plan and
  solution previews, mirrored joint states, demonstration replay, the pose restored after a
  preview), the initial pose, the held pose re-applied every physics step, the gripper and its
  mimic followers (each clamped to its own limits). `RobotGhosts` poses the ghosts through the
  same call with the same limits. `Erupt.Boundary.Tests` and CI fail when another runtime file
  assigns `jointPosition`.
- **The software clamp is the limit.** The joints are teleported every physics step and PhysX
  does not apply joint limits to a teleport; the controller frees the revolute joint locks on
  purpose (limited locks on zero-stiffness drives let the solver move the joints between
  steps). Nothing in physics holds a limit, so nothing may write a joint except through
  `JointWriter`.
- **What a write reports.** `ApplyJointState` returns a `JointWriteReport`: the joints that
  were clamped (requested and applied value) and the names the robot does not have.
- **Velocity.** With `RobotProfile.limitUserVelocity` on, motion the user drives directly (IK
  dragging, jogging) moves each joint at most `maxVelocity × deltaTime` per frame. Trajectory
  playback and state mirroring are exempt. A drag that has not reached its target only because
  of that budget is not a refusal.

## Flows

- **Input**: backend (`XriControllerBackend`, `OpenXrHandBackend`, …) → `InteractionRouter` (filtering, target
  resolution, UI rejection by layer) → intents (`Select`, `Drag`, `Activate`…) → bindings
  (`SelectionRouterBinding`, `RobotIntentDispatcher` → the owning robot's
  `RobotInteractionController`, `TierUiRig` for tier 3 summon).
  The hand backend maps gestures onto the same intents (`PinchTracker`: pinch selects, a held
  or moved pinch drags; thumb-middle pinch is the axis; the Meta menu gesture is `Activate`),
  so nothing downstream of the router knows which is in use.
- **Selection**: `SelectionService.Current` (typed by `SelectionKind`) → tier 2
  (`ContextualMenuModel` over `VerbRegistry`) → verb handlers (core bindings or plugins), which
  act on the robot that owns the selection. The menu holds the verbs that can be pressed:
  the ones the table marks as not implemented yet, and the ones nothing has bound, are left
  out unless `TierUiRig.ShowUnimplementedVerbs` is on (a selection with nothing to offer has
  no menu). A verb may carry a condition
  (`VerbRegistry.ShowWhen`): it is then listed only for the selections the condition accepts. A verb that
  cannot run is a refusal on the router (`ContextualMenuView.Invoke`). A selection that
  belongs to a robot makes that robot active.
- **A module's buttons come and go with the module.** Nothing of a plugin is on a menu or on
  the tab strip unless the plugin is registered, which needs its prefab in the scene (and so
  its package in the project). The verbs a plugin adds (`Set Start`, `Plan`) are in the registry only while it is registered. The guideline verbs a plugin
  implements (`Set Goal`, `Execute`, `Preview` by the planning family) are in the table for
  everyone, but a menu leaves out a verb nothing has bound, and the family also gives its
  verbs a condition, so they leave the menus again when its last plugin is unregistered
  while the app runs. The demonstration family adds a tab and no verb (`Correct` on a plan
  is in the table, marked pending). A plugin removes
  its tab when it is unregistered. The gripper verb is core's own: it is there with any
  robot. `Assets/Erupt.App/Tests/Modules` checks all of this with the real prefabs.
- **Environment → planning scenes** (one Unity environment, N independent MoveIt planning
  scenes): `ObstacleFactory` → `EnvironmentRegistry.Added` → `MoveItPlanningSceneSync` attaches
  a `CollisionObjectPublisher`, which only notices that its object appeared, moved, was
  resized or was destroyed and reports it to the MoveIt plugin's `PlanningSceneFanout`. The
  fan-out sends each change to every robot's `collision_object` (through that robot's `Ros`,
  publisher registered with `CollisionObjectPublisher.QueueSize`), with the pose in that
  robot's `PlanningFrame` and `PlanningFrameId` in the header. A robot that joins at runtime
  receives the whole current environment; one that leaves is dropped.
- **Planning scenes → environment**: each robot's `collision_objects_ros` is heard separately
  (and its `get_planning_scene` fetched once), and there is one Unity object per id, built by
  `CollisionObjectsListenerSimple` (the Unity side of the mirror; it no longer talks to ROS).
  A ledger keyed by (namespace, id) holds what was last sent there, and every id has an
  owner: Unity, or the namespace it first arrived from.
  - A message that matches what that namespace was last sent (pose within 1 mm / 0.1°, same
    shapes) is an echo: dropped.
  - An unknown id is adopted: built in Unity from that robot's planning frame, and sent to
    every *other* namespace.
  - A change from the owning namespace is applied and sent to the others.
  - A change from a namespace that does not own the id is ignored, with one warning.
  - A REMOVE Unity did not send removes the Unity object and is sent to the others, never
    back to its origin; the echo of a REMOVE Unity sent is dropped.
  - Ids with a claimed prefix that no Unity object owns are removed from the planning scene
    that reported them.
  - Frames: everything is sent stamped with the robot's `PlanningFrameId`. MoveIt keeps and
    reports its world in its own planning frame, the root link of its robot model (the FR3's
    `base`), and each message says which frame its pose is in. An inbound pose is brought into
    the robot's planning frame first (`PlanningSceneEndpoint.InPlanningFrame`: the planning
    frame itself, or a link of that robot), so echoes match and objects land where they are for
    a robot whose base is not at the origin of the planning frame.
  - Leaving and coming back: when a scene is unloaded (the reset action reloads it) the objects
    Unity put into the planning scenes are taken out again, once each (`PlanningSceneFanout.Leave`
    / `Dispose`), and the echoes of those REMOVEs are remembered across the reload so they do
    not delete the next scene's objects of the same id. After an outage of the ROS side (the
    app started first, the endpoint or MoveIt restarted) `MoveItPlugin` notices the connection
    return, or a `move_group` answer that was long in coming, and resynchronises
    (`PlanningSceneFanout.Resync`): every Unity object is sent again in full, each planning
    scene is asked what it holds, and what a planning scene owned and no longer holds is removed.
- **Attach / detach**: every robot has its own `AttachedCollisionObjectListener` on its
  `attached_collision_objects_ros`; link names are resolved on that robot only. An object a
  robot carries is silent in that robot's namespace (MoveIt has it as an attached body) and
  ignored on every inbound topic, but the other namespaces keep getting MOVEs so their
  planners see it move. After the detach, the pose the carrying robot's planning scene reports
  is applied (whoever owns the id) and sent to the others. `MtcSolutionPlayer` applies the
  attach / detach steps of an executing solution through the listener of the robot being
  executed. `PlanningSceneEndpoint.DetachAllFromRobot` and `PublishRemove` act on one robot's
  planning scene.
- **A robot's base is moved** (`RobotInstance.BaseMoved`): the planning frame of that
  robot's planning scene is somewhere else in Unity. `PlanningSceneFanout.FrameMoved` sends
  every object Unity or another planning scene owns to that planning scene again, at its pose
  in the moved frame (pose only where the shapes are already there), and sends the other
  planning scenes nothing; the objects that planning scene itself owns are fixed in its
  frame, so their Unity objects move with it and the others are told. An object that cannot
  be sent then (paused, ROS away) is sent in full when it can be.
- **Room anchors** (`SceneAnchorCollisionBridge`, AR module): each shared anchor is an
  environment object, so it reaches the planning scenes like any other obstacle.
- **Planning**: end-effector verbs → the `PlanningPlugin` that serves the robot owning the
  end effector → that robot's session → `PlanResult` (with `RobotId`) → `TrajectorySelectable`
  (+ world handle, marked as that robot's; a plan that arrives for the active robot becomes
  the selection, so its menu is there) → trajectory verbs → preview on that robot
  (`JointTrajectoryPlayer` / `MtcSolutionPlayer`) / execute in that robot's namespace
  (`MoveItPlanningClient` / `PickPlaceClient`). The `execute` verb follows the execution's
  statuses (`PlanningPlugin.ExecuteAndReport`): whatever keeps the robot from arriving (not
  connected, no command topic, nothing to send, the server busy, an aborted solution, the
  robot not reaching the end) is a refusal at the plan's handle, in the planner's words.
- **Where the ROS robot is** (MoveIt plugin): when the first joint state of a robot arrives
  after the connection has come up (at the start, after a scene reset, after a reconnect),
  the Unity robot takes that state, once, so the user starts from where the real robot is
  and not from the profile's initial pose. It is written like every other state (clamped to
  the limits, a mismatch reported). It does not happen when something has taken the robot's
  pose since the connection came up (`ControlTaken`: the user moved it, a preview plays), and
  `adoptRobotStateOnConnect` on the plugin turns it off. A start the user has already set
  stays as they set it; they set it again when they want it to be where the robot is now.
- **Execution and mirroring** (MoveIt plugin): executing publishes the trajectory on the
  robot's command topic, which gives no completion signal, so `MoveItPlanningClient` follows the
  robot's `joint_states` instead: the Unity robot shows the ROS robot until it reports the
  trajectory's last point (`ExecutionPhase.Succeeded`) or a deadline passes (`Failed`), and then
  lets go. "Mirror joints" on the tab is the same mirror switched on by hand. Either stops when
  something else takes the robot's pose: `IRobotModel.ControlTaken`, raised by `BeginInteraction`
  (a drag, a jog, a mirrored hand) and by local playbacks (`JointTrajectoryPlayer`,
  `MtcSolutionPlayer`). Without that the mirror and the user would write the joints in turn.
  A plan that does not come (MoveIt refused, start or goal missing, ROS away) is a refusal on
  the router (`RefusalCode.PlanFailed` / `PlannerUnavailable`) with MoveIt's error in words.
- **Gripper**: the gripper verb is core's and moves the robot model's fingers
  (`IRobotGripper`); the model announces the command (`GripperCommanded`), and the MoveIt
  plugin sends the profile's open or closed position of the gripper's driver joint to that
  robot's `execute_trajectory` action, so MoveIt drives whichever controller the gripper has.
  A command that cannot be sent or fails is a refusal; the Unity gripper has moved either way.
- **Incoming joint data**: a planned trajectory, an MTC solution (every step) and a finished
  demonstration are checked against the robot's limits once, when they arrive; the joints that
  leave the limits by more than the tolerance (1e-3 rad, 1e-4 m) are stored with the result
  (`PlanResult.LimitViolations`), the user is told which joint, and the preview plays clamped.
  Executing such a plan or solution is refused (`ExecutionPhase.Refused`, nothing sent) unless
  the planner plugin's `allowExecutionOutsideLimits` is on; the gate is in the plugin's
  `Execute` and again where the command leaves (`MoveItPlanningClient.Execute`,
  `PickPlaceClient.ExecuteAsync`). Mirrored live joint states (`joint_states`, display
  trajectories) are shown clamped; a real robot outside the model's limits means the model or
  the profile is wrong, which `LimitMismatchMonitor` reports once per joint and then at most
  every ten seconds. Initial and named poses are clamped like any other write, and
  `RobotProfile.Validate()` lists pose values outside the limits.
- **Refusals**: whatever cannot proceed reports an `InteractionRefusal` (reason, world point,
  `Code`, `Subject`, `SourceId`) to `InteractionRouter.ReportRefusal`, which drops repeats of
  the same code and subject within half a second (a drag reports every frame) and raises
  `Refused`. Three consumers listen to that event and nothing else: `RefusalToast` (Erupt.Ui)
  shows the reason as text at the refusal's point, `JointLimitIndicator` (Erupt.Robot, one per
  robot) tints the link whose joint is at its limit, and `RefusalHaptics` (OpenXR backend,
  one per controller) double-pulses the controller that caused it. IK reports `JointLimit`
  when it falls short with a joint at a limit and `OutOfReach` otherwise; jogging into a
  limit reports `JointLimit`; a plan outside the limits reports `PlanOutsideLimits`; a
  mirrored state outside the model reports `LimitMismatch`.
- **ROS**: everything goes through `IRosBus`. `PluginHost` puts the live bus
  (`RosBus.Instance` → `LiveRosBus`) in the context and gives it to the robot registry; it is
  the only code that reads `RosBus.Instance` (checked by `Erupt.Boundary.Tests` and CI). A
  robot's own traffic (joint states, trajectories, MoveIt and MTC services and actions, its
  planning scene) uses relative names on `robot.Ros`, so it lands in that robot's namespace:
  `joint_states` is `/joint_states` for a robot without a namespace and `/left/joint_states`
  for one in `left`. Names that belong to no robot are absolute on `context.Ros`. Each plugin
  hands the bus to the components it owns, which stay idle until they have one. Tests pass a
  `FakeRosBus` the same way. `RosName.Resolve` applies ROS name rules (a leading `/` is
  absolute, anything else lands under the namespace); `bus.Scoped(ns)` returns a bus that
  resolves every name that way, the root namespace included, and `bus.InNamespace(ns)` does
  the same but leaves names alone when `ns` is empty. With one robot in the root namespace the
  wire is what a single-robot setup expects (`/collision_object`, `/plan_kinematic_path`, …).
  `LiveRosBus` keeps one `ROSConnection` for the life of the app (`DontDestroyOnLoad`): the
  connector never closes a connection whose object is destroyed, so a connection per scene
  left the old socket and thread behind at every scene reload. Components subscribe when they
  start and unsubscribe when they are destroyed; publishing and unsubscribing never create a
  connection, and nothing does while the app quits. The bus does not listen to TF on its own
  account, and drops the connector's TF listener when the scene its frame objects lived in is
  unloaded (a tool that wants TF, such as the connector's visualization suite, gets a new one).
- **The ROS address** is the one the build carries (`Robotics > ROS Settings`, the
  `ROSConnectionPrefab` resource) unless the user set another in the app. `IRosEndpoint`
  (Erupt.Ros: `Host`, `Port`, `DefaultHost`, `DefaultPort`, `ConnectTo(host, port)`) is how a
  bus says where it connects and is sent elsewhere; it is separate from `IRosBus`, so a
  plugin cannot move the connection, and `bus.Endpoint()` finds it through a robot's
  namespaced view. `LiveRosBus.ConnectTo` moves the one `ROSConnection`: a connection that
  is up or still trying is closed and opened at the new address (the connector's
  `Connect` ends the running connection thread first), and because the object stays, every
  subscription, publisher, service and action client on it is announced to the new endpoint.
  Before there is a connection the address is only noted and given to the connection when
  the bus creates it, ahead of its `Start`. `RosEndpointSetting` keeps the user's address in
  `UserSettings` together with the built-in endpoint it replaced, and applies it only while
  the build still carries that endpoint, so changed ROS settings or a new build are not
  hidden by an address entered earlier. `PluginHost` applies it once, before the first
  plugin registers. A switch to another address is not an outage in time (the new side may
  answer within milliseconds), so `MoveItPlugin` also watches the endpoint and treats a
  change like a first connection: the planning scenes are held back until the connection
  has settled, then every scene is sent and each robot is resynchronised (`Reconnected`).
- **Live tests**: `Assets/Erupt.App/Tests/Live` runs the reference scenes against a real ROS 2
  stack (opt-in through `ERUPT_LIVE_ROS`; `Tools/live-tests.sh`), with the scene's FR3 or with
  any shipped robot put in its place (`ERUPT_LIVE_ROBOT`, `ERUPT_LIVE_ARMS`). See its README.
- **Demonstration**: `DemonstrationPlugin` feeds `InteractionSampleBus` samples while a
  demonstration is being recorded; the demonstration plugin samples a robot's joint state
  into a `JointTrajectory`, replays it on that robot's player, and publishes it in that
  robot's namespace. A recording starts with Record on the tab or a
  message on `/record_start`, at any time, and ends with Stop (or the same message). One
  demonstration is recorded at a time, on the robot it was started for.
- **Haptics**: `CollisionHaptics` (OpenXR backend) pulses both controllers as any robot's tip
  nears a collider that is not part of a robot; the tips and the robots' own colliders come
  from the router's `IProximitySource`s, so nothing is assigned per scene. The user sets
  three things on the Settings tab while using the app: whether it pulses at all
  (`HapticsEnabled`, "Collision haptics: on / off"), how strongly at contact (`Strength`,
  0 to 100 % in steps of 5) and from how far (`Distance`, 5 to 50 cm in steps of 1). The
  component's serialized values are the defaults, it adds the section to the tier UI it
  finds in the scene, a change applies from the next frame and is remembered
  (`UserSettings`, written out when the app is paused or the component goes).
  `RefusalHaptics` pulses the controller whose action was refused, through `IHapticOutput`
  on the backend.

## Scenes

`Assets/Scenes/KitchenFR3.unity` is the single-robot reference scene (one FR3, root
namespace; it assigns the scene's `origin` object as the robot's planning frame, which lies
where the robot's own anchor does). `Assets/Scenes/MultiArmFR3.unity` is the multi-arm
sample: the same ERUPT root, rig and plugins, a table with two obstacles, and three FR3
robots `left`, `right` and `third` (id = namespace), `third` turned around to face the other
two. Each uses its own planning-frame anchor, so the ROS side is started without poses:
`ARMS="left=fr3 right=fr3 third=fr3"` (`arms:="left=fr3 right=fr3 third=fr3"` with
`arms.launch.py`); the poses `@x,y,z,yaw` that used to follow each arm are no longer needed.

In Unity the world convention is ROS (x, y, z) = Unity (x, z, y) in a planning frame, and an
imported robot looks along its local z, so a robot that stands in a shared frame at ROS yaw ψ
has a Unity yaw of 90° − ψ about Y in that frame.

The **Starter Scene** sample of `com.erupt.core` (Package Manager > ERUPT Core > Samples) is
the same root, rig and plugins with a floor, a table and no robot; see `installation.md`.

All three work in the editor without a headset (*Without a headset*, above).

## The AR module (optional)

`com.erupt.plugin.ar` is in a project only when the project wants AR. It needs
`jp.keijiro.apriltag` (a fork, by pinned git URL) and Meta's MR Utility Kit, asks for them
through `erupt.requires` like core asks for its forks, and its assembly stays out of the
build until both are there. Nothing outside the package refers to it, and nothing outside it
refers to AprilTag, the MR Utility Kit or the passthrough camera (`Erupt.Boundary.Tests`, CI):
deleting the package folder and the two manifest lines leaves the rest compiling and its tests
passing. Its plugin (`ar`) places robots at printed AprilTags through
`RobotInstance.PlaceBase`: the detected pose is shown as a translucent copy of the robot and
the user confirms it with the world-space **Place here** button beside the proposed base. See the package's `README.md`.

## History

ERUPT grew out of XRViz, a single-assembly research application of the Parasol Lab. The split
into a core, plugins and robot packages is recorded in each package's `CHANGELOG.md`.
