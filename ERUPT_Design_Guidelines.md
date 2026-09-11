# ERUPT design guidelines

Working design document for the ERUPT XR motion-planning toolkit and its
extensions (learning from demonstration, grasp specification, language
specification, Apple Vision Pro support).

The purpose of this document is to make it possible to keep adding features
without the interface becoming unusable for novices. Every rule here exists to
answer one question: **where does the next feature go?**

---

## Part 1 — First principles

**P1. One source of truth.**
The environment representation, the human's expressed intent, and the planner's
output live in one synchronized place. Every feature that keeps these three in
sync compounds in value. Every feature that forks them (a second scene format
for LfD, a separate app for visionOS, a parallel obstacle list for grasping)
costs more later than it saves now.

**P2. Organize by user intent, not by subsystem.**
The most common failure mode is a menu per module: an Environment menu, a
Planning menu, an LfD menu, a Grasp menu. That structure mirrors the codebase,
not the workflow, and it grows linearly with the feature list. Organize around
what the user is acting on and what they are trying to do.

**P3. Spatial things get spatial controls.**
If a control acts on something in the world, it lives in the world, attached to
that thing. If a control configures the system, it lives in a panel. This single
heuristic resolves most menu placement questions.

**P4. Never execute an inferred intent invisibly.**
Anything the system infers — a spoken command, a detected object, a suggested
grasp — is a *proposal* that the user confirms, adjusts, or dismisses. This is
non-negotiable on a system that ultimately commands a physical arm.

**P5. Capability, not platform.**
Feature code never asks which device it is running on. It asks what the current
input source can do. Platform differences are absorbed at one boundary.

---

## Part 2 — UI architecture

### The three tiers

**Tier 1 — Persistent.** Always visible. Hard cap of four controls.
Current contents: mode indicator, undo, redo, voice input.
Nothing else earns a place here. If a fifth control seems necessary, it belongs
in tier 2 or tier 3.

**Tier 2 — Contextual.** Appears attached to the currently selected object,
disappears on deselect. This is where roughly 80% of functionality lives, and
where all new features should go by default.

**Tier 3 — Summoned.** Configuration panels. One open at a time, none open by
default. A user must be able to complete every core task without ever opening
tier 3.

### Tier 2 inventory (by selected object type)

| Selection | Verbs |
|---|---|
| Obstacle / collision object | resize, reshape, paint cost, lock, delete |
| Manipulable object | grasp here, set as target, properties |
| Robot link | set joint angle, lock joint |
| End effector | set goal, open/close gripper, preview grasp |
| Trajectory | scrub, correct, compare, execute |
| Waypoint | move, delete, pin timing |

Adding a feature usually means adding one row entry, not one menu.

### Tier 3 inventory

Grasp library · Scene library · Planner settings · Demonstration timeline ·
Voice transcript · Robot connection and status

### In-world widgets (neither menu nor panel)

Some controls are spatial data and belong in the scene itself, not in any tier:
gripper pose widget, cost-paint brush, constraint tube, trajectory scrub ribbon
rendered along the path, joint-limit indicators on links.

---

## Part 3 — Interaction model

### The abstraction boundary

```
Backend  →  Router  →  Interactables
(device)    (policy)   (features)
```

- **Backend** — one per platform. Produces raw source state. The only place that
  knows about triggers, pinches, thumbsticks, or gaze.
- **Router** — resolves target, applies filtering and precision scaling, emits
  intents, records telemetry. The only place with interaction *policy*.
- **Interactables** — robot links, obstacles, waypoints, gripper widgets. Consume
  intents. Never see device state.

Feature code must never contain a platform preprocessor branch. It asks
`source.Has(Capability.Ray)`, `Capability.Gaze`, `Capability.Haptics`, and so on.

### Modality and confidence on every sample

Every manipulation frame carries the modality that produced it and a tracking
confidence value. This is not optional plumbing — it is what allows:

- LfD methods to down-weight or reject degraded demonstration spans
- user studies to report how much of a session was low-confidence
- future analysis of how input modality affects demonstration quality

### Filtering policy

Jitter smoothing and precision scaling live in the Router, not in backends, so
behavior is identical across devices. Filter *parameters* vary by modality;
filter *behavior* does not.

Suggested 1-Euro starting points:
controller `minCutoff 1.0 / beta 0.007` · hands `0.6 / 0.020` · gaze-pinch
`0.5 / 0.030`.

### Refusals carry reasons

When an interaction cannot begin — joint at limit, target unreachable, object
locked by another user — the system returns a reason string that is surfaced
spatially at the point of failure. Silent no-ops are the most common source of
novice confusion.

### Division of labor between input channels

- **Gaze** selects. It is excellent at picking *what* and poor at continuous control.
- **Hands** manipulate. They specify *how*.
- **Voice** commands. It can reach any verb but always produces a proposal.

### The busy-hand rule

During a manipulation one hand is occupied. Anything needed mid-drag must be
reachable by the other hand, by gaze, or by voice — never by a control that
requires the hand currently holding the robot.

---

## Part 4 — How to add a feature

Apply this test, in order:

1. **Can it be one more verb on an existing object's context menu?**
   If yes, add it there. Cost: near zero. This is the expected answer.
2. **Is it a spatial control over spatial data?**
   If yes, it is an in-world widget attached to that data.
3. **Is it configuration or a library?**
   If yes, it is a tier 3 panel, closed by default, and it must not be required
   for any core task.
4. **Does it need a new mode or a new persistent control?**
   Then it must justify itself against everything already competing for tier 1,
   and the burden of proof is high. Most features that seem to need this do not.

### Modes

Modes are the smallest possible set: **Build · Plan · Teach**. Rules:

- The current mode is always visible in tier 1 and reinforced by the robot's
  outline color.
- Switching modes takes one action and is always reversible.
- No hidden modes. An invisible mode is a bug.

---

## Part 5 — Subsystem placement

### Grasping

Grasping is **not a mode**. A grasp is a property of an object plus a pose on
that object, so it lives in that object's tier 2 menu.

Primary flow: select object → "grasp here" → translucent gripper widget snaps to
the surface with an approach-angle ring and a width handle → confirm → saved as a
named grasp on the object *type*, so the next instance inherits it.

Preferred specification method where hand tracking is available: the user places
their own hand where they would grab the object and pinches. Hand pose maps to
gripper pose. Novices reason about "hold it here, like this," not about 6-DOF
poses and approach vectors.

The grasp library is tier 3 and exists only for reuse and editing. The common
path never opens it.

### Language specification

Language is an accelerator that sits parallel to the tiers, not inside them.

1. **Propose, never execute.** Speech produces a highlighted referent and a ghost
   preview. The user confirms with a pinch.
2. **Gaze resolves the nouns.** Ambiguous references highlight all candidates and
   are disambiguated by look-and-pinch. Language carries the verb; gaze carries
   the reference.
3. **Teach the GUI.** On execution, briefly flash the equivalent manual control so
   users learn the direct-manipulation path from their own commands.
4. Transcript lives in tier 3 and doubles as study instrumentation.

### Learning from demonstration

LfD is the **Teach** mode, not a separate application. Demonstration reuses the
existing manipulation interactions with recording enabled — no new interaction
vocabulary, only a new intent.

- Correction of an existing plan is the primary entry point, not full
  demonstration. It is gentler for novices and produces better-conditioned data.
- Positive and negative demonstrations are both first-class; marking "what not to
  do" is a capability only XR offers safely.
- The environment representation for learning is derived from the planning scene.
  Never maintain a parallel scene format.

### Planning

Expose preference-level controls, not algorithm parameters. After previewing a
plan the user asks for "smoother," "shorter," "more clearance." Raw planner
parameters remain available in tier 3 for expert users.

Planning failure must produce a spatial explanation: highlight the colliding
geometry, or indicate that the goal is unreachable. Never report bare failure.

---

## Part 6 — Platform notes

### Shared

- All interaction goes through the abstraction in Part 3.
- All features degrade gracefully by capability. Absent haptics fall back to audio
  and visual cues; absent thumbsticks fall back to gaze-teleport.

### Meta Quest / OpenXR

- Tier 1 is wrist-anchored.
- Tier 3 panels are grabbable world-space panels.
- Controllers and hand tracking are both backends behind the same router.

### Apple Vision Pro / visionOS

- Use the platform's grammar rather than porting floating Unity panels.
- Tier 3 becomes a single visionOS window with tabs, parkable on a wall.
- Tier 1 becomes an ornament on that window or a small world-anchored bar.
- Gaze hover intent lets tier 2 appear near the gazed object without a deliberate
  ray-select.
- Raw gaze direction is not available to apps. Gaze-based *selection* is
  supported; gaze *analytics* are not. Do not design study measures that require
  a gaze ray.
- Rendering in mixed reality passes through RealityKit via PolySpatial. Custom
  visual effects (swept volumes, clearance heatmaps, ghost robots) must be
  expressible in Shader Graph. Text rendering is TextMeshPro only.
- Camera frame access requires an Apple enterprise entitlement. Prefer ARKit image
  or object tracking for robot registration where possible.
- Consider latching pinch (pinch to attach, pinch to release) over hold-to-drag
  for sustained manipulations to reduce fatigue.

---

## Part 7 — Progressive disclosure

Gate features by demonstrated competence, not by a settings toggle.

| Stage | Visible |
|---|---|
| First session | Scene, one primitive shape tool, goal dragging, plan button |
| After a successful plan | Grasping, obstacle cost painting, trajectory scrubbing |
| After a successful execution | Teach mode, demonstration timeline |
| On request | Planner settings, libraries, export |

The existing study task set (object shielding, motion manipulation, collision
identification, goal specification) doubles as the competence ladder.

---

## Part 8 — Review checklists

### Design review

- [ ] Tier 1 still has four or fewer controls.
- [ ] The new feature added a verb to an existing context menu, not a new menu.
- [ ] No more than ~7 interactive elements visible with an object selected and a
      panel open.
- [ ] Current mode is visible and one action away from changing.
- [ ] Every failure state produces a spatially situated reason.
- [ ] Nothing needed mid-manipulation requires the busy hand.
- [ ] The feature is reachable by voice, and voice proposes rather than executes.
- [ ] A first-time user can still complete a core task without opening tier 3.

### Code review

- [ ] No platform preprocessor directives outside the backend layer.
- [ ] No direct reads of button or pinch state outside the backend layer.
- [ ] All manipulation flows emit samples carrying modality and confidence.
- [ ] Filtering and precision scaling happen only in the router.
- [ ] Interaction refusals return a user-facing reason.
- [ ] No parallel scene representation was introduced.
- [ ] New UI is registered as a tier with an explicit tier assignment, not
      instantiated ad hoc.
