# Phase 2 — UI retiering

Plan for review. **Not started.** Depends on `00-survey.md` and `01-report.md`.
Satisfies Guidelines Part 2, Part 4, and Part 8 code-review item 7.

**Goal:** menus organized by what the user selected, not by which subsystem owns the code.

---

## Gate: Phase 1 is not fully verified

Grabbing and selection were confirmed working in the headset after the B17 fix, but the
Phase 1 gate also requires the ROS traffic diff and the manual five-flow checklist
(`01-report.md`, "Not run"). The two PlayMode regression tests added with the B17 fix are
also still **unrun** — the Unity editor has held the project lock since.

Per the refactor prompt's constraint — "do not begin a phase before the prior one builds
and runs" — Phase 2 should not start until those clear.

---

## 0 — PolySpatial changes what the tier UI is built out of

Decision 4 selected PolySpatial mixed reality, which makes Part 6 apply as written. Two
consequences land directly on Phase 2, before any tier is built.

### The current UI stack probably does not port

All three panels are **UI Toolkit** (`UIDocument` + UXML): `WristMenuController`,
`MTCDashboardPanel`, `MoveItPlanningRequestMenuUI`, across 8 `.uxml` documents. Only
`SelectionManager` touches uGUI, and only to raycast against it.

**Checked against Unity's PolySpatial documentation (versions 1.3 and 2.2).** The
"Supported Unity Features & Components" page enumerates the UI section as: TextMesh
(Supported), Canvas Renderer (Partially Supported), Sprite Renderer (Supported),
TextMesh Pro (Partially Supported — *SDF only, no custom shaders*), Masking (Supported,
with fidelity limits), Platform Text. **"UI Toolkit", "UIDocument", "UXML" and
"PanelSettings" do not appear anywhere on the page.** The explicit statement is:

> "Unity UI works in world space, but screen space UI and advanced visual features like
> masking, shadowing, etc do not currently work."

"Unity UI" there means uGUI. This is absence from an enumerated support table plus
explicit uGUI support, rather than a sentence saying UI Toolkit is unsupported — but for
planning purposes that is decisive enough, and it agrees with Part 6's "Text rendering is
TextMeshPro only".

**Consequence: Phase 2 builds tier 1/2/3 in world-space uGUI + TextMeshPro.** Constraints
that follow:

- **World space only.** No screen-space canvases. ERUPT's panels are already world-space,
  so this costs nothing.
- **TextMeshPro SDF, no custom shaders** on text.
- **No reliance on masking** for layout or scroll clipping, and note that masking does not
  affect collider hit-testing — relevant to tier 3's scrollable solution list.
- The three existing UI Toolkit panels (`WristMenuController`, `MTCDashboardPanel`,
  `MoveItPlanningRequestMenuUI`, 8 `.uxml` documents) become **migration work, logged not
  done**. Tier 3 consolidation in §2.4 rebuilds the planner and solution-browser UI in
  uGUI rather than porting the UXML.

Building the tier system in UI Toolkit would have wasted most of this phase. Logged as
**B18**, now resolved.

### Design implications to bake in now

- **Tier 3 is one tabbed panel, not several.** Part 6 makes tier 3 a single visionOS
  window with tabs. Designing it as N separate summoned panels on Quest would force a
  restructure at port time; designing it as one panel with tabs makes the
  one-open-at-a-time invariant trivial and ports directly.
- **Tier 1 must be compact enough to become an ornament** — a small bar, four controls,
  no growth room. Reinforces the hard cap rather than fighting it.
- **Tier 2 must be able to appear on gaze-hover**, not only on a deliberate ray-select
  (Part 6). So the selection service from P1 should expose *hover intent* separately from
  *selection*, even though Quest controllers will only ever drive the latter.
- **In-world widgets must be Shader Graph expressible.** The scale widget, scrub ribbon,
  and cost brush cannot rely on custom HLSL or immediate-mode drawing.

Adding the PolySpatial packages is a new third-party dependency. Choosing the target is
not the same as approving the install, so I will ask before adding anything to
`Packages/manifest.json`. Nothing in Phase 2 requires it to be installed — only designed for.

---

## Three prerequisites that do not exist yet

Phase 2 cannot be a menu-moving exercise. Each of these is a subsystem the guidelines
assume and the codebase lacks. They are the real cost of this phase.

### P1 — There is no object typing, and there are two selection systems

Part 2's tier 2 table keys verbs off six selection types. The codebase has **one**: the
`"Selectable"` tag, checked at `SelectionManager.cs:85,113`. Nothing distinguishes an
obstacle from a manipulable object.

Worse, robot selection is a **separate, parallel system**:

| System | State | Highlight | Selects |
|---|---|---|---|
| `SelectionManager` | `SelectedObject` | material swap | anything tagged `Selectable` |
| `Quest3RobotInteractionController` | `selectedJoint`, handle | per-renderer colour tint | `ArticulationBody` links, IK handle |

They do not know about each other. Both currently fire off the same router `Select` intent.
A contextual menu cannot be "driven by the selected object's type" until there is one
selection concept that reports a type.

**Work:** introduce `SelectionKind { Obstacle, Manipulable, RobotLink, EndEffector,
Trajectory, Waypoint }` and an `ISelectable` component carrying it. Merge the two systems
behind one `SelectionService` that reports `(GameObject, SelectionKind)`. `SelectionManager`
keeps its public surface — `MTCDashboardPanel` and `WristMenuController` depend on it.

Note that `Trajectory` and `Waypoint` are **not selectable at all** today; MTC solutions
are rows in a panel list. Making a trajectory a selectable world object is new work, not a
migration.

### P2 — There is no undo

Part 2 fixes tier 1's contents at four: mode indicator, undo, redo, voice. There is no
command history anywhere in the codebase — obstacle edits write `transform` directly
(`WristMenuController.cs:233-237`), deletes call `Destroy` (`SelectionManager.cs:172`).

**Decided: build it.** A command object per mutation — create, delete, move, scale, snap —
with the planning-scene publish inside the undone/redone action so MoveIt stays in sync.
That last part is the subtle bit: `CollisionObjectPublisher` publishes MOVE from its own
`Update` when it notices a transform change (`:83-97`), so an undo that only rewrites the
transform will produce a MOVE anyway, while an undone *delete* must re-ADD rather than
rely on destruction being reversed. Undo of a delete therefore has to recreate the object
with its original `objectId`, or MoveIt's scene diverges from Unity's.

This is the largest single item in the phase and it is invisible in the guidelines'
phrasing. Backlog B10.

Voice does not exist either, but that is explicitly Phase 3, so tier 1 ships with three
controls and a reserved fourth slot.

### P3 — Object creation has no home in the verb table

Every Part 2 tier 2 verb assumes a selection; "Add Cube" has none. Survey C5 / backlog B12.
**Decided:** creation becomes a Build-mode in-world placement tool (Part 4 step 2), not a
menu entry. The `Add Shape` submenu and its three buttons disappear from the migrated
scene. `AddPrimitiveShape` (`WristMenuController.cs:635`) is the behavior to lift — it
also registers the `CollisionObjectPublisher` and the `objectsById` entry, so the new tool
must preserve that sequence or obstacles stop reaching MoveIt.

---

## The work

### 2.1 Tier system

`UiTier { Persistent, Contextual, Summoned }`. Registration-time assignment enforced in
code, per Part 8 CR-7: a `TieredPanel` cannot be instantiated without declaring its tier.

`UiTierRegistry` owns the invariants:
- **Tier 1 cap of four, enforced not conventional.** Registering a fifth throws. Part 2
  is explicit that this is a hard cap, so it fails loudly rather than logging.
- **Tier 3 one-open-at-a-time.** Opening a second summoned panel closes the first.
- Tier 2 lifetime is bound to selection: shown on select, destroyed on deselect.

### 2.2 Tier 1 — persistent

New, wrist-anchored (Part 6, Quest). Three controls plus a reserved slot:
mode indicator · undo · redo · *(voice, Phase 3)*.

Depends on P2. The mode indicator is also reinforced by the robot's outline colour, per
Part 4.

### 2.3 Tier 2 — contextual

Driven by `SelectionKind` from P1. Migrating the Part 2 table:

| Selection | Verbs | Source today |
|---|---|---|
| Obstacle | resize, reshape, paint cost, lock, delete | wrist Edit/Delete/Duplicate/Snap; *cost paint and lock are new* |
| Manipulable | grasp here, set as target, properties | *grasp is Phase 3*; rest new |
| Robot link | set joint angle, lock joint | `JogSelectedJoint`; *lock is new* |
| End effector | set goal, open/close gripper, preview grasp | MoveIt `Set Goal`; *gripper is new* |
| Trajectory | scrub, correct, compare, execute | MTC Preview/Execute; *scrub, correct, compare are new* |
| Waypoint | move, delete, pin timing | *entirely new* |

Roughly a third of the table migrates; the rest is new surface. The phase should land the
migration first and let the new verbs follow, so each commit stays reviewable.

### 2.4 Tier 3 — summoned

Consolidate into panels, closed by default, one open at a time:
Planner settings (from `MoveItPlanningRequestMenuUI`) · Scene/solution library (from
`MTCDashboardPanel`'s stage tree and solution list) · Robot connection and status.

Splitting `MTCDashboardPanel` means its Preview/Execute controls leave the panel and become
tier 2 trajectory verbs. Its 775-line sibling `MoveItPlanningRequestMenuUI` should be split
transport-from-UI at the same time (backlog B4) — the tier 3 panel is the UI half only.

**Part 2 constraint to hold:** a first-time user must complete every core task without
opening tier 3. Worth checking explicitly once the split lands, because planner pipeline
selection is currently mandatory before a plan can be sent
(`MoveItPlanningRequestMenuUI.cs:519-524` refuses with no planner selected).

### 2.5 In-world widgets

Spatial controls leave menus entirely (Part 1 P3):
- **Scale widget** replacing the Edit Shape toggle+slider stacks
  (`WristMenuController.cs:80-266`). Handles on the object, axis-locked per shape, reusing
  the existing `XRGrabTransformerScaleAxisLock` freeze flags.
- **Trajectory scrub ribbon** rendered along the path.
- **Cost-paint brush**, **gripper pose widget**, **constraint tube**, **joint-limit
  indicators** — new, and the last of these is the natural surface for the
  `InteractionRefusal` reasons Phase 1 already plumbs.

### 2.6 Mode manager

`Mode { Build, Plan, Teach }` — exactly three, per Part 4. Visible in tier 1, reinforced by
robot outline colour, one action to switch, always reversible. No hidden modes.

Build hosts the creation tool from P3. Teach is where `PickPlaceTaskRecorder` and the LfD
seam live.

### 2.7 Staying KitchenFR3-only — what that costs and how it works

Decided: no other scene is migrated. `Quest3ControllerRayInteractor` is **not** deleted,
the Phase 1 opt-in flags **stay**, and B14 / B7 / B3 remain open indefinitely. Part 8
CR-2 and CR-4 continue to pass per-scene rather than repo-wide, and the phase report will
say so rather than claiming a clean checklist.

The additive-only rule from Phase 1 therefore still governs every shared script. Two
places where that is harder in Phase 2 than it was in Phase 1:

- **P1's unified selection** merges two systems that 16 scenes rely on. It ships as new
  types plus an opt-in on `SelectionManager`; the legacy tag-and-material path stays the
  default. `Quest3RobotInteractionController` keeps its existing selection behavior and
  gains a parallel typed path.
- **The tier UI replaces the wrist menu**, but `WristMenuController` lives in the shared
  rig prefab. Rather than modify the prefab, the new tier UI is a separate prefab
  instanced only in KitchenFR3, and the existing `WristUIMenu` GameObject is **disabled
  via a scene-local prefab-instance override** — which, as Phase 1 established, is stored
  in the scene file and leaves the prefab asset untouched.

Net effect: `WristMenuController.cs` is not modified at all this phase, and the other 15
scenes keep their current menu.

---

## Verification

Phase 1's lesson (B17) was that editor-time checks cannot establish runtime behavior. So:

1. **Build B16 first — the ROS-stubbed PlayMode harness.** A seam around
   `ROSConnection.GetOrCreateInstance()` so `KitchenFR3` can run headless. Without it the
   five core flows stay manually-verified-only, and Phase 2 touches far more surface than
   Phase 1 did. This is a prerequisite, not a nice-to-have.
2. **Tier invariant tests.** Fifth tier 1 control throws; second tier 3 panel closes the
   first; tier 2 dies with its selection.
3. **PlayMode flow tests** over the five core flows against the stub.
4. **Part 8 design-review checklist** walked explicitly, including the two items no test
   can check: "~7 interactive elements visible with an object selected and a panel open",
   and "a first-time user can complete a core task without opening tier 3".
5. **Manual Quest pass.** Behavior here is *intended* to change, so this is a UX review
   rather than a diff — but the ROS traffic for each flow should still match Phase 1's
   capture.

---

## Decisions — settled

| # | Question | Decision |
|---|---|---|
| 1 | Where does object creation live? | **Build-mode in-world placement tool** (Part 4 step 2) |
| 2 | Undo stack in Phase 2? | **Build it** |
| 3 | Migrate `Kitchen` / `Task1_Kitchen1-4`? | **No — KitchenFR3 only** |
| 4 | visionOS target? | **PolySpatial mixed reality** |

Consequences of 3 and 4 are worked through in the two sections above; §2.7 was removed
because of 3, and §0 was added because of 4.

---

## Progress

| Item | Status |
|---|---|
| §0 B18 — UI stack decision | **done** — resolved by research; tiers build in world-space uGUI + TextMeshPro |
| Verification prereq — B16 ROS seam | **done** — `IRosBus` / `RosBus` / `FakeRosBus`, 9 call sites routed, 5 flow tests |
| P1 — unified selection with kinds | **done** — `SelectionKind`, `ISelectable`, `SelectableMarker`, `SelectionService` (7 tests) |
| P2 — undo stack | **done** — `IUndoableCommand`, `UndoStack` (8 tests) |
| §2.1 — tier system | **done** — `UiTier`, `IUiElement`, `UiTierRegistry`, `UiTierViolationException` (9 tests) |
| §2.2 — tier 1 controls | not started |
| §2.3 — tier 2 contextual menus | not started |
| §2.4 — tier 3 consolidation | not started |
| §2.5 — in-world widgets | not started |
| §2.6 — mode manager | **done** — `AppMode`, `ModeManager` (6 tests) |
| Bridging `SelectionManager` onto `SelectionService` | **done** — opt-in, default off |
| Bridging robot selection onto `SelectionService` | not started |
| Concrete undo commands | **done** — `ObstacleSnapshot`, `ObstacleFactory`, three commands (5 tests) |

**53/53 PlayMode tests pass.** No scene or prefab has been modified in Phase 2 — the
foundations are pure logic with no scene presence yet.

New assemblies this phase: `Erupt.Ui`, `Erupt.Ui.Tests`, `Erupt.Ros.Tests`.
`SelectableMarker` is named to avoid colliding with `UnityEngine.UI.Selectable`, which
tier UI code will have in scope.


## Blocked on maintainer commits

Three pieces of finished, passing work cannot be committed because they depend on
uncommitted changes in files the maintainer also edited:

| Held | Depends on |
|---|---|
| `Assets/Scripts/Obstacles/` | `CollisionObjectPublisher.suppressRemoveOnDestroy`, which is not at HEAD |
| `Assets/Scripts/Tests/` | the seamed `CollisionObjectPublisher` — the flow and obstacle tests assert against `FakeRosBus` |
| Five of nine ROS seam adopters | `CollisionObjectPublisher`, `CollisionObjectsListenerSimple`, `MTCDataManager`, `PickPlaceTaskRecorder`, `AttachedCollisionObjectListener` |
| `Assets/Scenes/KitchenFR3.unity` | an 18-line maintainer change interleaved with the Phase 1 migration |

Committing any of these would sweep in the maintainer's in-flight MTC work. The
unblocking step is for those files to be committed first; the refactor work then lands
cleanly on top.
