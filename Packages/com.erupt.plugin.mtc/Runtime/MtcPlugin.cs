using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using RosMessageTypes.MoveitTaskConstructorMsgs;

/// <summary>
/// MoveIt Task Constructor as an ERUPT planning plugin. MTC plans on the ROS side from a
/// recorded task (<see cref="PickPlaceClient"/> → mtc_pick_place_server); here "plan" means
/// "take the best solution", preview plays it on <see cref="MtcSolutionPlayer"/>, execute
/// sends its id to /execute_solution. The pick/place recorder is the Teach-mode entry (Part 5).
/// Depends on the MoveIt plugin for the planning scene.
/// </summary>
public class MtcPlugin : PlanningPlugin
{
    [Tooltip("Sibling components by default; found in the scene if empty.")]
    [SerializeField] private MtcSolutionPlayer player;
    [SerializeField] private PickPlaceTaskRecorder recorder;
    [SerializeField] private PickPlaceClient pickPlace;

    private static readonly string[] Deps = { "moveit" };
    // Per task: both are dropped when a new plan starts, so a handle can never execute a dead id.
    private readonly Dictionary<uint, PlanResult> resultById = new();
    private readonly Dictionary<PlanResult, uint> idByResult = new();
    private SolutionsTab tab;

    public override string Id => "mtc";
    public override string DisplayName => "MTC";
    public override IReadOnlyList<string> DependsOn => Deps;
    /// <summary>MTC plans from the recorded task; the end-effector goal verbs belong to MoveIt.</summary>
    public override bool AcceptsGoals => false;

    public MtcSolutionPlayer Player => player;
    public PickPlaceTaskRecorder Recorder => recorder;
    public PickPlaceClient PickPlace => pickPlace;
    public SolutionsTab Tab => tab;
    public bool InTeachMode { get; private set; }

    /// <summary>The solution shown in the browser; null until its fetch returns.</summary>
    public SolutionMsg SelectedSolution { get; private set; }
    public uint? SelectedSolutionId { get; private set; }

    public event Action SelectedSolutionChanged;
    public event Action TeachModeChanged;

    private void Awake()
    {
        if (player == null) player = GetComponent<MtcSolutionPlayer>();
        if (recorder == null) recorder = GetComponent<PickPlaceTaskRecorder>();
        if (pickPlace == null) pickPlace = GetComponent<PickPlaceClient>();
    }

    protected override void OnRegister(IEruptContext context)
    {
        if (pickPlace == null) pickPlace = FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
        if (player == null) player = FindFirstObjectByType<MtcSolutionPlayer>(FindObjectsInactive.Include);
        if (recorder == null) recorder = FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);
        if (pickPlace == null) Debug.LogError("[mtc] No PickPlaceClient in the scene.", this);
        if (player != null && context.Robot != null) player.SetRobot(context.Robot);

        try { pickPlace?.Initialise(context.Ros); }
        catch (InvalidOperationException) { /* already started on RosBus.Instance, the same bus */ }

        if (pickPlace != null)
        {
            pickPlace.OnSolutionIdsChanged += OnSolutionIds;
            pickPlace.OnTaskReset += OnTaskReset;
        }
        InTeachMode = context.Modes != null && context.Modes.Is(AppMode.Teach);
        base.OnRegister(context);
    }

    protected override void OnUnregister(IEruptContext context)
    {
        if (pickPlace != null)
        {
            pickPlace.OnSolutionIdsChanged -= OnSolutionIds;
            pickPlace.OnTaskReset -= OnTaskReset;
        }
        tab?.Dispose();
        tab = null;
        StopPreview();
        resultById.Clear();
        idByResult.Clear();
        SelectedSolution = null;
        SelectedSolutionId = null;
        base.OnUnregister(context);
    }

    protected override void OnModeChanged(AppMode mode)
    {
        bool teach = mode == AppMode.Teach;
        if (teach == InTeachMode) return;
        InTeachMode = teach;
        if (!teach && recorder != null && recorder.IsRecording) recorder.StopRecording();
        TeachModeChanged?.Invoke();
    }

    protected override void BuildSettingsTab(RectTransform content)
    {
        if (content == null) return;
        tab = new SolutionsTab(this, content);
    }

    // --- solutions ----------------------------------------------------------------

    private void OnSolutionIds()
    {
        // The first solution of a task is selected so its handle exists in the world.
        if (SelectedSolutionId == null && pickPlace.SolutionIds.Count > 0)
            SelectSolution(pickPlace.SolutionIds[0]);
    }

    private void OnTaskReset()
    {
        StopPreview();
        ClearResults();
        resultById.Clear();
        idByResult.Clear();
        SelectedSolution = null;
        SelectedSolutionId = null;
        SelectedSolutionChanged?.Invoke();
    }

    /// <summary>
    /// Make one solution the current plan: fetched on selection (a /get_solution round trip
    /// is 20–40 ms), then its handle appears at the end effector and the others hide.
    /// </summary>
    public void SelectSolution(uint solutionId, Action<PlanResult> done = null)
    {
        if (pickPlace == null) { done?.Invoke(null); return; }
        SelectedSolutionId = solutionId;
        SelectedSolution = null;
        SelectedSolutionChanged?.Invoke();

        pickPlace.FetchSolution(solutionId, solution =>
        {
            // A later selection wins over a slower fetch.
            if (SelectedSolutionId != solutionId) { done?.Invoke(null); return; }
            if (!resultById.TryGetValue(solutionId, out var result))
            {
                result = PublishResult(ToResult(solutionId, solution));
                resultById[solutionId] = result;
                idByResult[result] = solutionId;
            }
            SelectedSolution = solution;
            Transform ee = Context?.Robot?.EndEffector;
            PlaceHandle(result, ee != null ? ee.position : transform.position, ee);
            ShowOnlyHandle(result);
            if (Context?.Selection != null)
            {
                var selectable = Results.FirstOrDefault(r => r != null && r.Result == result);
                if (selectable != null) Context.Selection.Select(selectable);
            }
            SelectedSolutionChanged?.Invoke();
            done?.Invoke(result);
        },
        _ =>
        {
            if (SelectedSolutionId == solutionId)
            {
                SelectedSolutionId = null;
                SelectedSolutionChanged?.Invoke();
            }
            done?.Invoke(null);
        });
    }

    private PlanResult ToResult(uint solutionId, SolutionMsg solution)
    {
        var first = solution.sub_trajectory?.FirstOrDefault(s => s.trajectory?.joint_trajectory?.points?.Length > 0);
        return new PlanResult
        {
            Trajectory = first?.trajectory?.joint_trajectory,
            PlannerPayload = solution,
            PlannerId = Id,
            Label = $"MTC solution {solutionId}"
        };
    }

    /// <summary>MTC goals come from the recorded task; set-goal has nothing to set here.</summary>
    public override InteractionRefusal SetGoal(ISelectable endEffector) =>
        InteractionRefusal.Refuse("MTC plans from the recorded task; record a pick & place in Teach mode instead.",
            endEffector?.GameObject != null ? endEffector.GameObject.transform.position : Vector3.zero);

    /// <summary>"Plan" for MTC selects the best-cost solution (the server lists them by ascending cost).</summary>
    public override void RequestPlan(PlanPreferences preferences, Action<PlanResult> done)
    {
        if (pickPlace == null || pickPlace.SolutionIds.Count == 0) { done?.Invoke(null); return; }
        SelectSolution(pickPlace.SolutionIds[0], done);
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
        if (plan == null || pickPlace == null || !idByResult.TryGetValue(plan, out uint solutionId))
        {
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable,
                "This solution is not part of the current plan. Plan again."));
            return;
        }
        if (pickPlace.Busy)
        {
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "The server is busy with another goal."));
            return;
        }
        StopPreview();
        status?.Invoke(new ExecutionStatus(ExecutionPhase.Sending));
        try
        {
            await pickPlace.ExecuteAsync(solutionId);
            status?.Invoke(new ExecutionStatus(Map(pickPlace.LastOutcome), pickPlace.LastStatus));
        }
        catch (Exception e)
        {
            status?.Invoke(new ExecutionStatus(Map(pickPlace.LastOutcome), e.Message));
        }
    }

    private static ExecutionPhase Map(PickPlaceOutcome outcome)
    {
        switch (outcome)
        {
            case PickPlaceOutcome.Succeeded: return ExecutionPhase.Succeeded;
            case PickPlaceOutcome.Canceled: return ExecutionPhase.Canceled;
            case PickPlaceOutcome.Aborted: return ExecutionPhase.Aborted;
            case PickPlaceOutcome.Rejected:
            case PickPlaceOutcome.Unavailable: return ExecutionPhase.Unavailable;
            default: return ExecutionPhase.Failed;
        }
    }
}
