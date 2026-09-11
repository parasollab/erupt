# MTC Unity VR Interface — Component & Message Reference

This document defines every ROS topic, service, action, and message field required to build a Unity VR interface for MoveIt Task Constructor (MTC). It is structured as a specification for UI generation: each UI component lists exactly which ROS interface drives it and how the data maps to visual state.

---

## 1. System Overview

```
Unity VR Client (ROS-TCP-Connector)
        │
        │  TCP socket  port 10000
        │
ROS-TCP-Endpoint  (ros_tcp_endpoint)
        │
        ├─ SUB  /collision_objects_ros        ← PlanningSceneWatcher
        ├─ SUB  /attached_collision_objects_ros ← PlanningSceneWatcher (attach/detach events)
        ├─ SUB  /description                  ← MTC Introspection node
        ├─ SUB  /statistics                   ← MTC Introspection node
        ├─ SUB  /solution                     ← MTC Introspection node
        ├─ SUB  /mtc_execution_status         ← pick_place_dynamic_demo (legacy bridge)
        ├─ PUB  /pick_place_task              → pick_place_dynamic_demo (legacy, fire-and-forget)
        ├─ PUB  /mtc_execute_solution         → pick_place_dynamic_demo (legacy bridge)
        ├─ SRV  /get_solution_<task_id>       ← MTC Introspection node
        ├─ ACT  /pick_place                   → pick_place_dynamic_demo
        └─ ACT  /execute_task_solution        ← move_group capability
```

### Actions (native, since ROS-TCP-Connector gained ROS 2 action support)

The connector and endpoint in this workspace speak the ROS 2 action protocol
(`ros2_actions_v1`, advertised in the handshake), so Unity drives action servers
directly. The bridge topics below still exist for older endpoints.

| Action | Type | Unity component |
| --- | --- | --- |
| `/pick_place` | `study_interfaces/action/PickPlace` | `PickPlaceActionClient` |
| `/execute_task_solution` | `moveit_task_constructor_msgs/action/ExecuteTaskSolution` | `MTCDataManager` |

#### `/pick_place`

Same request the CLI makes:

```bash
ros2 action send_goal /pick_place study_interfaces/action/PickPlace \
  '{object_id: "object",
    place_pose: {header: {frame_id: "world"},
                 pose: {position: {x: 0.6, y: -0.15, z: 0.0},
                        orientation: {w: 1.0}}}}' --feedback
```

```
# Goal
string object_id                    # collision object to pick (must exist in the scene)
geometry_msgs/PoseStamped place_pose
bool execute true                   # false plans without executing
---
# Result
bool success
string message
---
# Feedback
string stage                        # "planning" | "executing"
```

`pick_place_dynamic_demo` queues action goals and `/pick_place_task` messages in the
same serial queue. Cancellation is accepted but only takes effect at a stage boundary —
MTC's `plan()` and `execute()` are blocking calls.

Unity side: `PickPlaceActionClient` registers the client and exposes
`SendGoalAsync(objectId, placePose, execute)`, `CancelAsync()`, `LastStatus`, `OnStatus`
and `OnFeedback`. `PickPlaceTaskRecorder` sends through it when its `pickPlaceAction`
field is assigned, and falls back to publishing `/pick_place_task` when it is empty.

### Legacy execution bridge (endpoints without action support)

For an endpoint that does not advertise actions, `pick_place_dynamic_demo` can run
plan-only (parameter `auto_execute`, default `false`) and expose:

| Topic | Type | Direction | Purpose |
| --- | --- | --- | --- |
| `/mtc_execute_solution` | `std_msgs/UInt32` | Unity → ROS | Execute the top-level solution with this introspection id |
| `/mtc_execution_status` | `std_msgs/String` | ROS → Unity | `PLANNING` \| `PLANNING_DONE` \| `EXECUTING` \| `SUCCEEDED` \| `FAILED:<reason>` \| `REJECTED:<reason>` |

While a planned task is alive the node re-publishes `/description` and `/statistics` at
1 Hz so a late-joining Unity client (bridged volatile through ros_tcp_endpoint, which never
receives the transient_local backlog) still populates its stage tree.

### Attached objects

When MoveIt attaches an object to the gripper it moves from `world.collision_objects`
into `robot_state.attached_collision_objects`. The watcher tracks this and publishes a
`moveit_msgs/AttachedCollisionObject` on `/attached_collision_objects_ros`
(`object.operation == ADD` → attached to `link_name`, `REMOVE` → detached) instead of
forwarding a spurious world REMOVE. Unity reparents the mirrored GameObject under the
link's Transform on attach and back under the world origin on detach.

### Namespace note

The MTC introspection node is named `introspection_<hostname>_<pid>_<ptr>`. In the current demo it has no ROS namespace, so all introspection topics appear at the **global** level: `/description`, `/statistics`, `/solution`, and `/get_solution_<task_id>`. To make topics predictable across restarts, construct the Task with an explicit namespace:

```cpp
// pick_place_task_dynamic.cpp
task_.reset(new moveit::task_constructor::Task("pick_place"));
// topics become /pick_place/description, /pick_place/solution, etc.
```

Until that is done, use topic discovery (`ros2 topic list`) at connection time to find the active introspection prefix.

---

## 2. ROS Interfaces Reference

### 2.1 Publishers (Unity → ROS)

#### `/pick_place_task`

**Type:** `moveit_task_constructor_msgs/msg/PickPlaceTask`
**QoS:** Reliable, depth 10

Triggers a full pick-and-place planning + execution cycle. The demo node queues messages and processes them serially.

```
string object_id            # id of CollisionObject to pick (must exist in planning scene)
geometry_msgs/PoseStamped place_pose
  std_msgs/Header header
    string frame_id         # e.g. "world"
  geometry_msgs/Pose pose
    geometry_msgs/Point position    { x, y, z }
    geometry_msgs/Quaternion orientation  { x, y, z, w }
```

---

### 2.2 Subscribers (ROS → Unity)

#### `/collision_objects_ros`

**Type:** `moveit_msgs/msg/CollisionObject`
**QoS:** Reliable, depth 10
**Source:** PlanningSceneWatcher (filters `/monitored_planning_scene` diffs)

One message per object, published only when geometry or pose changes. Handles `ADD`, `REMOVE`, `MOVE`, and `APPEND` operations.

```
std_msgs/Header header
  string frame_id           # reference frame, usually "world"
string id                   # unique object name, e.g. "object", "table"

shape_msgs/SolidPrimitive[] primitives
  uint8 type                # BOX=1, SPHERE=2, CYLINDER=3, CONE=4
  float64[] dimensions
    # BOX:      [size_x, size_y, size_z]
    # SPHERE:   [radius]
    # CYLINDER: [height, radius]
geometry_msgs/Pose[] primitive_poses

shape_msgs/Mesh[] meshes
  geometry_msgs/Point[] vertices   { x, y, z }
  shape_msgs/MeshTriangle[] triangles
    uint32[3] vertex_indices
geometry_msgs/Pose[] mesh_poses

int8 operation
  # ADD    = 0  → spawn or replace object
  # REMOVE = 1  → delete object
  # APPEND = 2  → add geometry to existing object
  # MOVE   = 3  → update pose only
```

#### `/description`

**Type:** `moveit_task_constructor_msgs/msg/TaskDescription`
**QoS:** Reliable, transient_local, depth 2

Sent once when the task is initialised and again (empty `stages[]`) as a reset signal when the task is destroyed. Carries the static stage tree.

```
string task_id              # opaque identifier, used to match statistics/solutions
StageDescription[] stages
  uint32 id                 # unique within this task
  uint32 parent_id          # id==parent_id means root stage
  string name               # human-readable, e.g. "pick object", "approach object"
  uint32 flags              # bitmask: interface type (generator / propagator / connector)
  Property[] properties
    string name
    string description
    string type             # C++ type name
    string value            # serialised value (for display only)
```

#### `/statistics`

**Type:** `moveit_task_constructor_msgs/msg/TaskStatistics`
**QoS:** Reliable, transient_local, depth 1

Published after every planning iteration. Carries per-stage solution counts and timing.

```
string task_id
StageStatistics[] stages
  uint32 id                       # matches StageDescription.id
  uint32[] solved                 # solution IDs that succeeded (sorted by cost)
  uint32[] failed                 # solution IDs that failed
  uint32 num_failed               # total failure count (failed[] may be omitted)
  float64 total_compute_time      # seconds
```

#### `/solution`

**Type:** `moveit_task_constructor_msgs/msg/Solution`
**QoS:** Reliable, transient_local, depth 1

Published once per complete solution found. Contains the full trajectory for immediate playback AND the sub-solution tree for per-stage inspection.

```
string task_id

moveit_msgs/PlanningScene start_scene   # world state before any motion

# ── Sub-solution tree ────────────────────────────────────────────
# Each entry represents one stage's contribution to this solution.
# sub_solution_id[] lists the IDs of its child stage sub-solutions,
# allowing the tree to be reconstructed for per-stage display.
SubSolution[] sub_solution
  SolutionInfo info
    uint32  id          # unique ID for this sub-solution
    float32 cost
    string  comment     # failure hint or planner annotation
    uint32  stage_id    # which stage produced this
    string  planner_id
    visualization_msgs/Marker[] markers
  uint32[] sub_solution_id   # IDs of constituent child sub-solutions

# ── Ordered trajectory segments ──────────────────────────────────
# One entry per atomic motion segment, in execution order.
# Zero-waypoint trajectories represent state-only transitions
# (e.g. "attach object", "allow collision").
SubTrajectory[] sub_trajectory
  SolutionInfo info               # id, cost, comment, stage_id, planner_id
  TrajectoryExecutionInfo execution_info
    string[] controller_names     # ros_controllers to use for this segment
  moveit_msgs/RobotTrajectory trajectory
    trajectory_msgs/JointTrajectory joint_trajectory
      string[] joint_names        # e.g. ["panda_joint1", ..., "panda_joint7"]
      JointTrajectoryPoint[] points
        float64[] positions       # joint angles in radians, same order as joint_names
        float64[] velocities
        float64[] accelerations
        builtin_interfaces/Duration time_from_start
    moveit_msgs/MultiDOFJointTrajectory multi_dof_joint_trajectory  # usually empty
  moveit_msgs/PlanningScene scene_diff   # world changes after this segment executes
                                         # (attached/detached objects, collision changes)
```

---

### 2.3 Services

#### `/get_solution_<task_id>`

**Type:** `moveit_task_constructor_msgs/srv/GetSolution`

Fetches a full `Solution` message for a specific solution ID. Use this for **on-demand loading** when the live `/solution` topic message is no longer cached, or when you want to re-fetch a solution after a task reset.

```
# Request
uint32 solution_id          # from SolutionInfo.id or StageStatistics.solved[]

# Response
Solution solution           # identical structure to /solution topic message
```

---

### 2.4 Actions

#### `/execute_task_solution`

**Type:** `moveit_task_constructor_msgs/action/ExecuteTaskSolution`

Sends a complete `Solution` (or a manually assembled subset of `sub_trajectory[]`) to MoveIt for hardware execution.

```
# Goal
Solution solution           # the solution to execute; sub_trajectory[] drives execution

# Feedback (published after each SubTrajectory completes)
uint32 sub_id               # index of the just-completed SubTrajectory
uint32 sub_no               # total number of SubTrajectories

# Result
moveit_msgs/MoveItErrorCodes error_code
  int32 val
    # SUCCESS        =  1
    # INVALID_MOTION_PLAN = -7
    # CONTROL_FAILED = -100
    # PREEMPTED      = -5 (on cancel)
```

---

## 3. UI Components

Each component maps to one or more ROS interfaces. Fields marked **[lazy]** should be fetched via `GetSolution` when first needed rather than stored upfront.

---

### 3.1 Scene Viewer

**Purpose:** Render the live planning scene in 3D / VR.

| Data                 | Source                                                                                                  |
| -------------------- | ------------------------------------------------------------------------------------------------------- |
| Object geometry      | `/collision_objects_ros` → `primitives[]` + `primitive_poses[]`, `meshes[]` + `mesh_poses[]` |
| Object pose          | `/collision_objects_ros` → `header.frame_id`, `primitive_poses[0]`                               |
| Object lifecycle     | `operation` field: `ADD`=spawn, `REMOVE`=destroy, `MOVE`=update pose                            |
| Place target preview | Local (user-placed in VR, becomes`place_pose` in `PickPlaceTask`)                                   |

**Logic:**

- Maintain a `Dictionary<string, GameObject>` keyed by `CollisionObject.id`.
- On `ADD`/`APPEND`: instantiate or update the GameObject using `primitives[0].type` to select mesh (cube/sphere/cylinder).
- On `REMOVE`: destroy the GameObject and remove from dictionary.
- On `MOVE`: update `Transform.position` and `Transform.rotation` from `primitive_poses[0]`.
- Coordinate conversion: ROS uses right-hand Z-up; Unity uses left-hand Y-up. Apply the standard ROS↔Unity transform.

---

### 3.2 Task Command Panel

**Purpose:** Let the user trigger a new pick-and-place task.

| Control                                | Action                           |
| -------------------------------------- | -------------------------------- |
| Object selector (dropdown or ray-cast) | Sets`PickPlaceTask.object_id`  |
| Place pose manipulator (VR gizmo)      | Sets`PickPlaceTask.place_pose` |
| "Plan & Execute" button                | Publishes to`/pick_place_task` |

**Data flow:**

```
User selects object → object_id = CollisionObject.id of selected object
User places target  → place_pose.header.frame_id = "world"
                      place_pose.pose = gizmo Transform (converted to ROS frame)
Button press        → Publish PickPlaceTask { object_id, place_pose }
```

---

### 3.3 Stage Tree Panel

**Purpose:** Display the MTC stage hierarchy with live planning counts.

**Data sources:**

- Structure: `/description` → `TaskDescription.stages[]`
- Live counts: `/statistics` → `TaskStatistics.stages[]` matched by `StageDescription.id == StageStatistics.id`

**Tree construction:**

```
stages[] is a flat list; build tree using parent_id:
  - stage where id == parent_id is the root
  - all other stages: parent = stage with id == this.parent_id
```

**Per-stage display fields:**

| Field           | Source                                                                       |
| --------------- | ---------------------------------------------------------------------------- |
| Name            | `StageDescription.name`                                                    |
| Solutions found | `StageStatistics.solved.Length`                                            |
| Failures        | `StageStatistics.num_failed`                                               |
| Best cost       | `StageStatistics.solved[0]` → ID → fetch cost via `GetSolution` [lazy] |
| Compute time    | `StageStatistics.total_compute_time` seconds                               |

**Update trigger:** Re-render on every `/statistics` message. No need to re-fetch `/description` unless `task_id` changes (indicates task reset).

---

### 3.4 Solution Browser

**Purpose:** List all found solutions ranked by cost and let the user preview or execute one.

**Data sources:**

- Solutions arrive on `/solution` — buffer all received messages keyed by `Solution.sub_trajectory[*].info.id`.
- For solutions not in the buffer (e.g. after reconnect): call `/get_solution_<task_id>` with the ID from `StageStatistics.solved[]`.

**Per-solution display fields:**

| Field               | Source                                                                                   |
| ------------------- | ---------------------------------------------------------------------------------------- |
| Solution rank       | Index in cost-sorted list                                                                |
| Total cost          | Sum of`SubTrajectory[*].info.cost`                                                     |
| Stage breakdown     | Group`sub_trajectory[]` by `info.stage_id`, correlate with `StageDescription.name` |
| Planner used        | `SubTrajectory[i].info.planner_id`                                                     |
| Comments / warnings | `SubTrajectory[i].info.comment` (non-empty = planner note)                             |

**Lazy fetching pattern:**

```
On "Browse Solutions" open:
  known_ids = StageStatistics.stages[root].solved[]  // all top-level solution IDs
  for each id in known_ids:
    if id not in solutionCache:
      Call GetSolution(solution_id = id)
      solutionCache[id] = response.solution
```

---

### 3.5 Trajectory Preview Player

**Purpose:** Animate the robot through a selected solution or individual stage trajectory without executing on hardware.

**Data source:** `Solution.sub_trajectory[]` from the Solution Browser buffer.

**Playback model:**

```
segments = solution.sub_trajectory[]               // ordered list
for each segment in segments:
  if segment.trajectory.joint_trajectory.points is empty:
    apply scene_diff (attach/detach objects) and continue
  else:
    for each point in segment.joint_trajectory.points:
      set joint angles: zip(joint_names, point.positions)
      advance time by point.time_from_start delta
```

**Per-segment controls:**

| Control                    | Data                                                                        |
| -------------------------- | --------------------------------------------------------------------------- |
| Stage label                | `StageDescription.name` where `id == SubTrajectory.info.stage_id`       |
| Segment cost badge         | `SubTrajectory.info.cost`                                                 |
| Skip to segment            | Seek to`segment.trajectory.joint_trajectory.points[0]`                    |
| Scene state at segment end | `SubTrajectory.scene_diff` (use for attached object updates during scrub) |

**Sub-solution tree view (optional):**
`Solution.sub_solution[]` carries the hierarchical decomposition. `SubSolution.sub_solution_id[]` gives the IDs of child sub-solutions, enabling a collapsible tree display aligned with the Stage Tree Panel.

---

### 3.6 Sub-Solution Selector

**Purpose:** Let the user browse per-stage alternatives and understand which combination any given solution uses.

**Key constraint:** Sub-solutions are only combinable if they were already combined in at least one top-level solution — stage N's end state must match stage N+1's start state. All valid combinations are enumerated by the top-level solutions.

**Workflow:**

```
1. Show list of top-level solutions (from solution cache)
2. For selected solution, highlight which sub_solution IDs belong to each stage
   - Group sub_trajectory[] by info.stage_id
   - Show each group as "Stage X used trajectory variant #N (cost=Y)"
3. Let user filter solutions by preferred stage variant:
   - "Show only solutions that used grasp pose approach #3"
   - Filter: solutions where sub_trajectory[stage_id==grasp_stage].info.id == selected_id
```

**Data needed per stage variant:**

| Field                | Source                                                    |
| -------------------- | --------------------------------------------------------- |
| Variant ID           | `SubTrajectory.info.id`                                 |
| Cost                 | `SubTrajectory.info.cost`                               |
| Stage name           | `StageDescription.name` correlated by `stage_id`      |
| Trajectory           | `SubTrajectory.trajectory.joint_trajectory`             |
| Appears in solutions | Cross-reference against all buffered`Solution` messages |

---

### 3.7 Execution Panel

**Purpose:** Send a selected solution to the robot for hardware execution with live progress feedback.

**Data flow:**

```
User selects solution → solution = solutionCache[selected_id]

Send action goal:
  ExecuteTaskSolution.Goal { solution = solution }

On Feedback:
  progress = feedback.sub_id / feedback.sub_no
  active_stage = StageDescription where id == solution.sub_trajectory[sub_id].info.stage_id
  highlight active_stage in Stage Tree Panel

On Result:
  if error_code.val == 1   → show "Execution succeeded"
  if error_code.val == -5  → show "Cancelled"
  else                      → show "Failed: " + error_code.val
```

**Cancellation:** Send a cancel request to the action server. The capability calls `plan_execution_->stop()`, returns `PREEMPTED`.

---

## 4. Message Type Quick Reference

| Message              | Package                          | Key Fields                                                                                                          |
| -------------------- | -------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| `PickPlaceTask`    | `moveit_task_constructor_msgs` | `object_id`, `place_pose`                                                                                       |
| `TaskDescription`  | `moveit_task_constructor_msgs` | `task_id`, `stages[]{id, parent_id, name, flags, properties}`                                                   |
| `TaskStatistics`   | `moveit_task_constructor_msgs` | `task_id`, `stages[]{id, solved[], failed[], num_failed, total_compute_time}`                                   |
| `Solution`         | `moveit_task_constructor_msgs` | `task_id`, `start_scene`, `sub_solution[]`, `sub_trajectory[]`                                              |
| `SubSolution`      | `moveit_task_constructor_msgs` | `info{id,cost,stage_id}`, `sub_solution_id[]`                                                                   |
| `SubTrajectory`    | `moveit_task_constructor_msgs` | `info{id,cost,stage_id,planner_id,comment}`, `execution_info`, `trajectory`, `scene_diff`                   |
| `SolutionInfo`     | `moveit_task_constructor_msgs` | `id`, `cost`, `comment`, `stage_id`, `planner_id`, `markers[]`                                          |
| `CollisionObject`  | `moveit_msgs`                  | `id`, `header.frame_id`, `primitives[]`, `primitive_poses[]`, `meshes[]`, `mesh_poses[]`, `operation` |
| `RobotTrajectory`  | `moveit_msgs`                  | `joint_trajectory{joint_names[], points[]{positions[], time_from_start}}`                                         |
| `MoveItErrorCodes` | `moveit_msgs`                  | `val` (SUCCESS=1, INVALID=-7, CONTROL_FAILED=-100, PREEMPTED=-5)                                                  |

---

## 5. C# Message Classes to Generate

Run `ros-tcp-connector`'s message generation tool against these packages:

```
moveit_task_constructor_msgs   (msgs/, srv/, action/)
moveit_msgs                    (msg/CollisionObject, RobotTrajectory, PlanningScene, MoveItErrorCodes)
geometry_msgs                  (msg/Pose, PoseStamped, Point, Quaternion, Vector3Stamped)
shape_msgs                     (msg/SolidPrimitive, Mesh, MeshTriangle)
trajectory_msgs                (msg/JointTrajectory, JointTrajectoryPoint)
std_msgs                       (msg/Header)
builtin_interfaces             (msg/Duration)
visualization_msgs             (msg/Marker)
sensor_msgs                    (msg/JointState)  # inside PlanningScene.robot_state
```

---

## 6. ROS Node Startup Order

```
1. ros2 launch moveit_task_constructor_demo run.launch.py exe:=pick_place_dynamic_demo
   # starts: move_group (with execute_task_solution capability), robot state publisher,
   #         pick_place_dynamic_demo (subscribes /pick_place_task, creates MTC introspection)

2. ros2 launch planning_scene_utils unity_monitor.launch.py
   # starts: planning_scene_watcher (→ /collision_objects_ros),
   #         latency_measurer, fps_monitor, latency_logger

3. ros2 launch ros_tcp_endpoint endpoint.py
   # opens TCP bridge on 0.0.0.0:10000

4. Unity VR Client connects to ROS_IP:10000
```

---

## 7. Connection & Discovery Checklist

- [ ] ROS-TCP-Endpoint running on port 10000, `ROS_IP` reachable from Unity headset
- [ ] Unity uses **ROS-TCP-Connector** UPM package with matching ROS 2 mode enabled
- [ ] All C# message classes generated from packages listed in Section 5
- [ ] Unity subscribes to `/description` with `QosProfile.TransientLocal` so it receives the last-published message on connect
- [ ] Unity subscribes to `/statistics` with `QosProfile.TransientLocal` likewise
- [ ] `task_id` extracted from first non-empty `/description` message; used to build service name `/get_solution_<task_id>`
- [ ] ROS frame → Unity frame conversion applied: flip Y/Z axes and handedness on all `Pose` fields
