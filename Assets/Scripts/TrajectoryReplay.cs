using System.Collections;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

public class TrajectoryReplay : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;

    private bool isReplaying = false;
    private Coroutine replayRoutine;
    private bool hasFinishedOneLoop = false;
    private bool loopReplay = true;

    private string[] savedNames;
    private float[] savedPositions;

    public bool HasFinishedOneLoop() => hasFinishedOneLoop;

    public bool IsRunning => replayRoutine != null;

    public void SetIKController(DirectArticulationIKController controller)
    {
        ikController = controller;
    }

    public void StartReplay(JointTrajectoryMsg trajectory, bool loop = true)
    {
        if (replayRoutine != null)
        {
            Debug.LogWarning("[TrajectoryReplay] StartReplay ignored: already running.");
            return;
        }
        loopReplay = loop;
        replayRoutine = StartCoroutine(RunReplay(trajectory));
    }

    public void RestartReplay(JointTrajectoryMsg trajectory, bool loop = true)
    {
        if (replayRoutine != null)
            StartCoroutine(RestartRoutine(trajectory, loop));
        else
            StartReplay(trajectory, loop);
    }

    public void StopReplay()
    {
        if (!isReplaying && replayRoutine == null) return;
        isReplaying = false;
    }

    private void OnDisable()
    {
        isReplaying = false;
        if (replayRoutine != null)
        {
            StopCoroutine(replayRoutine);
            replayRoutine = null;
        }
        RestoreSavedPose();
    }

    private IEnumerator RestartRoutine(JointTrajectoryMsg newTrajectory, bool loop)
    {
        isReplaying = false;
        if (replayRoutine != null)
            yield return replayRoutine;
        StartReplay(newTrajectory, loop);
    }

    private IEnumerator RunReplay(JointTrajectoryMsg trajectory)
    {
        hasFinishedOneLoop = false;

        if (ikController == null)
        {
            Debug.LogError("[TrajectoryReplay] ikController not assigned.");
            yield break;
        }

        savedNames = ikController.GetJointStateNames();
        savedPositions = ikController.GetJointStatePositions();

        isReplaying = true;

        try
        {
            while (isReplaying)
            {
                bool ok = true;
                yield return StartCoroutine(PlayTrajectory(trajectory, success => ok = success));
                if (!ok || !loopReplay) isReplaying = false;
            }
        }
        finally
        {
            RestoreSavedPose();
            replayRoutine = null;
            isReplaying = false;
        }
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

        ikController.ApplyJointState(names, points[0].positions);

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
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int j = 0; j < names.Length; j++)
                lerped[j] = from[j] + (to[j] - from[j]) * t;
            ikController.ApplyJointState(names, lerped);
            yield return null;
        }

        if (isReplaying)
            ikController.ApplyJointState(names, to);
    }

    private void RestoreSavedPose()
    {
        if (savedNames != null && savedPositions != null && ikController != null)
            ikController.ApplyJointState(savedNames, savedPositions);
        savedNames = null;
        savedPositions = null;
    }

    private static double DurationToSeconds(DurationMsg duration)
    {
        return duration.sec + (duration.nanosec * 1e-9);
    }
}
