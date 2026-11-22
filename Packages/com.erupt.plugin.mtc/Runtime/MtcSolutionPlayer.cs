using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Moveit;
using RosMessageTypes.Sensor;
using RosMessageTypes.EruptInterfaces;
using RosMessageTypes.Trajectory;
using Erupt.Environment;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ros;
using Erupt.Plugins.MoveIt;

namespace Erupt.Plugins.Mtc
{
    /// <summary>
    /// Previews an MTC solution on the robot: the whole solution, one stage's steps of it, or a
    /// stage's partial / failed solution from its own <c>start_scene</c>. Objects the solution
    /// attaches ride the link at the planned pose, through the <see cref="EnvironmentRegistry"/>;
    /// everything moved is put back when the preview ends and nothing reaches MoveIt meanwhile.
    /// While the pick/place client executes a solution the player instead shows the real motion:
    /// the robot follows the live joint states, and each attach/detach step of the solution is
    /// applied the moment the server reports the step done.
    /// </summary>
    public class MtcSolutionPlayer : MonoBehaviour
    {
        [SerializeField] private EnvironmentRegistry registry;

        [Tooltip("Seconds a partial preview (a stage, or a solution from its start scene) holds its final pose before the robot is restored.")]
        [SerializeField, Min(0f)] private float stagePreviewHoldSeconds = 1.5f;

        [Header("Live execution")]
        [Tooltip("While this client executes a solution the Unity robot follows its joint state topic, so the real " +
                 "motion is shown (previews are stopped). The plugin hands its client over at registration; found " +
                 "in the scene when empty.")]
        [SerializeField] private PickPlaceClient pickPlaceClient;

        // The attach mirror of the robot being executed (AttachedCollisionObjectListener.For).
        // While a solution executes, its attach/detach steps are applied through it as soon as
        // the server reports each step done, so the carried object is released when the hand
        // opens even when the planning-scene watcher's notice lags behind the robot.
        private AttachedCollisionObjectListener attachedListener;
        [Tooltip("Apply the executing solution's attach/detach steps from the execution feedback.")]
        [SerializeField] private bool applySolutionStepsDuringExecution = true;

        private RobotInstance robotInstance;

        /// <summary>
        /// The robot solutions are played on (the MTC plugin passes the active robot at
        /// registration). Its name rule maps the solution's joint and link names, and the live
        /// joint states are read from its joint state topic on its namespaced bus; while the
        /// robot has no bus <see cref="StartLiveMirror"/> does nothing.
        /// </summary>
        public void SetRobot(RobotInstance target)
        {
            if (ReferenceEquals(robotInstance, target)) return;
            StopLiveMirror();
            robotInstance = target;
        }

        private IRobotModel Robot => robotInstance != null ? robotInstance.Model : null;

        // The bus and topic the live mirror is subscribed on, kept so it unsubscribes from the same pair.
        private IRosBus mirrorBus;
        private string mirrorTopic;

        // The client whose phase changes this player follows (subscribed), or null.
        private PickPlaceClient boundClient;
        private bool mirroring;
        private JointStateMsg latestJointState;
        /// <summary>True while the Unity robot follows the live joint states (during execution).</summary>
        public bool IsMirroring => mirroring;

        // The solution being executed and how far its scene diffs have been applied.
        private bool trackingExecution;
        private uint trackedSolutionId;
        private bool solutionRequested;
        private SolutionMsg executingSolution;
        private int reportedStep = -1;       // highest sub_trajectory index the server reported done
        private int appliedThroughStep = -1; // highest index whose scene_diff attach/detach was applied
        /// <summary>Index of the last executed sub-trajectory whose attach/detach was applied, or -1.</summary>
        public int ExecutionStepApplied => appliedThroughStep;

        /// <summary>Why the last preview could not play, or null. Raised through <see cref="OnProblem"/>.</summary>
        public string LastProblem { get; private set; }
        public event System.Action<string> OnProblem;

        /// <summary>
        /// Something the user should be told: while mirroring, the real robot reported a joint
        /// outside the robot model's limits (once per joint, then rarely).
        /// </summary>
        public event System.Action<InteractionRefusal> OnRefusal;
        private readonly LimitMismatchMonitor mismatches = new LimitMismatchMonitor();

        private bool isPlaying;
        /// <summary>True while a preview is animating the robot (or holding its end state).</summary>
        public bool IsPlaying => isPlaying;
        private Coroutine playRoutine;
        private string[] savedNames;
        private float[] savedPositions;

        // Objects the preview has moved — reparented to a robot link (scene_diff or start_scene
        // attach) or placed at a start_scene world pose — with everything needed to put them
        // back when the preview ends.
        private class PreviewAttach
        {
            public bool attached;
            public GameObject go;
            public Transform originalParent;
            public Vector3 originalPos;
            public Quaternion originalRot;
            public Vector3 originalLocalScale;
            public bool wasPaused;
        }
        private readonly Dictionary<string, PreviewAttach> previewAttached = new();

        private void Start()
        {
            if (boundClient != null) return; // already handed over by the plugin
            if (pickPlaceClient == null) pickPlaceClient = FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
            SetPickPlaceClient(pickPlaceClient);
        }

        private void OnDestroy()
        {
            if (boundClient != null) boundClient.OnPhaseChanged -= OnClientPhaseChanged;
            boundClient = null;
        }

        private void Update()
        {
            if (mirroring) ApplyLatestJointState();
        }

        /// <summary>
        /// Follow this client: while it executes a solution the robot mirrors the live joint
        /// states and the solution's attach/detach steps are applied from its step feedback.
        /// </summary>
        public void SetPickPlaceClient(PickPlaceClient client)
        {
            if (boundClient == client) return;
            if (boundClient != null) boundClient.OnPhaseChanged -= OnClientPhaseChanged;
            StopLiveMirror();
            StopExecutionTracking();

            pickPlaceClient = client;
            boundClient = client;
            if (boundClient == null) return;
            boundClient.OnPhaseChanged += OnClientPhaseChanged;
            OnClientPhaseChanged();
        }

        private void OnClientPhaseChanged()
        {
            if (isActiveAndEnabled && boundClient != null && boundClient.Phase == PickPlacePhase.Executing)
            {
                StartLiveMirror();
                StartExecutionTracking();
            }
            else
            {
                StopLiveMirror();
                StopExecutionTracking();
            }
        }

        // ---- attach/detach from the executing solution ---------------------------------------
        //
        // MoveIt applies each sub-trajectory's scene diff (attach, detach) when that step finishes,
        // and the server reports the step done at the same moment through the execution feedback.
        // The planning-scene watcher's notice of the same attach/detach arrives on a separate,
        // slower path (full planning-scene messages with every mesh in the scene), so mirroring the
        // diff from the feedback keeps the Unity object in step with the robot it rides.

        private void StartExecutionTracking()
        {
            StopExecutionTracking();
            if (!applySolutionStepsDuringExecution || boundClient == null) return;
            uint solutionId = boundClient.ExecutingSolutionId;
            if (solutionId == 0) return; // plan-and-execute goals: no solution to read, the listener alone mirrors
            // The attach mirror of the robot being executed: its links, its planning scene.
            attachedListener = AttachedCollisionObjectListener.For(robotInstance);
            if (attachedListener == null) return;
            attachedListener.syncRobot = SyncMirroredJoints;

            trackingExecution = true;
            trackedSolutionId = solutionId;
            attachedListener.ForgetExpected();
            boundClient.OnExecutionFeedback += OnExecutionFeedback;
            RequestExecutingSolution();
        }

        // Feedback for the first steps may land before the solution does; ApplyReportedSteps
        // catches up once it is here. A failed fetch is retried on the next feedback.
        private void RequestExecutingSolution()
        {
            if (solutionRequested || executingSolution != null) return;
            solutionRequested = true;
            uint solutionId = trackedSolutionId;
            boundClient.FetchSolution(solutionId, solution =>
            {
                if (!trackingExecution || trackedSolutionId != solutionId) return;
                executingSolution = solution;
                ApplyReportedSteps();
            }, problem =>
            {
                if (!trackingExecution || trackedSolutionId != solutionId) return;
                solutionRequested = false;
                Debug.LogWarning($"[MtcSolutionPlayer] executing solution {solutionId} unavailable ({problem}); attach/detach follow the watcher until it is.");
            });
        }

        private void StopExecutionTracking()
        {
            if (trackingExecution && boundClient != null) boundClient.OnExecutionFeedback -= OnExecutionFeedback;
            trackingExecution = false;
            trackedSolutionId = 0;
            solutionRequested = false;
            executingSolution = null;
            reportedStep = -1;
            appliedThroughStep = -1;
        }

        // sub_id is the index of the sub-trajectory that just finished.
        private void OnExecutionFeedback(ExecuteSolutionFeedback feedback)
        {
            if (feedback == null || !trackingExecution) return;
            int step = feedback.sub_id > int.MaxValue ? int.MaxValue : (int)feedback.sub_id;
            reportedStep = Mathf.Max(reportedStep, step);
            if (executingSolution == null) RequestExecutingSolution();
            ApplyReportedSteps();
        }

        private void ApplyReportedSteps()
        {
            var steps = executingSolution?.sub_trajectory;
            if (steps == null || attachedListener == null) return;
            // The step feedback and the joint states share one socket and arrive in order, but the
            // joint states are only applied once per frame: bring the robot up to date first, or a
            // long frame places the attached object against an arm pose that is seconds old.
            SyncMirroredJoints();
            int last = Mathf.Min(reportedStep, steps.Length - 1);
            for (int step = appliedThroughStep + 1; step <= last; step++)
            {
                var acos = steps[step].scene_diff?.robot_state?.attached_collision_objects;
                if (acos == null) continue;
                foreach (var aco in acos)
                {
                    if (string.IsNullOrEmpty(aco?.@object?.id)) continue;
                    attachedListener.ApplyExpected(aco);
                }
            }
            appliedThroughStep = Mathf.Max(appliedThroughStep, last);
        }

        // ---- live joint states ---------------------------------------------------------------

        /// <summary>Apply the newest received joint state now (no-op when not mirroring or nothing new).</summary>
        public void SyncMirroredJoints()
        {
            if (mirroring) ApplyLatestJointState();
        }

        /// <summary>Follow the joint state topic on the Unity robot; any running preview is stopped first.</summary>
        public void StartLiveMirror()
        {
            IRosBus bus = robotInstance != null ? robotInstance.Ros : null;
            if (mirroring || Robot == null || bus == null) return;
            Stop();
            latestJointState = null;
            mirrorBus = bus;
            mirrorTopic = robotInstance.JointStatesTopic;
            mirrorBus.Subscribe<JointStateMsg>(mirrorTopic, OnJointState);
            mirroring = true;
        }

        /// <summary>Stop following the joint state topic; the robot stays at the last state received.</summary>
        public void StopLiveMirror()
        {
            if (!mirroring) return;
            mirroring = false;
            mirrorBus?.Unsubscribe<JointStateMsg>(mirrorTopic, OnJointState);
            mirrorBus = null;
            ApplyLatestJointState();
        }

        // Messages arrive far faster than frames; only the newest one is applied, once per frame.
        private void OnJointState(JointStateMsg msg) => latestJointState = msg;

        private void ApplyLatestJointState()
        {
            var msg = latestJointState;
            latestJointState = null;
            if (msg?.name == null || msg.position == null || Robot == null) return;
            string[] mapped = MapJointNames(msg.name);
            if (mapped == null) return;
            // A joint state may list joints the Unity robot does not have; only the ones it
            // knows (arm and gripper) are applied.
            var known = new HashSet<string>(Robot.GetJointStateNames());
            var names = new List<string>(mapped.Length);
            var positions = new List<double>(mapped.Length);
            for (int i = 0; i < mapped.Length && i < msg.position.Length; i++)
            {
                if (!known.Contains(mapped[i])) continue;
                names.Add(mapped[i]);
                positions.Add(msg.position[i]);
            }
            if (names.Count == 0) return;
            // The robot model clamps the state; the real robot outside the model's limits means
            // the model is wrong, which is reported once per joint rather than per message.
            JointWriteReport written = Robot.ApplyJointState(names.ToArray(), positions.ToArray());
            if (written.AnyClamped && OnRefusal != null) mismatches.Observe(written, Robot, OnRefusal);
        }

        // ---- preview -------------------------------------------------------------------------

        public void PlaySolution(SolutionMsg solution) => PlaySolution(solution, 0, int.MaxValue, useStartScene: false);

        /// <summary>
        /// Preview only sub-trajectories <paramref name="firstStep"/>..<paramref name="lastStep"/>
        /// (inclusive) — one stage's part of the solution. The steps before them are applied
        /// instantly, so the robot and any carried object start where that stage really begins.
        /// </summary>
        public void PlaySolution(SolutionMsg solution, int firstStep, int lastStep) =>
            PlaySolution(solution, firstStep, lastStep, useStartScene: false);

        /// <summary>
        /// Preview a solution from its own <c>start_scene</c> instead of the current state: a
        /// stage's partial solution starts mid-task, with the robot elsewhere and possibly the
        /// object already in the gripper. Needs a solution fetched with include_start_scene.
        /// </summary>
        public void PlaySolution(SolutionMsg solution, bool useStartScene) =>
            PlaySolution(solution, 0, int.MaxValue, useStartScene);

        private void PlaySolution(SolutionMsg solution, int firstStep, int lastStep, bool useStartScene)
        {
            LastProblem = null;
            if (Robot == null) { Report("no robot assigned to the solution player"); return; }
            if (solution?.sub_trajectory == null || solution.sub_trajectory.Length == 0) { Report("the solution has no trajectory segments"); return; }
            if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
            Stop();
            playRoutine = StartCoroutine(PlayRoutine(solution, firstStep, lastStep, useStartScene));
        }

        public void Stop()
        {
            isPlaying = false;
            if (playRoutine != null) { StopCoroutine(playRoutine); playRoutine = null; }
            RestorePose();
        }

        private IEnumerator PlayRoutine(SolutionMsg solution, int firstStep, int lastStep, bool useStartScene)
        {
            isPlaying = true;
            // The preview drives the joints from here on: a live mirror of the real robot lets go.
            Robot.TakeControl();
            savedNames = Robot.GetJointStateNames();
            savedPositions = Robot.GetJointStatePositions();
            bool playedAny = false;
            var unmatched = new SortedSet<string>();

            try
            {
                if (useStartScene) ApplyStartScene(solution.start_scene);

                for (int step = 0; step < solution.sub_trajectory.Length && step <= lastStep; step++)
                {
                    if (!isPlaying) break;
                    var seg = solution.sub_trajectory[step];

                    // Attach/detach stages are zero-motion ModifyPlanningScene segments — the
                    // scene_diff must be processed even when there is no trajectory to play.
                    ProcessSceneDiff(seg.scene_diff);

                    var jt = seg.trajectory?.joint_trajectory;
                    if (jt == null || jt.points == null || jt.points.Length == 0) continue;

                    // Arm and gripper (finger) segments are both played. A segment naming only
                    // joints the Unity robot lacks is skipped; the preview is only impossible
                    // when no segment at all moves a joint the Unity robot has.
                    string[] names = MapJointNames(jt.joint_names);
                    if (names == null)
                    {
                        unmatched.UnionWith(jt.joint_names);
                        continue;
                    }
                    playedAny = true;
                    if (step < firstStep)
                    {
                        // Fast-forward: land on where this earlier step ends.
                        Robot.ApplyJointState(names, jt.points[jt.points.Length - 1].positions);
                        continue;
                    }
                    yield return PlayJointTrajectory(jt, names);
                }

                // A single stage can be over in an instant (attach, gripper-only); hold its end
                // state briefly so it can be seen before the robot snaps back.
                bool partial = useStartScene || firstStep > 0 || lastStep < solution.sub_trajectory.Length - 1;
                if (isPlaying && partial && stagePreviewHoldSeconds > 0f)
                    yield return new WaitForSeconds(stagePreviewHoldSeconds);

                if (isPlaying && !playedAny && unmatched.Count > 0)
                    Report($"none of the solution's joints ({string.Join(", ", unmatched)}) exist on the Unity robot " +
                           $"({string.Join(", ", Robot.GetJointStateNames())})");
            }
            finally
            {
                RestorePose();
                isPlaying = false;
                playRoutine = null;
            }
        }

        // Mirror scene_diff attach/detach onto the Unity objects so the preview shows the
        // object riding the gripper, exactly like the live execution will.
        private void ProcessSceneDiff(PlanningSceneMsg sceneDiff)
        {
            var acos = sceneDiff?.robot_state?.attached_collision_objects;
            if (acos == null || registry == null) return;

            foreach (var aco in acos)
            {
                string id = aco.@object?.id;
                if (string.IsNullOrEmpty(id)) continue;

                if (aco.@object.operation == CollisionObjectMsg.REMOVE)
                    PreviewDetach(id);
                else
                    PreviewAttachObject(id, aco.link_name, aco.@object.pose);
            }
        }

        private void PreviewAttachObject(string id, string linkName, PoseMsg poseInLink)
        {
            if (previewAttached.TryGetValue(id, out var existing) && existing.attached) return;
            if (!registry.TryGetObject(id, out var env)) return;
            // The real robot is carrying it right now; the live attach owns its transform.
            if (env.AttachedTo != null) return;

            // Exact link only: the pose is relative to that link's frame, so the end-effector
            // fallback of FindLinkTransform would put the object in the wrong place.
            if (!Robot.TryFindLinkTransform(linkName, out Transform link)
                && !Robot.TryFindLinkTransform(MapName(linkName), out link))
            {
                Debug.LogWarning($"[MtcSolutionPlayer] no link '{linkName}' on the Unity robot; '{id}' stays put in the preview.");
                return;
            }

            Track(id, env.gameObject).attached = true;
            AttachedObjectPlacement.Place(env.transform, link, poseInLink);
        }

        // Remember how the object was before the preview first touched it, and stop it streaming
        // the preview motion into MoveIt's live scene. Undone in RestorePose.
        private PreviewAttach Track(string id, GameObject go)
        {
            if (previewAttached.TryGetValue(id, out var state) && state.go == go) return state;

            var publishers = go.GetComponentsInChildren<CollisionObjectPublisher>(true);
            state = new PreviewAttach
            {
                go = go,
                originalParent = go.transform.parent,
                originalPos = go.transform.position,
                originalRot = go.transform.rotation,
                originalLocalScale = go.transform.localScale,
                wasPaused = publishers.Length > 0 && publishers[0].pausePublishing,
            };
            previewAttached[id] = state;
            foreach (var pub in publishers) pub.pausePublishing = true;
            return state;
        }

        private void PreviewDetach(string id)
        {
            if (!previewAttached.TryGetValue(id, out var state) || state.go == null || !state.attached) return;
            // Leave the object where the preview placed it for now; full state (pose, parent,
            // publisher) is restored in RestorePose when the preview ends.
            state.attached = false;
            state.go.transform.SetParent(state.originalParent, worldPositionStays: true);
            state.go.transform.localScale = state.originalLocalScale;
        }

        // Put the robot and the mirrored objects where the solution starts. Everything moved here
        // goes through Track, so the end-of-preview restore puts it back.
        private void ApplyStartScene(PlanningSceneMsg startScene)
        {
            var jointState = startScene?.robot_state?.joint_state;
            var worldObjects = startScene?.world?.collision_objects;
            var attachedObjects = startScene?.robot_state?.attached_collision_objects;
            bool hasJoints = jointState?.name != null && jointState.name.Length > 0;
            if (!hasJoints && (worldObjects?.Length ?? 0) == 0 && (attachedObjects?.Length ?? 0) == 0)
            {
                Debug.LogWarning("[MtcSolutionPlayer] the solution carries no start scene (older server?); previewing from the current state.");
                return;
            }

            if (hasJoints && jointState.position != null)
            {
                // A full robot state may list joints the Unity robot does not have;
                // only the ones it knows (arm and gripper) are applied.
                string[] mapped = MapJointNames(jointState.name);
                if (mapped != null)
                {
                    var known = new HashSet<string>(Robot.GetJointStateNames());
                    var names = new List<string>();
                    var positions = new List<double>();
                    for (int i = 0; i < mapped.Length && i < jointState.position.Length; i++)
                    {
                        if (!known.Contains(mapped[i])) continue;
                        names.Add(mapped[i]);
                        positions.Add(jointState.position[i]);
                    }
                    Robot.ApplyJointState(names.ToArray(), positions.ToArray());
                }
            }

            if (registry == null) return;

            foreach (var co in worldObjects ?? System.Array.Empty<CollisionObjectMsg>())
            {
                if (string.IsNullOrEmpty(co?.id) || co.pose == null) continue;
                if (!registry.TryGetObject(co.id, out var env)) continue;
                if (env.AttachedTo != null) continue; // the real robot is carrying it

                var state = Track(co.id, env.gameObject);
                if (state.attached) continue;
                ApplyRosWorldPose(env.transform, co.pose);
            }

            foreach (var aco in attachedObjects ?? System.Array.Empty<AttachedCollisionObjectMsg>())
            {
                string id = aco?.@object?.id;
                if (!string.IsNullOrEmpty(id))
                    PreviewAttachObject(id, aco.link_name, aco.@object.pose);
            }
        }

        // A pose in the robot's planning frame (the solution comes from that robot's planner),
        // with the same conversion inbound collision objects get.
        private void ApplyRosWorldPose(Transform t, PoseMsg rosPose)
        {
            Transform origin = robotInstance != null && robotInstance.PlanningFrame != null
                ? robotInstance.PlanningFrame
                : registry.WorldOrigin;
            Vector3 pos = RosUnityConversion.RosToUnityPosition(rosPose.position);
            Quaternion rot = RosUnityConversion.RosToUnityQuaternion(rosPose.orientation);
            t.position = origin.TransformPoint(pos);
            t.rotation = origin.rotation * rot;
        }

        private IEnumerator PlayJointTrajectory(JointTrajectoryMsg jt, string[] names)
        {
            var points = jt.points;

            Robot.ApplyJointState(names, points[0].positions);
            double prevTime = DurationToSeconds(points[0].time_from_start);
            double[] prevPos = points[0].positions;

            for (int i = 1; i < points.Length && isPlaying; i++)
            {
                double currTime = DurationToSeconds(points[i].time_from_start);
                float duration = Mathf.Max(0.000001f, (float)(currTime - prevTime));
                yield return LerpJoints(names, prevPos, points[i].positions, duration);
                prevPos = points[i].positions;
                prevTime = currTime;
            }
        }

        private IEnumerator LerpJoints(string[] names, double[] from, double[] to, float duration)
        {
            float elapsed = 0f;
            var lerped = new double[names.Length];
            while (elapsed < duration && isPlaying)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                for (int j = 0; j < names.Length; j++)
                    lerped[j] = from[j] + (to[j] - from[j]) * t;
                Robot.ApplyJointState(names, lerped);
                yield return null;
            }
            if (isPlaying) Robot.ApplyJointState(names, to);
        }

        private void RestorePose()
        {
            if (savedNames != null && savedPositions != null && Robot != null)
                Robot.ApplyJointState(savedNames, savedPositions);
            savedNames = null;
            savedPositions = null;

            // Put preview-moved objects back exactly as they were before the preview.
            foreach (var state in previewAttached.Values)
            {
                if (state.go == null) continue;
                state.go.transform.SetParent(state.originalParent, worldPositionStays: true);
                state.go.transform.localScale = state.originalLocalScale;
                state.go.transform.SetPositionAndRotation(state.originalPos, state.originalRot);
                foreach (var pub in state.go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                    pub.pausePublishing = state.wasPaused;
            }
            previewAttached.Clear();
        }

        // The ROS robot and the Unity robot can be the same arm under different names; the
        // robot's name rule translates. Without it every joint lookup would miss and the preview
        // would silently do nothing.
        private string MapName(string name) =>
            robotInstance != null ? robotInstance.Names.ToLocal(name) : name;

        private string[] MapJointNames(string[] rosNames)
        {
            var known = new HashSet<string>(Robot.GetJointStateNames());
            var mapped = new string[rosNames.Length];
            int matched = 0;
            for (int i = 0; i < rosNames.Length; i++)
            {
                mapped[i] = known.Contains(rosNames[i]) ? rosNames[i] : MapName(rosNames[i]);
                if (known.Contains(mapped[i])) matched++;
            }
            return matched > 0 ? mapped : null;
        }

        private void Report(string problem)
        {
            LastProblem = problem;
            Debug.LogWarning("[MtcSolutionPlayer] cannot preview: " + problem);
            OnProblem?.Invoke(problem);
        }

        private static double DurationToSeconds(DurationMsg d) => d.sec + d.nanosec * 1e-9;

        private void OnDisable()
        {
            Stop();
            StopLiveMirror();
            StopExecutionTracking();
        }
    }
}
