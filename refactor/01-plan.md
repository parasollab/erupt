# Phase 1 — Interaction abstraction

Plan for review. Depends on `00-survey.md`. Satisfies Guidelines Part 3 and Part 8
code-review items 1–5.

**Goal:** no feature code reads device input, and no feature code branches on platform.

---

## Scope constraint: KitchenFR3 only

Per maintainer instruction, **`Assets/Scenes/KitchenFR3.unity` is the only scene whose
behavior may change.** This is stronger than the prefab fork alone, because every scene
compiles the same C# files. It forces a rule that shapes the whole phase:

> **Additive-only rule.** Changes to any script instanced outside KitchenFR3 must be
> additive and must default to today's behavior. A scene that has not opted in must
> produce byte-identical behavior. New behavior is reached only by a new component or an
> explicitly-set opt-in field.

### Artifact classification

| Artifact | In KitchenFR3 | Shared with | Treatment |
|---|---|---|---|
| `Prefabs/XR Origin (XR Rig).prefab` | yes (34 refs) | `Kitchen`, `LoadingScene`, `KitchenLoadingScene`, `KitchenLoadingSceneFR3` | **Fork as variant** |
| `Prefabs/MoveItPlanningRequestMenu.prefab` | yes (44 refs) | `Kitchen`, `Task1_Kitchen1-4` | Not touched in Phase 1 |
| `Prefabs/Robot IK Manager.prefab` | **no** | `Task1_Kitchen1-4` only | Not touched |
| `Prefabs/SelectionManager.prefab` | **no** | `Task1_Kitchen1-4` only | Not touched |
| `Prefabs/fr3 (1).prefab` | yes | nothing | Free to edit |
| `Quest3ControllerRayInteractor` component | scene-local | `Kitchen`, `Task1_Kitchen1-4` (scene-local copies) | **Script untouched**; component disabled in KitchenFR3 only |
| `Quest3RobotInteractionController` | scene-local | + `Robot IK Manager.prefab` | Additive overloads |
| `SelectionManager`, `DirectArticulationIKController` | scene-local | many scenes | Additive opt-in |
| `WristMenuController` | in rig prefab | 16 scenes | Additive opt-in |

Because the ray interactor is a scene-local component, **`Quest3ControllerRayInteractor.cs`
is not modified at all in Phase 1.** It is simply not present on the migrated rig. This
removes the `[Obsolete]` step from the approved plan and retires backlog B7 early for
Phase 1 purposes.

---

## 1.1 Assembly split

Four new asmdefs under `Assets/Interaction/`. This is the platform boundary — Part 8 CR-1
is enforced structurally, not by grepping for `#if`.

| Assembly | Platforms | Contents |
|---|---|---|
| `Erupt.Interaction.Core` | all | types, router, sample bus, filter, ray visual |
| `Erupt.Interaction.Backends.OpenXR` | Android, Standalone, Editor | `XriControllerBackend`, `OpenXrHandBackend`, `XriInteractableAdapter` |
| `Erupt.Interaction.Backends.VisionOS` | Apple targets | `VisionOsGazePinchBackend` (stub) |
| `Erupt.Interaction.Backends.Desktop` | Editor, Standalone | `DesktopMouseBackend` |

`Assets/xrviz.asmdef` gains a reference to **Core only**. Its `includePlatforms` allowlist
is left alone this phase — widening it is blocked on survey C4 (PolySpatial vs. immersive
Metal) and changes nothing until a visionOS build is actually attempted.

Backends reference Core; Core references nothing platform-specific; `xrviz` cannot see any
backend. Feature code becomes structurally incapable of reading device state.

## 1.2 Core types

```csharp
enum Modality { Controller, Hand, GazePinch, Mouse }

[Flags] enum Capability {
    None = 0, Ray = 1, Gaze = 2, Pinch = 4, Grip = 8,
    Thumbstick = 16, Haptics = 32, DirectTouch = 64, Precision = 128
}

readonly struct InteractionSample {     // Part 3: modality + confidence on every sample
    Modality modality; float confidence; double timestamp;
    Pose pose; string sourceId;         // "left" | "right" | "gaze" | "mouse"
}

readonly struct InteractionRefusal {    // Part 3: refusals carry reasons
    string Reason; Vector3 WorldPoint; bool IsRefused;
}

enum IntentKind { Select, Deselect, BeginDrag, Drag, EndDrag, Axis, Activate }

readonly struct InteractionIntent {
    IntentKind kind; InteractionSample sample;
    Ray ray; RaycastHit hit; Vector2 axis;
}

interface IInteractionSource {
    Modality Modality { get; }
    Capability Capabilities { get; }
    bool Has(Capability c);
    InteractionSample Current { get; }
    event Action<InteractionIntent> Raw;   // pre-policy; only the Router subscribes
}
```

`InteractionSampleBus` — static publish, instance subscribe. One publisher (Router).
Subscribers wired this phase: planning-scene sync, study telemetry
(`PickPlaceTaskRecorder`), and an empty LfD seam for RADER.

## 1.3 Router

`InteractionRouter` — the only class holding interaction *policy*.

- **Target resolution.** Absorbs the raycast logic currently in
  `Quest3ControllerRayInteractor.Update:47-56` and `SelectionManager.TrySelect:50-73`.
  UI-surface rejection moves here and uses a layer mask rather than the name-substring
  matching in `SelectionManager:97-104` — which retires backlog B2 for the KitchenFR3 path
  without touching the shared script's own logic.
- **Filtering.** `OneEuroFilter`, parameters by modality per Part 3: controller
  `minCutoff 1.0 / beta 0.007`, hands `0.6 / 0.020`, gaze-pinch `0.5 / 0.030`. Behavior
  identical across devices; only parameters vary. The controller path must reproduce
  current feel — verified in §1.7, not assumed.
- **Precision scaling.** Per modality, router-only. Absorbs the thumbstick deadzone
  currently at `Quest3ControllerRayInteractor:11,84`.
- **Refusals.** Router surfaces `InteractionRefusal` from interactables. Phase 1 delivers
  plumbing plus a debug surface; spatial presentation is Phase 3.
- **Telemetry.** Every emitted intent publishes an `InteractionSample` to the bus.

## 1.4 Backends

| Backend | Status | Notes |
|---|---|---|
| `XriControllerBackend` | **Real — ports existing behavior** | Owns the `InputActionAsset`. Declares `Ray\|Grip\|Thumbstick\|Haptics`. Reproduces trigger-select, grip-drag, thumbstick-jog from the survey §1a table. Confidence is constant `1.0` for controllers. |
| `OpenXrHandBackend` | Stub | `com.unity.xr.hands` 1.6.1 is already a dependency. Reports capabilities and real tracking confidence; pinch wired, manipulation left for Phase 2. |
| `VisionOsGazePinchBackend` | Stub, compile-only | Declares `Gaze\|Pinch`, explicitly **not** `Ray` — Part 6 states raw gaze direction is unavailable to apps. **No PolySpatial reference** (new dependency, needs approval). |
| `DesktopMouseBackend` | Real | Mouse ray + click. Required for spectator view and the 2D study condition, and it is the only way to exercise the router in CI without a headset. |

## 1.5 Rewiring, under the additive-only rule

| Script | Change | Default when not opted in |
|---|---|---|
| `Quest3ControllerRayInteractor.cs` | **none** — component removed from the migrated rig only | Unchanged everywhere |
| `Quest3RobotInteractionController.cs` | Add intent-taking overloads beside `TryBeginHandleDrag(interactor, ray, hit)` / `UpdateHandleDrag` / `EndHandleDrag` / `JogSelectedJoint`. Existing signatures untouched. | Old overloads still used by other scenes |
| `SelectionManager.cs` | Add `[SerializeField] bool useInteractionRouter = false`. When false, `Start()` binds `selectAction` exactly as today. When true, it subscribes to the router and skips the action binding. Public surface (`SelectedObject`, `SetSelectedObject`, `OnObjectSelected`, `OnSelectionCleared`, `DeleteSelectedObject`) unchanged — `WristMenuController` and `MTCDashboardPanel` depend on it. Also fixes the B5 lambda leak by naming the handler, which is behavior-neutral. | `false` → today's behavior |
| `WristMenuController.cs` | `SetupInputActions:342` gains a router path; falls back to the hardcoded `"XRI Left Interaction"/"Menu"` lookup when no router is present. | Fallback → today's behavior |
| `DirectArticulationIKController.cs` | Add `TrySolveToTarget(...) → InteractionRefusal` and `TryNudgeJoint(...) → InteractionRefusal` reporting the silent clamp at `ClampJointPosition:421`. Existing `void` methods delegate and discard. | Existing methods unchanged |
| `PickPlaceTaskRecorder.cs` | Subscribe to the sample bus for telemetry. Publishing on `/pick_place_task` unchanged. | Additive |
| XRI grab transformers | Untouched. New `XriInteractableAdapter` on migrated objects publishes drag frames to the bus. | Additive |

No ROS topic, service, or message type is renamed or restructured.

## 1.6 Scene and prefab work

1. Create `Assets/Prefabs/XR Origin (XR Rig) Interaction.prefab` as a **variant** of the
   existing rig. Add `InteractionRouter` + backends; set `WristMenuController`'s router
   reference.
2. In `KitchenFR3.unity` only: swap the rig instance for the variant, remove the two
   `Quest3ControllerRayInteractor` components, add `InteractionRayVisual`, set
   `SelectionManager.useInteractionRouter = true`, wire `Quest3RobotInteractionController`
   to the router.
3. Verify `Kitchen`, `LoadingScene`, `KitchenLoadingScene*`, `Task1_Kitchen1-4`, and all
   `Study Scenes/` show **zero diff** in `git status`.

## 1.7 Verification — Quest behavior unchanged

1. **ROS traffic diff (primary oracle).** Bag the survey §8 topic list on Quest against
   `KitchenFR3`, running the five flows, *before* any edit. Repeat after and diff message
   sequences and payloads. ROS output is the observable contract.
2. **EditMode router tests.** Record raw controller sample sequences from the pre-refactor
   build; assert filtered router output matches within tolerance. This is where the 1-Euro
   controller parameters are validated against current feel rather than assumed.
3. **PlayMode smoke via XR Device Simulator.** `Unity.XR.Interaction.Toolkit.Samples.InteractionSimulator`
   is already in the project. Script the five flows headless.
4. **Scene diff check.** `git status` must show no modification to any scene but
   `KitchenFR3.unity`, and no modification to the original rig prefab.
5. **Manual Quest checklist**, both builds side by side: create obstacle → scale it → snap
   to surface → set goal → request plan → preview → execute; plus grab feel, thumbstick
   push/pull, and joint jog, which the automated checks cover least well.

Gate: 1, 3, 4, 5 must pass before Phase 2.

## 1.8 Commits

1. `interaction: add Core assembly and interaction types (Guidelines Part 3)`
2. `interaction: add router with modality-parameterized filtering (Part 3, Part 8 CR-4)`
3. `interaction: add OpenXR controller backend (Part 3, Part 8 CR-2)`
4. `interaction: add hand, visionOS, and desktop backend stubs (Part 5 P5, Part 6)`
5. `interaction: add sample bus and telemetry subscribers (Part 3)`
6. `interaction: add opt-in router paths to selection, wrist menu, IK (Part 8 CR-2, CR-5)`
7. `interaction: fork XR rig prefab and migrate KitchenFR3 (Part 3)`

---

## Costs this scope creates

Stated so they are chosen rather than discovered later.

- **Dual code paths.** Every opt-in flag in §1.5 is a fork that lives until the other
  scenes migrate. Five flags across four scripts. Logged as backlog **B14**.
- **Filtering is only half-enforced.** Part 8 CR-4 says filtering happens only in the
  router. It will, for KitchenFR3 — but the deadzone at `Quest3ControllerRayInteractor:84`
  still runs in five other scenes. CR-4 passes for the migrated scene, not the repo.
- **Part 8 CR-2 likewise passes per-scene, not repo-wide**, until Phase 2 migrates the
  rest. The phase report will state both plainly rather than claiming a clean checklist.

## Open questions

- **C4 (survey §7)** — visionOS mode: PolySpatial MR or immersive Metal? Not blocking
  Phase 1; blocks Phase 2 and the asmdef allowlist widening.
- **B14 retirement** — when do `Kitchen` and `Task1_Kitchen1-4` migrate? That decision
  sets how long the dual paths live.
