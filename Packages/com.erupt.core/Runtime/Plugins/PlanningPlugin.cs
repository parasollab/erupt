using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RosMessageTypes.Trajectory;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ui;

namespace Erupt.Plugins
{
    /// <summary>
    /// Template for a planner: set a goal, plan, preview, execute, for any number of robots.
    /// The base class owns the UI contributions so every planner shows up the same way
    /// (Guidelines Part 2): <c>set-goal</c> / <c>plan</c> on the end effector, <c>preview</c> /
    /// <c>execute</c> on a trajectory, and one tier 3 tab <c>planner-{Id}</c> for settings.
    /// </summary>
    /// <remarks>
    /// There is one plugin instance however many robots the scene has. The verbs act on the
    /// robot the selected object belongs to (<see cref="IRobotRegistry.OwnerOf"/>, falling back
    /// to the active robot), so every template method that starts something names its robot;
    /// a plan remembers which robot it is for (<see cref="PlanResult.RobotId"/>). Keep what the
    /// planner holds per robot in a <see cref="RobotSessions{T}"/>. The tab shows the active
    /// robot and is rebuilt when that changes.
    /// </remarks>
    public abstract class PlanningPlugin : EruptPluginBehaviour
    {
        private readonly List<TrajectorySelectable> results = new();
        private readonly Dictionary<string, PlanResult> lastResultByRobot = new();
        private ActiveRobotTab settingsTab;

        /// <summary>The most recent plan for any robot, or null.</summary>
        public PlanResult LastResult { get; private set; }
        public IReadOnlyList<TrajectorySelectable> Results => results;
        public PanelTab SettingsTab => settingsTab?.Tab;
        public string SettingsTabId => $"planner-{Id}";

        /// <summary>The most recent plan for one robot, or null.</summary>
        public PlanResult LastResultFor(RobotInstance robot) =>
            robot != null && lastResultByRobot.TryGetValue(robot.Id, out PlanResult result) ? result : null;

        /// <summary>Fires whenever a plan arrives (after its selectable exists).</summary>
        public event Action<PlanResult> PlanProduced;

        // --- template ----------------------------------------------------------

        /// <summary>Take the goal for <paramref name="robot"/> from its end effector. Return a refusal with a reason when that cannot be done.</summary>
        public abstract InteractionRefusal SetGoal(RobotInstance robot, ISelectable endEffector);
        /// <summary>
        /// Plan for <paramref name="robot"/>; hand the result (or null) to <paramref name="done"/>.
        /// A plan that does not come is the planner's to explain: report a refusal with the
        /// reason (<see cref="Report"/>) before handing over null. The plan verb only starts
        /// the request.
        /// </summary>
        public abstract void RequestPlan(RobotInstance robot, PlanPreferences preferences, Action<PlanResult> done);
        /// <summary>Show a plan on the robot it is for (<see cref="RobotOf(PlanResult)"/>).</summary>
        public abstract void Preview(PlanResult plan);
        /// <summary>Stop the preview on one robot; null stops every robot's.</summary>
        public abstract void StopPreview(RobotInstance robot);
        /// <summary>
        /// Whether a preview of this plan is playing and keeps playing until it is stopped. A
        /// planner that says yes gets <c>Stop Preview</c> on the plan's menu in the place of
        /// <c>Preview</c> (call <see cref="RefreshVerbs"/> when the answer changes). The default
        /// is no: a planner whose preview ends by itself needs no stop.
        /// </summary>
        public virtual bool IsPreviewPlaying(PlanResult plan) => false;

        /// <summary>The verb on a plan that stops its preview; shown while <see cref="IsPreviewPlaying"/> says yes.</summary>
        public const string StopPreviewVerb = "stop-preview";

        /// <summary>Have open menus build themselves again (a preview started or stopped).</summary>
        protected void RefreshVerbs() => Context?.Ui?.Verbs?.Refresh();
        /// <summary>Send a plan to the robot it is for (<see cref="RobotOf(PlanResult)"/>).</summary>
        public abstract void Execute(PlanResult plan, Action<ExecutionStatus> status);

        /// <summary>Set the goal of the robot the selected end effector belongs to.</summary>
        public InteractionRefusal SetGoal(ISelectable endEffector) => SetGoal(RobotOf(endEffector), endEffector);
        /// <summary>Plan for the active robot.</summary>
        public void RequestPlan(PlanPreferences preferences, Action<PlanResult> done) =>
            RequestPlan(Context?.Robots?.Active, preferences, done);
        /// <summary>Stop every robot's preview.</summary>
        public void StopPreview() => StopPreview(null);

        /// <summary>Fill the settings tab for the active robot. Called again whenever the active robot changes. Default: nothing.</summary>
        protected virtual void BuildSettingsTab(RectTransform content) { }

        /// <summary>Preferences used when a verb triggers a plan for <paramref name="robot"/>; override to read the tab.</summary>
        protected virtual PlanPreferences DefaultPreferences(RobotInstance robot) => new PlanPreferences();

        // --- robots ------------------------------------------------------------

        /// <summary>
        /// Whether this planner can plan for a robot. A planner that has no session for a
        /// robot (no ROS bus, no planning group) says false, and that robot's end-effector
        /// verbs then go to the next planner that can serve it.
        /// </summary>
        public virtual bool Serves(RobotInstance robot) => true;

        /// <summary>The robot a selected object belongs to; the active robot for an object that belongs to none.</summary>
        public RobotInstance RobotOf(ISelectable target) => RobotOf(Context, target);

        /// <summary>The robot a plan is for; the active robot for a plan that names none.</summary>
        public RobotInstance RobotOf(PlanResult plan)
        {
            IRobotRegistry robots = Context?.Robots;
            if (robots == null) return null;
            if (plan != null && !string.IsNullOrEmpty(plan.RobotId) && robots.TryGet(plan.RobotId, out RobotInstance robot))
                return robot;
            // A plan that names a robot which has left belongs to nobody.
            return plan != null && !string.IsNullOrEmpty(plan.RobotId) ? null : robots.Active;
        }

        private static RobotInstance RobotOf(IEruptContext context, ISelectable target)
        {
            IRobotRegistry robots = context?.Robots;
            if (robots == null) return null;
            RobotInstance owner = target?.GameObject != null ? robots.OwnerOf(target.GameObject) : null;
            return owner != null ? owner : robots.Active;
        }

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

        /// <summary>The planner the end-effector verbs address for the active robot: the first registered one that accepts goals.</summary>
        public static PlanningPlugin ActiveOn(IUiHost host) => PlannersOn(host).FirstOrDefault(p => p != null && p.AcceptsGoals);

        /// <summary>The planner the end-effector verbs address for a robot: the first registered one that accepts goals and serves that robot.</summary>
        public static PlanningPlugin PlannerFor(IUiHost host, RobotInstance robot) =>
            PlannersOn(host).FirstOrDefault(p => p != null && p.AcceptsGoals && p.Serves(robot));

        // ---- a robot's base was moved ---------------------------------------------------

        // One per robot, so robots that join later are watched too and ones that leave are let go.
        private sealed class BaseWatch : IDisposable
        {
            private readonly PlanningPlugin plugin;
            private readonly RobotInstance robot;
            private readonly Func<string> guard;

            public BaseWatch(PlanningPlugin plugin, RobotInstance robot)
            {
                this.plugin = plugin;
                this.robot = robot;
                guard = () => plugin.WhyBaseMustStay(robot);
                robot.BaseMoved += plugin.RobotBaseMoved;
                robot.AddBaseGuard(guard);
            }

            public void Dispose()
            {
                if (robot == null) return;
                robot.BaseMoved -= plugin.RobotBaseMoved;
                robot.RemoveBaseGuard(guard);
            }
        }

        private RobotSessions<BaseWatch> baseWatches;

        // A plan is a joint trajectory checked against the planning scene as it was for the
        // base where it was: once the base has moved, the robot's plans are no longer plans.
        private void RobotBaseMoved(RobotInstance robot, Pose previousBase)
        {
            StopPreview(robot);
            ClearResults(robot);
            OnRobotBaseMoved(robot, previousBase);
        }

        /// <summary>
        /// A robot's base was put somewhere else (<see cref="RobotInstance.PlaceBase(Pose)"/>).
        /// The base class has stopped the robot's preview and dropped its plans; drop whatever
        /// else was made for the old base (a goal, a recorded task) and send again whatever
        /// the robot's ROS side holds in the robot's planning frame.
        /// </summary>
        protected virtual void OnRobotBaseMoved(RobotInstance robot, Pose previousBase) { }

        /// <summary>
        /// Why a robot's base has to stay where it is right now, as a sentence for the user;
        /// null when this planner does not mind. The base class has no reason; a planner that
        /// is executing on the robot, or whose robot carries something, says so.
        /// </summary>
        protected virtual string WhyBaseMustStay(RobotInstance robot) => null;

        protected override void OnRegister(IEruptContext context)
        {
            baseWatches = new RobotSessions<BaseWatch>(context.Robots, robot => new BaseWatch(this, robot));

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
                // Shared verbs, bound once. set-goal / plan go to the planner that serves the
                // robot the selected end effector belongs to; preview / execute go to whichever
                // planner produced the selected trajectory, which knows the robot it is for.
                ui.BindVerb("set-goal", target => ForEndEffector(context, target, (planner, robot) => planner.Report(planner.SetGoal(robot, target))));
                Action<ISelectable> plan = target => ForEndEffector(context, target,
                    (planner, robot) => planner.RequestPlan(robot, planner.DefaultPreferences(robot), _ => { }));
                if (!ui.Verbs.Contains(SelectionKind.EndEffector, "plan"))
                    ui.RegisterVerb(SelectionKind.EndEffector, "plan", "Plan", plan, Id);
                else
                    ui.BindVerb("plan", plan);
                ui.BindVerb("preview", target =>
                {
                    if (TryResult(target, out var r, out var owner)) owner.Preview(r);
                    else NoPlanHere(context, target, "preview");
                });
                ui.BindVerb("execute", target =>
                {
                    if (TryResult(target, out var r, out var owner)) owner.ExecuteAndReport(r, target);
                    else NoPlanHere(context, target, "execute");
                });
                Action<ISelectable> stopPreview = target =>
                {
                    if (TryResult(target, out var r, out var owner)) owner.StopPreview(owner.RobotOf(r));
                    else NoPlanHere(context, target, "stop previewing");
                };
                if (!ui.Verbs.Contains(SelectionKind.Trajectory, StopPreviewVerb))
                    ui.RegisterVerb(SelectionKind.Trajectory, StopPreviewVerb, "Stop Preview", stopPreview, Id);
                else
                    ui.BindVerb(StopPreviewVerb, stopPreview);
                // A binding outlives the planners; the verbs do not. They are on the menus
                // only while a planner is registered with this UI.
                if (ui.Verbs != null)
                {
                    System.Func<ISelectable, bool> hasPlanner = _ => plannersByHost.ContainsKey(ui);
                    foreach ((SelectionKind kind, string verb) in SharedVerbs)
                        if (ui.Verbs.Contains(kind, verb)) ui.Verbs.ShowWhen(kind, verb, hasPlanner);
                    // A plan whose preview is playing offers to stop it where it offered to play it.
                    ui.Verbs.ShowWhen(SelectionKind.Trajectory, "preview", t => hasPlanner(t) && !IsBeingPreviewed(t));
                    ui.Verbs.ShowWhen(SelectionKind.Trajectory, StopPreviewVerb, t => hasPlanner(t) && IsBeingPreviewed(t));
                }
            }

            settingsTab = new ActiveRobotTab(ui, context.Robots, SettingsTabId, DisplayName, BuildSettingsTab);
            if (settingsTab.Tab != null) settingsTab.Tab.Order = TabOrder;
        }

        /// <summary>Where this plugin's tab stands in the tier 3 strip (<see cref="PanelTab.Order"/>).</summary>
        protected virtual int TabOrder => PanelTab.DefaultOrder;

        // The verbs every planner shares: bound by the first one, shown while there is one.
        private static readonly (SelectionKind kind, string verb)[] SharedVerbs =
        {
            (SelectionKind.EndEffector, "set-goal"), (SelectionKind.EndEffector, "plan"),
            (SelectionKind.Trajectory, "preview"), (SelectionKind.Trajectory, "execute")
        };

        private static void NoPlanHere(IEruptContext context, ISelectable target, string what)
        {
            Vector3 at = target?.GameObject != null ? target.GameObject.transform.position : Vector3.zero;
            context.Router?.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, $"There is no plan here to {what}.", at));
        }

        /// <summary>
        /// Execute a plan the way the <c>execute</c> verb does: whatever keeps the robot from
        /// arriving is a refusal on the router, at what the user pointed at (the plan's handle),
        /// with the planner's own words: not connected, nothing to send, the server busy, the
        /// robot not reaching the end. A plan refused for leaving the joint limits has been
        /// reported by <see cref="MayExecute"/> already, and a cancelled execution is the
        /// user's own doing; neither is reported twice.
        /// </summary>
        /// <param name="pointedAt">Where the refusal is shown; null shows it at the robot's end effector.</param>
        /// <param name="status">Every status of the execution, for a caller that follows it.</param>
        public void ExecuteAndReport(PlanResult plan, ISelectable pointedAt = null, Action<ExecutionStatus> status = null)
        {
            Transform where = pointedAt?.GameObject != null ? pointedAt.GameObject.transform : null;
            if (where == null)
            {
                RobotInstance robot = RobotOf(plan);
                where = robot != null && robot.Model != null ? robot.Model.EndEffector : null;
            }
            Vector3 at = where != null ? where.position : transform.position;
            Execute(plan, s =>
            {
                status?.Invoke(s);
                RefusalCode code;
                switch (s.Phase)
                {
                    case ExecutionPhase.Unavailable: code = RefusalCode.PlannerUnavailable; break;
                    case ExecutionPhase.Failed:
                    case ExecutionPhase.Aborted: code = RefusalCode.ExecutionFailed; break;
                    default: return;
                }
                // A failure may come seconds later: the handle may have moved, or gone.
                if (where != null) at = where.position;
                Report(InteractionRefusal.Refuse(code, s.Message, at));
            });
        }

        // Runs an end-effector verb on the planner that serves the robot the target belongs to;
        // a robot no planner serves gets a refusal instead of a button that does nothing.
        private static void ForEndEffector(IEruptContext context, ISelectable target, Action<PlanningPlugin, RobotInstance> act)
        {
            RobotInstance robot = RobotOf(context, target);
            PlanningPlugin planner = PlannerFor(context.Ui, robot);
            if (planner != null)
            {
                act(planner, robot);
                return;
            }
            Vector3 at = target?.GameObject != null ? target.GameObject.transform.position : Vector3.zero;
            string who = robot != null ? $"robot '{robot.Id}'" : "this robot";
            context.Router?.ReportRefusal(InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable, $"No planner is available for {who}.", at));
        }

        protected override void OnUnregister(IEruptContext context)
        {
            baseWatches?.Dispose();
            baseWatches = null;
            ClearResults();
            settingsTab?.Dispose();
            settingsTab = null;
            if (context?.Ui != null)
            {
                // The tab goes with the plugin, and the shared verbs with the last planner.
                context.Ui.RemoveTab(SettingsTabId);
                if (plannersByHost.TryGetValue(context.Ui, out var planners))
                {
                    planners.Remove(this);
                    if (planners.Count == 0) plannersByHost.Remove(context.Ui);
                }
                context.Ui.Verbs?.Refresh();
            }
        }

        /// <summary>Rebuild the settings tab for the active robot (it is rebuilt by itself when the active robot changes).</summary>
        protected void RebuildSettingsTab() => settingsTab?.Rebuild();

        /// <summary>
        /// Record a plan for a robot: spawns its <see cref="TrajectorySelectable"/> (so
        /// trajectory verbs can appear), stamps <see cref="PlanResult.RobotId"/> and raises
        /// <see cref="PlanProduced"/>. Subclasses call this from their planner callback.
        /// </summary>
        protected PlanResult PublishResult(PlanResult result, RobotInstance robot)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.PlannerId ??= Id;
            if (robot != null) result.RobotId = robot.Id;
            int ordinal = results.Count(r => r != null && r.RobotId == result.RobotId) + 1;
            result.Label ??= ActiveRobotTab.TitleFor($"{DisplayName} plan {ordinal}", Context?.Robots, robot);

            var go = new GameObject("Trajectory");
            go.transform.SetParent(transform, false);
            var selectable = go.AddComponent<TrajectorySelectable>();
            selectable.Initialize(this, result);
            // The handle lives under the plugin, not under the robot the plan is for.
            if (robot != null) RobotOwned.Mark(go, robot);
            results.Add(selectable);

            LastResult = result;
            if (result.RobotId != null) lastResultByRobot[result.RobotId] = result;
            PlanProduced?.Invoke(result);
            return result;
        }

        /// <summary>Record a plan for the robot it names (<see cref="PlanResult.RobotId"/>), or for the active robot.</summary>
        protected PlanResult PublishResult(PlanResult result) => PublishResult(result, RobotOf(result));

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

        /// <summary>
        /// Take a robot's plan handles off whatever they follow (a goal ghost's end effector, say)
        /// and leave them where they are, under the plugin. Call before destroying what they
        /// follow: a handle is a child of it and would be destroyed with it, taking the plan's
        /// only selectable (and its verbs) away.
        /// </summary>
        protected void ReleaseHandles(RobotInstance robot)
        {
            string id = robot != null ? robot.Id : null;
            foreach (TrajectorySelectable r in results)
                if (r != null && (robot == null || r.RobotId == id) && r.transform.parent != transform)
                    r.transform.SetParent(transform, true);
        }

        /// <summary>Hide every handle of the plan's robot except the plan's own (e.g. only the selected solution is placed). Other robots' handles stay as they are.</summary>
        protected void ShowOnlyHandle(PlanResult result)
        {
            foreach (var r in results)
                if (r != null && r.RobotId == result?.RobotId) r.gameObject.SetActive(r.Result == result);
        }

        /// <summary>Destroy the plans of one robot; null destroys every robot's.</summary>
        protected void ClearResults(RobotInstance robot = null)
        {
            string id = robot != null ? robot.Id : null;
            for (int i = results.Count - 1; i >= 0; i--)
            {
                TrajectorySelectable r = results[i];
                if (r != null && robot != null && r.RobotId != id) continue;
                if (r != null) Destroy(r.gameObject);
                results.RemoveAt(i);
            }
            if (robot == null) lastResultByRobot.Clear();
            else lastResultByRobot.Remove(id);
            if (robot == null || LastResult?.RobotId == id) LastResult = null;
        }

        private static bool IsBeingPreviewed(ISelectable target) =>
            TryResult(target, out PlanResult result, out PlanningPlugin owner) && owner.IsPreviewPlaying(result);

        private static bool TryResult(ISelectable target, out PlanResult result, out PlanningPlugin owner)
        {
            var selectable = target as TrajectorySelectable;
            result = selectable?.Result;
            owner = selectable?.Owner;
            return result != null && owner != null;
        }

        /// <summary>Raise a refusal on the interaction router, where the user-facing feedback listens.</summary>
        protected void Report(InteractionRefusal refusal)
        {
            if (refusal.IsRefused) Context?.Router?.ReportRefusal(refusal);
        }

        // --- joint limits --------------------------------------------------------------

        [Header("Joint limits")]
        [Tooltip("Send plans for execution even when they take a joint outside the robot's limits. " +
                 "Off (the default) refuses them; either way the preview plays inside the limits.")]
        [SerializeField] private bool allowExecutionOutsideLimits;
        [Tooltip("A plan is outside the limits when it passes one by more than this.")]
        [SerializeField] private JointLimitTolerance limitTolerance = JointLimitTolerance.Default;

        /// <summary>Whether plans that leave the robot's joint limits are sent for execution anyway. Off by default.</summary>
        public bool AllowExecutionOutsideLimits
        {
            get => allowExecutionOutsideLimits;
            set
            {
                allowExecutionOutsideLimits = value;
                OnLimitPolicyChanged();
            }
        }

        /// <summary>How far past a limit a plan may go before it counts as outside the limits.</summary>
        public JointLimitTolerance LimitTolerance
        {
            get => limitTolerance;
            set
            {
                limitTolerance = value;
                OnLimitPolicyChanged();
            }
        }

        /// <summary>Called when the opt-out or the tolerance changes, for a subclass that hands them to its transport.</summary>
        protected virtual void OnLimitPolicyChanged() { }

        /// <summary>
        /// Check a plan against the limits of the robot it is for, once, when it arrives: the
        /// joints that leave their limits are stored on the plan
        /// (<see cref="PlanResult.LimitViolations"/>) and the user is told which joint.
        /// </summary>
        /// <param name="trajectories">
        /// Everything the plan moves, in Unity joint names (the steps of a multi-part solution).
        /// Null = the plan's own <see cref="PlanResult.Trajectory"/>.
        /// </param>
        protected void CheckLimits(PlanResult plan, RobotInstance robot, IEnumerable<JointTrajectoryMsg> trajectories = null)
        {
            if (plan == null) return;
            plan.LimitViolations = FindLimitViolations(robot, trajectories ?? new[] { plan.Trajectory }, limitTolerance);
            ReportLimitViolations(plan, robot);
        }

        /// <summary>The joints a set of trajectories (Unity joint names) takes outside a robot's limits; one entry per joint.</summary>
        public static List<JointLimitViolation> FindLimitViolations(
            RobotInstance robot, IEnumerable<JointTrajectoryMsg> trajectories, JointLimitTolerance tolerance)
        {
            var violations = new List<JointLimitViolation>();
            IJointLimits limits = robot != null && robot.Model != null ? robot.Model.Limits : null;
            if (limits == null || trajectories == null) return violations;

            int segment = 0;
            foreach (JointTrajectoryMsg trajectory in trajectories)
            {
                if (trajectory != null)
                {
                    List<JointLimitViolation> found = limits.Validate(trajectory, tolerance);
                    for (int i = 0; i < found.Count; i++) found[i] = found[i].InSegment(segment);
                    JointLimitViolation.Merge(violations, found);
                }
                segment++;
            }
            return violations;
        }

        /// <summary>Tell the user that a plan leaves the joint limits (nothing when it does not).</summary>
        protected void ReportLimitViolations(PlanResult plan, RobotInstance robot)
        {
            if (plan == null || !plan.HasLimitViolations) return;
            string consequence = allowExecutionOutsideLimits
                ? "The preview stays inside the limits."
                : "The preview stays inside the limits and the plan will not be executed.";
            Report(LimitRefusal(plan, robot, $"This plan is outside the joint limits: {JointLimitViolation.Describe(plan.LimitViolations)}. {consequence}"));
        }

        /// <summary>
        /// Whether a plan may be sent for execution. A plan that leaves the joint limits may
        /// not, unless <see cref="AllowExecutionOutsideLimits"/> is on: the refusal goes to the
        /// router and <paramref name="status"/> gets <see cref="ExecutionPhase.Refused"/>.
        /// Call it first in <see cref="Execute"/>.
        /// </summary>
        protected bool MayExecute(PlanResult plan, RobotInstance robot, Action<ExecutionStatus> status)
        {
            if (plan == null || !plan.HasLimitViolations || allowExecutionOutsideLimits) return true;

            InteractionRefusal refusal = LimitRefusal(plan, robot,
                $"Not executed: the plan is outside the joint limits ({JointLimitViolation.Describe(plan.LimitViolations)}).");
            Report(refusal);
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Refused, refusal.Reason));
            return false;
        }

        // The refusal points at the link of the joint that is furthest out.
        private static InteractionRefusal LimitRefusal(PlanResult plan, RobotInstance robot, string reason)
        {
            Transform link = null;
            JointLimitViolation? worst = JointLimitViolation.Worst(plan.LimitViolations);
            if (worst != null && robot != null && robot.Model != null)
                robot.Model.TryGetJointLink(worst.Value.Joint, out link);
            return InteractionRefusal.Refuse(RefusalCode.PlanOutsideLimits, reason, link != null ? link.position : Vector3.zero, link);
        }
    }
}
