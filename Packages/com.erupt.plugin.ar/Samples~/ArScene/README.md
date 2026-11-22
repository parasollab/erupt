# AR Scene

A passthrough scene for Meta Quest 3 / 3S: the user sees the room, and ERUPT's robots stand in it
where their printed AprilTags say.

- `ERUPT`, the ERUPT root (`EruptCore.prefab`), and `XR Rig`, `MoveItPlugin`, `MtcPlugin`,
  `DemonstrationCollectionPlugin`, `CollisionObjectListener`, `origin`: as in the Starter Scene
  of ERUPT Core, without the table and the floor (the room is the real one)
- `Passthrough`: Meta's `OVRManager` with passthrough switched on and the `OVRPassthroughLayer`
  drawn under the scene; the main camera clears to transparent black
- `Passthrough Camera`: Meta's `PassthroughCameraAccess`, the camera the tags are read from
- `ArPlugin` (`ArPlugin.prefab`): the AR plugin and the `AprilTagTracker`, wired to the
  passthrough camera and to the rig's camera offset
- `Room Anchors`: Meta's `MRUK` and the `SceneAnchorCollisionBridge`, which turns the furniture
  from the headset's Space Setup into obstacles the planners know
- `Robot Slot`: an empty object with a `MarkerAnchor` (tag 0, 0.10 m, no offset)

## Use

1. Run `ERUPT > Project Setup` and press **Fix** (it offers the packages the AR module needs).
2. Open `ArScene` and drag a robot prefab onto `Robot Slot` in the Hierarchy, for example
   `Packages/com.erupt.robot.franka/Robots/FR3.prefab`. The slot's `MarkerAnchor` binds to the
   robot that is dropped onto it. For more robots, duplicate the slot and give each robot its
   own id and ROS namespace (`RobotInstance`), and each slot its own tag id, or the same tag
   with another offset.
3. Print `Documentation~/tags/tag41_12_id0_100mm.svg` at 100 %, measure the inner square, and
   enter the size and the offset of the robot's base on the `MarkerAnchor`. The package README
   has the steps and the picture of the offset.
4. Build for the headset (Android). In the headset, run Space Setup once, allow the camera and
   the scene permissions when the app asks, look at the tag, select the robot and choose
   **Place at Marker**.

Passthrough, the passthrough camera and the room anchors exist only on the headset. In the
editor the scene plays with a black background and the tracker does nothing.

The scene needs the MoveIt, MTC and demonstration plugin packages; a plugin that is not in the
project shows as a missing prefab, which you can delete. The rig is built from the XR
Interaction Toolkit samples *Starter Assets* and *Hands Interaction Demo* and the XR Hands
sample *HandVisualizer*; Project Setup imports them.
