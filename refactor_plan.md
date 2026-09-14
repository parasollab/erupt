
# ERUPT Core + Plugin refactor (release / rebrand)

## Context

ERUPT is being rebranded from a robot *planning* toolkit to a robot *programming* toolkit
(**Extended Reality Universal Programming Toolkit**) for a proper open-source release. The
codebase already went through Guidelines Phases 0-2 (interaction router + backends in
`Assets/Interaction`, tier UI in `Assets/UI`, ROS seam in `Assets/Scripts/Ros`), but every
feature (MoveIt planning, MTC, pick/place study, RADER LfD) is still a flat pile of
MonoBehaviours in `Assets/Scripts`, compiled into one `xrviz` assembly, and hand-wired into
`KitchenFR3.unity`. There is no boundary between "what ERUPT always provides" and "what a
particular planner / LfD method provides", so adding a new method means editing core files.

Target: **ERUPT Core** owns the universal features (robot model + IK, environment mirror,
interaction router/backends, selection, modes, undo, tier UI, ROS transport, telemetry) and
exposes a **plugin contract**. Each feature family (planning, LfD, later grasp/language) is a
**template**: a base class + package shape that declares its ROS messages, domain logic,
verbs/tabs/widgets, and scene attachment. Concrete implementations (MoveIt, MTC, RADER) are
plugins built on a template. An **editor generator** scaffolds a new plugin from a template
with setup instructions.

## Decisions (settled with the user)

| # | Question        | Decision                                                                                                                                                                                                            |
| - | --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1 | Packaging       | UPM embedded packages:`Packages/com.erupt.core`, `Packages/com.erupt.plugin.<name>`                                                                                                                             |
| 2 | First plugin    | `com.erupt.plugin.moveit` (kinematic planning + planning-scene sync)                                                                                                                                              |
| 3 | MTC             | Separate`com.erupt.plugin.mtc`, depends on the MoveIt plugin                                                                                                                                                      |
| 4 | UI port         | Yes: MoveIt planner panel and MTC dashboard ported from UI Toolkit to tier 3 tabs + tier 2 verbs (uGUI);`TierUiRig` wired into `KitchenFR3`; `WristMenuController`, `.uxml` panels, `UIDocument`s deleted |
| 5 | Scenes / legacy | `KitchenFR3` (+ its loading scene) is the reference app; tag `pre-plugin-refactor`, then delete other scenes, `_Recovery`, dead scripts, legacy wrist-menu path, opt-in flags                                 |
| 6 | RADER           | Absorb generic parts into Core; LfD remainder becomes`com.erupt.plugin.rader` on the LfD template; submodule dropped                                                                                              |
| 7 | Name            | ERUPT = Extended Reality Universal Programming Toolkit                                                                                                                                                              |

## Findings that shape the design (from the surveys)

- Dependency direction is already right: `Erupt.Interaction.Core` and `Erupt.Ui` cannot see feature code. Everything else sits in `Assets/xrviz.asmdef` (Assets root, swallows `Assets/Scripts` and all 125 generated messages under `Assets/RosMessages`).
- Open seams a plugin can use today: `UiTierRegistry.RegisterPanel`, `TabbedPanelView.AddTab` (no non-test callers), `ContextualMenuModel.Bind` (only caller `Assets/Scripts/Ui/EruptVerbBindings.cs`, three obstacle verbs), `InteractionRouter.Awake` scene-scan + `Register` (`Assets/Interaction/Core/InteractionRouter.cs:55-83`, the pattern to copy for plugin discovery), `RosBus.Instance` / `IRosBus` (single `ROSConnection` call site `LiveRosBus.cs:28`).
- Closed doors: `VerbTable` is a static dictionary; `SelectionKind` a closed enum (stays closed by guideline); `TierUiRig.Awake` hard-codes tier composition; `ModeManager` has zero feature consumers; `UndoStack` is reachable only through `TierUiRig`.
- **The tier UI is in no scene or prefab.** `KitchenFR3` runs the legacy wrist menu (`WristMenuController` on the shared `XR Origin (XR Rig).prefab`), `MoveItPlanningRequestMenu.prefab` (UI Toolkit), and `MTCDashboardPanel` as an override on a second instance of that same prefab. `InteractionRouter` + two `XriControllerBackend` rays are scene-local under the rig instance. `Robot IK Manager` is scene-local and carries `RobotInteractionRouterBinding`. `MTCManager` root holds `MTCDataManager` + `PickPlaceActionClient`. Two fr3 robots exist (`fr3` root with `MTCTrajectoryPlayer`; prefab instance `fr3 (1)`). Hazards: missing-script component on `Ground` (`KitchenFR3.unity:6247`), duplicated `ResetKitchenScene` on the rig, `CollisionObjectsListenerSimple` on both `CollisionObjectListener` and `Ground`, a `ROS2 Action Demo` sample object.
- De-facto core services with no interface: `CollisionObjectsListenerSimple` (objectsById registry, 5+ dependents), `DirectArticulationIKController` (robot model, public API already clean: `JointNames`, `EndEffector`, `TrySolveToTarget`, `TryNudgeJoint`, `ApplyJointState`, `GetJointStatePositions`, `FindLinkTransform`). `ObstacleFactory.Create` is MoveIt-coupled only at `ObstacleFactory.cs:67-72` (adds `CollisionObjectPublisher`, calls `listener.RegisterUnityOwnedObject`).
- Messages: core needs only connector built-ins (Std, Geometry, Shape, BuiltinInterfaces, Trajectory, Sensor). `Moveit` (100 files) is MoveIt-plugin; `MoveitTaskConstructorMsgs` (16) + `StudyInterfaces` (3) are MTC-plugin. Six files under `Assets/RosMessages/{Trajectory,Sensor}` are byte-identical duplicates of connector built-ins (CS0436 hazard) and get deleted. `Assets/msgbrowser_settings.asset` points at a machine-local path.
- RADER (`Packages/RADER`, submodule, fork branch `xrviz`): 28 global-namespace scripts; only `UR5eAnalyticalIK`, `TargetSphere`, `CollisionHaptics` and four AR/ArUco scripts appear in any scene; its IK stack is parallel to `DirectArticulationIKController`; it bypasses `IRosBus`; asmdef references `Oculus.VR` + MRUK; `ARPlaneColorizer` exists with the same name in both RADER and `Assets/Scripts`. `Assets/xrviz.asmdef` references RADER by GUID; the only C# use is `ChArUcoTrackingManager` in `MarkerRobotPlacement.cs` (AR scene only).
- Submodules: `ROS-TCP-Connector` (branch `action-support`, dirty: VisionOS platform + a sample) and `URDF-Importer` (dirty: 20 uncommitted mimic-joint files). **A fresh clone does not reproduce this project until those are committed on the parasollab forks.**
- Editor idioms (`Assets/Interaction/Editor`): `[MenuItem("ERUPT/Refactor/...")]`, `EditorSceneManager.OpenScene` on a const path, `SerializedObject` + `ApplyModifiedPropertiesWithoutUndo` for every field write, never modify a prefab asset (assert `PrefabUtility.IsPartOfPrefabInstance`), fenced `XXX_BEGIN/END` log blobs. No `EditorWindow`, `AssetDatabase.CreateAsset`, `ScriptableObject`, or `[CustomEditor]` anywhere; the generator introduces these.
- Tests: per-assembly `*.Tests.asmdef`, `overrideReferences` + `nunit.framework.dll`, `defineConstraints: ["UNITY_INCLUDE_TESTS"]`, `RosBus.Override(fake)` / `Reset()`, PlayMode by design (backlog B17). `FakeRosBus` lives in `Erupt.Ros.Tests`, which references `xrviz`. Unity 6000.2.1f1 at `/Applications/Unity/Hub/Editor/6000.2.1f1`. No CI. Build Settings: `KitchenLoadingSceneFR3` (entry) → `KitchenFR3`. `productName: XRViz`.
- Dead: `GrabMoveDiagnostics.cs`, `TransformSearchExtensions.cs`, `FarOnlySelectFilter.cs`, `Assets/UI/WristUIButton.prefab`, `Assets/AddPlugin.uss`, `Assets/UI Toolkit/XRDashboard.uxml` + `Assets/Style.uss` + `PanelSettings.asset` (AR only), `Temp_VisionOS/` (generated Xcode output, not gitignored).

---

## Target layout

```
Packages/
  com.erupt.core/                     package.json  "ERUPT Core"  (deps: ros-tcp-connector, urdf-importer,
    │                                 xr.interaction.toolkit, xr.hands, inputsystem, ugui, textmeshpro)
    Runtime/
      Interaction/                    Erupt.Interaction.Core        (moved, asmdef+GUID kept)
      Interaction/Backends/{OpenXR,Desktop,VisionOS}/   (moved, asmdefs kept)
      Ros/                            Erupt.Ros          NEW: IRosBus, RosBus, LiveRosBus, RosUnityConversion, FPSPublisher
      Robot/                          Erupt.Robot        NEW: IRobotModel, DirectArticulationIKController, RobotInteractionRouterBinding,
                                                              JointTrajectoryPlayer (ex TrajectoryReplay), SpawnGhosts, TranslucentOverride
      Environment/                    Erupt.Environment  NEW: EnvironmentRegistry, EnvironmentObject, IEnvironmentSync, ObstacleFactory,
                                                              ObstacleCommands, ObstacleSnapshot, ObstacleSnapping, SelectableGrabController,
                                                              XR*Transformer*, ObstacleVerbBindings (ex EruptVerbBindings)
      Ui/                             Erupt.Ui           (moved) + VerbRegistry, IUiHost, Billboard
      Plugins/                        Erupt.Plugins      NEW: IEruptPlugin, EruptPluginBehaviour, PluginHost, IEruptContext,
                                                              PlanningPlugin, DemonstrationPlugin, PlanResult, TrajectorySelectable
    Editor/                           Erupt.Core.Editor  NEW: PluginGeneratorWindow, PluginTemplate, Templates/**, (migration tools until Phase 3)
    Tests/
      Support/                        Erupt.TestSupport  NEW: FakeRosBus, TestInteractionSource (UNITY_INCLUDE_TESTS)
      Interaction/ Ui/ Ros/ Environment/ Plugins/        test asmdefs (moved + new)
    Prefabs/EruptCore.prefab          PluginHost, SelectionService, ModeManager, EnvironmentRegistry, TierUiRig
    README.md  CHANGELOG.md  LICENSE  Documentation~/plugin-authoring.md  Documentation~/architecture.md

  com.erupt.plugin.moveit/            package.json (dep com.erupt.core)
    Runtime/                          Erupt.Plugins.MoveIt: MoveItPlugin (PlanningPlugin), MoveItPlanningClient, MoveItPlanningSceneSync,
                                        CollisionObjectPublisher, CollisionObjectsListener, AttachedCollisionObjectListener, PlannerSettingsTab
    Runtime/Messages/Moveit/          Erupt.Plugins.MoveIt.Messages  (moved from Assets/RosMessages/Moveit)
    Tests/                            Erupt.Plugins.MoveIt.Tests
    Prefabs/MoveItPlugin.prefab       README.md  MESSAGES.md

  com.erupt.plugin.mtc/               package.json (deps core, moveit)
    Runtime/                          Erupt.Plugins.Mtc: MtcPlugin (PlanningPlugin), MtcClient (ex MTCDataManager), MtcSolutionPlayer
                                        (ex MTCTrajectoryPlayer), SolutionsTab (ex MTCDashboardPanel), PickPlaceActionClient, PickPlaceTaskRecorder
    Runtime/Messages/{MoveitTaskConstructorMsgs,StudyInterfaces}/   Erupt.Plugins.Mtc.Messages
    Tests/  Prefabs/MtcPlugin.prefab  README.md  MESSAGES.md

  com.erupt.plugin.rader/             (Phase 5) Erupt.Plugins.Rader: RaderPlugin (DemonstrationPlugin), FERL, InfoLog, PointCloudPublisher,
                                        HandMirror, Robotiq2fGripperMirror (on IRobotModel), demonstration recorder ported from SetupUI onto IRosBus
  ROS-TCP-Connector/  URDF-Importer/  (submodules, unchanged)

Assets/
  Erupt.App/                          Erupt.App asmdef: SystemPrewarmer, ResetKitchenScene, LightFlicker, SpawnHuman, FPSSetter,
                                        SceneAnchorCollisionBridge (Meta MRUK; refs moveit plugin), WristMenuController (until Phase 3)
  Scenes/KitchenLoadingSceneFR3.unity  Scenes/KitchenFR3.unity
  fr3/  Models/  Materials/  Prefabs/{Shapes, XR Origin (XR Rig), fr3 (1)}  ...art and vendor folders unchanged
```

Assembly reference graph (arrows = "references"; core assemblies never point right):

```
Erupt.Interaction.Core ← Erupt.Interaction.Backends.* 
Erupt.Interaction.Core ← Erupt.Ui ← Erupt.Plugins
Erupt.Ros (→ ROSTCPConnector, .Messages, .MessageGeneration)
Erupt.Robot (→ Erupt.Interaction.Core, Erupt.Ros, ROSTCPConnector.Messages)
Erupt.Environment (→ Erupt.Interaction.Core, Erupt.Ui, XRI)
Erupt.Plugins (→ all of the above core assemblies)
Erupt.Plugins.MoveIt (→ Erupt.Plugins, Erupt.Plugins.MoveIt.Messages)
Erupt.Plugins.Mtc (→ Erupt.Plugins.MoveIt, Erupt.Plugins.Mtc.Messages)
Erupt.App (→ everything; the reference app may see plugins)
```

---

## Plugin contract (sketch)

```csharp
// Erupt.Plugins
public interface IEruptPlugin {
    string Id { get; }                              // "moveit", "mtc", "rader"
    string DisplayName { get; }
    IReadOnlyList<string> DependsOn { get; }        // plugin ids, resolved by PluginHost
    void OnRegister(IEruptContext ctx);             // contribute verbs/tabs/widgets, subscribe ROS
    void OnUnregister(IEruptContext ctx);
    void OnModeChanged(AppMode mode);               // Build / Plan / Teach
}

public abstract class EruptPluginBehaviour : MonoBehaviour, IEruptPlugin { /* default no-op hooks, Context property */ }

public interface IEruptContext {
    IRosBus Ros { get; }                            // RosBus.Instance today; injected so tests substitute
    IRobotModel Robot { get; }                      // DirectArticulationIKController
    EnvironmentRegistry Environment { get; }
    SelectionService Selection { get; }
    ModeManager Modes { get; }
    UndoStack Undo { get; }
    InteractionRouter Router { get; }               // InteractionSampleBus stays static
    IUiHost Ui { get; }
}

public interface IUiHost {                          // Erupt.Ui; implemented by TierUiRig
    void RegisterVerb(SelectionKind kind, Verb verb, Action<ISelectable> handler, string pluginId);   // adds row entry
    void BindVerb(string verbId, Action<ISelectable> handler);                                        // implements a guideline verb
    PanelTab AddTab(string id, string label, Action<RectTransform> build);                            // tier 3
    void SummonTab(string id);
    void RegisterWidget(IWorldWidget widget);       // in-world widgets; tier 1 is closed (cap 4)
}

// PluginHost (MonoBehaviour, on EruptCore prefab): in Start(), FindObjectsByType<EruptPluginBehaviour>(Include inactive),
// topo-sort by DependsOn (throw on cycle / missing), build EruptContext from scene services (FindFirstObjectByType with
// LogError if absent, same discipline as InteractionRouter.Awake), call OnRegister in order; ModeManager.ModeChanged → OnModeChanged;
// OnDestroy → OnUnregister in reverse.

// Templates
public abstract class PlanningPlugin : EruptPluginBehaviour {
    public abstract InteractionRefusal SetGoal(ISelectable endEffector);          // verb EndEffector/set-goal
    public abstract void RequestPlan(PlanPreferences prefs, Action<PlanResult> done);
    public abstract void Preview(PlanResult plan); public abstract void StopPreview();
    public abstract void Execute(PlanResult plan, Action<ExecutionStatus> status);
    // Base OnRegister contributes: EndEffector/set-goal, EndEffector/plan (EruptAddition), Trajectory/preview, Trajectory/execute,
    // tier 3 tab "planner-{Id}" built by BuildSettingsTab(RectTransform); spawns a TrajectorySelectable (SelectionKind.Trajectory)
    // per PlanResult so Trajectory verbs can appear (today nothing creates Trajectory selectables).
}
public sealed class PlanResult { public JointTrajectoryMsg Trajectory; public object PlannerPayload; public string PlannerId; }

public abstract class DemonstrationPlugin : EruptPluginBehaviour {
    protected abstract void OnSample(InteractionSample s);      // subscribed to InteractionSampleBus only while Teach && recording
    public abstract void StartDemonstration(); public abstract void StopDemonstration(); public abstract void Publish();
    // Base contributes: tier 3 tab "demos-{Id}" (record/stop/list), Trajectory/correct verb, Teach-mode gating.
}
```

Environment cut (keeps Guidelines P1: MoveIt planning scene stays authoritative; core holds the Unity mirror):

| Today                                                                                             | Becomes                                                                                                                                                                                                  | Assembly              |
| ------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------- |
| `CollisionObjectsListenerSimple.objectsById` + `RegisterUnityOwnedObject` (three writers, B1) | `EnvironmentRegistry` (single writer): `Register(EnvironmentObject)`, `Unregister(id, RemovalOrigin)`, `TryGet`, `WorldOrigin`, events `Added/Removed/Attached/Detached`                     | `Erupt.Environment` |
| `"Selectable"` tag + mesh-name sniffing (`EruptVerbBindings.PrimitiveTypeOf`)                 | `EnvironmentObject : MonoBehaviour` { `Id`, `Owner` (Unity/Remote), `PrimitiveType?`, `IsMesh` }                                                                                               | `Erupt.Environment` |
| `ObstacleFactory.Create(..., listener)` adds `CollisionObjectPublisher`                       | `ObstacleFactory.Create(snapshot, registry)` adds `EnvironmentObject` and registers; no publisher                                                                                                    | `Erupt.Environment` |
| `ObstacleFactory.Destroy(publishRemoval)` sets `suppressRemoveOnDestroy`                      | `registry.Unregister(id, publishRemoval ? Local : Remote)`                                                                                                                                             | `Erupt.Environment` |
| `CollisionObjectsListenerSimple` (ROS → Unity spawn)                                           | `MoveItPlanningSceneSync : IEnvironmentSync`: spawns remote objects into the registry; on `Added(Owner==Unity)` adds `CollisionObjectPublisher`; on `Removed(Remote)` suppresses the REMOVE echo | MoveIt plugin         |
| `CollisionObjectPublisher`, `AttachedCollisionObjectListener`                                 | unchanged behaviour, moved; attach →`registry.Attach(id, link)`                                                                                                                                       | MoveIt plugin         |
| `ObstacleCommands`, `EruptVerbBindings` take `CollisionObjectsListenerSimple`               | take`EnvironmentRegistry`; renamed `ObstacleVerbBindings`                                                                                                                                            | `Erupt.Environment` |

Robot cut: `IRobotModel` (Erupt.Robot) = the existing public surface of `DirectArticulationIKController` (`Root`, `EndEffector`, `JointNames`, `TryGetJointAngle`, `GetJointStatePositions`, `ApplyJointState`, `TrySolveToTarget`, `TryNudgeJoint`, `FindLinkTransform`, `BeginInteraction/EndInteraction`). `DirectArticulationIKController : MonoBehaviour, IRobotModel`. Phase 5 drops RADER's transform-based solvers (`IKSolver`/`CCDIK`/`UR5eAnalyticalIK`) rather than adding an `IIkSolver` seam; the articulation-body controller is the only kinematics path.

Verbs: `VerbRegistry` (instance, Erupt.Ui) seeded from the Guidelines Part 2 rows (`VerbTable` becomes the seed data only). `ContextualMenuModel` takes a `VerbRegistry`. Plugin-registered verbs carry `VerbOrigin.Plugin` + plugin id so the Part 8 review ("added a verb, not a menu") stays auditable. `SelectionKind` stays closed.

---

## Phases

Each phase ends with: project compiles, all PlayMode tests pass, `KitchenFR3` runs on Quest, one merge. Start each phase by copying its section to `refactor/0N-plan.md` and end with `refactor/0N-report.md`, matching the repo's existing convention.

### Phase 0 — Hygiene, tag, prune

- `git tag pre-plugin-refactor` on the current commit (after the maintainer commits the in-flight `mtc` branch work; the Phase 2 doc notes uncommitted maintainer edits in `CollisionObjectPublisher`, `KitchenFR3.unity` etc.).
- **User action (cannot be done from this repo):** commit and push the dirty `Packages/URDF-Importer` (20 files) and `Packages/ROS-TCP-Connector` (3 files + `Samples~`) changes on the parasollab forks, then `git add` the updated submodule pointers. Without this a fresh clone is not this project.
- `.gitignore`: add `/Temp_VisionOS/`. Delete `Temp_VisionOS/` and `Assets/_Recovery/` from disk (both untracked/ignored).
- Delete scenes: everything in `Assets/Scenes` except `KitchenFR3.unity` and `KitchenLoadingSceneFR3.unity` (Build Settings entry pair); remove the disabled entries from `ProjectSettings/EditorBuildSettings.asset`.
- Delete scene-only / dead scripts and assets: `GrabMoveDiagnostics.cs`, `TransformSearchExtensions.cs`, `FarOnlySelectFilter.cs`, `Quest3ControllerRayInteractor.cs` (absent from KitchenFR3; retires backlog B7/B14), `MarkerRobotPlacement.cs`, `ARPlaneColorizer.cs`, `SceneController.cs`, `RobotBaseTFPublisher.cs`, `Assets/Prefabs/{ARPlaneColored, ur5e_robot, Robot IK Manager, SelectionManager}.prefab` (verify none is instanced in the two kept scenes; the scene's `Robot IK Manager` and `SelectionManager` are scene-local), `Assets/UI/WristUIButton.prefab`, `Assets/AddPlugin.uss`, `Assets/Style.uss`, `Assets/UI Toolkit/{XRDashboard.uxml, PanelSettings.asset}`, `Assets/Editor/OpenCVForUnityDefine.cs` + `Editor.asmdef` (OpenCV was only used by the AR scene), `Assets/Samples/ROS TCP Connector/0.7.1/ROS 2 Action Client Demo/`, `Assets/RosMessages/Trajectory/` and `Assets/RosMessages/Sensor/` (six duplicate messages).
- Remove the `RADER` and `RADER.ArUco` GUID references from `Assets/xrviz.asmdef` (nothing in `Assets/` uses them once `MarkerRobotPlacement` is gone); `CollisionHaptics` stays available because the RADER package is still installed until Phase 5.
- `KitchenFR3.unity` fixes (via a one-off `ERUPT/Refactor/Clean KitchenFR3` editor command following `InteractionMigration.cs` idioms): remove the missing-script component on `Ground` and its duplicate `CollisionObjectsListenerSimple`, remove the duplicate `ResetKitchenScene` on the rig instance, delete the `ROS2 Action Demo` object. Inspect which fr3 the `Robot IK Manager` and `SpawnGhosts.realRobot` reference; delete the other robot (record the answer in the phase report).
- Gate: compile clean; existing 53+12 PlayMode tests pass; Quest smoke (create obstacle, set goal, plan, preview, execute; MTC record/preview/execute) unchanged.

### Phase 1 — Package extraction (mechanical) + environment cut

Goal: `xrviz.asmdef` is gone; every script lives in a package assembly; behaviour unchanged.

- Create `Packages/com.erupt.core/package.json`, `Packages/com.erupt.plugin.moveit/package.json`, `Packages/com.erupt.plugin.mtc/package.json` (model on `Packages/RADER/package.json`; embedded packages need no `manifest.json` entry).
- `git mv` **with `.meta` files** so GUIDs survive (scene/prefab references to `MonoScript`s and asmdefs are by GUID):

| From                                                                                                                                                                                                                                                               | To (asmdef)                                                                                                                                                               |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Assets/Interaction/Core/**`                                                                                                                                                                                                                                     | `com.erupt.core/Runtime/Interaction/**` (`Erupt.Interaction.Core`, unchanged)                                                                                         |
| `Assets/Interaction/Backends/**`                                                                                                                                                                                                                                 | `com.erupt.core/Runtime/Interaction/Backends/**` (unchanged)                                                                                                            |
| `Assets/Interaction/Editor/**`                                                                                                                                                                                                                                   | `com.erupt.core/Editor/Migration/**` (`Erupt.Core.Editor`, renamed from `Erupt.Interaction.Editor`; deleted in Phase 3)                                             |
| `Assets/Interaction/Tests/**`, `Assets/UI/Tests/**`                                                                                                                                                                                                            | `com.erupt.core/Tests/Interaction/**`, `Tests/Ui/**`                                                                                                                  |
| `Assets/UI/Core/**`                                                                                                                                                                                                                                              | `com.erupt.core/Runtime/Ui/**` (`Erupt.Ui`) + `Assets/Scripts/Billboard.cs`                                                                                         |
| `Assets/Scripts/Ros/*.cs`, `Conversions.cs`, `FPSPublisher.cs`                                                                                                                                                                                               | `com.erupt.core/Runtime/Ros/` (new `Erupt.Ros`)                                                                                                                       |
| `DirectArticulationIKController.cs`, `Quest3RobotInteractionController.cs`, `RobotInteractionRouterBinding.cs`, `TrajectoryReplay.cs`, `SpawnGhosts.cs`, `TransluscentOverride.cs` (file renamed to `TranslucentOverride.cs`)                        | `com.erupt.core/Runtime/Robot/` (new `Erupt.Robot`)                                                                                                                   |
| `Assets/Scripts/Obstacles/*.cs`, `SelectionManager.cs`, `SelectableGrabController.cs`, `XRGrabTransformerLockPose.cs`, `XRGrabTransformerScaleAxisLock.cs`, `XRTwoHandedScaleTransformer.cs`, `XRUIScaleTransformer.cs`, `Ui/EruptVerbBindings.cs` | `com.erupt.core/Runtime/Environment/` (new `Erupt.Environment`)                                                                                                       |
| `Assets/Scripts/Tests/FakeRosBus.cs`                                                                                                                                                                                                                             | `com.erupt.core/Tests/Support/` (new `Erupt.TestSupport`, `UNITY_INCLUDE_TESTS`, referenced by every test asmdef)                                                   |
| `CollisionObjectPublisher.cs`, `CollisionObjectsListenerSimple.cs`, `AttachedCollisionObjectListener.cs`, `MoveItPlanningRequestMenuUI.cs`                                                                                                                 | `com.erupt.plugin.moveit/Runtime/` (new `Erupt.Plugins.MoveIt`)                                                                                                       |
| `Assets/RosMessages/Moveit/**`                                                                                                                                                                                                                                   | `com.erupt.plugin.moveit/Runtime/Messages/Moveit/**` (new `Erupt.Plugins.MoveIt.Messages`, refs `Unity.Robotics.ROSTCPConnector.MessageGeneration` + `.Messages`) |
| `MTCDataManager.cs`, `MTCDashboardPanel.cs`, `MTCTrajectoryPlayer.cs`, `PickPlaceActionClient.cs`, `PickPlaceTaskRecorder.cs`                                                                                                                            | `com.erupt.plugin.mtc/Runtime/` (new `Erupt.Plugins.Mtc`)                                                                                                             |
| `Assets/RosMessages/{MoveitTaskConstructorMsgs,StudyInterfaces}/**`                                                                                                                                                                                              | `com.erupt.plugin.mtc/Runtime/Messages/**` (new `Erupt.Plugins.Mtc.Messages`)                                                                                         |
| `Tests/{CollisionObjectFlowTests, ObstacleCommandTests}.cs`                                                                                                                                                                                                      | `com.erupt.plugin.moveit/Tests/` ; `ObstacleCommandTests` split: registry half to `core/Tests/Environment`                                                          |
| `Tests/{MTCExecutionTests, PickPlaceActionTests}.cs`                                                                                                                                                                                                             | `com.erupt.plugin.mtc/Tests/`                                                                                                                                           |
| `SystemPrewarmer.cs`, `ResetKitchenScene.cs`, `LightFlicker.cs`, `SpawnHuman.cs`, `FPSSetter.cs`, `SceneAnchorCollisionBridge.cs`, `WristMenuController.cs`                                                                                          | `Assets/Erupt.App/` (new `Erupt.App`, refs core + both plugins + MRUK)                                                                                                |

- Delete `Assets/xrviz.asmdef`. Any script left under `Assets/` outside `Erupt.App` would fall into `Assembly-CSharp`; the gate greps for that.
- Environment cut (the one non-mechanical change, forced because `ObstacleFactory` in core must not reference `CollisionObjectPublisher`): add `EnvironmentRegistry`, `EnvironmentObject`, `IEnvironmentSync` to `Erupt.Environment`; rewrite `ObstacleFactory.cs:67-72` and `Destroy` per the table above; `CollisionObjectsListenerSimple` writes to the registry (its `objectsById` becomes a read-through property kept for the MTC player); new `MoveItPlanningSceneSync` component attaches publishers on `Added`. `ObstacleCommands` / `EruptVerbBindings` / `MTCTrajectoryPlayer` switch from listener to registry lookups.
- `KitchenFR3.unity`: add root `Environment` (`EnvironmentRegistry`, `WorldOrigin` = `origin`); add `MoveItPlanningSceneSync` next to `CollisionObjectsListenerSimple` on `CollisionObjectListener`. Done with an editor command that follows the `SerializedObject` idiom.
- Message generation: per-plugin `MESSAGES.md` recording source ROS packages + `erupt_ws` commit; the msg browser's output path is set to the plugin's `Runtime/Messages` before regenerating (document; `Assets/msgbrowser_settings.asset` is left as a local setting).
- Gate: compile; all tests pass in their new assemblies; new EditMode test `AssemblyBoundaryTests` (in `core/Tests`) uses `CompilationPipeline.GetAssemblies()` to assert no `Erupt.*` core assembly references `Erupt.Plugins.*` or `Erupt.App`, and greps `Packages/com.erupt.core` for `RosMessageTypes.Moveit` / `MoveitTaskConstructorMsgs` / `StudyInterfaces` (must be zero); Quest smoke unchanged.

### Phase 2 — Plugin contract, host, robot interface, selection merge

- `Erupt.Plugins` assembly: `IEruptPlugin`, `EruptPluginBehaviour`, `PluginHost`, `IEruptContext` + `EruptContext`, `PlanningPlugin`, `DemonstrationPlugin`, `PlanResult`, `PlanPreferences`, `ExecutionStatus`, `TrajectorySelectable`.
- `Erupt.Ui`: `VerbRegistry` (seeded from `VerbTable` rows), `IUiHost` implemented by `TierUiRig`, `IWorldWidget`; `ContextualMenuModel` takes the registry. `UndoStack` ownership moves from `TierOneBar` to `EruptContext` (bar receives it in `Initialise`).
- `Erupt.Robot`: `IRobotModel` extracted from `DirectArticulationIKController`; `RobotInteractionRouterBinding`, `JointTrajectoryPlayer`, `SpawnGhosts` consume the interface.
- Selection merge (retires the dual system and the Phase 1 opt-in flags): `SelectionManager` becomes `SelectionHighlighter` (material swap driven by `SelectionService.SelectionChanged`); `SelectableGrabController` listens to `SelectionService`; `SelectionManager.Instance` callers (`WristMenuController`, `MTCDashboardPanel`, `PickPlaceTaskRecorder`) updated. Robot-link selection in `Quest3RobotInteractionController` publishes `SelectionKind.RobotLink` / `EndEffector` through `SelectionService` (prerequisite for `set-goal` as a tier 2 verb in Phase 3).
- MoveIt plugin: `MoveItPlugin : PlanningPlugin` + `MoveItPlanningClient` (transport half of `MoveItPlanningRequestMenuUI`: `/plan_kinematic_path`, `/query_planner_interface`, `/joint_states`, trajectory publish; retires backlog B4 and the duplicated joint tables). `MoveItPlanningRequestMenuUI` shrinks to UI-only and drives the client (still UI Toolkit until Phase 3). MTC plugin: `MtcPlugin : PlanningPlugin` wrapping `MtcClient` (ex `MTCDataManager`, singleton removed, `IRosBus` injected) and `MtcSolutionPlayer`.
- `KitchenFR3.unity`: add `EruptCore.prefab` instance (root `ERUPT`: `PluginHost`, `SelectionService`, `SelectionHighlighter`, `ModeManager`, `EnvironmentRegistry` moved here, `TierUiRig` with `useTierUi=false` for now); add `MoveItPlugin.prefab` and `MtcPlugin.prefab` instances; `MTCManager` root removed (its components live in the MTC prefab); `Environment` root from Phase 1 folded into `ERUPT`.
- Tests: `PluginHostTests` (discovery, dependency order, cycle/missing refusal, mode fan-out, unregister on destroy), `VerbRegistryTests` (plugin verb appears for its kind, origin recorded, duplicate id refused), `PlanningPluginTests` with a fake planner (set-goal → plan → TrajectorySelectable exists → execute), `MoveItPlanningClientTests` against `FakeRosBus`.
- Gate: compile; tests; boundary test; Quest smoke unchanged (legacy UI still driving everything).

### Phase 3 — UI port and legacy retirement

- MoveIt: `PlannerSettingsTab` (uGUI via `UiBuilder`: pipeline/planner dropdown, attempts, allowed time, mirror toggle, plan button) built through `IUiHost.AddTab`; verbs `EndEffector/set-goal`, `EndEffector/plan`, `Trajectory/preview`, `Trajectory/execute` bound by `PlanningPlugin`. Ghost preview via `SpawnGhosts` becomes the `TrajectorySelectable` host so the Trajectory context menu appears on the ghost.
- MTC: `SolutionsTab` (stage tree, ranked solutions, cost breakdown, record start/stop) replaces `MTCDashboardPanel`; `Trajectory/preview|execute` on the selected solution's ghost; `Teach`-mode entry for `PickPlaceTaskRecorder`.
- Obstacle creation: Build-mode placement tool per `refactor/02-plan.md` P3 (lift `WristMenuController.AddPrimitiveShape` → `ObstacleFactory` already exists; add a tier 3 "Scene" tab with Cube/Sphere/Cylinder placement as the interim UI, flagged as a guideline deviation until the in-world tool lands).
- Flip `TierUiRig.useTierUi=true`, remove the `legacyMenu` toggle. Delete: `WristMenuController.cs`, `MoveItPlanningRequestMenuUI.cs`, `MTCDashboardPanel.cs`, `Assets/UI Toolkit/` (all `.uxml`, `.uss`, `WristUISettings.asset`, theme), `Assets/Prefabs/MoveItPlanningRequestMenu.prefab`, `XR UI Toolkit Manager` scene object, the migration tools under `core/Editor/Migration`, `Quest3RobotInteractionController` legacy selection path.
- `XR Origin (XR Rig).prefab` (now used by the two kept scenes only): remove `WristUI Document` child and `WristMenuController`; keep XRI interactors. `KitchenFR3.unity`: remove `MoveItPlanningRequestMenu` and `MTCMenu` instances; `TierUiRig` anchors → tier 1 on the left controller transform, tier 3 on a scene-local `Tier3Anchor`.
- Tests: `PlannerSettingsTabTests`, `SolutionsTabTests` (PlayMode, build the canvas, assert element counts ≤ 7 with a selection + panel open per Part 8), `ContextualMenu` end-to-end with a `TrajectorySelectable`.
- Gate: compile; tests; boundary test; **Quest UX review** of the five core flows plus MTC (behaviour intentionally changes; ROS traffic per flow should still match the Phase 0 capture, use `refactor/ros-traffic-diff.sh`); Part 8 design + code checklists walked in the phase report.

### Phase 4 — Generator and templates

- `Erupt.Core.Editor`: `[MenuItem("ERUPT/Plugins/Create Plugin...")]` → `PluginGeneratorWindow : EditorWindow` (name, package id, namespace, template family: Blank / Planning / Demonstration, output `Packages/com.erupt.plugin.<name>`, "Add to open scene" toggle).
- `PluginTemplate` (plain C# descriptor, one per family) listing template files under `Editor/Templates/<family>/` (`*.txt` with `{{PluginName}}`, `{{PackageId}}`, `{{Namespace}}`, `{{Family}}` tokens): `package.json`, `Runtime/{{PluginName}}.asmdef`, `Runtime/{{PluginName}}Plugin.cs` (derives from the family base with `TODO` hooks and comments citing the guideline part), `Runtime/Messages/README.md` + `{{PluginName}}.Messages.asmdef`, `Tests/{{PluginName}}.Tests.asmdef` + `{{PluginName}}SmokeTests.cs` (asserts the plugin registers against a `FakeRosBus` context), `README.md` with numbered setup steps (generate messages, drop `{{PluginName}}Plugin.prefab` into the scene next to `ERUPT`, assign robot root, run tests), `MESSAGES.md`.
- Prefab creation via `PrefabUtility.SaveAsPrefabAsset` on a temp GameObject carrying the generated behaviour (after `AssetDatabase.Refresh` + compilation; use `[InitializeOnLoad]` + `SessionState` to finish the prefab step post-recompile, the same two-step pattern `OpenCVForUnityDefine.cs` uses for defines). "Add to open scene" instantiates the prefab as a scene object (never edits a prefab asset) and assigns references with `SerializedObject`.
- The MoveIt and MTC packages are re-checked against the Planning template so the template is provably the shape of a real plugin (a test regenerates "Blank" and "Planning" into a temp folder and asserts the file set).
- Docs: `Documentation~/plugin-authoring.md` (contract, lifecycle, where a feature goes per Guidelines Part 4, verb/tab/widget rules, messages workflow, testing with `FakeRosBus`), `Documentation~/architecture.md` (assembly graph).
- Gate: compile; tests (generator file-set test); manual: generate a Planning plugin named `Demo`, project compiles, prefab exists, smoke test passes, delete it.

### Phase 5 — RADER absorb, LfD template proven, rader plugin

- Absorb into core: `CollisionHaptics` → `Erupt.Interaction.Backends.OpenXR` (haptics are a capability), with an `Erupt.*` namespace. Nothing else from RADER moves into core.
- Drop RADER's transform-based kinematics layer outright: `IKSolver`, `CCDIK`, `CCDIKJoint`, `UR5eAnalyticalIK` + `Runtime/Plugins/*` native libs, `SetupIK`, `RobotManager`, `ProcessUrdf`, `TargetSphere`. The project moved to URDF-Importer articulation bodies; `DirectArticulationIKController` (`IRobotModel`) already does what these scripts did by hand, so no `IIkSolver` abstraction is added. Decision recorded 2026-09-11.
- `com.erupt.plugin.rader`: `RaderPlugin : DemonstrationPlugin`; demonstration record/replay/publish ported from `SetupUI` onto `IRosBus` (`/{ns}/joint_trajectory`, `/{ns}/virtual_joint_state`, `/{ns}/interaction`, `/record_start`); `FERL`, `InfoLog`, `PointCloudPublisher` moved as-is onto `IRosBus`; `HandMirror`, `Robotiq2fGripperMirror` kept in the plugin, re-pointed from `RobotManager` (`SetTargetEEPose`, `SetGripperByJointName`, `GetJointAngles`) onto `IRobotModel` (`TrySolveToTarget`, `ApplyJointState`, `GetJointStatePositions`). ArUco/AR scripts, `SetupUI` menus, ur5e prefabs, `RosMessageTypes/Hri` dropped along with the kinematics layer above (all recoverable from the tag and from parasollab/RADER).
- Remove `Packages/RADER` from `.gitmodules`; remove `CollisionHaptics` reference on the rig instance and re-add the core version.
- Gate: compile; `DemonstrationPluginTests` with a fake recorder + `RaderPluginTests` against `FakeRosBus`; boundary test; Quest smoke: Teach mode records a demonstration and publishes.

### Phase 6 — Rebrand, docs, release

- `ProjectSettings.asset` `productName: ERUPT`; root `README.md` rewritten (name expansion, architecture picture, quick start, ROS commands, plugin list, how to create a plugin); package `displayName`s and versions (`0.1.0` → `1.0.0-preview.1`); `CHANGELOG.md`, `LICENSE` in each package; `refactor/` index updated; move `ERUPT_Design_Guidelines.md` to `Documentation~/design-guidelines.md`; delete the stray prompt files at the repo root (`claude_code_refactor_prompt.md`, `two_handed_ik_agent_prompt.md`, `visionos_capability_probe_prompt.md`, `mtc_connector.md`, `mtc_implementation.md`) or fold them into `docs/`.
- Optional CI: `.github/workflows/tests.yml` using the batchmode commands below (needs a Unity licence secret; documented, not required).

---

## Verification

Commands (run from the repo root; Unity must not be open on the project):

```bash
/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath . -logFile - | grep -E "error CS|Assembly.*could not be" ; echo "exit=$?"
```

```bash
/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults /tmp/erupt-playmode.xml -logFile - ; grep -o 'result="[A-Za-z]*"' /tmp/erupt-playmode.xml | sort | uniq -c
```

```bash
grep -rlE "RosMessageTypes\.(Moveit|MoveitTaskConstructorMsgs|StudyInterfaces)" Packages/com.erupt.core ; grep -rl "Assembly-CSharp" Assets --include=*.asmdef ; find Assets -name "*.cs" -not -path "Assets/Erupt.App/*" -not -path "Assets/Samples/*" -not -path "Assets/OpenCVForUnity/*" -not -path "Assets/_Heathen*" -not -path "Assets/LudicWorlds/*"
```

Per phase: the three commands above, plus the `AssemblyBoundaryTests` EditMode test from Phase 1, plus the Quest smoke checklist (create obstacle → set goal → plan → preview → execute; MTC record → preview → execute; undo/redo; mode cycle). Phases 0-2 must show unchanged behaviour; Phase 3 is a UX review with the ROS traffic diff (`refactor/ros-traffic-diff.sh record` before Phase 0, `compare` after each phase).

## Risks and mitigations

| Risk                                                                                               | Mitigation                                                                                                                                                                                |
| -------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GUID loss on move breaks scene/prefab script references                                            | `git mv` file + `.meta` together; never recreate an asmdef, move it; after each move open `KitchenFR3` in batchmode and grep the log for "missing script" / "The referenced script" |
| Two fr3 robots in`KitchenFR3`                                                                    | Phase 0 inspect command prints which robot`DirectArticulationIKController.robotRoot`, `SpawnGhosts.realRobot`, `MTCTrajectoryPlayer` point at; delete the other and record it       |
| Missing script on`Ground`, duplicate listener, duplicate `ResetKitchenScene`                   | Fixed in Phase 0 by the clean command; generator and host code null-check`MonoScript` and never assume component uniqueness                                                             |
| Dirty`URDF-Importer` / `ROS-TCP-Connector` submodules                                          | Blocking user action in Phase 0; the plan does not push to external repos                                                                                                                 |
| `SelectionManager` vs `SelectionService` duality; robot selection outside `SelectionService` | Merged in Phase 2 before any verb depends on`RobotLink` / `EndEffector` kinds                                                                                                         |
| Environment inversion changes ADD/MOVE/REMOVE timing                                               | `CollisionObjectFlowTests` (moved to the MoveIt plugin) assert the same op sequence via `FakeRosBus`; ROS traffic diff on device                                                      |
| `MoveItPlanningRequestMenuUI` (776 lines) split                                                  | Transport first (Phase 2, tested against`FakeRosBus`), UI second (Phase 3); the UXML panel keeps working in between                                                                     |
| PolySpatial port assumptions (uGUI only, no masking)                                               | All new UI goes through`UiBuilder`; tier 3 tabs avoid scroll masks (paginate the solution list)                                                                                         |
| Tests need the Editor lock free                                                                    | Batchmode commands above; run with the Editor closed                                                                                                                                      |
| RADER absorb loses upstream history                                                                | Tag + the parasollab/RADER repo remain;`Documentation~/architecture.md` records provenance                                                                                              |

## Existing code to reuse

- `Assets/Interaction/Core/InteractionRouter.cs:55-83` scene-scan + `Register` (PluginHost discovery)
- `Assets/UI/Core/{UiBuilder, TabbedPanelView, UiTierRegistry, ContextualMenuModel}.cs` (tier UI building blocks; `AddTab` for plugin tabs)
- `Assets/UI/Core/VerbTable.cs` (seed rows for `VerbRegistry`)
- `Assets/Scripts/Obstacles/{ObstacleFactory, ObstacleCommands, ObstacleSnapshot, ObstacleSnapping}.cs` (undoable obstacle mutations)
- `Assets/Scripts/Ros/{IRosBus, RosBus, LiveRosBus}.cs` and `Assets/Scripts/Tests/FakeRosBus.cs` (transport seam and fake)
- `Assets/Scripts/DirectArticulationIKController.cs` public API (becomes `IRobotModel`)
- `Assets/Scripts/TrajectoryReplay.cs` (becomes `JointTrajectoryPlayer`), `SpawnGhosts.cs` (trajectory ghost host)
- `Assets/Interaction/Editor/InteractionMigration.cs` (`SerializedObject` wiring idiom, `Find<T>` over roots, prefab-instance discipline), `InteractionMigrationVerify.cs` (verification report style)
- `Assets/Editor/OpenCVForUnityDefine.cs` (two-step editor action across recompiles)
- `Packages/RADER/package.json` (package manifest shape), `refactor/ros-traffic-diff.sh` (device-level behaviour oracle)
