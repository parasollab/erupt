using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Whether this planner takes goals from the end effector. A planner that plans on
        /// the ROS side from a recorded task (MTC) says false, so the shared set-goal / plan
        /// verbs go to the planner that can act on them.
        /// </summary>
        public virtual bool AcceptsGoals => true;

        // Every registered planner, per UI host, so the shared verbs are bound once and
        // dispatched rather than rebound by whichever planner registered last.
        private static readonly Dictionary<IUiHost, List<PlanningPlugin>> plannersByHost = new();

        /// <summary>Registered planners on a host, in registration order.</summary>
        public static IReadOnlyList<PlanningPlugin> PlannersOn(IUiHost host) =>
            host != null && plannersByHost.TryGetValue(host, out var list) ? list : Array.Empty<PlanningPlugin>();

        /// <summary>The planner the end-effector verbs address: the first registered one that accepts goals.</summary>
        public static PlanningPlugin ActiveOn(IUiHost host) => PlannersOn(host).FirstOrDefault(p => p != null && p.AcceptsGoals);

        protected override void OnRegister(IEruptContext context)
        {
            IUiHost ui = context.Ui;
            if (ui == null)
            {
                Debug.LogWarning($"[{Id}] No UI host; planner verbs and tab not contributed.", this);
                return;
            }

            if (!plannersByHost.TryGetValue(ui, out var planners)) plannersByHost[ui] = planners = new List<PlanningPlugin>();
            bool first = planners.Count == 0;
            planners.Add(this);

            if (first)
            {
                // Shared verbs, bound once. set-goal / plan go to the active planner; preview /
                // execute go to whichever planner produced the selected trajectory.
                ui.BindVerb("set-goal", target => { var p = ActiveOn(ui); if (p != null) p.Report(p.SetGoal(target)); });
                if (!ui.Verbs.Contains(SelectionKind.EndEffector, "plan"))
                    ui.RegisterVerb(SelectionKind.EndEffector, "plan", "Plan", _ => ActiveOn(ui)?.RequestPlan(ActiveOn(ui).DefaultPreferences(), _ => { }), Id);
                else
                    ui.BindVerb("plan", _ => ActiveOn(ui)?.RequestPlan(ActiveOn(ui).DefaultPreferences(), _ => { }));
                ui.BindVerb("preview", target => { if (TryResult(target, out var r, out var owner)) owner.Preview(r); });
                ui.BindVerb("execute", target => { if (TryResult(target, out var r, out var owner)) owner.Execute(r, _ => { }); });
            }

            settingsTab = ui.AddTab(SettingsTabId, DisplayName, BuildSettingsTab);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            ClearResults();
            settingsTab = null;
            if (context?.Ui != null && plannersByHost.TryGetValue(context.Ui, out var planners))
            {
                planners.Remove(this);
                if (planners.Count == 0) plannersByHost.Remove(context.Ui);
            }
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

        [Header("Trajectory handle")]
        [Tooltip("Diameter of the sphere that makes a plan selectable in the world.")]
        [SerializeField, Min(0.01f)] private float handleDiameter = 0.06f;
        [SerializeField] private Color handleColor = new Color(0.55f, 0.35f, 1f, 0.9f);

        /// <summary>
        /// Give a plan a presence in the world: a small sphere at <paramref name="worldPosition"/>
        /// (optionally following <paramref name="follow"/>) carrying its
        /// <see cref="TrajectorySelectable"/>, so a ray on it selects the plan and the
        /// Trajectory verbs (preview, execute) appear. Guidelines Part 2: verbs attach to a
        /// selection, so a plan must be selectable to have verbs at all.
        /// </summary>
        protected TrajectorySelectable PlaceHandle(PlanResult result, Vector3 worldPosition, Transform follow = null)
        {
            var selectable = results.Find(r => r != null && r.Result == result);
            if (selectable == null) return null;

            GameObject go = selectable.gameObject;
            if (go.GetComponent<Collider>() == null)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Handle";
                sphere.transform.SetParent(go.transform, false);
                sphere.transform.localScale = Vector3.one * handleDiameter;
                sphere.GetComponent<Collider>().isTrigger = true;
                var renderer = sphere.GetComponent<Renderer>();
                renderer.material.color = handleColor;
                // The handle is the collider the ray hits; SelectionService.Resolve walks
                // up to the TrajectorySelectable on this object.
            }
            go.transform.SetParent(follow != null ? follow : transform, true);
            go.transform.position = worldPosition;
            go.SetActive(true);
            return selectable;
        }

        /// <summary>Hide every handle except the given plan's (e.g. only the selected solution is placed).</summary>
        protected void ShowOnlyHandle(PlanResult result)
        {
            foreach (var r in results)
                if (r != null) r.gameObject.SetActive(r.Result == result);
        }

        protected void ClearResults()
        {
            foreach (var r in results)
                if (r != null) Destroy(r.gameObject);
            results.Clear();
            LastResult = null;
        }

        private static bool TryResult(ISelectable target, out PlanResult result, out PlanningPlugin owner)
        {
            var selectable = target as TrajectorySelectable;
            result = selectable?.Result;
            owner = selectable?.Owner;
            return result != null && owner != null;
        }

        private void Report(InteractionRefusal refusal)
        {
            if (refusal.IsRefused) Context?.Router?.ReportRefusal(refusal);
        }
    }
}
