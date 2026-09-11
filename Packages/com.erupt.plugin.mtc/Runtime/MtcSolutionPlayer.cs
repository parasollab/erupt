using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Moveit;
using RosMessageTypes.Trajectory;
using Erupt.Environment;

/// <summary>Previews an MTC solution on the robot, mirroring scene_diff attachments through the environment registry.</summary>
public class MtcSolutionPlayer : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    private Erupt.Robot.IRobotModel robot;

    /// <summary>Use the context's robot instead of the serialised controller.</summary>
    public void SetRobot(Erupt.Robot.IRobotModel model) => robot = model;
    private Erupt.Robot.IRobotModel Robot => robot ?? (ikController != null ? ikController : null);
    [SerializeField] private EnvironmentRegistry registry;

    private bool isPlaying;
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
        public bool wasPaused;
    }
    private readonly Dictionary<string, PreviewAttach> previewAttached = new();

    public void PlaySolution(SolutionMsg solution)
    {
        if (Robot == null) { Debug.LogError("[MtcSolutionPlayer] no robot assigned."); return; }
        if (registry == null) registry = FindFirstObjectByType<EnvironmentRegistry>();
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
        savedNames = Robot.GetJointStateNames();
        savedPositions = Robot.GetJointStatePositions();

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
                yield return PlayJointTrajectory(jt);
            }
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
                PreviewAttachObject(id, aco.link_name);
        }
    }

    private void PreviewAttachObject(string id, string linkName)
    {
        if (previewAttached.ContainsKey(id)) return;
        if (!registry.TryGet(id, out var go)) return;

        Transform link = Robot.FindLinkTransform(linkName);
        if (link == null) return;

        var publishers = go.GetComponentsInChildren<CollisionObjectPublisher>(true);
        var state = new PreviewAttach
        {
            go = go,
            originalParent = go.transform.parent,
            originalPos = go.transform.position,
            originalRot = go.transform.rotation,
            wasPaused = publishers.Length > 0 && publishers[0].pausePublishing,
        };
        previewAttached[id] = state;

        // Don't stream the preview motion into MoveIt's live scene.
        foreach (var pub in publishers) pub.pausePublishing = true;
        go.transform.SetParent(link, worldPositionStays: true);
    }

    private void PreviewDetach(string id)
    {
        if (!previewAttached.TryGetValue(id, out var state) || state.go == null) return;
        // Leave the object where the preview placed it for now; full state (pose, parent,
        // publisher) is restored in RestorePose when the preview ends.
        state.go.transform.SetParent(state.originalParent, worldPositionStays: true);
    }

    private IEnumerator PlayJointTrajectory(JointTrajectoryMsg jt)
    {
        string[] names = jt.joint_names;
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

        // Put preview-attached objects back exactly as they were before the preview.
        foreach (var state in previewAttached.Values)
        {
            if (state.go == null) continue;
            state.go.transform.SetParent(state.originalParent, worldPositionStays: true);
            state.go.transform.SetPositionAndRotation(state.originalPos, state.originalRot);
            foreach (var pub in state.go.GetComponentsInChildren<CollisionObjectPublisher>(true))
                pub.pausePublishing = state.wasPaused;
        }
        previewAttached.Clear();
    }

    private static double DurationToSeconds(DurationMsg d) => d.sec + d.nanosec * 1e-9;

    private void OnDisable() => Stop();
}
