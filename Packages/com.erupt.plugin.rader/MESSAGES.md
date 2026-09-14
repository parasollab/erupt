# Generated ROS messages — RADER plugin

| Source ROS package | Files | Generated from |
|---|---|---|
| _(none)_ — uses `std_msgs`, `sensor_msgs`, `trajectory_msgs`, `builtin_interfaces` from `Unity.Robotics.ROSTCPConnector.Messages` | 0 | — |

Output path for the message browser: `Packages/com.erupt.plugin.rader/Runtime/Messages`.
See `Runtime/Messages/README.md`.

## Topics

| Topic | Type | Direction | Purpose |
|---|---|---|---|
| `/{ns}/joint_trajectory` | `trajectory_msgs/JointTrajectory` | publish | the recorded demonstration (**Publish**) |
| `/{ns}/virtual_joint_state` | `sensor_msgs/JointState` | publish | the virtual robot's joints, periodic while **State** is on |
| `/{ns}/interaction` | `std_msgs/Bool` | publish | `false` when a demonstration starts, `true` when it stops (RADER wire convention) |
| `/{ns}/joint_states` | `sensor_msgs/JointState` | subscribe | mirrored onto the virtual robot while **Mirror** is on |
| `/record_start` | `std_msgs/Bool` | subscribe | any message toggles recording (Teach mode only) |
| `/feedback_request` | `std_msgs/Bool` | subscribe | FERL asks for feedback; the demos tab is summoned |
| `/req_satisfied` | `std_msgs/Bool` | subscribe | FERL says the request is satisfied; **Resume** becomes available |
| `/feedback_response` | `std_msgs/Bool` | publish | **Resume** |
| `/user_info` | `std_msgs/String` | subscribe | informational text, logged and shown in the tab |
| `/point_cloud` | `sensor_msgs/PointCloud2` | publish | optional `PointCloudPublisher` component |

`{ns}` is the plugin's `robotNamespace`; with an empty namespace the topics are un-prefixed
(`/joint_trajectory`, …).
