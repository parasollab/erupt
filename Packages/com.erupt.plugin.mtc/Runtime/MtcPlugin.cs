using System;
using System.Linq;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using RosMessageTypes.MoveitTaskConstructorMsgs;

/// <summary>
/// MoveIt Task Constructor as an ERUPT planning plugin. MTC plans on the ROS side from a
/// recorded task; here "plan" means "take the latest solution", preview plays it on
/// <see cref="MtcSolutionPlayer"/>, execute sends it to /execute_task_solution through
/// <see cref="MtcClient"/>. Depends on the MoveIt plugin for the planning scene.
/// </summary>
public class MtcPlugin : PlanningPlugin
{
    [Tooltip("Sibling components by default; found in the scene if empty.")]
    [SerializeField] private MtcClient client;
    [SerializeField] private MtcSolutionPlayer player;

    private static readonly string[] Deps = { "moveit" };

    public override string Id => "mtc";
    public override string DisplayName => "MTC";
    public override System.Collections.Generic.IReadOnlyList<string> DependsOn => Deps;

    public MtcClient Client => client;
    public MtcSolutionPlayer Player => player;

    private void Awake()
    {
        if (client == null) client = GetComponent<MtcClient>();
        if (player == null) player = GetComponent<MtcSolutionPlayer>();
    }

    protected override void OnRegister(IEruptContext context)
    {
        if (client == null) client = FindFirstObjectByType<MtcClient>(FindObjectsInactive.Include);
        if (player == null) player = FindFirstObjectByType<MtcSolutionPlayer>(FindObjectsInactive.Include);
        if (client == null) Debug.LogError("[mtc] No MtcClient in the scene.", this);
        if (player != null && context.Robot != null) player.SetRobot(context.Robot);

        // Inject the context's bus before the client starts (same frame as registration
        // when the prefab is enabled, otherwise the client keeps RosBus.Instance).
        try { client?.Initialise(context.Ros); }
        catch (InvalidOperationException) { /* already started: it used RosBus.Instance, which is the same bus */ }

        if (client != null) client.OnSolutionReceived += OnSolution;
        base.OnRegister(context);
    }

    protected override void OnUnregister(IEruptContext context)
    {
        if (client != null) client.OnSolutionReceived -= OnSolution;
        StopPreview();
        base.OnUnregister(context);
    }

    private void OnSolution(SolutionMsg solution) => PublishResult(ToResult(solution));

    private PlanResult ToResult(SolutionMsg solution)
    {
        var first = solution.sub_trajectory?.FirstOrDefault(s => s.trajectory?.joint_trajectory?.points?.Length > 0);
        return new PlanResult
        {
            Trajectory = first?.trajectory?.joint_trajectory,
            PlannerPayload = solution,
            PlannerId = Id,
            Label = $"MTC solution {MtcClient.TopLevelId(solution)}"
        };
    }

    /// <summary>MTC goals come from the recorded task; set-goal has nothing to set here.</summary>
    public override InteractionRefusal SetGoal(ISelectable endEffector) =>
        InteractionRefusal.Refuse("MTC plans from the recorded task; record a pick/place instead.",
            endEffector?.GameObject != null ? endEffector.GameObject.transform.position : Vector3.zero);

    public override void RequestPlan(PlanPreferences preferences, Action<PlanResult> done)
    {
        var latest = client != null && client.Solutions.Count > 0 ? client.Solutions[^1] : null;
        if (latest == null) { done?.Invoke(null); return; }
        done?.Invoke(LastResult != null && ReferenceEquals(LastResult.PlannerPayload, latest) ? LastResult : PublishResult(ToResult(latest)));
    }

    public override void Preview(PlanResult plan)
    {
        if (plan?.PlannerPayload is not SolutionMsg solution || player == null) return;
        player.PlaySolution(solution);
    }

    public override void StopPreview()
    {
        if (player != null) player.Stop();
    }

    public override async void Execute(PlanResult plan, Action<ExecutionStatus> status)
    {
        if (plan?.PlannerPayload is not SolutionMsg solution || client == null)
        {
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "No MTC solution to execute."));
            return;
        }
        StopPreview();
        status?.Invoke(new ExecutionStatus(ExecutionPhase.Sending));
        try
        {
            var result = await client.ExecuteSolutionAsync(solution);
            status?.Invoke(new ExecutionStatus(Map(client.LastExecutionStatus), client.LastExecutionStatus));
        }
        catch (Exception e)
        {
            status?.Invoke(new ExecutionStatus(Map(client.LastExecutionStatus), e.Message));
        }
    }

    private static ExecutionPhase Map(string mtcStatus)
    {
        if (string.IsNullOrEmpty(mtcStatus)) return ExecutionPhase.Failed;
        if (mtcStatus.StartsWith("SUCCEEDED")) return ExecutionPhase.Succeeded;
        if (mtcStatus.StartsWith("CANCEL")) return ExecutionPhase.Canceled;
        if (mtcStatus.StartsWith("ABORTED")) return ExecutionPhase.Aborted;
        if (mtcStatus.StartsWith("EXECUTING") || mtcStatus.StartsWith("SENDING")) return ExecutionPhase.Executing;
        if (mtcStatus.StartsWith("UNAVAILABLE") || mtcStatus.StartsWith("ENDPOINT") || mtcStatus.StartsWith("REJECTED")) return ExecutionPhase.Unavailable;
        return ExecutionPhase.Failed;
    }
}
