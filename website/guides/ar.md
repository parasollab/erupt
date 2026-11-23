# AR placement (optional)

With the AR module, the virtual robot stands where the real one does. You fix a printed tag
next to the robot, the headset's camera finds it, and ERUPT offers to put the robot there. The
module can also turn the furniture from the headset's Space Setup into obstacles.

The module is optional. A project without it contains none of its code and does not use the
headset camera. It works on Meta Quest 3 and 3S.

## Try the tabletop workbench

Open `Assets/Scenes/ARWorkbenchFR3.unity` in the ERUPT project. It has a Franka Research 3
and one movable block. Start the single simulated FR3 from the [quick start](quick-start.md)
when you want to preview a MoveIt plan.

- **In the editor**, press Play. Left-click the block to select it and right-drag it into the
  arm’s path. Select the blue end-effector handle and choose **Set Start**. Drag the handle
  to a reachable pose on the far side of the block, choose **Set Goal**, then **Plan** to
  watch the preview.
- **On Quest**, print `Documentation~/tags/tag41_12_id0_100mm.svg` from the AR package at
  100 %. Lay it flat where the arm’s base should stand, with the arrow pointing in the
  direction the arm should face. In **File > Build Profiles**, put the workbench first in
  your Android profile’s scene list and choose **Build And Run**. Allow camera access, look
  at the tag and press **Place here** beside the translucent arm’s base.
  Select and grab the block to put it on your table, then set the start and goal as above.

The block stays where the user puts it when the arm is placed again. The printed tag is
enough for this demo.

## What you need

- The package `com.erupt.plugin.ar`. The ERUPT project already has it. In your own project,
  add it by git URL like the other packages:
  `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.ar`
- Two packages the module depends on: an AprilTag detector and Meta's MR Utility Kit. The
  ERUPT project already has both. In your own project, first tell Unity where Meta's
  packages are: open **Edit > Project Settings > Package Manager** and add a scoped registry
  with the name `Meta XR`, the URL `https://npm.developer.oculus.com` and the scope
  `com.meta.xr`. Then ERUPT offers to add the two packages when it sees the module; press
  **Add**, or run **ERUPT > Project Setup** and press **Fix**.
- A printer and a ruler.

## Set up

1. **Print a tag.** The package folder `Documentation~/tags` has four tags ready to print.
   Print one at 100 % (no "fit to page") and fix the sheet flat on the table or the floor
   next to the robot, where it will stay. The arrow on the sheet marks the tag's top edge.

2. **Measure the tag.** The size ERUPT needs is the side of the inner square, which is the
   middle five of the nine cells across the printed pattern. For the ready-made tags that is
   10 cm, with the whole pattern 18 cm wide. Use what your ruler says.

3. **Measure where the robot's base is, seen from the tag.** Stand so that the tag's top edge
   points away from you, and measure from the centre of the tag to the centre of the robot's
   base:

   | Direction | Value |
   | --- | --- |
   | To the right | x, in metres |
   | Up, out of the tag | y |
   | Forward, toward the top edge | z |

   With no rotation the robot faces the tag's top edge. A robot that stands 40 cm behind the
   tag's bottom edge and looks over the tag has position (0, 0, -0.4) and rotation (0, 0, 0).

4. **Open the AR scene.** In the Package Manager, select **ERUPT AR Module**, open
   **Samples** and import **AR Scene**. Open it and drag a robot prefab onto the object
   named `Robot Slot`.

5. **Enter the numbers.** Select `Robot Slot`. On its **Marker Anchor** component, enter the
   tag's id (printed on the sheet), the tag size and the position and rotation from step 3.
   The Scene view draws where the tag has to lie for the robot as it stands, so you can
   check the numbers by eye. Leave **Tag Lies Flat** on for a tag on a horizontal surface.

6. **Build for the headset** and start the ROS side as usual.

7. **Confirm in the headset.** Look at the tag. A translucent copy of the robot appears where
   the tag puts it. Press **Place here** beside the translucent base. The robot moves there and
   stays, also when the tag is covered or removed. **Undo** puts it back. To correct the
   placement, look at the tag again and place again.

The robot is never moved without your confirmation.

## Several robots

Give every robot its own slot with a **Marker Anchor**. Each preview has its own button,
for example **Place left here**. Pressing it places only that robot.

- **One tag per robot:** each anchor gets a different tag id.
- **One tag for the whole cell:** every anchor gets the same tag id and size, and its own
  position and rotation.

## When placing is refused

ERUPT says why. The tag has not been seen yet or is not in view; the robot is executing a
motion; or the robot is carrying an object. Wait, or look at the tag, and try again.

Placing clears that robot's start, goal and plans, because they were made for where the
robot stood before. Obstacles are sent to the robot's planner again, relative to its new
position. Nothing has to be changed on the ROS side.

## Room furniture as obstacles

Run Space Setup on the headset once. With the AR scene, every piece of furniture it knows
becomes an obstacle for the planner, shown as a translucent box.

## If no tag is found

This module could not be tested without a headset camera, so expect to adjust it the first
time:

- **No tag is detected at all.** On the `ArPlugin` object, switch **Flip Image Vertically**
  on the tracker. An upside-down camera image never decodes.
- **Tags are only detected from close up.** Lower **Decimation** on the tracker, or print a
  larger tag.
- **The robot appears turned by half a turn.** The tag's top edge is the other one; turn the
  sheet or add 180 degrees to the rotation.
- **The robot appears at the wrong distance.** Check the tag size: it is the inner square,
  not the whole pattern.

The panel's **AR** tab shows what the tracker sees and, for each robot, whether its tag is in
view.

## Remove the module

Delete the folder `Packages/com.erupt.plugin.ar` and the lines `jp.keijiro.apriltag` and
`com.meta.xr.mrutilitykit` from `Packages/manifest.json`. The rest of ERUPT works without
them.
