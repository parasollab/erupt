using Erupt.Ros;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Moveit;

public class MTCDataManager : MonoBehaviour
{
    public static MTCDataManager Instance { get; private set; }

    [SerializeField] private string topicPrefix = "erupt_pick_place";

    public string CurrentTaskId { get; private set; }
    public TaskDescriptionMsg LastDescription { get; private set; }
    public TaskStatisticsMsg LastStatistics { get; private set; }

    // All complete solutions received, in arrival order
    public List<SolutionMsg> Solutions { get; } = new();

    // Keyed by sub_solution.info.id so any StageStatistics.solved[] ID resolves
    private readonly Dictionary<uint, SolutionMsg> solutionBySubSolId = new();

    // Top-level ids already in Solutions — the ROS node republishes introspection data
    // at 1 Hz for late joiners, so duplicates on /solution are expected.
    private readonly HashSet<uint> knownSolutionIds = new();

    public event Action<TaskDescriptionMsg> OnDescriptionReceived;
    public event Action<TaskStatisticsMsg> OnStatisticsUpdated;
    public event Action<SolutionMsg> OnSolutionReceived;
    public event Action OnTaskReset;
    public event Action<string> OnExecutionStatus;

    public string LastExecutionStatus { get; private set; } = "";

    private IRosBus ros;
    private string registeredServiceName;
    private IRosActionClient<ExecuteTaskSolutionGoal, ExecuteTaskSolutionResult,
        ExecuteTaskSolutionFeedback> executionClient;
    private IRosActionGoal<ExecuteTaskSolutionResult> activeExecutionGoal;

    private const string ExecuteAction = "/execute_task_solution";

    public bool ExecutionAvailable => executionClient?.State == RosActionClientState.Ready;
    public bool IsExecuting => activeExecutionGoal != null;
    public event Action<ExecuteTaskSolutionFeedback> OnExecutionFeedback;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        ros = RosBus.Instance;
        ros.Subscribe<TaskDescriptionMsg>("/description", OnDescription);
        ros.Subscribe<TaskStatisticsMsg>("/statistics", OnStatistics);
        ros.Subscribe<SolutionMsg>("/solution", OnSolution);
        executionClient = ros.RegisterActionClient<ExecuteTaskSolutionGoal,
            ExecuteTaskSolutionResult, ExecuteTaskSolutionFeedback>(ExecuteAction);
        executionClient.StateChanged += OnActionClientStateChanged;
        OnActionClientStateChanged(executionClient.State);
    }

    public async Task<RosActionResult<ExecuteTaskSolutionResult>> ExecuteSolutionAsync(
        SolutionMsg solution)
    {
        if (solution == null) throw new ArgumentNullException(nameof(solution));
        if (!ExecutionAvailable)
            throw new RosActionException("action_not_ready",
                executionClient?.LastError ?? "ExecuteTaskSolution action is not ready");
        if (activeExecutionGoal != null)
            throw new InvalidOperationException("A task solution is already executing");

        SetExecutionStatus("SENDING");
        try
        {
            var response = await executionClient.SendGoalAsync(
                new ExecuteTaskSolutionGoal(solution), OnExecutionFeedbackReceived);
            if (!response.Accepted)
            {
                SetExecutionStatus("REJECTED");
                throw new RosActionException("goal_rejected", "ExecuteTaskSolution goal was rejected");
            }

            activeExecutionGoal = response.Goal;
            SetExecutionStatus("EXECUTING");
            var result = await activeExecutionGoal.Result;
            int errorCode = result.Result?.error_code?.val ?? MoveItErrorCodesMsg.FAILURE;
            if (result.Status == RosActionGoalStatus.Succeeded &&
                errorCode == MoveItErrorCodesMsg.SUCCESS)
                SetExecutionStatus("SUCCEEDED");
            else if (result.Status == RosActionGoalStatus.Canceled)
                SetExecutionStatus("CANCELED");
            else if (result.Status == RosActionGoalStatus.Aborted)
                SetExecutionStatus($"ABORTED (MoveIt {errorCode})");
            else
                SetExecutionStatus($"FAILED ({result.Status}, MoveIt {errorCode})");
            return result;
        }
        catch (RosActionException exception)
        {
            switch (exception.Code)
            {
                case "goal_rejected":
                    SetExecutionStatus("REJECTED");
                    break;
                case "server_unavailable":
                case "action_not_ready":
                    SetExecutionStatus("UNAVAILABLE: " + exception.Message);
                    break;
                case "connection_lost":
                    SetExecutionStatus("DISCONNECTED: remote goal may still be running");
                    break;
                case "unsupported_endpoint":
                    SetExecutionStatus("ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                    break;
                default:
                    SetExecutionStatus("ACTION ERROR: " + exception.Message);
                    break;
            }
            throw;
        }
        catch (Exception exception)
        {
            SetExecutionStatus("ERROR: " + exception.Message);
            throw;
        }
        finally
        {
            activeExecutionGoal = null;
        }
    }

    public async Task<RosActionCancelResponse> CancelExecutionAsync()
    {
        if (activeExecutionGoal == null)
            throw new InvalidOperationException("No task solution is executing");
        IRosActionGoal<ExecuteTaskSolutionResult> goal = activeExecutionGoal;
        var response = await goal.CancelAsync();
        bool terminal = goal.Status == RosActionGoalStatus.Succeeded ||
            goal.Status == RosActionGoalStatus.Canceled ||
            goal.Status == RosActionGoalStatus.Aborted;
        if (!terminal)
            SetExecutionStatus(response.Accepted
                ? "CANCELING"
                : $"CANCEL REJECTED ({response.ReturnCode})");
        return response;
    }

    private void OnExecutionFeedbackReceived(ExecuteTaskSolutionFeedback feedback)
    {
        OnExecutionFeedback?.Invoke(feedback);
    }

    private void OnActionClientStateChanged(RosActionClientState state)
    {
        switch (state)
        {
            case RosActionClientState.Ready:
                if (!IsExecuting) SetExecutionStatus("READY");
                break;
            case RosActionClientState.Unsupported:
                SetExecutionStatus("ENDPOINT UPGRADE REQUIRED: ROS 2 actions unsupported");
                break;
            case RosActionClientState.Faulted:
                SetExecutionStatus("ACTION ERROR: " + executionClient.LastError);
                break;
            default:
                if (!IsExecuting) SetExecutionStatus("WAITING FOR ACTION ENDPOINT");
                break;
        }
    }

    private void SetExecutionStatus(string status)
    {
        LastExecutionStatus = status;
        OnExecutionStatus?.Invoke(status);
    }

    private void OnDescription(TaskDescriptionMsg msg)
    {
        // Empty stage list is MTC's reset signal (task destroyed).
        if (msg.stages == null || msg.stages.Length == 0)
        {
            LastDescription = msg;
            LastStatistics = null;
            ClearSolutions();
            OnTaskReset?.Invoke();
            OnDescriptionReceived?.Invoke(msg);
            return;
        }

        bool taskChanged = msg.task_id != CurrentTaskId;
        LastDescription = msg;
        CurrentTaskId = msg.task_id;

        if (taskChanged)
        {
            ClearSolutions();
            if (!string.IsNullOrEmpty(CurrentTaskId))
                RegisterService();
        }

        OnDescriptionReceived?.Invoke(msg);
    }

    private void ClearSolutions()
    {
        Solutions.Clear();
        solutionBySubSolId.Clear();
        knownSolutionIds.Clear();
    }

    private void OnStatistics(TaskStatisticsMsg msg)
    {
        LastStatistics = msg;
        OnStatisticsUpdated?.Invoke(msg);
    }

    private void OnSolution(SolutionMsg msg)
    {
        if (!AddSolution(msg)) return;
        OnSolutionReceived?.Invoke(msg);
    }

    /// <summary>The introspection id identifying this solution (its first sub-solution).</summary>
    public static uint TopLevelId(SolutionMsg msg) =>
        (msg.sub_solution != null && msg.sub_solution.Length > 0) ? msg.sub_solution[0].info.id : 0;

    // Returns false if this solution was already known (republished duplicates are expected).
    private bool AddSolution(SolutionMsg msg)
    {
        uint id = TopLevelId(msg);
        if (id != 0 && !knownSolutionIds.Add(id)) return false;

        Solutions.Add(msg);
        foreach (var sub in msg.sub_solution)
            solutionBySubSolId[sub.info.id] = msg;
        return true;
    }

    private void RegisterService()
    {
        string svcName = $"/{topicPrefix}/get_solution_{CurrentTaskId}";
        if (svcName == registeredServiceName) return;
        ros.RegisterRosService<GetSolutionRequest, GetSolutionResponse>(svcName);
        registeredServiceName = svcName;
        Debug.Log($"[MTCDataManager] Registered service {svcName}");
    }

    public void FetchSolution(uint solutionId, Action<SolutionMsg> callback)
    {
        if (solutionBySubSolId.TryGetValue(solutionId, out var cached)) { callback?.Invoke(cached); return; }

        if (string.IsNullOrEmpty(registeredServiceName))
        {
            Debug.LogWarning("[MTCDataManager] No task id yet — cannot fetch solution.");
            return;
        }

        ros.SendServiceMessage<GetSolutionResponse>(
            registeredServiceName,
            new GetSolutionRequest(solutionId),
            resp =>
            {
                if (resp?.solution == null) return;
                AddSolution(resp.solution);
                callback?.Invoke(resp.solution);
            });
    }

    void OnDestroy()
    {
        if (executionClient != null)
            executionClient.StateChanged -= OnActionClientStateChanged;
        if (Instance == this) Instance = null;
    }
}
