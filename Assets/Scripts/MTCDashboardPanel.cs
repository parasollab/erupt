using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using RosMessageTypes.MoveitTaskConstructorMsgs;

public class MTCDashboardPanel : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [SerializeField] private MTCDataManager dataManager;
    [SerializeField] private PickPlaceTaskRecorder pickPlaceRecorder;
    [Tooltip("Optional: same client the recorder sends goals through, so the plan tab " +
             "shows /pick_place acceptance, feedback stages and the final result.")]
    [SerializeField] private PickPlaceActionClient pickPlaceAction;
    [SerializeField] private MTCTrajectoryPlayer trajectoryPlayer;

    // Root
    private VisualElement root;

    // Tab panels
    private VisualElement panelPlan;
    private VisualElement panelStages;
    private VisualElement panelSolutions;

    // Plan tab
    private Button recordButton;
    private Label planObjectIdLabel;
    private Label planStatusLabel;

    // Stages tab
    private VisualElement stageTreeContainer;
    private VisualElement stageSolutionsContainer;

    // Solutions tab
    private VisualElement solutionListContainer;
    private VisualElement breakdownContainer;
    private Button executeButton;
    private Button cancelExecuteButton;
    private ProgressBar executionProgress;
    private Label execStatusLabel;

    // Shared
    private Label taskIdLabel;

    private SolutionMsg selectedSolution;
    private bool hasSelectedStage;
    private uint selectedStageId;
    private bool executing;

    // Guard so the ROS node's 1 Hz description republish doesn't rebuild an unchanged tree
    private string lastBuiltTaskId;
    private int lastBuiltStageCount = -1;

    private void OnEnable()
    {
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        root = uiDocument?.rootVisualElement;
        if (root == null) { Debug.LogError("[MTCDashboardPanel] No root visual element."); return; }

        BindUI();
        ShowTab(panelPlan);

        if (dataManager == null) dataManager = MTCDataManager.Instance;
        if (dataManager != null)
        {
            dataManager.OnDescriptionReceived += RefreshStages;
            dataManager.OnStatisticsUpdated += RefreshStats;
            dataManager.OnSolutionReceived += OnSolutionArrived;
            dataManager.OnTaskReset += OnTaskReset;
            dataManager.OnExecutionStatus += OnExecutionStatus;
            dataManager.OnExecutionFeedback += OnExecutionFeedback;

            if (dataManager.LastDescription != null) RefreshStages(dataManager.LastDescription);
            if (dataManager.LastStatistics != null) RefreshStats(dataManager.LastStatistics);
            if (!string.IsNullOrEmpty(dataManager.LastExecutionStatus))
                OnExecutionStatus(dataManager.LastExecutionStatus);
        }

        if (pickPlaceRecorder != null)
        {
            pickPlaceRecorder.OnRecordingComplete -= OnPickPlaceRecorded;
            pickPlaceRecorder.OnRecordingComplete += OnPickPlaceRecorded;
        }

        if (pickPlaceAction != null)
        {
            pickPlaceAction.OnStatus -= OnPickPlaceStatus;
            pickPlaceAction.OnStatus += OnPickPlaceStatus;
        }
    }

    private void OnDisable()
    {
        if (dataManager != null)
        {
            dataManager.OnDescriptionReceived -= RefreshStages;
            dataManager.OnStatisticsUpdated -= RefreshStats;
            dataManager.OnSolutionReceived -= OnSolutionArrived;
            dataManager.OnTaskReset -= OnTaskReset;
            dataManager.OnExecutionStatus -= OnExecutionStatus;
            dataManager.OnExecutionFeedback -= OnExecutionFeedback;
        }

        if (pickPlaceRecorder != null)
            pickPlaceRecorder.OnRecordingComplete -= OnPickPlaceRecorded;

        if (pickPlaceAction != null)
            pickPlaceAction.OnStatus -= OnPickPlaceStatus;
    }

    private void BindUI()
    {
        taskIdLabel = root.Q<Label>("mtcTaskIdLabel");
        panelPlan = root.Q<VisualElement>("mtcPanelPlan");
        panelStages = root.Q<VisualElement>("mtcPanelStages");
        panelSolutions = root.Q<VisualElement>("mtcPanelSolutions");

        BindButton("mtcTabPlan", () => ShowTab(panelPlan));
        BindButton("mtcTabStages", () => ShowTab(panelStages));
        BindButton("mtcTabSolutions", () => { ShowTab(panelSolutions); RefreshSolutionList(); });

        planObjectIdLabel = root.Q<Label>("mtcPlanObjectIdLabel");
        planStatusLabel = root.Q<Label>("mtcPlanStatusLabel");
        recordButton = root.Q<Button>("mtcPlanRecordButton");
        if (recordButton != null) recordButton.clicked += OnRecordClicked;

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
        if (executeButton != null) executeButton.clicked += OnExecuteClicked;
        if (cancelExecuteButton != null)
        {
            cancelExecuteButton.clicked += OnCancelClicked;
            cancelExecuteButton.SetEnabled(false);
        }

        // Force a rebuild on the next description — the UIDocument recreates its visual
        // tree on every enable, so cached "already built" state no longer applies.
        lastBuiltTaskId = null;
        lastBuiltStageCount = -1;
    }

    private void BindButton(string name, System.Action onClick)
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

    // ─── Plan tab ─────────────────────────────────────────────────────────────

    private void OnRecordClicked()
    {
        if (pickPlaceRecorder == null) return;

        if (!pickPlaceRecorder.IsRecording)
        {
            pickPlaceRecorder.StartRecording();
            if (recordButton != null)
            {
                recordButton.text = "Stop Recording";
                recordButton.style.color = new StyleColor(Color.yellow);
            }
            if (planStatusLabel != null) planStatusLabel.text = "Grab and place the object...";
        }
        else
        {
            pickPlaceRecorder.StopRecording();
            ResetRecordButton();
            if (planStatusLabel != null) planStatusLabel.text = "Idle";
        }
    }

    private void OnPickPlaceRecorded(string objectId)
    {
        if (planObjectIdLabel != null) planObjectIdLabel.text = objectId;
        if (planStatusLabel != null) planStatusLabel.text = $"Sent: {objectId}";
        ResetRecordButton();
    }

    // /pick_place progress: SENDING → RUNNING → PLANNING/EXECUTING (feedback) → result.
    private void OnPickPlaceStatus(string status)
    {
        if (planStatusLabel != null) planStatusLabel.text = $"Pick & place: {status}";
    }

    private void ResetRecordButton()
    {
        if (recordButton == null) return;
        recordButton.text = "Start Recording";
        recordButton.style.color = new StyleColor(StyleKeyword.Null);
    }

    // ─── Execution status ─────────────────────────────────────────────────────

    private void OnExecutionStatus(string status)
    {
        executing = status == "SENDING" || status == "EXECUTING" || status == "CANCELING";
        if (execStatusLabel != null) execStatusLabel.text = status;
        if (planStatusLabel != null) planStatusLabel.text = status;
        if (executeButton != null)
            executeButton.SetEnabled(!executing && dataManager != null && dataManager.ExecutionAvailable);
        if (cancelExecuteButton != null)
            cancelExecuteButton.SetEnabled(dataManager != null && dataManager.IsExecuting);
        if (executionProgress != null)
        {
            if (status == "SENDING") executionProgress.value = 0;
            if (status == "SUCCEEDED") executionProgress.value = 100;
            executionProgress.style.display = executing || status == "SUCCEEDED"
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }
    }

    private void OnExecutionFeedback(ExecuteTaskSolutionFeedback feedback)
    {
        if (executionProgress == null) return;

        float progress = feedback.sub_no == 0
            ? 0
            : Mathf.Clamp01((feedback.sub_id + 1f) / feedback.sub_no) * 100f;
        executionProgress.value = progress;
        executionProgress.style.display = DisplayStyle.Flex;

        string stageName = null;
        if (selectedSolution?.sub_trajectory != null &&
            feedback.sub_id < (uint)selectedSolution.sub_trajectory.Length)
        {
            uint stageId = selectedSolution.sub_trajectory[(int)feedback.sub_id].info.stage_id;
            stageName = dataManager?.LastDescription?.stages?
                .FirstOrDefault(stage => stage.id == stageId)?.name;
        }

        executionProgress.title = string.IsNullOrEmpty(stageName)
            ? $"Executing {feedback.sub_id + 1}/{feedback.sub_no}"
            : $"{stageName} ({feedback.sub_id + 1}/{feedback.sub_no})";
    }

    private void OnTaskReset()
    {
        selectedSolution = null;
        hasSelectedStage = false;
        stageSolutionsContainer?.Clear();
        solutionListContainer?.Clear();
        breakdownContainer?.Clear();
    }

    // ─── Stages tab ───────────────────────────────────────────────────────────

    private void RefreshStages(TaskDescriptionMsg desc)
    {
        if (taskIdLabel != null) taskIdLabel.text = $"Task: {desc.task_id}";

        // 1 Hz republish of an unchanged task: stats updates arrive via RefreshStats,
        // so skip the full tree rebuild.
        int stageCount = desc.stages?.Length ?? 0;
        if (desc.task_id == lastBuiltTaskId && stageCount == lastBuiltStageCount) return;

        lastBuiltTaskId = desc.task_id;
        lastBuiltStageCount = stageCount;
        RebuildStageTree(desc, dataManager?.LastStatistics);
    }

    private void RefreshStats(TaskStatisticsMsg stats)
    {
        if (dataManager?.LastDescription != null)
        {
            RebuildStageTree(dataManager.LastDescription, stats);
            if (hasSelectedStage) RefreshStageSolutions();
        }
    }

    private void RebuildStageTree(TaskDescriptionMsg desc, TaskStatisticsMsg stats)
    {
        if (stageTreeContainer == null) return;
        stageTreeContainer.Clear();
        if (desc.stages == null || desc.stages.Length == 0) return;

        var statsById = new Dictionary<uint, StageStatisticsMsg>();
        if (stats != null)
            foreach (var s in stats.stages) statsById[s.id] = s;

        var children = new Dictionary<uint, List<StageDescriptionMsg>>();
        StageDescriptionMsg rootStage = null;
        foreach (var stage in desc.stages)
        {
            if (stage.id == stage.parent_id) { rootStage = stage; continue; }
            if (!children.TryGetValue(stage.parent_id, out var list))
                children[stage.parent_id] = list = new List<StageDescriptionMsg>();
            list.Add(stage);
        }

        if (rootStage != null)
            AddStageRow(stageTreeContainer, rootStage, children, statsById, 0);
    }

    private void AddStageRow(
        VisualElement container,
        StageDescriptionMsg stage,
        Dictionary<uint, List<StageDescriptionMsg>> children,
        Dictionary<uint, StageStatisticsMsg> statsById,
        int depth)
    {
        statsById.TryGetValue(stage.id, out var s);
        int solved = s?.solved?.Length ?? 0;
        uint failed = s?.num_failed ?? 0;
        double time = s?.total_compute_time ?? 0;

        bool isSelected = hasSelectedStage && selectedStageId == stage.id;

        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.paddingLeft = depth * 12 + 4;
        row.style.paddingTop = 2;
        row.style.paddingBottom = 2;
        if (isSelected)
            row.style.backgroundColor = new StyleColor(new Color(0.15f, 0.28f, 0.15f));

        var nameLabel = new Label(stage.name);
        nameLabel.style.flexGrow = 1;
        nameLabel.style.color = new StyleColor(Color.white);
        nameLabel.style.fontSize = 12;

        var statsLabel = new Label($"✓{solved}  ✗{failed}  {time:F1}s");
        statsLabel.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
        statsLabel.style.fontSize = 11;
        statsLabel.style.unityTextAlign = TextAnchor.MiddleRight;

        row.Add(nameLabel);
        row.Add(statsLabel);

        uint capturedId = stage.id;
        row.RegisterCallback<PointerDownEvent>(_ => SelectStage(capturedId));
        container.Add(row);

        if (children.TryGetValue(stage.id, out var childList))
            foreach (var child in childList)
                AddStageRow(container, child, children, statsById, depth + 1);
    }

    private void SelectStage(uint stageId)
    {
        hasSelectedStage = true;
        selectedStageId = stageId;
        if (dataManager?.LastDescription != null)
            RebuildStageTree(dataManager.LastDescription, dataManager.LastStatistics);
        RefreshStageSolutions();
    }

    // Per-stage solution browsing: list this stage's solved[] ids (sorted by cost on the
    // ROS side); clicking one fetches the full solution via GetSolution and previews it.
    private void RefreshStageSolutions()
    {
        if (stageSolutionsContainer == null || dataManager == null) return;
        stageSolutionsContainer.Clear();
        if (!hasSelectedStage) return;

        var stats = dataManager.LastStatistics?.stages?.FirstOrDefault(s => s.id == selectedStageId);
        var solved = stats?.solved;
        if (solved == null || solved.Length == 0)
        {
            var empty = new Label("No solutions for this stage yet.");
            empty.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            empty.style.fontSize = 11;
            empty.style.paddingLeft = 6;
            stageSolutionsContainer.Add(empty);
            return;
        }

        int rank = 1;
        foreach (var id in solved)
        {
            uint capturedId = id;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingLeft = 6;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));

            var rankLabel = new Label($"#{rank}");
            rankLabel.style.width = 28;
            rankLabel.style.color = new StyleColor(new Color(1f, 0.8f, 0.2f));
            rankLabel.style.fontSize = 11;

            var idLabel = new Label($"solution id {id}");
            idLabel.style.flexGrow = 1;
            idLabel.style.color = new StyleColor(Color.white);
            idLabel.style.fontSize = 11;

            var hint = new Label("tap to preview");
            hint.style.color = new StyleColor(new Color(0.55f, 0.75f, 1f));
            hint.style.fontSize = 10;

            row.Add(rankLabel);
            row.Add(idLabel);
            row.Add(hint);
            row.RegisterCallback<PointerDownEvent>(_ => PreviewStageSolution(capturedId));
            stageSolutionsContainer.Add(row);
            rank++;
        }
    }

    private void PreviewStageSolution(uint solutionId)
    {
        dataManager?.FetchSolution(solutionId, sol =>
        {
            selectedSolution = sol;
            trajectoryPlayer?.PlaySolution(sol);
            RefreshBreakdown(sol);
        });
    }

    // ─── Solutions tab ────────────────────────────────────────────────────────

    private void OnSolutionArrived(SolutionMsg _)
    {
        if (panelSolutions?.resolvedStyle.display == DisplayStyle.Flex)
            RefreshSolutionList();
    }

    private void RefreshSolutionList()
    {
        if (solutionListContainer == null || dataManager == null) return;
        solutionListContainer.Clear();

        var ranked = dataManager.Solutions
            .Select(s => (sol: s, cost: (double)s.sub_trajectory.Sum(t => t.info.cost)))
            .OrderBy(x => x.cost)
            .ToList();

        int rank = 1;
        foreach (var (sol, cost) in ranked)
        {
            var capturedSol = sol;
            bool isSelected = capturedSol == selectedSolution;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));
            row.style.backgroundColor = new StyleColor(isSelected
                ? new Color(0.18f, 0.32f, 0.18f)
                : new Color(0.08f, 0.08f, 0.08f));

            var rankLabel = new Label($"#{rank}");
            rankLabel.style.width = 28;
            rankLabel.style.color = new StyleColor(new Color(1f, 0.8f, 0.2f));
            rankLabel.style.fontSize = 12;
            rankLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            var costLabel = new Label($"cost {cost:F3}");
            costLabel.style.flexGrow = 1;
            costLabel.style.color = new StyleColor(Color.white);
            costLabel.style.fontSize = 12;

            int segs = capturedSol.sub_trajectory.Length;
            var segLabel = new Label($"{segs} segs");
            segLabel.style.color = new StyleColor(new Color(0.55f, 0.75f, 1f));
            segLabel.style.fontSize = 11;
            segLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            row.Add(rankLabel);
            row.Add(costLabel);
            row.Add(segLabel);
            row.RegisterCallback<PointerDownEvent>(_ => SelectSolution(capturedSol));
            solutionListContainer.Add(row);
            rank++;
        }
    }

    private void SelectSolution(SolutionMsg sol)
    {
        selectedSolution = sol;
        RefreshSolutionList();
        RefreshBreakdown(sol);
    }

    private void RefreshBreakdown(SolutionMsg sol)
    {
        if (breakdownContainer == null) return;
        breakdownContainer.Clear();

        var stageNames = new Dictionary<uint, string>();
        if (dataManager?.LastDescription != null)
            foreach (var s in dataManager.LastDescription.stages)
                stageNames[s.id] = s.name;

        var grouped = sol.sub_trajectory.GroupBy(t => t.info.stage_id).ToList();
        foreach (var group in grouped)
        {
            stageNames.TryGetValue(group.Key, out var stageName);
            stageName ??= $"Stage {group.Key}";

            var header = new Label(stageName);
            header.style.color = new StyleColor(new Color(0.55f, 0.85f, 1f));
            header.style.fontSize = 12;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.paddingTop = 5;
            breakdownContainer.Add(header);

            foreach (var seg in group)
            {
                int pts = seg.trajectory?.joint_trajectory?.points?.Length ?? 0;
                var detailText = $"  id={seg.info.id}  cost={seg.info.cost:F3}  pts={pts}";
                if (!string.IsNullOrEmpty(seg.info.planner_id))
                    detailText += $"  [{seg.info.planner_id}]";

                var detail = new Label(detailText);
                detail.style.color = new StyleColor(new Color(0.75f, 0.75f, 0.75f));
                detail.style.fontSize = 11;
                detail.style.paddingLeft = 10;
                breakdownContainer.Add(detail);

                if (!string.IsNullOrEmpty(seg.info.comment))
                {
                    var comment = new Label($"  // {seg.info.comment}");
                    comment.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.35f));
                    comment.style.fontSize = 10;
                    comment.style.paddingLeft = 10;
                    breakdownContainer.Add(comment);
                }
            }
        }
    }

    private void OnPreviewClicked()
    {
        if (selectedSolution == null)
        {
            Debug.LogWarning("[MTCDashboardPanel] No solution selected for preview.");
            return;
        }
        trajectoryPlayer?.PlaySolution(selectedSolution);
    }

    // ─── Execute ──────────────────────────────────────────────────────────────

    private async void OnExecuteClicked()
    {
        if (executing) return;
        if (selectedSolution == null)
        {
            SetExecStatus("Select a solution first.");
            return;
        }

        uint id = MTCDataManager.TopLevelId(selectedSolution);
        if (id == 0 || !IsTopLevelSolution(id))
        {
            // Only complete (root-stage) solutions start from the robot's current state;
            // a lone sub-solution would be rejected by move_group anyway.
            SetExecStatus("Only complete solutions can be executed.");
            return;
        }

        trajectoryPlayer?.Stop();
        try
        {
            if (dataManager == null)
                throw new InvalidOperationException("MTC data manager is unavailable");
            await dataManager.ExecuteSolutionAsync(selectedSolution);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            SetExecStatus(dataManager?.LastExecutionStatus ?? $"Execution failed: {exception.Message}");
        }
    }

    private async void OnCancelClicked()
    {
        try
        {
            if (dataManager == null)
                throw new InvalidOperationException("MTC data manager is unavailable");
            await dataManager.CancelExecutionAsync();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            SetExecStatus($"Cancel failed: {exception.Message}");
        }
    }

    private bool IsTopLevelSolution(uint id)
    {
        var desc = dataManager?.LastDescription;
        var stats = dataManager?.LastStatistics;
        if (desc?.stages == null || stats?.stages == null) return false;

        var rootStage = desc.stages.FirstOrDefault(s => s.id == s.parent_id);
        if (rootStage == null) return false;

        var rootStats = stats.stages.FirstOrDefault(s => s.id == rootStage.id);
        return rootStats?.solved != null && rootStats.solved.Contains(id);
    }

    private void SetExecStatus(string text)
    {
        if (execStatusLabel != null) execStatusLabel.text = text;
    }
}
