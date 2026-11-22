# ERUPT MoveIt Plugin

Kinematic planning and planning-scene sync for ERUPT via MoveIt 2 — a `Planning` plugin.
Contract and lifecycle: `com.erupt.core/Documentation~/plugin-authoring.md`. Generated
`moveit_msgs` bindings: `MESSAGES.md`.

## Setup

1. **Scene.** Drop `Prefabs/MoveItPlugin.prefab` next to the `ERUPT` root. `PluginHost` finds
   it on Start; the robots, ROS bus and UI come from the context. One plugin serves every
   robot in the scene (`IEruptContext.Robots`): each robot gets a `MoveItRobotSession`
   (planning client, start / goal, preview, planning scene). The verbs act on the robot the
   selected end effector or plan belongs to; the tab shows the active robot.
2. **Names.** Everything a robot needs is sent on that robot's namespaced bus with relative
   names, so a robot in namespace `left` talks to `/left/plan_kinematic_path` and one without
   a namespace to `/plan_kinematic_path`. What differs per robot comes from its
   `RobotProfile`: the planning group (`armGroup`), `jointStatesTopic`,
   `trajectoryCommandTopic` (`<controller>/joint_trajectory`), the planning frame id and the
   ROS ↔ Unity joint-name rule. `MoveItPlanningSettings` on `MoveItPlugin` keeps what is
   MoveIt's own: the service names (`plan_kinematic_path`, `query_planner_interface`) and the
   planner defaults (pipeline, planner id, attempts, allowed time).
3. **Planning scene.** Put `MoveItPlanningSceneSync`, `CollisionObjectsListenerSimple` and
   `AttachedCollisionObjectListener` on one scene object (`CollisionObjectListener` in
   `KitchenFR3`; they are not on the prefab). The first attaches a `CollisionObjectPublisher`
   to every object the core `EnvironmentRegistry` adds; the publisher only detects changes.
   The plugin's `PlanningSceneFanout` sends each change (ADD / MOVE / REMOVE) to **every**
   robot's `collision_object`, in that robot's planning frame (`RobotInstance.PlanningFrame`),
   and listens to every robot's `collision_objects_ros`. Each robot's MoveIt has its own
   planning scene and knows nothing of the others; Unity keeps one object per id and a ledger
   of what each namespace was told, so echoes are dropped, an object that appears in one
   planning scene is adopted and sent to the others, and a removal in one is forwarded to the
   others (never back). `CollisionObjectsListenerSimple` builds the Unity objects; the scene's
   `AttachedCollisionObjectListener` serves the first robot and is copied for each further
   one, so every robot's `attached_collision_objects_ros` is resolved on that robot's own
   links. An object a robot carries is silent in that robot's namespace and keeps being
   updated in the others.
   With one robot in the root namespace this is the single `/collision_object` /
   `/collision_objects_ros` pair it always was.
4. **Use.** Select the end effector (its handle) → `Set Start` (tier 2) → drag the end
   effector to where the motion should end → `Set Goal` → `Plan`. The plan appears as a
   handle halfway between start and goal, becomes the selection (`selectNewPlan`) and previews, in a loop, on a blue
   copy of the robot while the robot stays where it is; its menu has `Execute` and
   `Stop Preview` (`Preview` once the preview has been stopped). Executing publishes the trajectory and the Unity robot
   follows the ROS robot until it arrives. Whatever cannot be done says why at the place it
   was asked: no start or goal, MoveIt not connected, MoveIt's own error in words, no command
   topic, a robot that did not arrive. The tier 3 **MoveIt** tab holds pipeline, planner,
   attempts, allowed time, joint-state mirroring, plan and reset.
5. **Where the robot starts.** When a robot's first joint state arrives after the connection
   has come up (at the start, after a scene reset, after a reconnect), the Unity robot takes
   it, once: the user starts from where the ROS robot is, not from the profile's initial
   pose. It is clamped to the joint limits like every write (a mismatch is reported), it does
   not happen when the user has moved the robot or a preview has played since the connection
   came up, and a start that was already set is kept as it was set. `adoptRobotStateOnConnect`
   on the plugin turns it off; "Mirror joints" on the tab is the continuous version.
6. **Tests.** Test Runner → `Erupt.Plugins.MoveIt.Tests` (PlayMode): planning client,
   collision-object flow, the fan-out with three planning scenes, three robots under one
   plugin, and the tab, all against `FakeRosBus` / `FakeRobotModel` / `FakeRobots`.

## Layout

```
Runtime/MoveItPlugin.cs                 the plugin (Erupt.Plugins.MoveIt): PlanningPlugin, one session per robot
Runtime/MoveItRobotSession.cs           what the plugin keeps for one robot
Runtime/MoveItPlanningClient.cs         transport (plain C#): joint-state mirror, planner discovery,
                                        motion-plan service, execution — on a robot's IRosBus + IRobotModel
Runtime/PlannerSettingsTab.cs           tier 3 tab (7 interactive elements), for the active robot
Runtime/PlanningSceneFanout.cs          one environment ↔ every robot's planning scene (ledger, owners, frames)
Runtime/MoveItPlanningSceneSync.cs      EnvironmentRegistry → a CollisionObjectPublisher per object
Runtime/CollisionObjectPublisher.cs     detects that an object appeared / moved / was resized / was destroyed
Runtime/CollisionObjectsListenerSimple.cs   the Unity side of the mirror: one object per id
Runtime/AttachedCollisionObjectListener.cs  attach / detach mirroring, one per robot
Runtime/Messages/                       moveit_msgs (Erupt.Plugins.MoveIt.Messages)
Tests/                                  MoveItPlanningClientTests, CollisionObjectFlowTests, PlanningSceneFanoutTests, MultiRobotMoveItTests, MoveItFirstRunTests, ObstacleCommandTests, PlannerSettingsTabTests
Prefabs/MoveItPlugin.prefab
```
