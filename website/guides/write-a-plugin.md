# Writing a plugin

A plugin adds one family of features to ERUPT, such as a planner or a way of learning from
demonstration, without changing the core. The MoveIt, MTC and demonstration features that
ship with ERUPT are plugins and use nothing a plugin of yours cannot use.

This page gets you to a working plugin. The full contract is in the
[plugin authoring reference](../../Packages/com.erupt.core/Documentation~/plugin-authoring.md).

## Create one

1. In Unity choose **ERUPT > Plugins > Create Plugin...**.
2. Pick a template:

   | Template | Start from it when |
   | --- | --- |
   | **Planning** | Your plugin turns a goal into a motion the user previews and executes |
   | **Demonstration** | Your plugin records what the user does with the robot |
   | **Blank** | Anything else |

3. Enter a name. ERUPT creates `Packages/com.erupt.plugin.<name>` with a package manifest, the
   plugin class with `TODO` markers where your code goes, an assembly for ROS message types, a
   smoke test, a README, and the plugin prefab. It can also put the prefab into the open
   scene.

A plugin is active in a scene when its prefab sits next to the `ERUPT` root object.

## What a plugin gets

When the app starts, ERUPT hands every plugin a *context*:

| From the context | You get |
| --- | --- |
| `Robots` | The robots in the scene, however many there are, and which one the user is working with |
| `Ros` | The connection to ROS |
| `Environment` | The obstacles and objects in the scene |
| `Selection` | What the user has selected |
| `Undo` | The undo stack; every change you make goes through it |
| `Ui` | The user interface: add actions to selected things, add a tab to the panel |
| `Router` | A way to tell the user that something cannot be done, and why |

## Four rules that keep plugins working together

1. **Take everything from the context.** Do not search the scene for robots or services, and
   do not open your own ROS connection. Tests replace the context with fakes.
2. **Expect any number of robots.** Keep what you need per robot in a session
   (`RobotSessions<T>` creates and disposes them for you), and act on the robot that owns the
   thing the user selected.
3. **Use relative ROS names on the robot's connection.** `robot.Ros` puts `joint_states` under
   that robot's namespace. A name with a leading `/` is the same for every robot, which is
   only right for things that belong to no robot.
4. **Add features where the user already looks.** First choice: an action on a kind of thing
   the user can already select. Second choice: one tab in the panel. Never a new menu. The
   [design guidelines](../../Packages/com.erupt.core/Documentation~/design-guidelines.md) explain why.

## A planning plugin in outline

The Planning template derives from `PlanningPlugin`, which provides the user-facing flow:
the goal, plan, preview and execute actions, the settings tab, and the handle a finished plan
is selected by. It also has the helpers for joint limits: call `CheckLimits` on a plan when it
arrives, and `MayExecute` first in `Execute` (the template does), which refuses a plan that
leaves the limits and tells the user why. Showing the preview is yours to implement. You fill
in what is specific to your planner:

```csharp
public sealed class Session : IDisposable
{
    public RobotInstance Robot { get; }

    public Session(RobotInstance robot)
    {
        Robot = robot;
        // "plan" becomes /plan, or /left/plan for a robot in namespace "left".
        robot.Ros.RegisterRosService<MyPlanRequest, MyPlanResponse>("plan");
    }

    public void Dispose() { }
}

private RobotSessions<Session> sessions;

protected override void OnRegister(IEruptContext ctx)
{
    sessions = new RobotSessions<Session>(ctx.Robots, robot => new Session(robot));
    base.OnRegister(ctx);
}
```

The generated class shows the remaining hooks: receive the goal for a robot, send the request,
and publish the result as a `PlanResult`.

## Test without ROS

`Erupt.TestSupport` provides `FakeRosBus` (records what you publish and lets a test answer
service calls), `FakeRobotModel` and `FakeRobots` (one or several robots without physics).
The generated smoke test uses three fake robots; extend it. All tests of the shipped plugins
run this way, with no ROS installation.

## ROS message types

Generate C# classes for your message types with **Robotics > Generate ROS Messages...** and put
them in the plugin's `Runtime/Messages` folder. The plugin's `MESSAGES.md` records which ROS
packages they came from.
