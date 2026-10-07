# Phase 1 report — Interaction abstraction

Implements `01-plan.md`. Guidelines Part 3; Part 8 code-review items 1–5.

**Scope honored:** `Assets/Scenes/KitchenFR3.unity` is the only scene modified. No prefab
asset was modified. `Assets/Prefabs/XR Origin (XR Rig).prefab` is byte-identical to its
pre-refactor state, verified by diff against a backup taken before any edit.

---

## Deviation from the approved plan: no prefab fork

The plan called for forking the XR rig prefab as a variant. **That turned out to be
unnecessary and was not done.** Inspection of the live scene showed:

- The two `Robot Ray` GameObjects carrying `Quest3ControllerRayInteractor` are
  **scene-local children** added under the rig instance, not part of the prefab.
- `WristMenuController` *is* in the prefab, but assigning its new router field is a
  **prefab-instance property override**, which Unity stores in the scene file, not in the
  prefab asset.

So every change lands in the scene. This is strictly better than a fork: the four other
scenes using the rig prefab (`Kitchen`, `LoadingScene`, `KitchenLoadingScene`,
`KitchenLoadingSceneFR3`) are untouched, and there is no duplicate prefab to keep in sync
or reconcile later. Backlog B7's "delete the obsolete script in Phase 2" still stands,
because five scenes still instance `Quest3ControllerRayInteractor` directly.

---

## What was built

### New assemblies — the platform boundary (Part 8 CR-1)

| Assembly | Platforms | Contents |
|---|---|---|
| `Erupt.Interaction.Core` | all | 13 files: types, router, sample bus, 1-Euro filter, ray visual |
| `Erupt.Interaction.Backends.OpenXR` | Android, Editor, desktop standalones | `XriControllerBackend`, `OpenXrHandBackend`, `XriInteractableAdapter` |
| `Erupt.Interaction.Backends.VisionOS` | all except Android | `VisionOsGazePinchBackend` (stub) |
| `Erupt.Interaction.Backends.Desktop` | Editor + desktop standalones | `DesktopMouseBackend` |
| `Erupt.Interaction.Editor` | Editor | migration, inspection, verification tooling |
| `Erupt.Interaction.Tests` | Editor | router policy tests |

`Assets/xrviz.asmdef` references `Erupt.Interaction.Core` **only**. Feature code is now
structurally incapable of referencing a backend — the assembly graph forbids it, which
holds without anyone remembering to grep for `#if`.

The visionOS assembly uses `excludePlatforms: ["Android"]` rather than an
`includePlatforms` allowlist, so it compiles in the Editor (errors surface immediately)
while shipping nothing into the Quest build.

### Core

`Modality` · `Capability` · `InteractionSample` · `InteractionIntent` ·
`InteractionRefusal` · `IInteractionSource` / `InteractionSourceBehaviour` ·
`InteractionSampleBus` · `OneEuroFilter` · `ModalityFilterProfile` · `InteractionRouter` ·
`InteractionRayVisual`.

Every sample carries modality, tracking confidence and timestamp (Part 3). The router is
the only holder of policy: target resolution, 1-Euro filtering, precision scaling,
deadzone, refusal broadcast, telemetry publication.

Filter parameters are the Part 3 starting points — controller `1.0/0.007`, hands
`0.6/0.020`, gaze-pinch `0.5/0.030` — with the controller deadzone set to **0.18**, the
value the deleted script applied inline, so the migrated feel matches.

### Rewiring under the additive-only rule

| Script | Change | Unmigrated scenes |
|---|---|---|
| `Quest3ControllerRayInteractor.cs` | **unmodified** — simply absent from KitchenFR3 | unaffected |
| `SelectionManager.cs` | `interactionRouter` + `selectionSourceId` opt-in; named handler replaces the un-removable lambda (retires B5) | `interactionRouter` unset → old path |
| `WristMenuController.cs` | `interactionRouter` opt-in on the menu toggle | unset → hardcoded action lookup |
| `Quest3RobotInteractionController.cs` | intent-taking overloads beside the existing signatures; drag token widened to `object` (private, not an API change) | old overloads intact |
| `DirectArticulationIKController.cs` | `TrySolveToTarget` / `TryNudgeJoint` returning `InteractionRefusal`; existing `void` methods unchanged | unaffected |
| `PickPlaceTaskRecorder.cs` | subscribes to the sample bus; `/pick_place_task` payload unchanged | additive |
| `RobotInteractionRouterBinding.cs` | **new** — router → robot handle and joint jog | not present |

No ROS topic, service, or message type was renamed or restructured.

---

## Two behavior differences found and corrected

Both were discovered by reading what the scene was actually bound to, not by assuming.

**1. Selection would have widened to both hands.** `SelectionManager.selectAction` was
bound to `XRI Right Interaction/SelectObject` — right trigger only. Routing both
controllers' triggers through `router.Select` would have let the left trigger start
selecting objects. Added `selectionSourceId`, set to `"right"` on migration, restoring the
original scoping.

Worth flagging: Part 3's busy-hand rule argues the *opposite* — anything needed mid-drag
should be reachable by the other hand. Allowing both is probably correct, but it is a
behavior change and Phase 1's gate is "unchanged". Logged as **B15** for Phase 2.

**2. Pointing at the wrist menu would have cleared the selection.** The old
`SelectionManager` rejected UI hits by matching GameObject *names* (`"wrist"`, `"ui"`,
`"menu"`, …) and returned early, preserving the selection. The router resolves targets by
raycast, so without a UI mask the wrist menu would come back as a non-`Selectable` world
hit and *clear* the selection — breaking select-object-then-use-the-menu, the core flow.
The router's `uiLayers` is set to `0x20` (layer 5, UI) during migration, computed from the
wrist menu's own hierarchy. This also retires **B2** for the migrated path, since name
matching is gone.

A third, smaller difference is handled but worth recording: with one router feeding one
binding, both controllers could have driven the IK handle simultaneously, where before a
single `activeDragInteractor` field meant the last gripper won. `RobotInteractionRouterBinding`
tracks `dragOwnerSourceId` and ignores drag frames from other sources.

---

## Verification

### Passed

| Check | Result |
|---|---|
| Project compiles | **0 errors**, 0 new warnings (remaining warnings are pre-existing in `ARPlaneColorizer` and `GrabMoveDiagnostics`) |
| All six new assemblies build | `Erupt.Interaction.*.dll` present in `Library/ScriptAssemblies` |
| PlayMode test suite | **12/12 pass** — deadzone at/below 0.17, pass-through above, discrete select unsmoothed, modality+confidence on every sample, confidence clamping, refusal broadcast and suppression, capability query, filter convergence, first-sample-unsmoothed, plus two wiring regression tests |
| Scene wiring (static) | **9/9 checks pass** (`ERUPT/Refactor/Verify KitchenFR3`). See the correction below: these passed while the scene was non-functional, so they establish inspector state, not behavior |
| Scene diff scope (plan item 4) | only `KitchenFR3.unity` modified; rig prefab byte-identical |
| Part 8 CR-1 | no preprocessor directives outside backends except the documented `#if OPENCV_FOR_UNITY` (B6 / survey C3) |
| Part 8 CR-2 | no device reads in Core or in feature code |
| Part 8 CR-4 | no filtering outside the router — except `Quest3ControllerRayInteractor`, which is the known B14 debt in the five unmigrated scenes |

### Correction — the first report of this phase was wrong

The originally reported "9/9 scene wiring verified" was **not evidence that the scene
worked**, and the scene did not work. `InteractionRouter.Awake` used
`GetComponentsInChildren`, which found none of the backends — they sit under
`Camera Offset/<hand>/Robot Ray`, not under the router. The `Register()` calls the
migration made ran at editor time into a non-serialized list and a non-serialized event
subscription, neither of which survives into play mode. Zero sources were registered, so
no input reached any feature: no object selection, no robot selection, and — because
`SelectableGrabController` gates each `XRGrabInteractable` on selection state — nothing
was grabbable. World-space menus kept working because they use XRI's UI interactor, which
this phase never touched.

Fixed by scanning the scene in `Awake` and logging an error when zero sources are found.
Confirmed working in the headset by the maintainer. Logged as **B17**.

Three further divergences were corrected in the same pass:

- **Discrete presses are no longer smoothed.** Filtering now applies only to `Drag`
  frames. Applying it to a press added targeting lag and made the drawn ray — which reads
  the live pose — disagree with the resolved target under fast motion.
- **Trigger-on-miss no longer clears the robot's joint highlight.** The old script called
  `SelectFromHit` only on a hit; the binding was calling it unconditionally.
- **Grip detection uses `IsPressed()` uniformly**, rather than branching on
  `expectedControlType`, which is empty for XRI's `Select` action.

The two `InteractionRouterWiringTests` were then **validated against the buggy code**:
reverting the `Awake` fix makes exactly those two fail (10/12), and restoring it returns
12/12. The tests catch the defect rather than merely passing alongside it.

A second defect surfaced while running them: the test assembly was `includePlatforms:
["Editor"]`, so Unity refused to `AddComponent` its MonoBehaviour helper — *"Can't add
script behaviour because it is an editor script"*. The original nested `FakeSource` had
slipped past that check because a nested private class gets no `MonoScript`. The assembly
is now platform-unrestricted with a `UNITY_INCLUDE_TESTS` define constraint, and the suite
runs in **PlayMode**, where component lifecycle actually executes. Running these as
EditMode tests could never have caught B17, because `Awake` does not run in edit mode.

### Not run — requires hardware I do not have

The plan's gate is items 1, 3, 4, 5. **Item 4 passed. Items 1, 3 and 5 have not been run.**

| Check | Why not |
|---|---|
| 1. ROS traffic diff before/after | Needs a Quest headset and a running ROS 2 stack |
| 3. PlayMode smoke via XR Device Simulator | The scene connects to ROS on load; a headless run produces connection failures rather than a meaningful signal. Writing a ROS-stubbed harness is worth doing but is its own piece of work |
| 5. Manual Quest checklist | Needs the headset |

**Phase 1's behavior-preservation gate is therefore not met yet.** Grabbing and selection
were confirmed working in the headset after the B17 fix, which clears the regression but
is not the same as confirming the five core flows produce identical ROS traffic. Phase 2
should not start until items 1 and 5 are run.

The honest lesson of this phase is that its first report over-claimed. Static verification
passed on a scene where nothing worked, and I described that as verified wiring. The
PlayMode suite exists now because editor-time checks structurally cannot establish runtime
behavior — which is also why `01-plan.md` item 3 and backlog **B16** are promoted to a
prerequisite for Phase 2 rather than a nice-to-have.

---

## Part 8 code-review checklist

| Check | Before | After |
|---|---|---|
| No platform preprocessor directives outside the backend layer | ⚠️ passed literally, failed in substance | ✅ enforced by assembly graph |
| No direct reads of button or pinch state outside the backend layer | ❌ 1 raw-device script, 8 action sites | ✅ **for KitchenFR3**; five scenes still on the old path (B14) |
| All manipulation flows emit samples with modality and confidence | ❌ no such type | ✅ router + `XriInteractableAdapter` |
| Filtering and precision scaling only in the router | ❌ inline in backend | ✅ **for KitchenFR3** (B14) |
| Interaction refusals return a user-facing reason | ❌ silent clamp, 92 log-only sites | ✅ plumbing done; spatial presentation is Phase 3 |
| No parallel scene representation | ✅ | ✅ unchanged |
| New UI registered with an explicit tier | ❌ no tier system | ❌ Phase 2 |

Two rows pass per-scene rather than repo-wide. That is inherent to the KitchenFR3-only
scope, not a shortfall in the work, and it resolves when Phase 2 migrates the rest.

---

## Tooling left in the repo

`ERUPT/Refactor/Inspect KitchenFR3` · `Migrate KitchenFR3 to Interaction Router`
(idempotent — re-running reuses the existing router) · `Verify KitchenFR3`.

Editor-only, in `Erupt.Interaction.Editor`, `autoReferenced: false`. Useful again when
Phase 2 migrates the remaining scenes.

---

## Next

1. Run verification items 1, 3 and 5 on the headset. Nothing else should start first.
2. Answer survey **C4** — PolySpatial mixed reality or immersive Metal — before Phase 2.
3. Decide **B14** retirement: when do `Kitchen` and `Task1_Kitchen1-4` migrate?
