using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Erupt.Plugins;

/// <summary>
/// The legacy UI Toolkit planning panel, now a view over <see cref="MoveItPlugin"/>: it
/// reads planner listings from the client, captures start/goal through the plugin, and
/// drives plan / preview / execute. No ROS, no robot access. Retired in Phase 3 when the
/// tier 3 planner tab replaces it.
/// </summary>
public class MoveItPlanningRequestMenuUI : MonoBehaviour
{
    [Tooltip("Found in the scene if empty.")]
    [SerializeField] private MoveItPlugin plugin;
    [SerializeField] private UIDocument uiDocument;
    [Tooltip("Optional: start/goal ghosts, as the legacy panel showed them.")]
    [SerializeField] private SpawnGhosts ghostSpawner;

    private VisualElement root;
    private Button setStartStateButton, setGoalStateButton, planningRequestButton;
    private Button stopReplayButton, executeTrajectoryButton, mirrorButton;
    private DropdownField plannerPipelineDropdown, plannerDropdown;
    private IntegerField numPlanningAttemptsField;
    private FloatField allowedPlanningTimeField;
    private Label planningResultLabel;

    private bool startSet, goalSet;
    private PlanResult lastPlan;
    private bool bound;

    private void OnEnable()
    {
        if (plugin == null) plugin = FindFirstObjectByType<MoveItPlugin>(FindObjectsInactive.Include);
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        root = uiDocument?.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: No UIDocument/rootVisualElement found.");
            return;
        }
        if (plugin == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: No MoveItPlugin in the scene.");
            return;
        }

        InitializeUIElements();
        SetupEventHandlers();
        BindPlugin();
    }

    private void OnDisable() => UnbindPlugin();

    private void BindPlugin()
    {
        if (bound) return;
        bound = true;
        plugin.PlanProduced += OnPlanProduced;
        plugin.PlanFailed += OnPlanFailed;
        if (plugin.Client != null) plugin.Client.PlannersUpdated += OnPlannersUpdated;
        else StartCoroutine(BindClientWhenReady());
    }

    private System.Collections.IEnumerator BindClientWhenReady()
    {
        while (plugin != null && plugin.Client == null) yield return null;
        if (plugin?.Client == null) yield break;
        plugin.Client.PlannersUpdated += OnPlannersUpdated;
        if (plugin.Client.Planners.Count > 0) OnPlannersUpdated(plugin.Client.Planners);
    }

    private void UnbindPlugin()
    {
        if (!bound || plugin == null) return;
        bound = false;
        plugin.PlanProduced -= OnPlanProduced;
        plugin.PlanFailed -= OnPlanFailed;
        if (plugin.Client != null) plugin.Client.PlannersUpdated -= OnPlannersUpdated;
    }

    private void InitializeUIElements()
    {
        planningResultLabel = root.Q<Label>("planningResultLabel");
        setStartStateButton = root.Q<Button>("planningRequestSetStartButton");
        setGoalStateButton = root.Q<Button>("planningRequestSetGoalStateButton");
        plannerPipelineDropdown = root.Q<DropdownField>("planningRequestPlannerPipelineIDDropDown");
        plannerDropdown = root.Q<DropdownField>("planningRequestPlannerIDDropDown");
        numPlanningAttemptsField = root.Q<IntegerField>("planningRequestNumPlanningAttemptsInput");
        allowedPlanningTimeField = root.Q<FloatField>("planningRequestAllowedPlanningTimeInput");
        planningRequestButton = root.Q<Button>("planningRequestSendButton");
        stopReplayButton = root.Q<Button>("planningRequestStopReplayButton");
        executeTrajectoryButton = root.Q<Button>("planningRequestExecuteTrajectoryButton");
        mirrorButton = root.Q<Button>("mirrorJointStateButton");

        if (setStartStateButton == null || setGoalStateButton == null ||
            plannerPipelineDropdown == null || plannerDropdown == null ||
            numPlanningAttemptsField == null || allowedPlanningTimeField == null ||
            planningRequestButton == null || stopReplayButton == null ||
            mirrorButton == null || executeTrajectoryButton == null ||
            planningResultLabel == null)
        {
            Debug.LogError("MoveItPlanningRequestMenuUI: One or more UI elements not found in UXML.");
            return;
        }

        plannerPipelineDropdown.choices = new List<string>();
        plannerPipelineDropdown.value = "";
        plannerDropdown.choices = new List<string>();
        plannerDropdown.value = "";

        var s = plugin.Settings;
        numPlanningAttemptsField.value = s.defaultNumPlanningAttempts;
        allowedPlanningTimeField.value = s.defaultAllowedPlanningTime;
        stopReplayButton.SetEnabled(false);
        executeTrajectoryButton.SetEnabled(false);
        UpdateButtonStates();
    }

    private void SetupEventHandlers()
    {
        setStartStateButton.clicked += OnSetStartStateClicked;
        setGoalStateButton.clicked += OnSetGoalStateClicked;
        plannerPipelineDropdown.RegisterValueChangedCallback(OnPipelineSelectionChanged);
        planningRequestButton.clicked += SendPlanningRequest;
        stopReplayButton.clicked += () =>
        {
            if (plugin.IsPreviewing) { plugin.StopPreview(); stopReplayButton.text = "Start Replay"; }
            else if (lastPlan != null) { plugin.Preview(lastPlan); stopReplayButton.text = "Stop Replay"; }
        };
        mirrorButton.clicked += ToggleMirroring;
        executeTrajectoryButton.clicked += ExecuteTrajectory;
    }

    // Callbacks may arrive off the panel's schedule; hop onto it.
    private void UI(Action a)
    {
        if (root != null) root.schedule.Execute(() => a()).ExecuteLater(0);
        else a();
    }

    private void OnPlannersUpdated(IReadOnlyList<PlannerListing> planners)
    {
        UI(() =>
        {
            var pipelines = planners.Select(p => p.PipelineId).ToList();
            plannerPipelineDropdown.choices = pipelines;

            string chosen = plugin.Settings.planningPipelineId;
            if (!pipelines.Contains(chosen)) chosen = pipelines.Count > 0 ? pipelines[0] : "";
            plannerPipelineDropdown.SetValueWithoutNotify(chosen);
            FillPlanners(chosen);
        });
    }

    private void OnPipelineSelectionChanged(ChangeEvent<string> evt) => FillPlanners(evt.newValue);

    private void FillPlanners(string pipeline)
    {
        string[] planners = plugin.Client?.PlannersFor(pipeline) ?? Array.Empty<string>();
        plannerDropdown.choices = planners.ToList();
        string def = plugin.Settings.defaultPlannerId;
        plannerDropdown.SetValueWithoutNotify(planners.Contains(def) ? def : planners.Length > 0 ? planners[0] : "");
    }

    private void ToggleMirroring()
    {
        if (plugin.Client == null) return;
        bool on = !plugin.Client.IsMirroring;
        plugin.Client.SetMirroring(on);
        mirrorButton.text = on ? "Stop Mirroring" : "Mirror Joint States";
    }

    private void OnSetStartStateClicked()
    {
        if (ghostSpawner != null) { if (!startSet) ghostSpawner.SpawnStartGhost(); else ghostSpawner.UpdateStartGhost(); }
        startSet = true;
        plugin.SetStartFromRobot();
        UpdateButtonStates();
    }

    private void OnSetGoalStateClicked()
    {
        if (ghostSpawner != null) { if (!goalSet) ghostSpawner.SpawnGoalGhost(); else ghostSpawner.UpdateGoalGhost(); }
        goalSet = true;
        plugin.SetGoalFromRobot();
        UpdateButtonStates();
    }

    public void SendPlanningRequest()
    {
        if (!plugin.HasStart || !plugin.HasGoal)
        {
            planningResultLabel.text = "Set both start and goal states before planning.";
            return;
        }
        stopReplayButton.SetEnabled(false);
        executeTrajectoryButton.SetEnabled(false);

        plugin.RequestPlan(new PlanPreferences
        {
            PipelineId = plannerPipelineDropdown.value,
            PlannerId = plannerDropdown.value,
            Attempts = numPlanningAttemptsField.value,
            AllowedTimeSeconds = allowedPlanningTimeField.value
        }, _ => { });
    }

    private void OnPlanProduced(PlanResult plan)
    {
        UI(() =>
        {
            lastPlan = plan;
            int points = plan.Trajectory?.points?.Length ?? 0;
            planningResultLabel.text = $"Planning successful! Time: {plan.PlanningTimeSeconds}s, Waypoints: {points}";
            plugin.Preview(plan);
            stopReplayButton.text = "Stop Replay";
            stopReplayButton.SetEnabled(true);
            executeTrajectoryButton.SetEnabled(true);
        });
    }

    private void OnPlanFailed(string message)
    {
        UI(() =>
        {
            lastPlan = null;
            planningResultLabel.text = $"Planning failed with error: {message}";
            stopReplayButton.SetEnabled(false);
            executeTrajectoryButton.SetEnabled(false);
        });
    }

    private void ExecuteTrajectory()
    {
        if (lastPlan == null) { planningResultLabel.text = "No planned trajectory to execute."; return; }
        plugin.Execute(lastPlan, status => UI(() => planningResultLabel.text = status.ToString()));
        mirrorButton.text = "Stop Mirroring";
    }

    private void UpdateButtonStates()
    {
        setStartStateButton.text = plugin.HasStart ? "Start State ✓" : "Set Start State";
        setGoalStateButton.text = plugin.HasGoal ? "Goal State ✓" : "Set Goal State";
    }

    public void ResetPlanningState()
    {
        startSet = goalSet = false;
        plugin.ResetPlanningState();
        UpdateButtonStates();
    }
}
