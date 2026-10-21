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
