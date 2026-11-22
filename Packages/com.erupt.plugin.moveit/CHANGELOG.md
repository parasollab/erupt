# Changelog

All notable changes to `com.erupt.plugin.moveit` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed (the preview plays on a copy of the robot)

- **A plan's preview plays on a blue translucent copy of the robot**, in a loop, and the robot
  itself stays where it is (`RobotGhosts.PlayPreview`). Before, the preview moved the robot
  and looped until the plan was executed or replaced, with nothing on the menus to stop it.
- **Stop Preview** is on the plan's menu in the place of **Preview** while that plan's
  preview plays (`MoveItPlugin.IsPreviewPlaying`). A preview also ends when the plan is
  executed, when a new plan is asked for, on **Reset start/goal**, and when the robot's base
  is moved.
- Executing is unchanged: the preview ends and the robot follows the ROS robot's joint states.
- **A plan's handle sits halfway between the start and the goal** (the end effectors of the
  two ghosts), or 15 cm above the goal when the two are closer than that. It used to sit at
  the goal's end effector, which is where the robot now stands during a preview: the handle
  was inside the robot's own end-effector handle and could not be pointed at.
- A robot whose profile has no ghost prefab has no copy: its preview plays on the robot as
  before, and Stop Preview gives the robot its pose back.

### Changed (with core)

- The MoveIt tab is removed when the plugin is unregistered, and `Set Goal`, `Plan`,
  `Execute` and `Preview` leave the menus with the last planner (core's `PlanningPlugin`);
  `Set Start` always did. Core removed the application modes; the plugin never used them.

### Added (first run)

- **The robot shows where the ROS robot is.** `MoveItPlugin.adoptRobotStateOnConnect`
  (serialized, on; `AdoptRobotStateOnConnect`), `MoveItPlanningClient.AdoptsReportedState`,
  `IsWaitingToAdopt`, `StateAdopted`: when the first joint state of a robot arrives after the
  connection has come up (register, scene reset, reconnect), the Unity robot takes it, once
  per joint, clamped to the limits and with the mismatch refusal a mirrored state has. Joints
  reported by separate publishers are each taken when they first appear, for two seconds
  after the first. It does not happen when something took the robot's pose since the
  connection came up (the user moved it, a preview played), nor while the mirror is on (which
  shows every state anyway). A start the user set before is kept as it was set. Until now the
  robot stayed in its profile's initial pose until "Mirror joints" was switched on: the same
  pose as the simulated robots start in, and not the pose of a real one.
- `selectNewPlan` (serialized, on; `SelectNewPlan`): a plan that arrives for the robot the
  user is working with becomes the selection, so `Execute` and `Preview` are there without
  finding the plan's handle (which the previewing arm passes through). A plan for another
  robot leaves the selection alone.

### Changed

- The end-effector menu reads Set Goal, Open/Close Gripper, Set Start, Plan (Set Start is
  registered before the base class adds Plan).

### Fixed (buttons that did nothing)

- `RequestPlan` for no robot, or for a robot MoveIt has no session for, returned without a
  word (the tab's Plan button with no active robot, a planner asked for a robot it does not
  serve). It is a `PlannerUnavailable` refusal at the robot's end effector and a `PlanFailed`
  for the tab.
- A failed Execute reaches the user (core's execute verb now reports the status): "MoveIt is
  not connected.", "The robot's profile names no trajectory command topic.", "No planned
  trajectory to execute.", "Execution did not finish: …".
- `Preview` of a plan without a trajectory, for a robot that has left or that has no
  trajectory player, and `SetGoalFromRobot` for a robot without a session, are refusals; the
  last two were a log line or nothing.

### Added (a robot's base is moved)

- When a robot's base is put somewhere else (`RobotInstance.PlaceBase`), the plugin drops the
  robot's start, goal and plans and brings its planning scene up to date:
  `PlanningSceneFanout.FrameMoved(endpoint)` sends every object to that planning scene again
  at its pose in the moved planning frame (pose only where the shapes are already there) and
  nothing to the other planning scenes; objects that planning scene itself owns move with
  the robot in Unity and the others are told. Objects that cannot be sent then are sent in
  full when they can.
- Moving the base is refused while the robot executes a trajectory or carries an object
  (`AttachedCollisionObjectListener.IsCarrying`, which stays true until the pose the object
  was put down at has arrived).

### Changed

- `PlanningSceneFanout.Tick` sends nothing while the fan-out is suspended (ROS away): pending
  objects wait, as object changes already did.

### Fixed (found against a real ROS 2 stack, with every shipped robot)

- The planner tab starts at the pipeline's default (no planner named, so MoveIt uses the one
  it configured for the group) and its planner button cycles default → each listed planner →
  default. It used to pick the first planner of the list whenever the settings' default was
  not among them: once MoveIt listed its OMPL planners, every plan asked for
  `AnytimePathShortening`, which the ROS side had not configured, and failed.

### Fixed (found against a real ROS 2 stack)

- Inbound collision objects are read in the frame their header names. MoveIt reports its world
  in its own planning frame (the robot model's root link); a robot whose base is not at the
  origin of the planning frame Unity addresses it in got its echoes taken for conflicts and
  ROS-side objects placed off by the base pose (`PlanningSceneEndpoint.InPlanningFrame`).
- The gripper verb reaches the real gripper: `MoveItPlanningClient.CommandGripper` sends the
  profile's open or closed position to MoveIt's `execute_trajectory` action
  (`MoveItPlanningSettings.executeTrajectoryActionName`). It used to move the Unity gripper only.
- An execution is followed to its end and then lets the robot go
  (`Execute(plan, settled)`, `IsFollowingExecution`, `MirroringChanged`): the mirror used to stay
  on for ever and overwrote the user's next pose, later previews and replays. The mirror also
  stops on `IRobotModel.ControlTaken`. Execution now reports `Succeeded` or `Failed`.
- A plan that does not come is a refusal on the router (it only reached the planner tab), with
  MoveIt's error in words (`DescribeError`); a request made while ROS is away is refused at
  once instead of waiting for ever.
- Scene unload: Unity's obstacles are taken out of the planning scenes once each and the
  echoes are remembered across the reload (`PlanningSceneFanout.Leave`); unregistering no
  longer throws on a destroyed plugin object, which had left every subscription in place.
- Outages: `MoveItPlugin.ResyncWithRos`, `PlanningSceneFanout.Resync`, `Reconnected`; plans in
  flight fail, the planner query is repeated, the environment is sent again and each planning
  scene is asked what it holds. Also when a `move_group` comes up after the endpoint.
- `PlannersFor` lists what the robot's arm group can be asked for (`PlannersForGroup`): OMPL's
  bare group names are not planners.
- `MotionPlanRequestMsg` no longer carries the fork-only `smoothness_level` field; the
  `ExecuteTrajectory` action bindings were added (see `MESSAGES.md`).

### Added (any number of robots)

- `MoveItRobotSession`: per robot, the planning client on the robot's namespaced bus, the
  captured start and goal, the preview on that robot's trajectory player, its ghosts, planner
  discovery, its planning-scene endpoint and its attach mirror. `MoveItPlugin.Sessions`,
  `SessionFor(robot)`, `ActiveSession`; the members without a robot address the active robot.
  `set-start`, `set-goal`, `plan`, `preview` and `execute` act on the robot that owns the
  selected object or plan.
- `PlanningSceneFanout` and `PlanningSceneEndpoint`: Unity's one environment shared with
  every robot's own planning scene. Each change goes to every robot's `collision_object` in
  that robot's planning frame; each robot's `collision_objects_ros` is heard separately, with
  a ledger per (namespace, id) and an owner per id (Unity, or the namespace the id first
  arrived from): echoes are dropped, unknown ids are adopted and forwarded to the other
  namespaces, a change from the owner is applied and forwarded, a change from a non-owner is
  ignored with one warning, and a REMOVE Unity did not send removes the object and is
  forwarded to the others only. A robot that joins at runtime receives the environment; one
  that leaves is dropped. `MoveItPlugin.PlanningScene`, `PlanningSceneOf(robot)`.
- One `AttachedCollisionObjectListener` per robot (`Robot`, `For(robot)`, `Release`,
  `IsAwaitingPlacePose`): link names are resolved on that robot only. An object a robot
  carries is silent in that robot's namespace and keeps publishing MOVE to the others
  (`CollisionObjectPublisher.attachedNamespace`); the place pose after a detach is taken from
  the carrying robot's planning scene and sent to the others.

### Changed (any number of robots)

- `CollisionObjectPublisher` no longer talks to ROS. It detects that its object appeared,
  moved, was resized or was destroyed and reports it to an `ICollisionObjectSink` (the
  fan-out); `Topic` is the relative name `collision_object`. `frameId` and `worldOrigin` are
  kept as serialized fields but no longer drive what is published: every robot gets the pose
  in its own planning frame, stamped with its planning frame id, and the orientation is now
  expressed in that frame too (it used to be sent in Unity world space).
- `CollisionObjectsListenerSimple` is the Unity side of the mirror only (one object per id);
  `Initialise(IRosBus)` is gone. The fan-out subscribes each robot's topic and fetches each
  robot's `get_planning_scene`.
- `CollisionObjectPublisher.PublishRemove` and `DetachAllFromRobot` (static, root namespace)
  became `PlanningSceneEndpoint.PublishRemove(id)` and `DetachAllFromRobot(done)` (one
  robot's planning scene; relative `apply_planning_scene`), also as
  `MoveItPlugin.PublishRemove(robot, id)` / `DetachAllFromRobot(robot, done)`.
- The `collision_object` publisher is registered (with `QueueSize`) when a robot's planning
  scene is connected, not by an app-level prewarm.
- With one robot in the root namespace the wire is unchanged: `/collision_object`, the same
  frame and the same ADD / MOVE / REMOVE sequence.

### Added

- Joint limits. A plan is checked against the robot's limits when it arrives
  (`PlanResult.LimitViolations`); the user is told which joint leaves them, the preview plays
  clamped, and `Execute` refuses the plan (`ExecutionPhase.Refused`, nothing published) unless
  the plugin's `allowExecutionOutsideLimits` is on. `MoveItPlanningClient` holds the same
  policy where the trajectory is published (`AllowExecutionOutsideLimits`, `LimitTolerance`).
- `MoveItPlanningClient.Refused`: mirrored joint states and display trajectories outside the
  robot model's limits are shown clamped and raise one `LimitMismatch` refusal per joint
  (repeated at most every 10 s), which the plugin forwards to the interaction router.
- `AttachedCollisionObjectListener`: a carried object is placed on the link at the message's
  link-relative pose and locked while attached (nothing published for its id, rigidbodies
  kinematic, not grabbable, selection dropped); detach restores all of it and waits for the
  re-ADD's place pose (`resumeTimeout`) before the publisher resumes, then raises `Released`.
  Attach notices that only restate an existing carry are ignored; one whose pose would make
  the object jump further than `maxAttachJump` is held back and re-checked for
  `pendingAttachSeconds`. `ApplyExpected` / `ForgetExpected` apply an attach or detach an
  executing solution reports before the watcher does (the watcher's late notices are then
  no-ops); `ReleaseAll` puts every carried object back at once. An object first seen in an
  attach notice is built from the notice's geometry.
- `CollisionObjectsListenerSimple`: `ClaimIdPrefix` (a planning-scene object with a claimed
  prefix that no live Unity object owns is removed from the planning scene instead of
  mirrored), `ClearRegistry` / `OnRegistryCleared`, `SpawnFromAttachedObject`,
  `awaitingDetachPose` (the ADD after a detach sets the place pose even on a Unity-owned
  object).
- `CollisionObjectPublisher`: `PublishRemove(id)`, `DetachAllFromRobot(done)` (one robot-state
  diff on `/apply_planning_scene`), `attachedToRobot`, `MarkTransformAsPublished`.
- `GrabbableCollisionObjectFactory.Configure`: the grab stack plus a publisher, used for every
  mirrored primitive and mesh.
- `AttachedObjectPlacement.Place`: parents a mirrored object under a robot link at the
  link-relative pose of an `AttachedCollisionObject` (FLU link frame folded into the project's
  world convention, link scale cancelled). Used by the MTC plugin's solution preview to show an
  object riding the gripper where the plan puts it.

### Fixed

- `CollisionObjectsListenerSimple`: every message for an attached id is ignored, not only a
  REMOVE, and that check now comes before the Unity-owned branch (MoveIt's world REMOVE of a
  carried object deleted a Unity-owned one). A re-ADD with unchanged geometry moves the object
  instead of rebuilding it; a rebuild no longer publishes a REMOVE for the id; a pose applied
  from ROS is not echoed back as a MOVE.
- `CollisionObjectPublisher` reported cylinders and capsules at half their height: Unity's
  meshes are 2 m tall at Y scale 1, and the publisher sent the scale as the height. It now
  doubles it, the inverse of what `CollisionObjectsListenerSimple` does on the way in.

### Changed

- `MoveItPlanningClient.CaptureRobotState` stamps the joint state with the robot's planning
  frame id (`RobotInstance.PlanningFrameId`) instead of its base link.
- The plugin plans for the registry's active robot (`IEruptContext.Robots.Active`) and sends
  everything on that robot's namespaced bus with relative names (`plan_kinematic_path`,
  `query_planner_interface`, `collision_objects_ros`, `attached_collision_objects_ros`,
  `get_planning_scene`): unchanged on the wire for a robot without a namespace, `/left/...`
  for one in `left`. `MoveItPlanningClient` is constructed with a `RobotInstance`.
- Robot-specific settings moved to the `RobotProfile`: `MoveItPlanningSettings` lost
  `planningGroupName`, `jointStateTopic`, `executeTrajectoryTopic` and the two joint-name
  prefixes; `defaultPlannerId` defaults to empty (it used to name a planning group);
  captured robot states carry the profile's base link as frame id; `MoveItPlugin` lost its
  `player` and `ghosts` fields (the robot's own are used); `AttachedCollisionObjectListener`
  lost its controller reference and name prefixes (`SetRobot`, the robot's name rule).

- Every script is in the `Erupt.Plugins.MoveIt` namespace (script GUIDs unchanged).
- `CollisionObjectsListenerSimple` and `AttachedCollisionObjectListener` no longer read the
  static ROS bus: `MoveItPlugin` hands them the context bus at registration
  (`Initialise(IRosBus)`), and they stay idle until then.
- Goal constraints cover the robot's IK chain (`IRobotModel.JointNames`) only. The joint state
  now also carries gripper joints; the start state keeps them, the goal no longer pins them.

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
