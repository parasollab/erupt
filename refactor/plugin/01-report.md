# Plugin refactor — Phase 1 report (package extraction + environment cut)

Implements `01-plan.md`. Branch `design_refactor`, on top of Phase 0 (`d6f94fb`).

**Scope honored:** `Assets/Scenes/KitchenFR3.unity` is the only scene modified (by the
wiring command). No prefab asset modified. Every script moved with its `.meta`, so scene,
prefab and asmdef references survive by GUID (477 renames, 0 GUID changes).

---

## Deviation from the approved plan: migration tools live in the app, not in core

The plan placed `Assets/Interaction/Editor/**` at `com.erupt.core/Editor/Migration` as
`Erupt.Core.Editor`. Those tools (`InteractionMigration`, `KitchenSceneCleanup`, the new
`EnvironmentWiring`) reference `WristMenuController`, `CollisionObjectsListenerSimple`,
`MTCTrajectoryPlayer` and `ResetKitchenScene` by design — they wire the reference scene.
Inside core they would fail the `AssemblyBoundaryTests` the same plan asks for. They now
live at `Assets/Erupt.App/Editor/Migration` as `Erupt.App.Editor` (asmdef GUID kept from
`Erupt.Interaction.Editor`). Phase 3 deletes them; Phase 4 creates a clean
`Erupt.Core.Editor` for the generator.

Two smaller ones:

- `ObstacleVerbBindings` (ex `EruptVerbBindings`) dropped its unused `ikController` field so
  `Erupt.Environment` does not need `Erupt.Robot`. Nothing read it.
- `package.json` for core does **not** declare `ros-tcp-connector` / `urdf-importer`: both are
  `file:` packages from the parasollab forks, and a version dependency would make UPM look
  for them in the registry. README documents the requirement.

## What was built

### Packages and assemblies

| Package | Assemblies |
|---|---|
| `Packages/com.erupt.core` | `Erupt.Interaction.Core` (+ 3 backends, moved), `Erupt.Ui` (moved, + `Billboard`), `Erupt.Ros` (new), `Erupt.Robot` (new), `Erupt.Environment` (new); tests: `Erupt.Interaction.Tests`, `Erupt.Ui.Tests` (moved), `Erupt.TestSupport` (ex `Erupt.Ros.Tests` GUID; `FakeRosBus`, `TestInteractionSource`), `Erupt.Environment.Tests` (new), `Erupt.Boundary.Tests` (new, EditMode) |
| `Packages/com.erupt.plugin.moveit` | `Erupt.Plugins.MoveIt`, `Erupt.Plugins.MoveIt.Messages` (100 `moveit_msgs` files), `Erupt.Plugins.MoveIt.Tests` |
| `Packages/com.erupt.plugin.mtc` | `Erupt.Plugins.Mtc`, `Erupt.Plugins.Mtc.Messages` (16 MTC + 3 study), `Erupt.Plugins.Mtc.Tests` |
| `Assets/Erupt.App` | `Erupt.App` (7 scene scripts), `Erupt.App.Editor` (migration tools) |

`Assets/xrviz.asmdef` deleted. `Assets/Scripts`, `Assets/Interaction`, `Assets/RosMessages` no
longer exist. Every new runtime assembly inherits the platform list `xrviz` had (no VisionOS),
so build content is unchanged. `TransluscentOverride.cs` → `TranslucentOverride.cs`: the class
was already spelled `TranslucentOverride`, so the file could never attach as a component.

`.gitignore`'s `*.app` rule was matching the `Assets/Erupt.App` directory (case-insensitive git
on macOS); `!/Assets/Erupt.App/` re-includes it.

### Environment cut (`Erupt.Environment`, single writer)

| Type | Role |
|---|---|
| `EnvironmentRegistry` | id → `EnvironmentObject`, `WorldOrigin`, events `Added` / `Removed(origin)` / `Attached` / `Detached`; `Register`, `Unregister(id, RemovalOrigin)`, `TryGet`, `Attach`, `Detach`, `Adopt` |
| `EnvironmentObject` | `Id`, `Owner` (Unity / Remote), `Primitive?`, `IsMesh`, `AttachedTo` |
| `IEnvironmentSync` | marker + `SyncId` / `Registry` for planner syncs |

Rewired:

- `ObstacleFactory.Create(snapshot, registry)` adds `EnvironmentObject` and registers; no publisher.
  `Destroy(obstacle, registry, publishRemoval)` unregisters with `Local` / `Remote` origin.
- `ObstacleCommands`, `ObstacleSnapshot` (id from `EnvironmentObject`; `WorldOrigin` field
  removed, the registry owns the frame), `ObstacleVerbBindings` (primitive from
  `EnvironmentObject`, mesh-name sniffing kept as fallback for wrist-menu shapes).
- `SelectionManager` recognises the wrist menu by component **name** (`GetComponent("WristMenuController")`)
  until Phase 3 deletes it — identical behaviour, no app reference.
- MoveIt plugin: new `MoveItPlanningSceneSync : IEnvironmentSync` on `Added(Owner==Unity)` adds a
  `CollisionObjectPublisher` (`worldOrigin` = registry origin = `origin`, same object the scene
  used everywhere) and registers the id with the listener; on `Removed(Remote)` sets
  `suppressRemoveOnDestroy`. It skips ids the listener already knows as Unity-owned, so
  scene-authored publishers and the MRUK anchor bridge (which publishes itself) get no second
  publisher. `CollisionObjectsListenerSimple` adopts remote spawns (`Owner=Remote`) and
  Unity-owned registrations into the registry and unregisters with `Remote` origin on ROS
  REMOVE; `objectsById` kept as its own index. `AttachedCollisionObjectListener` calls
  `registry.Attach/Detach` after reparenting.
- MTC plugin: `MTCTrajectoryPlayer` resolves `scene_diff` attachments through `registry.TryGet`.
- App: `WristMenuController.AddPrimitiveShape` adopts its shape into the registry (with the
  primitive) before the listener registration.

### Tests

- `core/Tests/Environment/ObstacleCommandTests` (6): register/owner/primitive, undo → `Local`
  removal, delete/undo same id + membership, pose/scale, transform round-trip, redo id reuse.
- `moveit/Tests/ObstacleCommandTests` (4): sync attaches publisher, create/undo → ADD then REMOVE,
  delete/undo re-ADDs same id, remote removal echoes nothing. Setup = registry + listener + sync.
- `core/Tests/Boundary/AssemblyBoundaryTests` (3, EditMode): no `Erupt.*` core assembly references
  `Erupt.Plugins.*` / `Erupt.App`; no core source names plugin message namespaces; no stray `.cs`
  under `Assets` outside `Erupt.App` and vendor folders.
- `CollisionObjectFlowTests`, `MTCExecutionTests`, `PickPlaceActionTests`: moved, unchanged.

### Scene wiring

`ERUPT/Refactor/Wire Environment (Phase 1)` (idempotent): root `Environment` + `EnvironmentRegistry`
(`worldOrigin` → `origin`); `MoveItPlanningSceneSync` next to the listener on
`CollisionObjectListener`; `registry` assigned on the listener, `AttachedCollisionObjectListener`,
`MTCTrajectoryPlayer`, `ObstacleVerbBindings`, `WristMenuController`.
`ERUPT/Refactor/Verify Environment Wiring (Phase 1)` checks it.

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS — one `CS0246` on first import (`EnvironmentWiring` lacked `using Erupt.UiBindings`), fixed; clean after |
| `Wire Environment (Phase 1)` + verifier | PASS — 8/8; `Environment` root created, `worldOrigin` → `origin`, sync on `CollisionObjectListener`, registry assigned on listener / attach listener / `MTCTrajectoryPlayer` / wrist menu (scene override on the rig instance, prefab untouched); `ObstacleVerbBindings` not in scene yet (tier UI arrives in Phase 3) |
| PlayMode tests in their new assemblies | PASS — all green in the Test Runner |
| EditMode `AssemblyBoundaryTests` | PASS — 3/3 (no core→plugin/app edge, no plugin message namespace in core, no stray script under Assets) |
| `grep RosMessageTypes.(Moveit|MTC|Study)` in core = 0; no `.cs` under `Assets` outside `Erupt.App`/vendor | PASS (static) |
| Quest smoke unchanged | SKIPPED — maintainer waived, as in Phase 0 |

## Next

Phase 2 (`refactor_plan.md` § Phase 2): plugin contract, `PluginHost`, `IRobotModel`, selection merge,
`MoveItPlugin` / `MtcPlugin`, `EruptCore.prefab`.
