using System;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using RosMessageTypes.Moveit;

/// <summary>
/// MoveIt 2 as an ERUPT planning plugin: goal capture from the robot's current pose,
/// /plan_kinematic_path, preview on the <see cref="JointTrajectoryPlayer"/>, execution by
/// publishing the trajectory. The transport lives in <see cref="MoveItPlanningClient"/>;
/// this class is the glue to the plugin contract and the scene.
/// </summary>
public class MoveItPlugin : PlanningPlugin
{
    [SerializeField] private MoveItPlanningSettings settings = new();
    [Tooltip("Plays previews. Found in the scene if empty.")]
    [SerializeField] private JointTrajectoryPlayer player;
    [Tooltip("Query /query_planner_interface on register and keep retrying until it answers.")]
    [SerializeField] private bool autoQueryPlanners = true;

    private RobotStateMsg startState;
    private RobotStateMsg goalState;
    private bool previewing;

    public override string Id => "moveit";
    public override string DisplayName => "MoveIt";

    public MoveItPlanningClient Client { get; private set; }
    public MoveItPlanningSettings Settings => settings;
    public bool HasStart => startState != null;
    public bool HasGoal => goalState != null;
    public bool IsPreviewing => previewing;

    /// <summary>Fires with the MoveIt error text when a plan request fails.</summary>
    public event Action<string> PlanFailed;

    protected override void OnRegister(IEruptContext context)
    {
        Client = new MoveItPlanningClient(context.Ros, context.Robot, settings);
        Client.Connect();
        if (player == null) player = FindFirstObjectByType<JointTrajectoryPlayer>();
        if (player != null && context.Robot != null) player.SetRobot(context.Robot);

        base.OnRegister(context);

        if (autoQueryPlanners) InvokeRepeating(nameof(TryQueryPlanners), 0.5f, 1.0f);
    }

    protected override void OnUnregister(IEruptContext context)
    {
        CancelInvoke(nameof(TryQueryPlanners));
        StopPreview();
        Client?.Dispose();
        Client = null;
        base.OnUnregister(context);
    }

    private void TryQueryPlanners()
    {
        if (Client == null) return;
        Client.QueryPlanners(_ => CancelInvoke(nameof(TryQueryPlanners)));
    }

    // --- start / goal ------------------------------------------------------------

    /// <summary>Capture the robot's current pose as the plan's start state.</summary>
    public void SetStartFromRobot()
    {
        if (Client == null) return;
        startState = Client.CaptureRobotState();
    }

    /// <summary>Capture the robot's current pose as the goal (the user has posed it with the IK handle).</summary>
    public void SetGoalFromRobot()
    {
        if (Client == null) return;
        goalState = Client.CaptureRobotState();
    }

    public override InteractionRefusal SetGoal(ISelectable endEffector)
    {
        if (Client == null)
            return InteractionRefusal.Refuse("MoveIt is not connected.", Vector3.zero);
        if (!HasStart) SetStartFromRobot();
        SetGoalFromRobot();
        return InteractionRefusal.None;
    }

    public void ResetPlanningState()
    {
        startState = null;
        goalState = null;
    }

    // --- plan / preview / execute ------------------------------------------------

    public override void RequestPlan(PlanPreferences preferences, Action<PlanResult> done)
    {
        if (Client == null) { done?.Invoke(null); return; }
        if (!HasStart || !HasGoal)
        {
            PlanFailed?.Invoke("Both start and goal states must be set before planning.");
            done?.Invoke(null);
            return;
        }

        StopPreview();
        Client.RequestPlan(startState, goalState, preferences, result =>
        {
            if (result == null) { done?.Invoke(null); return; }
            PublishResult(result);
            done?.Invoke(result);
        }, message => PlanFailed?.Invoke(message));
    }

    public override void Preview(PlanResult plan)
    {
        if (plan == null || !plan.HasTrajectory) return;
        if (player == null) { Debug.LogWarning("[moveit] No JointTrajectoryPlayer to preview on.", this); return; }
        previewing = true;
        player.StartReplay(plan.Trajectory);
    }

    public override void StopPreview()
    {
        if (!previewing) return;
        previewing = false;
        if (player != null) player.StopReplay();
    }

    public override void Execute(PlanResult plan, Action<ExecutionStatus> status)
    {
        StopPreview();
        if (Client == null)
        {
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "MoveIt is not connected."));
            return;
        }
        // Mirror joint states so the Unity robot follows the real one during execution,
        // exactly as the legacy panel did.
        Client.SetMirroring(true);
        status?.Invoke(new ExecutionStatus(ExecutionPhase.Sending));
        if (!Client.Execute(plan))
        {
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Failed, "No planned trajectory to execute."));
            return;
        }
        // The controller topic gives no completion signal; this is fire-and-forget.
        status?.Invoke(new ExecutionStatus(ExecutionPhase.Executing, "Trajectory published to the controller."));
    }

    protected override PlanPreferences DefaultPreferences() => new PlanPreferences
    {
        PipelineId = settings.planningPipelineId,
        PlannerId = settings.defaultPlannerId,
        Attempts = settings.defaultNumPlanningAttempts,
        AllowedTimeSeconds = settings.defaultAllowedPlanningTime
    };
}
