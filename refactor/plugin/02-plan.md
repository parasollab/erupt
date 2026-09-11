# Plugin refactor — Phase 2 plan (plugin contract, host, robot interface, selection merge)

Copied verbatim from `refactor_plan.md` § Phase 2 (and the contract sketch) at the start of the phase.

### Phase 2 — Plugin contract, host, robot interface, selection merge

- `Erupt.Plugins` assembly: `IEruptPlugin`, `EruptPluginBehaviour`, `PluginHost`, `IEruptContext` + `EruptContext`, `PlanningPlugin`, `DemonstrationPlugin`, `PlanResult`, `PlanPreferences`, `ExecutionStatus`, `TrajectorySelectable`.
- `Erupt.Ui`: `VerbRegistry` (seeded from `VerbTable` rows), `IUiHost` implemented by `TierUiRig`, `IWorldWidget`; `ContextualMenuModel` takes the registry. `UndoStack` ownership moves from `TierOneBar` to `EruptContext` (bar receives it in `Initialise`).
- `Erupt.Robot`: `IRobotModel` extracted from `DirectArticulationIKController`; `RobotInteractionRouterBinding`, `JointTrajectoryPlayer`, `SpawnGhosts` consume the interface.
- Selection merge (retires the dual system and the Phase 1 opt-in flags): `SelectionManager` becomes `SelectionHighlighter` (material swap driven by `SelectionService.SelectionChanged`); `SelectableGrabController` listens to `SelectionService`; `SelectionManager.Instance` callers (`WristMenuController`, `MTCDashboardPanel`, `PickPlaceTaskRecorder`) updated. Robot-link selection in `Quest3RobotInteractionController` publishes `SelectionKind.RobotLink` / `EndEffector` through `SelectionService` (prerequisite for `set-goal` as a tier 2 verb in Phase 3).
- MoveIt plugin: `MoveItPlugin : PlanningPlugin` + `MoveItPlanningClient` (transport half of `MoveItPlanningRequestMenuUI`: `/plan_kinematic_path`, `/query_planner_interface`, `/joint_states`, trajectory publish; retires backlog B4 and the duplicated joint tables). `MoveItPlanningRequestMenuUI` shrinks to UI-only and drives the client (still UI Toolkit until Phase 3). MTC plugin: `MtcPlugin : PlanningPlugin` wrapping `MtcClient` (ex `MTCDataManager`, singleton removed, `IRosBus` injected) and `MtcSolutionPlayer`.
- `KitchenFR3.unity`: add `EruptCore.prefab` instance (root `ERUPT`: `PluginHost`, `SelectionService`, `SelectionHighlighter`, `ModeManager`, `EnvironmentRegistry` moved here, `TierUiRig` with `useTierUi=false` for now); add `MoveItPlugin.prefab` and `MtcPlugin.prefab` instances; `MTCManager` root removed (its components live in the MTC prefab); `Environment` root from Phase 1 folded into `ERUPT`.
- Tests: `PluginHostTests` (discovery, dependency order, cycle/missing refusal, mode fan-out, unregister on destroy), `VerbRegistryTests` (plugin verb appears for its kind, origin recorded, duplicate id refused), `PlanningPluginTests` with a fake planner (set-goal → plan → TrajectorySelectable exists → execute), `MoveItPlanningClientTests` against `FakeRosBus`.
- Gate: compile; tests; boundary test; Quest smoke unchanged (legacy UI still driving everything).


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

Robot cut: `IRobotModel` (Erupt.Robot) = the existing public surface of `DirectArticulationIKController` (`Root`, `EndEffector`, `JointNames`, `TryGetJointAngle`, `GetJointStatePositions`, `ApplyJointState`, `TrySolveToTarget`, `TryNudgeJoint`, `FindLinkTransform`, `BeginInteraction/EndInteraction`). `DirectArticulationIKController : MonoBehaviour, IRobotModel`. Phase 5 adds `IIkSolver` so RADER's `UR5eAnalyticalIK` becomes a pluggable solver.

Verbs: `VerbRegistry` (instance, Erupt.Ui) seeded from the Guidelines Part 2 rows (`VerbTable` becomes the seed data only). `ContextualMenuModel` takes a `VerbRegistry`. Plugin-registered verbs carry `VerbOrigin.Plugin` + plugin id so the Part 8 review ("added a verb, not a menu") stays auditable. `SelectionKind` stays closed.

---
