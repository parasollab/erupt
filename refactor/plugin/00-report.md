# Plugin refactor — Phase 0 report (hygiene, tag, prune)

Implements `00-plan.md`. Branch `design_refactor`; tag `pre-plugin-refactor` = `46e3bef`.

**Scope honored:** `Assets/Scenes/KitchenFR3.unity` is the only scene modified. No prefab
asset was modified: every removal was a scene-local object or an added-component override.

---

## Prune (staged, uncommitted)

| Removed | Notes |
|---|---|
| All scenes except `KitchenFR3` + `KitchenLoadingSceneFR3` (`Study Scenes/**`, `AR`, `ARLoading`, `Kitchen`, `KitchenLoadingScene`, `LoadingScene`, `MainScene`, `MoveIt`, `Task1_Kitchen1-4`) | disabled entries dropped from `EditorBuildSettings.asset` |
| `GrabMoveDiagnostics`, `TransformSearchExtensions`, `FarOnlySelectFilter`, `Quest3ControllerRayInteractor`, `MarkerRobotPlacement`, `ARPlaneColorizer`, `SceneController`, `RobotBaseTFPublisher` | `InteractionMigration.MigrateRays` and the three `Quest3ControllerRayInteractor` overloads on `Quest3RobotInteractionController` removed with them |
| `Prefabs/{ARPlaneColored, ur5e_robot, Robot IK Manager, SelectionManager}.prefab`, `UI/WristUIButton.prefab`, `AddPlugin.uss`, `Style.uss`, `UI Toolkit/{XRDashboard.uxml, PanelSettings.asset}` | none instanced in the two kept scenes |
| `Editor/OpenCVForUnityDefine.cs` + `Editor.asmdef` | OpenCV only served the AR scene |
| `Samples/ROS TCP Connector/0.7.1/ROS 2 Action Client Demo/` | the `ROS2 Action Demo` scene root that used it is removed below |
| `RosMessages/Trajectory/`, `RosMessages/Sensor/` | six duplicates of generated messages |
| `RADER` + `RADER.ArUco` GUIDs from `Assets/xrviz.asmdef` | nothing in `Assets/` referenced them once `MarkerRobotPlacement` went |
| `Temp_VisionOS/`, `Assets/_Recovery/` from disk | `.gitignore` now ignores `/Temp_VisionOS/` and `*.slnx` |

Compile is clean after the prune (Editor build 2026-09-10 18:49, no `error CS`).

## KitchenFR3 clean-up (`ERUPT/Refactor/Clean KitchenFR3`)

First run crashed with `MissingReferenceException` in `RobotRoots`: `RemoveActionDemo`
destroyed a root while `Clean()` kept iterating the original `roots` array. Fixed by
re-reading `scene.GetRootGameObjects()` after each destructive step and filtering
destroyed roots in the helpers (`Live()`). The crashed run had not flagged the scene
dirty, so the fixed run started from the on-disk scene. Its log:

```
robots: roots with ArticulationBody = 'fr3' active=True prefabInstance=False asset=(scene),
        'fr3 (1)' active=False prefabInstance=True asset=Assets/Prefabs/fr3 (1).prefab
robots: DirectArticulationIKController.robotRoot -> fr3
robots: SpawnGhosts.realRobot -> fr3
robots: MTCTrajectoryPlayer host -> fr3
robots: SpawnGhosts.robotPrefab -> fr3 (1) (scene object=True)
ground: missing-script components 1, removed 1
ground: removed disabled CollisionObjectsListenerSimple (topic='/collision_objects_ros')
listener: remaining 1 -> 'CollisionObjectListener' enabled=True
reset: 'XR Origin (XR Rig)' enabled=True action=True addedOverride=True
reset: 'XR Origin (XR Rig)/Camera Offset/Right Controller' enabled=False action=False addedOverride=True
reset: removed disabled duplicate (added-component override, stored in the scene)
demo: deleted root 'ROS2 Action Demo' (sample script deleted in Phase 0)
ghosts: robotPrefab 'fr3 (1)' (scene instance) -> asset 'Assets/Prefabs/fr3 (1).prefab'
robot: deleting 'fr3 (1)' ('fr3 (1)' active=False prefabInstance=True asset=Assets/Prefabs/fr3 (1).prefab)
robot: kept 'fr3'
```

**Answer to the plan's open question:** the live robot is the scene-local `fr3` root. All
three consumers (`DirectArticulationIKController.robotRoot`, `SpawnGhosts.realRobot`,
`MTCTrajectoryPlayer`) pointed at it. The inactive `fr3 (1)` prefab instance existed only
as the ghost template; `SpawnGhosts.robotPrefab` now references the
`Assets/Prefabs/fr3 (1).prefab` asset directly and the instance is deleted.

A second run reported nothing left to remove (idempotent). Scene diff: 4 insertions,
175 deletions; the removed blocks are the `ROS2 Action Demo` root, the `fr3 (1)` prefab
instance, the `Ground` missing-script and listener components, and the duplicate
`ResetKitchenScene` override.

## Gate

| Check | Status |
|---|---|
| Compile clean | PASS |
| `ERUPT/Refactor/Verify KitchenFR3` (Guidelines Phase 1 interaction wiring, regression check) | PASS — 8/8 after the prune and clean-up |
| `ERUPT/Refactor/Verify KitchenFR3 Clean-up` | PASS — 11/11 (Ground clean, one listener, one reset, no action demo, one robot root, consumers agree, ghost prefab is an asset, no missing scripts anywhere, rig still a prefab instance) |
| 53 + 12 PlayMode tests | PASS — all green in the Test Runner after the prune and clean-up |
| Quest smoke (create obstacle → set goal → plan → preview → execute; MTC record/preview/execute) | PENDING — requires headset |
| Submodule pointers (`URDF-Importer` 33 dirty files, `ROS-TCP-Connector` 4; `RADER` + `ROS-TCP-Connector` pointers differ from recorded) | PENDING — maintainer action on the parasollab forks |

## Next

Commit Phase 0 once the gate rows above are filled, then start `01-plan.md` (package
extraction + environment cut). `KitchenSceneCleanup.cs` stays until Phase 3 with the
other migration tools.
