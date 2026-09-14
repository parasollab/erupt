using System;
using RosMessageTypes.Std;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Plugins;

namespace Erupt.Plugins.Rader
{
    /// <summary>
    /// RADER as an ERUPT demonstration plugin (Guidelines Part 5, Teach mode). The base class
    /// gates on Teach and binds the "correct" verb; this class owns the joint-space
    /// <see cref="DemonstrationRecorder"/>, replay through a <see cref="JointTrajectoryPlayer"/>,
    /// the FERL feedback handshake, the info log, and re-points the hand/gripper mirrors and
    /// the point-cloud publisher at the context. Everything ROS goes through
    /// <c>IEruptContext.Ros</c>; the robot is <c>IEruptContext.Robot</c>.
    /// </summary>
    public class RaderPlugin : DemonstrationPlugin
    {
        [Header("Robot")]
        [Tooltip("Topic namespace: /{ns}/joint_trajectory, /{ns}/virtual_joint_state, /{ns}/interaction, /{ns}/joint_states. Empty = no prefix.")]
        [SerializeField] private string robotNamespace = "";
        [Tooltip("Header frame_id on published demonstrations. Empty = the robot root's name.")]
        [SerializeField] private string frameId = "";

        [Header("Recording")]
        [SerializeField, Min(0.01f)] private float recordInterval = 0.1f;
        [SerializeField, Min(0.01f)] private float publishStateInterval = 0.2f;
        [Tooltip("Stream the virtual robot's joint state on /{ns}/virtual_joint_state.")]
        [SerializeField] private bool publishVirtualState = true;
        [Tooltip("Any message here toggles recording (Teach mode only), as in RADER.")]
        [SerializeField] private string recordStartTopic = "/record_start";

        [Header("FERL / info")]
        [SerializeField] private string feedbackRequestTopic = "/feedback_request";
        [SerializeField] private string satisfactionRequestTopic = "/req_satisfied";
        [SerializeField] private string feedbackResponseTopic = "/feedback_response";
        [SerializeField] private string userInfoTopic = "/user_info";

        [Header("Components (siblings by default)")]
        [SerializeField] private JointTrajectoryPlayer player;
        [SerializeField] private PointCloudPublisher pointCloud;

        private DemosTab tab;
        private bool replaying;

        public override string Id => "rader";
        public override string DisplayName => "RADER";

        public DemonstrationRecorder Recorder { get; private set; }
        public FerlFeedback Feedback { get; private set; }
        public InfoLog Log { get; private set; }
        public JointTrajectoryPlayer Player => player;
        public DemosTab Tab => tab;
        public bool IsReplaying => replaying;
        /// <summary>Interaction samples the base class delivered during the current/last demonstration.</summary>
        public int InteractionSampleCount { get; private set; }

        /// <summary>Recording, replay or Teach state changed.</summary>
        public event Action StateChanged;

        private void Awake()
        {
            if (player == null) player = GetComponent<JointTrajectoryPlayer>();
            if (pointCloud == null) pointCloud = GetComponent<PointCloudPublisher>();
        }

        protected override void OnRegister(IEruptContext context)
        {
            if (context.Robot == null)
            {
                Debug.LogError("[rader] No robot in the context; demonstrations cannot be recorded.", this);
            }
            else
            {
                string frame = string.IsNullOrEmpty(frameId)
                    ? (context.Robot.Root != null ? context.Robot.Root.name : "robot")
                    : frameId;
                Recorder = new DemonstrationRecorder(context.Ros, context.Robot, robotNamespace, frame)
                {
                    RecordInterval = recordInterval,
                    PublishStateInterval = publishStateInterval,
                    PublishState = publishVirtualState
                };
                Recorder.Changed += RaiseStateChanged;
                Recorder.Start();
            }

            Feedback = new FerlFeedback(context.Ros, feedbackRequestTopic, satisfactionRequestTopic, feedbackResponseTopic);
            Feedback.RequestReceived += SummonTab;
            Feedback.Start();

            Log = new InfoLog(context.Ros, userInfoTopic);
            Log.Start();

            context.Ros.Subscribe<BoolMsg>(recordStartTopic, OnRecordStart);

            if (player == null) player = GetComponent<JointTrajectoryPlayer>();
            if (player != null && context.Robot != null) player.SetRobot(context.Robot);
            if (pointCloud == null) pointCloud = GetComponent<PointCloudPublisher>();
            pointCloud?.Initialise(context.Ros);

            foreach (var mirror in GetComponentsInChildren<HandMirror>(true)) mirror.SetRobot(context.Robot);
            foreach (var mirror in GetComponentsInChildren<Robotiq2fGripperMirror>(true)) mirror.SetRobot(context.Robot);

            base.OnRegister(context);
        }

        protected override void OnUnregister(IEruptContext context)
        {
            StopReplay();
            StopDemonstration();
            base.OnUnregister(context);
            context.Ros?.Unsubscribe<BoolMsg>(recordStartTopic, OnRecordStart);
            if (Recorder != null) { Recorder.Changed -= RaiseStateChanged; Recorder.Dispose(); }
            if (Feedback != null) { Feedback.RequestReceived -= SummonTab; Feedback.Dispose(); }
            Log?.Dispose();
            tab?.Dispose();
            tab = null;
        }

        protected override void OnModeChanged(AppMode mode)
        {
            base.OnModeChanged(mode);
            RaiseStateChanged();
        }

        private void Update()
        {
            if (!IsRegistered) return;
            Recorder?.Tick(Time.timeAsDouble);
            // JointTrajectoryPlayer loops; RADER replays once.
            if (replaying && player != null && player.HasFinishedOneLoop()) StopReplay();
        }

        // --- DemonstrationPlugin ---------------------------------------------------------

        protected override void OnSample(InteractionSample sample) => InteractionSampleCount++;

        public override void StartDemonstration()
        {
            if (Recorder == null) return;
            if (!InTeachMode)
            {
                Debug.LogWarning("[rader] Demonstrations are recorded in Teach mode only.", this);
                return;
            }
            if (IsRecording) return;
            StopReplay();
            InteractionSampleCount = 0;
            Recorder.StartRecording(Time.timeAsDouble);
            SetRecording(true);
            RaiseStateChanged();
        }

        public override void StopDemonstration()
        {
            if (Recorder == null || !IsRecording) return;
            Recorder.StopRecording(Time.timeAsDouble);
            SetRecording(false);
            RaiseStateChanged();
        }

        public override void Publish()
        {
            if (Recorder == null || !Recorder.Publish())
                Debug.LogWarning("[rader] Nothing to publish; record a demonstration first.", this);
        }

        /// <summary>Play the last demonstration once on the virtual robot.</summary>
        public void Replay()
        {
            if (Recorder?.LastDemonstration == null || player == null) return;
            if (IsRecording) return;
            replaying = true;
            player.RestartReplay(Recorder.LastDemonstration);
            RaiseStateChanged();
        }

        public void StopReplay()
        {
            if (!replaying) return;
            replaying = false;
            if (player != null) player.StopReplay();
            RaiseStateChanged();
        }

        protected override void BuildDemosTab(RectTransform content)
        {
            if (content == null) return;   // headless UI host (tests)
            tab = new DemosTab(this, content);
        }

        // --- helpers ---------------------------------------------------------------------

        /// <summary>RADER convention: any message on the record-start topic toggles recording.</summary>
        private void OnRecordStart(BoolMsg _)
        {
            if (IsRecording) StopDemonstration();
            else StartDemonstration();
        }

        private void SummonTab() => Context?.Ui?.SummonTab(DemosTabId);

        private void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
