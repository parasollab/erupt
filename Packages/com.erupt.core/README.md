# ERUPT Core

Extended Reality Universal Programming Toolkit — the universal half of ERUPT: robot model and
IK (`Erupt.Robot`), environment mirror (`Erupt.Environment`), interaction router and backends
(`Erupt.Interaction.*`), selection, modes and undo, tier UI (`Erupt.Ui`), and the ROS transport
seam (`Erupt.Ros`). Feature families (planning, demonstration) are plugins built on this package.

## Requirements

Two packages are installed as local `file:` dependencies from the parasollab forks and are not
declared in `package.json` (a version dependency would make UPM look for them in the registry):

- `com.unity.robotics.ros-tcp-connector` — `Packages/ROS-TCP-Connector` (branch `action-support`)
- `com.unity.robotics.urdf-importer` — `Packages/URDF-Importer`

## Layout

```
Runtime/Interaction   Erupt.Interaction.Core (+ Backends/OpenXR incl. CollisionHaptics, Desktop, VisionOS)
Runtime/Ros           Erupt.Ros          IRosBus, RosBus, LiveRosBus, conversions, FPS publisher
Runtime/Robot         Erupt.Robot        DirectArticulationIKController, router binding, trajectory replay, ghosts
Runtime/Environment   Erupt.Environment  EnvironmentRegistry/Object, obstacle factory + undoable commands, grab transformers
Runtime/Ui            Erupt.Ui           tier UI building blocks, VerbRegistry, IUiHost (TierUiRig)
Runtime/Plugins       Erupt.Plugins      IEruptPlugin, PluginHost, IEruptContext, PlanningPlugin, DemonstrationPlugin
Prefabs/EruptCore     the ERUPT root: PluginHost + SelectionService/Highlighter/RouterBinding + ModeManager + EnvironmentRegistry + TierUiRig
Tests/Support         Erupt.TestSupport  FakeRosBus, FakeRobotModel, TestInteractionSource (UNITY_INCLUDE_TESTS)
Tests/*               per-assembly PlayMode tests; Tests/Boundary is EditMode and guards the package boundary
```

Core assemblies never reference `Erupt.Plugins.*` or `Erupt.App` (enforced by `AssemblyBoundaryTests`).

## Documentation

- `Documentation~/architecture.md` — packages, assembly graph, the `ERUPT` root, flows
- `Documentation~/plugin-authoring.md` — the plugin contract, context, families, messages, testing, generator
- `Documentation~/design-guidelines.md` — the UI and interaction rules every feature is reviewed against

Licence: BSD 3-Clause (`LICENSE.md`). Changes: `CHANGELOG.md`.
