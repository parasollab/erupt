using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

public class TrajectoryReplay : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    [SerializeField] private SpawnGhosts ghostSpawner;
    [SerializeField] private Color replayGhostColor = new Color(0f, 0.5f, 1f, 0.5f);

    public const float MinPlaybackSpeed = 0.1f;
    public const float MaxPlaybackSpeed = 3f;

    [SerializeField, Range(MinPlaybackSpeed, MaxPlaybackSpeed)] private float playbackSpeed = 1f;

    public float PlaybackSpeed
    {
        get => playbackSpeed;
        set => playbackSpeed = Mathf.Clamp(value, MinPlaybackSpeed, MaxPlaybackSpeed);
    }

    private bool isReplaying = false;
    private Coroutine replayRoutine;
    private bool hasFinishedOneLoop = false;

    private GameObject replayGhost;
    private Dictionary<string, ArticulationBody> ghostJointsByRosName;

    public bool HasFinishedOneLoop() => hasFinishedOneLoop;

    private void Awake()
    {
        if (ghostSpawner == null)
            ghostSpawner = GetComponent<SpawnGhosts>();
        ghostSpawner?.RegisterReplayController(this);
    }

    public void StartReplay(JointTrajectoryMsg trajectory)
    {
        if (replayRoutine != null)
        {
            Debug.LogWarning("[TrajectoryReplay] StartReplay ignored: already running.");
            return;
        }
        replayRoutine = StartCoroutine(RunReplay(trajectory));
    }

    public void RestartReplay(JointTrajectoryMsg trajectory)
    {
        if (replayRoutine != null)
            StartCoroutine(RestartRoutine(trajectory));
        else
            StartReplay(trajectory);
    }

    public void StopReplay()
    {
        if (!isReplaying && replayRoutine == null) return;
        isReplaying = false;
        ghostSpawner?.SetReplayActive(this, false);
    }

    private void OnDisable()
    {
        isReplaying = false;
        ghostSpawner?.SetReplayActive(this, false);
        if (replayRoutine != null)
        {
            StopCoroutine(replayRoutine);
            replayRoutine = null;
        }
        DestroyReplayGhost();
    }

    private void OnDestroy()
    {
        ghostSpawner?.UnregisterReplayController(this);
    }

    private IEnumerator RestartRoutine(JointTrajectoryMsg newTrajectory)
    {
        isReplaying = false;
        if (replayRoutine != null)
            yield return replayRoutine;
        StartReplay(newTrajectory);
    }

    private IEnumerator RunReplay(JointTrajectoryMsg trajectory)
    {
        hasFinishedOneLoop = false;

        if (ikController == null || ghostSpawner == null)
        {
            Debug.LogError("[TrajectoryReplay] ikController or ghostSpawner not assigned.");
            replayRoutine = null;
            yield break;
        }

        if (!SpawnReplayGhost())
        {
            replayRoutine = null;
            yield break;
        }

        isReplaying = true;
        ghostSpawner.SetReplayActive(this, true);

        try
        {
            while (isReplaying)
            {
                bool ok = true;
                yield return StartCoroutine(PlayTrajectory(trajectory, success => ok = success));
                if (!ok) isReplaying = false;
            }
        }
        finally
        {
            DestroyReplayGhost();
            replayRoutine = null;
            isReplaying = false;
            ghostSpawner.SetReplayActive(this, false);
        }
    }

    // Spawns a translucent ghost robot and maps each ROS joint name (from the real robot's
    // DirectArticulationIKController) to the ghost's matching ArticulationBody, so replay can
    // drive the ghost directly and leave the real robot free for the user to grab and replan.
    private bool SpawnReplayGhost()
    {
        // The replay source remains on the selectable for identification/debugging; all panels control
        // replay through SpawnGhosts' shared controller registration.
        replayGhost = ghostSpawner.SpawnGhost("ReplayGhost", replayGhostColor, this);
        if (replayGhost == null)
        {
            Debug.LogError("[TrajectoryReplay] Failed to spawn replay ghost.");
            return false;
        }

        var ghostArticulationsByGameObjectName = new Dictionary<string, ArticulationBody>();
        foreach (ArticulationBody ab in replayGhost.GetComponentsInChildren<ArticulationBody>(true))
            ghostArticulationsByGameObjectName[ab.name] = ab;

        IReadOnlyList<string> rosNames = ikController.JointNames;
        IReadOnlyList<ArticulationBody> realJoints = ikController.Joints;

        ghostJointsByRosName = new Dictionary<string, ArticulationBody>();
        for (int i = 0; i < rosNames.Count && i < realJoints.Count; i++)
        {
            if (ghostArticulationsByGameObjectName.TryGetValue(realJoints[i].name, out ArticulationBody ghostJoint))
                ghostJointsByRosName[rosNames[i]] = ghostJoint;
        }

        return true;
    }

    private void DestroyReplayGhost()
    {
        if (replayGhost != null)
        {
            ghostSpawner?.DetachPanelBeforeDestroy(replayGhost);
            Destroy(replayGhost);
        }
        replayGhost = null;
        ghostJointsByRosName = null;
    }

    private IEnumerator PlayTrajectory(JointTrajectoryMsg trajectory, System.Action<bool> done)
    {
        var points = trajectory.points;
        if (points == null || points.Length == 0)
        {
            Debug.LogWarning("[TrajectoryReplay] Empty trajectory.");
            done?.Invoke(false);
            yield break;
        }

        string[] names = trajectory.joint_names;

        ApplyGhostJointState(names, points[0].positions);

        double prevTime = DurationToSeconds(points[0].time_from_start);
        double[] prevPos = points[0].positions;

        for (int i = 1; i < points.Length && isReplaying; i++)
        {
            double[] targetPos = points[i].positions;
            if (targetPos == null || targetPos.Length != names.Length)
            {
                Debug.LogError("[TrajectoryReplay] Positions length mismatch.");
                done?.Invoke(false);
                yield break;
            }

            double currTime = DurationToSeconds(points[i].time_from_start);
            float duration = Mathf.Max(0.000001f, (float)(currTime - prevTime));

            yield return LerpJointsOverTime(names, prevPos, targetPos, duration);

            prevPos = targetPos;
            prevTime = currTime;
        }

        hasFinishedOneLoop = true;
        done?.Invoke(true);
    }

    private IEnumerator LerpJointsOverTime(string[] names, double[] from, double[] to, float duration)
    {
        float elapsed = 0f;
        double[] lerped = new double[names.Length];

        while (elapsed < duration && isReplaying)
        {
            elapsed += Time.deltaTime * playbackSpeed;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int j = 0; j < names.Length; j++)
                lerped[j] = from[j] + (to[j] - from[j]) * t;
            ApplyGhostJointState(names, lerped);
            yield return null;
        }

        if (isReplaying)
            ApplyGhostJointState(names, to);
    }

    // Mirrors SpawnGhosts.CopyPoseToGhost: drives the ghost's ArticulationBody joints directly,
    // bypassing DirectArticulationIKController entirely so the real robot is never touched.
    private void ApplyGhostJointState(string[] names, double[] positions)
    {
        if (ghostJointsByRosName == null) return;

        for (int i = 0; i < names.Length; i++)
        {
            if (!ghostJointsByRosName.TryGetValue(names[i], out ArticulationBody joint))
                continue;

            float positionRadians = (float)positions[i];
            joint.jointPosition = new ArticulationReducedSpace(positionRadians);

            ArticulationDrive drive = joint.xDrive;
            drive.target = joint.jointType == ArticulationJointType.RevoluteJoint
                ? positionRadians * Mathf.Rad2Deg
                : positionRadians;
            joint.xDrive = drive;

            joint.PublishTransform();
        }

        Physics.SyncTransforms();
    }

    private static double DurationToSeconds(DurationMsg duration)
    {
        return duration.sec + (duration.nanosec * 1e-9);
    }
}
