using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Trajectory;

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

    /// <summary>Why the last preview could not play, or null. Raised through <see cref="OnProblem"/>.</summary>
    public string LastProblem { get; private set; }
    public event System.Action<string> OnProblem;

    private bool isPlaying;
    /// <summary>True while a solution preview is animating the robot.</summary>
    public bool IsPlaying => isPlaying;
    private Coroutine playRoutine;
    private string[] savedNames;
    private float[] savedPositions;

    // Objects the preview has reparented to a robot link (via scene_diff attach), with
    // everything needed to put them back when the preview ends.
    private struct PreviewAttach
    {
        public GameObject go;
        public Transform originalParent;
        public Vector3 originalPos;
        public Quaternion originalRot;
        public Vector3 originalLocalScale;
        public bool wasPaused;
    }
    private readonly Dictionary<string, PreviewAttach> previewAttached = new();

    public void PlaySolution(SolutionMsg solution)
    {
        LastProblem = null;
        if (ikController == null) { Report("no robot controller assigned to the trajectory player"); return; }
        if (solution?.sub_trajectory == null || solution.sub_trajectory.Length == 0) { Report("the solution has no trajectory segments"); return; }
        if (sceneListener == null) sceneListener = FindFirstObjectByType<CollisionObjectsListenerSimple>();
        Stop();
        playRoutine = StartCoroutine(PlayRoutine(solution));
    }

    public void Stop()
    {
        isPlaying = false;
        if (playRoutine != null) { StopCoroutine(playRoutine); playRoutine = null; }
        RestorePose();
    }

    private IEnumerator PlayRoutine(SolutionMsg solution)
    {
        isPlaying = true;
        savedNames = ikController.GetJointStateNames();
        savedPositions = ikController.GetJointStatePositions();
        bool playedAny = false;
        var unmatched = new SortedSet<string>();

        try
        {
            foreach (var seg in solution.sub_trajectory)
            {
                if (!isPlaying) break;

                // Attach/detach stages are zero-motion ModifyPlanningScene segments — the
                // scene_diff must be processed even when there is no trajectory to play.
                ProcessSceneDiff(seg.scene_diff);

                var jt = seg.trajectory?.joint_trajectory;
                if (jt == null || jt.points == null || jt.points.Length == 0) continue;

                // Gripper open/close segments only name finger joints, which the Unity arm
                // controller does not drive. Skip those; the preview is only impossible
                // when no segment at all moves a joint the Unity robot has.
                string[] names = MapJointNames(jt.joint_names);
                if (names == null)
                {
                    unmatched.UnionWith(jt.joint_names);
                    continue;
                }
                playedAny = true;
                yield return PlayJointTrajectory(jt, names);
            }

            if (isPlaying && !playedAny && unmatched.Count > 0)
                Report($"none of the solution's joints ({string.Join(", ", unmatched)}) exist on the Unity robot " +
                       $"({string.Join(", ", ikController.JointNames)})");
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
        if (previewAttached.ContainsKey(id)) return;
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

        var publishers = go.GetComponentsInChildren<CollisionObjectPublisher>(true);
        var state = new PreviewAttach
        {
            go = go,
            originalParent = go.transform.parent,
            originalPos = go.transform.position,
            originalRot = go.transform.rotation,
            originalLocalScale = go.transform.localScale,
            wasPaused = publishers.Length > 0 && publishers[0].pausePublishing,
        };
        previewAttached[id] = state;

        // Don't stream the preview motion into MoveIt's live scene.
        foreach (var pub in publishers) pub.pausePublishing = true;
        AttachedObjectPlacement.Place(go.transform, link, poseInLink);
    }

    private void PreviewDetach(string id)
    {
        if (!previewAttached.TryGetValue(id, out var state) || state.go == null) return;
        // Leave the object where the preview placed it for now; full state (pose, parent,
        // publisher) is restored in RestorePose when the preview ends.
        state.go.transform.SetParent(state.originalParent, worldPositionStays: true);
        state.go.transform.localScale = state.originalLocalScale;
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
        var known = new HashSet<string>(ikController.JointNames);
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

    private void OnDisable() => Stop();
}
