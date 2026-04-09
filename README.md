# XRViz

Interactive Extended Reality Robotics Visualization Tool


## ROS2 Commands

### 1. Start TCP Endpoint

```
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

Replace `ROS_IP` with your IP address where ROS 2 is running

### 2. Start UR Controllers

```
ros2 launch ur_robot_driver ur_control.launch.py ur_type:=ur5e \
  robot_ip:=yyy.yyy.yyy.yyy use_mock_hardware:=true \
  initial_joint_controller:=joint_trajectory_controller launch_rviz:=false
```

### 3. Start MoveIt

```
ros2 launch ur_moveit_config ur_moveit.launch.py ur_type:=ur5e launch_rviz:=true

```

### 4. Start Planning Scene Watcher

```
ros2 run planning_scene_utils planning_scene_watcher
```
