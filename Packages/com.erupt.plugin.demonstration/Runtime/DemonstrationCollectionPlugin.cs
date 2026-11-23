using System;
using RosMessageTypes.Std;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;
using Erupt.Robot;

namespace Erupt.Plugins.Demonstration
{
    /// <summary>
    /// Demonstration collection as an ERUPT demonstration plugin (Guidelines Part 5), for
    /// any number of robots. The base class feeds the interaction samples and adds the
    /// tab; this class owns, per robot, a joint-space <see cref="DemonstrationRecorder"/> and
    /// a <see cref="JointTrajectoryPlayer"/> for replay (<see cref="RobotSession"/>), plus the
    /// info log, and points the hand/gripper mirrors and the point-cloud publisher at the
    /// context. A robot's demonstrations, virtual joint state and interaction flag go out in
    /// its ROS namespace (<c>joint_trajectory</c>, <c>virtual_joint_state</c>,
    /// <c>interaction</c> on <see cref="RobotInstance.Ros"/>), while the record trigger, the
    /// info log and the point cloud are global and use <c>IEruptContext.Ros</c>. One
    /// demonstration is recorded at a time; the members without a robot argument address the
    /// active robot, which the mirrors follow too.
    /// </summary>
    public class DemonstrationCollectionPlugin : DemonstrationPlugin
    {
        [Header("Robot")]
        [Tooltip("Header frame_id on published demonstrations. Empty = the robot root's name.")]
        [SerializeField] private string frameId = "";

        [Header("Recording")]
        [SerializeField, Min(0.01f)] private float recordInterval = 0.1f;
        [SerializeField, Min(0.01f)] private float publishStateInterval = 0.2f;
        [Tooltip("Stream the virtual robot's joint state on virtual_joint_state, in the robot's namespace.")]
        [SerializeField] private bool publishVirtualState = true;
        [Tooltip("Any message here toggles recording on the active robot.")]
        [SerializeField] private string recordStartTopic = "/record_start";

        [Header("Info")]
        [Tooltip("std_msgs/String messages here are logged and shown in the tab.")]
        [SerializeField] private string userInfoTopic = "/user_info";

        [Header("Components (siblings by default)")]
        [Tooltip("Replays demonstrations. Serves the first robot; every further robot gets a copy with the same settings.")]
        [SerializeField] private JointTrajectoryPlayer player;
        [SerializeField] private PointCloudPublisher pointCloud;

        /// <summary>What the plugin keeps for one robot.</summary>
        public sealed class RobotSession
        {
            public RobotInstance Robot { get; internal set; }
            public DemonstrationRecorder Recorder { get; internal set; }
            public JointTrajectoryPlayer Player { get; internal set; }
            public bool IsReplaying { get; internal set; }
            // The object that holds this robot's copy of the player; null for the session that
            // uses the plugin's own.
            internal GameObject host;
            internal bool usesPluginPlayer;
        }

        private RobotSessions<RobotSession> sessions;
        private bool pluginPlayerTaken;
        private RobotSession recordingSession;
        private DemosTab tab;

        /// <summary>The id other plugins name in <c>DependsOn</c>.</summary>
        public const string PluginId = "demonstration";

        public override string Id => PluginId;
        public override string DisplayName => "Demonstrations";
        protected override int TabOrder => 30;

        /// <summary>Every robot's session, in registration order; null before registration.</summary>
        public RobotSessions<RobotSession> Sessions => sessions;
        /// <summary>The session of the robot the user is working with (the one the tab shows).</summary>
        public RobotSession ActiveSession => sessions?.Active;
        /// <summary>A robot's session; null for a robot that has none.</summary>
        public RobotSession SessionFor(RobotInstance robot) => sessions?.For(robot);

        /// <summary>The active robot's recorder.</summary>
        public DemonstrationRecorder Recorder => ActiveSession?.Recorder;
        public InfoLog Log { get; private set; }
        /// <summary>The active robot's replay player; the plugin's own idle component while there is no robot.</summary>
        public JointTrajectoryPlayer Player => ActiveSession != null ? ActiveSession.Player : player;
        public DemosTab Tab => tab;
        /// <summary>Whether the active robot is replaying a demonstration.</summary>
        public bool IsReplaying => ActiveSession != null && ActiveSession.IsReplaying;
        /// <summary>The robot whose demonstration is being recorded; null when none is.</summary>
        public RobotInstance RecordingRobot => recordingSession?.Robot;
        /// <summary>Interaction samples the base class delivered during the current/last demonstration.</summary>
        public int InteractionSampleCount { get; private set; }

        /// <summary>Recording or replay state changed.</summary>
        public event Action StateChanged;

        private void Awake()
        {
            if (player == null) player = GetComponent<JointTrajectoryPlayer>();
            if (pointCloud == null) pointCloud = GetComponent<PointCloudPublisher>();
        }

        protected override void OnRegister(IEruptContext context)
        {
            if (player == null) player = GetComponent<JointTrajectoryPlayer>();
            IRobotRegistry robots = context.Robots;
            if (robots == null || robots.All.Count == 0)
                Debug.LogError("[demonstration] No robot in the context; demonstrations cannot be recorded.", this);

            pluginPlayerTaken = false;
            sessions = new RobotSessions<RobotSession>(robots, CreateSession, DisposeSession);

            Log = new InfoLog(context.Ros, userInfoTopic);
            Log.Start();

            context.Ros.Subscribe<BoolMsg>(recordStartTopic, OnRecordStart);

            if (pointCloud == null) pointCloud = GetComponent<PointCloudPublisher>();
            pointCloud?.Initialise(context.Ros);

            // A mirror with a robot assigned in the inspector keeps it; the others follow the active robot.
            PointMirrorsAt(robots?.Active);
            if (robots != null) robots.ActiveChanged += PointMirrorsAt;

            base.OnRegister(context);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            StopDemonstration(null);
            base.OnUnregister(context);
            if (context.Robots != null) context.Robots.ActiveChanged -= PointMirrorsAt;
            context.Ros?.Unsubscribe<BoolMsg>(recordStartTopic, OnRecordStart);
            sessions?.Dispose();
            sessions = null;
            Log?.Dispose();
            tab?.Dispose();
            tab = null;
        }

        // A robot without a model or a bus is reported once, here, and gets no session.
        private RobotSession CreateSession(RobotInstance robot)
        {
            IRobotModel model = robot.Model;
            if (model == null || robot.Ros == null)
            {
                Debug.LogError($"[demonstration] Robot '{robot.Id}' has no model or no ROS bus; its demonstrations cannot be recorded.", this);
                return null;
            }

            var session = new RobotSession { Robot = robot };
            string frame = string.IsNullOrEmpty(frameId)
                ? (model.Root != null ? model.Root.name : "robot")
                : frameId;
            session.Recorder = new DemonstrationRecorder(robot.Ros, model, frame, robot.JointStatesTopic, robot.Names)
            {
                RecordInterval = recordInterval,
                PublishStateInterval = publishStateInterval,
                PublishState = publishVirtualState
            };
            session.Recorder.Changed += RaiseStateChanged;
            session.Recorder.Refused += ReportRefusal;
            session.Recorder.Start();

            if (!pluginPlayerTaken)
            {
                pluginPlayerTaken = true;
                session.usesPluginPlayer = true;
                session.Player = player;
            }
            else if (player != null)
            {
                session.host = SessionComponents.CreateHost(transform, "Demonstrations", robot);
                session.Player = SessionComponents.Copy(player, session.host);
                session.host.SetActive(true);
            }
            if (session.Player != null) session.Player.SetRobot(model);
            return session;
        }

        private void DisposeSession(RobotSession session)
        {
            StopReplay(session);
            if (ReferenceEquals(recordingSession, session)) StopDemonstration(session.Robot);
            session.Recorder.Changed -= RaiseStateChanged;
            session.Recorder.Refused -= ReportRefusal;
            session.Recorder.Dispose();
            if (session.usesPluginPlayer) pluginPlayerTaken = false;
            else if (session.host != null) Destroy(session.host);
        }

        private void PointMirrorsAt(RobotInstance robot)
        {
            foreach (var mirror in GetComponentsInChildren<HandMirror>(true)) mirror.SetRobot(robot);
            foreach (var mirror in GetComponentsInChildren<Robotiq2fGripperMirror>(true)) mirror.SetRobot(robot);
        }

        private void Update()
        {
            if (!IsRegistered || sessions == null) return;
            foreach (RobotSession session in sessions)
            {
                session.Recorder.Tick(Time.timeAsDouble);
                // JointTrajectoryPlayer loops; a demonstration replays once.
                if (session.IsReplaying && session.Player != null && session.Player.HasFinishedOneLoop()) StopReplay(session);
            }
        }

        // --- DemonstrationPlugin ---------------------------------------------------------

        protected override void OnSample(InteractionSample sample) => InteractionSampleCount++;

        public override void StartDemonstration(RobotInstance robot)
        {
            RobotSession session = SessionFor(robot);
            if (session == null)
            {
                RefuseFor(robot, robot != null ? $"Robot '{robot.Id}' cannot record a demonstration (it has no robot model or no ROS connection)."
                                     : "There is no robot to record a demonstration on.");
                return;
            }
            if (IsRecording)
            {
                RefuseFor(robot, ReferenceEquals(recordingSession, session)
                    ? "A demonstration is already being recorded on this robot."
                    : $"A demonstration is being recorded on robot '{recordingSession?.Robot?.Id}'; stop it first.");
                return;
            }
            StopReplay(session);
            InteractionSampleCount = 0;
            recordingSession = session;
            session.Recorder.StartRecording(Time.timeAsDouble);
            SetRecording(true);
            RaiseStateChanged();
        }

        public override void StopDemonstration(RobotInstance robot)
        {
            RobotSession session = recordingSession;
            if (session == null || !IsRecording) return;
            if (robot != null && !ReferenceEquals(session.Robot, robot)) return;
            recordingSession = null;
            session.Recorder.StopRecording(Time.timeAsDouble);
            SetRecording(false);
            RaiseStateChanged();
        }

        public override void Publish(RobotInstance robot)
        {
            RobotSession session = SessionFor(robot);
            if (session == null || !session.Recorder.Publish())
            {
                Debug.LogWarning("[demonstration] Nothing to publish; record a demonstration first.", this);
                RefuseFor(robot, "Nothing to publish; record a demonstration first.");
            }
        }

        /// <summary>Play the active robot's last demonstration once on the virtual robot.</summary>
        public void Replay() => Replay(Context?.Robots?.Active);

        /// <summary>Play a robot's last demonstration once on that robot.</summary>
        public void Replay(RobotInstance robot)
        {
            RobotSession session = SessionFor(robot);
            if (session?.Recorder.LastDemonstration == null)
            {
                RefuseFor(robot, "There is no demonstration to replay; record one first.");
                return;
            }
            if (session.Player == null)
            {
                RefuseFor(robot, $"Robot '{robot.Id}' cannot replay a demonstration (it has no trajectory player).");
                return;
            }
            if (ReferenceEquals(recordingSession, session))
            {
                RefuseFor(robot, "A demonstration is being recorded; stop it before replaying.");
                return;
            }
            ReportDemonstrationOutsideLimits(session);
            session.IsReplaying = true;
            session.Player.RestartReplay(session.Recorder.LastDemonstration);
            RaiseStateChanged();
        }

        /// <summary>Stop the active robot's replay.</summary>
        public void StopReplay() => StopReplay(ActiveSession);

        /// <summary>Stop a robot's replay.</summary>
        public void StopReplay(RobotInstance robot) => StopReplay(SessionFor(robot));

        private void StopReplay(RobotSession session)
        {
            if (session == null || !session.IsReplaying) return;
            session.IsReplaying = false;
            if (session.Player != null) session.Player.StopReplay();
            RaiseStateChanged();
        }

        protected override void BuildDemosTab(RectTransform content)
        {
            tab?.Dispose();
            tab = null;
            if (content == null) return;   // headless UI host (tests)
            tab = new DemosTab(this, content);
        }

        // --- helpers ---------------------------------------------------------------------

        /// <summary>Any message on the record-start topic toggles recording on the active robot, whatever its value.</summary>
        private void OnRecordStart(BoolMsg _)
        {
            if (IsRecording) StopDemonstration(null);
            else StartDemonstration(Context?.Robots?.Active);
        }

        private void RaiseStateChanged() => StateChanged?.Invoke();

        // A refusal at the robot it is about (its end effector), where the user is working.
        private void RefuseFor(RobotInstance robot, string reason)
        {
            Transform endEffector = robot != null && robot.Model != null ? robot.Model.EndEffector : null;
            ReportRefusal(InteractionRefusal.Refuse(RefusalCode.VerbUnavailable, reason,
                endEffector != null ? endEffector.position : transform.position));
        }

        private void ReportRefusal(InteractionRefusal refusal) => Context?.Router?.ReportRefusal(refusal);

        // The demonstration was checked against the joint limits when it was finished. One
        // that leaves them still replays (the robot model clamps every joint write); the user
        // is told which joint.
        private void ReportDemonstrationOutsideLimits(RobotSession session)
        {
            var violations = session.Recorder.LastDemonstrationViolations;
            if (violations == null || violations.Count == 0) return;

            Transform link = null;
            JointLimitViolation? worst = JointLimitViolation.Worst(violations);
            IRobotModel model = session.Robot.Model;
            if (worst != null && model != null) model.TryGetJointLink(worst.Value.Joint, out link);
            ReportRefusal(InteractionRefusal.Refuse(
                RefusalCode.PlanOutsideLimits,
                $"This demonstration is outside the joint limits: {JointLimitViolation.Describe(violations)}. The replay stays inside the limits.",
                link != null ? link.position : Vector3.zero,
                link));
        }
    }
}
