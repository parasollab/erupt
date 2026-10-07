#!/usr/bin/env bash
# ROS traffic diff — Phase 1 behaviour-preservation check.
#
# Run on the ROS 2 host (not the Mac). Records the topics ERUPT touches while you
# perform the five core flows in the headset, then summarises them so two runs can
# be compared.
#
#   ./ros-traffic-diff.sh record baseline
#   ./ros-traffic-diff.sh record refactored
#   ./ros-traffic-diff.sh compare baseline refactored
#
# Timestamps and ids differ between runs by nature, so `compare` reports per-topic
# message counts and the semantic sequence of CollisionObject operations rather than
# a byte diff.

set -euo pipefail

TOPICS=(
  /collision_object
  /collision_objects_ros
  /attached_collision_objects_ros
  /pick_place_task
  /mtc_execute_solution
  /mtc_execution_status
  /joint_trajectory_controller/joint_trajectory
)

usage() { sed -n '2,20p' "$0"; exit 1; }

record() {
  local name="${1:?usage: record <name>}"
  echo "Recording to ./$name — run the five flows, then Ctrl-C:"
  echo "  1. create an obstacle   2. scale it   3. snap it to a surface"
  echo "  4. set a goal and plan  5. preview, then execute"
  ros2 bag record -o "$name" "${TOPICS[@]}"
}

summarise() {
  local bag="${1:?}"
  echo "## $bag"
  ros2 bag info "$bag" | sed -n '/Topic information/,$p'
  echo
  echo "### /collision_object operation sequence"
  # ADD=0 REMOVE=1 APPEND=2 MOVE=3
  python3 - "$bag" <<'PY'
import sys
try:
    from rosbag2_py import SequentialReader, StorageOptions, ConverterOptions
    from rclpy.serialization import deserialize_message
    from moveit_msgs.msg import CollisionObject
except ImportError:
    print("  (rosbag2_py/moveit_msgs unavailable — compare `ros2 bag info` counts instead)")
    sys.exit(0)

names = {0: "ADD", 1: "REMOVE", 2: "APPEND", 3: "MOVE"}
reader = SequentialReader()
reader.open(StorageOptions(uri=sys.argv[1]), ConverterOptions("", ""))
while reader.has_next():
    topic, data, _ = reader.read_next()
    if topic != "/collision_object":
        continue
    msg = deserialize_message(data, CollisionObject)
    print(f"  {names.get(msg.operation, msg.operation):6} {msg.id}")
PY
}

compare() {
  local a="${1:?}" b="${2:?}"
  summarise "$a" > "/tmp/_traffic_a.txt"
  summarise "$b" > "/tmp/_traffic_b.txt"
  echo "=== differences ($a vs $b) ==="
  # Ignore the trailing numeric suffix in generated object ids; it is a timestamp.
  diff <(sed -E 's/_[0-9]{10,}$//' /tmp/_traffic_a.txt) \
       <(sed -E 's/_[0-9]{10,}$//' /tmp/_traffic_b.txt) \
    && echo "IDENTICAL — no behavioural divergence in ROS traffic."
}

case "${1:-}" in
  record)  shift; record "$@" ;;
  compare) shift; compare "$@" ;;
  *)       usage ;;
esac
