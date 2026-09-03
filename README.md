# ERUPT

Interactive Extended Reality Robotics Visualization Tool

> Note: The AR scene locates the robot with a printed AprilTag (family `tagStandard41h12`),
> detected by the `jp.keijiro.apriltag` fork in the `Packages/AprilTag` submodule, so run
> `git submodule update --init --recursive` after cloning. Print [Docs/tag41_12_id0.svg](Docs/tag41_12_id0.svg)
> at 100% scale (10 cm detection square) and lay it flat where the robot base should go.
>
> Object tags for the reachability indicators are smaller (5 cm detection square) and use other
> IDs: generate them with [Docs/make_apriltag_svg.py](Docs/make_apriltag_svg.py) (`--ids 1-4 --size 0.05`,
> patterns from the [apriltag-imgs](https://github.com/AprilRobotics/apriltag-imgs) repo), print at
> 100% scale and list the IDs under *Additional Tags* on the `AprilTagTracker` in the AR scene.

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
