#!/usr/bin/env python3
"""Export one robot of the ERUPT ROS workspace for Unity.

Runs inside the ERUPT ROS container (see generate.sh). For a robot named in
``erupt_bringup/config/robots`` it writes

    <out>/<robot>/<robot>.urdf       the URDF the ROS side uses, without driver-only elements
    <out>/<robot>/<robot>.srdf       the SRDF MoveIt uses (planning groups, named poses, gripper)
    <out>/<robot>/<robot>.ros.json   the ROS names the Unity robot profile needs
    <out>/<robot>/<package>/...      the mesh files the URDF uses

so that every ``package://<package>/...`` path in the URDF resolves next to the URDF. The
URDF elements that only matter to ROS drivers and simulators (``ros2_control``, ``gazebo``,
``transmission``) are removed; nothing else is changed.
"""

import argparse
import json
import os
import shutil
import xml.etree.ElementTree as ET

import yaml
from ament_index_python.packages import get_package_share_directory, get_packages_with_prefixes
from erupt_bringup.robots import load_robot, moveit_config, resolve

DROP = ("ros2_control", "gazebo", "transmission")
TRAJECTORY_CONTROLLER = "joint_trajectory_controller/JointTrajectoryController"


def package_path(filename, shares):
    """Return (package, relative path) for a package:// or file:// mesh reference."""
    if filename.startswith("package://"):
        package, _, rest = filename[len("package://"):].partition("/")
        return package, rest
    path = filename[len("file://"):] if filename.startswith("file://") else filename
    for package, share in shares.items():
        if path.startswith(share + os.sep):
            return package, os.path.relpath(path, share)
    raise RuntimeError(f"Cannot tell which package '{filename}' belongs to")


def ros_names(robot, urdf, srdf):
    """What the Unity robot profile has to know about this robot's ROS side."""
    with open(resolve(robot["ros2_controllers"])) as f:
        controllers = yaml.safe_load(f)
    types = {name: spec["type"] for name, spec in controllers["controller_manager"]["ros__parameters"].items()
             if isinstance(spec, dict) and "type" in spec}

    def parameters(name):
        return controllers.get(name, {}).get("ros__parameters", {})

    arm_controller = next(name for name, kind in types.items()
                          if kind == TRAJECTORY_CONTROLLER and parameters(name).get("joints") == robot["arm_joints"])
    names = {
        "robot": robot["name"],
        "planning_frame": "world",
        "arm_group": robot["arm_group"],
        "arm_joints": robot["arm_joints"],
        "joint_states_topic": "joint_states",
        "trajectory_command_topic": arm_controller + "/joint_trajectory",
        "named_poses": {group: [state.get("name") for state in srdf.iter("group_state") if state.get("group") == group]
                        for group in sorted({state.get("group") for state in srdf.iter("group_state")})},
    }

    # The link the base pose refers to: the one named in the robot file, otherwise the link
    # the description fixes to its "world" link.
    names["base_link"] = robot.get("base_link") or next(
        (joint.find("child").get("link") for joint in urdf.iter("joint")
         if joint.find("parent") is not None and joint.find("parent").get("link") == "world"), None)

    gripper = [name for name in types if name not in ("joint_state_broadcaster", arm_controller)]
    if gripper:
        spec = parameters(gripper[0])
        names["gripper_controller"] = {
            "name": gripper[0],
            "type": types[gripper[0]],
            "joints": spec.get("joints") or [spec.get("joint")],
        }

    if "mtc" in robot:
        with open(resolve(robot["mtc"])) as f:
            mtc = next(iter(yaml.safe_load(f).values()))["ros__parameters"]
        names.update(
            gripper_group=mtc["hand_group_name"],
            end_effector_link=mtc["hand_frame"],
            initial_pose=mtc["arm_home_pose"],
            gripper_open_pose=mtc["hand_open_pose"],
            gripper_closed_pose=mtc["hand_close_pose"],
        )
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("robot", help="robot name: a file in erupt_bringup/config/robots")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    robot = load_robot(args.robot)
    config = moveit_config(robot)
    urdf = ET.fromstring(config.robot_description["robot_description"])
    srdf_text = config.robot_description_semantic["robot_description_semantic"]
    srdf = ET.fromstring(srdf_text)

    shares = {p: get_package_share_directory(p) for p in get_packages_with_prefixes()}
    urdf.set("name", args.robot)
    for element in list(urdf):
        if element.tag in DROP:
            urdf.remove(element)

    folder = os.path.join(args.out, args.robot)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    copied = set()
    for mesh in urdf.iter("mesh"):
        package, rest = package_path(mesh.get("filename"), shares)
        mesh.set("filename", f"package://{package}/{rest}")
        if (package, rest) in copied:
            continue
        copied.add((package, rest))
        target = os.path.join(folder, package, rest)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copyfile(os.path.join(shares[package], rest), target)

    note = (f"<!-- Generated from the ERUPT ROS workspace's robot '{args.robot}' "
            "(erupt_bringup/config/robots); see Tools/robot-descriptions. -->\n")
    ET.indent(urdf)
    with open(os.path.join(folder, args.robot + ".urdf"), "w") as f:
        f.write('<?xml version="1.0"?>\n' + note + ET.tostring(urdf, encoding="unicode") + "\n")
    srdf.set("name", args.robot)
    ET.indent(srdf)
    with open(os.path.join(folder, args.robot + ".srdf"), "w") as f:
        f.write('<?xml version="1.0"?>\n' + note + ET.tostring(srdf, encoding="unicode") + "\n")
    with open(os.path.join(folder, args.robot + ".ros.json"), "w") as f:
        json.dump(ros_names(robot, urdf, srdf), f, indent=2)
        f.write("\n")

    joints = [j for j in urdf.iter("joint") if j.get("type") != "fixed" and j.get("name")]
    size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(folder) for f in fs)
    print(f"{args.robot}: {len(joints)} moving joints, {len(copied)} mesh files, {size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
