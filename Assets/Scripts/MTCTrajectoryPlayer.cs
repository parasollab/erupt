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

    [Tooltip("Seconds a single-stage preview holds its final pose before the robot is restored.")]
    [SerializeField, Min(0f)] private float stagePreviewHoldSeconds = 1.5f;

    /// <summary>Why the last preview could not play, or null. Raised through <see cref="OnProblem"/>.</summary>
    public string LastProblem { get; private set; }
    public event System.Action<string> OnProblem;

    private bool isPlaying;
    /// <summary>True while a solution preview is animating the robot.</summary>
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
            Debug.LogWarning("[MTCTrajectoryPlayer] the solution carries no start scene (older server?); previewing from the current state.");
            return;
        }

        if (hasJoints && jointState.position != null)
        {
            // A full robot state also lists joints the Unity robot does not drive
            // (fingers); only the ones it knows are applied.
            string[] mapped = MapJointNames(jointState.name);
            if (mapped != null)
            {
                var known = new HashSet<string>(ikController.JointNames);
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
