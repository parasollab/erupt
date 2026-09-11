using System;
using System.Linq;
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
    [Tooltip("Start/goal ghosts; the goal ghost hosts the plan's trajectory handle. Found in the scene if empty.")]
    [SerializeField] private SpawnGhosts ghosts;

    private PlannerSettingsTab tab;

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
    /// <summary>Fires when the start or goal state is set or reset.</summary>
    public event Action GoalChanged;

    protected override void OnRegister(IEruptContext context)
    {
        Client = new MoveItPlanningClient(context.Ros, context.Robot, settings);
        Client.Connect();
        if (player == null) player = FindFirstObjectByType<JointTrajectoryPlayer>();
        if (player != null && context.Robot != null) player.SetRobot(context.Robot);
        if (ghosts == null) ghosts = FindFirstObjectByType<SpawnGhosts>();

        base.OnRegister(context);

        // The legacy panel let the user pose the robot, set start, pose again, set goal.
        // set-goal alone cannot express that, so start gets its own verb (plugin origin).
        if (context.Ui != null && !context.Ui.Verbs.Contains(SelectionKind.EndEffector, "set-start"))
            context.Ui.RegisterVerb(SelectionKind.EndEffector, "set-start", "Set Start", _ => SetStartFromRobot(), Id);

        if (autoQueryPlanners) InvokeRepeating(nameof(TryQueryPlanners), 0.5f, 1.0f);
    }

    protected override void OnUnregister(IEruptContext context)
    {
        CancelInvoke(nameof(TryQueryPlanners));
        StopPreview();
        tab?.Dispose();
        tab = null;
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
        if (ghosts != null) { if (ghosts.StartGhost == null) ghosts.SpawnStartGhost(); else ghosts.UpdateStartGhost(); }
        GoalChanged?.Invoke();
    }

    /// <summary>Capture the robot's current pose as the goal (the user has posed it with the IK handle).</summary>
    public void SetGoalFromRobot()
    {
        if (Client == null) return;
        goalState = Client.CaptureRobotState();
        if (ghosts != null) { if (ghosts.GoalGhost == null) ghosts.SpawnGoalGhost(); else ghosts.UpdateGoalGhost(); }
        GoalChanged?.Invoke();
    }

    /// <summary>Capture the goal only; the start has its own verb (set-start).</summary>
    public override InteractionRefusal SetGoal(ISelectable endEffector)
    {
        if (Client == null)
            return InteractionRefusal.Refuse("MoveIt is not connected.", Vector3.zero);
        SetGoalFromRobot();
        return InteractionRefusal.None;
    }

    public void ResetPlanningState()
    {
        startState = null;
        goalState = null;
        ghosts?.ClearGhosts();
        GoalChanged?.Invoke();
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
            PlaceOnGoalGhost(result);
            Preview(result);
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

    protected override PlanPreferences DefaultPreferences() => tab != null ? tab.Preferences : new PlanPreferences
    {
        PipelineId = settings.planningPipelineId,
        PlannerId = settings.defaultPlannerId,
        Attempts = settings.defaultNumPlanningAttempts,
        AllowedTimeSeconds = settings.defaultAllowedPlanningTime
    };

    protected override void BuildSettingsTab(RectTransform content)
    {
        if (content == null) return;   // a headless UI host (tests) passes no content
        tab = new PlannerSettingsTab(this, content);
    }

    public PlannerSettingsTab Tab => tab;

    // The goal ghost is where the user looks for the plan, so the trajectory handle sits at
    // its end effector; without a ghost it sits at the real end effector.
    private void PlaceOnGoalGhost(PlanResult result)
    {
        Transform ee = Context?.Robot?.EndEffector;
        Transform anchor = null;
        if (ghosts != null && ghosts.GoalGhost != null && ee != null)
        {
            var match = ghosts.GoalGhost.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == ee.name);
            anchor = match != null ? match : ghosts.GoalGhost.transform;
        }
        Vector3 position = anchor != null ? anchor.position : ee != null ? ee.position : transform.position;
        PlaceHandle(result, position, anchor);
    }
}
