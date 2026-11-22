using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.Trajectory;

namespace Erupt.Plugins.Mtc
{
    /// <summary>
    /// MoveIt Task Constructor as an ERUPT planning plugin, for any number of robots. MTC plans
    /// on the ROS side from a recorded task (<see cref="PickPlaceClient"/> → mtc_pick_place_server);
    /// here "plan" means "take the best solution", preview plays it on <see cref="MtcSolutionPlayer"/>,
    /// execute sends its id to execute_solution. The pick/place recorder (the tab's Record button)
    /// is how a task is given. Depends on the MoveIt plugin for the planning scene. A solution is checked against
    /// the robot's joint limits when it is fetched; one that leaves them previews clamped and is not
    /// sent to execute_solution unless the plugin allows execution outside limits.
    /// </summary>
    /// <remarks>
    /// Every robot has its own pick-and-place server in its ROS namespace, so every robot has a
    /// <see cref="RobotSession"/>: its client, solution player, task recorder and solutions. The
    /// components on the plugin's own object serve the first robot; each further robot gets
    /// copies with the same settings. The members without a robot argument address the active
    /// robot's session, which is also the one the tab shows. A robot whose ROS side runs no
    /// pick-and-place server keeps its session; its client is simply not
    /// <see cref="PickPlaceClient.Available"/> and the tab says so.
    ///
    /// Preview has a scope (<see cref="PreviewStageId"/>): the whole solution, or one stage's
    /// steps of it, so a container stage like "pick object" can be watched on its own. A stage's
    /// partial or failed attempts (<see cref="AttemptsOf"/>) can be previewed from their own
    /// start scene with <see cref="PreviewStageSolution"/>; those are never executable and never
    /// become the selected solution.
    /// </remarks>
    public class MtcPlugin : PlanningPlugin
    {
        [Tooltip("Sibling components by default; found in the scene if empty. They serve the first robot; " +
                 "every further robot gets copies with the same settings.")]
        [SerializeField] private MtcSolutionPlayer player;
        [SerializeField] private PickPlaceTaskRecorder recorder;
        [SerializeField] private PickPlaceClient pickPlace;

        /// <summary>What the plugin keeps for one robot.</summary>
        public sealed class RobotSession
        {
            public RobotInstance Robot { get; internal set; }
            public MtcSolutionPlayer Player { get; internal set; }
            public PickPlaceTaskRecorder Recorder { get; internal set; }
            public PickPlaceClient PickPlace { get; internal set; }

            /// <summary>The solution shown in the browser; null until its fetch returns.</summary>
            public SolutionMsg SelectedSolution { get; internal set; }
            public uint? SelectedSolutionId { get; internal set; }
            /// <summary>Stage whose part of the selected solution the preview verb plays; null = the whole solution.</summary>
            public uint? PreviewStageId { get; internal set; }
            /// <summary>What the last preview request led to, or null.</summary>
            public string PreviewStatus { get; internal set; }
            public IReadOnlyList<uint> PreviewableStages => previewableStages;

            /// <summary>False while this robot's ROS side offers no pick-and-place action (no server, or not connected yet).</summary>
            public bool Available => PickPlace != null && PickPlace.Available;

            // Per task: both are dropped when a new plan starts, so a handle can never execute a dead id.
            internal readonly Dictionary<uint, PlanResult> resultById = new();
            internal readonly Dictionary<PlanResult, uint> idByResult = new();
            internal readonly List<uint> previewableStages = new();
            // Partial / failed stage solution being fetched for a preview; a later request or a task
            // reset replaces it, so a slower fetch is dropped. Never feeds SelectedSolutionId.
            internal uint? pendingStageSolutionId;
            // The object that holds this robot's copies of the components; null for the session
            // that uses the plugin's own.
            internal GameObject host;
            internal bool usesPluginComponents;
            internal Action onSolutionIds, onTaskReset, onDescription;
            internal Action<string> onProblem, onDiscarded;
        }

        private static readonly string[] Deps = { "moveit" };
        private RobotSessions<RobotSession> sessions;
        // The plugin's own components are in use by one session at a time.
        private bool pluginComponentsTaken;
        private SolutionsTab tab;

        public override string Id => "mtc";
        public override string DisplayName => "MTC";
        protected override int TabOrder => 20;
        public override IReadOnlyList<string> DependsOn => Deps;
        /// <summary>MTC plans from the recorded task; the end-effector goal verbs belong to MoveIt.</summary>
        public override bool AcceptsGoals => false;

        /// <summary>Every robot's session, in registration order; null before registration.</summary>
        public RobotSessions<RobotSession> Sessions => sessions;
        /// <summary>The session of the robot the user is working with (the one the tab shows).</summary>
        public RobotSession ActiveSession => sessions?.Active;
        /// <summary>A robot's session; null for a robot that has none.</summary>
        public RobotSession SessionFor(RobotInstance robot) => sessions?.For(robot);

        /// <summary>The active robot's solution player; the plugin's own idle component while there is no robot.</summary>
        public MtcSolutionPlayer Player => ActiveSession != null ? ActiveSession.Player : player;
        /// <summary>The active robot's task recorder; the plugin's own idle component while there is no robot.</summary>
        public PickPlaceTaskRecorder Recorder => ActiveSession != null ? ActiveSession.Recorder : recorder;
        /// <summary>The active robot's pick-and-place client; the plugin's own idle component while there is no robot.</summary>
        public PickPlaceClient PickPlace => ActiveSession != null ? ActiveSession.PickPlace : pickPlace;
        public SolutionsTab Tab => tab;

        /// <summary>The active robot's solution shown in the browser; null until its fetch returns.</summary>
        public SolutionMsg SelectedSolution => ActiveSession?.SelectedSolution;
        public uint? SelectedSolutionId => ActiveSession?.SelectedSolutionId;

        /// <summary>Stage whose part of the active robot's selected solution the preview verb plays; null = the whole solution.</summary>
        public uint? PreviewStageId => ActiveSession?.PreviewStageId;
        /// <summary>Stages with at least one step in the active robot's selected solution, in stage-tree order.</summary>
        public IReadOnlyList<uint> PreviewableStages => ActiveSession?.PreviewableStages ?? Array.Empty<uint>();
        /// <summary>What the active robot's last preview request led to (shown on the tab), or null.</summary>
        public string PreviewStatus => ActiveSession?.PreviewStatus;

        // Raised for every robot's session; the tab shows the active one and rereads it.
        public event Action SelectedSolutionChanged;
        public event Action PreviewScopeChanged;
        public event Action PreviewStatusChanged;

        /// <summary>A stage's solution that is not executable: partial (the stage solved) or failed.</summary>
        public readonly struct StageAttempt
        {
            public readonly uint Id;
            public readonly bool Failed;
            public StageAttempt(uint id, bool failed) { Id = id; Failed = failed; }
        }

        private void Awake()
        {
            if (player == null) player = GetComponent<MtcSolutionPlayer>();
            if (recorder == null) recorder = GetComponent<PickPlaceTaskRecorder>();
            if (pickPlace == null) pickPlace = GetComponent<PickPlaceClient>();
        }

        protected override void OnRegister(IEruptContext context)
        {
            if (pickPlace == null) pickPlace = FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
            if (player == null) player = FindFirstObjectByType<MtcSolutionPlayer>(FindObjectsInactive.Include);
            if (recorder == null) recorder = FindFirstObjectByType<PickPlaceTaskRecorder>(FindObjectsInactive.Include);
            if (pickPlace == null) Debug.LogError("[mtc] No PickPlaceClient in the scene.", this);

            IRobotRegistry robots = context.Robots;
            if (robots == null || robots.All.Count == 0)
                Debug.LogError("[mtc] No robot is registered; MTC cannot plan.", this);

            pluginComponentsTaken = false;
            sessions = new RobotSessions<RobotSession>(robots, CreateSession, DisposeSession);

            base.OnRegister(context);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            tab?.Dispose();
            tab = null;
            sessions?.Dispose();
            sessions = null;
            base.OnUnregister(context);
        }

        // A task being planned or executed on the robot's ROS side was recorded and solved for
        // the base where it is.
        protected override string WhyBaseMustStay(RobotInstance robot)
        {
            RobotSession session = SessionFor(robot);
            if (session?.PickPlace == null || !session.PickPlace.Busy) return null;
            return session.PickPlace.Phase == PickPlacePhase.Executing
                ? $"Robot '{robot.Id}' is executing a pick-and-place solution."
                : $"A pick-and-place task is being planned for robot '{robot.Id}'.";
        }

        // The solutions were found for the scene as it was in the robot's planning frame, which
        // moved with the base: the task is dropped as if it had not been planned (the base
        // class has already stopped the preview and removed the handles).
        protected override void OnRobotBaseMoved(RobotInstance robot, Pose previousBase)
        {
            RobotSession session = SessionFor(robot);
            if (session?.PickPlace != null) session.PickPlace.ForgetTask();
        }

        // Each robot's server is in that robot's ROS namespace and its solutions are played on
        // that robot's model. The first session takes the plugin's own components; the others
        // get copies on a child object.
        private RobotSession CreateSession(RobotInstance robot)
        {
            var session = new RobotSession { Robot = robot };
            if (!pluginComponentsTaken)
            {
                pluginComponentsTaken = true;
                session.usesPluginComponents = true;
                session.Player = player;
                session.Recorder = recorder;
                session.PickPlace = pickPlace;
            }
            else
            {
                session.host = SessionComponents.CreateHost(transform, "MTC", robot);
                // The client first: the recorder and the player look for it next to them.
                if (pickPlace != null) session.PickPlace = SessionComponents.Copy(pickPlace, session.host);
                if (player != null) session.Player = SessionComponents.Copy(player, session.host);
                if (recorder != null) session.Recorder = SessionComponents.Copy(recorder, session.host);
            }

            if (session.Player != null)
            {
                session.Player.SetRobot(robot);
                session.onProblem = problem => SetPreviewStatus(session, "Cannot preview: " + problem);
                session.Player.OnProblem += session.onProblem;
                session.Player.OnRefusal += Report;
            }

            // The client and the recorder stay idle until they get the bus.
            IRosBus bus = robot.Ros;
            if (session.PickPlace != null)
            {
                session.PickPlace.SetRobot(robot);
                if (bus != null) session.PickPlace.Initialise(bus);
                session.onSolutionIds = () => OnSolutionIds(session);
                session.onTaskReset = () => OnTaskReset(session);
                session.onDescription = () => RefreshPreviewScope(session);
                session.PickPlace.OnSolutionIdsChanged += session.onSolutionIds;
                session.PickPlace.OnTaskReset += session.onTaskReset;
                session.PickPlace.OnDescriptionChanged += session.onDescription;
                session.PickPlace.ExecutionGuard = id => WhyNotExecutable(session, id);
            }
            if (session.Recorder != null)
            {
                session.Recorder.SetRobot(robot);
                session.Recorder.SetPickPlaceClient(session.PickPlace);
                if (bus != null) session.Recorder.Initialise(bus);
                // A recording that is stopped with nothing to send used to end in a log line:
                // the user had pressed Record and Stop and no task appeared.
                session.onDiscarded = reason => RecordingDiscarded(robot, reason);
                session.Recorder.OnRecordingDiscarded += session.onDiscarded;
            }

            // While the client executes, the player shows the real robot and the solution's
            // attach/detach steps instead of a preview.
            if (session.Player != null && session.PickPlace != null) session.Player.SetPickPlaceClient(session.PickPlace);

            if (session.host != null) session.host.SetActive(true);
            return session;
        }

        private void DisposeSession(RobotSession session)
        {
            if (session.PickPlace != null)
            {
                session.PickPlace.OnSolutionIdsChanged -= session.onSolutionIds;
                session.PickPlace.OnTaskReset -= session.onTaskReset;
                session.PickPlace.OnDescriptionChanged -= session.onDescription;
                session.PickPlace.ExecutionGuard = null;
            }
            if (session.Recorder != null && session.onDiscarded != null)
                session.Recorder.OnRecordingDiscarded -= session.onDiscarded;
            if (session.Player != null)
            {
                session.Player.Stop();
                session.Player.OnProblem -= session.onProblem;
                session.Player.OnRefusal -= Report;
            }
            if (session.Recorder != null && session.Recorder.IsRecording) session.Recorder.CancelRecording();
            ClearResults(session.Robot);
            session.resultById.Clear();
            session.idByResult.Clear();
            session.previewableStages.Clear();

            if (session.usesPluginComponents)
            {
                // The plugin's own components go back to idle and can serve another robot.
                if (session.Player != null) { session.Player.SetPickPlaceClient(null); session.Player.SetRobot(null); }
                if (session.Recorder != null) session.Recorder.Release();
                if (session.PickPlace != null) session.PickPlace.Release();
                pluginComponentsTaken = false;
            }
            else if (session.host != null)
            {
                Destroy(session.host);
            }
        }

        protected override void BuildSettingsTab(RectTransform content)
        {
            tab?.Dispose();
            tab = null;
            if (content == null) return;
            tab = new SolutionsTab(this, content);
        }

        // --- solutions ----------------------------------------------------------------

        private void OnSolutionIds(RobotSession session)
        {
            // The first solution of a task is selected so its handle exists in the world.
            if (session.SelectedSolutionId == null && session.PickPlace.SolutionIds.Count > 0)
                SelectSolution(session, session.PickPlace.SolutionIds[0]);
        }

        private void OnTaskReset(RobotSession session)
        {
            if (session.Player != null) session.Player.Stop();
            ClearResults(session.Robot);
            session.resultById.Clear();
            session.idByResult.Clear();
            session.SelectedSolution = null;
            session.SelectedSolutionId = null;
            session.pendingStageSolutionId = null; // drops any partial fetch still in flight
            session.PreviewStatus = null;
            RefreshPreviewScope(session);
            SelectedSolutionChanged?.Invoke();
            PreviewStatusChanged?.Invoke();
        }

        /// <summary>
        /// Make one solution the active robot's current plan: fetched on selection (a get_solution
        /// round trip is 20–40 ms), then its handle appears at the end effector and that robot's
        /// other handles hide.
        /// </summary>
        public void SelectSolution(uint solutionId, Action<PlanResult> done = null) =>
            SelectSolution(ActiveSession, solutionId, done);

        /// <summary>As <see cref="SelectSolution(uint, Action{PlanResult})"/>, for a given robot.</summary>
        public void SelectSolution(RobotInstance robot, uint solutionId, Action<PlanResult> done = null) =>
            SelectSolution(SessionFor(robot), solutionId, done);

        private void SelectSolution(RobotSession session, uint solutionId, Action<PlanResult> done = null)
        {
            if (session?.PickPlace == null) { done?.Invoke(null); return; }
            RobotInstance robot = session.Robot;
            session.SelectedSolutionId = solutionId;
            session.SelectedSolution = null;
            SelectedSolutionChanged?.Invoke();

            session.PickPlace.FetchSolution(solutionId, solution =>
            {
                // A later selection wins over a slower fetch; so does the robot leaving.
                if (session.SelectedSolutionId != solutionId || SessionFor(robot) != session) { done?.Invoke(null); return; }
                if (!session.resultById.TryGetValue(solutionId, out var result))
                {
                    result = PublishResult(ToResult(robot, solutionId, solution), robot);
                    session.resultById[solutionId] = result;
                    session.idByResult[result] = solutionId;
                    // Every step of the solution, checked once, here, where it arrives.
                    CheckLimits(result, robot, UnityTrajectories(robot, solution));
                }
                session.SelectedSolution = solution;
                Transform ee = robot != null ? robot.Model?.EndEffector : null;
                PlaceHandle(result, ee != null ? ee.position : transform.position, ee);
                ShowOnlyHandle(result);
                // A solution arriving for a robot the user is not working with must not take
                // the selection away from what they are doing.
                if (Context?.Selection != null && ReferenceEquals(Context.Robots?.Active, robot))
                {
                    var selectable = Results.FirstOrDefault(r => r != null && r.Result == result);
                    if (selectable != null) Context.Selection.Select(selectable);
                }
                RefreshPreviewScope(session);
                SelectedSolutionChanged?.Invoke();
                done?.Invoke(result);
            },
            _ =>
            {
                if (session.SelectedSolutionId == solutionId)
                {
                    session.SelectedSolutionId = null;
                    SelectedSolutionChanged?.Invoke();
                }
                done?.Invoke(null);
            });
        }

        private PlanResult ToResult(RobotInstance robot, uint solutionId, SolutionMsg solution)
        {
            var first = solution.sub_trajectory?.FirstOrDefault(s => s.trajectory?.joint_trajectory?.points?.Length > 0);
            return new PlanResult
            {
                Trajectory = first?.trajectory?.joint_trajectory,
                PlannerPayload = solution,
                PlannerId = Id,
                // With several robots the label (the tier 2 menu header) says whose solution it is.
                Label = ActiveRobotTab.TitleFor($"MTC solution {solutionId}", Context?.Robots, robot)
            };
        }

        // The solution's steps under the Unity robot's joint names, which is what the robot's
        // limits are keyed by. A step without motion keeps its place so step numbers line up.
        private static IEnumerable<JointTrajectoryMsg> UnityTrajectories(RobotInstance robot, SolutionMsg solution)
        {
            foreach (SubTrajectoryMsg step in solution?.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>())
            {
                JointTrajectoryMsg trajectory = step?.trajectory?.joint_trajectory;
                if (trajectory?.joint_names == null || trajectory.points == null)
                {
                    yield return null;
                    continue;
                }
                yield return new JointTrajectoryMsg
                {
                    joint_names = robot != null ? robot.Names.ToLocal(trajectory.joint_names) : trajectory.joint_names,
                    points = trajectory.points
                };
            }
        }

        private void RecordingDiscarded(RobotInstance robot, string reason)
        {
            Transform endEffector = robot != null && robot.Model != null ? robot.Model.EndEffector : null;
            Report(InteractionRefusal.Refuse(RefusalCode.VerbUnavailable,
                $"No pick & place was recorded: {reason}. Select an object, grab it, put it where it should go and let go; then stop the recording.",
                endEffector != null ? endEffector.position : transform.position));
        }

        // Asked by the client before a solution id goes to execute_solution.
        private string WhyNotExecutable(RobotSession session, uint solutionId)
        {
            if (AllowExecutionOutsideLimits) return null;
            if (!session.resultById.TryGetValue(solutionId, out PlanResult result) || !result.HasLimitViolations) return null;
            return $"solution {solutionId} is outside the joint limits ({JointLimitViolation.Describe(result.LimitViolations)})";
        }

        /// <summary>MTC goals come from the recorded task; set-goal has nothing to set here.</summary>
        public override InteractionRefusal SetGoal(RobotInstance robot, ISelectable endEffector) =>
            InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable,
                "MTC plans from the recorded task; record a pick & place on the MTC tab instead.",
                endEffector?.GameObject != null ? endEffector.GameObject.transform.position : Vector3.zero);

        /// <summary>"Plan" for MTC selects the robot's best-cost solution (the server lists them by ascending cost).</summary>
        public override void RequestPlan(RobotInstance robot, PlanPreferences preferences, Action<PlanResult> done)
        {
            RobotSession session = SessionFor(robot);
            if (session?.PickPlace == null || session.PickPlace.SolutionIds.Count == 0)
            {
                Transform endEffector = robot != null && robot.Model != null ? robot.Model.EndEffector : null;
                Report(InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable,
                    session?.PickPlace == null
                        ? "Pick-and-place is not available for this robot."
                        : "There is no pick-and-place solution yet; record a pick & place on the MTC tab first.",
                    endEffector != null ? endEffector.position : transform.position));
                done?.Invoke(null);
                return;
            }
            SelectSolution(session, session.PickPlace.SolutionIds[0], done);
        }

        // --- preview --------------------------------------------------------------------

        /// <summary>Preview the plan on its robot, or only the scoped stage's steps of it (<see cref="PreviewStageId"/>).</summary>
        public override void Preview(PlanResult plan)
        {
            RobotSession session = SessionFor(RobotOf(plan));
            if (plan?.PlannerPayload is not SolutionMsg solution || session?.Player == null) return;
            string what = session.idByResult.TryGetValue(plan, out uint id) ? $"solution {id}" : plan.Label;
            if (session.PreviewStageId is uint stage && TryGetStageSteps(session, solution, stage, out int first, out int last))
            {
                string span = first == last ? $"step {first + 1}" : $"steps {first + 1}-{last + 1}";
                SetPreviewStatus(session, $"Previewing {StageName(session, stage)} ({span}) of {what}...");
                session.Player.PlaySolution(solution, first, last);
            }
            else
            {
                SetPreviewStatus(session, $"Previewing {what}...");
                session.Player.PlaySolution(solution);
            }
        }

        public override void StopPreview(RobotInstance robot)
        {
            if (sessions == null) return;
            if (robot != null)
            {
                RobotSession session = SessionFor(robot);
                if (session?.Player != null) session.Player.Stop();
                return;
            }
            foreach (RobotSession session in sessions)
                if (session.Player != null) session.Player.Stop();
        }

        /// <summary>Scope the preview verb to one stage of the active robot's selected solution, or null for all of it.</summary>
        public void SetPreviewStage(uint? stageId)
        {
            RobotSession session = ActiveSession;
            if (session == null) return;
            if (stageId != null && !session.previewableStages.Contains(stageId.Value)) stageId = null;
            if (session.PreviewStageId == stageId) return;
            session.PreviewStageId = stageId;
            PreviewScopeChanged?.Invoke();
        }

        /// <summary>
        /// First and last step (inclusive) of the active robot's selected solution that belong to
        /// a stage. A container stage (e.g. "pick object") covers the steps of all the stages
        /// nested under it.
        /// </summary>
        public bool TryGetStageSteps(uint stageId, out int first, out int last)
        {
            RobotSession session = ActiveSession;
            first = last = -1;
            return session != null && TryGetStageSteps(session, session.SelectedSolution, stageId, out first, out last);
        }

        private static bool TryGetStageSteps(RobotSession session, SolutionMsg solution, uint stageId, out int first, out int last)
        {
            first = last = -1;
            var steps = solution?.sub_trajectory;
            if (steps == null) return false;

            // The stage and everything nested under it.
            var family = new HashSet<uint> { stageId };
            var stages = session.PickPlace?.Description?.stages;
            if (stages != null)
                for (bool grew = true; grew;)
                {
                    grew = false;
                    foreach (var stage in stages)
                        if (stage.id != stage.parent_id && family.Contains(stage.parent_id) && family.Add(stage.id))
                            grew = true;
                }

            for (int i = 0; i < steps.Length; i++)
            {
                if (!family.Contains(steps[i].info.stage_id)) continue;
                if (first < 0) first = i;
                last = i;
            }
            return first >= 0;
        }

        // Stages in tree order (depth first, as the tab draws them) that own steps of the
        // selected solution; without a description, the steps' own stage ids in order.
        private void RefreshPreviewScope(RobotSession session)
        {
            List<uint> previewableStages = session.previewableStages;
            previewableStages.Clear();
            var steps = session.SelectedSolution?.sub_trajectory;
            if (steps != null && steps.Length > 0)
            {
                var stages = session.PickPlace?.Description?.stages;
                if (stages != null && stages.Length > 0)
                {
                    var ids = new HashSet<uint>(stages.Select(s => s.id));
                    var children = stages.ToLookup(s => s.parent_id);
                    foreach (var root in stages.Where(s => s.id == s.parent_id || !ids.Contains(s.parent_id)))
                        Visit(root);

                    void Visit(StageDescriptionMsg stage)
                    {
                        if (TryGetStageSteps(session, session.SelectedSolution, stage.id, out _, out _)) previewableStages.Add(stage.id);
                        foreach (var child in children[stage.id])
                            if (child.id != stage.id) Visit(child);
                    }
                }
                else
                {
                    foreach (var step in steps)
                        if (!previewableStages.Contains(step.info.stage_id)) previewableStages.Add(step.info.stage_id);
                }
            }
            // A scope the new solution has no steps for falls back to the whole solution.
            if (session.PreviewStageId != null && !previewableStages.Contains(session.PreviewStageId.Value))
                session.PreviewStageId = null;
            PreviewScopeChanged?.Invoke();
        }

        /// <summary>
        /// A stage's solutions that are not executable: partial ones (listed as solved for the
        /// stage but not for the task) and failed ones, from the statistics.
        /// </summary>
        public IReadOnlyList<StageAttempt> AttemptsOf(uint stageId)
        {
            var attempts = new List<StageAttempt>();
            PickPlaceClient pickPlace = PickPlace;
            var stats = pickPlace?.Statistics?.stages?.FirstOrDefault(s => s.id == stageId);
            if (stats == null) return attempts;
            var complete = pickPlace.SolutionIds;
            foreach (uint id in stats.solved ?? Array.Empty<uint>())
                if (!complete.Contains(id)) attempts.Add(new StageAttempt(id, false));
            foreach (uint id in stats.failed ?? Array.Empty<uint>())
                attempts.Add(new StageAttempt(id, true));
            return attempts;
        }

        /// <summary>
        /// Fetch a stage's partial or failed solution of the active robot with its start scene and
        /// preview it from there; a failed one also reports the planner's reason. These are never
        /// executable, so the selected (executable) solution is untouched.
        /// </summary>
        public void PreviewStageSolution(uint solutionId, bool failed = false)
        {
            RobotSession session = ActiveSession;
            PickPlaceClient pickPlace = session?.PickPlace;
            if (pickPlace == null) return;
            RobotInstance robot = session.Robot;
            if (pickPlace.Phase == PickPlacePhase.Executing)
            {
                SetPreviewStatus(session, "Cannot preview while a solution is executing.");
                return;
            }

            string kind = failed ? "failed solution" : "stage solution";
            session.pendingStageSolutionId = solutionId;
            SetPreviewStatus(session, $"Fetching {kind} {solutionId}...");

            pickPlace.FetchSolution(solutionId,
                solution =>
                {
                    // A later request (or a task reset) wins over a slower fetch.
                    if (session.pendingStageSolutionId != solutionId) return;
                    session.pendingStageSolutionId = null;

                    string comment = FirstComment(solution);
                    string reason = failed
                        ? $"Solution {solutionId} failed: {(string.IsNullOrEmpty(comment) ? "no reason given by the planner" : comment)}"
                        : null;

                    bool hasSteps = solution.sub_trajectory != null && solution.sub_trajectory.Length > 0;
                    if (!hasSteps || session.Player == null || pickPlace.Phase == PickPlacePhase.Executing)
                    {
                        SetPreviewStatus(session, reason ?? (hasSteps
                            ? "Preview unavailable right now."
                            : $"Stage solution {solutionId} has no motion to preview."));
                        return;
                    }

                    SetPreviewStatus(session, failed ? reason : $"Previewing stage solution {solutionId}...");
                    // Not executable, so nothing to refuse later; the user is told now that
                    // what plays is clamped to the joint limits.
                    var attempt = new PlanResult
                    {
                        LimitViolations = FindLimitViolations(robot, UnityTrajectories(robot, solution), LimitTolerance)
                    };
                    ReportLimitViolations(attempt, robot);
                    session.Player.PlaySolution(solution, useStartScene: true);
                },
                message =>
                {
                    if (session.pendingStageSolutionId != solutionId) return;
                    session.pendingStageSolutionId = null;
                    SetPreviewStatus(session, $"Solution {solutionId} unavailable: {message}");
                },
                includeStartScene: true);
        }

        private static string FirstComment(SolutionMsg solution)
        {
            foreach (var step in solution?.sub_trajectory ?? Array.Empty<SubTrajectoryMsg>())
                if (!string.IsNullOrEmpty(step?.info?.comment)) return step.info.comment;
            // A failure with no motion at all only carries its reason on the sub-solutions.
            foreach (var sub in solution?.sub_solution ?? Array.Empty<SubSolutionMsg>())
                if (!string.IsNullOrEmpty(sub?.info?.comment)) return sub.info.comment;
            return null;
        }

        private static string StageName(RobotSession session, uint stageId) =>
            session.PickPlace?.StageName(stageId) ?? $"stage {stageId}";

        private void SetPreviewStatus(RobotSession session, string text)
        {
            session.PreviewStatus = text;
            PreviewStatusChanged?.Invoke();
        }

        // --- execute --------------------------------------------------------------------

        public override async void Execute(PlanResult plan, Action<ExecutionStatus> status)
        {
            RobotInstance robot = RobotOf(plan);
            RobotSession session = SessionFor(robot);
            PickPlaceClient pickPlace = session?.PickPlace;
            if (plan == null || pickPlace == null || !session.idByResult.TryGetValue(plan, out uint solutionId))
            {
                status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable,
                    "This solution is not part of the current plan. Plan again."));
                return;
            }
            if (pickPlace.Busy)
            {
                status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "The server is busy with another goal."));
                return;
            }
            if (!MayExecute(plan, robot, status)) return;
            if (session.Player != null) session.Player.Stop();
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Sending));
            try
            {
                await pickPlace.ExecuteAsync(solutionId);
                status?.Invoke(new ExecutionStatus(Map(pickPlace.LastOutcome), pickPlace.LastStatus));
            }
            catch (Exception e)
            {
                status?.Invoke(new ExecutionStatus(Map(pickPlace.LastOutcome), e.Message));
            }
        }

        private static ExecutionPhase Map(PickPlaceOutcome outcome)
        {
            switch (outcome)
            {
                case PickPlaceOutcome.Succeeded: return ExecutionPhase.Succeeded;
                case PickPlaceOutcome.Canceled: return ExecutionPhase.Canceled;
                case PickPlaceOutcome.Aborted: return ExecutionPhase.Aborted;
                case PickPlaceOutcome.Rejected:
                case PickPlaceOutcome.Unavailable: return ExecutionPhase.Unavailable;
                default: return ExecutionPhase.Failed;
            }
        }
    }
}
