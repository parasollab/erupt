# ERUPT design guidelines

Working design document for the ERUPT XR motion-planning toolkit and its
extensions (learning from demonstration, grasp specification, language
specification). ERUPT targets Meta Quest, with controllers or tracked hands; a
desktop mouse backend exists for working in the editor.

The purpose of this document is to make it possible to keep adding features
without the interface becoming unusable for novices. Every rule here exists to
answer one question: **where does the next feature go?**

---

## Part 1 — First principles

**P1. One source of truth.**
The environment representation, the human's expressed intent, and the planner's
output live in one synchronized place. Every feature that keeps these three in
sync compounds in value. Every feature that forks them (a second scene format
for LfD, a separate app for hand tracking, a parallel obstacle list for grasping)
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

**P5. Capability, not device.**
Feature code never asks which device it is running on, and never references a
device SDK. It asks what the current input source can do. The differences between
controllers, tracked hands and the editor's mouse are absorbed at one boundary.

---

## Part 2 — UI architecture

### The three tiers

**Tier 1 — Persistent.** Always visible. Hard cap of four controls.
Current contents: menu (opens and closes tier 3), undo, redo, voice input.
Nothing else earns a place here. If a fifth control seems necessary, it belongs
in tier 2 or tier 3. The menu control is here because the panel must be findable
by someone who has been told nothing: a controller button nobody knows about is
not a way in.

**Tier 2 — Contextual.** Appears attached to the currently selected object,
disappears on deselect. This is where roughly 80% of functionality lives, and
where all new features should go by default. A menu holds what can be done now:
a verb of the inventory below that nothing implements yet is not shown (a button
that is always disabled reads as broken), and a selection with nothing to offer
has no menu. The inventory is the plan; one switch on the tier UI shows it whole.

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

**Several robots.** A scene may hold any number of robots, and there is still one
selection and no robot picker: *selection defines the active robot*. Selecting a
link, an end effector or a plan of a robot makes that robot the active one, and it
stays active while something else is selected. A verb acts on the robot the selected
object belongs to. The active robot is shown, only when there is more than one,
by the tier 2 header ("End Effector · left"), the tier 3 tab titles ("MoveIt · left")
and a light tint on its links. Tier 1 does not grow.

### Tier 3 inventory

Grasp library · Scene library · Planner settings · Demonstration timeline ·
Voice transcript · Robot connection and status · Settings

**Settings** is the last tab and holds what is a preference of the person using
the app rather than a part of the task: today the collision haptics (on or off,
strength, distance) and the ROS connection (where the app connects, whether it
is connected, and a keypad to enter another address). A setting has a default
that works, is kept between sessions, and takes effect at once. The component a
setting belongs to puts its controls there, so a scene without that component
shows no dead control.

A keypad is the one way to enter text or a number that has no range to slide
over. It is shown only after its own button was pressed, in the place of the
tab's other controls, and the same button puts it away; it never waits behind a
closed panel or another tab.

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

- **Backend** — one per input device (Quest controller, tracked hand, desktop
  mouse). Produces raw source state. The only place that knows about triggers,
  pinches, thumbsticks, or mouse buttons.
- **Router** — resolves target, applies filtering and precision scaling, emits
  intents, records telemetry. The only place with interaction *policy*.
- **Interactables** — robot links, obstacles, waypoints, gripper widgets. Consume
  intents. Never see device state.

Feature code must never contain a platform preprocessor branch. It asks
`source.Has(Capability.Ray)`, `Capability.Thumbstick`, `Capability.Haptics`, and so on.

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
controller `minCutoff 1.0 / beta 0.007` · hands `0.6 / 0.020`.

### Refusals carry reasons

When an interaction cannot begin — joint at limit, target unreachable, object
locked by another user — the system returns a reason string that is surfaced
spatially at the point of failure. Silent no-ops are the most common source of
novice confusion.

A refusal also carries a machine-readable code and, where there is one, the
object it is about (the link of the joint at its limit) and the input source
that caused it. It is reported to the router, and everything the user perceives
is driven from the router's refusal event: the reason as text at the point of
failure, a tint on the link whose joint is at its limit, a double pulse on the
controller that caused it. A feature that refuses reports the refusal; it does
not draw its own warning.

### Joint limits are not negotiable

The robot model never shows a pose outside the robot's joint limits, whichever
path the motion came from: the user's hand, a planner, a recorded
demonstration, the real robot's joint states. Every joint write goes through
the robot model, which clamps it. A plan that leaves the limits is flagged
when it arrives, previews clamped, and is not executed unless the planner
plugin was explicitly set to allow it.

### Division of labor between input channels

- **The ray** selects. Pointing with a controller or a hand picks *what*.
- **Hands** manipulate. Holding and moving (grip or pinch) specifies *how*.
- **Voice** commands. It can reach any verb but always produces a proposal.

### The busy-hand rule

During a manipulation one hand is occupied. Anything needed mid-drag must be
reachable by the other hand or by voice — never by a control that requires the
hand currently holding the robot.

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
4. **Does it need a new persistent control?**
   Then it must justify itself against everything already competing for tier 1,
   and the burden of proof is high. Most features that seem to need this do not.

A feature never needs a mode; see below.

### No modes

ERUPT has no application modes. It used to have three (Build, Plan, Teach), and
what they gated was small: the buttons that add a shape, the buttons that start
a recording, one verb on a plan. What they cost was a first-time user's "why is
this greyed out". Every action a mode stood in front of is already an explicit
press on a button that says what it does, so the mode added a second step and
no safety. Rules:

- Everything a feature offers is offered whenever it can work. What cannot
  work right now refuses with its reason (Part 3); it does not wait behind a
  switch somewhere else.
- State that changes what the user's hands do (a recording in progress, a
  preview playing, an execution running) is started and stopped by the same
  visible control, which shows that state while it lasts ("Record" becomes
  "Stop").
- Do not add a mode, a mode-like global toggle, or a control that changes what
  other controls mean. A hidden mode is a bug; a visible one is a cost that has
  not paid for itself here.

---

## Part 5 — Subsystem placement

### Grasping

Grasping is **not a separate tool**. A grasp is a property of an object plus a
pose on that object, so it lives in that object's tier 2 menu.

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
   preview. The user confirms with a select (trigger or pinch).
2. **Pointing resolves the nouns.** Ambiguous references highlight all candidates
   and are disambiguated by point-and-select. Language carries the verb; the ray
   carries the reference.
3. **Teach the GUI.** On execution, briefly flash the equivalent manual control so
   users learn the direct-manipulation path from their own commands.
4. Transcript lives in tier 3 and doubles as a log of what the user asked for.

### Learning from demonstration

LfD is part of the same application: not a separate one, and not a mode to
enter. Demonstration reuses the existing manipulation interactions with a
recording running, which the user starts and stops with one button (Record on
the demonstrations tab) — no new interaction vocabulary, only a new intent.

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
- All features degrade gracefully by capability. Absent haptics (tracked hands,
  mouse) fall back to audio and visual cues; absent thumbsticks fall back to a
  pinch gesture.
- Core feature code names no device and does not reference the Meta SDK. Device
  specifics stay in the backends; features that need the Meta SDK (scene anchors,
  passthrough) stay in the app.

### Meta Quest / OpenXR

- Tier 1 is wrist-anchored: on the left controller, or on the left wrist when
  hands are tracked. On the controller it sits where a watch face would: behind
  the controller's origin, lifted and leaned back toward the user's eyes, so
  that the controller's own ray never crosses it. It is pressed with the other
  hand's ray or a poke.
- Tier 3 is one grabbable world-space panel with tabs.
- Controllers and hand tracking are both backends behind the same router, and the
  user can switch between them at any time by picking the controllers up or
  putting them down.
- UI is world-space uGUI with TextMeshPro; screen-space canvases do not render in
  the headset.

### Desktop (editor)

- The mouse and keyboard backend exists for working in the editor without a headset.
  It goes through the same router and intents as the Quest backends and is not a
  shipping target. It sits on the ERUPT root and stands down whenever an XR head
  device is present.
- With no wrist to anchor it to, tier 1 rides the top of the view. Tier 3 opens
  square in front of the camera. Tier 2 stays attached to the selection, drawn large
  enough to read from where the camera is and in front of whatever would hide it.

---

## Part 7 — Progressive disclosure

Gate features by demonstrated competence, not by a settings toggle.

| Stage | Visible |
|---|---|
| First session | Scene, one primitive shape tool, goal dragging, plan button |
| After a successful plan | Grasping, obstacle cost painting, trajectory scrubbing |
| After a successful execution | Demonstration recording, demonstration timeline |
| On request | Planner settings, libraries, export |

The core task set (object shielding, motion manipulation, collision
identification, goal specification) doubles as the competence ladder.

---

## Part 8 — Review checklists

### Design review

- [ ] Tier 1 still has four or fewer controls.
- [ ] The new feature added a verb to an existing context menu, not a new menu.
- [ ] No more than ~7 interactive elements visible with an object selected and a
      panel open (a slider is one element, and so is a keypad the user asked for,
      which has its tab to itself while it is open).
- [ ] No mode was added: nothing the feature offers waits behind a switch
      somewhere else, and a state that lasts (recording, previewing) is shown and
      ended by the control that started it.
- [ ] Every failure state produces a spatially situated reason.
- [ ] Nothing needed mid-manipulation requires the busy hand.
- [ ] The feature is reachable by voice, and voice proposes rather than executes.
- [ ] A first-time user can still complete a core task without opening tier 3.

### Code review

- [ ] No platform preprocessor directives outside the backend layer.
- [ ] No device name and no Meta SDK reference in core feature code.
- [ ] No direct reads of button or pinch state outside the backend layer.
- [ ] All manipulation flows emit samples carrying modality and confidence.
- [ ] Filtering and precision scaling happen only in the router.
- [ ] Interaction refusals return a user-facing reason and a code, and are reported
      to the router rather than drawn by the feature.
- [ ] No joint write outside the robot model: joints are moved through
      `IRobotModel` (`ApplyJointState`, IK, jog), never by assigning
      `ArticulationBody.jointPosition`. `JointWriter` is the only file that does.
- [ ] Joint data from outside (plans, solutions, demonstrations) is checked
      against `IRobotModel.Limits` when it arrives, and execution of a plan
      outside the limits goes through `PlanningPlugin.MayExecute`.
- [ ] No assumption about the robots: no robot count, no model, no joint count, no
      gripper kind. A plugin keeps per-robot state in a session
      (`RobotSessions<T>`) and acts on the owner of the selected object, not on a
      robot captured at registration.
- [ ] No absolute ROS name for per-robot traffic: a robot's topics, services and
      actions are relative names on `robot.Ros`, and poses sent to a robot are in
      that robot's planning frame.
- [ ] No parallel scene representation was introduced.
- [ ] New UI is registered as a tier with an explicit tier assignment, not
      instantiated ad hoc.
