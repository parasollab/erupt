using System.Collections;
using UnityEngine;
using RosMessageTypes.Trajectory;
using RosMessageTypes.BuiltinInterfaces;

/// <summary>Plays a joint trajectory on the robot model, restoring the pose when stopped.</summary>
public class JointTrajectoryPlayer : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;
    private Erupt.Robot.IRobotModel robot;

    /// <summary>Use a robot other than the serialised controller (plugins inject the context's).</summary>
    public void SetRobot(Erupt.Robot.IRobotModel model) => robot = model;
    // Explicit null test: a missing serialised reference is a Unity fake null that ?? would keep.
    private Erupt.Robot.IRobotModel Robot => robot ?? (ikController != null ? ikController : null);

    private bool isReplaying = false;
    private Coroutine replayRoutine;
    private bool hasFinishedOneLoop = false;

    private string[] savedNames;
    private float[] savedPositions;

    public bool HasFinishedOneLoop() => hasFinishedOneLoop;

    public void StartReplay(JointTrajectoryMsg trajectory)
    {
        if (replayRoutine != null)
        {
            Debug.LogWarning("[JointTrajectoryPlayer] StartReplay ignored: already running.");
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

        if (Robot == null)
        {
            Debug.LogError("[JointTrajectoryPlayer] ikController not assigned.");
            yield break;
        }

        savedNames = Robot.GetJointStateNames();
        savedPositions = Robot.GetJointStatePositions();

        isReplaying = true;

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
            Debug.LogWarning("[JointTrajectoryPlayer] Empty trajectory.");
            done?.Invoke(false);
            yield break;
        }

        string[] names = trajectory.joint_names;

        Robot.ApplyJointState(names, points[0].positions);

        double prevTime = DurationToSeconds(points[0].time_from_start);
        double[] prevPos = points[0].positions;

        for (int i = 1; i < points.Length && isReplaying; i++)
        {
            double[] targetPos = points[i].positions;
            if (targetPos == null || targetPos.Length != names.Length)
            {
                Debug.LogError("[JointTrajectoryPlayer] Positions length mismatch.");
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
            Robot.ApplyJointState(names, lerped);
            yield return null;
        }

        if (isReplaying)
            Robot.ApplyJointState(names, to);
    }

    private void RestoreSavedPose()
    {
        if (savedNames != null && savedPositions != null && Robot != null)
            Robot.ApplyJointState(savedNames, savedPositions);
        savedNames = null;
        savedPositions = null;
    }

    private static double DurationToSeconds(DurationMsg duration)
    {
        return duration.sec + (duration.nanosec * 1e-9);
    }
}
