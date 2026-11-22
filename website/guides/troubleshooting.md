# Troubleshooting

Find the symptom, then work through the causes in order; the most common one is first.

## The robot does not react to ROS: planning is refused, or Execute moves nothing

Unity is not connected to ROS. The Unity console then repeats `Connection to ... failed`.

1. **Is the ROS side running?** In the terminal where you started it, the log should have
   settled. `docker ps` shows the container; `docker compose -f docker/compose.yaml logs erupt`
   shows its log.
2. **Is the address right?** Open **Robotics > ROS Settings**. In the editor, with Docker on the
   same computer, the address is `127.0.0.1` and the port 10000. In the headset, the address
   must be your computer's address on the local network; `127.0.0.1` there means the headset
   itself.
3. **Can the headset reach the computer?** Both must be on the same network, and the
   computer's firewall must let port 10000 through. Guest and university networks often stop
   devices from talking to each other; a phone hotspot or your own router avoids that.
4. **Was ROS started after the app?** That is fine: ERUPT connects when ROS appears, sends
   the scene, and each robot takes the pose its ROS robot reports.
5. **Several robots, and only one does not react?** Its **Ros Namespace** does not match the
   namespace of its ROS stack. See [Several robot arms](multi-arm.md).

## The robot in Unity is in a different pose from the real robot

Each robot takes the pose of its ROS robot once, when the connection comes up. After that you
pose it freely, so the two differ as soon as you drag the arm; that is intended. To make the
robot follow the ROS robot all the time, switch **Mirror joints** on in the MoveIt tab.

If the robot never took the ROS robot's pose, no joint states arrive for it: check the
connection (above) and that the joint names on both sides are the same.

## Plan does nothing, or is refused

ERUPT says why in a message next to the menu. The usual reasons:

| Message says | What to do |
| --- | --- |
| ROS is not connected | See the section above |
| The planner found no path | Move the goal, or move an obstacle out of the way. A goal inside an obstacle is always refused |
| The start is in collision | The robot's start pose touches an obstacle in the planning scene. Move the obstacle, or set a different start. The `move_group` log on the ROS side names the two things that touch |
| A joint would leave its limits | The plan came from a robot description that differs from the one in Unity. See *Joint limit warnings* below |
| Set a start first | Select the end effector and choose **Set Start** before **Plan** |

When **Execute** fails, the message at the plan gives the reason the ROS side reported.

After the robot has executed a motion, press **Set Start** again before the next plan.
Otherwise the next plan starts from the old start pose.

## The robot stops while I drag it, and one link changes colour

A joint reached its limit. The tinted link is the one that stopped, and the message names the
joint. Drag back, or choose a goal the arm can reach differently. ERUPT never moves a joint past
its limits, whatever the source of the motion.

## Joint limit warnings while the robot mirrors ROS

The robot on the ROS side reported a joint position outside the limits ERUPT has for it. That
means the two sides use different robot descriptions. Use the same description on both sides:
the robot packages are generated from the descriptions the ROS workspace launches, so a robot
from `com.erupt.robot.*` matches the robot of the same name in `erupt_ws`.

## Obstacles in the headset and in MoveIt disagree

- **An obstacle I added is missing in MoveIt.** It is sent when the connection is up. If you
  restarted the ROS side, ERUPT sends everything again when it reconnects; give it a few
  seconds.
- **Everything is offset by the same amount.** The robot's planning frame in Unity does not
  match where the ROS side thinks the robot stands. With the robot prefabs as shipped this
  cannot happen, because every robot carries its own planning frame. If you assigned a shared
  planning frame to several robots, the ROS side must be started with the same base poses; see
  [Several robot arms](multi-arm.md).

## Pick and place finds no solution

Open the task in the MTC tab. The stage tree shows which stage failed and how often, and
selecting a failure shows the robot in the state MoveIt rejected, with the reason.

| Stage that fails | Likely reason |
| --- | --- |
| *grasp pose IK* | The object is out of reach, wider than the open gripper, or lying on its side (the hand comes in along the object's own up axis). The server log says what the hand would touch |
| *attach object* | The object touches something the server could not account for; look at the server log for a warning about the object |
| *place pose IK*, *lower object* | The place pose is out of reach, or more than 2 cm inside another object |

## Nothing reacts to clicks in the headset, but the editor works

Menus in the scene need the XR rig with its ray interactors and an event system that takes
XR input. A scene built from scratch often lacks them, and the mouse works without. Start
from the Starter Scene sample, or copy its rig and event system into your scene. If the scene
is fine, run **ERUPT > Project Setup** to check the project's XR settings.

## The app stops receiving data after a while

The endpoint on the ROS side restarts itself if it crashes, and ERUPT reconnects and sends the
scene again. If the connection does not come back within a few seconds, restart the ROS side.

## Unity reports compile errors after adding the packages to my own project

Run **ERUPT > Project Setup**. It adds the packages ERUPT depends on (the ROS connector and the
URDF importer cannot be declared as ordinary dependencies), the `ROS2` scripting define and the
tags the code uses. See [Add ERUPT to your own project](install-package.md).

## Still stuck

Open an issue on GitHub with the Unity console output and, for anything involving ROS, the log
of the ROS side.
