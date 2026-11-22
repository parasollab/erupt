# ERUPT AR Module (`com.erupt.plugin.ar`)

Optional. Augmented reality for ERUPT on Meta Quest 3 / 3S:

- **Marker tracking.** `AprilTagTracker` reads printed AprilTags (family tagStandard41h12)
  from the headset's passthrough camera: any number of tags, each with its own id and size.
- **Placing robots at markers.** A robot with a `MarkerAnchor` is bound to a tag. While the
  tag is in view the plugin shows where the robot would go, as a translucent copy of the
  robot; the user confirms with **Place at Marker** on the robot. Any number of robots, one
  tag per robot or one shared work-cell tag.
- **Room anchors.** `SceneAnchorCollisionBridge` turns the furniture from the headset's Space
  Setup into obstacles, which reach every robot's planning scene like any other obstacle.

A project gets this package only if it wants AR. Without it a project has no AprilTag code,
no AprilTag native library and no use of the passthrough camera; nothing else in ERUPT
refers to this package. Meta Quest only: there is no camera abstraction and no other headset.

## Install

The package needs two packages that it cannot bring along by itself:

| Package | Why | Source |
|---|---|---|
| `jp.keijiro.apriltag` | AprilTag detection. A fork with a `ProcessImage` overload that takes the full camera intrinsics, which the passthrough camera needs (its principal point is not at the image centre) | `https://github.com/courtneymcbeth/jp.keijiro.apriltag.git?path=/Packages/jp.keijiro.apriltag#1def475558924699f970fb57ff2b007250703526` |
| `com.meta.xr.mrutilitykit` | Meta's MR Utility Kit: the passthrough camera (`PassthroughCameraAccess`) and the room's scene anchors | `207.0.0` |

1. Add the package: Package Manager > **+** > *Install package from git URL*:
   `https://github.com/parasollab/erupt.git?path=/Packages/com.erupt.plugin.ar`
   (in the template project it is already in `Packages/`).
2. ERUPT offers to add the two packages above to the project manifest; say **Add**, or run
   `ERUPT > Project Setup` and press **Fix**. Until both are there the module's assembly is
   left out of the build (it carries the define constraints `ERUPT_APRILTAG` and `ERUPT_MRUK`),
   so the project compiles either way.
   The MR Utility Kit comes from Meta's package registry. The template project's manifest
   names that registry; in your own project add it first under *Project Settings > Package
   Manager > Scoped Registries* (name `Meta XR`, URL `https://npm.developer.oculus.com`,
   scope `com.meta.xr`). Without it the Package Manager looks for the package in the Unity
   Asset Store, which only works for an account that has added *Meta MR Utility Kit* to its
   assets.
3. On the headset the app needs the camera permission `horizonos.permission.HEADSET_CAMERA`
   and, for the room anchors, `com.oculus.permission.USE_SCENE` in the Android manifest, and
   passthrough enabled for the project and on the scene's `OVRManager`. The template project's
   `Assets/Plugins/Android/AndroidManifest.xml` and Meta project settings have them; in your
   own project switch on passthrough, passthrough camera access and scene support in Meta's
   settings (the `OVRManager` inspector, *Quest Features*) and let Meta's tools update the
   manifest. The sample scene's `OVRManager` asks for both permissions at start.

### Remove

Delete the folder `Packages/com.erupt.plugin.ar` and the two lines `"jp.keijiro.apriltag"`
and `"com.meta.xr.mrutilitykit"` from `Packages/manifest.json`. Nothing else has to change:
the rest of ERUPT compiles and its tests pass without them. (A project that has Meta's
all-in-one SDK, `com.meta.xr.sdk.all`, still gets the MR Utility Kit through that package;
ERUPT does not use it then.)

## Place a robot at a marker

1. **Print a tag.** `Documentation~/tags/` has ids 0 to 3 at a tag size of 0.10 m, ready to
   print; `Documentation~/make_apriltag_svg.py` makes other ids and sizes. Print at 100 %
   (no "fit to page") and fix the sheet flat where it will stay: on the table or the floor,
   next to the robot. The arrow on the sheet marks the tag's top edge.

   The **tag size** is the side of the inner square the detector measures, which for this
   family is the 5 central cells of the 9-cell printed pattern, *not* the full printed
   square: a 0.10 m tag is printed 18 cm wide. Measure the inner square with a ruler and use
   what you measure.

2. **Measure the offset** from the tag to the robot's base (the origin of the robot's root
   object, which is the origin of its URDF), in the tag's own directions:

   ```
              z  (towards the tag's top edge, the arrow)
              ^
              |
        +-----------+
        |           |
        |    tag    | ----> x  (towards the tag's right edge)        y points up, out of the tag
        |           |
        +-----------+
   ```

   Position: metres from the centre of the tag along x, y and z. Rotation: degrees; with
   (0, 0, 0) the robot faces the tag's top edge (the robot's front is ROS +x), and y turns it
   clockwise seen from above. A robot whose base stands 40 cm from the centre of the tag on
   the side of its bottom edge, looking over the tag, has position (0, 0, -0.4) and rotation
   (0, 0, 0).

3. **Bind it.** Add a `MarkerAnchor` to the robot: on the robot's root object or on a child
   (do not edit the robot prefab; add it to the instance in your scene), or on an empty
   object that you then drop the robot onto, as the sample scene's `Robot Slot` does. Enter
   the *Tag Id*, the *Tag Size* and the offset. With the anchor selected, the Scene view
   draws where the tag has to lie for the robot as it stands, so the offset can also be
   checked by eye. Leave *Tag Lies Flat* on for a tag on a horizontal surface: only its
   position and heading are then used and the robot stands upright. Switch it off for a tag
   on a wall or a slope.

   - *One tag per robot*: give each robot's anchor its own tag id.
   - *One tag for the work cell*: give every robot's anchor the same tag id and tag size, and
     each its own offset.

   The scene needs the `ArPlugin` prefab (the plugin and the tracker) next to the ERUPT root,
   a `PassthroughCameraAccess` and passthrough; start from the sample scene (Package Manager >
   ERUPT AR Module > Samples > AR Scene). The plugin tells the tracker which tags to look for.

4. **Confirm in the headset.** Look at the tag. A translucent copy of the robot appears where
   the tag puts it. Select the robot (any link or the end effector) and choose **Place at
   Marker**. The robot moves there and stays there, also when the tag is covered or taken
   away; to correct the placement, look at the tag again and place again. **Undo** puts the
   robot back where it was. The *AR* tab of the panel says what the tracker sees and, per
   robot, whether its tag is in view.

   The verb is offered only for a robot that has a marker anchor and whose tag is in view. A
   placement that cannot be done says why: the tag has not been seen yet, the tag is not in
   view, the robot is executing, or the robot is carrying an object.

## What placing does

Placing goes through core: `RobotInstance.PlaceBase(pose)` moves the robot's articulation root
and raises `BaseMoved`. Every robot plans in its own planning frame, which is part of the
robot and moves with it, so nothing on the ROS side is reconfigured; what the ROS side holds in
that frame is brought up to date instead:

- the MoveIt plugin sends every object of the environment to that robot's planning scene
  again, at its pose in the moved frame (the other robots' planning scenes are sent nothing),
  and objects that robot's ROS side itself put into the planning scene move with the robot;
- the robot's start, goal and plans, and its pick-and-place task, are dropped: they were made
  for the base where it was;
- placing is refused while the robot executes a trajectory or a pick-and-place solution, while
  a pick-and-place task is being planned for it, and while it carries an object.

A robot that uses a planning frame shared with other robots (a transform outside the robot)
can be placed too, but its ROS side then still has it at the old pose in that frame; the log
says so. Use the robot's own planning frame (the default of every shipped robot) for AR.

## Room anchors

Add `SceneAnchorCollisionBridge` and Meta's `MRUK` component to the scene (the sample has
them under `Room Anchors`) and run Space Setup on the headset once. Each piece of furniture
becomes an obstacle (a box at the anchor's bounds) and a coloured translucent box in the
view; *Publish Planes* adds the floor, the walls and the ceiling. The anchors are ordinary
environment objects, so a robot that is placed elsewhere gets them again in its own frame.

## Components

| Component | What it does |
|---|---|
| `ArPlugin` | The plugin (id `ar`): a session per robot, the proposal and its ghost, the verb `place-at-marker` on robot links and end effectors, the *AR* tab |
| `AprilTagTracker` | Reads the passthrough camera a few times a second, detects the tags it was told to look for, filters each tag's pose. `Track(id, size)`, `TryGetTag`, `IsVisible`, `TagObserved`; `InjectObservation` feeds a pose without a camera (editor, tests) |
| `MarkerAnchor` | Binds a robot to a tag: id, size, offset of the base |
| `MarkerMath` | The arithmetic, free of the camera: the pose filter, the tag and marker frames, the rescale that lets one detection pass serve tags of any size, the row flip, the intrinsics of a cropped image, the proposed base |
| `SceneAnchorCollisionBridge` | Room anchors as obstacles |

## What needs a headset

The tracker cannot run in the editor (there is no passthrough camera), and the tests cover
everything up to the camera. Check on the device: that tags are detected at all (if none is,
switch *Flip Image Vertically* on the tracker: a vertically mirrored image never decodes; a
*Debug Renderer* shows the frame the detector gets), the distance at which a tag of your size
is detected (*Decimation* trades range for speed), that the proposed robot stands where the
real one does (tag size, offset, which edge is the tag's top), and that the passthrough view,
the camera permission prompt and the room anchors appear.

## Tests

`Tests/` (PlayMode, on a fake bus, no camera): the tracking arithmetic, the tracker's
bookkeeping with injected observations, marker placement with zero, one and three robots in
both layouts, the refusals, undo, the prefab, and the plugin next to the MoveIt plugin.
`Tests/Editor` checks the sample scene as it lies in the package.
