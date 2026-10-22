using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Trajectory;
using RosMessageTypes.Sensor;
using RosMessageTypes.StudyInterfaces;
using Erupt.Ros;

public class MTCTrajectoryPlayer : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private CollisionObjectsListenerSimple sceneListener;

    [Header("Robot name mapping")]
    [Tooltip("Joint/link prefix used by the ROS robot, e.g. \"panda_\" for the MTC Panda demo. " +
             "Names the Unity robot does not know are retried with Unity Name Prefix instead.")]
    [SerializeField] private string rosNamePrefix = "panda_";
    [Tooltip("Joint/link prefix of the Unity robot, e.g. \"fr3_\".")]
    [SerializeField] private string unityNamePrefix = "fr3_";

    [Tooltip("Seconds a single-stage preview holds its final pose before the robot is restored.")]
    [SerializeField, Min(0f)] private float stagePreviewHoldSeconds = 1.5f;

    /// <summary>Why the last preview could not play, or null. Raised through <see cref="OnProblem"/>.</summary>
    public string LastProblem { get; private set; }
    public event System.Action<string> OnProblem;

    [Header("Live execution")]
    [Tooltip("While this client executes a solution the Unity robot follows /joint_states, so the real motion " +
             "is shown (previews are stopped). Found in the scene when empty.")]
    [SerializeField] private PickPlaceClient pickPlaceClient;
    [SerializeField] private string jointStateTopic = "/joint_states";

    [Tooltip("Live attach listener. While a solution executes, its attach/detach steps are applied " +
             "through this listener as soon as the server reports each step done, so the carried " +
             "object is released when the hand opens even when the planning-scene watcher's notice " +
             "lags behind the robot. Found in the scene when empty.")]
    [SerializeField] private AttachedCollisionObjectListener attachedListener;
    [Tooltip("Apply the executing solution's attach/detach steps from the execution feedback.")]
    [SerializeField] private bool applySolutionStepsDuringExecution = true;

    [Tooltip("Log every preview step (segment index, joints, attach/detach) to the console.")]
    public bool verboseLog = false;

    private IRosBus ros;
    private bool mirroring;
    private JointStateMsg latestJointState;
    /// <summary>True while the Unity robot follows the live /joint_states (during execution).</summary>
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

    private bool isPlaying;
    /// <summary>True while a solution preview is animating the robot.</summary>
    public bool IsPlaying => isPlaying;
    /// <summary>Index of the sub-trajectory being previewed, or -1.</summary>
    public int CurrentStep { get; private set; } = -1;
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
        if (pickPlaceClient == null) pickPlaceClient = FindFirstObjectByType<PickPlaceClient>(FindObjectsInactive.Include);
        if (pickPlaceClient != null)
        {
            pickPlaceClient.OnPhaseChanged += OnClientPhaseChanged;
            OnClientPhaseChanged();
        }
    }

    private void OnDestroy()
    {
        if (pickPlaceClient != null) pickPlaceClient.OnPhaseChanged -= OnClientPhaseChanged;
        StopLiveMirror();
        StopExecutionTracking();
    }

    private void Update()
    {
        if (mirroring) ApplyLatestJointState();
    }

    private void OnClientPhaseChanged()
    {
        if (pickPlaceClient != null && pickPlaceClient.Phase == PickPlacePhase.Executing)
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
    // slower path (full planning-scene messages with every mesh in the cell), so mirroring the
    // diff from the feedback keeps the Unity object in step with the robot it rides.

    private void StartExecutionTracking()
    {
        StopExecutionTracking();
        if (!applySolutionStepsDuringExecution || pickPlaceClient == null) return;
        uint solutionId = pickPlaceClient.ExecutingSolutionId;
        if (solutionId == 0) return; // plan-and-execute goals: no solution to read, the listener alone mirrors
        if (attachedListener == null) attachedListener = FindFirstObjectByType<AttachedCollisionObjectListener>();
        if (attachedListener == null) return;
        attachedListener.syncRobot = SyncMirroredJoints;

        trackingExecution = true;
        trackedSolutionId = solutionId;
        attachedListener.ForgetExpected();
        pickPlaceClient.OnExecutionFeedback += OnExecutionFeedback;
        RequestExecutingSolution();
    }

    // Feedback for the first steps may land before the solution does; ApplyReportedSteps
    // catches up once it is here. A failed fetch is retried on the next feedback.
    private void RequestExecutingSolution()
    {
        if (solutionRequested || executingSolution != null) return;
        solutionRequested = true;
        uint solutionId = trackedSolutionId;
        pickPlaceClient.FetchSolution(solutionId, solution =>
        {
            if (!trackingExecution || trackedSolutionId != solutionId) return;
            executingSolution = solution;
            ApplyReportedSteps();
        }, problem =>
        {
            if (!trackingExecution || trackedSolutionId != solutionId) return;
            solutionRequested = false;
            Debug.LogWarning($"[MTCTrajectoryPlayer] executing solution {solutionId} unavailable ({problem}); attach/detach follow the watcher until it is.");
        });
    }

    private void StopExecutionTracking()
    {
        if (trackingExecution && pickPlaceClient != null) pickPlaceClient.OnExecutionFeedback -= OnExecutionFeedback;
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
                if (verboseLog)
                    Debug.Log($"[MTCTrajectoryPlayer] step {step} ('{pickPlaceClient?.StageName(steps[step].info?.stage_id ?? 0)}', reported {reportedStep}) done: " +
                              $"{(aco.@object.operation == CollisionObjectMsg.REMOVE ? "detach" : "attach")} '{aco.@object.id}' frame '{aco.@object.header?.frame_id}'");
                attachedListener.ApplyExpected(aco);
            }
        }
        appliedThroughStep = Mathf.Max(appliedThroughStep, last);
    }

    /// <summary>Apply the newest received joint state now (no-op when not mirroring or nothing new).</summary>
    public void SyncMirroredJoints()
    {
        if (mirroring) ApplyLatestJointState();
    }

    /// <summary>Follow /joint_states on the Unity robot; any running preview is stopped first.</summary>
    public void StartLiveMirror()
    {
        if (mirroring || ikController == null) return;
        ros ??= RosBus.Instance;
        if (ros == null) return;
        Stop();
        ros.Subscribe<JointStateMsg>(jointStateTopic, OnJointState);
        mirroring = true;
    }

    /// <summary>Stop following /joint_states; the robot stays at the last state received.</summary>
    public void StopLiveMirror()
    {
        if (!mirroring) return;
        mirroring = false;
        ros?.Unsubscribe<JointStateMsg>(jointStateTopic, OnJointState);
        ApplyLatestJointState();
    }

    // Messages arrive far faster than frames; only the newest one is applied, once per frame.
    private void OnJointState(JointStateMsg msg) => latestJointState = msg;

    private void ApplyLatestJointState()
    {
        var msg = latestJointState;
        latestJointState = null;
        if (msg?.name == null || msg.position == null || ikController == null) return;
        string[] mapped = MapJointNames(msg.name);
        if (mapped == null) return;
        var names = new List<string>(mapped.Length);
        var positions = new List<double>(mapped.Length);
        for (int i = 0; i < mapped.Length && i < msg.position.Length; i++)
        {
            if (!ikController.HasJoint(mapped[i])) continue;
            names.Add(mapped[i]);
            positions.Add(msg.position[i]);
        }
        if (names.Count > 0) ikController.ApplyJointState(names.ToArray(), positions.ToArray());
    }

    public void PlaySolution(SolutionMsg solution) => PlaySolution(solution, 0, int.MaxValue);

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
        if (ikController == null) { Report("no robot controller assigned to the trajectory player"); return; }
        if (solution?.sub_trajectory == null || solution.sub_trajectory.Length == 0) { Report("the solution has no trajectory segments"); return; }
        if (sceneListener == null) sceneListener = FindFirstObjectByType<CollisionObjectsListenerSimple>();
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
        savedNames = ikController.GetJointStateNames();
        savedPositions = ikController.GetJointStatePositions();
        bool playedAny = false;
        var unmatched = new SortedSet<string>();

        try
        {
            if (useStartScene) ApplyStartScene(solution.start_scene);

            for (int step = 0; step < solution.sub_trajectory.Length && step <= lastStep; step++)
            {
                if (!isPlaying) break;
                var seg = solution.sub_trajectory[step];
                CurrentStep = step;
                if (verboseLog)
                    Debug.Log($"[MTCTrajectoryPlayer] step {step} stage={seg.info?.stage_id} pts={seg.trajectory?.joint_trajectory?.points?.Length ?? 0} " +
                              $"joints={(seg.trajectory?.joint_trajectory?.joint_names == null ? "-" : string.Join(",", seg.trajectory.joint_trajectory.joint_names))} " +
                              $"acos={string.Join(",", (seg.scene_diff?.robot_state?.attached_collision_objects ?? System.Array.Empty<AttachedCollisionObjectMsg>()).Select(a => $"{(a.@object?.operation == CollisionObjectMsg.REMOVE ? "REMOVE" : "ADD")}:{a.@object?.id}"))}");

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
                    ikController.ApplyJointState(names, jt.points[jt.points.Length - 1].positions);
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
                       $"({string.Join(", ", ikController.GetJointStateNames())})");
        }
        finally
        {
            RestorePose();
            isPlaying = false;
            CurrentStep = -1;
            playRoutine = null;
        }
    }

    // Mirror scene_diff attach/detach onto the Unity objects so the preview shows the
    // object riding the gripper, exactly like the live execution will.
    private void ProcessSceneDiff(PlanningSceneMsg sceneDiff)
    {
        var acos = sceneDiff?.robot_state?.attached_collision_objects;
        if (acos == null || sceneListener == null) return;

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

    private void PreviewAttachObject(string id, string linkName, RosMessageTypes.Geometry.PoseMsg poseInLink)
    {
        if (previewAttached.TryGetValue(id, out var existing) && existing.attached) return;
        // The real robot is carrying it right now; the live attach owns its transform.
        if (sceneListener.attachedIds.Contains(id)) return;
        if (!sceneListener.TryGetObject(id, out var go)) return;

        // Exact link only: the pose is relative to that link's frame, so the end-effector
        // fallback of FindLinkTransform would put the object in the wrong place.
        if (!ikController.TryFindLinkTransform(linkName, out Transform link)
            && !ikController.TryFindLinkTransform(MapName(linkName), out link))
        {
            Debug.LogWarning($"[MTCTrajectoryPlayer] no link '{linkName}' on the Unity robot; '{id}' stays put in the preview.");
            return;
        }

        Track(id, go).attached = true;
        AttachedObjectPlacement.Place(go.transform, link, poseInLink);
        if (verboseLog) Debug.Log($"[MTCTrajectoryPlayer] preview attach '{id}' to {link.name} at step {CurrentStep}");
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
        if (verboseLog) Debug.Log($"[MTCTrajectoryPlayer] preview detach '{id}' at step {CurrentStep}, world pos {state.go.transform.position}");
        PreviewReleased?.Invoke(state.go);
    }

    /// <summary>
    /// The preview's robot has just opened its hand on this object (it is back under its
    /// original parent at the preview's place pose). Whatever a listener does to it is undone
    /// when the preview ends, announced first by <see cref="PreviewEnding"/>.
    /// </summary>
    public event System.Action<GameObject> PreviewReleased;

    /// <summary>Raised just before a preview puts its moved objects back where they started.</summary>
    public event System.Action PreviewEnding;

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
            Debug.LogWarning("[MTCTrajectoryPlayer] the solution carries no start scene (older server?); previewing from the current state.");
            return;
        }

        if (hasJoints && jointState.position != null)
        {
            // A full robot state may list joints the Unity robot does not have;
            // only the ones it knows (arm and gripper) are applied.
            string[] mapped = MapJointNames(jointState.name);
            if (mapped != null)
            {
                var known = new HashSet<string>(ikController.GetJointStateNames());
                var names = new List<string>();
                var positions = new List<double>();
                for (int i = 0; i < mapped.Length && i < jointState.position.Length; i++)
                {
                    if (!known.Contains(mapped[i])) continue;
                    names.Add(mapped[i]);
                    positions.Add(jointState.position[i]);
                }
                ikController.ApplyJointState(names.ToArray(), positions.ToArray());
            }
        }

        if (sceneListener == null) return;

        foreach (var co in worldObjects ?? System.Array.Empty<CollisionObjectMsg>())
        {
            if (string.IsNullOrEmpty(co?.id) || co.pose == null) continue;
            if (sceneListener.attachedIds.Contains(co.id)) continue; // the real robot is carrying it
            if (!sceneListener.TryGetObject(co.id, out var go)) continue;

            var state = Track(co.id, go);
            if (state.attached) continue;
            sceneListener.ApplyRosWorldPose(go.transform, co.pose);
        }

        foreach (var aco in attachedObjects ?? System.Array.Empty<AttachedCollisionObjectMsg>())
        {
            string id = aco?.@object?.id;
            if (!string.IsNullOrEmpty(id))
                PreviewAttachObject(id, aco.link_name, aco.@object.pose);
        }
    }

    private IEnumerator PlayJointTrajectory(JointTrajectoryMsg jt, string[] names)
    {
        var points = jt.points;

        ikController.ApplyJointState(names, points[0].positions);
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
            ikController.ApplyJointState(names, lerped);
            yield return null;
        }
        if (isPlaying) ikController.ApplyJointState(names, to);
    }

    private void RestorePose()
    {
        if (savedNames != null && savedPositions != null && ikController != null)
            ikController.ApplyJointState(savedNames, savedPositions);
        savedNames = null;
        savedPositions = null;

        // Put preview-attached objects back exactly as they were before the preview.
        if (previewAttached.Count > 0) PreviewEnding?.Invoke();
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

    // The ROS robot and the Unity robot can be the same arm under different names (the MTC
    // demo plans for "panda_*", the scene shows "fr3_*"). Without this every joint lookup
    // misses and the preview silently does nothing.
    private string MapName(string name) =>
        !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(rosNamePrefix) && name.StartsWith(rosNamePrefix)
            ? unityNamePrefix + name.Substring(rosNamePrefix.Length) : name;

    private string[] MapJointNames(string[] rosNames)
    {
        var known = new HashSet<string>(ikController.GetJointStateNames());
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
        Debug.LogWarning("[MTCTrajectoryPlayer] cannot preview: " + problem);
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
