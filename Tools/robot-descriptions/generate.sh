#!/bin/bash
# Regenerates the URDF, SRDF, ROS names and mesh folders of the robots ERUPT ships, from the
# robot registry of the ERUPT ROS workspace (erupt_bringup/config/robots), so the Unity robots
# and the ROS robots agree on joint names, link names, limits, groups and controllers.
#
#   Tools/robot-descriptions/generate.sh <output folder> [robot ...]
#
# Without robot names it exports the ones ERUPT ships. The output folder must be one Docker
# can mount (with Colima or Docker Desktop: a folder under your home directory).
#
# Needs Docker and the ERUPT ROS image (build it in the erupt_ws repository with
# `docker compose -f docker/compose.yaml build`). Environment:
#   ERUPT_ROS_IMAGE        image tag (default erupt-ros:jazzy)
#   ERUPT_BRINGUP_CONFIG   a working copy of erupt_ws/src/erupt_bringup/config to use instead of
#                          the one built into the image (for trying out a robot file)
set -euo pipefail

OUT="$(mkdir -p "${1:?usage: generate.sh <output folder> [robot ...]}" && cd "$1" && pwd)"
shift
ROBOTS="${*:-fr3 ur5e ur10e kinova_gen3 xarm7 iiwa14}"
HERE="$(cd "$(dirname "$0")" && pwd)"
IMAGE="${ERUPT_ROS_IMAGE:-erupt-ros:jazzy}"

MOUNTS=(-v "$HERE:/tools:ro" -v "$OUT:/out")
if [ -n "${ERUPT_BRINGUP_CONFIG:-}" ]; then
  MOUNTS+=(-v "$(cd "$ERUPT_BRINGUP_CONFIG" && pwd):/opt/erupt_ws/install/erupt_bringup/share/erupt_bringup/config:ro")
fi

docker run --rm "${MOUNTS[@]}" "$IMAGE" bash -c "
  set -e
  for robot in $ROBOTS; do python3 /tools/collect.py --out /out \$robot; done
"
