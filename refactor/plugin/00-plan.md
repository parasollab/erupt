# Plugin refactor — Phase 0 plan (hygiene, tag, prune)

Copied verbatim from `refactor_plan.md` § Phase 0 at the start of the phase. The new series lives under `refactor/plugin/` because `refactor/01-*` and `02-*` are already taken by the Guidelines phases.

### Phase 0 — Hygiene, tag, prune

- `git tag pre-plugin-refactor` on the current commit (after the maintainer commits the in-flight `mtc` branch work; the Phase 2 doc notes uncommitted maintainer edits in `CollisionObjectPublisher`, `KitchenFR3.unity` etc.).
- **User action (cannot be done from this repo):** commit and push the dirty `Packages/URDF-Importer` (20 files) and `Packages/ROS-TCP-Connector` (3 files + `Samples~`) changes on the parasollab forks, then `git add` the updated submodule pointers. Without this a fresh clone is not this project.
- `.gitignore`: add `/Temp_VisionOS/`. Delete `Temp_VisionOS/` and `Assets/_Recovery/` from disk (both untracked/ignored).
- Delete scenes: everything in `Assets/Scenes` except `KitchenFR3.unity` and `KitchenLoadingSceneFR3.unity` (Build Settings entry pair); remove the disabled entries from `ProjectSettings/EditorBuildSettings.asset`.
- Delete scene-only / dead scripts and assets: `GrabMoveDiagnostics.cs`, `TransformSearchExtensions.cs`, `FarOnlySelectFilter.cs`, `Quest3ControllerRayInteractor.cs` (absent from KitchenFR3; retires backlog B7/B14), `MarkerRobotPlacement.cs`, `ARPlaneColorizer.cs`, `SceneController.cs`, `RobotBaseTFPublisher.cs`, `Assets/Prefabs/{ARPlaneColored, ur5e_robot, Robot IK Manager, SelectionManager}.prefab` (verify none is instanced in the two kept scenes; the scene's `Robot IK Manager` and `SelectionManager` are scene-local), `Assets/UI/WristUIButton.prefab`, `Assets/AddPlugin.uss`, `Assets/Style.uss`, `Assets/UI Toolkit/{XRDashboard.uxml, PanelSettings.asset}`, `Assets/Editor/OpenCVForUnityDefine.cs` + `Editor.asmdef` (OpenCV was only used by the AR scene), `Assets/Samples/ROS TCP Connector/0.7.1/ROS 2 Action Client Demo/`, `Assets/RosMessages/Trajectory/` and `Assets/RosMessages/Sensor/` (six duplicate messages).
- Remove the `RADER` and `RADER.ArUco` GUID references from `Assets/xrviz.asmdef` (nothing in `Assets/` uses them once `MarkerRobotPlacement` is gone); `CollisionHaptics` stays available because the RADER package is still installed until Phase 5.
- `KitchenFR3.unity` fixes (via a one-off `ERUPT/Refactor/Clean KitchenFR3` editor command following `InteractionMigration.cs` idioms): remove the missing-script component on `Ground` and its duplicate `CollisionObjectsListenerSimple`, remove the duplicate `ResetKitchenScene` on the rig instance, delete the `ROS2 Action Demo` object. Inspect which fr3 the `Robot IK Manager` and `SpawnGhosts.realRobot` reference; delete the other robot (record the answer in the phase report).
- Gate: compile clean; existing 53+12 PlayMode tests pass; Quest smoke (create obstacle, set goal, plan, preview, execute; MTC record/preview/execute) unchanged.

