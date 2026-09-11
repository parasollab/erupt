# ERUPT MoveIt Plugin

Kinematic planning and planning-scene sync for ERUPT via MoveIt 2.

- `MoveItPlanningSceneSync` — mirrors the core `EnvironmentRegistry` into MoveIt's planning scene
  (attaches a `CollisionObjectPublisher` to Unity-owned objects; suppresses REMOVE echoes for removals
  commanded by ROS). Sits next to `CollisionObjectsListenerSimple`, which handles ROS → Unity.
- `AttachedCollisionObjectListener` — reparents objects MoveIt reports as attached.
- `MoveItPlanningRequestMenuUI` — planning request panel (UI Toolkit; ported to tier UI in Phase 3).

See `MESSAGES.md` for the generated `moveit_msgs` bindings.
