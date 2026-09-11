# Plugin refactor — Phase 2 report (plugin contract, host, robot interface, selection merge)

Implements `02-plan.md`. Branch `design_refactor`, on top of Phase 1 (`9baa7ca`).

**Scope honored:** `KitchenFR3.unity` is the only scene modified (by `Wire Plugins (Phase 2)`).
The XR rig prefab is untouched: fields on its instance (`WristMenuController.registry`,
`.selectionManager`) are scene overrides. Three new prefab assets are created by the wiring
command inside the packages.

---

## What was built

### `Erupt.Plugins` (core, `Runtime/Plugins/`)

| Type | Role |
|---|---|
| `IEruptPlugin` | `Id`, `DisplayName`, `DependsOn`, `OnRegister/OnUnregister(IEruptContext)`, `OnModeChanged(AppMode)` |
| `EruptPluginBehaviour` | MonoBehaviour base with no-op hooks and `Context`; discovered by the host |
| `IEruptContext` / `EruptContext` | `Ros`, `Robot` (`IRobotModel`), `Environment`, `Selection`, `Modes`, `Undo`, `Router`, `Ui` (`IUiHost`, null without a rig) |
| `PluginHost` | on the ERUPT root; `Start` finds every `EruptPluginBehaviour` (inactive included), orders by `DependsOn` (Kahn; `PluginDependencyException` on a missing id or a cycle refuses the whole set), builds the context from scene services (`FindFirstObjectByType` + `LogError`, the `InteractionRouter.Awake` discipline), fans `ModeManager.ModeChanged` out, unregisters in reverse on destroy. Owns the `UndoStack` and hands it to the rig. `Initialise(ctx)` for tests. |
| `PlanningPlugin` | template: `SetGoal`, `RequestPlan`, `Preview`, `StopPreview`, `Execute`; base `OnRegister` binds `set-goal`/`preview`/`execute`, registers `plan` on EndEffector (`VerbOrigin.Plugin`), adds tab `planner-{Id}`; `PublishResult` spawns a `TrajectorySelectable` per plan |
| `DemonstrationPlugin` | template: `OnSample` fed from `InteractionSampleBus` only while Teach + recording; `Start/StopDemonstration`, `Publish`; tab `demos-{Id}`, `correct` verb, Teach gate on mode change |
| `PlanResult`, `PlanPreferences`, `ExecutionStatus` (+ `ExecutionPhase`), `TrajectorySelectable` | data + the Trajectory selectable |

### `Erupt.Ui`

- `VerbRegistry` (instance, seeded from `VerbTable`): `Register(kind, id, label, pluginId)` → `VerbOrigin.Plugin` + `PluginId`; duplicate id in a row refused; `RemoveAllFrom(pluginId)`; `Changed`.
- `IUiHost` (+ `IWorldWidget`): `RegisterVerb`, `BindVerb`, `AddTab(id, label, build)`, `SummonTab`, `Register/UnregisterWidget`. Implemented by `TierUiRig`, which owns the registry and hands it to tier 2.
- `ContextualMenuModel(VerbRegistry)` reads the registry and rebuilds on `Changed`; parameterless constructor keeps the table for existing tests.
- `UndoStack` ownership: `PluginHost.Undo`; `TierUiRig.UseUndoStack` / `TierOneBar.UseUndoStack` before build (the rig configures the bar on an inactive object so Awake sees it). If the rig woke first, the context adopts the bar's stack — there is one stack either way.

### `Erupt.Robot`

- `IRobotModel` extracted (Root, EndEffector, JointNames, joint-state get/apply, `TrySolveToTarget`, `TryNudgeJoint`, `FindLinkTransform`, Begin/EndInteraction); `DirectArticulationIKController` implements it (`Root` added).
- `TrajectoryReplay` → `JointTrajectoryPlayer` (GUID kept; scene component survives), `SetRobot(IRobotModel)`.
- `Quest3RobotInteractionController` publishes typed selection: every controllable joint (and its handle sphere) carries a `SelectableMarker(RobotLink)`, the IK handle an `EndEffector` marker; `SelectJoint` → `SelectionService.Select`, the handle hit selects the end effector, and it listens to `SelectionChanged` so an obstacle selection drops the joint tint. It clears the service only when the current selection is its own.

### Selection merge

- `SelectionManager` → `SelectionHighlighter` (GUID kept): material swap for Obstacle/Manipulable driven by `SelectionService.SelectionChanged`; `Select(GameObject)` / `ClearSelection` / `DeleteSelectedObject` for legacy callers.
- New `SelectionRouterBinding` (Interaction.Core, on ERUPT): router `Select` → service; "Selectable"-tagged objects without a marker get an Obstacle marker; robot kinds are left to the robot binding, so subscription order cannot make the two fight. **The Phase 1 opt-in flags are gone**: no `interactionRouter` opt-in, no `selectionSourceId` right-hand filter, no legacy input-action path.
- `SelectableGrabController`, `ObstacleVerbBindings`, `WristMenuController`, `PickPlaceTaskRecorder` moved off `SelectionManager.Instance`; the migration editor tools no longer bind selection.

### Plugins

- MoveIt: `MoveItPlanningClient` (plain class, `IRosBus` + `IRobotModel` + `MoveItPlanningSettings`): connect, planner query, robot-state capture, request building (joint goal constraints, tolerance), `RequestPlan` → `PlanResult` in Unity joint names, `Execute` publishes ROS-named trajectory, mirroring, joint-name remap both ways. `MoveItPlugin : PlanningPlugin` wires it to the context and `JointTrajectoryPlayer`. `MoveItPlanningRequestMenuUI` is UI-only (776 → 255 lines) and drives the plugin; backlog B4 and the duplicated joint tables are retired.
- MTC: `MTCDataManager` → `MtcClient` (singleton removed, `Initialise(IRosBus)`; `RosBus.Instance` fallback keeps `MTCExecutionTests` valid), `MTCTrajectoryPlayer` → `MtcSolutionPlayer` (`SetRobot`), `MtcPlugin : PlanningPlugin` (`DependsOn = ["moveit"]`): every `/solution` becomes a `PlanResult` + Trajectory selectable; preview on the player; execute through the action client; `SetGoal` refuses with a reason (MTC plans from the recorded task). `MTCDashboardPanel` keeps its `dataManager` field (type renamed).

### Scene wiring (`ERUPT/Refactor/Wire Plugins (Phase 2)`)

Creates `Packages/com.erupt.core/Prefabs/EruptCore.prefab` (root `ERUPT`: `PluginHost`, `SelectionService`, `SelectionHighlighter`, `SelectionRouterBinding`, `ModeManager`, `EnvironmentRegistry`, `TierUiRig` with `useTierUi=false`), `…moveit/Prefabs/MoveItPlugin.prefab`, `…mtc/Prefabs/MtcPlugin.prefab` (`MtcPlugin` + `MtcClient` + `PickPlaceActionClient`) if missing; instances them; folds the Phase 1 `Environment` root, the `SelectionManager` root and `MTCManager` into them (registry `worldOrigin` → `origin`, highlight material carried over, `PickPlaceActionClient` values copied, every `registry` / `selectionManager` / `dataManager` / `pickPlaceAction` reference repointed); sets the MoveIt plugin to the scene's real values (`panda_arm`, `/panda_arm_controller/joint_trajectory`) and its player to the scene `JointTrajectoryPlayer`; points the planning panel at the plugin. `Verify Plugin Wiring (Phase 2)` checks all of it.

### Tests

- `core/Tests/Plugins` (`Erupt.Plugins.Tests`, PlayMode): `PluginHostTests` (dependency order, missing / cycle refusal registers nothing, later registration may satisfy an earlier one, duplicate id, mode fan-out, reverse unregister on destroy, scene discovery + unregister), `PlanningPluginTests` with `FakePlanner` + `FakeUiHost` (verbs/tab contributed, set-goal reaches the plugin, plan → Trajectory selectable + `PlanProduced`, preview/execute use the selected trajectory, unregister destroys selectables).
- `core/Tests/Ui/VerbRegistryTests` (seeded rows, plugin verb with origin/id, duplicate refused, None refused, `RemoveAllFrom`, live menu model refresh).
- `moveit/Tests/MoveItPlanningClientTests` against `FakeRosBus` + a fake `IRobotModel` (registrations, name remap, mirroring gate, planner query, request contents, success → Unity-named trajectory, failure → error, execute publishes ROS names).

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS — three first-import errors fixed (`Verb` edit had not applied; `Path` helper shadowed `System.IO.Path`; `DeleteRoot` iterated a stale roots array), clean after |
| `Wire Plugins (Phase 2)` + verifier | PASS — 21/21; prefabs created, ERUPT/MoveItPlugin/MtcPlugin instanced, 5 registry + 2 selection + 2 MTC references repointed, three legacy roots removed; the pick/place recorder keeps its unassigned action client (topic delivery) as before |
| PlayMode tests (existing + Plugins/VerbRegistry/MoveItClient suites) | PASS — all green in the Test Runner |
| EditMode `AssemblyBoundaryTests` | PASS — 3/3 with `Erupt.Plugins` in place |
| Quest smoke unchanged (legacy UI still drives everything) | SKIPPED — maintainer waived, as in Phases 0–1 |

## Next

Phase 3 (`refactor_plan.md` § Phase 3): UI port to tier 2 verbs / tier 3 tabs, `TierUiRig` on, legacy wrist menu and UXML panels retired, migration tools deleted.
