# Robot Setup: Franka Research 3

Written by `ERUPT > Robots > Robot Setup…`. Running it again on the same output folder rewrites this file.

0 problems, 1 warning.

## Warnings

The robot is usable; check that these are what you want.

- Pose 'extended' puts 'fr3_joint4' at -0.1, outside its limits [-3.077, -0.1169]; it is stored as -0.1169.

## Found

- Copied the description into 'Packages/com.erupt.robot.franka/Robots': the URDF, the SRDF, the ROS-names file and the 19 mesh files the URDF names (0 files new or changed).
- URDF 'fr3': 26 links, 25 joints, 9 of them movable; root link 'base'.
- Planning frame: 'world' (ROS-names file).
- The planning frame 'world' is not a link of the URDF; the SRDF attaches it to 'base' (fixed), so the planning-frame anchor sits on that link.
- Arm group: 'fr3_arm' (ROS-names file).
- Gripper group: 'fr3_hand' (ROS-names file).
- Arm joints (ROS-names file): fr3_joint1, fr3_joint2, fr3_joint3, fr3_joint4, fr3_joint5, fr3_joint6, fr3_joint7.
- Gripper joints (SRDF group 'fr3_hand'): fr3_finger_joint1, fr3_finger_joint2.
- Base link: 'base' (ROS-names file).
- End-effector link: 'fr3_hand_tcp' (typed).
- Named poses (SRDF): ready, extended, open, close.
- Initial pose: 'ready' (ROS-names file).
- Gripper driver joint: 'fr3_finger_joint1' (ROS-names file, gripper controller).
- Gripper position 0.035 of 'fr3_finger_joint1' from the pose 'open' (ROS-names file).
- Gripper position 0 of 'fr3_finger_joint1' from the pose 'close' (ROS-names file).
- Gripper open width 0.07 m, measured in the URDF: the change in the distance between 'fr3_leftfinger' and 'fr3_rightfinger' from closed to open.
- Joint states topic: 'joint_states' (ROS-names file).
- Trajectory command topic: 'fr3_arm_controller/joint_trajectory' (ROS-names file).
- The profile existed: limit overrides and margins, the velocity switch, the ROS name rule and the gripper speed were kept.
- Imported 'Packages/com.erupt.robot.franka/Robots/fr3.urdf' with the URDF Importer: 26 links, 26 articulation bodies.
- Joint limits in the import agree with the URDF.
- Link frames in the import agree with the URDF.
- Visual and collision geometry overlap on all 11 links that have both.
- Removed 2 of the importer's own control components (keyboard controller, mimic drivers): ERUPT's controller drives the joints.
- Updated in place: the prefabs and the profile keep their GUIDs.

## Written

- `Packages/com.erupt.robot.franka/Robots/FR3.prefab`
- `Packages/com.erupt.robot.franka/Robots/FR3Ghost.prefab`
- `Packages/com.erupt.robot.franka/Robots/FR3.asset`
- `Packages/com.erupt.robot.franka/Robots/FR3.setup.md`
