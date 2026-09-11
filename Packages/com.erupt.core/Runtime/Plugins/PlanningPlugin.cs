using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// Template for a planner: set a goal, plan, preview, execute. The base class owns the
    /// UI contributions so every planner shows up the same way (Guidelines Part 2):
    /// <c>set-goal</c> / <c>plan</c> on the end effector, <c>preview</c> / <c>execute</c>
    /// on a trajectory, and one tier 3 tab <c>planner-{Id}</c> for settings.
    /// </summary>
    public abstract class PlanningPlugin : EruptPluginBehaviour
    {
        private readonly List<TrajectorySelectable> results = new();
        private PanelTab settingsTab;

        /// <summary>The most recent plan, or null.</summary>
        public PlanResult LastResult { get; private set; }
        public IReadOnlyList<TrajectorySelectable> Results => results;
        public PanelTab SettingsTab => settingsTab;
        public string SettingsTabId => $"planner-{Id}";

        /// <summary>Fires whenever a plan arrives (after its selectable exists).</summary>
        public event Action<PlanResult> PlanProduced;

        // --- template ----------------------------------------------------------

        public abstract InteractionRefusal SetGoal(ISelectable endEffector);
        public abstract void RequestPlan(PlanPreferences preferences, Action<PlanResult> done);
        public abstract void Preview(PlanResult plan);
        public abstract void StopPreview();
        public abstract void Execute(PlanResult plan, Action<ExecutionStatus> status);

        /// <summary>Fill the settings tab. Default: nothing.</summary>
        protected virtual void BuildSettingsTab(RectTransform content) { }

        /// <summary>Preferences used when a verb triggers a plan; override to read the tab.</summary>
        protected virtual PlanPreferences DefaultPreferences() => new PlanPreferences();

        // --- base contributions ------------------------------------------------

        protected override void OnRegister(IEruptContext context)
        {
            IUiHost ui = context.Ui;
            if (ui == null)
            {
                Debug.LogWarning($"[{Id}] No UI host; planner verbs and tab not contributed.", this);
                return;
            }

            ui.BindVerb("set-goal", target => Report(SetGoal(target)));
            if (!ui.Verbs.Contains(SelectionKind.EndEffector, "plan"))
                ui.RegisterVerb(SelectionKind.EndEffector, "plan", "Plan", _ => RequestPlan(DefaultPreferences(), _ => { }), Id);
            else
                ui.BindVerb("plan", _ => RequestPlan(DefaultPreferences(), _ => { }));
            ui.BindVerb("preview", target => { if (TryResult(target, out var r)) Preview(r); });
            ui.BindVerb("execute", target => { if (TryResult(target, out var r)) Execute(r, _ => { }); });

            settingsTab = ui.AddTab(SettingsTabId, DisplayName, BuildSettingsTab);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            ClearResults();
            settingsTab = null;
        }

        /// <summary>
        /// Record a plan: spawns its <see cref="TrajectorySelectable"/> (so trajectory verbs
        /// can appear) and raises <see cref="PlanProduced"/>. Subclasses call this from
        /// their planner callback.
        /// </summary>
        protected PlanResult PublishResult(PlanResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.PlannerId ??= Id;
            result.Label ??= $"{DisplayName} plan {results.Count + 1}";

            var go = new GameObject("Trajectory");
            go.transform.SetParent(transform, false);
            var selectable = go.AddComponent<TrajectorySelectable>();
            selectable.Initialize(this, result);
            results.Add(selectable);

            LastResult = result;
            PlanProduced?.Invoke(result);
            return result;
        }

        protected void ClearResults()
        {
            foreach (var r in results)
                if (r != null) Destroy(r.gameObject);
            results.Clear();
            LastResult = null;
        }

        private static bool TryResult(ISelectable target, out PlanResult result)
        {
            result = (target as TrajectorySelectable)?.Result;
            return result != null;
        }

        private void Report(InteractionRefusal refusal)
        {
            if (refusal.IsRefused) Context?.Router?.ReportRefusal(refusal);
        }
    }
}
