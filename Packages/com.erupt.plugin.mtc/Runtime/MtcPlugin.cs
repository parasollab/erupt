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
/// <remarks>
/// Preview has a scope (<see cref="PreviewStageId"/>): the whole solution, or one stage's
/// steps of it, so a container stage like "pick object" can be watched on its own. A stage's
/// partial or failed attempts (<see cref="AttemptsOf"/>) can be previewed from their own
/// start scene with <see cref="PreviewStageSolution"/>; those are never executable and never
/// become the selected solution.
/// </remarks>
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
    private readonly List<uint> previewableStages = new();
    private SolutionsTab tab;
    // Partial / failed stage solution being fetched for a preview; a later request or a task
    // reset replaces it, so a slower fetch is dropped. Never feeds SelectedSolutionId.
    private uint? pendingStageSolutionId;

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

    /// <summary>Stage whose part of the selected solution the preview verb plays; null = the whole solution.</summary>
    public uint? PreviewStageId { get; private set; }
    /// <summary>Stages with at least one step in the selected solution, in stage-tree order.</summary>
    public IReadOnlyList<uint> PreviewableStages => previewableStages;
    /// <summary>What the last preview request led to (shown on the tab), or null.</summary>
    public string PreviewStatus { get; private set; }

    public event Action SelectedSolutionChanged;
    public event Action TeachModeChanged;
    public event Action PreviewScopeChanged;
    public event Action PreviewStatusChanged;

    /// <summary>A stage's solution that is not executable: partial (the stage solved) or failed.</summary>
    public readonly struct StageAttempt
    {
        public readonly uint Id;
        public readonly bool Failed;
        public StageAttempt(uint id, bool failed) { Id = id; Failed = failed; }
    }

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
        if (player != null)
        {
            if (context.Robot != null) player.SetRobot(context.Robot);
            player.OnProblem += OnPreviewProblem;
        }

        try { pickPlace?.Initialise(context.Ros); }
        catch (InvalidOperationException) { /* already started on RosBus.Instance, the same bus */ }

        if (pickPlace != null)
        {
            pickPlace.OnSolutionIdsChanged += OnSolutionIds;
            pickPlace.OnTaskReset += OnTaskReset;
            pickPlace.OnDescriptionChanged += RefreshPreviewScope;
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
            pickPlace.OnDescriptionChanged -= RefreshPreviewScope;
        }
        if (player != null) player.OnProblem -= OnPreviewProblem;
        tab?.Dispose();
        tab = null;
        StopPreview();
        resultById.Clear();
        idByResult.Clear();
        SelectedSolution = null;
        SelectedSolutionId = null;
        pendingStageSolutionId = null;
        PreviewStageId = null;
        PreviewStatus = null;
        previewableStages.Clear();
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
        pendingStageSolutionId = null; // drops any partial fetch still in flight
        PreviewStatus = null;
        RefreshPreviewScope();
        SelectedSolutionChanged?.Invoke();
        PreviewStatusChanged?.Invoke();
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
            RefreshPreviewScope();
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

    // --- preview --------------------------------------------------------------------

    /// <summary>Preview the plan, or only the scoped stage's steps of it (<see cref="PreviewStageId"/>).</summary>
    public override void Preview(PlanResult plan)
    {
        if (plan?.PlannerPayload is not SolutionMsg solution || player == null) return;
        string what = idByResult.TryGetValue(plan, out uint id) ? $"solution {id}" : plan.Label;
        if (PreviewStageId is uint stage && TryGetStageSteps(solution, stage, out int first, out int last))
        {
            string span = first == last ? $"step {first + 1}" : $"steps {first + 1}-{last + 1}";
            SetPreviewStatus($"Previewing {StageName(stage)} ({span}) of {what}...");
            player.PlaySolution(solution, first, last);
        }
        else
        {
            SetPreviewStatus($"Previewing {what}...");
            player.PlaySolution(solution);
        }
    }

    public override void StopPreview()
    {
        if (player != null) player.Stop();
    }

    /// <summary>Scope the preview verb to one stage of the selected solution, or null for all of it.</summary>
    public void SetPreviewStage(uint? stageId)
    {
        if (stageId != null && !previewableStages.Contains(stageId.Value)) stageId = null;
        if (PreviewStageId == stageId) return;
        PreviewStageId = stageId;
        PreviewScopeChanged?.Invoke();
    }

    /// <summary>
    /// First and last step (inclusive) of the selected solution that belong to a stage. A
    /// container stage (e.g. "pick object") covers the steps of all the stages nested under it.
    /// </summary>
    public bool TryGetStageSteps(uint stageId, out int first, out int last) =>
        TryGetStageSteps(SelectedSolution, stageId, out first, out last);

    private bool TryGetStageSteps(SolutionMsg solution, uint stageId, out int first, out int last)
    {
        first = last = -1;
        var steps = solution?.sub_trajectory;
        if (steps == null) return false;

        // The stage and everything nested under it.
        var family = new HashSet<uint> { stageId };
        var stages = pickPlace?.Description?.stages;
        if (stages != null)
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var stage in stages)
                    if (stage.id != stage.parent_id && family.Contains(stage.parent_id) && family.Add(stage.id))
                        grew = true;
            }

        for (int i = 0; i < steps.Length; i++)
        {
            if (!family.Contains(steps[i].info.stage_id)) continue;
            if (first < 0) first = i;
            last = i;
        }
        return first >= 0;
    }

    // Stages in tree order (depth first, as the tab draws them) that own steps of the
    // selected solution; without a description, the steps' own stage ids in order.
    private void RefreshPreviewScope()
    {
        previewableStages.Clear();
        var steps = SelectedSolution?.sub_trajectory;
        if (steps != null && steps.Length > 0)
        {
            var stages = pickPlace?.Description?.stages;
            if (stages != null && stages.Length > 0)
            {
                var ids = new HashSet<uint>(stages.Select(s => s.id));
                var children = stages.ToLookup(s => s.parent_id);
                foreach (var root in stages.Where(s => s.id == s.parent_id || !ids.Contains(s.parent_id)))
                    Visit(root);

                void Visit(StageDescriptionMsg stage)
                {
                    if (TryGetStageSteps(SelectedSolution, stage.id, out _, out _)) previewableStages.Add(stage.id);
                    foreach (var child in children[stage.id])
                        if (child.id != stage.id) Visit(child);
                }
            }
            else
            {
                foreach (var step in steps)
                    if (!previewableStages.Contains(step.info.stage_id)) previewableStages.Add(step.info.stage_id);
            }
        }
        // A scope the new solution has no steps for falls back to the whole solution.
        if (PreviewStageId != null && !previewableStages.Contains(PreviewStageId.Value))
        {
            PreviewStageId = null;
            PreviewScopeChanged?.Invoke();
        }
        else PreviewScopeChanged?.Invoke();
    }

    /// <summary>
    /// A stage's solutions that are not executable: partial ones (listed as solved for the
    /// stage but not for the task) and failed ones, from the statistics.
    /// </summary>
    public IReadOnlyList<StageAttempt> AttemptsOf(uint stageId)
    {
        var attempts = new List<StageAttempt>();
        var stats = pickPlace?.Statistics?.stages?.FirstOrDefault(s => s.id == stageId);
        if (stats == null) return attempts;
        var complete = pickPlace.SolutionIds;
        foreach (uint id in stats.solved ?? Array.Empty<uint>())
            if (!complete.Contains(id)) attempts.Add(new StageAttempt(id, false));
        foreach (uint id in stats.failed ?? Array.Empty<uint>())
            attempts.Add(new StageAttempt(id, true));
        return attempts;
    }

    /// <summary>
    /// Fetch a stage's partial or failed solution with its start scene and preview it from
    /// there; a failed one also reports the planner's reason. These are never executable, so
    /// the selected (executable) solution is untouched.
    /// </summary>
    public void PreviewStageSolution(uint solutionId, bool failed = false)
    {
        if (pickPlace == null) return;
        if (pickPlace.Phase == PickPlacePhase.Executing)
        {
            SetPreviewStatus("Cannot preview while a solution is executing.");
            return;
        }

        string kind = failed ? "failed solution" : "stage solution";
        pendingStageSolutionId = solutionId;
        SetPreviewStatus($"Fetching {kind} {solutionId}...");

        pickPlace.FetchSolution(solutionId,
            solution =>
            {
                // A later request (or a task reset) wins over a slower fetch.
                if (pendingStageSolutionId != solutionId) return;
                pendingStageSolutionId = null;

                string comment = FirstComment(solution);
                string reason = failed
                    ? $"Solution {solutionId} failed: {(string.IsNullOrEmpty(comment) ? "no reason given by the planner" : comment)}"
                    : null;

                bool hasSteps = solution.sub_trajectory != null && solution.sub_trajectory.Length > 0;
                if (!hasSteps || player == null || pickPlace.Phase == PickPlacePhase.Executing)
                {
                    SetPreviewStatus(reason ?? (hasSteps
                        ? "Preview unavailable right now."
                        : $"Stage solution {solutionId} has no motion to preview."));
                    return;
                }

                SetPreviewStatus(failed ? reason : $"Previewing stage solution {solutionId}...");
                player.PlaySolution(solution, useStartScene: true);
            },
            message =>
            {
                if (pendingStageSolutionId != solutionId) return;
                pendingStageSolutionId = null;
                SetPreviewStatus($"Solution {solutionId} unavailable: {message}");
            },
            includeStartScene: true);
    }

    private static string FirstComment(SolutionMsg solution)
    {
        foreach (var step in solution?.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>())
            if (!string.IsNullOrEmpty(step?.info?.comment)) return step.info.comment;
        // A failure with no motion at all only carries its reason on the sub-solutions.
        foreach (var sub in solution?.sub_solution ?? Array.Empty<SubSolutionMsg>())
            if (!string.IsNullOrEmpty(sub?.info?.comment)) return sub.info.comment;
        return null;
    }

    private string StageName(uint stageId) => pickPlace?.StageName(stageId) ?? $"stage {stageId}";

    private void OnPreviewProblem(string problem) => SetPreviewStatus("Cannot preview: " + problem);

    private void SetPreviewStatus(string text)
    {
        PreviewStatus = text;
        PreviewStatusChanged?.Invoke();
    }

    // --- execute --------------------------------------------------------------------

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
