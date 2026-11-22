# Robot Setup: UFactory xArm7

Written by `ERUPT > Robots > Robot Setup…`. Running it again on the same output folder rewrites this file.

0 problems, 1 warning.

## Warnings

The robot is usable; check that these are what you want.

- 4 collision meshes are too detailed for one convex collider (Unity's limit is 256 polygons): link2_0, link3_0, link4_0, link5_0. Unity uses part of the hull and logs "Couldn't create a Convex Mesh" when the robot is first created. The colliders only serve to select and grab the links, so the robot works; simpler collision meshes in the description make the message go away.

## Found

- Copied the description into 'Packages/com.erupt.robot.ufactory/Robots': the URDF, the SRDF, the ROS-names file and the 16 mesh files the URDF names (0 files new or changed).
- URDF 'xarm7': 18 links, 17 joints, 13 of them movable; root link 'world'.
- Planning frame: 'world' (ROS-names file).
- The planning frame 'world' is a link of the URDF; the planning-frame anchor sits on it.
- Arm group: 'xarm7' (ROS-names file).
- Gripper group: 'xarm_gripper' (ROS-names file).
- Arm joints (ROS-names file): joint1, joint2, joint3, joint4, joint5, joint6, joint7.
- Gripper joints (SRDF group 'xarm_gripper'): drive_joint, left_finger_joint, left_inner_knuckle_joint, right_outer_knuckle_joint, right_finger_joint, right_inner_knuckle_joint.
- Base link: 'link_base' (ROS-names file).
- End-effector link: 'link_tcp' (ROS-names file).
- Named poses (SRDF): home, hold-up, ready, open, close, closed.
- Initial pose: 'ready' (ROS-names file).
- Gripper driver joint: 'drive_joint' (ROS-names file, gripper controller).
- Gripper position 0 of 'drive_joint' from the pose 'open' (ROS-names file).
- Gripper position 0.84 of 'drive_joint' from the pose 'closed' (ROS-names file).
- Gripper open width 0.08619 m, measured in the URDF: the change in the distance between 'left_finger' and 'right_finger' from closed to open.
- Joint states topic: 'joint_states' (ROS-names file).
- Trajectory command topic: 'xarm7_traj_controller/joint_trajectory' (ROS-names file).
- The profile existed: limit overrides and margins, the velocity switch, the ROS name rule and the gripper speed were kept.
- Imported 'Packages/com.erupt.robot.ufactory/Robots/xarm7.urdf' with the URDF Importer: 18 links, 18 articulation bodies.
- Joint limits in the import agree with the URDF.
- Link frames in the import agree with the URDF.
- Visual and collision geometry overlap on all 15 links that have both.
- Removed 6 of the importer's own control components (keyboard controller, mimic drivers): ERUPT's controller drives the joints.
- Updated in place: the prefabs and the profile keep their GUIDs.

## Written

- `Packages/com.erupt.robot.ufactory/Robots/xArm7.prefab`
- `Packages/com.erupt.robot.ufactory/Robots/xArm7Ghost.prefab`
- `Packages/com.erupt.robot.ufactory/Robots/xArm7.asset`
- `Packages/com.erupt.robot.ufactory/Robots/xArm7.setup.md`
