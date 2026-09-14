# ERUPT architecture

## Packages

```
Packages/com.erupt.core            ERUPT Core — universal features + the plugin contract
Packages/com.erupt.plugin.moveit   MoveIt 2 planning + planning-scene sync (Planning family)
Packages/com.erupt.plugin.mtc      MoveIt Task Constructor solutions + pick/place study tooling (Planning family; depends on moveit)
Packages/com.erupt.plugin.rader    RADER demonstration recording/replay/publish, FERL feedback, hand + gripper mirroring (Demonstration family)
Assets/Erupt.App                   the reference app (KitchenFR3): scene-specific scripts only
Packages/ROS-TCP-Connector, URDF-Importer   parasollab forks (submodules)
```

## Assemblies (arrows = references; core never points right)

```
Erupt.Interaction.Core  ←  Erupt.Interaction.Backends.{OpenXR, Desktop, VisionOS}
Erupt.Interaction.Core  ←  Erupt.Ui  ←  Erupt.Interaction.Backends.OpenXR (raycaster installer)
Erupt.Ros               →  ROSTCPConnector, .Messages, .MessageGeneration
Erupt.Robot             →  Erupt.Interaction.Core, Erupt.Ros
Erupt.Environment       →  Erupt.Interaction.Core, Erupt.Ui, XRI
Erupt.Plugins           →  all of the above core assemblies
Erupt.Plugins.MoveIt    →  Erupt.Plugins, Erupt.Plugins.MoveIt.Messages
Erupt.Plugins.Mtc       →  Erupt.Plugins.MoveIt, Erupt.Plugins.Mtc.Messages
Erupt.Plugins.Rader     →  Erupt.Plugins, Erupt.Plugins.Rader.Messages, XR Hands
Erupt.App               →  everything (the reference app may see plugins)
Erupt.Core.Editor       →  core (generator, templates)
Tests: Erupt.TestSupport (FakeRosBus, FakeRobotModel, TestInteractionSource) ← every *.Tests; Erupt.Boundary.Tests enforces the graph
```

## The ERUPT root (EruptCore.prefab)

`PluginHost` (discovery, dependency order, context, undo stack) · `SelectionService` (the one
selection) · `SelectionHighlighter` · `SelectionRouterBinding` (router select → selection) ·
`ModeManager` (Build / Plan / Teach) · `EnvironmentRegistry` (the mirror; `worldOrigin`) ·
`TierUiRig` (tiers 1–3, the `IUiHost`) · `ObstacleVerbBindings` · `ScenePlacementTab` ·
`XrUiRaycastInstaller` (OpenXR).

## Flows

- **Input**: backend (`XriControllerBackend`, …) → `InteractionRouter` (filtering, target
  resolution, UI rejection by layer) → intents (`Select`, `Drag`, `Activate`…) → bindings
  (`SelectionRouterBinding`, `RobotInteractionRouterBinding`, `TierUiRig` for tier 3 summon).
- **Selection**: `SelectionService.Current` (typed by `SelectionKind`) → tier 2
  (`ContextualMenuModel` over `VerbRegistry`) → verb handlers (core bindings or plugins).
- **Environment**: `ObstacleFactory` → `EnvironmentRegistry.Added` → `MoveItPlanningSceneSync`
  attaches a `CollisionObjectPublisher`; `CollisionObjectsListenerSimple` mirrors ROS objects
  back into the registry. Removals carry an origin so a ROS-commanded removal is not echoed.
- **Planning**: end-effector verbs → `PlanningPlugin` → `PlanResult` → `TrajectorySelectable`
  (+ world handle) → trajectory verbs → preview (`JointTrajectoryPlayer` / `MtcSolutionPlayer`)
  / execute (`MoveItPlanningClient` / `MtcClient`).
- **ROS**: everything goes through `IRosBus` (`RosBus.Instance` → `LiveRosBus`; tests
  `RosBus.Override(new FakeRosBus())`).
- **Demonstration (Teach)**: `DemonstrationPlugin` gates on Teach and feeds
  `InteractionSampleBus` samples while recording; the RADER plugin samples the robot's joint
  state into a `JointTrajectory`, replays it on `JointTrajectoryPlayer`, and publishes it.
- **Haptics**: `CollisionHaptics` (OpenXR backend) pulses both controllers as the tip link
  nears a non-robot collider.

## History

Built in phases 0–6 on `design_refactor` from the former XRViz project (tag
`pre-plugin-refactor`); each phase has a plan and a report under `refactor/plugin/`, indexed
in `refactor/README.md`. RADER's provenance: `parasollab/RADER`, branch `xrviz`, absorbed in
Phase 5.
