#!/bin/bash
# Run ERUPT's live tests: the reference app's real scenes, in batch mode, against a running
# ROS 2 stack. See Assets/Erupt.App/Tests/Live/README.md.
#
#   Tools/live-tests.sh single               one robot in the root namespace (KitchenFR3)
#   Tools/live-tests.sh arms                 three robots: left, right, third (MultiArmFR3)
#   Tools/live-tests.sh offline              nothing listening on the endpoint
#   Tools/live-tests.sh single <filter>      only the tests whose full name matches <filter>
#
# The scenes hold FR3s. For another shipped robot, name the robot the ROS side was started
# with; its prefab is put in the FR3's place as the scene loads:
#
#   ERUPT_LIVE_ROBOT=ur5e Tools/live-tests.sh single
#   ERUPT_LIVE_ARMS="left=fr3 right=ur5e third=xarm7" Tools/live-tests.sh arms
#
# Environment:
#   ERUPT_LIVE_ROS           host:port of the ROS-TCP endpoint       (default 127.0.0.1:10000)
#   ERUPT_LIVE_ROBOT         single / offline: the ROS robot name    (default: the scene's FR3)
#   ERUPT_LIVE_ARMS          arms: id=robot for each arm             (default: the scene's FR3s)
#   ERUPT_LIVE_WAIT_FOR_ROS  offline only: seconds the second test waits for the stack to be
#                            started while it runs                   (default: that test is skipped)
#   UNITY                    the Unity editor binary                 (default: the Hub install of the project's version)
#   ERUPT_BUILD_TARGET       build target the tests run under        (default: the editor's own
#                            platform, e.g. StandaloneOSX; the project defines ROS2 for
#                            Standalone and for Android. "Android" runs them as the headset build)
#   ERUPT_LIVE_RESULTS       directory for the log and the results   (default: ./Logs/live-tests)
set -u
scenario="${1:-}"
filter="${2:-Erupt.App.Live.Tests}"
case "$scenario" in
  single|arms|offline) ;;
  *) echo "usage: $0 <single|arms|offline> [test filter]" >&2; exit 2 ;;
esac

project="$(cd "$(dirname "$0")/.." && pwd)"
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

out="${ERUPT_LIVE_RESULTS:-$project/Logs/live-tests}"
mkdir -p "$out"
# One set of files per robot, so runs with different robots do not overwrite each other.
name="$scenario"
[ "$scenario" != arms ] && [ -n "${ERUPT_LIVE_ROBOT:-}" ] && name="$scenario-$ERUPT_LIVE_ROBOT"
results="$out/$name.xml"
log="$out/$name.log"
rm -f "$results"

export ERUPT_LIVE_ROS="${ERUPT_LIVE_ROS:-127.0.0.1:10000}"
export ERUPT_LIVE_SCENARIO="$scenario"
echo "ERUPT live tests: scenario '$scenario'${ERUPT_LIVE_ROBOT:+, robot $ERUPT_LIVE_ROBOT}${ERUPT_LIVE_ARMS:+, arms $ERUPT_LIVE_ARMS} against $ERUPT_LIVE_ROS (log: $log)"
# The editor's own platform, which is what a project opens under when it was never switched.
if [ -z "${ERUPT_BUILD_TARGET:-}" ]; then
  case "$(uname -s)" in
    Darwin) ERUPT_BUILD_TARGET=StandaloneOSX ;;
    Linux)  ERUPT_BUILD_TARGET=StandaloneLinux64 ;;
    *)      ERUPT_BUILD_TARGET=StandaloneWindows64 ;;
  esac
fi
"$UNITY" -batchmode -nographics -projectPath "$project" -buildTarget "$ERUPT_BUILD_TARGET" \
  -runTests -testPlatform PlayMode \
  -testFilter "$filter" -testResults "$results" -logFile "$log" >/dev/null 2>&1
# Unity may exit non-zero after a clean run; the results file is what counts.

if [ ! -f "$results" ]; then
  echo "No results: Unity did not get as far as running the tests (compile error, licence, project already open?). See $log" >&2
  exit 1
fi
python3 - "$results" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
ran = [c for c in root.iter('test-case') if c.get('result') != 'Skipped']
for case in ran:
    line = f"{case.get('result'):8} {case.get('name')}  ({float(case.get('duration') or 0):.1f} s)"
    if case.get('result') != 'Passed':
        line += "\n         " + (case.findtext('failure/message') or '').strip().replace('\n', '\n         ')[:1500]
    print(line)
passed = sum(1 for c in ran if c.get('result') == 'Passed')
print(f"{passed}/{len(ran)} passed")
if not ran:
    print("Nothing ran: is the scenario right, and does the filter match a test?")
sys.exit(0 if ran and passed == len(ran) else 1)
PY
