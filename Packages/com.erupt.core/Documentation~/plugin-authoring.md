# Writing an ERUPT plugin

ERUPT Core owns everything a robot-programming app always needs: the robot model and IK,
the environment mirror, interaction, selection, undo, the tier UI and the ROS
transport. A **plugin** adds one feature family on top — a planner, a demonstration method,
later grasping or language — without editing core. This document is the contract.

## The contract

```csharp
public interface IEruptPlugin
{
    string Id { get; }                          // "moveit", "mtc", "demonstration" — others depend on it by this
    string DisplayName { get; }
    IReadOnlyList<string> DependsOn { get; }    // plugin ids that must register first
    void OnRegister(IEruptContext ctx);         // contribute verbs / tabs / widgets, subscribe ROS
    void OnUnregister(IEruptContext ctx);
}
```

That is the whole interface. ERUPT has no application modes, so there is no mode hook and
nothing to gate on one: what a plugin offers is offered whenever it can work, as a button the
user presses, and what cannot work right now refuses with its reason. (Plugins written for
the earlier contract: delete `OnModeChanged`, `IEruptContext.Modes` and any "switch to …
mode first" refusal, and let the button that used to be greyed out start the thing.)

Derive from `EruptPluginBehaviour` (a MonoBehaviour with no-op hooks and a `Context`
property) and put it on a prefab. `PluginHost`, on the `ERUPT` root, finds every
`EruptPluginBehaviour` in the scene on Start, orders them by `DependsOn` (a missing id or a
cycle refuses the whole set with a logged error), builds the context from scene services
and calls `OnRegister` in order. Destroying the host unregisters in reverse.

## The context

Everything core offers, handed to you at registration:

| Member | Type | Use it for |
|---|---|---|
| `Ros` | `IRosBus` | publish/subscribe/services/actions for names that belong to no robot; a `FakeRosBus` in tests |
| `Robots` | `IRobotRegistry` | the robots in the scene, any number of them: `All`, `TryGet(id)`, `OwnerOf(gameObject)`, `Active` (the robot the user last selected), the `Added` / `Removed` / `ActiveChanged` events. A `RobotInstance` gives you its `Id`, `Model` (`IRobotModel`: joints, IK, links), `Gripper` (null without one), `Profile` (groups, joints, gripper, ROS names), `Names` (ROS ↔ Unity joint and link names), `Ros` (the bus in the robot's namespace), and `PlanningFrame` / `PlanningFrameId` (the Unity pose and the frame id of the frame its ROS side plans in) |
| `Environment` | `EnvironmentRegistry` | the mirror of scene objects (ids, owners, attach state) |
| `Selection` | `SelectionService` | what is selected and of which kind |
| `Undo` | `UndoStack` | every mutation you make goes through `Do(IUndoableCommand)` |
| `Router` | `InteractionRouter` | report refusals (`ReportRefusal`): a reason, a `RefusalCode`, and where there is one the object it is about. The toast, the joint tint and the controller pulse listen there; repeats within half a second are dropped, so reporting every frame is fine |
| `Ui` | `IUiHost` | the only way to touch the UI (null in a scene without a tier UI — skip UI work then) |

Never `FindObjectOfType` a core service; take it from the context so tests can substitute it.

The same goes for ROS: a plugin and the components it owns never read `RosBus.Instance`.
Give each component an `Initialise(IRosBus)` method, hand it the bus it belongs on, and keep
the component idle (no subscribe, no publish) until then. A boundary test and a CI step
reject `RosBus.Instance` anywhere but in `PluginHost`.

Name topics, services and actions the ROS way: a leading `/` is absolute, anything else is
relative. Whatever belongs to one robot (its joint states, its planner, its controllers, its
planning scene) is a **relative** name sent on that robot's bus, `robot.Ros`, which resolves
it under the robot's namespace: `joint_states` goes out as `/joint_states` for a robot
without a namespace and as `/left/joint_states` for one in `left`. Whatever is global is an
absolute name on `ctx.Ros`. An absolute name for per-robot traffic is a bug: it works with
one robot and sends every robot's traffic to the same place with two. Take robot-specific
strings (planning group, controller topic, joint and link names on the ROS side) from
`robot.Profile` and `robot.Names`, never from your own settings, and express every pose you
send in that robot's planning frame (`robot.PlanningFrame`, stamped `robot.PlanningFrameId`).
Do not look robots up in the scene; `ctx.Robots` is the only source.

## Any number of robots: one plugin, per-robot sessions

A scene holds one, two, three or more robots, of the same or different models, each in its
own ROS namespace with its own planner, controllers and planning scene. A plugin is still
**one** instance with one id, one `DependsOn` and one tab. What it keeps per robot lives in a
session, and `RobotSessions<T>` keeps one session per robot for you:

```csharp
public sealed class Session : IDisposable
{
    public RobotInstance Robot { get; }
    public Session(RobotInstance robot)
    {
        Robot = robot;
        robot.Ros.RegisterRosService<MyRequest, MyResponse>("plan");   // /<namespace>/plan
        robot.Ros.Subscribe<JointStateMsg>(robot.JointStatesTopic, OnJointState);
    }
    public void Dispose() { /* unsubscribe from Robot.Ros */ }
}

private RobotSessions<Session> sessions;

protected override void OnRegister(IEruptContext ctx)
{
    // One per robot now, one for every robot that joins later, disposed when a robot leaves.
    // Return null for a robot you cannot serve (and say so once, here).
    sessions = new RobotSessions<Session>(ctx.Robots, robot => robot.Ros != null ? new Session(robot) : null);
    base.OnRegister(ctx);
}

protected override void OnUnregister(IEruptContext ctx)
{
    sessions?.Dispose();
    base.OnUnregister(ctx);
}
```

- `sessions.For(robot)`, `sessions.For(selectable)` (the owner of the selected object, the
  active robot when it belongs to none), `sessions.Active`, `sessions.Robots`, enumeration.
- **Act on the owner of the target, not on "the" robot.** A verb handler gets the selected
  object; the robot is `ctx.Robots.OwnerOf(target.GameObject)`, falling back to
  `ctx.Robots.Active`. The family base classes do this for their verbs and hand you the robot.
  Never capture `Robots.Active` at registration.
- **The tab shows the active robot.** The family tabs are rebuilt when the active robot
  changes and their title names it while the scene has several robots ("MoveIt · left");
  `BuildSettingsTab` / `BuildDemosTab` is called again, so build from `sessions.Active`.
  For a tab of your own use `ActiveRobotTab`.
- **A robot the plugin cannot serve does not break the others**: no session, a refusal with a
  reason when the user tries, one log line. MTC keeps a session for a robot whose ROS side has
  no pick-and-place server and shows "Pick-and-place not available for this robot".
- **MonoBehaviour helpers** that a plugin prefab carries once (a player, a client) serve the
  first robot; `SessionComponents.Copy(template, host)` makes a copy with the same serialized
  settings for each further robot.
- With one robot in the root namespace all of this reduces to what a single-robot plugin does:
  the same absolute wire names, one session, a tab titled with the plugin's name.

Put every type in your assembly's root namespace (`Erupt.Plugins.<Name>`); a boundary test
fails on a type in an `Erupt.*` assembly that is outside the `Erupt` namespaces.

## Moving the robot, and joint limits

A plugin moves a robot only through its `IRobotModel`; a boundary test and CI fail when
runtime code outside `JointWriter` assigns `ArticulationBody.jointPosition`.

```csharp
public interface IRobotModel
{
    IJointLimits Limits { get; }                                        // never null
    JointWriteReport ApplyJointState(string[] names, double[] positions);
    JointWriteReport ApplyJointState(IList<string> names, IList<float> positions);
    bool TryGetJointLink(string jointName, out Transform link);         // the link a joint moves
    // ... joints, joint state, IK, link lookup
}
```

- Every write is clamped to the joint's limits. `ApplyJointState` tells you what it did: the
  `JointWriteReport` lists the joints that were clamped (`Clamped`: joint, requested, applied)
  and the names the robot does not have (`Unknown`); `IsClean` when it wrote what was asked.
- `Limits` (`IJointLimits`) answers questions without moving anything: `TryGet(joint, out
  JointLimit)`, `Clamp`, `StepToward`, and `Validate(trajectory, tolerance)` /
  `Validate(names, positions, tolerance)`, which return one `JointLimitViolation` per joint
  that leaves its limits (worst point, excess, how many points). Continuous joints and joints
  without a range have no position limits.
- **Data from outside is checked once, when it arrives.** A planner calls
  `CheckLimits(result, robot)` after `PublishResult` (pass every trajectory of a multi-step
  solution): the violations are stored in `PlanResult.LimitViolations` and the user is told
  which joint. The preview needs nothing special, since the robot model clamps it.
- **A plan that does not come, and an execution that does not happen, say why.** The plan
  verb only starts `RequestPlan`: report a refusal with the reason (`Report(...)`) on every
  path that hands `done` a null (no session for the robot, start or goal missing, the planner
  refused). `Execute` reports through its `status` callback, and the execute verb turns
  `Unavailable`, `Failed` and `Aborted` into refusals at the plan's handle with the status's
  message (`ExecuteAndReport`, which a tab's own execute button can call too), so give every
  such status a sentence the user can act on.
- **Execution of a plan outside the limits is refused.** Start `Execute` with
  `if (!MayExecute(plan, robot, status)) return;`: the status gets `ExecutionPhase.Refused`
  and the refusal goes to the router, unless the plugin's serialized
  `allowExecutionOutsideLimits` is on (off by default; `limitTolerance` defaults to 1e-3 rad
  and 1e-4 m). Gate the transport as well if it can be reached without the plugin, as
  `MoveItPlanningClient.Execute` and `PickPlaceClient.ExecutionGuard` do.
- **Taking the robot's pose.** Whatever starts to drive the robot locally (a preview, a
  replay) calls `robot.Model.TakeControl()` first, as `BeginInteraction()` does for the user's
  drags; `JointTrajectoryPlayer` does it for you. Whatever feeds the robot from outside (a
  joint-state mirror) listens to `IRobotModel.ControlTaken` and stops, as the MoveIt plugin's
  mirror does. Two writers that do not know of each other make the robot flicker between two poses.
- **Mirrored joint states** (the real robot's `joint_states`) are written like any other
  state and come back clamped. Hand the report to a `LimitMismatchMonitor`, which raises one
  `LimitMismatch` refusal per joint instead of one per message, and forward that to
  `ctx.Router.ReportRefusal`.

## Moving a robot's base

`robot.PlaceBase(pose, out reason)` puts the robot's base at a world pose and raises
`robot.BaseMoved` (the robot, the pose the base had). The robot's planning frame moves with
it, so every pose you sent in that frame, or made for the old base, is stale:

- A planner gets `OnRobotBaseMoved(robot, previousBase)` from `PlanningPlugin` after the base
  class has stopped the robot's preview and dropped its plans: drop the goal, the recorded
  task and whatever else was made for the old base, and send again what the robot's ROS side
  holds in the robot's planning frame. Any other plugin subscribes to `BaseMoved` per robot
  (in its session) and does the same.
- Say when the base has to stay: a planner overrides `WhyBaseMustStay(robot)` (a sentence for
  the user, or null); any other plugin calls `robot.AddBaseGuard(() => reason)` and removes
  the guard with its session. `PlaceBase` is refused with that reason. MoveIt refuses while
  the robot executes or carries an object, MTC while it plans or executes a task.
- To move a base yourself, hand a `PlaceBaseCommand(robot, pose)` to `ctx.Undo.Do`: undo puts
  the base back. Check `robot.CanPlaceBase(out reason)` first and report the refusal.
  `robot.Ghosts.ShowPlacementGhost(pose)` shows where the robot would go before the user
  confirms; `com.erupt.plugin.ar` is the example.

## Where a feature goes (Guidelines Part 4 — `design-guidelines.md` in this folder)

1. **A verb on an existing selection kind** — the default. `ctx.Ui.RegisterVerb(SelectionKind.EndEffector, "my-verb", "My Verb", handler, Id)`.
   The verb carries your plugin id, so reviewers can see who added it. To implement a verb
   that already exists in the guideline table (e.g. `set-goal`), `BindVerb` it instead; a
   table verb that is marked pending, or that nothing has bound, is not on the menus.
   `SelectionKind` is closed: you may add verbs to a kind, never a kind.
   A verb that only makes sense for some selections of its kind gets a condition:
   `ctx.Ui.Verbs.ShowWhen(kind, "my-verb", target => ...)` lists it only where the condition
   says yes; call `ctx.Ui.Verbs.Refresh()` when the answer may have changed. The handler
   still refuses with a reason when it cannot run: a menu may be open while the answer changes.
2. **A tier 3 tab** — configuration and libraries only. `ctx.Ui.AddTab("my-tab", "My Tab", content => ...)`.
   Build with `UiBuilder`; keep it at or under seven interactive elements (Part 8).
   A `CreateCycle` button costs one element where a dropdown would cost two.
   Tabs stand in the strip by `PanelTab.Order` (lower is further left; the Scene tab is 0,
   MoveIt 10, MTC 20, Demonstrations 30, AR 40, anything else 100), not by the order the
   plugins start in. Set it on the tab `AddTab` returns, or override `TabOrder` in a
   `PlanningPlugin` or `DemonstrationPlugin`. Take the tab away again in `OnUnregister`
   (`ctx.Ui.RemoveTab("my-tab")`; the family base classes do it for theirs), so that a module
   that is gone leaves no tab behind. A value in a range gets `UiBuilder.CreateSlider`
   (label, range, step, how the value reads); it costs one element. A preference of the
   person using the app, rather than a part of the task, belongs on the Settings tab, the
   last of the strip: a component contributes a section with
   `TierUiRig.Settings.AddSection(id, column => ...)` and keeps the value through
   `UserSettings`.
3. **An in-world widget** — spatial controls (`ctx.Ui.RegisterWidget`).
4. **Never** a new menu, a fifth tier 1 control, or a second open panel; the registry throws.

No modes: do not gate behaviour on a global state of the app, and do not add one. A state
that lasts (a recording, a preview) is started and ended by the same control, which shows
it meanwhile. A guideline verb you implement with `BindVerb` stays bound after you are
unregistered, so give it a condition that is true only while you are registered
(`ctx.Ui.Verbs.ShowWhen(kind, verb, _ => IsRegistered)`); the verbs you add with
`RegisterVerb` are removed for you.

## Families

| Family | Base class | You implement | The base contributes |
|---|---|---|---|
| Blank | `EruptPluginBehaviour` | the two hooks | nothing |
| Planning | `PlanningPlugin` | `SetGoal(robot, endEffector)`, `RequestPlan(robot, preferences, done)`, `Preview(plan)`, `StopPreview(robot)` (null = every robot), `Execute(plan, status)`, `BuildSettingsTab`; optionally `Serves(robot)` | `set-goal`/`plan` on the end effector (for the robot that owns it), `preview`/`execute` on trajectories (for the robot the plan names), all four on the menus only while a planner is registered, tab `planner-{Id}` showing the active robot (removed on unregister); `PublishResult(result, robot)` makes a plan selectable and stamps `PlanResult.RobotId`, `RobotOf(plan)` gives the robot back, `PlaceHandle` puts it in the world; `CheckLimits` / `MayExecute` flag and gate plans outside the joint limits; `ClearResults(robot)` drops one robot's plans |
| Demonstration | `DemonstrationPlugin` | `OnSample`, `StartDemonstration(robot)`, `StopDemonstration(robot)` (null = whichever is running), `Publish(robot)`, `BuildDemosTab` | sample feed from `InteractionSampleBus` while a demonstration is being recorded, `correct` verb on a plan (on the menu while the plugin is registered; starts a demonstration on the plan's robot and opens the demos tab), tab `demos-{Id}` showing the active robot (removed on unregister), `Refuse(reason, target)` |

The overloads without a robot (`RequestPlan(preferences, done)`, `StartDemonstration()`, …)
address the active robot.

Shipped examples: `com.erupt.plugin.moveit` and `com.erupt.plugin.mtc` (Planning),
`com.erupt.plugin.demonstration` (Demonstration — a plain-C# recorder on `IRosBus` + `IRobotModel`,
tested with `FakeRosBus`, `FakeRobotModel` and `FakeRobots`), `com.erupt.plugin.ar` (Blank: a verb with a
condition, a session per robot, no ROS traffic of its own).

Two planners can coexist: shared verbs are bound once and dispatched — preview/execute to
the plugin that produced the selected trajectory, set-goal/plan to the first registered
planner whose `AcceptsGoals` is true (a planner that plans on the ROS side says false) and
which `Serves` the robot the selected end effector belongs to. A robot no planner serves
gets a refusal.

## Messages

Generate your ROS message bindings into `Runtime/Messages` (they compile into
`<Assembly>.Messages`) with the ROS-TCP-Connector message browser, output path set to your
package. Do not regenerate the core packages (std, geometry, sensor, trajectory, shape);
duplicates produce CS0436. Record sources and commit in `MESSAGES.md`.

## Testing

Reference `Erupt.TestSupport` (`FakeRosBus`, `FakeRobotModel`, `FakeRobots`) and `Erupt.Plugins.Tests` (`FakeUiHost`).
`new FakeRobotModel("j1").WithLimits(new JointLimit("j1", JointKind.Revolute, -1f, 1f))` clamps
and reports like the real controller, for tests of plans and joint states outside the limits.
A smoke test registers the plugin against a hand-built `EruptContext`:

```csharp
var ui = new FakeUiHost();
var robots = FakeRobots.Single(bus, new FakeRobotModel("j1"), rosNamespace: "left");   // one robot, in /left
((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack() });
Assert.Contains("planner-demo", ui.Tabs);          // what you contributed
bus.Inbound("/left/joint_states", msg);             // drive your subscriptions
Assert.AreEqual(1, bus.CountOn("/left/out"));       // what you published
FakeRobots.DestroyAll(robots);                      // in teardown
```

`FakeRobots.Add(registry, id, rosNamespace, model, profile)` registers further robots, and
`bus.Root` is the fake bus as a robot without a namespace sees it, for components you test on
their own.

**Test with three robots, not one and not two.** `FakeRobots.Three(bus)` registers `left`,
`right` and `third`, each in the namespace of its id on the one fake bus (three, so that
nothing passes by assuming two), and `FakeRobots.EndEffectorOf(robot)` gives a selectable the
robot owns:

```csharp
RobotRegistry robots = FakeRobots.Three(bus);
((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Robots = robots, Ui = ui, Undo = new UndoStack() });

CollectionAssert.Contains(bus.RegisteredServices, "/third/plan");        // every robot's names, in its namespace
ui.Bound["set-goal"](FakeRobots.EndEffectorOf(robots.All[1]));           // a verb on the right robot's end effector
Assert.IsTrue(plugin.Sessions.For(robots.All[1]).HasGoal);               // reached the right robot's session
Assert.IsFalse(plugin.Sessions.For(robots.All[0]).HasGoal);              // and no other
robots.SetActive(robots.All[2]);
Assert.AreEqual("Demo · third", ui.TabsById["planner-demo"].Label);     // the tab follows the active robot
```

Also keep one test with a single robot in the root namespace that pins the absolute wire
names (`/plan`, `/joint_states`): that is the contract with a single-robot ROS setup.

A fake bus cannot tell you that the ROS side agrees with you about names, namespaces, frames
or message layouts. `Assets/Erupt.App/Tests/Live` runs the reference scenes against a real
stack (opt-in; see its README): add your plugin's flow there when it talks to ROS.

`AssemblyBoundaryTests` guards the split: nothing in core may reference `Erupt.Plugins.*`
or `Erupt.App`; your plugin may reference core and the plugins it declares in `DependsOn`.

## Generating a plugin

`ERUPT > Plugins > Create Plugin...` writes the package from a family template and, after
the assembly compiles, creates `Prefabs/<Name>Plugin.prefab` and (optionally) instances it
in the open scene. The generated `README.md` lists the setup steps.
