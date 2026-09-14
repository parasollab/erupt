# Writing an ERUPT plugin

ERUPT Core owns everything a robot-programming app always needs: the robot model and IK,
the environment mirror, interaction, selection, modes, undo, the tier UI and the ROS
transport. A **plugin** adds one feature family on top — a planner, a demonstration method,
later grasping or language — without editing core. This document is the contract.

## The contract

```csharp
public interface IEruptPlugin
{
    string Id { get; }                          // "moveit", "mtc", "rader" — others depend on it by this
    string DisplayName { get; }
    IReadOnlyList<string> DependsOn { get; }    // plugin ids that must register first
    void OnRegister(IEruptContext ctx);         // contribute verbs / tabs / widgets, subscribe ROS
    void OnUnregister(IEruptContext ctx);
    void OnModeChanged(AppMode mode);           // Build / Plan / Teach
}
```

Derive from `EruptPluginBehaviour` (a MonoBehaviour with no-op hooks and a `Context`
property) and put it on a prefab. `PluginHost`, on the `ERUPT` root, finds every
`EruptPluginBehaviour` in the scene on Start, orders them by `DependsOn` (a missing id or a
cycle refuses the whole set with a logged error), builds the context from scene services
and calls `OnRegister` in order. Mode changes fan out to every plugin; destroying the host
unregisters in reverse.

## The context

Everything core offers, handed to you at registration:

| Member | Type | Use it for |
|---|---|---|
| `Ros` | `IRosBus` | publish/subscribe/services/actions; a `FakeRosBus` in tests |
| `Robot` | `IRobotModel` | joint names/state, IK, link transforms, end effector |
| `Environment` | `EnvironmentRegistry` | the mirror of scene objects (ids, owners, attach state) |
| `Selection` | `SelectionService` | what is selected and of which kind |
| `Modes` | `ModeManager` | current mode, `Is(AppMode)` |
| `Undo` | `UndoStack` | every mutation you make goes through `Do(IUndoableCommand)` |
| `Router` | `InteractionRouter` | report refusals with a reason (`ReportRefusal`) |
| `Ui` | `IUiHost` | the only way to touch the UI (null in a scene without a tier UI — skip UI work then) |

Never `FindObjectOfType` a core service; take it from the context so tests can substitute it.

## Where a feature goes (Guidelines Part 4)

1. **A verb on an existing selection kind** — the default. `ctx.Ui.RegisterVerb(SelectionKind.EndEffector, "my-verb", "My Verb", handler, Id)`.
   The verb carries your plugin id, so reviewers can see who added it. To implement a verb
   that already exists in the guideline table (e.g. `set-goal`), `BindVerb` it instead.
   `SelectionKind` is closed: you may add verbs to a kind, never a kind.
2. **A tier 3 tab** — configuration and libraries only. `ctx.Ui.AddTab("my-tab", "My Tab", content => ...)`.
   Build with `UiBuilder`; keep it at or under seven interactive elements (Part 8).
   A `CreateCycle` button costs one element where a dropdown would cost two.
3. **An in-world widget** — spatial controls (`ctx.Ui.RegisterWidget`).
4. **Never** a new menu, a fifth tier 1 control, or a second open panel; the registry throws.

Mode rules: gate behaviour in `OnModeChanged`. Demonstration lives in Teach, planning in
Plan, environment editing in Build.

## Families

| Family | Base class | You implement | The base contributes |
|---|---|---|---|
| Blank | `EruptPluginBehaviour` | the three hooks | nothing |
| Planning | `PlanningPlugin` | `SetGoal`, `RequestPlan`, `Preview`, `StopPreview`, `Execute`, `BuildSettingsTab` | `set-goal`/`plan` on the end effector, `preview`/`execute` on trajectories, tab `planner-{Id}`; `PublishResult` makes a plan selectable, `PlaceHandle` puts it in the world |
| Demonstration | `DemonstrationPlugin` | `OnSample`, `StartDemonstration`, `StopDemonstration`, `Publish`, `BuildDemosTab` | Teach-mode gate, sample feed from `InteractionSampleBus` while recording, `correct` verb, tab `demos-{Id}` |

Shipped examples: `com.erupt.plugin.moveit` and `com.erupt.plugin.mtc` (Planning),
`com.erupt.plugin.rader` (Demonstration — a plain-C# recorder on `IRosBus` + `IRobotModel`,
tested with `FakeRosBus` and `FakeRobotModel`).

Two planners can coexist: shared verbs are bound once and dispatched — preview/execute to
the plugin that produced the selected trajectory, set-goal/plan to the first registered
planner whose `AcceptsGoals` is true (a planner that plans on the ROS side says false).

## Messages

Generate your ROS message bindings into `Runtime/Messages` (they compile into
`<Assembly>.Messages`) with the ROS-TCP-Connector message browser, output path set to your
package. Do not regenerate the core packages (std, geometry, sensor, trajectory, shape);
duplicates produce CS0436. Record sources and commit in `MESSAGES.md`.

## Testing

Reference `Erupt.TestSupport` (`FakeRosBus`) and `Erupt.Plugins.Tests` (`FakeUiHost`).
A smoke test registers the plugin against a hand-built `EruptContext`:

```csharp
var ui = new FakeUiHost();
((IEruptPlugin)plugin).OnRegister(new EruptContext { Ros = bus, Ui = ui, Undo = new UndoStack() });
Assert.Contains("planner-demo", ui.Tabs);          // what you contributed
bus.Inbound("/topic", msg);                         // drive your subscriptions
Assert.AreEqual(1, bus.CountOn("/out"));            // what you published
```

`AssemblyBoundaryTests` guards the split: nothing in core may reference `Erupt.Plugins.*`
or `Erupt.App`; your plugin may reference core and the plugins it declares in `DependsOn`.

## Generating a plugin

`ERUPT > Plugins > Create Plugin...` writes the package from a family template and, after
the assembly compiles, creates `Prefabs/<Name>Plugin.prefab` and (optionally) instances it
in the open scene. The generated `README.md` lists the setup steps.
