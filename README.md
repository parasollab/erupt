# ERUPT

[![tests](https://github.com/parasollab/erupt/actions/workflows/tests.yml/badge.svg?branch=release)](https://github.com/parasollab/erupt/actions/workflows/tests.yml)
[![docs](https://github.com/parasollab/erupt/actions/workflows/docs.yml/badge.svg?branch=release)](https://github.com/parasollab/erupt/actions/workflows/docs.yml)

**Extended Reality Universal Programming Toolkit.** ERUPT lets you program robot arms from
inside a Meta Quest headset. You see the robot and its surroundings, place obstacles, set
goals, look at each plan before anything moves, and run it. Planning and execution happen in
ROS 2 with MoveIt 2; ERUPT is the Unity front end.

**Documentation: <https://parasollab.github.io/erupt/>**

- Plan and execute motions with MoveIt 2, and build the planning scene by hand.
- Pick and place with MoveIt Task Constructor: show the task, browse the solutions stage by
  stage, execute.
- Record, replay and publish demonstrations.
- Any number of arms, each in its own ROS namespace.
- Joint limits hold for every interaction, plan and replay.
- Controllers or bare hands. The Unity editor with a mouse is enough to try it.

## Quick start

You need Unity 6 (the exact version is in `ProjectSettings/ProjectVersion.txt`), `git`, and
Docker for the simulated robot.

1. Start a simulated Franka Research 3 with MoveIt and the bridge to Unity:

   ```bash
   git clone https://github.com/parasollab/erupt_ws.git
   cd erupt_ws
   docker compose -f docker/compose.yaml up
   ```

2. Clone this repository and open the folder in Unity. There are no submodules; Unity
   downloads the packages the project uses on the first open.

   ```bash
   git clone https://github.com/parasollab/erupt.git
   ```

3. Open `Assets/Scenes/KitchenFR3` and press Play.

The [quick start](https://parasollab.github.io/erupt/guides/quick-start.html) continues from
here: planning a first motion with the mouse, then building for the headset.

## Two ways to use it

- **This repository as your project.** Clone or fork it and change the scenes. It is a
  complete Unity project with a kitchen scene, a three-arm scene, an AR tabletop workbench
  and every shipped robot. In `Assets/Scenes/ARWorkbenchFR3.unity`, you can place an FR3
  on your own table and preview a motion around a movable block.
- **The packages in your own project.** Add them by git URL, for example
  `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.core`. See
  [Add ERUPT to your own project](https://parasollab.github.io/erupt/guides/install-package.html).

## Packages

| Package | What it adds |
| --- | --- |
| `com.erupt.core` | The core: robot model with inverse kinematics and joint limits, environment, input, selection, undo, user interface, ROS transport, plugin contract. Required |
| `com.erupt.plugin.moveit` | Motion planning with MoveIt 2 and planning-scene synchronisation |
| `com.erupt.plugin.mtc` | Pick and place with MoveIt Task Constructor. Needs the MoveIt plugin |
| `com.erupt.plugin.demonstration` | Recording, replaying and publishing demonstrations |
| `com.erupt.plugin.ar` | Optional. Places robots in the room from printed AprilTags, and turns room furniture into obstacles |
| `com.erupt.robot.franka` | Franka Research 3 with the Franka Hand |
| `com.erupt.robot.universal-robots` | UR5e and UR10e with the Robotiq 2F-85 |
| `com.erupt.robot.kinova` | Kinova Gen3 (7 joints) with the Robotiq 2F-85 |
| `com.erupt.robot.ufactory` | UFactory xArm7 with its gripper |
| `com.erupt.robot.kuka` | KUKA LBR iiwa 14 with the Robotiq 2F-85 |

Core never references a plugin, and a test enforces it. A planning or learning method of your
own is a new plugin package: **ERUPT > Plugins > Create Plugin…** makes one from a template
([Writing a plugin](https://parasollab.github.io/erupt/guides/write-a-plugin.html)). A robot
ERUPT does not ship is set up from its URDF with **ERUPT > Robots > Robot Setup…**
([Adding a robot](https://parasollab.github.io/erupt/guides/add-a-robot.html)).

## The ROS 2 side

The companion workspace [`erupt_ws`](https://github.com/parasollab/erupt_ws) has the launch
files, the pick-and-place server and a Docker image that starts any shipped robot, or several:
`ROBOT=ur5e docker compose -f docker/compose.yaml up`. It targets ROS 2 Jazzy.
[The ROS 2 side](https://parasollab.github.io/erupt/guides/ros-setup.html) lists what ERUPT
needs from a ROS system you bring yourself.

## Repository layout

```
Assets/Scenes             KitchenFR3, MultiArmFR3 and ARWorkbenchFR3
Assets/Erupt.App          scripts that belong to the reference scenes only
Packages/com.erupt.*      the ERUPT packages
Tools/robot-descriptions  makes the robot packages from the descriptions the ROS workspace exports
Tools/live-tests.sh       tests of the app against a running ROS 2 stack
website/                  the documentation site (DocFX)
```

## Preview the documentation

With the .NET 8 SDK or later installed, run these commands from the repository root.
The first command installs the version of DocFX used by the documentation workflow and is
needed only once. If your shell cannot find `docfx`, add the tools directory printed by the
installer to your PATH.

```bash
dotnet tool install --global docfx --version 2.81.0
docfx website/docfx.json --serve --port 8080
```

Open [the local preview](http://localhost:8080/). After editing a page or the theme, stop
the server with Ctrl+C, rerun the second command, and refresh the browser.

## Tests

**Window > General > Test Runner** runs every suite without ROS: the tests use a stand-in for
the ROS connection. `Tools/live-tests.sh single` runs the app against a real ROS 2 stack; see
[`Assets/Erupt.App/Tests/Live/README.md`](Assets/Erupt.App/Tests/Live/README.md).

## Licence

BSD 3-Clause, © Parasol Lab, University of Illinois Urbana-Champaign. See `LICENSE`; each
package carries its own `LICENSE.md`, and each robot package the notices of the description it
was made from. The ROS-TCP-Connector and URDF-Importer forks keep their upstream Apache 2.0
licences.
