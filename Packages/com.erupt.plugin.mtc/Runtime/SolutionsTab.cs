using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Ui;
using RosMessageTypes.MoveitTaskConstructorMsgs;

/// <summary>
/// Tier 3 "MTC" tab: stage tree with per-stage counts, the ranked solutions, the cost
/// breakdown of the selected one, and the pick/place recorder entry (Teach mode only,
/// Guidelines Part 5: demonstration lives in Teach). Replaces MTCDashboardPanel.
/// Interactive elements: record + up to five solution buttons + cancel = 7 (Part 8).
/// Preview and execute are tier 2 verbs on the selected solution's handle, not buttons here.
/// </summary>
public sealed class SolutionsTab
{
    public const int MaxSolutionButtons = 5;
    private static readonly Vector2 ButtonSize = new(300f, 56f);

    private readonly MtcPlugin plugin;
    private readonly RectTransform root;
    private readonly Button recordButton, cancelButton;
    private readonly TMPro.TextMeshProUGUI taskLabel, stagesLabel, statusLabel, breakdownLabel;
    private readonly Transform solutionsRow;
    private readonly List<Button> solutionButtons = new();
    private readonly Dictionary<Button, SolutionMsg> solutionByButton = new();

    public SolutionsTab(MtcPlugin plugin, RectTransform content)
    {
        this.plugin = plugin;
        root = content;

        var column = UiBuilder.CreateColumn("Mtc", content, 8f);
        UiBuilder.Stretch(column.GetComponent<RectTransform>());
        Transform t = column.transform;

        taskLabel = UiBuilder.CreateLabel("Task", t, "No task", 24f);
        recordButton = UiBuilder.CreateButton("Record", t, "Record pick & place", ButtonSize, OnRecord);
        statusLabel = UiBuilder.CreateLabel("Status", t, "Idle", 20f);
        stagesLabel = UiBuilder.CreateLabel("Stages", t, "", 18f);
        stagesLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
        stagesLabel.enableWordWrapping = true;
        solutionsRow = UiBuilder.CreateColumn("Solutions", t, 4f).transform;
        breakdownLabel = UiBuilder.CreateLabel("Breakdown", t, "", 18f);
        breakdownLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
        breakdownLabel.enableWordWrapping = true;
        cancelButton = UiBuilder.CreateButton("Cancel", t, "Cancel execution", ButtonSize, () => _ = plugin.Client?.CancelExecutionAsync());

        Subscribe();
        RefreshAll();
    }

    public RectTransform Root => root;
    public int InteractiveElementCount => UiBuilder.CountInteractive(root);
    public IReadOnlyList<Button> SolutionButtons => solutionButtons;

    public void Dispose()
    {
        var c = plugin.Client;
        if (c != null)
        {
            c.OnDescriptionReceived -= OnDescription;
            c.OnStatisticsUpdated -= OnStatistics;
            c.OnSolutionReceived -= OnSolution;
            c.OnTaskReset -= OnTaskReset;
            c.OnExecutionStatus -= OnExecutionStatus;
        }
        plugin.SelectedSolutionChanged -= RefreshSelection;
        plugin.TeachModeChanged -= RefreshRecord;
        if (plugin.Recorder != null) plugin.Recorder.OnRecordingComplete -= OnRecorded;
        if (plugin.PickPlace != null) plugin.PickPlace.OnStatus -= OnPickPlaceStatus;
    }

    private void Subscribe()
    {
        var c = plugin.Client;
        if (c != null)
        {
            c.OnDescriptionReceived += OnDescription;
            c.OnStatisticsUpdated += OnStatistics;
            c.OnSolutionReceived += OnSolution;
            c.OnTaskReset += OnTaskReset;
            c.OnExecutionStatus += OnExecutionStatus;
        }
        plugin.SelectedSolutionChanged += RefreshSelection;
        plugin.TeachModeChanged += RefreshRecord;
        if (plugin.Recorder != null) plugin.Recorder.OnRecordingComplete += OnRecorded;
        if (plugin.PickPlace != null) plugin.PickPlace.OnStatus += OnPickPlaceStatus;
    }

    // --- record (Teach mode) -----------------------------------------------------

    private void OnRecord()
    {
        var recorder = plugin.Recorder;
        if (recorder == null || !plugin.InTeachMode) return;
        if (!recorder.IsRecording) { recorder.StartRecording(); statusLabel.text = "Grab and place the object..."; }
        else { recorder.StopRecording(); statusLabel.text = "Idle"; }
        RefreshRecord();
    }

    private void OnRecorded(string objectId)
    {
        statusLabel.text = $"Sent: {objectId}";
        RefreshRecord();
    }

    private void OnPickPlaceStatus(string status) => statusLabel.text = $"Pick & place: {status}";

    private void RefreshRecord()
    {
        var recorder = plugin.Recorder;
        bool teach = plugin.InTeachMode;
        UiBuilder.SetInteractable(recordButton, teach && recorder != null);
        UiBuilder.SetButtonText(recordButton,
            recorder == null ? "No recorder" :
            !teach ? "Record (Teach mode)" :
            recorder.IsRecording ? "Stop recording" : "Record pick & place");
    }

    // --- task / stages ------------------------------------------------------------

    private void OnDescription(TaskDescriptionMsg desc) { RefreshTask(); RefreshStages(); }
    private void OnStatistics(TaskStatisticsMsg stats) => RefreshStages();
    private void OnTaskReset() { RefreshAll(); }
    private void OnSolution(SolutionMsg _) => RefreshSolutions();
    private void OnExecutionStatus(string status)
    {
        statusLabel.text = status;
        UiBuilder.SetInteractable(cancelButton, plugin.Client != null && plugin.Client.IsExecuting);
    }

    private void RefreshAll()
    {
        RefreshTask(); RefreshStages(); RefreshSolutions(); RefreshSelection(); RefreshRecord();
        UiBuilder.SetInteractable(cancelButton, plugin.Client != null && plugin.Client.IsExecuting);
    }

    private void RefreshTask()
    {
        string id = plugin.Client?.CurrentTaskId;
        taskLabel.text = string.IsNullOrEmpty(id) ? "No task" : $"Task {id}";
    }

    private void RefreshStages()
    {
        var desc = plugin.Client?.LastDescription;
        var stats = plugin.Client?.LastStatistics;
        if (desc?.stages == null || desc.stages.Length == 0) { stagesLabel.text = ""; return; }

        var statsById = new Dictionary<uint, StageStatisticsMsg>();
        if (stats?.stages != null) foreach (var s in stats.stages) statsById[s.id] = s;
        var children = new Dictionary<uint, List<StageDescriptionMsg>>();
        StageDescriptionMsg rootStage = null;
        foreach (var stage in desc.stages)
        {
            if (stage.id == stage.parent_id) { rootStage = stage; continue; }
            if (!children.TryGetValue(stage.parent_id, out var list)) children[stage.parent_id] = list = new List<StageDescriptionMsg>();
            list.Add(stage);
        }
        var sb = new StringBuilder();
        if (rootStage != null) AppendStage(sb, rootStage, children, statsById, 0);
        stagesLabel.text = sb.ToString().TrimEnd();
    }

    private static void AppendStage(StringBuilder sb, StageDescriptionMsg stage, Dictionary<uint, List<StageDescriptionMsg>> children,
                                    Dictionary<uint, StageStatisticsMsg> statsById, int depth)
    {
        statsById.TryGetValue(stage.id, out var s);
        sb.Append(' ', depth * 2).Append(stage.name)
          .Append($"  ✓{s?.solved?.Length ?? 0}  ✗{s?.num_failed ?? 0}  {s?.total_compute_time ?? 0:F1}s\n");
        if (children.TryGetValue(stage.id, out var kids))
            foreach (var k in kids) AppendStage(sb, k, children, statsById, depth + 1);
    }

    // --- solutions ----------------------------------------------------------------

    private void RefreshSolutions()
    {
        foreach (var b in solutionButtons) if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        solutionButtons.Clear();
        solutionByButton.Clear();

        var ranked = plugin.RankedSolutions.Take(MaxSolutionButtons).ToList();
        int rank = 1;
        foreach (var (sol, cost) in ranked)
        {
            var captured = sol;
            var button = UiBuilder.CreateButton($"Solution{rank}", solutionsRow, $"#{rank}  cost {cost:F3}  {sol.sub_trajectory.Length} segs", ButtonSize,
                () => plugin.SelectSolution(captured));
            solutionButtons.Add(button);
            solutionByButton[button] = sol;
            rank++;
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var selected = plugin.SelectedSolution;
        foreach (var (button, sol) in solutionByButton.Select(kv => (kv.Key, kv.Value)))
        {
            var image = button.GetComponent<Image>();
            if (image != null) image.color = sol == selected ? new Color(0.18f, 0.32f, 0.18f, 0.94f) : new Color(0.16f, 0.17f, 0.20f, 0.94f);
        }
        breakdownLabel.text = selected != null ? Breakdown(selected) : "";
    }

    private string Breakdown(SolutionMsg sol)
    {
        var stageNames = new Dictionary<uint, string>();
        var desc = plugin.Client?.LastDescription;
        if (desc?.stages != null) foreach (var s in desc.stages) stageNames[s.id] = s.name;

        var sb = new StringBuilder();
        foreach (var group in sol.sub_trajectory.GroupBy(t => t.info.stage_id))
        {
            stageNames.TryGetValue(group.Key, out var name);
            sb.Append(name ?? $"Stage {group.Key}").Append('\n');
            foreach (var seg in group)
            {
                int pts = seg.trajectory?.joint_trajectory?.points?.Length ?? 0;
                sb.Append($"  id={seg.info.id}  cost={seg.info.cost:F3}  pts={pts}");
                if (!string.IsNullOrEmpty(seg.info.planner_id)) sb.Append($"  [{seg.info.planner_id}]");
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
