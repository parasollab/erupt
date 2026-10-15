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
