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
/// Tier 3 "MTC" tab: the solution browser and the execution panel. Stage tree with per-stage
/// counts, the current task's solution ids, the steps of the selected one (highlighted as
/// execution feedback arrives), and the pick/place recorder entry, which is the plan button
/// (Teach mode only, Guidelines Part 5: demonstration lives in Teach).
/// Interactive elements: record + up to five solution buttons + cancel = 7 (Part 8).
/// Preview and execute are tier 2 verbs on the selected solution's handle, not buttons here.
/// </summary>
public sealed class SolutionsTab
{
    public const int MaxSolutionButtons = 5;
    private static readonly Vector2 ButtonSize = new(300f, 56f);
    private static readonly Color SelectedColor = new(0.18f, 0.32f, 0.18f, 0.94f);
    private static readonly Color IdleColor = new(0.16f, 0.17f, 0.20f, 0.94f);
    private const string ActiveMark = "<color=#7CFC7C><b>";
    private const string ActiveEnd = "</b></color>";

    private readonly MtcPlugin plugin;
    private readonly RectTransform root;
    private readonly Button recordButton, cancelButton;
    private readonly TMPro.TextMeshProUGUI taskLabel, stagesLabel, statusLabel, breakdownLabel;
    private readonly Transform solutionsRow;
    private readonly List<Button> solutionButtons = new();
    private readonly Dictionary<Button, uint> idByButton = new();

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
        stagesLabel.richText = true;
        solutionsRow = UiBuilder.CreateColumn("Solutions", t, 4f).transform;
        breakdownLabel = UiBuilder.CreateLabel("Breakdown", t, "", 18f);
        breakdownLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
        breakdownLabel.enableWordWrapping = true;
        breakdownLabel.richText = true;
        cancelButton = UiBuilder.CreateButton("Cancel", t, "Cancel", ButtonSize, () => _ = plugin.PickPlace?.CancelAsync());

        Subscribe();
        RefreshAll();
    }

    public RectTransform Root => root;
    public int InteractiveElementCount => UiBuilder.CountInteractive(root);
    public IReadOnlyList<Button> SolutionButtons => solutionButtons;
    public string StatusText => statusLabel.text;
    public string StagesText => stagesLabel.text;
    public string BreakdownText => breakdownLabel.text;

    public void Dispose()
    {
        var c = plugin.PickPlace;
        if (c != null)
        {
            c.OnDescriptionChanged -= RefreshTaskAndStages;
            c.OnStatisticsChanged -= RefreshStages;
            c.OnSolutionIdsChanged -= RefreshSolutions;
            c.OnTaskReset -= RefreshAll;
            c.OnStatus -= OnStatus;
            c.OnPhaseChanged -= RefreshBusy;
            c.OnExecutionFeedback -= OnExecutionFeedback;
        }
        plugin.SelectedSolutionChanged -= RefreshSelection;
        plugin.TeachModeChanged -= RefreshRecord;
        if (plugin.Recorder != null) plugin.Recorder.OnRecordingComplete -= OnRecorded;
    }

    private void Subscribe()
    {
        var c = plugin.PickPlace;
        if (c != null)
        {
            c.OnDescriptionChanged += RefreshTaskAndStages;
            c.OnStatisticsChanged += RefreshStages;
            c.OnSolutionIdsChanged += RefreshSolutions;
            c.OnTaskReset += RefreshAll;
            c.OnStatus += OnStatus;
            c.OnPhaseChanged += RefreshBusy;
            c.OnExecutionFeedback += OnExecutionFeedback;
        }
        plugin.SelectedSolutionChanged += RefreshSelection;
        plugin.TeachModeChanged += RefreshRecord;
        if (plugin.Recorder != null) plugin.Recorder.OnRecordingComplete += OnRecorded;
    }

    // --- record = plan (Teach mode) -------------------------------------------------

    private void OnRecord()
    {
        var recorder = plugin.Recorder;
        if (recorder == null || !plugin.InTeachMode) return;
        if (!recorder.IsRecording) { recorder.StartRecording(); statusLabel.text = "Grab and place the object..."; }
        else { recorder.StopRecording(); if (!Busy) statusLabel.text = "Idle"; }
        RefreshRecord();
    }

    private void OnRecorded(string objectId)
    {
        if (!Busy) statusLabel.text = $"Sent: {objectId}";
        RefreshRecord();
    }

    private bool Busy => plugin.PickPlace != null && plugin.PickPlace.Busy;

    private void RefreshRecord()
    {
        var recorder = plugin.Recorder;
        bool teach = plugin.InTeachMode;
        // One task at a time: a goal sent while the server is busy is rejected, so no new plan until this one ends.
        UiBuilder.SetInteractable(recordButton, teach && recorder != null && !Busy);
        UiBuilder.SetButtonText(recordButton,
            recorder == null ? "No recorder" :
            !teach ? "Record (Teach mode)" :
            Busy ? "Busy..." :
            recorder.IsRecording ? "Stop recording" : "Record pick & place");
    }

    private void RefreshBusy()
    {
        RefreshRecord();
        UiBuilder.SetInteractable(cancelButton, Busy);
        UiBuilder.SetButtonText(cancelButton,
            plugin.PickPlace?.Phase == PickPlacePhase.Planning ? "Cancel planning" :
            plugin.PickPlace?.Phase == PickPlacePhase.Executing ? "Cancel execution" : "Cancel");
        RefreshStages();
        RefreshBreakdown();
    }

    // --- task / stages ------------------------------------------------------------

    private void OnStatus(string status) => statusLabel.text = status;

    private void OnExecutionFeedback(RosMessageTypes.StudyInterfaces.ExecuteSolutionFeedback _)
    {
        RefreshStages();
        RefreshBreakdown();
    }

    private void RefreshAll()
    {
        RefreshTask(); RefreshStages(); RefreshSolutions(); RefreshBusy();
    }

    private void RefreshTaskAndStages() { RefreshTask(); RefreshStages(); }

    private void RefreshTask()
    {
        string id = plugin.PickPlace?.TaskId;
        string name = plugin.PickPlace?.Description?.stages?.FirstOrDefault(s => s.id == PickPlaceClient.RootStageId)?.name;
        taskLabel.text = string.IsNullOrEmpty(id) ? "No task" : string.IsNullOrEmpty(name) ? $"Task {id}" : name;
    }

    /// <summary>Stage being executed: the one after the last finished sub-trajectory, or null.</summary>
    private uint? ActiveStageId()
    {
        var c = plugin.PickPlace;
        if (c == null || c.Phase != PickPlacePhase.Executing) return null;
        var steps = plugin.SelectedSolution?.sub_trajectory;
        if (steps == null || steps.Length == 0 || c.ExecutingSolutionId != plugin.SelectedSolutionId)
            return c.LastExecutionFeedback?.stage_id;
        int current = ActiveStep(c, steps.Length);
        return current < steps.Length ? steps[current].info.stage_id : (uint?)null;
    }

    // Feedback reports the step that just finished, so the running one is the next.
    private static int ActiveStep(PickPlaceClient c, int stepCount) =>
        c.LastExecutionFeedback == null ? 0 : (int)Math.Min(c.LastExecutionFeedback.sub_id + 1, (uint)stepCount);

    private void RefreshStages()
    {
        var desc = plugin.PickPlace?.Description;
        var stats = plugin.PickPlace?.Statistics;
        if (desc?.stages == null || desc.stages.Length == 0) { stagesLabel.text = ""; return; }

        var statsById = new Dictionary<uint, StageStatisticsMsg>();
        if (stats?.stages != null) foreach (var s in stats.stages) statsById[s.id] = s;
        var children = new Dictionary<uint, List<StageDescriptionMsg>>();
        var ids = new HashSet<uint>(desc.stages.Select(s => s.id));
        var roots = new List<StageDescriptionMsg>();
        foreach (var stage in desc.stages)
        {
            // Stage 0 (the Task wrapper) is never published, so the root container's parent is absent.
            if (stage.id == stage.parent_id || !ids.Contains(stage.parent_id)) { roots.Add(stage); continue; }
            if (!children.TryGetValue(stage.parent_id, out var list)) children[stage.parent_id] = list = new List<StageDescriptionMsg>();
            list.Add(stage);
        }
        var sb = new StringBuilder();
        uint? active = ActiveStageId();
        foreach (var rootStage in roots) AppendStage(sb, rootStage, children, statsById, 0, active);
        stagesLabel.text = sb.ToString().TrimEnd();
    }

    private static void AppendStage(StringBuilder sb, StageDescriptionMsg stage, Dictionary<uint, List<StageDescriptionMsg>> children,
                                    Dictionary<uint, StageStatisticsMsg> statsById, int depth, uint? active)
    {
        statsById.TryGetValue(stage.id, out var s);
        bool isActive = active == stage.id;
        sb.Append(' ', depth * 2);
        if (isActive) sb.Append(ActiveMark).Append("▶ ");
        sb.Append(stage.name);
        if (isActive) sb.Append(ActiveEnd);
        sb.Append($"  ✓{s?.solved?.Length ?? 0}  ✗{s?.num_failed ?? 0}  {s?.total_compute_time ?? 0:F1}s\n");
        if (children.TryGetValue(stage.id, out var kids))
            foreach (var k in kids) AppendStage(sb, k, children, statsById, depth + 1, active);
    }

    // --- solutions ----------------------------------------------------------------

    private void RefreshSolutions()
    {
        foreach (var b in solutionButtons) if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        solutionButtons.Clear();
        idByButton.Clear();

        var ids = plugin.PickPlace?.SolutionIds ?? Array.Empty<uint>();
        int rank = 1;
        foreach (uint id in ids.Take(MaxSolutionButtons))
        {
            uint captured = id;
            var button = UiBuilder.CreateButton($"Solution{rank}", solutionsRow, $"#{rank}  solution {id}", ButtonSize,
                () => plugin.SelectSolution(captured));
            solutionButtons.Add(button);
            idByButton[button] = id;
            rank++;
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        uint? selected = plugin.SelectedSolutionId;
        foreach (var (button, id) in idByButton.Select(kv => (kv.Key, kv.Value)))
        {
            var image = button.GetComponent<Image>();
            if (image != null) image.color = id == selected ? SelectedColor : IdleColor;
        }
        RefreshBreakdown();
        RefreshStages();
    }

    private void RefreshBreakdown()
    {
        var selected = plugin.SelectedSolution;
        breakdownLabel.text = selected != null ? Breakdown(selected) :
            plugin.SelectedSolutionId != null ? $"Fetching solution {plugin.SelectedSolutionId}..." : "";
    }

    // One line per sub-trajectory, in execution order: sub_id from the feedback indexes this list.
    private string Breakdown(SolutionMsg sol)
    {
        var c = plugin.PickPlace;
        var steps = sol.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>();
        bool tracking = c != null && c.ExecutingSolutionId == plugin.SelectedSolutionId &&
                        (c.Phase == PickPlacePhase.Executing || c.LastExecutionFeedback != null);
        int done = !tracking || c.LastExecutionFeedback == null ? 0 : ActiveStep(c, steps.Length);
        bool running = tracking && c.Phase == PickPlacePhase.Executing;

        var sb = new StringBuilder();
        sb.Append($"Solution {plugin.SelectedSolutionId}  cost {steps.Sum(s => s.info.cost):F3}  {steps.Length} steps\n");
        for (int i = 0; i < steps.Length; i++)
        {
            var info = steps[i].info;
            string name = c?.StageName(info.stage_id) ?? $"Stage {info.stage_id}";
            int pts = steps[i].trajectory?.joint_trajectory?.points?.Length ?? 0;
            bool isActive = running && i == done;
            if (isActive) sb.Append(ActiveMark);
            sb.Append(tracking && i < done ? "✓ " : isActive ? "▶ " : "  ");
            sb.Append($"{i + 1}. {name}  cost={info.cost:F3}  pts={pts}");
            if (!string.IsNullOrEmpty(info.planner_id)) sb.Append($"  [{info.planner_id}]");
            if (isActive) sb.Append(ActiveEnd);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}
