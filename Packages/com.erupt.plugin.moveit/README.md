# ERUPT MoveIt Plugin

Kinematic planning and planning-scene sync for ERUPT via MoveIt 2 — a `Planning` plugin.
Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`. Generated
`moveit_msgs` bindings: `MESSAGES.md`.

## Setup

1. **Scene.** Drop `Prefabs/MoveItPlugin.prefab` next to the `ERUPT` root. `PluginHost` finds
   it on Start; the robot, ROS bus and UI come from the context.
2. **Topics.** `MoveItPlanningSettings` on `MoveItPlugin` holds the topic and service names
   (`/joint_states`, `/plan_kinematic_path`, `/query_planner_interface`, the trajectory
   topic) and the planner defaults (pipeline, planner id, attempts, allowed time).
3. **Planning scene.** Put `MoveItPlanningSceneSync`, `CollisionObjectsListenerSimple` and
   `AttachedCollisionObjectListener` on one scene object (`CollisionObjectListener` in
   `KitchenFR3`; they are not on the prefab). The first attaches a `CollisionObjectPublisher`
   to every object the core `EnvironmentRegistry` adds (ADD / MOVE / REMOVE to
   `/collision_objects`); the second mirrors `/collision_objects_ros` back into the registry;
   the third reparents objects MoveIt reports as attached. ROS-commanded removals are not
   echoed back.
4. **Use.** Plan mode: select the end effector → `Set Goal` (tier 2) → `Plan`; the result is a
   ghost trajectory with `Preview` / `Execute`. The tier 3 **Planner settings** tab holds
   pipeline, planner, attempts, allowed time, joint-state mirroring, plan and reset.
5. **Tests.** Test Runner → `Erupt.Plugins.MoveIt.Tests` (PlayMode): planning client,
   collision-object flow and the tab, all against `FakeRosBus` / `FakeRobotModel`.

## Layout

```
Runtime/MoveItPlugin.cs                 the plugin (Erupt.Plugins.MoveIt): PlanningPlugin
Runtime/MoveItPlanningClient.cs         transport (plain C#): joint-state mirror, planner discovery,
                                        motion-plan service, execution — on IRosBus + IRobotModel
Runtime/PlannerSettingsTab.cs           tier 3 tab (7 interactive elements)
Runtime/MoveItPlanningSceneSync.cs      EnvironmentRegistry → planning scene
Runtime/CollisionObjectPublisher.cs     per-object ADD / MOVE / REMOVE publisher
Runtime/CollisionObjectsListenerSimple.cs   /collision_objects_ros → EnvironmentRegistry
Runtime/AttachedCollisionObjectListener.cs  attach / detach mirroring
Runtime/Messages/                       moveit_msgs (Erupt.Plugins.MoveIt.Messages)
Tests/                                  MoveItPlanningClientTests, CollisionObjectFlowTests, ObstacleCommandTests, PlannerSettingsTabTests
Prefabs/MoveItPlugin.prefab
```
