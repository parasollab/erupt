#!/bin/bash
# Turn the exported robot descriptions (out/) into the ERUPT robot packages
# (Packages/com.erupt.robot.*) with ERUPT's Robot Setup, in batch mode. Which description
# goes where, and under which name, is in packages.json next to this file.
#
#   Tools/robot-descriptions/setup-robots.sh            every robot in packages.json
#   Tools/robot-descriptions/setup-robots.sh UR5e,FR3   only these (asset names)
#
# Close the Unity editor first: only one Unity can have the project open. Running it again
# updates the prefabs and profiles in place and keeps their GUIDs.
#
# Environment:
#   UNITY   the Unity editor binary   (default: the Hub install of the project's version)
set -u
here="$(cd "$(dirname "$0")" && pwd)"
project="$(cd "$here/../.." && pwd)"
version="$(sed -n 's/^m_EditorVersion: //p' "$project/ProjectSettings/ProjectVersion.txt")"
if [ -z "${UNITY:-}" ]; then
  case "$(uname -s)" in
    Darwin) UNITY="/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity" ;;
    Linux)  UNITY="$HOME/Unity/Hub/Editor/$version/Editor/Unity" ;;
    *)      UNITY="C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe" ;;
  esac
fi
if [ ! -x "$UNITY" ]; then
  echo "Unity $version not found at '$UNITY'; set UNITY to the editor binary." >&2
  exit 2
fi

log="$project/Logs/robot-setup.log"
mkdir -p "$project/Logs"
only=()
[ -n "${1:-}" ] && only=(-only "$1")

cd "$project" || exit 2
"$UNITY" -batchmode -nographics -quit -projectPath "$project" -logFile "$log" \
  -executeMethod Erupt.Editor.RobotSetup.RunFromCommandLine \
  -settingsList Tools/robot-descriptions/packages.json ${only[@]+"${only[@]}"} >/dev/null 2>&1
status=$?
grep -E '^\[RobotSetup\]' "$log" | sort -u
if ! grep -q '^\[RobotSetup\]' "$log"; then
  echo "Robot Setup did not run (compile error, licence, project already open?). See $log" >&2
  exit 1
fi
# Unity may exit non-zero after a clean run; a robot that could not be written is what counts.
if grep -q 'Robot Setup could not write' "$log"; then exit 1; fi
exit 0
