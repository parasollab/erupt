**Ready for review**Select text to add comments on the plan

# MTC Connector UI — Implementation Plan

## Context

`mtc_connector.md` defines 7 UI components that expose MoveIt Task Constructor (MTC) capabilities to the Unity VR client. The project already partially implements sections 3.1 (scene viewer) and 3.2 (task command panel). This plan adds the missing pieces: live stage tree, solution browser, trajectory preview, sub-solution selector, and hardware execution — all wired into the existing wrist-menu + UIToolkit panel infrastructure.

---

## What Already Exists (no changes needed)

| MTC Spec                   | Existing code                                                                                                | Status            |
| -------------------------- | ------------------------------------------------------------------------------------------------------------ | ----------------- |
| 3.1 Scene Viewer           | `CollisionObjectsListenerSimple.cs` subscribes `/collision_objects_ros`, spawns 3D primitives            | **Done**    |
| 3.2 Task Command           | `PickPlaceTaskRecorder.cs` — object selection, grab→release pose capture, publishes `/pick_place_task` | **Done**    |
| Robot joint drive          | `DirectArticulationIKController.ApplyJointState(string[], double[])`                                       | Reuse for preview |
| Trajectory playback engine | `TrajectoryReplay.cs` — coroutine-based joint interpolation                                               | Reuse pattern     |
| World-space panel pattern  | `MoveItPlanningRequestMenuUI.cs` + `MoveItPlanningRequest.uxml`                                          | Copy pattern      |
| Wrist menu                 | `WristMenuController.cs` + `WristMenu.uxml`                                                              | Add one button    |

All `moveit_task_constructor_msgs` message types are already generated under `Assets/RosMessages/MoveitTaskConstructorMsgs/`:

* `TaskDescriptionMsg`, `TaskStatisticsMsg`, `SolutionMsg`
* `SubSolutionMsg`, `SubTrajectoryMsg`, `SolutionInfoMsg`, `StageDescriptionMsg`, `StageStatisticsMsg`
* `GetSolutionRequest`, `GetSolutionResponse`

**Not yet generated:** action types for `/execute_task_solution`. Must be added manually (see below).

---

## Architecture

```
WristMenuController  ──[MTC button]──▶  MTCDashboardPanel (world-space, grabbable)
                                              │
                                    ┌─────────┴──────────┐
                                    │   4-tab UIToolkit   │
                                    │  ① Plan             │  ← PickPlaceTaskRecorder (existing)
                                    │  ② Stages           │  ← StageTreePanel view
                                    │  ③ Solutions        │  ← SolutionBrowserPanel view
                                    │  ④ Execute          │  ← ExecutionPanel view
                                    └─────────┬──────────┘
                                              │
                                       MTCDataManager  (singleton)
                                       ├─ sub: /description   → OnDescriptionReceived
                                       ├─ sub: /statistics    → OnStatisticsUpdated
                                       ├─ sub: /solution      → cache + OnSolutionReceived
                                       └─ svc: /get_solution_{task_id}
                                              │
                                       MTCTrajectoryPlayer
                                       └─ drives DirectArticulationIKController.ApplyJointState()
```

---

## Files to Create

### 1. `Assets/Scripts/MTCDataManager.cs`

Singleton MonoBehaviour. All other scripts observe events from this.

```csharp
// Key members
string CurrentTaskId;
Dictionary<uint, SolutionMsg> SolutionCache;
TaskDescriptionMsg LastDescription;
TaskStatisticsMsg LastStatistics;

event Action<TaskDescriptionMsg> OnDescriptionReceived;
event Action<TaskStatisticsMsg> OnStatisticsUpdated;
event Action<SolutionMsg>       OnSolutionReceived;

void FetchSolution(uint id, Action<SolutionMsg> callback);
// → checks SolutionCache; if missing, calls /get_solution_{task_id} service
```

Subscriptions use `ROSConnection.Subscribe<T>()` with QoS matching the spec:

* `/description` and `/statistics` → TransientLocal (use overload with `QoSSettings` if available, else default)
* `/solution` → Reliable

Service name built as `"/get_solution_" + CurrentTaskId` whenever `task_id` changes from `/description`.

### 2. `Assets/Scripts/MTCTrajectoryPlayer.cs`

Drives the physical robot through a `SolutionMsg`. Reuses the coroutine interpolation pattern from `TrajectoryReplay.cs` but operates on `SubTrajectoryMsg[]`.

```csharp
[SerializeField] DirectArticulationIKController ikController;
[SerializeField] CollisionObjectsListenerSimple sceneListener; // for scene_diff

void PlaySolution(SolutionMsg solution);
void Stop();
void SeekToSegment(int segmentIndex);

// Internal coroutine: iterate sub_trajectory[]
//   - empty joint_trajectory.points → apply scene_diff, continue
//   - non-empty → ApplyJointState per point with time_from_start delta
```

### 3. `Assets/Scripts/MTCDashboardPanel.cs` + `Assets/UI Toolkit/MTCDashboard.uxml`

World-space UIToolkit panel (same setup as `MoveItPlanningRequestMenuUI`: `UIDocument` on a `RenderTexture` canvas, `XRGrabInteractable` on the panel GameObject).

**UXML layout — 4 tab buttons across the top, one content region:**

**Tab ① Plan** — Buttons wired to `PickPlaceTaskRecorder.StartRecording()` / `StopRecording()`. Shows current object_id and recording status label.

**Tab ② Stages** — Populated from `MTCDataManager.OnDescriptionReceived` and `OnStatisticsUpdated`.

* Build tree from flat `stages[]` using `parent_id` (stage where `id == parent_id` is root).
* Each row: indented name + solved count + num_failed + compute_time (seconds, 2 dp).
* Re-render on every statistics update; full rebuild only when `task_id` changes.

**Tab ③ Solutions** — Populated from `MTCDataManager.SolutionCache`.

* ListView sorted ascending by `sum(sub_trajectory[*].info.cost)`.
* Per row: rank, total cost, "Fetch" button (triggers `FetchSolution`) if not cached.
* Selecting a row loads the solution into `MTCTrajectoryPlayer` for preview.
* Sub-solution view: when a solution is selected, show `sub_trajectory[]` grouped by `stage_id`, each group labelled with `StageDescription.name`. Allows filtering by stage variant.

**Tab ④ Execute** — Sends selected solution to robot.

* "Execute" button → send `ExecuteTaskSolutionAction` goal with selected `SolutionMsg`.
* Progress bar driven by feedback `sub_id / sub_no`.
* Active stage label: `StageDescription.name` where `id == sub_trajectory[sub_id].info.stage_id`.
* Cancel button + result label (Success / Cancelled / Failed: `error_code.val`).

### 4. `Assets/RosMessages/MoveitTaskConstructorMsgs/action/` — Action message types

Manually create three C# files following the ROS-TCP-Connector convention (same pattern as `Assets/RosMessages/Moveit/action/` files that were deleted):

* `ExecuteTaskSolutionActionGoal.cs` — wraps `SolutionMsg solution`
* `ExecuteTaskSolutionFeedback.cs` — `uint sub_id`, `uint sub_no`
* `ExecuteTaskSolutionResult.cs` — `MoveItErrorCodesMsg error_code`

Use `ROSConnection.RegisterActionClient<>()` and `SendActionGoalAndWaitForResultAsync()` (or the callback-based variant) in `MTCDashboardPanel`.

---

## Files to Modify

### `Assets/UI Toolkit/WristMenu.uxml`

Add one `<Button name="mtc-button" text="MTC" />` to the main button row (alongside the existing shape and pick-place buttons).

### `Assets/Scripts/WristMenuController.cs`

Wire `mtc-button.clicked` → toggle `MTCDashboardPanel.gameObject.SetActive()`. Mirror the existing pattern used for the MoveIt planning menu toggle.

---

## ROS Frame Conversion

All `Pose` fields from MTC messages need the same ROS↔Unity axis flip already used in `Conversions.cs` (`RosUnityConversion.UnityToRosPosition` / `RosUnityConversion.UnityToRosQuaternion`). Apply the inverse (ROS→Unity) when reading `start_scene` poses or `primitive_poses` from `SolutionMsg`.

---

## Verification

1. In Unity Editor (or on device), open WristMenu → tap **MTC** → dashboard panel appears in world space.
2. **Stages tab** : After running the MTC demo node, the stage tree populates within ~1 s; solved/failed counts update with each planning iteration.
3. **Solutions tab** : After planning completes, solutions appear ranked by cost; selecting one drives the robot through the trajectory in editor (ghost replay using `MTCTrajectoryPlayer`).
4. **Execute tab** : Pressing Execute with a solution selected logs action goal sent; progress bar advances; result label shows "Execution succeeded" on `error_code.val == 1`.
5. **Plan tab** : Start recording → grab an object → place it → stop recording → `PickPlaceTask` published (verify with `ros2 topic echo /pick_place_task`).
