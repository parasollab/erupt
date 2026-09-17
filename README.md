# ERUPT

Interactive Extended Reality Robotics Visualization Tool

> Note: The AR scene depends on OpenCV for Unity (not included).

## ROS2 Setup

The ROS-side nodes live in [hri_ws](https://github.com/parasollab/hri_ws). Clone it, then build and source the workspace:

```sh
cd hri_ws
colcon build
source install/setup.bash
```

## ROS2 Commands

### 1. Start TCP Endpoint

```sh
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

Replace `ROS_IP` with your IP address where ROS 2 is running

### 2. Start UR Controllers

```sh
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur5e \
  robot_ip:=yyy.yyy.yyy.yyy use_mock_hardware:=true \
  initial_joint_controller:=joint_trajectory_controller launch_rviz:=false
```

### 3. Start MoveIt

```sh
ros2 launch ur_moveit_config ur_moveit.launch.py ur_type:=ur5e launch_rviz:=true
```

### 4. Start Planning Scene Watcher

```sh
ros2 run planning_scene_utils planning_scene_watcher
```

## Benchmarking

The benchmark nodes are in the `planning_scene_utils` package of [hri_ws](https://github.com/parasollab/hri_ws). The Kitchen scene includes a `LatencyBenchmark` component (on the XR Origin rig) that runs a scripted create/move/delete workload and streams latency, FPS, network jitter, and GC metrics. To use it:

1. Start the ROS2 commands above, then the measurement nodes:

   ```sh
   ros2 run planning_scene_utils latency_measurer
   ros2 run planning_scene_utils latency_logger
   ```

2. Build and run the Unity app on the HMD, then trigger a run:

   ```sh
   ros2 topic pub --once /benchmark/start std_msgs/msg/String "data: ''"
   ```

3. The logger writes CSVs (latency, fps, jitter, gc) to `/tmp`. Summarize them with:

   ```sh
   ros2 run planning_scene_utils analyze_metrics --objects bench
   ```

## FERL scene-graph test scene

`Assets/Scenes/FERL/FERLTest.unity` is a standalone (non-study) scene for testing the
scene-graph version of FERL in `preference_rl` (built as a package in `erupt_ws`, see
its README). It uses the FR3 model; typed objects (laptop, cup, table, human, marker,
other) are grabbable, carry the four FERL attributes, and are published as a scene
graph on `/ferl/scene_graph`. The session menu sets endpoints, plans through the
bridge, plays the plan on the robot, records robot traces (drag the end effector) and
environmental traces (edit the scene), and learns/replans; dragging the end-effector
handle while the plan plays is a FERL correction.

Start the bridge on the ROS machine:

```sh
ros2 launch preference_rl ferl_bridge.launch.py ros_ip:=<ROS machine IP>
```

The scene also runs in the editor without a headset: WASD/QE + right mouse to fly,
left-click to select and drag objects (Ctrl: ground plane, Alt: rotate, wheel:
push/pull, Delete: remove), drag the end-effector handle with the mouse, and keys
`1`/`2` set start/goal, `I` IK endpoints, `P` plan, `Space` play, `R`/`T` record a
robot/env trace, `C` scene edit as correction, `L` learn, `M` cycle the reward map, `F5` save, `F9` reset.

The **Reward map** button asks the bridge to sample reachable end-effector positions and draws them in the robot frame coloured by the weighted total cost or by one feature (blue low, red high); it refreshes automatically after each Learn. Every plan also draws its end-effector path as a green line with waypoint dots; the previous plan's path stays in faded red so a replan is easy to compare.
