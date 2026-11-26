# ERUPT Core

Extended Reality Universal Programming Toolkit — the universal half of ERUPT: robot model and
IK (`Erupt.Robot`), environment mirror (`Erupt.Environment`), interaction router and backends
(`Erupt.Interaction.*`), selection and undo, tier UI (`Erupt.Ui`), and the ROS transport
seam (`Erupt.Ros`). Feature families (planning, demonstration) are plugins built on this package.

## Requirements

Unity 6 (6000.3). The registry packages core needs (XR Interaction Toolkit, XR Hands, Input
System, uGUI, XR Core Utils) are dependencies in `package.json`. Two more come from git, and
a package that is itself installed from git cannot declare git dependencies:

- `com.unity.robotics.ros-tcp-connector` (parasollab fork: ROS 2 actions)
- `com.unity.robotics.urdf-importer` (parasollab fork: mimic joints)

Core lists them, pinned, under `"erupt": { "requires": [...] }` in `package.json`. In a
project that lacks them, ERUPT offers to add them to the project manifest when the editor
starts, and `ERUPT > Project Setup` offers them again together with everything else a fresh
project needs (tags, the `ROS2` scripting define, TextMesh Pro's resources, OpenXR settings).
Until the connector is in the project and the `ROS2` scripting define is set, the assemblies
that need them are not compiled (no compile errors in between).
Installing ERUPT into your own project, step by step: `Documentation~/installation.md`.

## Robots

`ERUPT > Robots > Robot Setup…` turns a URDF (with an SRDF and a ROS-names file when there are
any) into a robot prefab, a ghost prefab and a `RobotProfile`. The robots ERUPT ships
(`com.erupt.robot.*`: FR3, UR5e, UR10e, Kinova Gen3, xArm7, LBR iiwa 14) are its output.
`Documentation~/robots.md`.

## In the editor, without a headset

Open a scene that has the ERUPT root (`KitchenFR3`, `MultiArmFR3`, the Starter Scene with a
robot dropped in), start the ROS side and press Play. With no headset connected the root's
**Desktop Controls** take the view: the camera starts looking at the robots, the tier 1 bar
(**Menu**, **Undo**, **Redo**) sits at the top of the Game view, and the mouse and the
keyboard work the same intents the controllers do.

| Input | Does |
|---|---|
| Left click | selects what is under the pointer (an end effector's handle, a link, an obstacle, a plan's handle) and presses menu and panel buttons; a click on nothing clears the selection |
| Right button held, drag | moves what it started on: the arm by its end-effector handle, a joint handle or a link; the selected obstacle |
| Scroll wheel while dragging | pushes the dragged point away or pulls it closer (5 cm a notch) |
| Scroll wheel otherwise | turns the selected joint |
| `Tab`, or a click on **Menu** in the bar | opens and closes the tier 3 panel, square in front of the camera |
| `W` `A` `S` `D` | move the view forward, left, back, right |
| `Q` / `E` | move the view down / up |
| `Shift` (left, held) | move three times as fast |
| Arrow keys, or middle button held and drag | look around |
| `F` | back to a view of the robots, from the side the camera is on |
| `H` | hide or show the hint |

The keys are shown in a corner of the Game view and logged once when the controls start;
**Show Hint** on the root's *Desktop Controls* turns both off, **Desktop Controls** switches
the whole thing off, and every key is a field there. They stay clear of the editor's Game
view shortcuts (which all use Ctrl or Cmd) and of the keys a UI input module acts on (Enter,
Space, Escape); the scenes' XR UI input module takes no keyboard navigation.

The desktop controls **stand down** whenever an XR head device is present: an XR display is
running, or the Input System has a head-mounted display (a headset, or an XR simulator's).
Then the mouse backend raises nothing, the camera is left to its tracked pose driver, tier 1
is on the wrist and tier 3 opens in front of the head. That is the case in every headset
build and in the editor while a headset drives the view; the check runs every frame, so a
headset that is connected or taken away mid-session changes hands cleanly.

The panel's sliders (the **Settings** tab) take the mouse like its buttons: press on a
slider to put its handle there, or drag it. The same tab shows the ROS address the app
connects to; **Change address** opens a keypad whose keys are buttons like any other, so
the address can be changed with the mouse, a ray or a poke while the app runs. It is kept
between sessions and replaces the address set under **Robotics > ROS Settings** until that
one changes.

Not on the desktop: turning an obstacle (a drag moves it and keeps its orientation; Resize
has its own − / + buttons, which the mouse presses), two-handed scaling, a second pointer
(two arms cannot be dragged at once), hand tracking and haptics.

## Layout

```
Runtime/Interaction   Erupt.Interaction.Core (+ UserSettings; Backends/OpenXR incl. WristAnchor, CollisionHaptics; Backends/Desktop: DesktopMouseBackend, DesktopControls)
Runtime/Ros           Erupt.Ros          IRosBus, RosBus, LiveRosBus, RosName, NamespacedRosBus, conversions; IRosEndpoint (where the bus connects, and ConnectTo), RosAddress
Runtime/Robot         Erupt.Robot        RobotProfile, RobotInstance, RobotRegistry (any number of robots), DirectArticulationIKController, RobotInteractionController + RobotIntentDispatcher, trajectory replay, RobotGhosts
Runtime/Environment   Erupt.Environment  EnvironmentRegistry/Object, obstacle factory + undoable commands, grab transformers
Runtime/Ui            Erupt.Ui           tier UI building blocks (UiBuilder incl. the slider), VerbRegistry, IUiHost (TierUiRig), SettingsTab
Runtime/Plugins       Erupt.Plugins      IEruptPlugin, PluginHost, IEruptContext, PlanningPlugin, DemonstrationPlugin; RosConnectionSection + RosEndpointSetting (the ROS address on the Settings tab)
Editor                Erupt.Core.Editor  plugin generator and templates, Robot Setup (Editor/RobotSetup), RobotProfileCreator
Editor/Setup          Erupt.Setup.Editor the offer to add missing packages, Project Setup, Collada materials for the project's render pipeline (references no other package)
Samples~/StarterScene the Starter Scene sample: ERUPT root, XR rig, the three plugins, a floor and a table, no robot
Prefabs/EruptCore     the ERUPT root: PluginHost + SelectionService/Highlighter/RouterBinding + EnvironmentRegistry + TierUiRig + the desktop controls
Tests/Support         Erupt.TestSupport  FakeRosBus, FakeRobotModel, FakeRobots, TestInteractionSource, IdleRosConnection (UNITY_INCLUDE_TESTS)
Tests/*               per-assembly PlayMode tests; Tests/Boundary (the package boundary), Tests/Editor and Tests/Ros are EditMode
```

Core assemblies never reference `Erupt.Plugins.*` or `Erupt.App` (enforced by `AssemblyBoundaryTests`).

## Documentation

- `Documentation~/installation.md` — adding ERUPT to your own project, Project Setup, the Starter Scene
- `Documentation~/robots.md` — the shipped robots, adding one to a scene, Robot Setup for a robot of your own
- `Documentation~/architecture.md` — packages, assembly graph, the `ERUPT` root, flows
- `Documentation~/plugin-authoring.md` — the plugin contract, context, families, messages, testing, generator
- `Documentation~/design-guidelines.md` — the UI and interaction rules every feature is reviewed against

Licence: BSD 3-Clause (`LICENSE.md`). Changes: `CHANGELOG.md`.
