using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Erupt.Plugins;
using Erupt.Ui;

/// <summary>
/// Tier 3 "Planner settings" tab for MoveIt (Guidelines Part 2 tier 3 inventory). Seven
/// interactive elements at most, per the Part 8 design review: pipeline, planner,
/// attempts, allowed time, mirror toggle, plan, reset. Start and goal are not here —
/// they come from the end effector's tier 2 verbs.
/// </summary>
public sealed class PlannerSettingsTab
{
    private static readonly int[] AttemptChoices = { 1, 5, 10, 20 };
    private static readonly float[] TimeChoices = { 1f, 2f, 5f, 10f };
    private static readonly Vector2 ButtonSize = new(300f, 60f);

    private readonly MoveItPlugin plugin;
    private readonly Button pipelineButton, plannerButton, planButton, resetButton;
    private readonly TMPro.TextMeshProUGUI status;
    private readonly RectTransform root;

    private string[] pipelines = Array.Empty<string>();
    private string[] planners = Array.Empty<string>();
    private int pipelineIndex = -1, plannerIndex = -1, attemptsIndex = 2, timeIndex = 2;

    public PlannerSettingsTab(MoveItPlugin plugin, RectTransform content)
    {
        this.plugin = plugin;
        root = content;

        var column = UiBuilder.CreateColumn("Planner", content, 8f);
        UiBuilder.Stretch(column.GetComponent<RectTransform>());
        Transform t = column.transform;

        // Pipeline and planner are rebuilt when the query answers; until then they cycle over nothing.
        pipelineButton = UiBuilder.CreateButton("Pipeline", t, "Pipeline: —", ButtonSize, NextPipeline);
        plannerButton = UiBuilder.CreateButton("Planner", t, "Planner: —", ButtonSize, NextPlanner);
        UiBuilder.CreateCycle("Attempts", t, "Attempts", AttemptChoices.Select(a => a.ToString()).ToArray(), attemptsIndex, ButtonSize, i => attemptsIndex = i);
        UiBuilder.CreateCycle("Time", t, "Time", TimeChoices.Select(s => $"{s:0.#}s").ToArray(), timeIndex, ButtonSize, i => timeIndex = i);
        UiBuilder.CreateToggle("Mirror", t, "Mirror joints", false, ButtonSize, on => plugin.Client?.SetMirroring(on));
        planButton = UiBuilder.CreateButton("Plan", t, "Plan", ButtonSize, () => plugin.RequestPlan(Preferences, _ => { }));
        resetButton = UiBuilder.CreateButton("Reset", t, "Reset start/goal", ButtonSize, () => { plugin.ResetPlanningState(); Refresh(); });
        status = UiBuilder.CreateLabel("Status", t, "No start/goal set", 22f);

        SeedFromSettings();
        Refresh();

        plugin.PlanProduced += _ => Refresh();
        plugin.PlanFailed += m => { status.text = $"Planning failed: {m}"; };
        plugin.GoalChanged += Refresh;
        if (plugin.Client != null) plugin.Client.PlannersUpdated += OnPlanners;
    }

    public RectTransform Root => root;
    public int InteractiveElementCount => UiBuilder.CountInteractive(root);

    /// <summary>What the plan button (and the tier 2 plan verb) sends.</summary>
    public PlanPreferences Preferences => new PlanPreferences
    {
        PipelineId = pipelineIndex >= 0 ? pipelines[pipelineIndex] : plugin.Settings.planningPipelineId,
        PlannerId = plannerIndex >= 0 ? planners[plannerIndex] : plugin.Settings.defaultPlannerId,
        Attempts = AttemptChoices[attemptsIndex],
        AllowedTimeSeconds = TimeChoices[timeIndex]
    };

    public void Dispose()
    {
        if (plugin == null) return;
        plugin.GoalChanged -= Refresh;
        if (plugin.Client != null) plugin.Client.PlannersUpdated -= OnPlanners;
    }

    private void SeedFromSettings()
    {
        var s = plugin.Settings;
        attemptsIndex = Mathf.Max(0, Array.IndexOf(AttemptChoices, s.defaultNumPlanningAttempts));
        timeIndex = Mathf.Max(0, Array.IndexOf(TimeChoices, s.defaultAllowedPlanningTime));
        if (plugin.Client != null && plugin.Client.Planners.Count > 0) OnPlanners(plugin.Client.Planners);
    }

    private void OnPlanners(IReadOnlyList<PlannerListing> listings)
    {
        pipelines = listings.Select(l => l.PipelineId).ToArray();
        pipelineIndex = Math.Max(0, Array.IndexOf(pipelines, plugin.Settings.planningPipelineId));
        if (pipelines.Length == 0) pipelineIndex = -1;
        FillPlanners();
        Refresh();
    }

    private void FillPlanners()
    {
        planners = pipelineIndex >= 0 ? plugin.Client.PlannersFor(pipelines[pipelineIndex]) : Array.Empty<string>();
        plannerIndex = planners.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(planners, plugin.Settings.defaultPlannerId));
    }

    private void NextPipeline()
    {
        if (pipelines.Length == 0) return;
        pipelineIndex = (pipelineIndex + 1) % pipelines.Length;
        FillPlanners();
        Refresh();
    }

    private void NextPlanner()
    {
        if (planners.Length == 0) return;
        plannerIndex = (plannerIndex + 1) % planners.Length;
        Refresh();
    }

    private void Refresh()
    {
        UiBuilder.SetButtonText(pipelineButton, $"Pipeline: {(pipelineIndex >= 0 ? pipelines[pipelineIndex] : "—")}");
        UiBuilder.SetButtonText(plannerButton, $"Planner: {(plannerIndex >= 0 ? planners[plannerIndex] : "—")}");
        bool ready = plugin.HasStart && plugin.HasGoal;
        UiBuilder.SetInteractable(planButton, ready);
        UiBuilder.SetInteractable(resetButton, plugin.HasStart || plugin.HasGoal);
        if (plugin.LastResult != null)
            status.text = $"Plan: {plugin.LastResult.Trajectory?.points?.Length ?? 0} waypoints in {plugin.LastResult.PlanningTimeSeconds:0.00}s";
        else
            status.text = ready ? "Start and goal set"
                        : plugin.HasGoal ? "Goal set — use Set Start on the end effector"
                        : plugin.HasStart ? "Start set — use Set Goal on the end effector"
                        : "Use Set Start and Set Goal on the end effector";
    }
}
