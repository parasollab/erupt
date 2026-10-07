# Refactor backlog

Guideline violations and code hazards found out of scope for the current phase.
Per `claude_code_refactor_prompt.md`: log here, do not fix in flight.

Status: `open` · `scheduled(phase)` · `done` · `wontfix`

---

## From Phase 0

### B1 — Three writers to `objectsById` — `open`
`CollisionObjectsListenerSimple.cs:82,90,97,101` (authoritative, from ROS),
`SceneAnchorCollisionBridge.cs:298,315`, and `WristMenuController.cs:592,693`
(optimistic local insert before ROS confirms) all mutate the same map.
Not a parallel scene representation, so Part 8 CR-6 still passes — but a local insert
that ROS never confirms leaves a permanent ghost entry. Guidelines Part 1 P1.
*Suggested fix:* route creation through a single `PlanningSceneWriter` that inserts only
on ROS echo, with a pending-set for optimistic display.

### B2 — `SelectionManager` UI-hit detection matches on GameObject names — `open`
`SelectionManager.cs:97-104` returns "this is UI" for any transform whose name contains
`"wrist"`, `"ui"`, `"menu"`, `"panel"`, or `"button"` — case-insensitive, anywhere in the
name, walking the whole parent chain. An obstacle named `Menu Cube` is unselectable.
*Suggested fix:* a layer or an `IUiSurface` marker component. Naturally addressed when
target resolution moves into the Router (Phase 1) — reassess then.

### B3 — `GrabMoveDiagnostics.cs` is dead code — `open`
123 lines, reads `rightHandMoveAction` / `rightSnapTurnAction` via `ReadValue<Vector2>()`
at `:97-98`. Referenced by **no scene and no prefab**. It counts against Part 8 CR-2 while
doing nothing. *Suggested fix:* delete. Trivial, but out of scope for a phase whose gate
is "behavior unchanged."

### B4 — `MoveItPlanningRequestMenuUI` mixes three concerns in 775 lines — `open`
ROS transport (service registration, request construction, response handling), planner
configuration state, and UI Toolkit wiring in one `MonoBehaviour`. Also holds a hardcoded
`jointNames` array and `jointLimits` table (`:97-102`) that duplicate what
`DirectArticulationIKController` reads from the `ArticulationBody` drives.
*Suggested fix:* split transport into a `MoveItPlanningClient`; the UI half becomes the
tier 3 Planner Settings panel in Phase 2. Deleting the duplicated joint tables is the
higher-value part.

### B5 — `SelectionManager` subscribes with an unremovable lambda — `open`
`SelectionManager.cs:44`: `selectAction.action.performed += ctx => TrySelect();` in
`Start()`, with no matching `-=` in `OnDisable`/`OnDestroy`. Leaks across scene loads;
`KitchenLoadingScene*` scenes make that a real path. Contrast `WristMenuController`, which
does unsubscribe correctly at `:837-853`.

### B6 — `#if OPENCV_FOR_UNITY` in `MarkerRobotPlacement.cs:13,19,80` — `wontfix`
Optional-package presence guard, not a platform check. Part 8 CR-1 is about platform
forks. Recorded so a future CR-1 audit does not re-flag it. See survey §7 C3.

### B7 — `Quest3ControllerRayInteractor` deletion is deferred — `scheduled(2)`
Phase 1 introduces the Router but cannot delete this script, because `Kitchen.unity` and
`Task1_Kitchen1-4.unity` still instance it directly and only `KitchenFR3` migrates to the
forked rig. It is marked `[Obsolete]` in Phase 1 and deleted in Phase 2 once those scenes
migrate. Same for `Quest3RobotInteractionController` in `Prefabs/Robot IK Manager.prefab`.

### B8 — Two overlapping study scene sets — `open`
`Assets/Scenes/Task1_Kitchen1-4.unity` and `Assets/Scenes/Study Scenes/Kitchen 1-5` +
`Kitchen Low/Medium/High Clutter.unity`, plus
`Kitchen High Clutter (dupe of Kitchen 4).unity` which announces its own duplication.
Which set is the live study protocol is unclear, and it determines what the Phase 1
freeze actually protects. **Needs an answer from the maintainer**, not a code fix.

### B9 — `Assets/_Recovery/` contains three stale scene copies — `open`
`0.unity`, `0 (1).unity`, `0 (2).unity` reference most refactored scripts and will show up
in every future "which scenes use X" grep, inflating apparent migration scope.
*Suggested fix:* confirm they are recoverable junk and delete, or move out of `Assets/`.

### B10 — Undo/redo does not exist — `scheduled(2)`
Part 2 names undo and redo as two of tier 1's four controls. There is no command history
anywhere in the codebase; obstacle edits write `transform` directly. Building tier 1
honestly means building an undo stack first. Sized here because it is the largest hidden
cost in Phase 2. See survey §7 C6.

### B11 — Competence-event store does not exist — `scheduled(3)`
Part 7 gates disclosure on "recorded competence events." `PickPlaceTaskRecorder` publishes
one task to `/pick_place_task`; it is not a per-user ledger and does not persist across
sessions. Phase 3 needs a persistence decision. See survey §7 C7.

### B12 — Object creation has no home in the Part 2 verb table — `scheduled(2)`
Every tier 2 verb is scoped to a selection; "Add Cube" has no selection. Proposed
resolution is a Build-mode in-world placement tool (Part 4 step 2), but this is a visible
UX change and needs review before Phase 2 implements it. See survey §7 C5.

### B13 — Plugin UI has UXML but no controller — `done` (partially; see below)
`XRDashboard.uxml`, `AddPlugin.uxml`, `AddPluginCard.uxml`, `PluginCard.uxml`, and
`PluginCards.uss` exist under `Assets/UI Toolkit/`, plus a stray duplicate
`Assets/AddPlugin.uxml`. No script in `Assets/Scripts` queries their element names.
Maintainer confirmed the plugin UI is no longer used. Removed:
`AddPlugin.uxml`, `AddPluginCard.uxml`, `PluginCard.uxml`, `PluginCards.uss`, and the
stray duplicate `Assets/AddPlugin.uxml`.

**`XRDashboard.uxml` was NOT removed.** It is the `sourceAsset` of a live `UIDocument`
component in `Assets/Scenes/AR.unity`, so deleting it would leave a broken reference in
that scene. It has no controller script in `Assets/Scripts`, which means it renders but
nothing drives it — worth deciding separately whether the AR scene still needs it. Tracked
as **B19**.

---

## From Phase 1 planning

### B14 — Dual code paths from the KitchenFR3-only scope — `scheduled(2)`
The additive-only rule (see `01-plan.md`) adds opt-in flags to `SelectionManager`,
`WristMenuController`, `Quest3RobotInteractionController`, and
`DirectArticulationIKController` so unmigrated scenes keep today's behavior. Five forks
across four scripts. They are removable only when `Kitchen` and `Task1_Kitchen1-4` migrate
to the router. Until then Part 8 CR-2 and CR-4 pass for KitchenFR3, not for the repo.

### B8 — superseded — `wontfix`
Which study scene set is live no longer blocks Phase 1: the maintainer scoped all changes
to `KitchenFR3.unity`, so every study scene is out of scope regardless. Revisit before
Phase 2 migration.

### B15 — Selection is scoped to the right hand to preserve behavior — `scheduled(2)`
`SelectionManager.selectionSourceId` is set to `"right"` in KitchenFR3 because the
pre-refactor binding was `XRI Right Interaction/SelectObject`. Guidelines Part 3's
busy-hand rule argues both hands should be able to select — anything needed mid-drag must
be reachable by the hand that is not holding the robot. Clearing the field enables that;
it is a deliberate behavior change and so belongs in Phase 2, not in a phase gated on
"unchanged".

### B2 — partially retired — `scheduled(2)`
Name-substring UI rejection is gone on the KitchenFR3 path: the router rejects UI by layer
mask (`uiLayers = 0x20`). `SelectionManager.IsHittingWristUI` still runs for the five
scenes on the legacy path. Retires fully with B14.

### B5 — retired — `done`
The un-removable lambda in `SelectionManager.Start` is now a named handler with a matching
`OnDestroy` unsubscribe, on both the router and legacy paths.

### B16 — No ROS-stubbed play-mode harness — `open`
Phase 1 verification item 3 could not run: `KitchenFR3` opens a ROS connection on load, so
a headless PlayMode test reports connection failures rather than flow success. A stub or
seam around `ROSConnection.GetOrCreateInstance()` would make the five core flows testable
in CI without a headset or a robot. Worth doing before Phase 2 widens the blast radius.

### B17 — Static scene verification missed a runtime wiring failure — `open`
`ERUPT/Refactor/Verify KitchenFR3` reported 9/9 PASS on a scene where **no input reached
any feature**. It checked that components existed and fields were assigned; it could not
see that `InteractionRouter.Awake` used `GetComponentsInChildren`, which found none of the
backends because they sit under `Camera Offset/<hand>/Robot Ray` rather than under the
router. The editor-time `Register()` calls the migration made populated a non-serialized
list and a non-serialized event subscription, so nothing survived into play mode.

Fixed by scanning the scene in `Awake` and logging an error when zero sources are found,
plus two PlayMode regression tests in `InteractionRouterWiringTests`.

**The lesson is the general one:** any check that runs only in the editor cannot establish
that a scene works at runtime. Phase 2 must not rely on `Verify` alone — B16's ROS-stubbed
PlayMode harness is the thing that would have caught this in CI, and it should be built
before Phase 2 widens the blast radius.

### B18 — UI Toolkit does not survive the PolySpatial port — `done` (resolved by research)
All three ERUPT panels are UI Toolkit (`UIDocument` + UXML, 8 documents):
`WristMenuController`, `MTCDashboardPanel`, `MoveItPlanningRequestMenuUI`. PolySpatial
renders through RealityKit, where the supported UI path is Canvas-based uGUI with
TextMeshPro; Guidelines Part 6's "Text rendering is TextMeshPro only" corroborates.

**Confirmed** against PolySpatial docs 1.3 and 2.2: the UI support table lists TextMesh,
Canvas Renderer, Sprite Renderer, TextMesh Pro (SDF only, no custom shaders), Masking and
Platform Text. UI Toolkit / UIDocument / UXML / PanelSettings are absent, and the page
states "Unity UI works in world space, but screen space UI ... do not currently work".

Phase 2 therefore builds all tier UI in world-space uGUI + TextMeshPro. The three existing
UI Toolkit panels are migration work, logged here rather than done:

- `WristMenuController` + `WristMenu.uxml` — superseded by tiers 1/2 in KitchenFR3, but
  still live in 15 other scenes
- `MoveItPlanningRequestMenuUI` + `MoveItPlanningRequest.uxml` — rebuilt as the tier 3
  planner panel
- `MTCDashboardPanel` + `MTCDashboard.uxml` — split into a tier 3 library and tier 2
  trajectory verbs
- `XRDashboard.uxml`, `AddPlugin*.uxml`, `PluginCard.uxml` — see B13, may be dead already

### B14 — will not retire in Phase 2 — `open`
The maintainer scoped Phase 2 to KitchenFR3 as well, so `Kitchen` and `Task1_Kitchen1-4`
stay on the legacy input path. `Quest3ControllerRayInteractor` is not deleted, the Phase 1
opt-in flags stay, and Part 8 CR-2/CR-4 keep passing per-scene rather than repo-wide.
Every change to selection or input must keep the legacy path working. Retires only when
those five scenes migrate. B7 and B3 are blocked behind this for the same reason.

### B19 — XRDashboard.uxml renders in AR.unity with no controller — `open`
`Assets/UI Toolkit/XRDashboard.uxml` is bound to a `UIDocument` in
`Assets/Scenes/AR.unity` but no script in `Assets/Scripts` queries its element names, so
it displays without anything driving it. Kept during the B13 cleanup precisely because the
scene reference is live. Decide whether the AR scene still needs it; if not, remove the
`UIDocument` component and the asset together. Out of scope while work is KitchenFR3-only.
