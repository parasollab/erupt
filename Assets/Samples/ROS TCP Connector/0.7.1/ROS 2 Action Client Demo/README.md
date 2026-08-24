# ROS 2 Action Client Demo

This sample talks to the Fibonacci action server included with ROS-TCP-Endpoint
0.7.1. It exercises action registration, goal acceptance and rejection,
feedback, succeeded and aborted results, cancellation, endpoint errors, and
disconnect handling.

## Start ROS

On the ROS 2 Jazzy machine:

```bash
cd /path/to/erupt_ws
source /opt/ros/jazzy/setup.bash
colcon build --symlink-install --packages-select ros_tcp_endpoint
source install/setup.bash
ros2 launch ros_tcp_endpoint action_demo.py
```

## Start Unity

1. Set `Robotics > ROS Settings > Protocol` to ROS 2.
2. Set the ROS IP and port (the demo endpoint uses port 10000).
3. Add `Ros2ActionClientDemo` to an empty GameObject in every scene/build where
   you want to run the demo.
4. Build and deploy that scene to the headset.
5. Enter Play mode. `Auto Run On Ready` is enabled by default and runs success,
   rejection, abort, and cancellation without requiring XR button interaction.

Every state change, feedback message, result, and cancel response is emitted
through `Debug.Log` with the managed thread ID and Unity frame number. Erupt's
world-space `DebugPanel` captures these logs on the headset. A normal goal
succeeds, order 0 is rejected, order 13 is deliberately aborted, and the final
automatic goal is canceled after one second. Disable `Auto Run On Ready` if you
want to use the desktop on-screen buttons manually.

To test server-unavailable errors, run only the endpoint with
`ros2 launch ros_tcp_endpoint endpoint.py`. To test disconnect handling, stop
the entire `action_demo.py` launch process while a goal is active.
