# ERUPT

**Extended Reality Universal Programming Toolkit.** ERUPT lets you program robot arms from
inside a Meta Quest headset. You see the robot and its surroundings, place obstacles, set
goals, look at each plan before anything moves, and run it. Planning and execution happen in
ROS 2 with MoveIt 2; ERUPT is the front end.

## What you can do with it

- **Plan and execute motions.** Pose the robot to set a start and a goal, plan with MoveIt,
  watch the plan play on the robot, execute.
- **Build the planning scene by hand.** Place, move and resize obstacles; every change reaches
  MoveIt as a collision object, and objects added on the ROS side appear in the headset.
- **Pick and place.** Show a task by grabbing an object and putting it down. MoveIt Task
  Constructor plans it; browse the solutions stage by stage, see why a stage failed, execute.
- **Collect demonstrations.** Record the motions you make the robot do, replay them and
  publish them to ROS.
- **Work with any number of arms.** Each arm has its own ROS namespace and its own MoveIt;
  the scene is shared between all of them.
- **Stay inside joint limits.** No interaction, plan or replay moves a joint past its limits,
  and ERUPT tells you which joint stopped you.
- **Use controllers or bare hands.**

## Start here

| If you want to | Read |
| --- | --- |
| Try ERUPT with a simulated robot | [Quick start](guides/quick-start.md) |
| Add ERUPT to a Unity project you already have | [Add ERUPT to your own project](guides/install-package.md) |
| Set up the ROS 2 side, in Docker or on a real robot | [The ROS 2 side](guides/ros-setup.md) |
| Learn the controls | [Using the app](guides/using-the-app.md) |
| Use another of the shipped robots | [Robots](guides/robots.md) |
| Use a robot that ERUPT does not ship | [Adding a robot](guides/add-a-robot.md) |
| Run two or more arms | [Several robot arms](guides/multi-arm.md) |
| Put the virtual robot where the real one stands | [AR placement](guides/ar.md) |
| Add your own planner or learning method | [Writing a plugin](guides/write-a-plugin.md) |
| Find out why something does not work | [Troubleshooting](guides/troubleshooting.md) |

## How it is put together

ERUPT is a set of Unity packages. **ERUPT Core** holds what every such app needs: the robot
model with inverse kinematics and joint limits, the environment, input from controllers and
hands, selection, undo, the user interface and the connection to ROS. Everything specific to a
planning or learning method is a **plugin** in its own package. Robots are packages too.

| Package | What it adds |
| --- | --- |
| `com.erupt.core` | The core. Required |
| `com.erupt.plugin.moveit` | Motion planning with MoveIt 2 and planning-scene synchronisation |
| `com.erupt.plugin.mtc` | Pick-and-place with MoveIt Task Constructor. Needs the MoveIt plugin |
| `com.erupt.plugin.demonstration` | Recording, replaying and publishing demonstrations |
| `com.erupt.plugin.ar` | Optional. Places robots in the room from AprilTag markers |
| `com.erupt.robot.*` | One package per robot maker, see [Robots](guides/robots.md) |

The [architecture reference](../Packages/com.erupt.core/Documentation~/architecture.md) describes the pieces and how they
talk to each other.

## Requirements

- Unity 6 (the exact version is in `ProjectSettings/ProjectVersion.txt` of the repository)
- A Meta Quest 3 for use in the headset; the Unity editor is enough to try it with a mouse
- A computer that runs ROS 2 Jazzy with MoveIt 2, or Docker to run the provided container

ERUPT is developed by the Parasol Lab at the University of Illinois Urbana-Champaign and
released under the BSD 3-Clause licence.
