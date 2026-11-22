# Starter Scene

A scene with everything ERUPT needs and no robot:

- `ERUPT`, the ERUPT root (`EruptCore.prefab`): plugin host, selection, environment, tier UI, desktop controls
- `XR Rig`: an XR Origin with ERUPT's controller backends (`XriControllerBackend`), hand
  backends (`OpenXrHandBackend`), the wrist anchor of the tier 1 bar (Menu, Undo, Redo) and
  the collision haptics, which the Settings tab adjusts; XR Interaction
  Toolkit swaps controllers and hands as you pick the controllers up or put them down
- `MoveItPlugin`, `MtcPlugin`, `DemonstrationCollectionPlugin`
- `CollisionObjectListener`: the MoveIt planning-scene mirror
- `Table` (its top is an obstacle the planners know) and `Floor`, built from primitives
- `origin`, the scene's world origin, and a light

## Use

1. Run `ERUPT > Project Setup` once and press **Fix** (packages, tags, the `ROS2` define,
   TextMesh Pro's resources, the XR Interaction Toolkit samples the rig is built from).
2. Open `StarterScene`.
3. Drag a robot prefab onto the table, for example
   `Packages/com.erupt.robot.universal-robots/Robots/UR5e.prefab` (y = 0.75 puts its base on
   the table top). Nothing else has to be wired.
4. Start the ROS side for that robot (`ROBOT=ur5e docker compose -f docker/compose.yaml up erupt`
   in the ERUPT ROS workspace), set the ROS IP under `Robotics > ROS Settings` when ROS is on
   another machine, and press Play. Without a headset the view starts on the robot and
   the mouse and the keyboard drive the scene (the keys are shown in the Game view).

The scene needs the MoveIt, MTC and demonstration plugin packages
(`com.erupt.plugin.moveit`, `.mtc`, `.demonstration`); a plugin that is not in the project
shows as a missing prefab, which you can delete. The rig is built from the XR Interaction
Toolkit samples *Starter Assets* and *Hands Interaction Demo* and the XR Hands sample
*HandVisualizer*; Project Setup imports them.
