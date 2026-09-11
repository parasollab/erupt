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
3. **Robot.** The plugin receives the robot through `IEruptContext.Robot`; nothing to assign
   unless `{{PluginName}}Plugin` declares its own serialised references.
4. **Tests.** Open the Test Runner and run `{{AssemblyName}}.Tests` (PlayMode): the smoke test
   registers the plugin against a `FakeRosBus` context.
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
