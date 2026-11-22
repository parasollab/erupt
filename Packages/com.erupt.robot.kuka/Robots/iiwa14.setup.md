# Robot Setup: KUKA LBR iiwa 14 R820

Written by `ERUPT > Robots > Robot Setup…`. Running it again on the same output folder rewrites this file.

No problems found.

## Found

- Copied the description into 'Packages/com.erupt.robot.kuka/Robots': the URDF, the SRDF, the ROS-names file and the 34 mesh files the URDF names (0 files new or changed).
- URDF 'iiwa14': 18 links, 17 joints, 13 of them movable; root link 'lbr_link_0'.
- Planning frame: 'world' (ROS-names file).
- The planning frame 'world' is not a link of the URDF; the planning-frame anchor sits on the root link 'lbr_link_0'. That is right when the ROS side starts the robot at the origin of its planning frame.
- Arm group: 'arm' (ROS-names file).
- Gripper group: 'gripper' (ROS-names file).
- Arm joints (ROS-names file): lbr_A1, lbr_A2, lbr_A3, lbr_A4, lbr_A5, lbr_A6, lbr_A7.
- Gripper joints (SRDF group 'gripper'): robotiq_85_left_knuckle_joint, robotiq_85_right_knuckle_joint, robotiq_85_left_inner_knuckle_joint, robotiq_85_right_inner_knuckle_joint, robotiq_85_left_finger_tip_joint, robotiq_85_right_finger_tip_joint.
- Base link: 'lbr_link_0' (ROS-names file).
- End-effector link: 'lbr_link_ee' (ROS-names file).
- Named poses (SRDF): zero, transport, ready, open, close.
- Initial pose: 'ready' (ROS-names file).
- Gripper driver joint: 'robotiq_85_left_knuckle_joint' (ROS-names file, gripper controller).
- Gripper position 0 of 'robotiq_85_left_knuckle_joint' from the pose 'open' (ROS-names file).
- Gripper position 0.79 of 'robotiq_85_left_knuckle_joint' from the pose 'close' (ROS-names file).
- Gripper open width 0.0837 m, measured in the URDF: the change in the distance between 'robotiq_85_left_finger_tip_link' and 'robotiq_85_right_finger_tip_link' from closed to open.
- Joint states topic: 'joint_states' (ROS-names file).
- Trajectory command topic: 'joint_trajectory_controller/joint_trajectory' (ROS-names file).
- The profile existed: limit overrides and margins, the velocity switch, the ROS name rule and the gripper speed were kept.
- Imported 'Packages/com.erupt.robot.kuka/Robots/iiwa14.urdf' with the URDF Importer: 18 links, 18 articulation bodies.
- Joint limits in the import agree with the URDF.
- Link frames in the import agree with the URDF.
- Visual and collision geometry overlap on all 17 links that have both.
- Removed 6 of the importer's own control components (keyboard controller, mimic drivers): ERUPT's controller drives the joints.
- Updated in place: the prefabs and the profile keep their GUIDs.

## Written

- `Packages/com.erupt.robot.kuka/Robots/iiwa14.prefab`
- `Packages/com.erupt.robot.kuka/Robots/iiwa14Ghost.prefab`
- `Packages/com.erupt.robot.kuka/Robots/iiwa14.asset`
- `Packages/com.erupt.robot.kuka/Robots/iiwa14.setup.md`
