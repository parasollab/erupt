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

    private Canvas uguiCanvas;
    private GameObject uguiPanelPlan;
    private GameObject uguiPanelStages;
    private GameObject uguiPanelSolutions;
    private Transform uguiStageTreeContainer;
    private Transform uguiSolutionListContainer;
    private Transform uguiBreakdownContainer;
    private UnityEngine.UI.Text uguiTaskIdLabel;
    private UnityEngine.UI.Text uguiPlanObjectIdLabel;
    private UnityEngine.UI.Text uguiPlanStatusLabel;
    private UnityEngine.UI.Text uguiRecordButtonText;
    private bool useUGUIRuntimeMenu;

    private SolutionMsg selectedSolution;

    private void OnEnable()
    {
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();

        useUGUIRuntimeMenu = VisionOSSampleControlsUI.ShouldUseRuntimeUGUI() || uiDocument == null;
        if (useUGUIRuntimeMenu)
        {
            if (uiDocument != null)
                uiDocument.enabled = false;

            BindUGUI();
            ShowUGUITab(uguiPanelPlan);
        }
        else
        {
            root = uiDocument?.rootVisualElement;
            if (root == null) { Debug.LogError("[MTCDashboardPanel] No root visual element."); return; }

            BindUI();
            ShowTab(panelPlan);
        }

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

    private void BindUGUI()
    {
        uguiCanvas = VisionOSSampleControlsUI.EnsureCanvas(
            transform,
            "VisionOS MTC Dashboard Canvas",
            new Vector2(1050f, 1250f),
            new Vector2(1.05f, 1.25f),
            new Vector3(0f, -0.2f, 0f),
            sortingOrder: 125);

        VisionOSSampleControlsUI.ClearChildren(uguiCanvas.transform);
        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            uguiCanvas.transform,
            "MTC Dashboard Panel",
            new Vector2(1050f, 1250f));

        uguiTaskIdLabel = VisionOSSampleControlsUI.CreateText(panel.transform, "Task: --", 42, TextAnchor.MiddleCenter, VisionOSSampleControlsUI.TextColor, 90f);

        var tabs = VisionOSSampleControlsUI.CreateHorizontalRow(panel.transform, "MTC Tabs", 950f, 100f);
        VisionOSSampleControlsUI.CreateButton(tabs.transform, "Plan", () => ShowUGUITab(uguiPanelPlan), 290f, 100f, 32);
        VisionOSSampleControlsUI.CreateButton(tabs.transform, "Stages", () => ShowUGUITab(uguiPanelStages), 290f, 100f, 32);
        VisionOSSampleControlsUI.CreateButton(tabs.transform, "Solutions", () => { ShowUGUITab(uguiPanelSolutions); RefreshSolutionList(); }, 290f, 100f, 32);

        uguiPanelPlan = CreateUGUITabPanel(panel.transform, "Plan");
        uguiPlanObjectIdLabel = VisionOSSampleControlsUI.CreateText(uguiPanelPlan.transform, "Object: --", 32, TextAnchor.MiddleLeft, VisionOSSampleControlsUI.TextColor, 80f);
        uguiPlanStatusLabel = VisionOSSampleControlsUI.CreateText(uguiPanelPlan.transform, "Idle", 32, TextAnchor.MiddleLeft, VisionOSSampleControlsUI.TextColor, 80f);
        var recordButtonUGUI = VisionOSSampleControlsUI.CreateButton(uguiPanelPlan.transform, "Start Recording", OnRecordClicked, 950f, 100f, 35);
        uguiRecordButtonText = recordButtonUGUI.GetComponentInChildren<UnityEngine.UI.Text>();

        uguiPanelStages = CreateUGUITabPanel(panel.transform, "Stages");
        uguiStageTreeContainer = uguiPanelStages.transform;

        uguiPanelSolutions = CreateUGUITabPanel(panel.transform, "Solutions");
        uguiSolutionListContainer = CreateUGUITabPanel(uguiPanelSolutions.transform, "Solution List", 950f, 360f, addBackground: false).transform;
        uguiBreakdownContainer = CreateUGUITabPanel(uguiPanelSolutions.transform, "Solution Breakdown", 950f, 360f, addBackground: false).transform;
        var solutionActions = VisionOSSampleControlsUI.CreateHorizontalRow(uguiPanelSolutions.transform, "Solution Actions", 950f, 100f);
        VisionOSSampleControlsUI.CreateButton(solutionActions.transform, "Preview", OnPreviewClicked, 455f, 100f, 35);
        VisionOSSampleControlsUI.CreateButton(solutionActions.transform, "Stop", () => trajectoryPlayer?.Stop(), 455f, 100f, 35);
    }

    private GameObject CreateUGUITabPanel(Transform parent, string panelName, float width = 950f, float height = 820f, bool addBackground = false)
    {
        var panel = VisionOSSampleControlsUI.CreateVerticalPanel(
            parent,
            "MTC " + panelName + " Tab",
            new Vector2(width, height),
            addBackground);

        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 1f);

        return panel;
    }

    private void ShowTab(VisualElement active)
    {
        foreach (var p in new[] { panelPlan, panelStages, panelSolutions })
            if (p != null) p.style.display = p == active ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void ShowUGUITab(GameObject active)
    {
        foreach (var panel in new[] { uguiPanelPlan, uguiPanelStages, uguiPanelSolutions })
            if (panel != null) panel.SetActive(panel == active);
    }

    // ─── Plan tab ─────────────────────────────────────────────────────────────

    private void OnRecordClicked()
    {
        if (pickPlaceRecorder == null) return;

        if (!pickPlaceRecorder.IsRecording)
        {
            pickPlaceRecorder.OnRecordingComplete = id =>
            {
                SetPlanStatus($"Sent: {id}");
                SetRecordButton("Start Recording", Color.white);
            };
            pickPlaceRecorder.StartRecording();
            SetRecordButton("Stop Recording", Color.yellow);
            SetPlanStatus("Grab and place the object...");
        }
        else
        {
            pickPlaceRecorder.StopRecording();
            SetRecordButton("Start Recording", Color.white);
            SetPlanStatus("Idle");
        }
    }

    private void SetRecordButton(string text, Color color)
    {
        if (recordButton != null)
        {
            recordButton.text = text;
            recordButton.style.color = color == Color.white ? new StyleColor(StyleKeyword.Null) : new StyleColor(color);
        }
        if (uguiRecordButtonText != null)
        {
            uguiRecordButtonText.text = text;
            uguiRecordButtonText.color = color == Color.white ? VisionOSSampleControlsUI.TextColor : color;
        }
    }

    private void SetPlanStatus(string text)
    {
        if (planStatusLabel != null)
            planStatusLabel.text = text;
        if (uguiPlanStatusLabel != null)
            uguiPlanStatusLabel.text = text;
    }

    // ─── Stages tab ───────────────────────────────────────────────────────────

    private void RefreshStages(TaskDescriptionMsg desc)
    {
        if (taskIdLabel != null) taskIdLabel.text = $"Task: {desc.task_id}";
        if (uguiTaskIdLabel != null) uguiTaskIdLabel.text = $"Task: {desc.task_id}";
        RebuildStageTree(desc, dataManager?.LastStatistics);
    }

    private void RefreshStats(TaskStatisticsMsg stats)
    {
        if (dataManager?.LastDescription != null)
            RebuildStageTree(dataManager.LastDescription, stats);
    }

    private void RebuildStageTree(TaskDescriptionMsg desc, TaskStatisticsMsg stats)
    {
        if (stageTreeContainer == null && uguiStageTreeContainer == null) return;
        if (stageTreeContainer != null)
            stageTreeContainer.Clear();
        if (uguiStageTreeContainer != null)
            VisionOSSampleControlsUI.ClearChildren(uguiStageTreeContainer);
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
        {
            if (stageTreeContainer != null)
                AddStageRow(stageTreeContainer, rootStage, children, statsById, 0);
            AddUGUIStageRow(uguiStageTreeContainer, rootStage, children, statsById, 0);
        }
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

    private void AddUGUIStageRow(
        Transform container,
        StageDescriptionMsg stage,
        Dictionary<uint, List<StageDescriptionMsg>> children,
        Dictionary<uint, StageStatisticsMsg> statsById,
        int depth)
    {
        if (container == null)
            return;

        statsById.TryGetValue(stage.id, out var s);
        int solved = s?.solved?.Length ?? 0;
        uint failed = s?.num_failed ?? 0;
        double time = s?.total_compute_time ?? 0;
        string indent = new string(' ', depth * 2);
        VisionOSSampleControlsUI.CreateText(
            container,
            $"{indent}{stage.name}    ok {solved}  fail {failed}  {time:F1}s",
            24,
            TextAnchor.MiddleLeft,
            VisionOSSampleControlsUI.TextColor,
            48f);

        if (children.TryGetValue(stage.id, out var childList))
            foreach (var child in childList)
                AddUGUIStageRow(container, child, children, statsById, depth + 1);
    }

    // ─── Solutions tab ────────────────────────────────────────────────────────

    private void OnSolutionArrived(SolutionMsg _)
    {
        bool uiToolkitSolutionsVisible = panelSolutions?.resolvedStyle.display == DisplayStyle.Flex;
        bool uguiSolutionsVisible = uguiPanelSolutions != null && uguiPanelSolutions.activeSelf;
        if (uiToolkitSolutionsVisible || uguiSolutionsVisible)
            RefreshSolutionList();
    }

    private void RefreshSolutionList()
    {
        if (dataManager == null || (solutionListContainer == null && uguiSolutionListContainer == null)) return;
        if (solutionListContainer != null)
            solutionListContainer.Clear();
        if (uguiSolutionListContainer != null)
            VisionOSSampleControlsUI.ClearChildren(uguiSolutionListContainer);

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

            if (solutionListContainer != null)
            {
                row.Add(rankLabel);
                row.Add(costLabel);
                row.Add(segLabel);
                row.RegisterCallback<PointerDownEvent>(_ => SelectSolution(capturedSol));
                solutionListContainer.Add(row);
            }

            if (uguiSolutionListContainer != null)
            {
                string label = $"#{rank}  cost {cost:F3}  {segs} segs";
                VisionOSSampleControlsUI.CreateButton(
                    uguiSolutionListContainer,
                    label,
                    () => SelectSolution(capturedSol),
                    900f,
                    70f,
                    26);
            }
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
        if (breakdownContainer == null && uguiBreakdownContainer == null) return;
        if (breakdownContainer != null)
            breakdownContainer.Clear();
        if (uguiBreakdownContainer != null)
            VisionOSSampleControlsUI.ClearChildren(uguiBreakdownContainer);

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
            if (breakdownContainer != null)
                breakdownContainer.Add(header);
            if (uguiBreakdownContainer != null)
                VisionOSSampleControlsUI.CreateText(uguiBreakdownContainer, stageName, 26, TextAnchor.MiddleLeft, VisionOSSampleControlsUI.AccentTextColor, 54f);

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
                if (breakdownContainer != null)
                    breakdownContainer.Add(detail);
                if (uguiBreakdownContainer != null)
                    VisionOSSampleControlsUI.CreateText(uguiBreakdownContainer, detailText.Trim(), 22, TextAnchor.MiddleLeft, VisionOSSampleControlsUI.TextColor, 44f);

                if (!string.IsNullOrEmpty(seg.info.comment))
                {
                    var comment = new Label($"  // {seg.info.comment}");
                    comment.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.35f));
                    comment.style.fontSize = 10;
                    comment.style.paddingLeft = 10;
                    if (breakdownContainer != null)
                        breakdownContainer.Add(comment);
                    if (uguiBreakdownContainer != null)
                        VisionOSSampleControlsUI.CreateText(uguiBreakdownContainer, seg.info.comment, 20, TextAnchor.MiddleLeft, VisionOSSampleControlsUI.DisabledTextColor, 40f);
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
