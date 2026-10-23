# MTC plan diagnostics (headless)

`MtcPlanProbe.cs` is an editor-only probe, kept out of `Assets/` so it is not part of the project.
To use it: copy it to `Assets/Editor/`, close the Unity editor, have the ROS stack (move_group,
`mtc_pick_place_server`, ROS-TCP-Endpoint at the IP in `ROSConnectionPrefab`) running, then

```
"/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics \
  -projectPath "$PWD" -executeMethod MtcPlanProbe.Run -logFile /tmp/probe.log
grep '\[PROBE\]' /tmp/probe.log
```

It plays `FactoryDemo`, waits for `PickPlaceClient` to be ready, dumps `/get_planning_scene`
(objects, primitives, robot joints), then sweeps 24 grasp yaws through `/compute_ik` and
`/check_state_validity` for a side and a top-down grasp, reporting IK success and the colliding
body pairs. The `Plan(...)` helper sends a real `/pick_place` goal and prints per-stage
solved/failed counts. Delete the copy from `Assets/Editor/` afterwards.

Found with it (2026-10-12): the demo `grasp_frame_transform` is a side grasp, so every grasp IK
collided hand/link6/link7 with the table; `pick_place_server.launch.py` now overrides it top-down.

The probe's `Go()` is edited per investigation; the archived version plans the first blank, prints
each solution segment's joints and scene-diff attach/detach ops, then plays the Unity preview with
`MTCTrajectoryPlayer.verboseLog` on while logging the part's parent and pose every 250 ms. Earlier
variants (kept in the session notes) spawned a molded part and planned it off its tray slot, and
executed a solution while logging `/attached_collision_objects_ros` and `/collision_objects_ros`.

## Timing the attach/detach notices against the robot (`detach_timing_probe.py`)

Unity learns about attach/detach from `planning_scene_watcher.py` on `/attached_collision_objects_ros`;
if that lags the robot, the carried part is "lifted back up" after the hand opens and snaps to its
place pose only when the notice lands. The probe speaks the ROS-TCP-Endpoint protocol itself (no
Unity, no ROS install needed): it subscribes to `/joint_states`, `/attached_collision_objects_ros`,
`/collision_objects_ros` and `/monitored_planning_scene`, reads the scene over `/get_planning_scene`,
picks the topmost `blank_*` and plans+executes placing it back where it is via the deprecated
`/pick_place_task` topic, then prints a timeline of robot-motion windows against the notices.

```
python3 -I Docs/mtc_diagnostics/detach_timing_probe.py <endpoint ip> [record seconds]
```

Needs the full stack up (move_group, `mtc_pick_place_server`, the watcher, ROS-TCP-Endpoint on port
10000). A healthy run shows the ACO REMOVE and the blank's CO ADD inside the gap between the
"open hand" and "retreat" motion windows; a lagging watcher shows them after the last window.

## Robot placement phase (`PlacementProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, run **without** `-nographics` (the XR ray line renderers
crash the headless GfxDevice when the probe's camera renders):

```
PROBE_OUT=/tmp/probe "/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" -batchmode \
  -projectPath "$PWD" -executeMethod PlacementProbe.Run -logFile /tmp/probe.log
grep '\[PROBE' /tmp/probe.log
```

No ROS needed. It plays `FactoryDemo` and checks the `RobotPlacementController` flow: starts in
Placing with the menus hidden and every `CollisionObjectPublisher` paused, the visualizer holding
the baked map; `Nudge`/`Rotate` move the fr3 root, its root `ArticulationBody` (TeleportRoot
survives the physics steps), `BaseTransform` and the pedestal; the six reach markers re-sample
(moving the robot away turns targets unreachable, the nudge clamps to the cell bounds); `Reset`;
`Confirm` shows the menus, unpauses and flags every room publisher for a fresh ADD, re-enables the
ROS map after the delay; `BeginReposition` returns to Placing. Screenshots land in `PROBE_OUT`
(`placing_default`, `placing_moved`, `placing_far`, `placement_panel`, `confirmed`). Verified
2026-10-20: 40 checks, 0 failures, with ROS-TCP-Endpoint up the ADDs went out within 100 ms of Confirm.

## Vise load (`ViseProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, run **without** `-nographics`; no ROS needed:

```
PROBE_OUT=/tmp/probe "/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" -batchmode \
  -projectPath "$PWD" -executeMethod ViseProbe.Run -logFile /tmp/probe.log
grep '\[PROBE' /tmp/probe.log
```

It plays `FactoryDemo`, confirms the robot placement, opens the doors and takes the top billet through
the load the robot would do: hovers it 4.5 cm over the parallels, 14 degrees skewed and off the clamp
axis (5 cm side across the jaws, i.e. world rotation = yaw only; the baked billets are yawed -90 with
the base), hands it to `ReleasedPartGravity.Drop` as the detach would, and checks it lands flat on the
parallels with the rest gap and is kinematic again. `MachineVise.Clamp` then has to centre it on the
clamp axis and square it to the plates while leaving its position along the jaws alone; a 40 degree
load must come out `ClampedPoor` ("clamped on its corners"), closing on nothing `Empty`. The machine
panel's vise lines are read back. It also lists any collider inside the jaw window and whatever the
settled part touches, which is how the first run's "landed on the back jaw" turned out to be the
probe's own hover pose. Screenshots in `PROBE_OUT`: `vise_open`, `vise_hover`, `vise_dropped`,
`vise_clamped`, `vise_clamped_close`, `vise_clamped_poor`, `machine_panel`. Verified 2026-10-21:
28 checks, 0 failures.

## Planning into the vise (`VisePlanProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, `-nographics` is fine, ROS stack up (move_group, `mtc_pick_place_server`,
ROS-TCP-Endpoint at the prefab IP):

```
PROBE_BASE_DX=0.4 "/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics \
  -projectPath "$PWD" -executeMethod VisePlanProbe.Run -logFile /tmp/plan.log
grep '\[PROBE\]' /tmp/plan.log
```

It plays `FactoryDemo`, puts the robot on the placement phase's ideal spot (plus `PROBE_BASE_DX` / `PROBE_BASE_DZ`
metres, world), confirms, opens the doors, dumps the planning scene (the `table` compound's primitives included),
asks `/check_state_validity` whether the ready pose is collision-free, then sends real `/pick_place` goals: the top
billet into the open jaws with its 5 cm side across (A), with its 8 cm side across (B) and onto the conveyor's drop
anchor as a control (C), printing per-stage solved/failed counts and the first failure comments. After A it sweeps
`/compute_ik` + `/check_state_validity` over 24 yaws of the top-down grasp at the place pose, 15 cm above it (the
retreat) and at the pick. `PROBE_GRID="x,z;x,z;..."` instead re-confirms the robot at each base pose and runs only
the ready-pose check and the two sweeps (the stock stand and conveyor do not follow the robot in the probe; in a
relayout they do).

Found with it (2026-10-21): from the authored spot (x -0.4, left of the door opening) nothing plans. The ready pose
has `panda_hand` 1.9 cm inside `vf2_body` (the wall left of the opening; the doors make no difference), so every
connect stage fails, and the place over the vise has `panda_link5` through the parked left door / the wall for all
24 yaws, because the forearm must cross the opening's left edge to hold the hand vertical over a vise 24 cm behind
the door plane. Grid: x <= -0.1 fails on link5, x >= 0.2 at z 1.0 (and everything at z 0.9) cannot rise 15 cm for
the retreat; x = 0, z = 1.0 gives 20-21/24 and plans both billet orientations into the jaws with 3 solutions each
(every stage incl. move home). The conveyor control fails there only because the conveyor stayed 1 m away.

## Demo guide (`GuideProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, run **without** `-nographics` (the card, the highlight frames and the
chevron are rendered into screenshots); no ROS needed:

```
PROBE_OUT=/tmp/probe "/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" -batchmode \
  -projectPath "$PWD" -executeMethod GuideProbe.Run -logFile /tmp/probe.log
grep '\[PROBE' /tmp/probe.log
```

It plays `FactoryDemo` and walks the self-guided flow the way a first-time user would, checking `Factory.DemoGuide`
at every step: Welcome (card lower right of the eye, the placement panel shows only a Begin button, framed, chevron pointing at it), Begin -> PlaceRobot (the unload steps after Clamp are exercised by the video probe, not here) (ring framed,
off-screen chevron shown when the head turns away and hidden when looking at the ring, the card glides after the
head), nudge to the ideal spot -> ConfirmPlacement (frame + pulsing class on the Confirm button, button-sized at
the lower part of the panel), Confirm -> OpenDoors (frame on the now-shown machine panel), doors -> SelectBillet
(top billet framed), select -> GrabBillet, `GuidedRecording.NotifyGrabbed` -> recording started -> CarryToVise
(jaw opening framed), billet set between the jaws -> ReleaseInVise, `NotifyReleased` -> the recording stops after
the settle and the plan goes out (with ROS up: Planning -> Execute with the best solution selected and the Execute
button framed; offline: refused, the guide offers the billet again and the card shows ROBOT OFFLINE after the grace
period), the stuck timers shortened by reflection on the SelectBillet step -> hint and "ask for help" lines, a billet
under the floor / outside the working area -> back on the stand (`PartRecovery`), `DemoReset.Restart` -> placement
phase at the start pose, doors closing, vise open, guide on Welcome. Screenshots in `PROBE_OUT`: `guide_welcome`,
`guide_place_ring`, `guide_offscreen_chevron`, `guide_confirm_button`, `guide_confirm_closeup`, `guide_open_doors`,
`guide_select_billet`, `guide_carry_to_vise`, `guide_vise_closeup`, `guide_card_hint`, `guide_card_offline`,
`guide_restarted`. Verified 2026-10-23: 55 checks, 0 failures (ROS up). Play-mode tests for the step order and the
auto-recording: `-runTests -testPlatform PlayMode -testFilter "DemoGuideStepsTests|GuidedRecordingTests"`.

## Recording the demo as a video (`DemoVideoProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, ROS stack up, run **without** `-nographics` (frames are rendered). Two
versions of the same walkthrough, from one switch: `VIDEO_GUIDE=1` keeps the instruction card, the target frames
and the off-screen chevron on camera (they follow the video camera as they would the headset; the card and chevron
are drawn by a URP overlay camera stacked on the video camera so scene geometry never hides them); `VIDEO_GUIDE=0`
hides them (the guide still runs underneath: Begin, auto-recording and auto-selection are the demo's behaviour).

```
VIDEO_GUIDE=1 PROBE_OUT=/tmp/video_guide "/Applications/Unity/Hub/Editor/6000.2.1f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -projectPath "$PWD" -executeMethod DemoVideoProbe.Run -logFile /tmp/video.log
# captions.txt (frame<TAB>caption) -> captions.srt at 15 fps, then
ffmpeg -framerate 15 -i frames/frame_%05d.jpg -vf "subtitles=captions.srt:force_style='Alignment=1,FontSize=20,MarginL=40,MarginV=30'" \
  -c:v libx264 -pix_fmt yuv420p -crf 20 factory_demo_with_guide.mp4
```

The walkthrough is the self-guided flow (2026-10-23): Begin on the placement panel, drag the ring to the ideal spot,
Confirm, Open Doors, select the top billet, `GuidedRecording.NotifyGrabbed` (the grab starts the recording), carry it
over the open jaws (10 deg skewed) and `NotifyReleased` (the recording stops after the settle and the plan goes out;
the dashboard selects the best solution and shows the Solutions tab), Execute (the part drops into the jaws when the
gripper opens), Clamp, the machine panel, Close Doors, the machining timer (`DemoGuide.machiningSeconds`), Open Doors, Release, select the finished part, grab (records again, zone = conveyor), let go on the belt, Execute (the part drops onto the belt), done. Panel close-ups are framed with the panel left of centre
so the card's corner never covers the button being pressed. 1920x1080 at 15 fps, JPEG 95, 4x MSAA; ~120 s, 24
captions. Recorded 2026-10-23 for both versions: load and unload each planned 3 solutions and executed, drops 15 mm
flat, yaw exact, clamped squared from 10 deg; `~/Desktop/factory_demo_with_guide.mp4` and
`~/Desktop/factory_demo_without_guide.mp4`. The earlier version of the probe (preview, solution browsing by yaw)
is in git history before this date.

Things that look odd in the footage but are not bugs: after "move home" the hand hovers just in front of and above
the vise because the ready pose reaches 0.31 m forward from a base 0.25 m before the door plane.

### Choosing recording cameras (`CameraScoutProbe.cs`)

Same copy-to-`Assets/Editor/` recipe, no ROS, run without `-nographics`. Renders 1080p stills of the user's view
before and after confirming, the wide/side views and nine candidate vise cameras with a billet seated in the
jaws and the robot in its home pose, so an angle the arm does not block can be picked without a headset.
Picked 2026-10-21: the vise close-up is `window + (0.55, 0.45, -0.05)`, from inside the opening to the right of
the vise, which keeps the arm on the left of frame; the old front-right view had the forearm across the vise.
The video probe also parks the IK jog handle (disables `Quest3RobotInteractionController`, hides its handle) and
records 1920x1080 at 15 fps, JPEG 95, 4x MSAA.

### Button presses on camera (video probe, 2026-10-21)

Every panel step in the recording is done through the panel's own button: `Press(doc, buttonName, caption)` cuts
to the panel, slides a yellow pointer dot onto the button, flashes it white and sends it a `NavigationSubmitEvent`
(the same path the headset's poke uses), so Confirm placement, Open Doors, Start/Stop Recording, the Solutions
tab, Preview, Execute, Clamp and Release all run through the UI. Element positions come from
`element.worldBound` relative to `rootVisualElement.worldBound` mapped onto the document's poke collider; note the
world-space document's layout y runs *downward from 0* (the root spans y = -height..0), so distance from the top
is `yMax - y`. `PROBE_POINTER_TEST=1` presses three buttons and exits (a 2-minute check of the mapping).
The probe also browses the plan's solutions and previews/executes the one whose place yaw is closest to the
demonstration: the MTC server samples the place yaw for a box, so the executed yaw differs per solution and per
run (26, 25, 40 degrees seen for a 10-degree demonstration). One run also aborted execution with MoveIt error
-4 (CONTROL_FAILED) after the place, on the fake controllers; rerun.

### Full cycle (video v7, 2026-10-21)

The recording now ends with the unload: Release the vise, Start Recording, carry the billet from the open jaws
onto the conveyor's drop anchor (1.5 cm up), Stop Recording, plan on the dashboard, Execute, cut to the belt as
the hand opens. Both pick-and-places go through the same `DemonstrateAndPlan` / `ExecuteSelected` routines, so the
probe is the reference for the whole cycle: load 3 solutions, clamp squared, unload 3 solutions, billet dropped
15 mm onto the belt at the anchor; 142 s. The doors are slowed to 3 s for the camera and the planning wait is
filmed on the dashboard. `getSolutionTimeoutSeconds` is 15 s in the scene (builder constant
`FactoryGetSolutionTimeout`, validator check) and 20 s in the recording.

### Timings and clean-up (video v9, 2026-10-22)

MTC plans the load in ~1 s and the unload in ~0.5 s. What looked like a long planning wait was reading the
solutions back: one `/get_solution` reply (19 steps, each with a scene diff) takes ~7 s to arrive and
deserialize, so the probe now prefetches all of them in parallel (3 in ~3 s) while the Plan tab shows the status,
then browses them on the Solutions tab from the cache (~0.6 s per row). The headset dashboard pays the same ~7 s
on every first tap of a solution; slimming the scene diffs on the server would fix that for both.
The probe hides the headset's controller models and pointer rays (45 renderers/visual components, the ray visuals
re-enable their LineRenderers each LateUpdate so the components are disabled first). The small white capsules
under each panel are the panels' grab handles, part of the VR UI, left as they are. One unload plan aborted
instantly on a run and planned normally on the next (3 solutions); the probe demonstrates again if the first
attempt is refused and dumps the per-stage statistics on a failure.

### Place yaw: server-side fix (2026-10-22)

The executed place came out 15 or 30 degrees off the demonstrated yaw (MoveIt's own post-place re-add says so; the
vise then reports "squared from 25 deg" or "clamped on its corners, 40 deg"). Cause: `PickPlaceTaskDynamic` in the
MTC fork gave the place `ComputeIK` the hand's grasp frame as IK frame, so the TCP (not the object) reached the target
and the object carried the sampled grasp yaw (15-degree steps). `demo/src/pick_place_task_dynamic.cpp` now uses
`setIKFrame(object_id)` like upstream MTC. Rebuild on the ROS machine:
`colcon build --packages-select moveit_task_constructor_demo mtc_pick_place_server` and relaunch the server.
The video probe logs "yaw X (demonstrated Y)" after each drop to confirm; the solution-browsing yaw estimate in the
probe is unreliable (it read a constant 15 deg) and only decides which row is selected.

### Instant plan refusals after a drop (2026-10-22)

"ABORTED: planning failed" within 0.0 s with grasp pose IK 0/25 came from a wrong part pose: right after the recorder
put the billet back (or the preview restored it) the publisher sent a position on a straight blend between the old and
new poses, inside the platter. `ReleasedPartGravity` had switched the billet's rigidbody to `Interpolate` for the fall and
left it there; an interpolating kinematic body reports a blended transform for one frame after every teleport. The drop
routine now restores the previous interpolation mode when the part freezes.
