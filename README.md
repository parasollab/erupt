# ERUPT

**Extended Reality Universal Programming Toolkit** — a Unity XR front end for programming
robots: build the environment, set goals and plan with MoveIt 2, browse and execute MoveIt
Task Constructor solutions, and record demonstrations, all from a headset talking to a ROS 2
host. ERUPT Core owns everything such an app always needs; each planning or
learning-from-demonstration method is a **plugin** built on a template, so adding a method
never means editing core.

Reference app: `Assets/Scenes/KitchenFR3.unity` (a Franka FR3 in a kitchen), entered through
`KitchenLoadingSceneFR3.unity`. Target device: Meta Quest 3 over OpenXR; a desktop backend
exists for Editor use.

## Architecture

```
Packages/com.erupt.core            ERUPT Core: robot model + IK, environment mirror, interaction router
                                   and backends, selection, modes, undo, tier UI, ROS transport, plugin contract
Packages/com.erupt.plugin.moveit   MoveIt 2 kinematic planning + planning-scene sync      (Planning family)
Packages/com.erupt.plugin.mtc      MoveIt Task Constructor solutions + pick/place study   (Planning family, needs moveit)
Packages/com.erupt.plugin.rader    RADER demonstration record / replay / publish, FERL    (Demonstration family)
Assets/Erupt.App                   the reference app's scene-specific scripts only
Packages/ROS-TCP-Connector         parasollab fork (submodule, branch action-support: ROS 2 actions)
Packages/URDF-Importer             parasollab fork (submodule: mimic joints)
```

In the scene, one `ERUPT` root (`EruptCore.prefab`) carries `PluginHost`, `SelectionService`,
`ModeManager` (Build / Plan / Teach), `EnvironmentRegistry` and `TierUiRig`. Each plugin is a
prefab next to it; `PluginHost` finds the plugins on Start, orders them by dependency and hands
each one a context (ROS bus, robot model, environment, selection, modes, undo, UI host).
Core assemblies never reference a plugin; an EditMode test enforces that.

Full picture: [`Packages/com.erupt.core/Documentation~/architecture.md`](Packages/com.erupt.core/Documentation~/architecture.md).
Design rules the UI follows: [`design-guidelines.md`](Packages/com.erupt.core/Documentation~/design-guidelines.md).

## Quick start

1. **Clone with submodules.** The submodules use SSH URLs on the `parasollab` organisation.

   ```bash
   git clone --recurse-submodules git@github.com:parasollab/erupt.git
   # HTTPS-only machines: git config --global url."https://github.com/".insteadOf git@github.com:
   ```

2. **Open in Unity** (version in `ProjectSettings/ProjectVersion.txt`, Unity 6). The four
   `com.erupt.*` packages are embedded and resolve on first open.

3. **Point at your ROS host.** Set the ROS IP on the `ROSConnection` prefab
   (`Robotics > ROS Settings`; the prefab under `Assets/Resources` is git-ignored and per machine).

4. **Run.** Build Settings already lists `KitchenLoadingSceneFR3` → `KitchenFR3`. Play in the
   Editor with the desktop backend, or build for Android (Quest 3).

5. **Tests.** Window > General > Test Runner: every `Erupt.*.Tests` suite runs in PlayMode
   against `FakeRosBus` (no ROS needed); `Erupt.Boundary.Tests` and `Erupt.Core.Editor.Tests`
   run in EditMode.

## Using the app

- **Tier 1** (wrist bar): mode indicator (Build / Plan / Teach), Undo, Redo.
- **Tier 2** (contextual menu on the selected thing): obstacle → resize, delete, snap, duplicate;
  end effector → set goal, plan; trajectory → preview, execute (and `correct` in Teach).
- **Tier 3** (summoned with the controller Menu button, one panel at a time): the **Scene** tab
  (place cube / sphere / cylinder), **Planner settings** (MoveIt), **MTC** (stage tree, ranked
  solutions, pick/place recorder) and **RADER** (record / replay / publish demonstrations).

The core flow is: place an obstacle → select the end effector → set goal → plan → preview
the ghost → execute. MTC adds record a pick/place task → browse solutions → preview → execute.
Teach mode adds record a demonstration → replay → publish.

## ROS 2 host

Packages live in the companion `erupt_ws` workspace (`planning_scene_utils`, `study_*`, the
`moveit_task_constructor` demo). Start order for the reference app:

```bash
# 1. MoveIt + MTC demo (move_group with execute_task_solution, robot state publisher, pick_place_dynamic_demo)
ros2 launch moveit_task_constructor_demo run.launch.py exe:=pick_place_dynamic_demo

# 2. Planning-scene mirror → Unity (/collision_objects_ros, /attached_collision_objects_ros) + monitors
ros2 launch planning_scene_utils unity_monitor.launch.py

# 3. TCP bridge for the headset (port 10000)
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

MoveIt-only sessions can replace step 1 with your robot's `move_group` launch and step 2 with
`ros2 run planning_scene_utils planning_scene_watcher`. Every topic, service and action the MTC
plugin uses is listed in
[`Packages/com.erupt.plugin.mtc/Documentation~/ros-interface.md`](Packages/com.erupt.plugin.mtc/Documentation~/ros-interface.md);
each plugin's `MESSAGES.md` records which ROS packages its generated C# bindings came from.

## Plugins

| Package | Family | What it adds | Prefab |
|---|---|---|---|
| `com.erupt.plugin.moveit` | Planning | `/plan_kinematic_path` planning, planner discovery, trajectory execution, planning-scene sync | `MoveItPlugin.prefab` |
| `com.erupt.plugin.mtc` | Planning | MTC introspection, ranked solutions, `/execute_task_solution` and `/pick_place` actions, pick/place recorder | `MtcPlugin.prefab` |
| `com.erupt.plugin.rader` | Demonstration | joint-space demonstration recording, replay, publish; FERL feedback; hand and gripper mirroring | `RaderPlugin.prefab` |

A plugin is a prefab dropped next to the `ERUPT` root. Each package has a `README.md` with its
setup steps, a `MESSAGES.md`, a `CHANGELOG.md` and tests against the core fakes.

## Creating a plugin

`ERUPT > Plugins > Create Plugin...` in the Editor scaffolds `Packages/com.erupt.plugin.<name>`
from the Blank, Planning or Demonstration template: package manifest, runtime and message
assemblies, a plugin class with `TODO` hooks, a smoke test, a README with numbered setup steps,
and (after compilation) the plugin prefab, optionally instanced in the open scene. The
contract, the context, where a feature goes (a verb on an existing selection kind first, a
tier 3 tab second, never a new menu) and how to test with `FakeRosBus` are in
[`plugin-authoring.md`](Packages/com.erupt.core/Documentation~/plugin-authoring.md).

## Repository layout

```
Assets/Scenes             KitchenLoadingSceneFR3, KitchenFR3 (the reference app)
Assets/Erupt.App          Erupt.App assembly: scene helpers (prewarm, reset, lighting, FPS)
Assets/Erupt.App/Editor   Erupt.App.Editor: migration tools used during the refactor
Packages/com.erupt.*      the ERUPT packages (see Architecture)
refactor/                 design and refactor history (index in refactor/README.md)
.github/workflows         optional CI (boundary greps on push; Unity tests on demand, needs a licence secret)
```

## Licence

BSD 3-Clause, © Parasol Laboratory, University of Illinois Urbana-Champaign. See `LICENSE`
(each package carries its own `LICENSE.md`). The ROS-TCP-Connector and URDF-Importer forks keep
their upstream Apache 2.0 licences.
