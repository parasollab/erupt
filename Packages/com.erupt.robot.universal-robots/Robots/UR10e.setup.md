# Robot Setup: Universal Robots UR10e

Written by `ERUPT > Robots > Robot Setup…`. Running it again on the same output folder rewrites this file.

0 problems, 1 warning.

## Warnings

The robot is usable; check that these are what you want.

- 3 collision meshes are too detailed for one convex collider (Unity's limit is 256 polygons): shoulder_0, upperarm_0, forearm_0. Unity uses part of the hull and logs "Couldn't create a Convex Mesh" when the robot is first created. The colliders only serve to select and grab the links, so the robot works; simpler collision meshes in the description make the message go away.

## Found

- Copied the description into 'Packages/com.erupt.robot.universal-robots/Robots': the URDF, the SRDF, the ROS-names file and the 34 mesh files the URDF names (0 files new or changed).
- URDF 'ur10e': 24 links, 23 joints, 12 of them movable; root link 'world'.
- Planning frame: 'world' (ROS-names file).
- The planning frame 'world' is a link of the URDF; the planning-frame anchor sits on it.
- Arm group: 'ur_manipulator' (ROS-names file).
- Gripper group: 'gripper' (ROS-names file).
- Arm joints (ROS-names file): shoulder_pan_joint, shoulder_lift_joint, elbow_joint, wrist_1_joint, wrist_2_joint, wrist_3_joint.
- Gripper joints (SRDF group 'gripper'): robotiq_85_left_knuckle_joint, robotiq_85_right_knuckle_joint, robotiq_85_left_inner_knuckle_joint, robotiq_85_right_inner_knuckle_joint, robotiq_85_left_finger_tip_joint, robotiq_85_right_finger_tip_joint.
- Base link: 'base_link' (ROS-names file).
- End-effector link: 'tool0' (ROS-names file).
- Named poses (SRDF): home, up, test_configuration, ready, open, close.
- Initial pose: 'ready' (ROS-names file).
- Gripper driver joint: 'robotiq_85_left_knuckle_joint' (ROS-names file, gripper controller).
- Gripper position 0 of 'robotiq_85_left_knuckle_joint' from the pose 'open' (ROS-names file).
- Gripper position 0.79 of 'robotiq_85_left_knuckle_joint' from the pose 'close' (ROS-names file).
- Gripper open width 0.0837 m, measured in the URDF: the change in the distance between 'robotiq_85_left_finger_tip_link' and 'robotiq_85_right_finger_tip_link' from closed to open.
- Joint states topic: 'joint_states' (ROS-names file).
- Trajectory command topic: 'joint_trajectory_controller/joint_trajectory' (ROS-names file).
- The profile existed: limit overrides and margins, the velocity switch, the ROS name rule and the gripper speed were kept.
- Imported 'Packages/com.erupt.robot.universal-robots/Robots/ur10e.urdf' with the URDF Importer: 24 links, 24 articulation bodies.
- Joint limits in the import agree with the URDF.
- Link frames in the import agree with the URDF.
- Visual and collision geometry overlap on all 17 links that have both.
- Removed 6 of the importer's own control components (keyboard controller, mimic drivers): ERUPT's controller drives the joints.
- Updated in place: the prefabs and the profile keep their GUIDs.

## Written

- `Packages/com.erupt.robot.universal-robots/Robots/UR10e.prefab`
- `Packages/com.erupt.robot.universal-robots/Robots/UR10eGhost.prefab`
- `Packages/com.erupt.robot.universal-robots/Robots/UR10e.asset`
- `Packages/com.erupt.robot.universal-robots/Robots/UR10e.setup.md`
