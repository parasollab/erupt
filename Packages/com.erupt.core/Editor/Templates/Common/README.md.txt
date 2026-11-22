# ERUPT {{DisplayName}} Plugin

A `{{Family}}` plugin for ERUPT (Extended Reality Universal Programming Toolkit), generated
from the `{{Family}}` template. Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`.

## Setup

1. **Messages.** If the plugin talks to ROS with its own message types, generate them into
   `Runtime/Messages` (see `Runtime/Messages/README.md`) and record them in `MESSAGES.md`.
   Core types (std, geometry, sensor, trajectory, shape) are already available.
2. **Scene.** Drop `Prefabs/{{PluginName}}Plugin.prefab` into the scene next to the `ERUPT`
   root (the generator's "Add to open scene" does this). `PluginHost` on `ERUPT` finds it on
   Start and registers it in dependency order.
3. **Robots.** A scene may hold any number of robots. The plugin finds them through
   `IEruptContext.Robots` and keeps what it needs per robot in a session
   (`RobotSessions<T>`: one per robot, created when a robot joins and disposed when it
   leaves); verbs act on the robot that owns the selected object. Nothing to assign unless
   `{{PluginName}}Plugin` declares its own serialised references. Send a robot's own ROS
   traffic through `robot.Ros` with relative names, so it lands in that robot's namespace.
4. **Tests.** Open the Test Runner and run `{{AssemblyName}}.Tests` (PlayMode): the smoke tests
   register the plugin against a `FakeRosBus` context, once without a robot and once with
   three fake robots (`FakeRobots.Three`).
5. **Implement.** Fill the `TODO` hooks in `Runtime/{{PluginName}}Plugin.cs`. Add features as
   verbs on existing selection kinds (`Context.Ui.RegisterVerb`) or as a tier 3 tab, never as a
   new menu (Guidelines Part 2, Part 4).

## Layout

```
Runtime/{{PluginName}}Plugin.cs      the plugin ({{AssemblyName}})
Runtime/Messages/                    generated ROS messages ({{AssemblyName}}.Messages)
Tests/{{PluginName}}SmokeTests.cs    registration smoke test
Prefabs/{{PluginName}}Plugin.prefab  created by the generator after compilation
```
