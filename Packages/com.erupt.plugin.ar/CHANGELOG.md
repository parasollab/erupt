# Changelog

All notable changes to `com.erupt.plugin.ar` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- The AR tab is removed when the plugin is unregistered, like its verb. (Core removed the
  application modes; the module never used them.)

### Changed (where Meta's packages come from)

- The template project takes Meta's packages from Meta's own registry
  (`https://npm.developer.oculus.com`, a scoped registry for `com.meta.xr` in
  `Packages/manifest.json`) and lists the parts of the all-in-one SDK by name instead of
  `com.meta.xr.sdk.all`, which that registry does not carry. Same packages, same versions.
  A fresh clone therefore opens with any Unity account; before, the account had to own the
  packages in the Asset Store.

### Added

- The package: an optional AR module for Meta Quest. A project without it has no AprilTag
  code, no AprilTag native library and no use of the passthrough camera, and nothing else in
  ERUPT refers to it. It asks for `jp.keijiro.apriltag` (the fork with full camera intrinsics,
  pinned to a commit) and `com.meta.xr.mrutilitykit` through `erupt.requires`, so ERUPT offers
  to add them; its assembly `Erupt.Plugins.Ar` is left out until both are in the project.
- `AprilTagTracker`, ported from the lab's AR demo: tagStandard41h12 tags from the Quest
  passthrough camera (`PassthroughCameraAccess`, asynchronous GPU readback), one detection
  pass for any number of tags with their own ids and sizes. There is no longer a primary
  tag: every tag is listed, or added with `Track(id, size)`, and a tag that is not listed is
  ignored. Headset poses are lifted through the rig's tracking space.
- `MarkerMath`: the arithmetic of tracking and placement, separate from the camera (pose
  filter, tag and marker frames, size rescale, row flip, intrinsics of a cropped image,
  levelling, the proposed base).
- `MarkerAnchor`: binds a robot to a tag (id, size, the base's pose relative to the tag),
  for one tag per robot or one shared work-cell tag.
- `ArPlugin` (id `ar`): per robot, the proposed base pose while the robot's tag is in view,
  shown as a translucent copy of the robot; the verb `place-at-marker` on robot links and end
  effectors, offered only for a robot with a marker anchor whose tag is in view; refusals with
  reasons; undo; an *AR* tab with the tracker's and each robot's state.
- `SceneAnchorCollisionBridge`, moved here from the reference app (`Erupt.App`).
- Sample **AR Scene**: passthrough, the ERUPT root and plugins, the AR plugin with the
  tracker, the room anchors and a robot slot with a marker anchor.
- `Documentation~/tags`: printable tags 0 to 3 at 0.10 m, and `make_apriltag_svg.py`.

### Changed (against the lab's AR demo)

- A robot is no longer moved by a controller button to wherever the marker is: the detected
  pose is proposed and the user confirms it on the robot.
- The offset from the tag to the robot is data on the robot (`MarkerAnchor`) instead of a
  raycast to the surface under the marker and a fixed split between two robots.
