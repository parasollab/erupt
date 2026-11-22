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

namespace Erupt.Plugins.Mtc
{
    /// <summary>
    /// Tier 3 "MTC" tab: the solution browser and the execution panel. Stage tree with per-stage
    /// counts, the current task's solution ids, the steps of the selected one (highlighted as
    /// execution feedback arrives), the preview scope (whole solution or one stage of it), the
    /// scoped stage's partial / failed attempts (cycling to one previews it from its start scene),
    /// and the pick/place recorder entry, which is the plan button.
    /// Interactive elements: record + up to three solution buttons + scope + attempt + cancel = 7
    /// (Part 8); three buttons cover a default plan (<c>PickPlaceClient.maxSolutions</c> = 3).
    /// Preview and execute are tier 2 verbs on the selected solution's handle, not buttons here.
    /// The tab shows the robot that was active when it was built (the plugin builds a new one
    /// when the active robot changes); a robot whose ROS side offers no pick-and-place action
    /// reads <see cref="UnavailableText"/> and cannot record.
    /// </summary>
    public sealed class SolutionsTab
    {
        public const int MaxSolutionButtons = 3;
        private static readonly Vector2 ButtonSize = new(300f, 56f);
        private static readonly Color SelectedColor = new(0.18f, 0.32f, 0.18f, 0.94f);
        private static readonly Color IdleColor = new(0.16f, 0.17f, 0.20f, 0.94f);
        private const string ActiveMark = "<color=#7CFC7C><b>";
        private const string ActiveEnd = "</b></color>";

        /// <summary>Status shown for a robot whose ROS side has no pick-and-place server (or is not connected yet).</summary>
        public const string UnavailableText = "Pick-and-place not available for this robot";

        private readonly MtcPlugin plugin;
        // The active robot's client and recorder when the tab was built: what it subscribed to.
        private readonly PickPlaceClient client;
        private readonly PickPlaceTaskRecorder recorder;
        private readonly RectTransform root;
        private readonly Button recordButton, cancelButton, scopeButton, attemptButton;
        private readonly TMPro.TextMeshProUGUI taskLabel, stagesLabel, statusLabel, breakdownLabel;
        private readonly Transform solutionsRow;
        private readonly List<Button> solutionButtons = new();
        private readonly Dictionary<Button, uint> idByButton = new();
        private readonly List<MtcPlugin.StageAttempt> attempts = new();
        private int attemptIndex = -1;

        public SolutionsTab(MtcPlugin plugin, RectTransform content)
        {
            this.plugin = plugin;
            client = plugin.PickPlace;
            recorder = plugin.Recorder;
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
            scopeButton = UiBuilder.CreateButton("Scope", t, "Preview: whole solution", ButtonSize, NextScope);
            attemptButton = UiBuilder.CreateButton("Attempt", t, "Attempt: —", ButtonSize, NextAttempt);
            breakdownLabel = UiBuilder.CreateLabel("Breakdown", t, "", 18f);
            breakdownLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
            breakdownLabel.enableWordWrapping = true;
            breakdownLabel.richText = true;
            cancelButton = UiBuilder.CreateButton("Cancel", t, "Cancel", ButtonSize, () => _ = client?.CancelAsync());

            Subscribe();
            RefreshAll();
        }

        public RectTransform Root => root;
        public int InteractiveElementCount => UiBuilder.CountInteractive(root);
        public IReadOnlyList<Button> SolutionButtons => solutionButtons;
        public Button ScopeButton => scopeButton;
        public Button AttemptButton => attemptButton;
        public string StatusText => statusLabel.text;
        public string StagesText => stagesLabel.text;
        public string BreakdownText => breakdownLabel.text;

        public void Dispose()
        {
            var c = client;
            if (c != null)
            {
                c.OnDescriptionChanged -= RefreshTaskAndStages;
                c.OnStatisticsChanged -= OnStatistics;
                c.OnSolutionIdsChanged -= RefreshSolutions;
                c.OnTaskReset -= RefreshAll;
                c.OnStatus -= OnStatus;
                c.OnPhaseChanged -= RefreshBusy;
                c.OnExecutionFeedback -= OnExecutionFeedback;
            }
            plugin.SelectedSolutionChanged -= RefreshSelection;
            plugin.PreviewScopeChanged -= RefreshScope;
            plugin.PreviewStatusChanged -= OnPreviewStatus;
            if (recorder != null) recorder.OnRecordingComplete -= OnRecorded;
        }

        private void Subscribe()
        {
            var c = client;
            if (c != null)
            {
                c.OnDescriptionChanged += RefreshTaskAndStages;
                c.OnStatisticsChanged += OnStatistics;
                c.OnSolutionIdsChanged += RefreshSolutions;
                c.OnTaskReset += RefreshAll;
                c.OnStatus += OnStatus;
                c.OnPhaseChanged += RefreshBusy;
                c.OnExecutionFeedback += OnExecutionFeedback;
            }
            plugin.SelectedSolutionChanged += RefreshSelection;
            plugin.PreviewScopeChanged += RefreshScope;
            plugin.PreviewStatusChanged += OnPreviewStatus;
            if (recorder != null) recorder.OnRecordingComplete += OnRecorded;
        }

        // --- record = plan ---------------------------------------------------------------

        private void OnRecord()
        {
            if (recorder == null) return;
            if (!Available && !recorder.IsRecording) { statusLabel.text = UnavailableText; return; }
            if (!recorder.IsRecording) { recorder.StartRecording(); statusLabel.text = "Grab and place the object..."; }
            else { recorder.StopRecording(); if (!Busy) statusLabel.text = "Idle"; }
            RefreshRecord();
        }

        private void OnRecorded(string objectId)
        {
            if (!Busy) statusLabel.text = $"Sent: {objectId}";
            RefreshRecord();
        }

        private bool Busy => client != null && client.Busy;

        // False while this robot's ROS side offers no pick-and-place action.
        private bool Available => client != null && client.Available;

        private void RefreshRecord()
        {
            bool recording = recorder != null && recorder.IsRecording;
            // One task at a time: a goal sent while the server is busy is rejected, so no new plan until this one ends.
            // A recording in progress can always be stopped, even if the server has gone away meanwhile.
            UiBuilder.SetInteractable(recordButton, recorder != null && !Busy && (Available || recording));
            UiBuilder.SetButtonText(recordButton,
                recorder == null ? "No recorder" :
                Busy ? "Busy..." :
                recording ? "Stop recording" :
                !Available ? "Record (not available)" : "Record pick & place");
            if (!Available && !Busy && !recording) statusLabel.text = UnavailableText;
        }

        private void RefreshBusy()
        {
            RefreshRecord();
            UiBuilder.SetInteractable(cancelButton, Busy);
            UiBuilder.SetButtonText(cancelButton,
                client?.Phase == PickPlacePhase.Planning ? "Cancel planning" :
                client?.Phase == PickPlacePhase.Executing ? "Cancel execution" : "Cancel");
            RefreshStages();
            RefreshBreakdown();
            RefreshAttempts();
        }

        // --- task / stages ------------------------------------------------------------

        // The client's status line, except that a robot without a pick-and-place action says so
        // in plain words instead of the transport's state.
        private void OnStatus(string status)
        {
            statusLabel.text = !Available && !Busy ? UnavailableText : status;
            RefreshRecord();
        }

        private void OnStatistics() { RefreshStages(); RefreshAttempts(); }

        private void OnExecutionFeedback(RosMessageTypes.EruptInterfaces.ExecuteSolutionFeedback _)
        {
            RefreshStages();
            RefreshBreakdown();
        }

        private void RefreshAll()
        {
            RefreshTask(); RefreshStages(); RefreshSolutions(); RefreshScope(); RefreshBusy();
        }

        private void OnPreviewStatus()
        {
            if (!string.IsNullOrEmpty(plugin.PreviewStatus)) statusLabel.text = plugin.PreviewStatus;
        }

        private void RefreshTaskAndStages() { RefreshTask(); RefreshStages(); }

        private void RefreshTask()
        {
            string id = client?.TaskId;
            string name = client?.Description?.stages?.FirstOrDefault(s => s.id == PickPlaceClient.RootStageId)?.name;
            taskLabel.text = string.IsNullOrEmpty(id) ? "No task" : string.IsNullOrEmpty(name) ? $"Task {id}" : name;
        }

        /// <summary>Stage being executed: the one after the last finished sub-trajectory, or null.</summary>
        private uint? ActiveStageId()
        {
            var c = client;
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
            var desc = client?.Description;
            var stats = client?.Statistics;
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

            var ids = client?.SolutionIds ?? Array.Empty<uint>();
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

        // --- preview scope and stage attempts ---------------------------------------------

        /// <summary>Cycle the preview verb's scope: the whole solution, then each stage with steps in it.</summary>
        private void NextScope()
        {
            var stages = plugin.PreviewableStages;
            if (stages.Count == 0) { plugin.SetPreviewStage(null); return; }
            int index = plugin.PreviewStageId is uint current ? stages.ToList().IndexOf(current) : -1;
            index = index + 1 >= stages.Count ? -1 : index + 1;
            plugin.SetPreviewStage(index < 0 ? null : stages[index]);
        }

        private void RefreshScope()
        {
            var c = client;
            string text = "Preview: whole solution";
            if (plugin.PreviewStageId is uint stage)
            {
                string name = c?.StageName(stage) ?? $"stage {stage}";
                text = plugin.TryGetStageSteps(stage, out int first, out int last)
                    ? $"Preview: {name} ({(first == last ? $"step {first + 1}" : $"steps {first + 1}-{last + 1}")})"
                    : $"Preview: {name}";
            }
            UiBuilder.SetButtonText(scopeButton, text);
            UiBuilder.SetInteractable(scopeButton, plugin.PreviewableStages.Count > 0);
            RefreshAttempts();
        }

        /// <summary>Cycle through the scoped stage's partial / failed attempts; each one is previewed as it comes up.</summary>
        private void NextAttempt()
        {
            if (attempts.Count == 0) return;
            attemptIndex = (attemptIndex + 1) % attempts.Count;
            var attempt = attempts[attemptIndex];
            RefreshAttemptText();
            plugin.PreviewStageSolution(attempt.Id, attempt.Failed);
        }

        // The attempts of the scoped stage (the task's root stage when the scope is the whole
        // solution, so its failed complete attempts can be read). Reset when the list changes.
        private void RefreshAttempts()
        {
            var fresh = plugin.AttemptsOf(plugin.PreviewStageId ?? PickPlaceClient.RootStageId);
            if (fresh.Count != attempts.Count || fresh.Where((a, i) => a.Id != attempts[i].Id || a.Failed != attempts[i].Failed).Any())
            {
                attempts.Clear();
                attempts.AddRange(fresh);
                attemptIndex = -1;
            }
            RefreshAttemptText();
            UiBuilder.SetInteractable(attemptButton, attempts.Count > 0 && client?.Phase != PickPlacePhase.Executing);
        }

        private void RefreshAttemptText()
        {
            string text = attempts.Count == 0 ? "Attempt: —" :
                attemptIndex < 0 ? $"Attempt: {attempts.Count} to preview" :
                attempts[attemptIndex].Failed ? $"Attempt: ✗ #{attempts[attemptIndex].Id} failed ({attemptIndex + 1}/{attempts.Count})"
                                              : $"Attempt: #{attempts[attemptIndex].Id} partial ({attemptIndex + 1}/{attempts.Count})";
            UiBuilder.SetButtonText(attemptButton, text);
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
            var c = client;
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
}
