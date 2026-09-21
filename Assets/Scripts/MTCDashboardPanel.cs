using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.StudyInterfaces;

/// <summary>
/// MTC dashboard for mtc_pick_place_server, driven by <see cref="PickPlaceClient"/>.
/// Plan: the pick/place recorder sends a plan-only goal; progress and cancel.
/// Stages: stage tree with per-stage counts, the running stage highlighted during execution.
/// Solutions: the current task's solution ids (server order = ascending cost); tapping one
/// fetches it (/get_solution) and lists its steps; Execute sends its id to /execute_solution
/// and the steps tick off as feedback arrives. A new plan clears everything: the server
/// invalidates every earlier task and solution id.
/// </summary>
public class MTCDashboardPanel : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private PickPlaceTaskRecorder pickPlaceRecorder;
    [Tooltip("Found in the scene when empty.")]
    [SerializeField] private PickPlaceClient pickPlaceAction;
    [SerializeField] private MTCTrajectoryPlayer trajectoryPlayer;

    private static readonly Color RowColor = new(0.08f, 0.08f, 0.08f);
    private static readonly Color SelectedColor = new(0.18f, 0.32f, 0.18f);
    private static readonly Color ActiveColor = new(0.10f, 0.40f, 0.10f);
    private static readonly Color StageSelectedColor = new(0.15f, 0.20f, 0.30f);
    private static readonly Color DoneColor = new(0.55f, 0.85f, 0.55f);
    private static readonly Color MutedColor = new(0.6f, 0.6f, 0.6f);

    private VisualElement root;
    private VisualElement panelPlan, panelStages, panelSolutions;

    private Button recordButton, planCancelButton;
    private Label planObjectIdLabel, planStatusLabel, taskIdLabel;

    private VisualElement stageTreeContainer, stageSolutionsContainer;

    private VisualElement solutionListContainer, breakdownContainer;
    private Button executeButton, cancelExecuteButton;
    private ProgressBar executionProgress;
    private Label execStatusLabel;

    private bool hasSelectedStage;
    private uint selectedStageId;

    /// <summary>Solution chosen in the browser; its message arrives with the /get_solution response.</summary>
    public uint? SelectedSolutionId { get; private set; }
    public SolutionMsg SelectedSolution { get; private set; }
    public VisualElement Root => root;

    private PickPlaceClient Client => pickPlaceAction;
    private bool Busy => Client != null && Client.Busy;

    private void OnEnable()
    {
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        root = uiDocument?.rootVisualElement;
        if (root == null) { Debug.LogError("[MTCDashboardPanel] No root visual element."); return; }

        if (pickPlaceAction == null) pickPlaceAction = FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
        if (pickPlaceRecorder == null) pickPlaceRecorder = FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);

        BindUI();
        ShowTab(panelPlan);

        if (Client != null)
        {
            Client.OnStatus += OnStatus;
            Client.OnPhaseChanged += RefreshControls;
            Client.OnTaskReset += OnTaskReset;
            Client.OnDescriptionChanged += RefreshStages;
            Client.OnStatisticsChanged += RefreshStages;
            Client.OnSolutionIdsChanged += RefreshSolutionList;
            Client.OnExecutionFeedback += OnExecutionFeedback;
            if (!string.IsNullOrEmpty(Client.LastStatus)) OnStatus(Client.LastStatus);
        }
        else
        {
            Debug.LogWarning("[MTCDashboardPanel] No PickPlaceClient in the scene.");
        }

        if (pickPlaceRecorder != null)
        {
            pickPlaceRecorder.OnRecordingComplete -= OnPickPlaceRecorded;
            pickPlaceRecorder.OnRecordingComplete += OnPickPlaceRecorded;
            pickPlaceRecorder.OnRecordingDiscarded -= OnPickPlaceDiscarded;
            pickPlaceRecorder.OnRecordingDiscarded += OnPickPlaceDiscarded;
        }

        RefreshAll();
    }

    private void OnDisable()
    {
        if (Client != null)
        {
            Client.OnStatus -= OnStatus;
            Client.OnPhaseChanged -= RefreshControls;
            Client.OnTaskReset -= OnTaskReset;
            Client.OnDescriptionChanged -= RefreshStages;
            Client.OnStatisticsChanged -= RefreshStages;
            Client.OnSolutionIdsChanged -= RefreshSolutionList;
            Client.OnExecutionFeedback -= OnExecutionFeedback;
        }

        if (pickPlaceRecorder != null)
        {
            pickPlaceRecorder.OnRecordingComplete -= OnPickPlaceRecorded;
            pickPlaceRecorder.OnRecordingDiscarded -= OnPickPlaceDiscarded;
        }
    }

    private void BindUI()
    {
        taskIdLabel = root.Q<Label>("mtcTaskIdLabel");
        panelPlan = root.Q<VisualElement>("mtcPanelPlan");
        panelStages = root.Q<VisualElement>("mtcPanelStages");
        panelSolutions = root.Q<VisualElement>("mtcPanelSolutions");

        BindButton("mtcTabPlan", () => ShowTab(panelPlan));
        BindButton("mtcTabStages", () => ShowTab(panelStages));
        BindButton("mtcTabSolutions", () => ShowTab(panelSolutions));

        planObjectIdLabel = root.Q<Label>("mtcPlanObjectIdLabel");
        planStatusLabel = root.Q<Label>("mtcPlanStatusLabel");
        recordButton = root.Q<Button>("mtcPlanRecordButton");
        if (recordButton != null) recordButton.clicked += OnRecordClicked;
        planCancelButton = root.Q<Button>("mtcPlanCancelButton");
        if (planCancelButton != null) planCancelButton.clicked += Cancel;

        stageTreeContainer = root.Q<VisualElement>("mtcStageTreeContainer");
        stageSolutionsContainer = root.Q<VisualElement>("mtcStageSolutionsContainer");
        solutionListContainer = root.Q<VisualElement>("mtcSolutionListContainer");
        breakdownContainer = root.Q<VisualElement>("mtcBreakdownContainer");
        executeButton = root.Q<Button>("mtcExecuteButton");
        cancelExecuteButton = root.Q<Button>("mtcCancelExecuteButton");
        executionProgress = root.Q<ProgressBar>("mtcExecProgress");
        execStatusLabel = root.Q<Label>("mtcExecStatusLabel");

        BindButton("mtcPreviewButton", OnPreviewClicked);
        BindButton("mtcStopPreviewButton", () => trajectoryPlayer?.Stop());
        if (executeButton != null) executeButton.clicked += ExecuteSelected;
        if (cancelExecuteButton != null) cancelExecuteButton.clicked += Cancel;
    }

    private void BindButton(string name, Action onClick)
    {
        var button = root.Q<Button>(name);
        if (button == null) { Debug.LogWarning($"[MTCDashboardPanel] Missing UI element '{name}'."); return; }
        button.clicked += onClick;
    }

    private void ShowTab(VisualElement active)
    {
        foreach (var p in new[] { panelPlan, panelStages, panelSolutions })
            if (p != null) p.style.display = p == active ? DisplayStyle.Flex : DisplayStyle.None;
    }

    // The solution list refreshes the controls, which refresh the stages, breakdown and progress.
    private void RefreshAll() => RefreshSolutionList();

    // ─── Plan tab ─────────────────────────────────────────────────────────────

    private void OnRecordClicked()
    {
        if (pickPlaceRecorder == null || Busy) return;

        if (!pickPlaceRecorder.IsRecording)
        {
            pickPlaceRecorder.StartRecording();
            if (planStatusLabel != null) planStatusLabel.text = "Grab and place the object...";
        }
        else
        {
            // StopRecording raises OnRecordingComplete or OnRecordingDiscarded, which set the status.
            pickPlaceRecorder.StopRecording();
        }
        RefreshControls();
    }

    private void OnPickPlaceRecorded(string objectId)
    {
        if (planObjectIdLabel != null) planObjectIdLabel.text = objectId;
        RefreshControls();
    }

    private void OnPickPlaceDiscarded(string reason)
    {
        if (planStatusLabel != null) planStatusLabel.text = $"Not sent: {reason}";
        RefreshControls();
    }

    private void OnStatus(string status)
    {
        if (planStatusLabel != null) planStatusLabel.text = status;
        if (execStatusLabel != null) execStatusLabel.text = status;
        RefreshControls();
    }

    /// <summary>Cancel the active goal, planning or executing. It ends as CANCELED, not as an error.</summary>
    public async void Cancel()
    {
        if (!Busy) return;
        try { await Client.CancelAsync(); }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (execStatusLabel != null) execStatusLabel.text = $"Cancel failed: {exception.Message}";
        }
    }

    // Button states follow the client: one task at a time, so nothing new starts while a goal is active.
    private void RefreshControls()
    {
        bool busy = Busy;
        bool recording = pickPlaceRecorder != null && pickPlaceRecorder.IsRecording;
        if (recordButton != null)
        {
            recordButton.SetEnabled(pickPlaceRecorder != null && !busy);
            recordButton.text = pickPlaceRecorder == null ? "No recorder" :
                busy ? "Busy..." :
                recording ? "Stop Recording" : "Start Recording";
            recordButton.style.color = recording ? new StyleColor(Color.yellow) : new StyleColor(StyleKeyword.Null);
        }
        planCancelButton?.SetEnabled(busy);
        if (planCancelButton != null)
            planCancelButton.text = Client?.Phase == PickPlacePhase.Executing ? "Cancel Execution" : "Cancel Planning";
        cancelExecuteButton?.SetEnabled(busy);
        executeButton?.SetEnabled(!busy && SelectedSolutionId != null && Client != null && Client.ExecutionAvailable);
        RefreshProgress();
        RefreshStages();
        RefreshBreakdown();
    }

    // ─── Stages tab ───────────────────────────────────────────────────────────

    private void RefreshTask()
    {
        if (taskIdLabel == null) return;
        string id = Client?.TaskId;
        taskIdLabel.text = string.IsNullOrEmpty(id) ? "Task: ---" : $"Task: {id}";
    }

    /// <summary>Stage running now: the one after the last finished sub-trajectory, or null when idle.</summary>
    private uint? ActiveStageId()
    {
        if (Client == null || Client.Phase != PickPlacePhase.Executing) return null;
        var steps = SelectedSolution?.sub_trajectory;
        if (steps == null || steps.Length == 0 || Client.ExecutingSolutionId != SelectedSolutionId)
            return Client.LastExecutionFeedback?.stage_id;
        int current = ActiveStep(steps.Length);
        return current < steps.Length ? steps[current].info.stage_id : (uint?)null;
    }

    // Feedback reports the step that just finished, so the running one is the next.
    private int ActiveStep(int stepCount) =>
        Client?.LastExecutionFeedback == null ? 0 : (int)Math.Min(Client.LastExecutionFeedback.sub_id + 1, (uint)stepCount);

    private void RefreshStages()
    {
        RefreshTask();
        if (stageTreeContainer == null) return;
        stageTreeContainer.Clear();
        var desc = Client?.Description;
        if (desc?.stages == null || desc.stages.Length == 0) { stageSolutionsContainer?.Clear(); return; }

        var statsById = new Dictionary<uint, StageStatisticsMsg>();
        if (Client.Statistics?.stages != null)
            foreach (var s in Client.Statistics.stages) statsById[s.id] = s;

        var ids = new HashSet<uint>(desc.stages.Select(s => s.id));
        var children = new Dictionary<uint, List<StageDescriptionMsg>>();
        var roots = new List<StageDescriptionMsg>();
        foreach (var stage in desc.stages)
        {
            // Stage 0 (the Task wrapper) is never published, so the root container's parent is absent.
            if (stage.id == stage.parent_id || !ids.Contains(stage.parent_id)) { roots.Add(stage); continue; }
            if (!children.TryGetValue(stage.parent_id, out var list))
                children[stage.parent_id] = list = new List<StageDescriptionMsg>();
            list.Add(stage);
        }

        uint? active = ActiveStageId();
        foreach (var stage in roots)
            AddStageRow(stageTreeContainer, stage, children, statsById, 0, active);
        RefreshStageSolutions();
    }

    private void AddStageRow(VisualElement container, StageDescriptionMsg stage,
        Dictionary<uint, List<StageDescriptionMsg>> children, Dictionary<uint, StageStatisticsMsg> statsById,
        int depth, uint? active)
    {
        statsById.TryGetValue(stage.id, out var s);
        bool isActive = active == stage.id;

        var row = Row(isActive ? ActiveColor : hasSelectedStage && selectedStageId == stage.id ? StageSelectedColor : Color.clear);
        row.name = $"mtcStage{stage.id}";
        row.style.paddingLeft = depth * 12 + 4;

        var nameLabel = Text((isActive ? "▶ " : "") + stage.name, Color.white, 12);
        nameLabel.style.flexGrow = 1;
        if (isActive) nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        var statsLabel = Text($"✓{s?.solved?.Length ?? 0}  ✗{s?.num_failed ?? 0}  {s?.total_compute_time ?? 0:F1}s", new Color(0.65f, 0.65f, 0.65f), 11);
        statsLabel.style.unityTextAlign = TextAnchor.MiddleRight;
        row.Add(nameLabel);
        row.Add(statsLabel);

        uint capturedId = stage.id;
        row.RegisterCallback<PointerDownEvent>(_ => SelectStage(capturedId));
        container.Add(row);

        if (children.TryGetValue(stage.id, out var childList))
            foreach (var child in childList)
                AddStageRow(container, child, children, statsById, depth + 1, active);
    }

    public void SelectStage(uint stageId)
    {
        hasSelectedStage = true;
        selectedStageId = stageId;
        RefreshStages();
    }

    // The selected stage's solved[] ids. Only the root container's are complete, executable
    // solutions that /get_solution serves; the others are partial and shown for reference.
    private void RefreshStageSolutions()
    {
        if (stageSolutionsContainer == null) return;
        stageSolutionsContainer.Clear();
        if (!hasSelectedStage || Client == null) return;

        var solved = Client.Statistics?.stages?.FirstOrDefault(s => s.id == selectedStageId)?.solved;
        if (solved == null || solved.Length == 0)
        {
            stageSolutionsContainer.Add(Text("No solutions for this stage yet.", MutedColor, 11));
            return;
        }

        bool complete = selectedStageId == PickPlaceClient.RootStageId;
        int rank = 1;
        foreach (uint id in solved)
        {
            uint capturedId = id;
            var row = Row(Color.clear);
            row.Add(RankLabel(rank++));
            var idLabel = Text($"solution id {id}", complete ? Color.white : MutedColor, 11);
            idLabel.style.flexGrow = 1;
            row.Add(idLabel);
            row.Add(Text(complete ? "tap to open" : "partial", complete ? new Color(0.55f, 0.75f, 1f) : MutedColor, 10));
            if (complete)
                row.RegisterCallback<PointerDownEvent>(_ => { SelectSolution(capturedId); ShowTab(panelSolutions); });
            stageSolutionsContainer.Add(row);
        }
    }

    // ─── Solutions tab ────────────────────────────────────────────────────────

    private void OnTaskReset()
    {
        trajectoryPlayer?.Stop();
        SelectedSolutionId = null;
        SelectedSolution = null;
        hasSelectedStage = false;
        stageSolutionsContainer?.Clear();
        RefreshAll();
    }

    private void RefreshSolutionList()
    {
        if (solutionListContainer == null) return;
        solutionListContainer.Clear();
        var ids = Client?.SolutionIds ?? Array.Empty<uint>();

        int rank = 1;
        foreach (uint id in ids)
        {
            uint capturedId = id;
            bool isSelected = SelectedSolutionId == id;
            var row = Row(isSelected ? SelectedColor : RowColor);
            row.name = $"mtcSolution{id}";
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;
            row.Add(RankLabel(rank++));

            var idLabel = Text($"solution {id}", Color.white, 12);
            idLabel.style.flexGrow = 1;
            row.Add(idLabel);

            string detail = isSelected && SelectedSolution != null
                ? $"cost {SelectedSolution.sub_trajectory.Sum(t => t.info.cost):F3}  {SelectedSolution.sub_trajectory.Length} steps"
                : isSelected ? "fetching..." : "tap to show";
            row.Add(Text(detail, new Color(0.55f, 0.75f, 1f), 11));

            row.RegisterCallback<PointerDownEvent>(_ => SelectSolution(capturedId));
            solutionListContainer.Add(row);
        }
        RefreshControls();
    }

    /// <summary>Show one of the current task's solutions: fetched with /get_solution (20–40 ms), cached per task.</summary>
    public void SelectSolution(uint solutionId)
    {
        if (Client == null) return;
        SelectedSolutionId = solutionId;
        SelectedSolution = null;
        RefreshSolutionList();

        Client.FetchSolution(solutionId,
            solution =>
            {
                // A later selection wins over a slower fetch.
                if (SelectedSolutionId != solutionId) return;
                SelectedSolution = solution;
                RefreshSolutionList();
            },
            message =>
            {
                if (SelectedSolutionId != solutionId) return;
                SelectedSolutionId = null;
                if (execStatusLabel != null) execStatusLabel.text = $"Solution {solutionId} unavailable: {message}";
                RefreshSolutionList();
            });
    }

    // One row per sub-trajectory, in execution order: sub_id from the feedback indexes this list.
    private void RefreshBreakdown()
    {
        if (breakdownContainer == null) return;
        breakdownContainer.Clear();
        if (SelectedSolutionId == null) return;
        if (SelectedSolution == null)
        {
            breakdownContainer.Add(Text($"Fetching solution {SelectedSolutionId}...", MutedColor, 11));
            return;
        }

        var steps = SelectedSolution.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>();
        bool tracking = Client != null && Client.ExecutingSolutionId == SelectedSolutionId &&
                        (Client.Phase == PickPlacePhase.Executing || Client.LastExecutionFeedback != null);
        int done = tracking ? ActiveStep(steps.Length) : 0;
        bool running = tracking && Client.Phase == PickPlacePhase.Executing;

        for (int i = 0; i < steps.Length; i++)
        {
            var info = steps[i].info;
            bool isActive = running && i == done;
            bool isDone = tracking && i < done;
            string stageName = Client?.StageName(info.stage_id) ?? $"Stage {info.stage_id}";
            int pts = steps[i].trajectory?.joint_trajectory?.points?.Length ?? 0;

            var row = Row(isActive ? ActiveColor : Color.clear);
            row.name = $"mtcStep{i}";
            string mark = isDone ? "✓ " : isActive ? "▶ " : "   ";
            string text = $"{mark}{i + 1}. {stageName}  cost={info.cost:F3}  pts={pts}";
            if (!string.IsNullOrEmpty(info.planner_id)) text += $"  [{info.planner_id}]";
            var label = Text(text, isDone ? DoneColor : Color.white, 11);
            if (isActive) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(label);
            breakdownContainer.Add(row);

            if (!string.IsNullOrEmpty(info.comment))
            {
                var comment = Text($"      // {info.comment}", new Color(0.65f, 0.65f, 0.35f), 10);
                breakdownContainer.Add(comment);
            }
        }
    }

    private void OnPreviewClicked()
    {
        if (SelectedSolution == null)
        {
            if (execStatusLabel != null) execStatusLabel.text = "Select a solution first.";
            return;
        }
        if (trajectoryPlayer == null)
        {
            if (execStatusLabel != null) execStatusLabel.text = "Preview unavailable: no trajectory player assigned.";
            return;
        }
        if (execStatusLabel != null) execStatusLabel.text = $"Previewing solution {SelectedSolutionId}...";
        trajectoryPlayer.OnProblem -= OnPreviewProblem;
        trajectoryPlayer.OnProblem += OnPreviewProblem;
        trajectoryPlayer.PlaySolution(SelectedSolution);
    }

    private void OnPreviewProblem(string problem)
    {
        if (execStatusLabel != null) execStatusLabel.text = "Cannot preview: " + problem;
    }

    // ─── Execute ──────────────────────────────────────────────────────────────

    private void OnExecutionFeedback(ExecuteSolutionFeedback feedback)
    {
        RefreshProgress();
        RefreshStages();
        RefreshBreakdown();
    }

    private void RefreshProgress()
    {
        if (executionProgress == null) return;
        var feedback = Client?.LastExecutionFeedback;
        bool executingSolution = Client != null && Client.Phase == PickPlacePhase.Executing && Client.ExecutingSolutionId != 0;
        if (!executingSolution && feedback == null)
        {
            executionProgress.style.display = DisplayStyle.None;
            return;
        }

        executionProgress.style.display = DisplayStyle.Flex;
        if (feedback == null || feedback.sub_no == 0)
        {
            executionProgress.value = 0;
            executionProgress.title = "Starting execution";
            return;
        }
        executionProgress.value = Mathf.Clamp01((feedback.sub_id + 1f) / feedback.sub_no) * 100f;
        string stageName = Client.StageName(feedback.stage_id);
        executionProgress.title = string.IsNullOrEmpty(stageName)
            ? $"{feedback.sub_id + 1}/{feedback.sub_no} steps done"
            : $"{feedback.sub_id + 1}/{feedback.sub_no} done ({stageName})";
    }

    /// <summary>Execute the selected solution by id on /execute_solution.</summary>
    public async void ExecuteSelected()
    {
        if (Client == null || Busy) return;
        if (SelectedSolutionId == null)
        {
            if (execStatusLabel != null) execStatusLabel.text = "Select a solution first.";
            return;
        }

        trajectoryPlayer?.Stop();
        try
        {
            await Client.ExecuteAsync(SelectedSolutionId.Value);
        }
        catch (Exception exception)
        {
            // Rejections, cancels and lost connections are already on the status line.
            Debug.LogWarning($"[MTCDashboardPanel] execution ended: {exception.Message}");
        }
        RefreshControls();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static VisualElement Row(Color background)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.paddingLeft = 6;
        row.style.paddingTop = 3;
        row.style.paddingBottom = 3;
        row.style.borderBottomWidth = 1;
        row.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));
        row.style.backgroundColor = new StyleColor(background);
        return row;
    }

    private static Label Text(string text, Color color, int size)
    {
        var label = new Label(text);
        label.style.color = new StyleColor(color);
        label.style.fontSize = size;
        return label;
    }

    private static Label RankLabel(int rank)
    {
        var label = Text($"#{rank}", new Color(1f, 0.8f, 0.2f), 12);
        label.style.width = 28;
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        return label;
    }
}
