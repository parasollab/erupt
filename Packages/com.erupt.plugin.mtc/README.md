# ERUPT MTC Plugin

MoveIt Task Constructor solutions, execution and pick/place study tooling for ERUPT.
Depends on `com.erupt.plugin.moveit` (planning scene) and `com.erupt.core`.

- `MTCDataManager` — MTC introspection topics and `/execute_task_solution` action client
- `MTCTrajectoryPlayer` — previews a solution on the robot, resolving `scene_diff` attachments through the core `EnvironmentRegistry`
- `MTCDashboardPanel` — dashboard (UI Toolkit; ported to a tier 3 tab in Phase 3)
- `PickPlaceActionClient`, `PickPlaceTaskRecorder` — study pick/place action + recorder

See `MESSAGES.md` for the generated message bindings.
