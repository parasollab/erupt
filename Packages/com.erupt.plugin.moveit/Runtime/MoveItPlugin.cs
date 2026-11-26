using System;
using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;
using Erupt.Ros;

namespace Erupt.Plugins.MoveIt
{
    /// <summary>
    /// MoveIt 2 as an ERUPT planning plugin, for any number of robots: goal capture from a
    /// robot's current pose, plan_kinematic_path, preview on that robot's
    /// <see cref="JointTrajectoryPlayer"/>, execution by publishing the trajectory. Each robot
    /// has a <see cref="MoveItRobotSession"/> (planning client, start/goal, preview, planning
    /// scene endpoint) that talks to that robot's own MoveIt in its ROS namespace; group, topics
    /// and joint names come from that robot's profile. The verbs act on the robot the selected
    /// object belongs to; the members without a robot argument address the active robot.
    /// </summary>
    /// <remarks>
    /// The plugin also owns the <see cref="PlanningSceneFanout"/>: Unity has one environment,
    /// every robot's MoveIt has its own planning scene, and the fan-out keeps them all in step
    /// (see that class for the rules). The scene's <see cref="CollisionObjectsListenerSimple"/>
    /// is the Unity side of that mirror; the scene's <see cref="AttachedCollisionObjectListener"/>
    /// serves the first robot and is the template for the others'.
    /// </remarks>
    public class MoveItPlugin : PlanningPlugin
    {
        [SerializeField] private MoveItPlanningSettings settings = new();
        [Tooltip("Query query_planner_interface on register and keep retrying until it answers.")]
        [SerializeField] private bool autoQueryPlanners = true;
        [Tooltip("Show the ROS robot's joint state on the Unity robot once, when the first joint state arrives after the connection " +
                 "comes up (at the start, after a scene reset, after a reconnect), unless the user has moved the robot since. " +
                 "Off leaves the robot in its profile's initial pose until \"Mirror joints\" is switched on.")]
        [SerializeField] private bool adoptRobotStateOnConnect = true;
        [Tooltip("When a plan arrives for the robot the user is working with, select it, so its menu (Preview, Execute) is there " +
                 "without hunting for the plan's handle. Off leaves the selection where it was.")]
        [SerializeField] private bool selectNewPlan = true;

        private RobotSessions<MoveItRobotSession> sessions;
        private PlannerSettingsTab tab;

        // The planning scenes of all robots, kept in step with Unity's one environment.
        private PlanningSceneFanout scene;
        // Attach mirrors that are in the scene and not in use by a robot; the first is also the
        // template the further robots' mirrors are copied from.
        private readonly List<AttachedCollisionObjectListener> freeAttachListeners = new();
        private AttachedCollisionObjectListener attachTemplate;
        // A scene without a robot still mirrors the planning scene in the root namespace.
        private PlanningSceneEndpoint robotlessScene;
        private AttachedCollisionObjectListener robotlessAttachListener;

        public override string Id => "moveit";
        public override string DisplayName => "MoveIt";
        protected override int TabOrder => 10;

        public MoveItPlanningSettings Settings => settings;

        /// <summary>Whether a plan that arrives for the active robot becomes the selection. On by default.</summary>
        public bool SelectNewPlan
        {
            get => selectNewPlan;
            set => selectNewPlan = value;
        }

        /// <summary>
        /// Whether each robot takes the joint state its ROS side reports, once, when the
        /// connection comes up (see <see cref="MoveItPlanningClient.AdoptsReportedState"/>).
        /// On by default. Changing it applies from each robot's next connection.
        /// </summary>
        public bool AdoptRobotStateOnConnect
        {
            get => adoptRobotStateOnConnect;
            set
            {
                adoptRobotStateOnConnect = value;
                if (sessions == null) return;
                foreach (MoveItRobotSession session in sessions) session.Client.AdoptsReportedState = value;
            }
        }

        /// <summary>Every robot's session, in registration order; null before registration.</summary>
        public RobotSessions<MoveItRobotSession> Sessions => sessions;
        /// <summary>The session of the robot the user is working with.</summary>
        public MoveItRobotSession ActiveSession => sessions?.Active;
        /// <summary>A robot's session; null for a robot MoveIt does not serve.</summary>
        public MoveItRobotSession SessionFor(RobotInstance robot) => sessions?.For(robot);

        /// <summary>The active robot's planning client.</summary>
        public MoveItPlanningClient Client => ActiveSession?.Client;
        public bool HasStart => ActiveSession != null && ActiveSession.HasStart;
        public bool HasGoal => ActiveSession != null && ActiveSession.HasGoal;
        public bool IsPreviewing => ActiveSession != null && ActiveSession.IsPreviewing;

        /// <summary>Fires with the MoveIt error text when a plan request fails.</summary>
        public event Action<string> PlanFailed;
        /// <summary>Fires when a robot's start or goal state is set or reset.</summary>
        public event Action GoalChanged;

        public override bool Serves(RobotInstance robot) => SessionFor(robot) != null;

        /// <summary>The fan-out that shares the environment with every robot's planning scene; null before registration.</summary>
        public PlanningSceneFanout PlanningScene => scene;

        /// <summary>A robot's planning scene; null for a robot MoveIt does not serve.</summary>
        public PlanningSceneEndpoint PlanningSceneOf(RobotInstance robot) => SessionFor(robot)?.Scene;

        /// <summary>Detach every body from one robot in its own planning scene (<see cref="PlanningSceneEndpoint.DetachAllFromRobot"/>).</summary>
        public void DetachAllFromRobot(RobotInstance robot, Action<bool> done = null)
        {
            PlanningSceneEndpoint endpoint = PlanningSceneOf(robot);
            if (endpoint == null) { done?.Invoke(false); return; }
            endpoint.DetachAllFromRobot(done);
        }

        /// <summary>Remove one object no Unity object owns from one robot's planning scene.</summary>
        public void PublishRemove(RobotInstance robot, string id) => PlanningSceneOf(robot)?.PublishRemove(id);

        protected override void OnRegister(IEruptContext context)
        {
            IRobotRegistry robots = context.Robots;
            if (robots == null || robots.All.Count == 0)
                Debug.LogError("[moveit] No robot is registered; MoveIt cannot plan.", this);

            // The Unity side of the planning-scene mirror and the attach mirrors live in the
            // scene; they do nothing until the fan-out and the robots' sessions use them.
            var mirrors = FindObjectsByType<CollisionObjectsListenerSimple>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
            if (mirrors.Length > 1)
                Debug.LogWarning($"[moveit] {mirrors.Length} CollisionObjectsListenerSimple components in the scene; '{mirrors[0].name}' mirrors the planning scenes.", this);
            scene = new PlanningSceneFanout(mirrors.Length > 0 ? mirrors[0] : null);
            freeAttachListeners.Clear();
            freeAttachListeners.AddRange(FindObjectsByType<AttachedCollisionObjectListener>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID));
            attachTemplate = freeAttachListeners.Count > 0 ? freeAttachListeners[0] : null;

            // Before the first object reports itself: is there anyone to send it to yet?
            linkOpen = linkWasEverClosed = linkOpenedOnce = false;
            onlineSince = offlineSince = -1f;
            linkHost = null;
            WatchConnection();
            sessions = new RobotSessions<MoveItRobotSession>(robots, CreateSession, DisposeSession);

            if (sessions.Count == 0 && context.Ros != null)
            {
                Transform origin = context.Environment != null ? context.Environment.WorldOrigin : null;
                robotlessScene = scene.AddEndpoint(context.Ros.Scoped(""), "", origin);
                if (freeAttachListeners.Count > 0)
                {
                    robotlessAttachListener = freeAttachListeners[0];
                    freeAttachListeners.RemoveAt(0);
                    robotlessAttachListener.Initialise(robotlessScene.Ros);
                    robotlessScene.Attached = robotlessAttachListener;
                }
            }

            // MoveIt captures a start pose as well as the planning family's goal pose.
            if (context.Ui != null && !context.Ui.Verbs.Contains(SelectionKind.EndEffector, "set-start"))
                context.Ui.RegisterVerb(SelectionKind.EndEffector, "set-start", "Set Start", target => SetStartFromRobot(RobotOf(target)), Id);

            base.OnRegister(context);

            if (autoQueryPlanners) InvokeRepeating(nameof(TryQueryPlanners), 0.5f, 1.0f);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            // On a scene unload the host may unregister a plugin whose object is already
            // destroyed; its invokes went with it, and asking again would throw and leave the
            // rest of the unregistration (every subscription) undone.
            if (this != null) CancelInvoke(nameof(TryQueryPlanners));
            tab?.Dispose();
            tab = null;
            // The fan-out goes first, while the planning scenes are still connected: it takes
            // Unity's objects out of them (see PlanningSceneFanout.Dispose). Dropping the robots
            // first would leave nowhere to send that, and every obstacle the user had added
            // would stay behind in MoveIt and come back as a ROS-side object in the next scene.
            scene?.Dispose();
            sessions?.Dispose();
            sessions = null;
            DropRobotlessScene();
            scene = null;
            freeAttachListeners.Clear();
            attachTemplate = null;
            base.OnUnregister(context);
        }

        private void Update()
        {
            if (scene != null) scene.Tick();
            if (sessions != null)
                foreach (MoveItRobotSession session in sessions) session.Client.Tick();
            WatchConnection();
        }

        // How long the endpoint has to be unreachable before coming back counts as an outage
        // (what was said meanwhile is lost and a restarted ROS side has forgotten the rest).
        private const float OutageSeconds = 1.5f;
        // How long after the connection is up before anything is sent: the connector registers
        // its publishers and services a frame or two after the socket opens, and whatever is
        // sent before that the endpoint throws away ("Not registered to publish topic").
        private const float SettleSeconds = 0.3f;
        private bool linkOpen, linkWasEverClosed, linkOpenedOnce;
        private float onlineSince = -1f, offlineSince = -1f;
        // Where the connection goes, to notice when the user sends it somewhere else.
        private string linkHost;
        private int linkPort;

        /// <summary>Fires after the plugin has resynchronised with ROS: at the first connection of a start without ROS, and after an outage.</summary>
        public event Action Reconnected;

        // The connector reconnects by itself and registers its topics again, but what the app
        // said while nothing was listening is gone, and so is whatever a restarted ROS side
        // knew. While the endpoint is away nothing is sent to the planning scenes (the objects
        // wait); when it is there, and was away at the start or for longer than a hiccup, every
        // planning scene is brought up to date.
        //
        // The user can also send the connection to another address (the Settings tab). That is
        // another ROS side, which has been told nothing, however quickly it answers: it gets
        // everything, as at a first connection.
        private void WatchConnection()
        {
            IRosBus bus = Context?.Ros;
            if (bus == null || scene == null) return;
            float now = Time.realtimeSinceStartup;
            IRosEndpoint endpoint = bus.Endpoint();
            if (endpoint != null)
            {
                string host = endpoint.Host;
                int port = endpoint.Port;
                if (linkHost != null && (port != linkPort || host != linkHost))
                {
                    linkOpen = linkOpenedOnce = false;
                    linkWasEverClosed = true;
                    onlineSince = offlineSince = -1f;
                    scene.Suspended = true;
                }
                linkHost = host;
                linkPort = port;
            }
            if (!bus.HasConnectionThread || bus.HasConnectionError)
            {
                if (offlineSince < 0f) offlineSince = now;
                onlineSince = -1f;
                linkOpen = false;
                linkWasEverClosed = true;
                scene.Suspended = true;
                return;
            }
            if (linkOpen) return;
            if (!linkWasEverClosed)
            {
                // Connected from the first moment (a fake bus in tests): nothing was missed.
                linkOpen = linkOpenedOnce = true;
                return;
            }
            if (onlineSince < 0f) onlineSince = now;
            if (now - onlineSince < SettleSeconds) return;

            bool outage = !linkOpenedOnce || (offlineSince >= 0f && onlineSince - offlineSince > OutageSeconds);
            linkOpen = linkOpenedOnce = true;
            offlineSince = -1f;
            scene.Suspended = false;
            if (outage) ResyncWithRos();
        }

        // A robot's move_group answered long after the app could first reach the endpoint: it
        // was still starting. Its planning scene missed what was sent meanwhile.
        internal void MoveItCameUpLate(MoveItRobotSession session)
        {
            if (scene == null || session?.Scene == null) return;
            Debug.Log($"[moveit] MoveIt of robot '{session.Robot.Id}' is up now: sending the environment to its planning scene again.");
            scene.Resync(session.Scene);
        }

        /// <summary>
        /// Bring every robot's planning scene and planner state back in step with ROS (see
        /// <see cref="PlanningSceneFanout.Resync"/>). Runs by itself when the connection returns
        /// after an outage.
        /// </summary>
        public void ResyncWithRos()
        {
            Debug.Log("[moveit] ROS is reachable: sending the environment and asking each planning scene what it holds.");
            scene?.Resync();
            if (sessions != null)
                foreach (MoveItRobotSession session in sessions) session.ConnectionRestored();
            Reconnected?.Invoke();
        }

        // A robot the plugin cannot serve (no ROS bus) is reported once, here, and gets no session.
        private MoveItRobotSession CreateSession(RobotInstance robot)
        {
            if (robot.Ros == null)
            {
                Debug.LogError($"[moveit] Robot '{robot.Id}' has no ROS bus; MoveIt cannot plan for it.", this);
                return null;
            }
            var session = new MoveItRobotSession(this, robot, settings);
            session.PreviewChanged += RefreshVerbs;
            ApplyLimitPolicy(session);
            session.Client.Refused += Report;
            session.Client.AdoptsReportedState = adoptRobotStateOnConnect;
            session.Client.Connect();

            // The robot's own planning scene joins the fan-out: it receives the environment as
            // it is now and is heard from here on.
            DropRobotlessScene();
            session.Scene = scene.AddRobot(robot);
            if (ReferenceEquals(session.Scene.Robot, robot)) GiveAttachListener(session);
            return session;
        }

        private void DisposeSession(MoveItRobotSession session)
        {
            session.Client.Refused -= Report;
            ClearResults(session.Robot);
            if (session.AttachListener != null)
            {
                // Whatever the robot was carrying goes back into the world before its mirror goes.
                session.AttachListener.Release();
                if (session.attachHost != null) Destroy(session.attachHost);
                else freeAttachListeners.Add(session.AttachListener);
                session.AttachListener = null;
            }
            if (scene != null && session.Scene != null && ReferenceEquals(session.Scene.Robot, session.Robot))
            {
                session.Scene.Attached = null;
                // A robot that goes because its scene is unloaded takes the app's obstacles out
                // of its planning scene on the way (a scene reset really resets); one that is
                // removed while the scene goes on leaves its planning scene as it is.
                RobotInstance robot = session.Robot;
                bool unloading = ReferenceEquals(robot, null) || robot == null || !robot.gameObject.scene.isLoaded;
                if (unloading) scene.Leave(session.Scene);
                else scene.Remove(session.Scene);
            }
            session.Dispose();
        }

        // Each robot hears its own attach notices and resolves links on itself only. A listener
        // that is in the scene serves the first robot that needs one; the others get a copy of it.
        private void GiveAttachListener(MoveItRobotSession session)
        {
            AttachedCollisionObjectListener listener = null;
            if (freeAttachListeners.Count > 0)
            {
                listener = freeAttachListeners[0];
                freeAttachListeners.RemoveAt(0);
            }
            else if (attachTemplate != null)
            {
                session.attachHost = SessionComponents.CreateHost(transform, "Attached objects", session.Robot);
                listener = SessionComponents.Copy(attachTemplate, session.attachHost);
                listener.SetSceneListener(scene.Mirror);
                session.attachHost.SetActive(true);
            }
            if (listener == null) return;
            listener.SetRobot(session.Robot);
            listener.Initialise(session.Robot.Ros);
            session.AttachListener = listener;
            session.Scene.Attached = listener;
        }

        private void DropRobotlessScene()
        {
            if (robotlessScene == null) return;
            if (robotlessAttachListener != null)
            {
                robotlessAttachListener.Release();
                freeAttachListeners.Insert(0, robotlessAttachListener);
                robotlessAttachListener = null;
            }
            scene?.Remove(robotlessScene);
            robotlessScene = null;
        }

        private void TryQueryPlanners()
        {
            if (sessions == null) return;
            foreach (MoveItRobotSession session in sessions) session.TryQueryPlanners();
        }

        // --- a robot's base was moved -------------------------------------------------

        // The trajectory being executed and the object in the gripper are where they are for
        // the base where it is: neither survives a move of the base.
        protected override string WhyBaseMustStay(RobotInstance robot)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null) return null;
            if (session.Client.IsFollowingExecution) return $"Robot '{robot.Id}' is executing a trajectory.";
            if (session.AttachListener != null && session.AttachListener.IsCarrying) return $"Robot '{robot.Id}' is carrying an object.";
            return null;
        }

        // The base class has stopped the preview and dropped the robot's plans. The start and
        // the goal go too (their ghosts stand where the robot was), and the robot's planning
        // scene is told where everything is now: all of it was expressed in the robot's
        // planning frame, which moved with the base.
        protected override void OnRobotBaseMoved(RobotInstance robot, Pose previousBase)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null) return;
            session.ResetPlanningState();
            GoalChanged?.Invoke();
            if (scene != null && session.Scene != null && ReferenceEquals(session.Scene.Robot, robot))
                scene.FrameMoved(session.Scene);
        }

        // --- start / goal ------------------------------------------------------------

        /// <summary>Capture the active robot's current pose as the plan's start state.</summary>
        public void SetStartFromRobot() => SetStartFromRobot(Context?.Robots?.Active);

        /// <summary>Capture a robot's current pose as its plan's start state.</summary>
        public void SetStartFromRobot(RobotInstance robot)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null)
            {
                Report(Unavailable(robot, null));
                return;
            }
            session.SetStartFromRobot();
            GoalChanged?.Invoke();
        }

        /// <summary>Capture the active robot's current pose as the goal.</summary>
        public void SetGoalFromRobot() => SetGoalFromRobot(Context?.Robots?.Active);

        /// <summary>Capture a robot's current pose as the goal (the user has posed it with the IK handle).</summary>
        public void SetGoalFromRobot(RobotInstance robot)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null)
            {
                Report(Unavailable(robot, null));
                return;
            }
            session.SetGoalFromRobot();
            GoalChanged?.Invoke();
        }

        /// <summary>Capture the goal only; the start has its own verb (set-start).</summary>
        public override InteractionRefusal SetGoal(RobotInstance robot, ISelectable endEffector)
        {
            if (SessionFor(robot) == null) return Unavailable(robot, endEffector);
            SetGoalFromRobot(robot);
            return InteractionRefusal.None;
        }

        /// <summary>Forget the active robot's start and goal.</summary>
        public void ResetPlanningState() => ResetPlanningState(Context?.Robots?.Active);

        public void ResetPlanningState(RobotInstance robot)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null) return;
            // The plans' handles ride the goal ghost, which is about to go.
            ReleaseHandles(robot);
            session.ResetPlanningState();
            GoalChanged?.Invoke();
        }

        private static InteractionRefusal Unavailable(RobotInstance robot, ISelectable target, Transform where = null)
        {
            Vector3 at = target?.GameObject != null ? target.GameObject.transform.position
                       : where != null ? where.position : Vector3.zero;
            string reason = robot != null ? $"MoveIt is not connected for robot '{robot.Id}'." : "MoveIt is not connected.";
            return InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable, reason, at);
        }

        // --- plan / preview / execute ------------------------------------------------

        public override void RequestPlan(RobotInstance robot, PlanPreferences preferences, Action<PlanResult> done)
        {
            MoveItRobotSession session = SessionFor(robot);
            if (session == null)
            {
                // No robot to plan for, or one MoveIt does not serve: say so where the user is.
                InteractionRefusal unavailable = robot != null
                    ? Unavailable(robot, null, robot.Model?.EndEffector)
                    : InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable, "There is no robot to plan for.", transform.position);
                PlanFailed?.Invoke(unavailable.Reason);
                Report(unavailable);
                done?.Invoke(null);
                return;
            }
            if (!session.HasStart || !session.HasGoal)
            {
                FailPlan(robot, "Both start and goal states must be set before planning.", RefusalCode.PlanFailed,
                    session.HasStart ? "Set a goal first (Set Goal on the end effector)." : "Set a start first (Set Start on the end effector).");
                done?.Invoke(null);
                return;
            }

            session.StopPreview();
            session.Client.RequestPlan(session.StartState, session.GoalState, preferences, result =>
            {
                if (result == null) { done?.Invoke(null); return; }
                // The robot may have left while MoveIt was planning.
                if (SessionFor(robot) != session) { done?.Invoke(null); return; }
                PublishResult(result, robot);
                session.HandleAnchor(transform.position, out Vector3 position, out Transform anchor);
                TrajectorySelectable handle = PlaceHandle(result, position, anchor);
                // The next thing the user does with a plan is preview or execute it. A plan
                // that arrives for a robot the user is not working with leaves the selection alone.
                if (selectNewPlan && handle != null && Context?.Selection != null && ReferenceEquals(Context.Robots?.Active, robot))
                    Context.Selection.Select(handle);
                // The client checked the plan against the joint limits when it arrived; a plan
                // outside them is still previewed (the robot model clamps every joint write).
                ReportLimitViolations(result, robot);
                Preview(result);
                done?.Invoke(result);
            }, message => FailPlan(robot, message, session.Client.IsOnline ? RefusalCode.PlanFailed : RefusalCode.PlannerUnavailable,
                                   $"Planning failed: {message}."));
        }

        // A plan that did not come is something the user is told, wherever they pressed "plan":
        // the tab shows the text (PlanFailed), the router gets the refusal at the end effector.
        private void FailPlan(RobotInstance robot, string message, RefusalCode code, string reason)
        {
            PlanFailed?.Invoke(message);
            Transform endEffector = robot != null ? robot.Model?.EndEffector : null;
            Report(InteractionRefusal.Refuse(code, reason, endEffector != null ? endEffector.position : transform.position, endEffector));
        }

        public override void Preview(PlanResult plan)
        {
            if (plan == null || !plan.HasTrajectory)
            {
                Report(InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, "This plan has no motion to preview.", transform.position));
                return;
            }
            RobotInstance robot = RobotOf(plan);
            MoveItRobotSession session = SessionFor(robot);
            if (session == null)
            {
                Report(InteractionRefusal.Refuse(RefusalCode.PlannerUnavailable, "The robot this plan was made for is not here any more.", transform.position));
                return;
            }
            if (!session.Preview(plan))
            {
                Transform endEffector = robot.Model?.EndEffector;
                Report(InteractionRefusal.Refuse(RefusalCode.NotControllable, $"Robot '{robot.Id}' cannot play a preview (it has no trajectory player).",
                    endEffector != null ? endEffector.position : transform.position));
            }
        }

        public override void StopPreview(RobotInstance robot)
        {
            if (sessions == null) return;
            if (robot != null) SessionFor(robot)?.StopPreview();
            else foreach (MoveItRobotSession session in sessions) session.StopPreview();
        }

        /// <summary>A MoveIt preview plays in a loop on the robot's blue copy until it is stopped.</summary>
        public override bool IsPreviewPlaying(PlanResult plan)
        {
            if (plan == null) return false;
            MoveItRobotSession session = SessionFor(RobotOf(plan));
            return session != null && session.IsPreviewing && ReferenceEquals(session.PreviewedPlan, plan);
        }

        public override void Execute(PlanResult plan, Action<ExecutionStatus> status)
        {
            RobotInstance robot = RobotOf(plan);
            MoveItRobotSession session = SessionFor(robot);
            if (session == null)
            {
                status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "MoveIt is not connected."));
                return;
            }
            session.StopPreview();
            MoveItPlanningClient client = session.Client;
            if (!client.CanExecute)
            {
                status?.Invoke(new ExecutionStatus(ExecutionPhase.Unavailable, "The robot's profile names no trajectory command topic."));
                return;
            }
            // The client publishes, so it gets the policy as it stands now (the checkbox may
            // have been changed in the inspector since registration).
            ApplyLimitPolicy(session);
            if (!MayExecute(plan, robot, status)) return;
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Sending));
            // The Unity robot follows the real one's joint states for the length of the
            // execution; the client reports when the robot has reached the trajectory's end.
            bool sent = client.Execute(plan, (reached, problem) => status?.Invoke(reached
                ? new ExecutionStatus(ExecutionPhase.Succeeded, "The robot reached the end of the trajectory.")
                : new ExecutionStatus(ExecutionPhase.Failed, $"Execution did not finish: {problem}.")));
            if (!sent)
            {
                status?.Invoke(new ExecutionStatus(ExecutionPhase.Failed, "No planned trajectory to execute."));
                return;
            }
            status?.Invoke(new ExecutionStatus(ExecutionPhase.Executing, "Trajectory published to the controller."));
        }

        // The clients are where the trajectories are published, so they hold the same policy.
        protected override void OnLimitPolicyChanged()
        {
            if (sessions == null) return;
            foreach (MoveItRobotSession session in sessions) ApplyLimitPolicy(session);
        }

        private void ApplyLimitPolicy(MoveItRobotSession session)
        {
            session.Client.AllowExecutionOutsideLimits = AllowExecutionOutsideLimits;
            session.Client.LimitTolerance = LimitTolerance;
        }

        // The tab holds the active robot's choices; a plan asked for another robot uses the defaults.
        protected override PlanPreferences DefaultPreferences(RobotInstance robot) =>
            tab != null && ReferenceEquals(robot, Context?.Robots?.Active) ? tab.Preferences : new PlanPreferences
            {
                PipelineId = settings.planningPipelineId,
                PlannerId = settings.defaultPlannerId,
                Attempts = settings.defaultNumPlanningAttempts,
                AllowedTimeSeconds = settings.defaultAllowedPlanningTime
            };

        protected override void BuildSettingsTab(RectTransform content)
        {
            tab?.Dispose();
            tab = null;
            if (content == null) return;   // a headless UI host (tests) passes no content
            tab = new PlannerSettingsTab(this, content);
        }

        public PlannerSettingsTab Tab => tab;
    }
}
