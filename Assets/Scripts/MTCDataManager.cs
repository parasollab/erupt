using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.MoveitTaskConstructorMsgs;

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

    public event Action<TaskDescriptionMsg> OnDescriptionReceived;
    public event Action<TaskStatisticsMsg> OnStatisticsUpdated;
    public event Action<SolutionMsg> OnSolutionReceived;

    private ROSConnection ros;
    private string registeredServiceName;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<TaskDescriptionMsg>($"/{topicPrefix}/description", OnDescription);
        ros.Subscribe<TaskStatisticsMsg>($"/{topicPrefix}/statistics", OnStatistics);
        ros.Subscribe<SolutionMsg>($"/{topicPrefix}/solution", OnSolution);
    }

    private void OnDescription(TaskDescriptionMsg msg)
    {
        bool taskChanged = msg.task_id != CurrentTaskId;
        LastDescription = msg;
        CurrentTaskId = msg.task_id;

        if (taskChanged)
        {
            Solutions.Clear();
            solutionBySubSolId.Clear();
            if (!string.IsNullOrEmpty(CurrentTaskId))
                RegisterService();
        }

        OnDescriptionReceived?.Invoke(msg);
    }

    private void OnStatistics(TaskStatisticsMsg msg)
    {
        LastStatistics = msg;
        OnStatisticsUpdated?.Invoke(msg);
    }

    private void OnSolution(SolutionMsg msg)
    {
        Solutions.Add(msg);
        foreach (var sub in msg.sub_solution)
            solutionBySubSolId[sub.info.id] = msg;
        OnSolutionReceived?.Invoke(msg);
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
                Solutions.Add(resp.solution);
                foreach (var sub in resp.solution.sub_solution)
                    solutionBySubSolId[sub.info.id] = resp.solution;
                callback?.Invoke(resp.solution);
            });
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
