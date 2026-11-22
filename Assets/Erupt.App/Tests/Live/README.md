# Live tests: the app against a real ROS 2 stack

Every other ERUPT test runs on `FakeRosBus`. These load the reference app's real scenes on the
real `LiveRosBus`, in batch mode, drive the app through the entry points the UI uses (the tier 2
verbs, the Scene tab, the plugins' public members) and assert on what the ROS side reports back
(`get_planning_scene`, `joint_states`, action results). They are the check that the two sides
agree on names, namespaces, frames, units and message layouts, which a fake bus cannot give.

They are **opt-in**: without the environment variables below every test is ignored, so the normal
suites and CI never open a socket.

## Running

Start a ROS side (see the README of the ROS workspace, `erupt_ws`), then from this repository:

```bash
# one FR3 in the root namespace: KitchenFR3
docker compose -f docker/compose.yaml up -d erupt                      # in erupt_ws; give it ~40 s
Tools/live-tests.sh single

# three FR3s, each at the origin of its own world (MultiArmFR3 gives every robot its own
# planning frame, so the ROS side is started without poses)
ARMS="left=fr3 right=fr3 third=fr3" \
  docker compose -f docker/compose.yaml --profile arms up -d arms       # in erupt_ws
Tools/live-tests.sh arms

# the app started before ROS: stop the ROS side first
docker compose -f docker/compose.yaml --profile arms down               # in erupt_ws
Tools/live-tests.sh offline
# ... and ROS appearing later: start the single-arm stack about a minute after this was launched
ERUPT_LIVE_WAIT_FOR_ROS=240 Tools/live-tests.sh offline
```

Only one of the two stacks can hold port 10000. `Tools/live-tests.sh <scenario> <filter>` runs the
tests whose full name matches the filter (for example `T08`). The log and the NUnit results are
written to `Logs/live-tests/`. The Unity editor must not have the project open.

### Any shipped robot

The scenes hold FR3s. To run the suite with another robot, start the ROS side with it and name
it; as the scene loads, the FR3 is replaced by that robot's shipped prefab (the profile whose
`rosRobotName` is the name), at the same place, with the same id and namespace and nothing else
done to it. That is also the check that a robot prefab dropped into a scene needs no wiring.

```bash
ROBOT=ur5e docker compose -f docker/compose.yaml up -d erupt            # in erupt_ws
ERUPT_LIVE_ROBOT=ur5e Tools/live-tests.sh single                        # results in Logs/live-tests/single-ur5e.*

ARMS="left=fr3 right=ur5e third=xarm7" docker compose -f docker/compose.yaml --profile arms up -d arms
ERUPT_LIVE_ARMS="left=fr3 right=ur5e third=xarm7" Tools/live-tests.sh arms
```

Robot names: `fr3`, `ur5e`, `ur10e`, `kinova_gen3`, `xarm7`, `iiwa14`.

The script runs the editor under its own platform's Standalone build target (`StandaloneOSX`,
`StandaloneLinux64`, `StandaloneWindows64`), which is what a fresh clone opens under. The
project's `ROS2` scripting define is set for Standalone and for Android; under a target without
it the connector speaks the ROS 1 protocol and the handshake fails. `ERUPT_BUILD_TARGET=Android`
runs the suite as the headset build is compiled.

By hand (any OS, or from the Test Runner window): set `ERUPT_LIVE_ROS=host:port` and
`ERUPT_LIVE_SCENARIO=single|arms|offline` in the environment Unity is started from, and run the
PlayMode tests of the assembly `Erupt.App.Live.Tests`. Run them on their own: they load scenes
in single mode and leave the app's robots registered.

| Variable | Meaning |
|---|---|
| `ERUPT_LIVE_ROS` | `host:port` of the ROS-TCP endpoint; unset = every live test is ignored |
| `ERUPT_LIVE_SCENARIO` | which stack is behind it: `single`, `arms` or `offline` |
| `ERUPT_LIVE_ROBOT` | `single` / `offline`: the ROS robot behind the endpoint (default: the scene's FR3) |
| `ERUPT_LIVE_ARMS` | `arms`: `id=robot` for each arm, as the ROS side takes it (default: the scene's FR3s) |
| `ERUPT_LIVE_WAIT_FOR_ROS` | `offline` only: seconds the second test waits for the stack to be started |

## What is checked

`SingleArmLiveTests` (scenario `single`) and `ThreeArmLiveTests` (`arms`) run the same checks,
the latter for every arm in its own namespace plus what only several arms can get wrong.

| Test | Checks |
|---|---|
| T01 | the connection comes up; robots, namespaces, plugins and sessions are as expected |
| T02 | every joint ROS reports is one Unity has, under the name rule both ways; a Unity joint ROS does not report follows one it does (a gripper's mimic joints); mirrored `joint_states` equal the Unity pose, followers included; nothing is clamped |
| T03 | planner discovery answers |
| T03b | the robot stands in its planning frame where its ROS side has it: the root link of MoveIt's robot model, seen from the planning frame, is at the same pose in Unity and through the ROS side's TF |
| T04 | every Unity obstacle is in MoveIt at the pose of its Unity transform in the planning frame (1 mm, 0.1°), with its size; mesh vertices coincide. Arms: also relative to each base link |
| T05 | add, move, turn, resize (box, sphere, cylinder on its side), delete, undo, redo |
| T05b (single) | the ROS robot is brought to the profile's `ready` pose; mirroring it clamps nothing |
| T06 | 10 s at rest: no message on `collision_object` or `collision_objects_ros` |
| T07 | an object added, moved and removed on the ROS side follows in Unity (and in the other arms' planning scenes), and nothing is sent back to where it came from |
| T08 | set start, drag the end effector, set goal, plan, preview, execute; the ROS robot reaches the goal, the Unity robot follows and can be posed again. Arms: no other robot moves |
| T08b (single) | the same flow with a simulated mouse and keyboard, as in the editor without a headset: a left click selects the end effector, clicks on Set Start, Set Goal and Plan at the buttons' places on screen, a right-drag moves the arm, the plan is the selection and a click on Execute sends it; the ROS robot arrives and the Unity robot follows. Then `Tab` opens the panel, tier 1's Menu button closes and opens it, the MoveIt tab's mirror switch, Add Cube on the Scene tab (the cube reaches MoveIt's planning scene) and Delete on the cube's menu |
| T09 | a post in the way: a plan that clears it, or a refusal; a goal inside an obstacle: a refusal the user is told |
| T10 | the gripper verb closes and opens the ROS gripper (arms: only the one it was pressed on) |
| T11 | pick and place of a cube in the air: record, stage tree and solutions, preview, execute; attach and detach are mirrored and the object ends at the demonstrated pose on both sides. Arms: while another arm plans and executes; also on the arm that is turned around |
| T11c | the everyday case: the same cube standing on a surface (a published obstacle of the scene under the two spots; a slab is added where there is none) is picked up and put down on it somewhere else. Whether the hand can reach an object that rests on something is the ROS side's grasp: where the fingertips would reach into the surface (the Robotiq 2F-85 on the UR5e, UR10e, Gen3 and iiwa 14) the pick-and-place server takes the object further back along the approach ("The hand takes … 16 mm further back than configured" in its log). It passes with all six shipped robots |
| T12 | a demonstration is recorded, replayed and published; what goes on the wire is well formed |
| T13 | reloading the scene keeps one connection and resets the planning scene |
| T99 | still connected; no exception, no error, no unrecognised echo, no pose in an unknown frame |

`OfflineLiveTests` (`offline`): with nothing listening the app raises no exception and answers
plan, gripper and pick-and-place requests with a refusal at once (T01); when the stack is
started later it connects, sends the environment (including what was added meanwhile) and works (T02).

## Notes for your own ROS setup

- Nothing in the tests names a robot: planning groups, controller, gripper and joint names come
  from the robot's profile (`Packages/com.erupt.robot.*/Robots/*.asset`). They assume a robot of
  `erupt_bringup`, mock hardware that reaches a commanded pose, and MoveIt with the
  `execute_trajectory` action and, for T11, `mtc_pick_place_server`.
- They change the planning scene and move the robot. Objects they add are removed again; what
  an aborted run leaves (`unity_cube_<n>`, `live_*`) is removed at the start of the next.
- A failure message quotes both sides (what MoveIt holds, what Unity has); the Unity log has the
  app's own account, the ROS side's log the other half.
