# Plugin refactor — Phase 3 report (UI port and legacy retirement)

Implements `03-plan.md`. Branch `design_refactor`, on top of Phase 2 (`81a73b0`).

**Scope:** `KitchenFR3.unity` and, for the first time in the refactor and as the plan directs,
`Assets/Prefabs/XR Origin (XR Rig).prefab` (the wrist UI nested instance is removed; XRI
interactors untouched). `EruptCore.prefab` and `MtcPlugin.prefab` gain components.
Behaviour changes on purpose: this phase is gated by a UX review, not behaviour preservation.

---

## What was built

### Tier 3 tabs (uGUI through `UiBuilder`, each ≤ 7 interactive elements)

| Tab | Owner | Elements |
|---|---|---|
| Planner settings (`planner-moveit`) | `PlannerSettingsTab` via `MoveItPlugin.BuildSettingsTab` | pipeline, planner (cycle buttons fed by `/query_planner_interface`), attempts, allowed time (cycle over presets), mirror toggle, plan, reset = 7 |
| MTC (`planner-mtc`) | `SolutionsTab` via `MtcPlugin.BuildSettingsTab` | record pick & place (Teach mode only), up to 5 ranked solution buttons, cancel execution = 7; stage tree and cost breakdown are read-only text |
| Scene (`scene`) | `ScenePlacementTab` on the ERUPT root | Add Cube / Sphere / Cylinder (Build mode only) = 3 |

`UiBuilder` gained `CreateCycle` (a dropdown costs two elements; a cycle button costs one),
`CreateToggle`, `CountInteractive` (what the Part 8 budget counts) and a `CanvasCreated` event.

### Tier 2 verbs on plans

`PlanningPlugin.PlaceHandle(result, position, follow)` gives a plan a small sphere in the world
carrying its `TrajectorySelectable`; `SelectionService.Resolve` walks up from the sphere's
collider, so a ray on it selects the plan and tier 2 shows `preview` / `execute` (bound by the
template since Phase 2). MoveIt places the handle at the goal ghost's end effector
(`SpawnGhosts` now exposes `StartGhost` / `GoalGhost`); MTC places only the selected
solution's handle at the real end effector and hides the rest. Set-goal on the end effector
spawns/updates the ghosts (start on first goal), so the legacy Set Start / Set Goal buttons
have no replacement panel — they were verbs all along.

### Teach-mode entry for the recorder

`PickPlaceTaskRecorder` moves from the rig's wrist UI to the `MtcPlugin` object; `MtcPlugin`
tracks Teach mode from `OnModeChanged` (`InTeachMode`, `TeachModeChanged`), stops a recording
when the mode leaves Teach, and the tab's record button is disabled outside Teach
(Guidelines Part 5: demonstration lives in Teach).

### XR input for uGUI

Nothing added XRI's `TrackedDeviceGraphicRaycaster` to the tier canvases, so controller rays
could never click them (the UI Toolkit panels had their own XR manager). `XrUiRaycastInstaller`
(OpenXR backend, on the ERUPT root) listens to `UiBuilder.CanvasCreated` and installs the
raycaster on every world canvas; the scene's `EventSystem` already runs `XRUIInputModule` and
the rig's interactors have UI interaction on.

### Found on the device (first UX pass)

- **Tier 3 had no summoner** — closed by default with nothing to open it. The controller Menu
  button's router `Activate` intent (which opened the wrist menu) now toggles tier 3
  (`TierUiRig.ToggleTierThree`).
- **Menu buttons did nothing.** The tier canvases had no collider, so a trigger press on a
  button also reached the router as a world select whose ray passed through the panel and
  cleared the selection. `UiBuilder.CreateWorldCanvas` now adds a thin trigger `BoxCollider`
  on the UI layer, which the router already consumes (`uiLayers`), and the ray visual stops
  at the panel. `XrUiRaycastInstaller` also sets `Canvas.worldCamera` explicitly and logs.
  After both fixes: tier 3 opens, the Scene tab creates, tier 2 buttons respond.

- **Shared verbs went to the last planner.** Each `PlanningPlugin` bound set-goal/plan/preview/execute
  on registration, so MTC (registered after MoveIt) owned them all and set-goal refused. The base
  now binds them once per UI host: preview/execute dispatch to the `TrajectorySelectable.Owner`,
  set-goal/plan to the first registered planner with `AcceptsGoals` (MTC says false).
- **New shapes all landed on one point** (2 m ahead), so a second shape sat inside the first and
  the ray always selected the first; duplicate's 20 cm offset on a 1 m cube did the same. The
  Scene tab now steps sideways to a clear spot (`Physics.CheckSphere`). Duplicate keeps the
  legacy 20 cm offset.

- **Duplicating a mesh made a cube.** `ObstacleVerbBindings` recovered a primitive from the mesh
  name and defaulted to Cube, so duplicating the mug built a 1 m cube on top of it (and an undone
  delete of a mug would have done the same); the cube then swallowed every ray, which read as
  "cannot deselect". Non-primitives are now cloned: `DuplicateMeshObstacleCommand` /
  `DeleteMeshObstacleCommand` (inactive hidden clone, restored under the original id), and the
  MoveIt sync re-keys a clone's copied publisher. Primitive detection is exact (built-in mesh names).

- **Duplicates stayed highlighted.** The copy was taken while the original wore the highlight
  material, so it inherited it as its real material. Duplicate now clears the selection before
  capturing, and the highlighter swaps `sharedMaterial` rather than instancing `material`.

### Retired

`WristMenuController.cs`, `MoveItPlanningRequestMenuUI.cs`, `MTCDashboardPanel.cs`,
`Assets/UI Toolkit/` (uxml, uss, `WristUISettings.asset`, theme), `MoveItPlanningRequestMenu.prefab`,
the `MoveItPlanningRequestMenu` / `MTCMenu` / `XR UI Toolkit Manager` / `PanelInputConfiguration`
scene objects, the `WristUIMenu` nested instance in the rig prefab, `TierUiRig.legacyMenu`,
and the Phase 0–2 migration tools (`InteractionMigration*`, `InteractionDeltaProbe`,
`KitchenSceneCleanup`, `EnvironmentWiring`, `PluginWiring`). `UiPortWiring` remains until
Phase 4 removes the folder. `useTierUi` stays as a debugging switch.

### Scene wiring (`ERUPT/Refactor/Port UI (Phase 3)`)

Recorder values copied to `MtcPlugin`; legacy UI roots deleted; `TierUiRig` on, tier 1
anchored to `Left Controller`, tier 3 to a new scene-local `Tier3Anchor` (first-guess
position; move it in the Editor); `ScenePlacementTab.shapeMaterial` ← the wrist menu's
`litMaterial`; `MoveItPlugin.ghosts` ← `SpawnGhosts`; then the rig prefab edit.
`Verify UI Port (Phase 3)` checks all of it.

### Tests

`PlannerSettingsTabTests` (budget 6–7, preferences follow the planner query, plan verb sends the
tab's choices), `SolutionsTabTests` (8 solutions → 5 buttons ≤ 7 elements ranked by cost, first
solution selected with a collider-bearing handle, record gated on Teach), `TrajectoryContextMenuTests`
(real `TierUiRig`: a plan's handle resolves to the selectable, tier 2 shows preview/execute and
they reach the plugin; `plan` is a plugin verb).

## Deviations and losses (for the UX review)

- **Obstacle creation is a tier 3 tab**, not the in-world placement tool Part 4 step 2 wants.
  Interim; flagged in `ScenePlacementTab`'s remarks.
- **The wrist menu's per-axis scale slider is gone.** "Resize" is a spatial control (Part 1 P3 →
  in-world widget, deferred). Two-handed grab scaling (`XRTwoHandedScaleTransformer`) remains.
  The `resize` verb stays visible and disabled until the widget exists.
- **Recording requires Teach mode** (one tier 1 press). Before, the wrist menu recorded from any mode.
- **Set start is its own verb**: `set-start` is a MoveIt plugin verb on the end effector beside `plan` (maintainer's call during the review; the first cut captured start implicitly on set-goal, which made the first plan zero-length unless the robot was moved after setting the goal). Reset on the planner tab clears both.
- Tier 1 (4) + the Obstacle row (7 verbs) already exceed the ~7 budget when an obstacle is
  selected; this predates Phase 3 and is left for the UX review.

## Part 8 checklists

Design: tier 1 has four controls ✔; features were added as verbs (`plan`, preview/execute on
trajectories) and tabs, no new menu ✔; each tab ≤ 7 elements ✔ (obstacle row noted above);
mode visible and one action away (tier 1) ✔; refusals carry a reason (`SetGoal` on MTC,
`ContextualMenuModel.Invoke`) ✔; nothing mid-manipulation needs the busy hand (tier 2 follows
selection; tier 1 on the left controller) — to confirm on device; voice: placeholder only ✗
(unchanged); core task without tier 3: create obstacle needs the Scene tab ✗ (the deviation
above).
Code: no platform directives outside backends ✔ (`XrUiRaycastInstaller` lives in the OpenXR
backend); no direct button/pinch reads ✔; refusals return reasons ✔; no parallel scene
representation ✔ (`EnvironmentRegistry` only); UI registered with a tier ✔ (`AddTab`,
`RegisterVerb`).

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS — first-import fixes (`Erupt.Environment` lacked a TextMeshPro reference; the MTC tab test lacked `using Erupt.Ros` and used double costs), clean after |
| `Port UI (Phase 3)` + verifier | PASS — 13/13; second run needed because the deleted panel prefab's instances had been renamed "… (Missing Prefab …)", now matched by prefix; wrist UI removed from the rig prefab, recorder on `MtcPlugin`, tier UI on, anchors set |
| PlayMode tests (all suites + 3 new) | PASS — all green in the Test Runner |
| EditMode `AssemblyBoundaryTests` | PASS — 3/3 |
| Quest UX review: five core flows + MTC | PARTIAL — offline flows pass on device after the fixes above (tier 3 summon, tier 1/2/3 clicks, Scene tab creation, selection/deselection, duplicate of primitives and meshes, delete, set-start/set-goal ghosts). Plan, preview, execute, MTC solutions and pick/place recording need a ROS host, which was not available; deferred to the first ROS session on this build |
| ROS traffic diff vs. Phase 0 baseline (`refactor/ros-traffic-diff.sh`) | DEFERRED — no ROS host during the review and no baseline bag exists; a baseline can still be recorded from a Phase 2 build (`81a73b0`) if wanted |

## Next

Phase 4 (`refactor_plan.md` § Phase 4): plugin generator and templates in `Erupt.Core.Editor`.
