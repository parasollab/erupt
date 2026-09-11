using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using RosMessageTypes.MoveitTaskConstructorMsgs;

/// <summary>
/// MoveIt Task Constructor as an ERUPT planning plugin. MTC plans on the ROS side from a
/// recorded task; here "plan" means "take the best solution", preview plays it on
/// <see cref="MtcSolutionPlayer"/>, execute sends it to /execute_task_solution through
/// <see cref="MtcClient"/>. The pick/place recorder is the Teach-mode entry (Part 5).
/// Depends on the MoveIt plugin for the planning scene.
/// </summary>
public class MtcPlugin : PlanningPlugin
{
    [Tooltip("Sibling components by default; found in the scene if empty.")]
    [SerializeField] private MtcClient client;
    [SerializeField] private MtcSolutionPlayer player;
    [SerializeField] private PickPlaceTaskRecorder recorder;
    [SerializeField] private PickPlaceActionClient pickPlace;

    private static readonly string[] Deps = { "moveit" };
    private readonly Dictionary<SolutionMsg, PlanResult> resultBySolution = new();
    private SolutionsTab tab;

    public override string Id => "mtc";
    public override string DisplayName => "MTC";
    public override IReadOnlyList<string> DependsOn => Deps;
    /// <summary>MTC plans from the recorded task; the end-effector goal verbs belong to MoveIt.</summary>
    public override bool AcceptsGoals => false;

    public MtcClient Client => client;
    public MtcSolutionPlayer Player => player;
    public PickPlaceTaskRecorder Recorder => recorder;
    public PickPlaceActionClient PickPlace => pickPlace;
    public SolutionsTab Tab => tab;
    public bool InTeachMode { get; private set; }
    public SolutionMsg SelectedSolution { get; private set; }

    public event Action SelectedSolutionChanged;
    public event Action TeachModeChanged;

    /// <summary>Solutions by ascending total cost.</summary>
    public IEnumerable<(SolutionMsg sol, double cost)> RankedSolutions =>
        client == null ? Enumerable.Empty<(SolutionMsg, double)>() :
        client.Solutions.Select(s => (s, (double)s.sub_trajectory.Sum(t => t.info.cost))).OrderBy(x => x.Item2);

    private void Awake()
    {
        if (client == null) client = GetComponent<MtcClient>();
        if (player == null) player = GetComponent<MtcSolutionPlayer>();
        if (recorder == null) recorder = GetComponent<PickPlaceTaskRecorder>();
        if (pickPlace == null) pickPlace = GetComponent<PickPlaceActionClient>();
    }

    protected override void OnRegister(IEruptContext context)
    {
        if (client == null) client = FindFirstObjectByType<MtcClient>(FindObjectsInactive.Include);
        if (player == null) player = FindFirstObjectByType<MtcSolutionPlayer>(FindObjectsInactive.Include);
        if (recorder == null) recorder = FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);
        if (client == null) Debug.LogError("[mtc] No MtcClient in the scene.", this);
        if (player != null && context.Robot != null) player.SetRobot(context.Robot);

        try { client?.Initialise(context.Ros); }
        catch (InvalidOperationException) { /* already started on RosBus.Instance, the same bus */ }

        if (client != null)
        {
            client.OnSolutionReceived += OnSolution;
            client.OnTaskReset += OnTaskReset;
        }
        InTeachMode = context.Modes != null && context.Modes.Is(AppMode.Teach);
        base.OnRegister(context);
    }

    protected override void OnUnregister(IEruptContext context)
    {
        if (client != null)
        {
            client.OnSolutionReceived -= OnSolution;
            client.OnTaskReset -= OnTaskReset;
        }
        tab?.Dispose();
        tab = null;
        StopPreview();
        resultBySolution.Clear();
        SelectedSolution = null;
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

    private void OnSolution(SolutionMsg solution)
    {
        var result = PublishResult(ToResult(solution));
        resultBySolution[solution] = result;
        // The first solution of a task is selected so its handle exists in the world.
        if (SelectedSolution == null) SelectSolution(solution);
    }

    private void OnTaskReset()
    {
        ClearResults();
        resultBySolution.Clear();
        SelectedSolution = null;
        SelectedSolutionChanged?.Invoke();
    }

    /// <summary>Make one solution the current plan: its handle appears at the end effector, others hide.</summary>
    public void SelectSolution(SolutionMsg solution)
    {
        if (solution == null || !resultBySolution.TryGetValue(solution, out var result)) return;
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
    }

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
        InteractionRefusal.Refuse("MTC plans from the recorded task; record a pick & place in Teach mode instead.",
            endEffector?.GameObject != null ? endEffector.GameObject.transform.position : Vector3.zero);

    /// <summary>"Plan" for MTC selects the best-cost solution.</summary>
    public override void RequestPlan(PlanPreferences preferences, Action<PlanResult> done)
    {
        var best = RankedSolutions.Select(x => x.sol).FirstOrDefault();
        if (best == null) { done?.Invoke(null); return; }
        SelectSolution(best);
        done?.Invoke(resultBySolution[best]);
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
            await client.ExecuteSolutionAsync(solution);
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
