# The ROS 2 side

ERUPT in the headset is a front end. Planning, the planning scene and execution happen in
ROS 2 with MoveIt 2, on a computer the headset reaches over the network. The companion
repository [`erupt_ws`](https://github.com/parasollab/erupt_ws) holds everything that side
needs and is the reference for it; this page is the short version.

## What runs there

For each robot arm, in that arm's ROS namespace:

| Piece | What it does for the app |
| --- | --- |
| The robot's bringup (driver or simulation, controllers) | Publishes `joint_states`, executes trajectories |
| MoveIt `move_group` | Plans motions, holds the planning scene, executes |
| `planning_scene_watcher` (`planning_scene_utils`) | Reports every change of MoveIt's planning scene to the app, object by object |
| `pick_place_server` (`mtc_pick_place_server`), optional | Plans pick-and-place tasks with MoveIt Task Constructor and lets the app browse and execute the solutions |

And once, for all arms: the **TCP endpoint** (`ros_tcp_endpoint`), which the headset connects
to on port 10000.

## Start a simulated robot with Docker

No ROS installation is needed, only a Docker engine.

```bash
git clone https://github.com/parasollab/erupt_ws.git
cd erupt_ws
docker compose -f docker/compose.yaml up
```

The first run builds the image, which takes a while. After that the command starts a simulated
Franka Research 3 with MoveIt, the planning-scene watcher, the pick-and-place server and the
TCP endpoint.

Choose another robot with `ROBOT`, and put it in a namespace with `NAMESPACE`:

```bash
ROBOT=ur5e docker compose -f docker/compose.yaml up
ROBOT=fr3 NAMESPACE=left docker compose -f docker/compose.yaml up
```

| `ROBOT` | Arm | Gripper |
| --- | --- | --- |
| `fr3` (default) | Franka Research 3 | Franka hand |
| `ur5e`, `ur10e` | Universal Robots UR5e, UR10e | Robotiq 2F-85 |
| `kinova_gen3` | Kinova Gen3, 7 joints | Robotiq 2F-85 |
| `xarm7` | UFactory xArm7 | xArm gripper |
| `iiwa14` | KUKA LBR iiwa 14 R820 | Robotiq 2F-85 |

These are the same robots that ship as ERUPT robot packages, generated from the same
descriptions, so joint names, link names and limits agree on both sides.

Several arms at once, each in its own namespace:

```bash
ARMS="left=fr3 right=fr3 third=ur5e" docker compose -f docker/compose.yaml --profile arms up arms
```

See [Several robot arms](multi-arm.md).

## Point the app at it

In Unity, open **Robotics > ROS Settings** and enter the IP address of the computer that runs
ROS and port 10000. When the Unity editor and Docker run on the same computer, `127.0.0.1`
works. A headset needs the computer's address on the local network, and both must be on the
same network.

## Without Docker, or with a real robot

Build `erupt_ws` from source on Ubuntu 24.04 with ROS 2 Jazzy (the repository's README has the
commands) and start the same launch files:

```bash
ros2 launch erupt_bringup demo.launch.py robot:=fr3
```

For a real robot, or a simulator of your own, start your usual bringup and MoveIt, and add the
pieces ERUPT needs in the same namespace:

```bash
ros2 launch planning_scene_utils planning_scene_watcher.launch.py
ros2 launch erupt_bringup endpoint.launch.py
```

For pick-and-place, also start `pick_place_server` and make sure `move_group` loads
`move_group/ExecuteTaskSolutionCapability`. The robot in Unity has to match the robot in ROS:
see [Adding a robot](add-a-robot.md).

## Names the app uses

Every name is relative to the robot's namespace. For a robot without a namespace
`joint_states` is `/joint_states`; for the robot in namespace `left` it is
`/left/joint_states`.

| Name | Kind | Direction (seen from the app) |
| --- | --- | --- |
| `joint_states` | topic | in |
| `<controller>/joint_trajectory` | topic | out |
| `plan_kinematic_path`, `query_planner_interface`, `get_planning_scene` | services | out |
| `collision_object` | topic | out |
| `collision_objects_ros`, `attached_collision_objects_ros` | topics | in |
| `execute_trajectory` | action | out (the gripper) |
| `execute_task_solution` | action | out |
| `pick_place`, `execute_solution` | actions | out |
| `get_solution` | service | out |
| `pick_place/description`, `pick_place/statistics` | topics | in |

The message types and the provider of each name are listed in the `erupt_ws` README. The
controller name and the planning group differ per robot and come from the robot's profile in
Unity.
