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

    // Solutions tab
    private VisualElement solutionListContainer;
    private VisualElement breakdownContainer;

    // Shared
    private Label taskIdLabel;

    private SolutionMsg selectedSolution;

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

            if (dataManager.LastDescription != null) RefreshStages(dataManager.LastDescription);
            if (dataManager.LastStatistics != null) RefreshStats(dataManager.LastStatistics);
        }
    }

    private void OnDisable()
    {
        if (dataManager != null)
        {
            dataManager.OnDescriptionReceived -= RefreshStages;
            dataManager.OnStatisticsUpdated -= RefreshStats;
            dataManager.OnSolutionReceived -= OnSolutionArrived;
        }
    }

    private void BindUI()
    {
        taskIdLabel = root.Q<Label>("mtcTaskIdLabel");
        panelPlan = root.Q<VisualElement>("mtcPanelPlan");
        panelStages = root.Q<VisualElement>("mtcPanelStages");
        panelSolutions = root.Q<VisualElement>("mtcPanelSolutions");

        root.Q<Button>("mtcTabPlan").clicked += () => ShowTab(panelPlan);
        root.Q<Button>("mtcTabStages").clicked += () => ShowTab(panelStages);
        root.Q<Button>("mtcTabSolutions").clicked += () => { ShowTab(panelSolutions); RefreshSolutionList(); };

        planObjectIdLabel = root.Q<Label>("mtcPlanObjectIdLabel");
        planStatusLabel = root.Q<Label>("mtcPlanStatusLabel");
        recordButton = root.Q<Button>("mtcPlanRecordButton");
        recordButton.clicked += OnRecordClicked;

        stageTreeContainer = root.Q<VisualElement>("mtcStageTreeContainer");
        solutionListContainer = root.Q<VisualElement>("mtcSolutionListContainer");
        breakdownContainer = root.Q<VisualElement>("mtcBreakdownContainer");

        root.Q<Button>("mtcPreviewButton").clicked += OnPreviewClicked;
        root.Q<Button>("mtcStopPreviewButton").clicked += () => trajectoryPlayer?.Stop();
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
            pickPlaceRecorder.OnRecordingComplete = id =>
            {
                planStatusLabel.text = $"Sent: {id}";
                recordButton.text = "Start Recording";
                recordButton.style.color = new StyleColor(StyleKeyword.Null);
            };
            pickPlaceRecorder.StartRecording();
            recordButton.text = "Stop Recording";
            recordButton.style.color = new StyleColor(Color.yellow);
            planStatusLabel.text = "Grab and place the object...";
        }
        else
        {
            pickPlaceRecorder.StopRecording();
            recordButton.text = "Start Recording";
            recordButton.style.color = new StyleColor(StyleKeyword.Null);
            planStatusLabel.text = "Idle";
        }
    }

    // ─── Stages tab ───────────────────────────────────────────────────────────

    private void RefreshStages(TaskDescriptionMsg desc)
    {
        if (taskIdLabel != null) taskIdLabel.text = $"Task: {desc.task_id}";
        RebuildStageTree(desc, dataManager?.LastStatistics);
    }

    private void RefreshStats(TaskStatisticsMsg stats)
    {
        if (dataManager?.LastDescription != null)
            RebuildStageTree(dataManager.LastDescription, stats);
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

        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.paddingLeft = depth * 12 + 4;
        row.style.paddingTop = 2;
        row.style.paddingBottom = 2;

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
        container.Add(row);

        if (children.TryGetValue(stage.id, out var childList))
            foreach (var child in childList)
                AddStageRow(container, child, children, statsById, depth + 1);
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
}
