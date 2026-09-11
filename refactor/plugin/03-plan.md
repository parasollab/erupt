# Plugin refactor — Phase 3 plan (UI port and legacy retirement)

Copied verbatim from `refactor_plan.md` § Phase 3 at the start of the phase.

### Phase 3 — UI port and legacy retirement

- MoveIt: `PlannerSettingsTab` (uGUI via `UiBuilder`: pipeline/planner dropdown, attempts, allowed time, mirror toggle, plan button) built through `IUiHost.AddTab`; verbs `EndEffector/set-goal`, `EndEffector/plan`, `Trajectory/preview`, `Trajectory/execute` bound by `PlanningPlugin`. Ghost preview via `SpawnGhosts` becomes the `TrajectorySelectable` host so the Trajectory context menu appears on the ghost.
- MTC: `SolutionsTab` (stage tree, ranked solutions, cost breakdown, record start/stop) replaces `MTCDashboardPanel`; `Trajectory/preview|execute` on the selected solution's ghost; `Teach`-mode entry for `PickPlaceTaskRecorder`.
- Obstacle creation: Build-mode placement tool per `refactor/02-plan.md` P3 (lift `WristMenuController.AddPrimitiveShape` → `ObstacleFactory` already exists; add a tier 3 "Scene" tab with Cube/Sphere/Cylinder placement as the interim UI, flagged as a guideline deviation until the in-world tool lands).
- Flip `TierUiRig.useTierUi=true`, remove the `legacyMenu` toggle. Delete: `WristMenuController.cs`, `MoveItPlanningRequestMenuUI.cs`, `MTCDashboardPanel.cs`, `Assets/UI Toolkit/` (all `.uxml`, `.uss`, `WristUISettings.asset`, theme), `Assets/Prefabs/MoveItPlanningRequestMenu.prefab`, `XR UI Toolkit Manager` scene object, the migration tools under `core/Editor/Migration`, `Quest3RobotInteractionController` legacy selection path.
- `XR Origin (XR Rig).prefab` (now used by the two kept scenes only): remove `WristUI Document` child and `WristMenuController`; keep XRI interactors. `KitchenFR3.unity`: remove `MoveItPlanningRequestMenu` and `MTCMenu` instances; `TierUiRig` anchors → tier 1 on the left controller transform, tier 3 on a scene-local `Tier3Anchor`.
- Tests: `PlannerSettingsTabTests`, `SolutionsTabTests` (PlayMode, build the canvas, assert element counts ≤ 7 with a selection + panel open per Part 8), `ContextualMenu` end-to-end with a `TrajectorySelectable`.
- Gate: compile; tests; boundary test; **Quest UX review** of the five core flows plus MTC (behaviour intentionally changes; ROS traffic per flow should still match the Phase 0 capture, use `refactor/ros-traffic-diff.sh`); Part 8 design + code checklists walked in the phase report.

