using System.Collections;
using UnityEngine;
using RosMessageTypes.MoveitTaskConstructorMsgs;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Trajectory;

public class MTCTrajectoryPlayer : MonoBehaviour
{
    [SerializeField] private DirectArticulationIKController ikController;

    private bool isPlaying;
    private Coroutine playRoutine;
    private string[] savedNames;
    private float[] savedPositions;

    public void PlaySolution(SolutionMsg solution)
    {
        if (ikController == null) { Debug.LogError("[MTCTrajectoryPlayer] ikController not assigned."); return; }
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

        try
        {
            foreach (var seg in solution.sub_trajectory)
            {
                if (!isPlaying) break;
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

    private IEnumerator PlayJointTrajectory(JointTrajectoryMsg jt)
    {
        string[] names = jt.joint_names;
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
    }

    private static double DurationToSeconds(DurationMsg d) => d.sec + d.nanosec * 1e-9;

    private void OnDisable() => Stop();
}
