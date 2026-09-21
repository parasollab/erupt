using Erupt.Ros;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.StudyInterfaces;
using GetSolutionRequest = RosMessageTypes.StudyInterfaces.GetSolutionRequest;
using GetSolutionResponse = RosMessageTypes.StudyInterfaces.GetSolutionResponse;

/// <summary>What the server is doing for this client. One task at a time, so one phase.</summary>
public enum PickPlacePhase { Idle, Planning, Executing }

/// <summary>How the last goal ended. Canceled is a normal outcome, not an error.</summary>
public enum PickPlaceOutcome { None, Succeeded, Canceled, Aborted, Rejected, Unavailable, Disconnected, Failed }

/// <summary>
/// Client for mtc_pick_place_server: plans on <c>/pick_place</c>, lists the solutions from
/// <c>/pick_place/statistics</c>, fetches them with <c>/get_solution</c> and executes one on
/// <c>/execute_solution</c>. See Documentation~/ros-interface.md for the wire protocol.
/// </summary>
/// <remarks>
/// Rules the server enforces, mirrored here:
/// one goal at a time (a goal sent while busy is rejected, not queued); a new plan
/// invalidates every earlier task_id and solution_id, so all task state is dropped when a
/// plan starts and ids outside <see cref="SolutionIds"/> are refused locally; task_id is
/// <c>&lt;introspection id&gt;:&lt;n&gt;</c> and goes back verbatim, while the
/// <c>/pick_place/*</c> topics carry it without the <c>:&lt;n&gt;</c> suffix.
/// Disconnecting does not cancel a goal, so the active goal is cancelled on destroy,
/// pause and quit. The bus is shared (<see cref="RosBus.Instance"/>): the endpoint only
/// talks to its most recent connection, so this never opens one of its own.
/// </remarks>
public class PickPlaceClient : MonoBehaviour
{
    /// <summary>Statistics stage holding the executable solution ids: the root container. Stage 0 is the Task wrapper and is never published.</summary>
    public const uint RootStageId = 1;

    [Tooltip("PickPlace action served by mtc_pick_place_server.")]
    [SerializeField] private string actionName = "/pick_place";

    [Tooltip("Frame the place pose is expressed in.")]
    [SerializeField] private string placeFrameId = "world";

    [SerializeField] private string executeActionName = "/execute_solution";
    [SerializeField] private string getSolutionService = "/get_solution";
    [SerializeField] private string descriptionTopic = "/pick_place/description";
    [SerializeField] private string statisticsTopic = "/pick_place/statistics";

    [Tooltip("max_solutions for plan-only goals; 0 = server default.")]
    [SerializeField] private uint maxSolutions = 3;

    [Tooltip("A /get_solution round trip is 20-40 ms; past this the fetch is reported as failed.")]
    [SerializeField] private float getSolutionTimeoutSeconds = 5f;

    [Tooltip("Cancel the active goal when the app is paused. The robot keeps moving otherwise.")]
    [SerializeField] private bool cancelOnPause = true;

    public string ActionName => actionName;
    public string PlaceFrameId => placeFrameId;

    public PickPlacePhase Phase { get; private set; } = PickPlacePhase.Idle;
    public bool Busy => Phase != PickPlacePhase.Idle;
    public bool Available => planClient != null && planClient.State == RosActionClientState.Ready;
    public bool ExecutionAvailable => executeClient != null && executeClient.State == RosActionClientState.Ready;

    /// <summary>Current task, verbatim from the server; null until the first feedback that carries it.</summary>
    public string TaskId { get; private set; }

    /// <summary>Executable solution ids of the current task, in the server's order (ascending cost).</summary>
    public IReadOnlyList<uint> SolutionIds => solutionIds;

    /// <summary>Stage tree of the current task; null until its description arrives.</summary>
    public TaskDescriptionMsg Description { get; private set; }
    public TaskStatisticsMsg Statistics { get; private set; }

    /// <summary>Latest planning feedback of the current goal.</summary>
    public PickPlaceFeedback LastFeedback { get; private set; }

    /// <summary>Latest execution feedback; null when nothing has executed since the last plan.</summary>
    public ExecuteSolutionFeedback LastExecutionFeedback { get; private set; }

    /// <summary>Solution being executed or last executed on /execute_solution.</summary>
    public uint ExecutingSolutionId { get; private set; }

    public string LastStatus { get; private set; } = "";
    public PickPlaceOutcome LastOutcome { get; private set; } = PickPlaceOutcome.None;

    public event Action<string> OnStatus;
    public event Action<PickPlaceFeedback> OnFeedback;
    public event Action<ExecuteSolutionFeedback> OnExecutionFeedback;
    /// <summary>A new plan started: every id handed out before this is dead.</summary>
    public event Action OnTaskReset;
    public event Action OnSolutionIdsChanged;
    public event Action OnDescriptionChanged;
    public event Action OnStatisticsChanged;
    public event Action OnPhaseChanged;

    private IRosBus ros;
    private bool started;
    private IRosActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback> planClient;
    private IRosActionClient<ExecuteSolutionGoal, ExecuteSolutionResult, ExecuteSolutionFeedback> executeClient;

    // The accepted goal, as a cancel delegate so plan and execute goals share one slot.
    private Func<Task<RosActionCancelResponse>> cancelActiveGoal;
    private Func<RosActionGoalStatus> activeGoalStatus;
    private bool cancelRequested;

    private readonly List<uint> solutionIds = new();
    private readonly Dictionary<uint, SolutionMsg> solutionCache = new();
    // Solutions fetched with include_start_scene. Kept apart: one fetched without its start
    // scene must never answer a request that needs it.
    private readonly Dictionary<uint, SolutionMsg> solutionWithSceneCache = new();
    // Bumped per plan so a /get_solution response for a dead task is dropped.
    private int taskGeneration;
    // Latest topic messages since the plan started. The description can arrive before the
    // feedback that names the task, so both are held and matched once the task id is known.
    private TaskDescriptionMsg heldDescription;
    private TaskStatisticsMsg heldStatistics;

    /// <summary>Use this bus instead of <see cref="RosBus.Instance"/>. Must be called before Start.</summary>
    public void Initialise(IRosBus bus)
    {
        if (started) throw new InvalidOperationException("PickPlaceClient already started; initialise before Start.");
        ros = bus;
    }

    void Start()
    {
        started = true;
        ros ??= RosBus.Instance;
        // Order matters: the endpoint subscribes volatile, so a subscription made after a
        // goal gets no replay and the description is silently lost.
        ros.Subscribe<TaskDescriptionMsg>(descriptionTopic, OnDescription);
        ros.Subscribe<TaskStatisticsMsg>(statisticsTopic, OnStatistics);
        planClient = ros.RegisterActionClient<PickPlaceGoal, PickPlaceResult, PickPlaceFeedback>(actionName);
        executeClient = ros.RegisterActionClient<ExecuteSolutionGoal, ExecuteSolutionResult,
            ExecuteSolutionFeedback>(executeActionName);
        ros.RegisterRosService<GetSolutionRequest, GetSolutionResponse>(getSolutionService);
        planClient.StateChanged += OnClientStateChanged;
        OnClientStateChanged(planClient.State);
    }

    void OnDestroy()
    {
        CancelForTeardown();
        if (planClient != null) planClient.StateChanged -= OnClientStateChanged;
        if (ros != null && started)
        {
            ros.Unsubscribe<TaskDescriptionMsg>(descriptionTopic, OnDescription);
            ros.Unsubscribe<TaskStatisticsMsg>(statisticsTopic, OnStatistics);
        }
    }

    void OnApplicationPause(bool paused)
    {
        if (paused && cancelOnPause) CancelForTeardown();
    }

    void OnApplicationQuit() => CancelForTeardown();

    // --- plan -----------------------------------------------------------------------

    /// <summary>Plan only (execute: false): the solutions appear in <see cref="SolutionIds"/>.</summary>
    public Task<RosActionResult<PickPlaceResult>> PlanAsync(
        string objectId, PoseStampedMsg placePose, PlanningSceneMsg startSceneDiff = null) =>
        SendPickPlaceAsync(objectId, placePose, false, maxSolutions, startSceneDiff);

    /// <summary>Plan and execute in one goal, skipping the browser.</summary>
    public Task<RosActionResult<PickPlaceResult>> PlanAndExecuteAsync(
        string objectId, PoseStampedMsg placePose, PlanningSceneMsg startSceneDiff = null) =>
        SendPickPlaceAsync(objectId, placePose, true, 0, startSceneDiff);

    /// <summary>Build the stamped place pose from a ROS-frame position and orientation.</summary>
    public PoseStampedMsg PlacePose(PointMsg position, QuaternionMsg orientation) =>
        new PoseStampedMsg(
            new RosMessageTypes.Std.HeaderMsg(new RosMessageTypes.BuiltinInterfaces.TimeMsg(), placeFrameId),
            new PoseMsg(position, orientation));

    private async Task<RosActionResult<PickPlaceResult>> SendPickPlaceAsync(
        string objectId, PoseStampedMsg placePose, bool execute, uint max, PlanningSceneMsg startSceneDiff)
    {
        if (string.IsNullOrEmpty(objectId)) throw new ArgumentException("object_id is required", nameof(objectId));
        if (placePose == null) throw new ArgumentNullException(nameof(placePose));
        if (Busy) throw new InvalidOperationException("The server runs one task at a time and a goal is active");
        if (!Available)
        {
            Finish(PickPlaceOutcome.Unavailable, "UNAVAILABLE: " + (planClient?.LastError ?? $"{actionName} is not ready"));
            throw new RosActionException("action_not_ready", planClient?.LastError ?? $"{actionName} is not ready");
        }

        BeginTask();
        SetPhase(PickPlacePhase.Planning);
        SetStatus("SENDING");
        try
        {
            var goal = new PickPlaceGoal(objectId, placePose, startSceneDiff ?? new PlanningSceneMsg(), execute, max);
            var response = await planClient.SendGoalAsync(goal, OnPlanFeedback);
            if (!response.Accepted)
                throw new RosActionException("goal_rejected",
                    "the server is busy with another task, or refused the goal");

            Track(response.Goal);
            var result = await response.Goal.Result;
            // A goal that ends before its first feedback still names its task in the result.
            if (string.IsNullOrEmpty(TaskId) && !string.IsNullOrEmpty(result.Result?.task_id))
                AdoptTaskId(result.Result.task_id);
            Report(result.Status, result.Result != null && result.Result.success,
                result.Result?.message, result.Result?.error_code, execute ? "SUCCEEDED" : "PLANNED");
            return result;
        }
        catch (Exception exception)
        {
            ReportException(exception);
            throw;
        }
        finally
        {
            Untrack();
        }
    }

    private void OnPlanFeedback(PickPlaceFeedback feedback)
    {
        if (feedback == null) return;
        LastFeedback = feedback;
        if (string.IsNullOrEmpty(TaskId) && !string.IsNullOrEmpty(feedback.task_id))
            AdoptTaskId(feedback.task_id);
        if (feedback.stage == "executing") SetPhase(PickPlacePhase.Executing);
        OnFeedback?.Invoke(feedback);
        SetStatus(DescribeProgress(feedback));
    }

    /// <summary>Progress line; best_cost is +Infinity until the first solution and is not printed as a number.</summary>
    public static string DescribeProgress(PickPlaceFeedback feedback)
    {
        string stage = string.IsNullOrEmpty(feedback.stage) ? "WORKING" : feedback.stage.ToUpperInvariant();
        if (feedback.stage != "planning") return stage;
        if (feedback.solutions_found == 0 || float.IsInfinity(feedback.best_cost) || float.IsNaN(feedback.best_cost))
            return $"{stage}: no solution yet";
        string count = feedback.solutions_found == 1 ? "1 solution" : $"{feedback.solutions_found} solutions";
        return $"{stage}: {count}, best cost {feedback.best_cost:F2}";
    }

    // --- execute --------------------------------------------------------------------

    /// <summary>Execute one of <see cref="SolutionIds"/> on /execute_solution.</summary>
    public async Task<RosActionResult<ExecuteSolutionResult>> ExecuteAsync(uint solutionId)
    {
        if (Busy) throw new InvalidOperationException("The server runs one task at a time and a goal is active");
        if (string.IsNullOrEmpty(TaskId) || !solutionIds.Contains(solutionId))
        {
            // Never put an id from an earlier plan on the wire.
            Finish(PickPlaceOutcome.Rejected, $"REJECTED: solution {solutionId} is not part of the current plan");
            throw new RosActionException("stale_solution", $"solution {solutionId} is not part of the current plan");
        }
        if (!ExecutionAvailable)
        {
            Finish(PickPlaceOutcome.Unavailable, "UNAVAILABLE: " + (executeClient?.LastError ?? $"{executeActionName} is not ready"));
            throw new RosActionException("action_not_ready", executeClient?.LastError ?? $"{executeActionName} is not ready");
        }

        ExecutingSolutionId = solutionId;
        LastExecutionFeedback = null;
        LastOutcome = PickPlaceOutcome.None;
        cancelRequested = false;
        SetPhase(PickPlacePhase.Executing);
        SetStatus("SENDING");
        try
        {
            var response = await executeClient.SendGoalAsync(
                new ExecuteSolutionGoal(TaskId, solutionId), OnExecuteFeedback);
            if (!response.Accepted)
                throw new RosActionException("goal_rejected",
                    "the task or solution id is stale or unknown; plan again");

            Track(response.Goal);
            SetStatus("EXECUTING");
            var result = await response.Goal.Result;
            Report(result.Status, result.Result != null && result.Result.success,
                result.Result?.message, result.Result?.error_code, "SUCCEEDED");
            return result;
        }
        catch (Exception exception)
        {
            ReportException(exception);
            throw;
        }
        finally
        {
            Untrack();
        }
    }

    private void OnExecuteFeedback(ExecuteSolutionFeedback feedback)
    {
        if (feedback == null) return;
        LastExecutionFeedback = feedback;
        OnExecutionFeedback?.Invoke(feedback);
        string stage = StageName(feedback.stage_id);
        SetStatus($"EXECUTING: step {feedback.sub_id + 1}/{feedback.sub_no} done" +
                  (string.IsNullOrEmpty(stage) ? "" : $" ({stage})"));
    }

    // --- cancel ---------------------------------------------------------------------

    /// <summary>
    /// Cancel the active goal, planning or executing. The goal then ends CANCELED with
    /// error code PREEMPTED, reported as <see cref="PickPlaceOutcome.Canceled"/>.
    /// A cancel asked for before the goal is accepted is sent as soon as it is.
    /// </summary>
    public async Task<bool> CancelAsync()
    {
        if (!Busy) return false;
        cancelRequested = true;
        if (cancelActiveGoal == null) return true;

        Func<RosActionGoalStatus> status = activeGoalStatus;
        var response = await cancelActiveGoal();
        if (!IsTerminal(status()))
            SetStatus(response.Accepted ? "CANCELING" : $"CANCEL REJECTED ({response.ReturnCode})");
        return response.Accepted;
    }

    private async void CancelForTeardown()
    {
        if (!Busy) return;
        try { await CancelAsync(); }
        catch (Exception exception) { Debug.LogWarning($"[PickPlaceClient] cancel on teardown failed: {exception.Message}"); }
    }

    private void Track<TResult>(IRosActionGoal<TResult> goal)
        where TResult : Unity.Robotics.ROSTCPConnector.MessageGeneration.Message
    {
        cancelActiveGoal = goal.CancelAsync;
        activeGoalStatus = () => goal.Status;
        if (cancelRequested) CancelForTeardown();
    }

    private void Untrack()
    {
        cancelActiveGoal = null;
        activeGoalStatus = null;
        cancelRequested = false;
        SetPhase(PickPlacePhase.Idle);
    }

    private static bool IsTerminal(RosActionGoalStatus status) =>
        status == RosActionGoalStatus.Succeeded || status == RosActionGoalStatus.Canceled ||
        status == RosActionGoalStatus.Aborted;

    // --- solutions ------------------------------------------------------------------

    /// <summary>
    /// Fetch a solution of the current task (cached per task): one of <see cref="SolutionIds"/>,
    /// or any stage's partial or failed solution listed in <see cref="Statistics"/>. With
    /// <paramref name="includeStartScene"/> the server also fills <c>start_scene</c>, the state
    /// the solution starts from. A response for a task that has since been replaced is dropped.
    /// <paramref name="failed"/> gets the server's message.
    /// </summary>
    public void FetchSolution(uint solutionId, Action<SolutionMsg> fetched, Action<string> failed = null,
        bool includeStartScene = false)
    {
        if (string.IsNullOrEmpty(TaskId) || !IsKnownSolutionId(solutionId))
        {
            failed?.Invoke($"solution {solutionId} is not part of the current plan");
            return;
        }
        if (solutionWithSceneCache.TryGetValue(solutionId, out var cached) ||
            (!includeStartScene && solutionCache.TryGetValue(solutionId, out cached)))
        {
            fetched?.Invoke(cached);
            return;
        }

        int generation = taskGeneration;
        bool answered = false;
        ros.SendServiceMessage<GetSolutionResponse>(
            getSolutionService,
            new GetSolutionRequest(TaskId, solutionId, includeStartScene),
            response =>
            {
                answered = true;
                if (generation != taskGeneration) return;
                if (response == null || !response.success || response.solution == null)
                {
                    string message = string.IsNullOrEmpty(response?.message)
                        ? $"{getSolutionService} failed for solution {solutionId}" : response.message;
                    SetStatus("SOLUTION UNAVAILABLE: " + message);
                    failed?.Invoke(message);
                    return;
                }
                (includeStartScene ? solutionWithSceneCache : solutionCache)[solutionId] = response.solution;
                fetched?.Invoke(response.solution);
            });
        if (!answered) FailFetchAfterTimeout();

        // A response that cannot be deserialised (stale C# message classes) never reaches the
        // callback, so without this the browser would wait forever.
        async void FailFetchAfterTimeout()
        {
            await Task.Delay(TimeSpan.FromSeconds(getSolutionTimeoutSeconds));
            if (answered || generation != taskGeneration || this == null) return;
            answered = true;
            string message = $"no usable response from {getSolutionService} after {getSolutionTimeoutSeconds:0.#} s " +
                             "(check the Unity log for a deserialisation error)";
            SetStatus("SOLUTION UNAVAILABLE: " + message);
            failed?.Invoke(message);
        }
    }

    /// <summary>True for executable solutions and for any stage's solved[] / failed[] id.</summary>
    public bool IsKnownSolutionId(uint solutionId)
    {
        if (solutionIds.Contains(solutionId)) return true;
        if (Statistics?.stages == null) return false;
        foreach (var stage in Statistics.stages)
            if ((stage.solved != null && Array.IndexOf(stage.solved, solutionId) >= 0) ||
                (stage.failed != null && Array.IndexOf(stage.failed, solutionId) >= 0))
                return true;
        return false;
    }

    /// <summary>Stage name from the current description; null when unknown.</summary>
    public string StageName(uint stageId)
    {
        if (Description?.stages == null) return null;
        foreach (var stage in Description.stages)
            if (stage.id == stageId) return stage.name;
        return null;
    }

    /// <summary>The task id as the /pick_place/* topics carry it: everything after the last ':' removed.</summary>
    public static string TopicTaskId(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return taskId;
        int colon = taskId.LastIndexOf(':');
        return colon < 0 ? taskId : taskId.Substring(0, colon);
    }

    private void BeginTask()
    {
        taskGeneration++;
        TaskId = null;
        Description = null;
        Statistics = null;
        heldDescription = null;
        heldStatistics = null;
        LastFeedback = null;
        LastExecutionFeedback = null;
        ExecutingSolutionId = 0;
        LastOutcome = PickPlaceOutcome.None;
        cancelRequested = false;
        solutionIds.Clear();
        solutionCache.Clear();
        solutionWithSceneCache.Clear();
        OnTaskReset?.Invoke();
    }

    private void AdoptTaskId(string taskId)
    {
        TaskId = taskId;
        if (heldDescription != null) OnDescription(heldDescription);
        if (heldStatistics != null) OnStatistics(heldStatistics);
    }

    private bool IsCurrentTask(string topicTaskId) =>
        !string.IsNullOrEmpty(TaskId) && topicTaskId == TopicTaskId(TaskId);

    private void OnDescription(TaskDescriptionMsg msg)
    {
        if (msg == null) return;
        if (string.IsNullOrEmpty(TaskId)) { if (Phase == PickPlacePhase.Planning) heldDescription = msg; return; }
        // An empty stage list is MTC's "task destroyed" signal; the ids die with the next plan, not here.
        if (!IsCurrentTask(msg.task_id) || msg.stages == null || msg.stages.Length == 0) return;
        Description = msg;
        OnDescriptionChanged?.Invoke();
    }

    private void OnStatistics(TaskStatisticsMsg msg)
    {
        if (msg == null) return;
        if (string.IsNullOrEmpty(TaskId)) { if (Phase == PickPlacePhase.Planning) heldStatistics = msg; return; }
        if (!IsCurrentTask(msg.task_id)) return;
        Statistics = msg;
        OnStatisticsChanged?.Invoke();

        uint[] solved = null;
        if (msg.stages != null)
            foreach (var stage in msg.stages)
                if (stage.id == RootStageId) { solved = stage.solved; break; }
        if (solved == null || SameIds(solved)) return;
        solutionIds.Clear();
        solutionIds.AddRange(solved);
        OnSolutionIdsChanged?.Invoke();
    }

    private bool SameIds(uint[] ids)
    {
        if (ids.Length != solutionIds.Count) return false;
        for (int i = 0; i < ids.Length; i++)
            if (ids[i] != solutionIds[i]) return false;
        return true;
    }

    // --- status ---------------------------------------------------------------------

    private void Report(RosActionGoalStatus status, bool success, string message,
        MoveItErrorCodesMsg errorCode, string successWord)
    {
        bool preempted = errorCode != null && errorCode.val == MoveItErrorCodesMsg.PREEMPTED;
        if (status == RosActionGoalStatus.Canceled || preempted)
            Finish(PickPlaceOutcome.Canceled, "CANCELED");
        else if (status == RosActionGoalStatus.Succeeded && success)
            Finish(PickPlaceOutcome.Succeeded, successWord + Suffix(message));
        else if (status == RosActionGoalStatus.Aborted)
            Finish(PickPlaceOutcome.Aborted, "ABORTED" + Suffix(message));
        else
            Finish(PickPlaceOutcome.Failed, $"FAILED ({status})" + Suffix(message));
    }

    private void ReportException(Exception exception)
    {
        string code = (exception as RosActionException)?.Code;
        switch (code)
        {
            case "goal_rejected":
                Finish(PickPlaceOutcome.Rejected, "REJECTED: " + exception.Message);
                break;
            case "server_unavailable":
            case "action_not_ready":
                Finish(PickPlaceOutcome.Unavailable, "UNAVAILABLE: " + exception.Message);
                break;
            case "connection_lost":
                Finish(PickPlaceOutcome.Disconnected, "DISCONNECTED: the robot may still be moving");
                break;
            case "unsupported_endpoint":
                Finish(PickPlaceOutcome.Unavailable, "ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                break;
            default:
                Finish(PickPlaceOutcome.Failed, "ERROR: " + exception.Message);
                break;
        }
    }

    private void OnClientStateChanged(RosActionClientState state)
    {
        switch (state)
        {
            case RosActionClientState.Ready:
                if (!Busy) SetStatus("READY");
                break;
            case RosActionClientState.Unsupported:
                SetStatus("ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                break;
            case RosActionClientState.Faulted:
                SetStatus("ACTION ERROR: " + planClient.LastError);
                break;
            default:
                if (!Busy) SetStatus("WAITING FOR ACTION ENDPOINT");
                break;
        }
    }

    private static string Suffix(string message) =>
        string.IsNullOrEmpty(message) ? "" : ": " + message;

    private void Finish(PickPlaceOutcome outcome, string status)
    {
        LastOutcome = outcome;
        SetStatus(status);
    }

    private void SetPhase(PickPlacePhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        OnPhaseChanged?.Invoke();
    }

    private void SetStatus(string status)
    {
        LastStatus = status;
        OnStatus?.Invoke(status);
    }
}
